using LivePhoto.Core;
using LivePhoto.Core.Repair;

namespace LivePhoto.App.Services;

/// <summary>
/// 批量编排：并行度、进度聚合、ETA、取消。
/// <para>单条异常<b>不中断整批</b>；取消后已落盘的文件必然都通过过校验。</para>
/// </summary>
public sealed class BatchRunner
{
    private readonly IPixelDecoder _decoder;

    public BatchRunner(IPixelDecoder decoder) => _decoder = decoder;

    public async Task<BatchSummary> RunAsync(
        IReadOnlyList<string> sourceFiles,
        string sourceRoot,
        string outputRoot,
        RepairOptions repairOptions,
        ConflictStrategy conflict,
        int parallelism,
        IProgress<BatchProgress>? progress,
        CancellationToken ct)
    {
        BatchSummary summary = new();
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        int done = 0;
        object gate = new();

        await Parallel.ForEachAsync(
            sourceFiles,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = ct },
            async (src, token) =>
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                string target = OutputPolicy.BuildTargetPath(sourceRoot, outputRoot, src, conflict);
                BatchEntry entry;
                RepairResult? r = null;

                if (conflict == ConflictStrategy.Skip && File.Exists(target))
                {
                    entry = new BatchEntry
                    {
                        FileName = Path.GetFileName(src),
                        SourcePath = src,
                        Outcome = RepairOutcome.Skipped,
                        Message = "输出已存在，按策略跳过",
                    };
                }
                else
                {
                    r = await Task.Run(() => RepairOrchestrator.Run(src, target, repairOptions, _decoder, token), token)
                                   .ConfigureAwait(false);
                    entry = new BatchEntry
                    {
                        FileName = Path.GetFileName(src),
                        SourcePath = src,
                        TargetPath = r.Success ? target : null,
                        Outcome = r.Outcome,
                        Message = r.Message,
                        SourceSize = r.SourceSize,
                        OutputSize = r.OutputSize,
                        SourceSha256 = r.SourceSha256,
                        OutputSha256 = r.OutputSha256,
                    };
                }

                int current;
                lock (gate)
                {
                    summary.Entries.Add(entry);
                    summary.Attempted++;
                    switch (entry.Outcome)
                    {
                        case RepairOutcome.Success:
                            summary.Succeeded++;
                            summary.TotalInputBytes += entry.SourceSize;
                            summary.TotalOutputBytes += entry.OutputSize;
                            break;
                        case RepairOutcome.Skipped:
                            summary.Skipped++;
                            break;
                        case RepairOutcome.Cancelled:
                            summary.Cancelled++;
                            break;
                        default:
                            summary.Failed++;
                            break;
                    }

                    done++;
                    current = done;
                }

                progress?.Report(new BatchProgress(
                    current, sourceFiles.Count, Path.GetFileName(src), entry.Outcome, entry.Message));
            }).ConfigureAwait(false);

        sw.Stop();
        summary.Elapsed = sw.Elapsed;
        summary.Entries.Sort((a, b) => string.CompareOrdinal(a.FileName, b.FileName));
        return summary;
    }
}
