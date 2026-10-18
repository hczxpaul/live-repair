using System.Windows.Media.Imaging;
using LivePhoto.Core;
using LivePhoto.Core.Analysis;

namespace LivePhoto.App;

/// <summary>
/// 列表行（UI 模型）。
/// <para>
/// 这里是 <c>AnalysisResult</c> 到界面的唯一转换点 ——
/// 领域对象不认识"✔"、"8.21 MB"这类东西，格式化只发生在这里。
/// </para>
/// </summary>
public sealed class PhotoRow : ViewModelBase
{
    private bool _isSelected;
    private BitmapSource? _thumbnail;
    private string _status = "待处理";
    private RepairOutcome? _outcome;
    private long _outputSize;

    public PhotoRow(AnalysisResult a)
    {
        Analysis = a;
        IsSelected = a.NeedsFix;
    }

    public AnalysisResult Analysis { get; }

    public string FileName => Analysis.FileName;

    public string DirectoryName => Path.GetDirectoryName(Analysis.Path) ?? string.Empty;

    public string SizeText => OutputPolicyText(Analysis.Size);

    public string VideoText => Analysis.HasEmbeddedVideo ? OutputPolicyText(Analysis.VideoLength) : "—";

    public string ConditionText => Analysis.Condition == PhotoCondition.VendorDeclared
                                   && !string.IsNullOrEmpty(Analysis.Vendor)
        ? $"厂商格式（{Analysis.Vendor}）"
        : ConditionName(Analysis.Condition);

    public bool ConditionOk => Analysis.NeedsFix;

    public string PairingText => Analysis.Pairing switch
    {
        PairingStatus.EmbeddedVideo => "内嵌视频",
        PairingStatus.ExternalMov => "外挂 MOV",
        PairingStatus.Both => "两者皆有",
        _ => "未配对",
    };

    public string ReasonText => Analysis.Reason;

    public string DeviceText => string.IsNullOrEmpty(Analysis.Model) ? "—" : Analysis.Model;

    public string MakerNoteText => Analysis.MakerNoteLength == 0 ? "无" : $"{Analysis.MakerNoteLength} B";

    public string MakerNoteActionText => Analysis.MakerNoteAction;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set => Set(ref _thumbnail, value);
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public RepairOutcome? Outcome
    {
        get => _outcome;
        set => Set(ref _outcome, value);
    }

    public long OutputSize
    {
        get => _outputSize;
        set
        {
            if (Set(ref _outputSize, value))
            {
                OnPropertyChanged(nameof(DeltaText));
            }
        }
    }

    public string DeltaText => OutputSize == 0 ? "" : $"{OutputSize - Analysis.Size:+#;-#;0}";

    /// <summary>
    /// 列表显示层的过滤 —— 这里按 <b>T2 的判定结果</b>过滤，而不是按 T1 的猜测。
    /// <para>
    /// 2026-10-18 修：原先只有「仅显需修复 + 关键字」两个条件在这儿生效，
    /// 「排除已合规」「仅疑似被 Apple 改写」只在选分析目标时起作用。结果就是：
    /// 点了「全部分析」（那条路径不套用任何预筛）之后，已合规的照片照样躺在列表里，
    /// 用户勾着「排除已合规」却看得见它们 —— 看起来像筛选失效。
    /// 现在勾选的含义统一为「列表里不要出现」，与走哪条分析路径无关。
    /// </para>
    /// </summary>
    public bool MatchesFilter(RowFilter f)
    {
        if (f.OnlyNeedFix && !Analysis.NeedsFix)
        {
            return false;
        }

        // 「已合规」与「厂商格式」都表示"不需要动手"：前者走 Google 标准，
        // 后者是荣耀/华为那种私有声明（绿联照样能播）。勾了就不该占着列表。
        if (f.ExcludeAlreadyStandard &&
            Analysis.Condition is PhotoCondition.AlreadyStandard or PhotoCondition.VendorDeclared)
        {
            return false;
        }

        if (f.OnlyAppleMakerNote && !Analysis.MakerNoteIsApple)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(f.Keyword))
        {
            return true;
        }

        return FileName.Contains(f.Keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static string OutputPolicyText(long bytes) => Services.OutputPolicy.FormatSize(bytes);

    private static string ConditionName(PhotoCondition c) => c switch
    {
        PhotoCondition.NeedFix => "需修复",
        PhotoCondition.MissingDeclaration => "缺实况声明",
        PhotoCondition.ContainerFix => "需补容器",
        PhotoCondition.AlreadyStandard => "已合规",
        PhotoCondition.VendorDeclared => "厂商格式",
        PhotoCondition.Suspicious => "可疑",
        PhotoCondition.NotMotionPhoto => "非实况",
        PhotoCondition.ExternalMovOnly => "仅外挂 MOV",
        PhotoCondition.BrokenJpeg => "JPEG 损坏",
        PhotoCondition.BrokenExif => "EXIF 损坏",
        PhotoCondition.UnsupportedFormat => "格式不支持",
        PhotoCondition.AppleNative => "iPhone 原生",
        _ => c.ToString(),
    };
}

/// <summary>
/// 列表显示过滤条件。<b>刻意不含设备勾选</b> ——
/// 设备的勾选项语义是"进不进全量分析"（界面上就是这么写的），不是"显不显示"。
/// </summary>
public sealed record RowFilter(
    string? Keyword,
    bool OnlyNeedFix,
    bool ExcludeAlreadyStandard,
    bool OnlyAppleMakerNote);

/// <summary>日志行。</summary>
public sealed class LogLine
{
    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;

    public required string Level { get; init; }

    public required string Text { get; init; }

    public string TimeText => Time.ToString("HH:mm:ss");

    // 没有这个，UI 自动化读到的日志项会显示成类型名 "LivePhoto.App.LogLine"
    public override string ToString() => $"{TimeText} [{Level}] {Text}";
}
