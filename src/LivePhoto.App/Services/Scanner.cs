using LivePhoto.Core.Analysis;

namespace LivePhoto.App.Services;

/// <summary>扫描选项（T0 阶段用，全部是<b>零 I/O 或元数据</b>级别的条件）。</summary>
public sealed class ScanOptions
{
    public bool IncludeSubdirectories { get; set; }

    /// <summary>是否要求文件名前缀（如 LivePhoto）。关闭则扫全部 JPEG 只做结构判定。</summary>
    public bool RequireNamePrefix { get; set; } = true;

    public string[] NamePrefixes { get; set; } = { "LivePhoto" };

    public string[] Extensions { get; set; } = { ".jpg", ".jpeg" };

    public long? MinSize { get; set; }

    public long? MaxSize { get; set; }

    /// <summary>文件修改时间下限（用文件系统元数据判断，不读内容）。</summary>
    public DateTime? ModifiedFrom { get; set; }

    public DateTime? ModifiedTo { get; set; }

    /// <summary>
    /// 并发度。<b>固定为 1</b> —— 目标环境是远程机械盘，带宽已饱和，
    /// 多路并发只会让 NAS 磁头在文件之间来回寻道，实测反而更慢。
    /// </summary>
    public int Parallelism { get; set; } = DefaultParallelism;

    public const int DefaultParallelism = 1;
}

/// <summary>T1 之后的筛选条件（基于头部探测结果，不额外读盘）。</summary>
public sealed class ScanFilter
{
    /// <summary>设备厂商白名单；null 或空表示不限制。</summary>
    public HashSet<string>? Makes { get; set; }

    /// <summary>机型白名单；null 或空表示不限制。</summary>
    public HashSet<string>? Models { get; set; }

    /// <summary>只看疑似被 Apple 改写过的。</summary>
    public bool OnlyAppleMakerNote { get; set; }

    /// <summary>排除已经有 Google 标准声明的（它们不需要修）。</summary>
    public bool ExcludeAlreadyStandard { get; set; }

    public bool IsEmpty =>
        (Makes is null || Makes.Count == 0)
        && (Models is null || Models.Count == 0)
        && !OnlyAppleMakerNote
        && !ExcludeAlreadyStandard;

    /// <summary>判断一条头部探测结果是否通过筛选。</summary>
    public bool Match(HeadInfo h)
    {
        if (!h.Ok)
        {
            // 探测失败只说明没拿到信息，不能因此把文件筛掉
            return !(OnlyAppleMakerNote || ExcludeAlreadyStandard);
        }

        if (Makes is { Count: > 0 } && !Makes.Contains(h.Make ?? "(未知)"))
        {
            return false;
        }

        if (Models is { Count: > 0 } && !Models.Contains(h.Model ?? "(未知)"))
        {
            return false;
        }

        if (OnlyAppleMakerNote && !h.MakerNoteIsApple)
        {
            return false;
        }

        if (ExcludeAlreadyStandard && h.HasGoogleXmp)
        {
            return false;
        }

        return true;
    }
}

/// <summary>T0 枚举结果。</summary>
public sealed class EnumerateOutcome
{
    public List<string> Files { get; init; } = new();

    public int TotalJpeg { get; init; }

    public int FilteredOut { get; init; }

    /// <summary>枚举过程中的错误（远程盘断连时必须让用户看见，不能静默返回空）。</summary>
    public List<string> Errors { get; init; } = new();
}

/// <summary>T1 探测结果：头部信息 + 按设备分组的计数。</summary>
public sealed class ProbeOutcome
{
    public List<HeadInfo> Heads { get; init; } = new();

    public int Failed { get; init; }

    public List<string> Errors { get; init; } = new();

    /// <summary>设备 → 数量，用于填充 UI 的厂商下拉。</summary>
    public List<KeyValuePair<string, int>> MakeCounts =>
        Heads.Where(h => h.Ok)
             .GroupBy(h => h.Make ?? "(未知)")
             .Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))
             .OrderByDescending(kv => kv.Value)
             .ToList();

    public List<KeyValuePair<string, int>> ModelCounts =>
        Heads.Where(h => h.Ok)
             .GroupBy(h => h.Model ?? "(未知)")
             .Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))
             .OrderByDescending(kv => kv.Value)
             .ToList();
}

/// <summary>扫描结果 + 过滤统计。</summary>
public sealed class ScanOutcome
{
    public List<AnalysisResult> Results { get; init; } = new();

    public int TotalJpeg { get; init; }

    public int FilteredOut { get; init; }
}

/// <summary>
/// 三级漏斗扫描器：T0 枚举（零 I/O）→ T1 头部探测（64KB/张）→ T2 全量分析（通读文件）。
/// <para>
/// 分级的目的只有一个：<b>把最贵的 T2 留给最少的候选</b>。
/// 远程机械盘上一张 8.9 MB 要花约 145 ms，砍掉 70% 就省 70%。
/// </para>
/// </summary>
public sealed class ScannerService
{
    private const int ProgressBatch = 20;

    // ---------------- T0：枚举，零 I/O ----------------

    public EnumerateOutcome Enumerate(string root, ScanOptions options)
    {
        List<string> result = new();
        List<string> errors = new();
        if (!Directory.Exists(root))
        {
            errors.Add($"目录不存在：{root}");
            return new EnumerateOutcome { Errors = errors };
        }

        EnumerationOptions eo = new()
        {
            RecurseSubdirectories = options.IncludeSubdirectories,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
        };

        HashSet<string> exts = new(options.Extensions, StringComparer.OrdinalIgnoreCase);
        int totalJpeg = 0;
        int filteredOut = 0;
        bool needMeta = options.MinSize is > 0 || options.MaxSize is > 0
                        || options.ModifiedFrom is not null || options.ModifiedTo is not null;

        try
        {
            foreach (string f in Directory.EnumerateFiles(root, "*", eo))
            {
                if (!exts.Contains(Path.GetExtension(f)))
                {
                    continue;
                }

                totalJpeg++;

                if (options.RequireNamePrefix && !MatchesPrefix(Path.GetFileName(f), options.NamePrefixes))
                {
                    filteredOut++;
                    continue;
                }

                if (needMeta)
                {
                    // 只有用户真的设了这些条件才去查元数据（远程盘上每次查询是一次往返）
                    FileInfo fi;
                    try
                    {
                        fi = new FileInfo(f);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{f}: {ex.Message}");
                        continue;
                    }

                    if (options.MinSize is { } min && fi.Length < min)
                    {
                        filteredOut++;
                        continue;
                    }

                    if (options.MaxSize is { } max && fi.Length > max)
                    {
                        filteredOut++;
                        continue;
                    }

                    if (options.ModifiedFrom is { } from && fi.LastWriteTime < from)
                    {
                        filteredOut++;
                        continue;
                    }

                    if (options.ModifiedTo is { } to && fi.LastWriteTime > to)
                    {
                        filteredOut++;
                        continue;
                    }
                }

                result.Add(f);
            }
        }
        catch (Exception ex)
        {
            // 远程盘断连时原先是静默吞掉的 —— 必须报出来
            errors.Add($"枚举中断：{ex.Message}");
        }

        return new EnumerateOutcome
        {
            Files = result,
            TotalJpeg = totalJpeg,
            FilteredOut = filteredOut,
            Errors = errors,
        };
    }

    // ---------------- T1：头部探测，64KB/张 ----------------

    public async Task<ProbeOutcome> ProbeAsync(
        IReadOnlyList<string> files,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        List<HeadInfo> heads = new(files.Count);
        List<string> errors = new();
        int failed = 0;

        await Task.Run(() =>
        {
            for (int i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                HeadInfo h;
                try
                {
                    h = HeadProbe.Probe(files[i]);
                }
                catch (Exception ex)
                {
                    errors.Add($"{files[i]}: {ex.Message}");
                    failed++;
                    continue;
                }

                if (!h.Ok)
                {
                    failed++;
                }

                heads.Add(h);

                if (progress is not null && (i % ProgressBatch == ProgressBatch - 1 || i == files.Count - 1))
                {
                    progress.Report(i + 1);
                }
            }
        }, ct).ConfigureAwait(false);

        return new ProbeOutcome { Heads = heads, Failed = failed, Errors = errors };
    }

    // ---------------- T2：全量分析，通读文件 ----------------

    public async Task<ScanOutcome> AnalyzeAsync(
        IReadOnlyList<string> files,
        IProgress<List<AnalysisResult>>? progress,
        CancellationToken ct)
    {
        List<AnalysisResult> all = new(files.Count);
        List<AnalysisResult> batch = new();

        await Task.Run(() =>
        {
            for (int i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                AnalysisResult ar = Analyzer.Analyze(files[i]);
                all.Add(ar);
                batch.Add(ar);

                if (batch.Count >= ProgressBatch || i == files.Count - 1)
                {
                    progress?.Report(new List<AnalysisResult>(batch));
                    batch.Clear();
                }
            }
        }, ct).ConfigureAwait(false);

        return new ScanOutcome
        {
            Results = all.OrderBy(a => a.FileName, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    // ---------------- 一键全量（跳过 T1）----------------

    public async Task<ScanOutcome> ScanAsync(
        string root,
        ScanOptions options,
        IProgress<List<AnalysisResult>>? progress,
        CancellationToken ct)
    {
        EnumerateOutcome en = Enumerate(root, options);
        ScanOutcome so = await AnalyzeAsync(en.Files, progress, ct).ConfigureAwait(false);

        return new ScanOutcome
        {
            Results = so.Results,
            TotalJpeg = en.TotalJpeg,
            FilteredOut = en.FilteredOut,
        };
    }

    private static bool MatchesPrefix(string fileName, string[] prefixes)
    {
        foreach (string p in prefixes)
        {
            if (fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
