using System.Text;
using LivePhoto.Core;
using LivePhoto.Core.Repair;

namespace LivePhoto.App.Services;

/// <summary>
/// 输出目录合法性、路径映射、冲突策略、预检。
/// <para>
/// <b>安全闸门之一。</b>UI 层的校验只是"早失败"优化，这里的校验才是可复用的正式规则，
/// 而 <see cref="RepairOrchestrator"/> 在写盘前还会再复核一次（纵深防御第三层）。
/// </para>
/// </summary>
public static class OutputPolicy
{
    /// <summary>
    /// 校验输出目录。返回 null 表示通过，否则返回给用户的中文原因。
    /// </summary>
    public static string? Validate(string? sourceRoot, string? outputRoot)
    {
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            return "尚未选择输出目录";
        }

        if (!string.IsNullOrWhiteSpace(sourceRoot))
        {
            string src = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
            string dst = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));

            if (RepairOrchestrator.IsSameOrSubPath(dst, src))
            {
                return "输出目录不能位于源目录内（会导致修复结果被再次扫描、结构污染）";
            }
        }

        try
        {
            Directory.CreateDirectory(outputRoot);
        }
        catch (Exception ex)
        {
            return $"输出目录无法创建：{ex.Message}";
        }

        // 写权限探测：写一个 0 字节文件再删除
        string probe = Path.Combine(outputRoot, ".write_probe_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            return $"输出目录不可写：{ex.Message}";
        }

        return null;
    }

    /// <summary>磁盘余量检查。返回 null 表示充足。</summary>
    public static string? CheckDiskSpace(string outputRoot, long requiredBytes)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(outputRoot)) ?? outputRoot;
            DriveInfo di = new(root);
            long need = (long)(requiredBytes * 1.05) + (16 << 20);
            if (di.AvailableFreeSpace < need)
            {
                return $"磁盘空间可能不足：需要约 {FormatSize(need)}，剩余 {FormatSize(di.AvailableFreeSpace)}";
            }
        }
        catch
        {
            // UNC/网络路径下 DriveInfo 拿不到信息（它只认盘符）—— 远程盘输出时
            // 容量检查本就做不了，但不该静默返回"充足"，给使用者一个提示
            if (Path.GetFullPath(outputRoot).StartsWith(@"\\", StringComparison.Ordinal))
            {
                return "无法检查网络磁盘的剩余空间（输出在远程盘上）—— 请自行确认容量充足";
            }

            // 本地路径探测失败不作为阻断条件
        }

        return null;
    }

    /// <summary>生成目标路径（含冲突处理）。</summary>
    public static string BuildTargetPath(
        string sourceRoot, string outputRoot, string sourceFile, ConflictStrategy strategy)
    {
        string rel = Path.GetRelativePath(sourceRoot, sourceFile);
        string target = Path.Combine(outputRoot, rel);

        string? dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (!File.Exists(target))
        {
            return target;
        }

        return strategy switch
        {
            ConflictStrategy.Skip => target,
            ConflictStrategy.Overwrite => target,
            _ => UniquePath(target),
        };
    }

    private static string UniquePath(string path)
    {
        string? dir = Path.GetDirectoryName(path) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 1; i < 10000; i++)
        {
            string cand = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(cand))
            {
                return cand;
            }
        }

        return Path.Combine(dir, $"{name} ({Guid.NewGuid().ToString("N")[..8]}){ext}");
    }

    /// <summary>友好大小显示。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        double kb = bytes / 1024.0;
        if (kb < 1024)
        {
            return $"{kb:F1} KB";
        }

        double mb = kb / 1024.0;
        return mb < 1024 ? $"{mb:F2} MB" : $"{mb / 1024.0:F2} GB";
    }
}

/// <summary>同名文件冲突策略。</summary>
public enum ConflictStrategy
{
    /// <summary>自动重命名（推荐，最安全）。</summary>
    Rename,

    /// <summary>跳过已存在的文件。</summary>
    Skip,

    /// <summary>覆盖（仅限输出目录，需二次确认）。</summary>
    Overwrite,
}

/// <summary>批量执行的进度快照。</summary>
public sealed record BatchProgress(int Done, int Total, string? CurrentFile, RepairOutcome? LastOutcome, string? LastMessage);

/// <summary>批量执行的汇总。</summary>
public sealed class BatchSummary
{
    public int Attempted { get; set; }

    public int Succeeded { get; set; }

    public int Skipped { get; set; }

    public int Failed { get; set; }

    public int Cancelled { get; set; }

    public long TotalInputBytes { get; set; }

    public long TotalOutputBytes { get; set; }

    public TimeSpan Elapsed { get; set; }

    public List<BatchEntry> Entries { get; } = new();

    /// <summary>不变式：Attempted 必须等于其余四项之和。</summary>
    public bool IsConsistent => Attempted == Succeeded + Skipped + Failed + Cancelled;
}

/// <summary>单条执行记录（用于报告导出）。</summary>
public sealed class BatchEntry
{
    public required string FileName { get; init; }

    public required string SourcePath { get; init; }

    public string? TargetPath { get; init; }

    public required RepairOutcome Outcome { get; init; }

    public string Message { get; init; } = string.Empty;

    public long SourceSize { get; init; }

    public long OutputSize { get; init; }

    public long Delta => OutputSize - SourceSize;

    public string? SourceSha256 { get; init; }

    public string? OutputSha256 { get; init; }

    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;
}
