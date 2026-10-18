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

    public string ConditionText => ConditionName(Analysis.Condition);

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

    public bool MatchesFilter(string? keyword, bool onlyNeedFix)
    {
        if (onlyNeedFix && !Analysis.NeedsFix)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(keyword))
        {
            return true;
        }

        return FileName.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static string OutputPolicyText(long bytes) => Services.OutputPolicy.FormatSize(bytes);

    private static string ConditionName(PhotoCondition c) => c switch
    {
        PhotoCondition.NeedFix => "需修复",
        PhotoCondition.MissingDeclaration => "缺实况声明",
        PhotoCondition.ContainerFix => "需补容器",
        PhotoCondition.AlreadyStandard => "已合规",
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
