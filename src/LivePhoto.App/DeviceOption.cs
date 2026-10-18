using LivePhoto.Core.Analysis;

namespace LivePhoto.App;

/// <summary>筛选面板里的一个设备选项（厂商或机型 + 数量）。</summary>
public sealed class DeviceOption : ViewModelBase
{
    private bool _isSelected = true;

    public DeviceOption(string name, int count)
    {
        Name = name;
        Count = count;
    }

    public string Name { get; }

    public int Count { get; }

    public string Label => $"{Name} ×{Count}";

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

/// <summary>一行尚未做全量分析的候选（T1 探测后的展示用）。</summary>
public sealed record CandidateRow(string Path, HeadInfo Head);
