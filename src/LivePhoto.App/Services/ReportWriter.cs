using System.Globalization;
using System.Text;

namespace LivePhoto.App.Services;

/// <summary>
/// 结果报告导出。UTF-8 with BOM —— 中文路径与中文失败原因用 Excel 直接打开不乱码。
/// </summary>
public static class ReportWriter
{
    public static void WriteCsv(BatchSummary s, string path)
    {
        StringBuilder sb = new();
        sb.Append("文件名,结果,原因,源字节,输出字节,增量,源SHA256,输出SHA256,源路径,输出路径\n");
        foreach (BatchEntry e in s.Entries)
        {
            sb.Append(Csv(Escape(e.FileName))).Append(',');
            sb.Append(Csv(OutcomeText(e.Outcome))).Append(',');
            sb.Append(Csv(Escape(e.Message))).Append(',');
            sb.Append(e.SourceSize.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(e.OutputSize.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(e.Delta.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(Csv(e.SourceSha256 ?? string.Empty)).Append(',');
            sb.Append(Csv(e.OutputSha256 ?? string.Empty)).Append(',');
            sb.Append(Csv(Escape(e.SourcePath))).Append(',');
            sb.Append(Csv(Escape(e.TargetPath ?? string.Empty))).Append('\n');
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    public static void WriteTxt(BatchSummary s, string path)
    {
        StringBuilder sb = new();
        sb.AppendLine("LivePhoto 修复报告");
        sb.AppendLine(new string('=', 60));
        sb.AppendLine($"时间       : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"总计       : {s.Attempted}");
        sb.AppendLine($"成功       : {s.Succeeded}");
        sb.AppendLine($"跳过       : {s.Skipped}");
        sb.AppendLine($"失败       : {s.Failed}");
        sb.AppendLine($"取消       : {s.Cancelled}");
        sb.AppendLine($"耗时       : {s.Elapsed:mm\\:ss}");
        sb.AppendLine($"输入总量   : {OutputPolicy.FormatSize(s.TotalInputBytes)}");
        sb.AppendLine($"输出总量   : {OutputPolicy.FormatSize(s.TotalOutputBytes)}");
        sb.AppendLine($"净增量     : {OutputPolicy.FormatSize(s.TotalOutputBytes - s.TotalInputBytes)}");
        sb.AppendLine();

        IEnumerable<BatchEntry> bad = s.Entries.Where(e => e.Outcome is not (LivePhoto.Core.RepairOutcome.Success or LivePhoto.Core.RepairOutcome.Skipped));
        if (bad.Any())
        {
            sb.AppendLine("未成功明细:");
            foreach (BatchEntry e in bad)
            {
                sb.AppendLine($"  - {e.FileName}: {e.Message}");
            }
        }
        else
        {
            sb.AppendLine("全部成功，无失败项。");
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    public static string OutcomeText(LivePhoto.Core.RepairOutcome o) => o switch
    {
        LivePhoto.Core.RepairOutcome.Success => "成功",
        LivePhoto.Core.RepairOutcome.Skipped => "跳过",
        LivePhoto.Core.RepairOutcome.VerifyFailed => "校验未通过",
        LivePhoto.Core.RepairOutcome.ReadFailed => "读取失败",
        LivePhoto.Core.RepairOutcome.WriteFailed => "写入失败",
        LivePhoto.Core.RepairOutcome.GuardRejected => "被安全闸门拒绝",
        LivePhoto.Core.RepairOutcome.Cancelled => "已取消",
        _ => o.ToString(),
    };

    private static string Csv(string v) => v;

    private static string Escape(string v)
    {
        if (v.Contains(',') || v.Contains('"') || v.Contains('\n'))
        {
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        }

        return v;
    }
}
