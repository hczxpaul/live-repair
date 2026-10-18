using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using LivePhoto.Core;
using LivePhoto.Core.Analysis;
using LivePhoto.Core.Repair;
using LivePhoto.App.Imaging;
using LivePhoto.App.Services;
using Microsoft.Win32;

namespace LivePhoto.App;

/// <summary>主窗口视图模型。</summary>
public sealed class MainViewModel : ViewModelBase
{
    private const int LogLimit = 5000;

    private readonly WpfDecoder _decoder = new();
    private readonly ScannerService _scanner = new();
    private readonly BatchRunner _runner;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    private ShellState _state = ShellState.Empty;
    private string _sourceFolder = string.Empty;
    private string _outputFolder = string.Empty;
    private PhotoRow? _selected;
    private bool _onlyNeedFix = true;
    private string _keyword = string.Empty;
    private bool _includeSubdirectories;
    private bool _requireNamePrefix = true;
    private bool _onlyAppleMakerNote;
    private bool _excludeAlreadyStandard = true;
    private string _namePrefixes = "LivePhoto";
    private List<HeadInfo> _heads = new();
    private int _candidateCount;
    private int _probedCount;
    private bool _filterDirty;
    private int _done;
    private int _total;
    private string _progressText = string.Empty;
    private double _progressValue;
    private CancellationTokenSource? _runCts;

    // 扫描/分析用的取消源（与修复用的 _runCts 分开，避免互相取消）
    private CancellationTokenSource? _scanCts;
    private bool _isAnalyzing;   // T2 分析期间 State 保持 Ready，按钮可用性靠它补
    private CancellationTokenSource? _previewCts;
    private BatchSummary? _lastSummary;

    public MainViewModel()
    {
        _runner = new BatchRunner(_decoder);

        OpenFolderCommand = new RelayCommand(OpenFolder);
        RescanCommand = new AsyncRelayCommand(EnumerateAndProbeAsync, () => !string.IsNullOrEmpty(_sourceFolder) && State != ShellState.Repairing);
        PickOutputCommand = new RelayCommand(PickOutput);
        StartCommand = new AsyncRelayCommand(StartAsync, () => CanStart);
        CancelCommand = new RelayCommand(Cancel, () => State is ShellState.Repairing or ShellState.Scanning || _isAnalyzing);
        SelectAllCommand = new RelayCommand(() => SetAll(true));
        SelectNoneCommand = new RelayCommand(() => SetAll(false));
        SelectNeedFixCommand = new RelayCommand(SelectNeedFix);
        AnalyzeSelectedCommand = new AsyncRelayCommand(AnalyzeSelectedAsync, () => CanReanalyze);
        AnalyzeAllCommand = new AsyncRelayCommand(AnalyzeAllAsync, () => CanReanalyze);
        ExportReportCommand = new RelayCommand(ExportReport, () => _lastSummary is not null);
        OpenOutputCommand = new RelayCommand(OpenOutput, () => !string.IsNullOrEmpty(_outputFolder));

        RowsView = System.Windows.Data.CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = o => o is PhotoRow r && r.MatchesFilter(_keyword, _onlyNeedFix);
    }

    // ---------------- 绑定属性 ----------------

    public ObservableCollection<PhotoRow> Rows { get; } = new();

    public ObservableCollection<LogLine> Logs { get; } = new();

    /// <summary>设备 / 机型筛选选项（T1 后填充）。</summary>
    public ObservableCollection<DeviceOption> Devices { get; } = new();

    public System.ComponentModel.ICollectionView RowsView { get; }

    public ShellState State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value))
            {
                RefreshCommands();
                Raise(nameof(IsBusy), nameof(CanStart), nameof(StatusText),
                      nameof(BlockReason), nameof(HintText));
            }
        }
    }

    /// <summary>
    /// 所有命令的 CanExecute 都（直接或间接）依赖 State，必须主动通知。
    /// 否则 WPF CommandManager 的自动重查时机不定，按钮会挂旧状态 ——
    /// 表现为扫描/分析/修复结束后"该灭不灭、该亮不亮"，用户点了却无反应。
    /// </summary>
    private void RefreshCommands()
    {
        CancelCommand?.RaiseCanExecuteChanged();
        AnalyzeSelectedCommand?.RaiseCanExecuteChanged();
        AnalyzeAllCommand?.RaiseCanExecuteChanged();
        StartCommand?.RaiseCanExecuteChanged();
        RescanCommand?.RaiseCanExecuteChanged();
    }

    public bool IsBusy => State is ShellState.Scanning or ShellState.Repairing;

    public string SourceFolder
    {
        get => _sourceFolder;
        set => Set(ref _sourceFolder, value);
    }

    public string OutputFolder
    {
        get => _outputFolder;
        set
        {
            if (Set(ref _outputFolder, value))
            {
                Raise(nameof(CanStart), nameof(BlockReason), nameof(HintText));
            }
        }
    }

    public PhotoRow? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                // 观察 Task 异常：预览加载历史上出过"async Task 被丢弃 → 异常无声吞掉 →
                // 缩略图再也不显示且日志无痕迹"的 bug（2026-10-17 修）。这里确保将来
                // 再出同类问题至少会在日志里留名，而不是静默死亡。
                _ = LoadPreviewAsync(value).ContinueWith(t =>
                    Log("警告", $"预览加载失败：{t.Exception?.InnerException?.Message}"),
                    TaskContinuationOptions.OnlyOnFaulted);
                Raise(nameof(HasSelection));
            }
        }
    }

    public bool HasSelection => _selected is not null;

    public bool OnlyNeedFix
    {
        get => _onlyNeedFix;
        set
        {
            if (Set(ref _onlyNeedFix, value))
            {
                RefreshFilter();
            }
        }
    }

    public string Keyword
    {
        get => _keyword;
        set
        {
            if (Set(ref _keyword, value))
            {
                RefreshFilter();
            }
        }
    }

    public bool IncludeSubdirectories
    {
        get => _includeSubdirectories;
        set => Set(ref _includeSubdirectories, value);
    }

    /// <summary>是否只扫 LivePhoto 开头的 JPEG。关掉则扫全部 JPEG，纯靠结构判定。</summary>
    public bool RequireNamePrefix
    {
        get => _requireNamePrefix;
        set
        {
            if (!Set(ref _requireNamePrefix, value))
            {
                return;
            }

            Log("信息", value ? "已开启「仅 LivePhoto 前缀」" : "已关闭「仅 LivePhoto 前缀」，将扫描全部 JPEG");

            // 条件变了就立刻重扫，否则界面还停留在旧结果上，看着像没生效
            if (!string.IsNullOrEmpty(_sourceFolder))
            {
                _ = EnumerateAndProbeAsync();
            }
        }
    }

    /// <summary>文件名前缀，逗号分隔多个。</summary>
    public string NamePrefixes
    {
        get => _namePrefixes;
        set => Set(ref _namePrefixes, value);
    }

    /// <summary>T0 之后通过文件名/大小/日期筛选的文件数（尚未读任何内容）。</summary>
    public int CandidateCount
    {
        get => _candidateCount;
        set => Set(ref _candidateCount, value);
    }

    /// <summary>T1 之后成功探测出设备信息的文件数。</summary>
    public int ProbedCount
    {
        get => _probedCount;
        set => Set(ref _probedCount, value);
    }

    /// <summary>按当前设备/机型勾选情况，将要进入 T2 全量分析的文件数。</summary>
    public int SelectedCandidateCount
    {
        get
        {
            ScanFilter f = BuildFilter();
            return _heads.Count(h => f.Match(h));
        }
    }

    public bool OnlyAppleMakerNote
    {
        get => _onlyAppleMakerNote;
        set
        {
            if (Set(ref _onlyAppleMakerNote, value))
            {
                _filterDirty = true;
                Raise(nameof(SelectedCandidateCount), nameof(BlockReason), nameof(HintText));
                AnalyzeSelectedCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool ExcludeAlreadyStandard
    {
        get => _excludeAlreadyStandard;
        set
        {
            if (Set(ref _excludeAlreadyStandard, value))
            {
                _filterDirty = true;
                Raise(nameof(SelectedCandidateCount), nameof(BlockReason), nameof(HintText));
                AnalyzeSelectedCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int Done
    {
        get => _done;
        set => Set(ref _done, value);
    }

    public int Total
    {
        get => _total;
        set => Set(ref _total, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        set => Set(ref _progressValue, value);
    }

    public string ProgressText
    {
        get => _progressText;
        set
        {
            if (Set(ref _progressText, value))
            {
                Raise(nameof(HintText));
            }
        }
    }

    public RepairMode Mode { get; set; } = RepairMode.Retain;

    public MakerNotePolicy MakerNotePolicy { get; set; } = MakerNotePolicy.Auto;

    public ConflictStrategy Conflict { get; set; } = ConflictStrategy.Rename;

    /// <summary>
    /// 修复批次并行度。<b>默认 1</b> —— 使用场景是远程机械盘（输入输出都在 NAS 上），
    /// 带宽已饱和，多路并发只会让磁头在文件之间来回寻道，实测反而慢 3~5 倍
    /// （与扫描侧 <see cref="ScanOptions.DefaultParallelism"/> 同口径）。
    /// 本地盘上想更快可调大，结果不受影响（每条修复相互独立）。
    /// </summary>
    public int Parallelism { get; set; } = 1;

    public int TotalCount => Rows.Count;

    public int NeedFixCount => Rows.Count(r => r.Analysis.NeedsFix);

    /// <summary>
    /// 自带 Google 实况声明、但内嵌视频缺 ftyp 盒的文件数。
    /// 这类文件能被识别成实况照片、视频却播不出，值得单独提示。
    /// </summary>
    public int ContainerFixCount => Rows.Count(r => r.Analysis.Condition == PhotoCondition.ContainerFix);

    public int SelectedCount => Rows.Count(r => r.IsSelected);

    public int SuccessCount => Rows.Count(r => r.Outcome == RepairOutcome.Success);

    public int FailCount => Rows.Count(r => r.Outcome is not (null or RepairOutcome.Success or RepairOutcome.Skipped));

    public string StatusText => State switch
    {
        ShellState.Empty => "尚未选择文件夹",
        ShellState.Scanning => "正在扫描…",
        ShellState.Ready => $"就绪：{TotalCount} 个文件，其中 {NeedFixCount} 个需修复",
        ShellState.Repairing => $"修复中 {Done}/{Total}",
        ShellState.Finished => $"完成：成功 {SuccessCount}，失败 {FailCount}",
        _ => string.Empty,
    };

    public bool CanStart => !IsBusy &&
                            State is ShellState.Ready or ShellState.Finished &&
                            !string.IsNullOrEmpty(_outputFolder) &&
                            SelectedCount > 0;

    /// <summary>
    /// 为什么现在不能开始修复（空串 = 可以开始）。
    /// <para>按钮变灰却不说明原因是设计缺陷 —— 这里把具体原因摆在界面上。</para>
    /// </summary>
    public string BlockReason
    {
        get
        {
            if (State == ShellState.Scanning)
            {
                return "正在扫描，请稍候…";
            }

            if (State == ShellState.Repairing)
            {
                return "正在修复中…";
            }

            if (State == ShellState.Empty)
            {
                return "请先「打开文件夹」";
            }

            if (State == ShellState.Probed)
            {
                return $"已探测 {ProbedCount} 个文件 · 当前勾选 {SelectedCandidateCount} 个"
                       + " —— 点「分析所选」或「全部分析」";
            }

            if (string.IsNullOrEmpty(_outputFolder))
            {
                return "尚未设置输出目录（点「输出目录…」）";
            }

            if (SelectedCount == 0)
            {
                int visible = RowsView.Cast<PhotoRow>().Count();
                if (visible == 0)
                {
                    return Rows.Count == 0
                        ? "没有扫描到文件：目录里没有 JPEG，或文件名不以 LivePhoto 开头（可取消勾选「仅 LivePhoto 前缀」）"
                        : "当前筛选下没有条目（可取消勾选「仅显需修复」或清空关键字）";
                }

                return "尚未选中任何条目（勾选左侧复选框，或点「全选」）";
            }

            return string.Empty;
        }
    }

    /// <summary>
    /// 已探测完成后，任何时候都应能重新挑选条件再分析一次。
    /// <para>之前写成 <c>State == Probed</c>，结果 T2 一跑完 State 变 Ready，按钮就永久灰了 ——
    /// 用户改完筛选条件再也点不动。</para>
    /// </summary>
    public bool CanReanalyze =>
        (State is ShellState.Probed or ShellState.Ready or ShellState.Finished) && !IsBusy;

    /// <summary>进度条下方的提示：修复中显示进度，否则显示"为什么不能开始"。</summary>
    public string HintText
    {
        get
        {
            if (State == ShellState.Repairing)
            {
                return ProgressText;
            }

            // 筛选用改过但还没重新分析 —— 列表里是旧结果，得说清楚
            if (State is ShellState.Ready or ShellState.Finished && _filterDirty)
            {
                return "筛选条件已变化 —— 再点一次「分析所选」即可按新条件刷新";
            }

            return BlockReason.Length > 0 ? BlockReason : ProgressText;
        }
    }

    // ---------------- 命令 ----------------

    public RelayCommand OpenFolderCommand { get; }

    public AsyncRelayCommand RescanCommand { get; }

    public RelayCommand PickOutputCommand { get; }

    public AsyncRelayCommand StartCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand SelectAllCommand { get; }

    public RelayCommand SelectNoneCommand { get; }

    public RelayCommand SelectNeedFixCommand { get; }

    public AsyncRelayCommand AnalyzeSelectedCommand { get; }

    public AsyncRelayCommand AnalyzeAllCommand { get; }

    public RelayCommand ExportReportCommand { get; }

    public RelayCommand OpenOutputCommand { get; }

    // ---------------- 实现 ----------------

    /// <summary>
    /// 打开文件夹 → T0 枚举 → T1 头部探测。
    /// <para>只读每张 64 KB，远程机械盘上 1000 张约 1 秒；用户框定范围后才进入 T2。</para>
    /// </summary>
    public async Task EnumerateAndProbeAsync()
    {
        if (string.IsNullOrEmpty(SourceFolder))
        {
            return;
        }

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        CancellationTokenSource cts = new();
        _scanCts = cts;

        State = ShellState.Scanning;
        Rows.Clear();
        Devices.Clear();
        _heads = new List<HeadInfo>();
        _filterDirty = false;
        RaiseCounts();

        Log("信息", $"开始扫描：{SourceFolder}");

        ScanOptions opt = BuildScanOptions();
        Progress<int> probeProgress = new(done =>
        {
            ProbedCount = done;
            ProgressValue = 100.0 * done / Math.Max(1, CandidateCount);
            ProgressText = $"正在识别设备 {done}/{CandidateCount}";
        });

        try
        {
            EnumerateOutcome en = await Task.Run(() => _scanner.Enumerate(SourceFolder, opt), cts.Token)
                .ConfigureAwait(true);
            CandidateCount = en.Files.Count;
            Raise(nameof(SelectedCandidateCount), nameof(BlockReason), nameof(HintText));

            foreach (string err in en.Errors.Take(5))
            {
                Log("错误", err);
            }

            Log("信息", $"T0 枚举：共 {en.TotalJpeg} 个 JPEG，通过文件名等条件 {en.Files.Count} 个"
                      + (en.FilteredOut > 0 ? $"（过滤 {en.FilteredOut} 个）" : ""));

            if (en.Files.Count == 0)
            {
                State = ShellState.Probed;
                Log("警告", en.TotalJpeg == 0
                    ? "该目录里没有任何 .jpg / .jpeg 文件"
                    : "所有 JPEG 都被文件名/大小/日期条件过滤掉了 —— 可取消勾选「仅 LivePhoto 前缀」");
                return;
            }

            // ---- T1：只读 64KB/张，用于设备筛选 ----
            ProbeOutcome po = await _scanner.ProbeAsync(en.Files, probeProgress, cts.Token).ConfigureAwait(true);
            _heads = po.Heads;

            foreach (DeviceOption o in po.MakeCounts.Select(kv => new DeviceOption(kv.Key, kv.Value)))
            {
                o.PropertyChanged += OnDeviceChanged;
                Devices.Add(o);
            }

            ProbedCount = po.Heads.Count;
            Raise(nameof(SelectedCandidateCount), nameof(BlockReason), nameof(HintText));

            foreach (string err in po.Errors.Take(5))
            {
                Log("错误", err);
            }

            Log("信息", $"T1 头部探测完成：{po.Heads.Count} 个文件，识别出设备 {Devices.Count} 种"
                      + (po.Failed > 0 ? $"（{po.Failed} 个探测失败，仍会参与后续分析）" : ""));

            State = ShellState.Probed;
            ProgressText = $"候选 {en.Files.Count} 个，勾选设备后点「分析所选」";
        }
        catch (OperationCanceledException)
        {
            Log("信息", "扫描已取消");
            State = ShellState.Empty;
        }
        catch (Exception ex)
        {
            Log("错误", $"扫描失败：{ex.Message}");
            State = ShellState.Empty;
        }
        finally
        {
            CancelCommand.RaiseCanExecuteChanged();
            RaiseCounts();
        }
    }

    /// <summary>T2：只对勾选设备后的候选做全量分析。</summary>
    public async Task AnalyzeSelectedAsync() => await AnalyzeAsync(BuildFilter()).ConfigureAwait(false);

    /// <summary>T2：不分设备，全部分析（一键全量）。</summary>
    public async Task AnalyzeAllAsync() => await AnalyzeAsync(null).ConfigureAwait(false);

    private async Task AnalyzeAsync(ScanFilter? filter)
    {
        if (string.IsNullOrEmpty(SourceFolder))
        {
            return;
        }

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        CancellationTokenSource cts = new();
        _scanCts = cts;

        List<HeadInfo> targets = filter is null ? _heads : _heads.Where(filter.Match).ToList();

        if (targets.Count == 0)
        {
            Log("警告", "没有符合条件的候选（可调整设备勾选，或点「全部分析」）");
            return;
        }

        State = ShellState.Ready;      // T2 期间保持 Ready（IsBusy 只认 Repairing）
        _isAnalyzing = true;
        CancelCommand.RaiseCanExecuteChanged();
        Rows.Clear();
        Total = targets.Count;
        Done = 0;
        ProgressValue = 0;
        Log("信息", $"T2 全量分析 {targets.Count} 个文件（远程盘上这一步最贵，请耐心等）");

        Progress<List<LivePhoto.Core.Analysis.AnalysisResult>> progress = new(batch =>
        {
            foreach (LivePhoto.Core.Analysis.AnalysisResult ar in batch)
            {
                PhotoRow row = new(ar);
                row.PropertyChanged += OnRowPropertyChanged;
                Rows.Add(row);
            }

            Done = Rows.Count;
            ProgressValue = 100.0 * Done / Math.Max(1, Total);
            ProgressText = $"正在分析 {Done}/{Total} · {Path.GetFileName(batch[0].Path)}";
            RaiseCounts();
        });

        try
        {
            ScanOutcome so = await _scanner.AnalyzeAsync(
                targets.Select(h => h.Path).ToList(), progress, cts.Token).ConfigureAwait(true);

            State = ShellState.Ready;
            _filterDirty = false;
            ProgressText = $"分析完成：{TotalCount} 个文件，需修复 {NeedFixCount} 个";
            if (ContainerFixCount > 0)
            {
                ProgressText += $"（其中 {ContainerFixCount} 个视频缺 ftyp）";
            }


            RaiseCounts();
            Raise(nameof(HintText));
            Log("信息", $"分析完成：{TotalCount} 个文件，需修复 {NeedFixCount} 个");

            // 缺 ftyp 的文件最容易被忽略：它带标准声明，会被当成"已合规"，
            // 结果在相册里能识别成实况照片、视频却播不出。单独提示一行，
            // 免得用户以为工具漏了。
            if (ContainerFixCount > 0)
            {
                Log("信息", $"发现 {ContainerFixCount} 个文件：内嵌视频缺 ftyp 盒"
                          + "（能识别成实况照片、但视频播放失败）→ 已默认勾选，"
                          + "修复会补回标准容器头并同步平移索引，视频数据不动");
            }


        }
        catch (OperationCanceledException)
        {
            Log("信息", "分析已取消");
            State = ShellState.Probed;
        }
        catch (Exception ex)
        {
            Log("错误", $"分析失败：{ex.Message}");
            State = ShellState.Probed;
        }
        finally
        {
            _isAnalyzing = false;
            CancelCommand.RaiseCanExecuteChanged();
            RaiseCounts();
        }
    }

    private ScanOptions BuildScanOptions()
    {
        string[] prefixes = (_namePrefixes ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToArray();
        if (prefixes.Length == 0)
        {
            prefixes = new[] { "LivePhoto" };
        }

        return new ScanOptions
        {
            IncludeSubdirectories = _includeSubdirectories,
            RequireNamePrefix = RequireNamePrefix,
            NamePrefixes = prefixes,
            Parallelism = ScanOptions.DefaultParallelism,
        };
    }

    private ScanFilter BuildFilter()
    {
        HashSet<string>? makes = Devices.Where(d => d.IsSelected).Select(d => d.Name).ToHashSet();
        return new ScanFilter
        {
            Makes = makes.Count == 0 ? null : makes,
            OnlyAppleMakerNote = _onlyAppleMakerNote,
            ExcludeAlreadyStandard = _excludeAlreadyStandard,
        };
    }

    private void OnDeviceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceOption.IsSelected))
        {
            // 必须同时通知 BlockReason / HintText —— 它们内部读 SelectedCandidateCount，
            // 而 UI 上显示"当前勾选 N 个"的那个文本块绑的是 HintText。
            _filterDirty = true;
            Raise(nameof(SelectedCandidateCount), nameof(BlockReason), nameof(HintText));
            AnalyzeSelectedCommand.RaiseCanExecuteChanged();
            AnalyzeAllCommand.RaiseCanExecuteChanged();
        }
    }

    private void OpenFolder()
    {
        OpenFolderDialog dlg = new()
        {
            Title = "选择包含实况照片的文件夹",
            Multiselect = false,
        };

        if (dlg.ShowDialog() != true)
        {
            return;
        }

        SourceFolder = dlg.FolderName;
        if (string.IsNullOrEmpty(OutputFolder))
        {
            OutputFolder = SuggestOutput(SourceFolder);
        }

        Log("信息", $"打开文件夹：{SourceFolder}");
        _ = EnumerateAndProbeAsync();
    }

    public void PickOutput()
    {
        OpenFolderDialog dlg = new()
        {
            Title = "选择输出目录（不能位于源目录内）",
            Multiselect = false,
        };

        if (dlg.ShowDialog() != true)
        {
            return;
        }

        string? err = OutputPolicy.Validate(SourceFolder, dlg.FolderName);
        if (err is not null)
        {
            Log("错误", err);
            System.Windows.MessageBox.Show(err, "输出目录不可用",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        OutputFolder = dlg.FolderName;
        Log("信息", $"输出目录：{OutputFolder}");
        Raise(nameof(CanStart));
    }


    public async Task StartAsync()
    {
        string? err = OutputPolicy.Validate(SourceFolder, OutputFolder);
        if (err is not null)
        {
            Log("错误", err);
            return;
        }

        List<PhotoRow> selected = Rows.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0)
        {
            Log("警告", "没有选中任何条目");
            return;
        }

        long need = selected.Sum(r => r.Analysis.Size);
        string? spaceWarn = OutputPolicy.CheckDiskSpace(OutputFolder, need);
        if (spaceWarn is not null)
        {
            Log("警告", spaceWarn);
        }

        State = ShellState.Repairing;
        Total = selected.Count;
        Done = 0;
        ProgressValue = 0;
        _runCts = new CancellationTokenSource();

        Log("信息", $"开始修复 {selected.Count} 个文件 → {OutputFolder}");

        Progress<BatchProgress> progress = new(p =>
        {
            Done = p.Done;
            Total = p.Total;
            ProgressValue = p.Total == 0 ? 0 : p.Done * 100.0 / p.Total;
            ProgressText = p.Total == 0 ? string.Empty : $"{p.Done}/{p.Total} · {p.CurrentFile}";

            if (p.LastOutcome is not null)
            {
                string level = p.LastOutcome switch
                {
                    RepairOutcome.Success => "成功",
                    RepairOutcome.Skipped => "跳过",
                    _ => "失败",
                };

                Log(level, $"{p.CurrentFile} — {p.LastMessage}");
            }
        });

        BatchSummary summary;
        try
        {
            summary = await _runner.RunAsync(
                selected.Select(r => r.Analysis.Path).ToList(),
                SourceFolder,
                OutputFolder,
                // 批次里的文件全部来自用户勾选：默认勾选只含「需修复」，「可疑」必须手动勾。
                // 显式勾选即视为授权 —— 可疑文件按用户意愿进入修复流（MakerNote 仍原样保留）。
                new RepairOptions(Mode, MakerNotePolicy, UserOverride: true),
                Conflict,
                Parallelism,
                progress,
                _runCts.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log("错误", $"批量执行异常：{ex.Message}");
            State = ShellState.Finished;
            return;
        }

        // 回写每条状态
        foreach (BatchEntry e in summary.Entries)
        {
            PhotoRow? row = Rows.FirstOrDefault(r => r.Analysis.Path == e.SourcePath);
            if (row is null)
            {
                continue;
            }

            row.Outcome = e.Outcome;
            row.Status = e.Outcome == RepairOutcome.Success ? "成功" : ReportWriter.OutcomeText(e.Outcome);
            row.OutputSize = e.OutputSize;
        }

        _lastSummary = summary;
        RaiseCounts();
        State = ShellState.Finished;
        ProgressText = $"完成：成功 {summary.Succeeded}，跳过 {summary.Skipped}，失败 {summary.Failed}（耗时 {summary.Elapsed:mm\\:ss}）";

        Log("信息", $"全部完成：成功 {summary.Succeeded}，跳过 {summary.Skipped}，失败 {summary.Failed}，"
                  + $"净增量 {OutputPolicy.FormatSize(summary.TotalOutputBytes - summary.TotalInputBytes)}");

        if (!summary.IsConsistent)
        {
            Log("警告", "统计不一致（Attempted 不等于各项之和），请检查");
        }
    }

    public void Cancel()
    {
        if (State == ShellState.Repairing)
        {
            _runCts?.Cancel();
            Log("信息", "已请求取消，等待当前条目完成…");
            return;
        }

        if (State == ShellState.Scanning || _isAnalyzing)
        {
            _scanCts?.Cancel();
            Log("信息", _isAnalyzing
                ? "已请求取消分析，等待当前文件完成…"
                : "已请求取消扫描，等待当前文件完成…");
        }
    }

    private void SetAll(bool value)
    {
        int n = 0;
        foreach (PhotoRow r in RowsView.Cast<PhotoRow>())
        {
            r.IsSelected = value;
            n++;
        }

        if (n == 0)
        {
            Log("警告", "当前筛选下没有条目，全选无效 —— 可取消勾选「仅显需修复」或清空关键字");
        }

        RaiseCounts();
    }

    private void SelectNeedFix()
    {
        foreach (PhotoRow r in RowsView.Cast<PhotoRow>())
        {
            r.IsSelected = r.Analysis.NeedsFix;
        }

        RaiseCounts();
    }

    private void ExportReport()
    {
        if (_lastSummary is null)
        {
            return;
        }

        SaveFileDialog dlg = new()
        {
            Filter = "CSV 报告|*.csv",
            FileName = $"修复报告_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };

        if (dlg.ShowDialog() != true)
        {
            return;
        }

        ReportWriter.WriteCsv(_lastSummary, dlg.FileName);
        ReportWriter.WriteTxt(_lastSummary, Path.ChangeExtension(dlg.FileName, ".txt"));
        Log("信息", $"报告已导出：{dlg.FileName}");
    }

    private void OpenOutput()
    {
        if (string.IsNullOrEmpty(_outputFolder))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _outputFolder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log("错误", $"打开输出目录失败：{ex.Message}");
        }
    }

    private async Task LoadPreviewAsync(PhotoRow? row)
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        // 必须置 null：已释放的 CTS 留在字段里，下次进来第一句 Cancel() 就会抛
        // ObjectDisposedException（2026-10-17 实测：async Task 被 _ = 丢弃 →
        // 异常无声吞掉 → 缩略图再也不加载、日志无任何痕迹）。
        _previewCts = null;
        if (row is null)
        {
            return;
        }

        CancellationTokenSource cts = new();
        _previewCts = cts;

        try
        {
            // 150ms 防抖：快速连点列表时不打爆 CPU
            await Task.Delay(150, cts.Token).ConfigureAwait(true);
            string path = row.Analysis.Path;
            var bmp = await Task.Run(() => _decoder.LoadThumbnail(path, 640), cts.Token).ConfigureAwait(true);
            if (!cts.IsCancellationRequested)
            {
                row.Thumbnail = bmp;
            }
        }
        catch (OperationCanceledException)
        {
            // 被后续选择取消，正常
        }
        catch (Exception ex)
        {
            Log("警告", $"预览加载失败：{ex.Message}");
        }
    }

    private void RefreshFilter()
    {
        _dispatcher.InvokeAsync(() => RowsView.Refresh());
    }

    private void RaiseCounts()
    {
        Raise(nameof(TotalCount), nameof(NeedFixCount), nameof(SelectedCount),
              nameof(SuccessCount), nameof(FailCount), nameof(CanStart), nameof(StatusText),
              nameof(BlockReason), nameof(HintText));
        StartCommand.RaiseCanExecuteChanged();
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PhotoRow.IsSelected))
        {
            RaiseCounts();
        }
    }

    private void Log(string level, string text)
    {
        _dispatcher.InvokeAsync(() =>
        {
            Logs.Add(new LogLine { Level = level, Text = text });
            while (Logs.Count > LogLimit)
            {
                Logs.RemoveAt(0);
            }
        });
    }

    private static string SuggestOutput(string source)
    {
        string parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(source)) ?? source;
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        return Path.Combine(parent, name + "_已修复");
    }
}
