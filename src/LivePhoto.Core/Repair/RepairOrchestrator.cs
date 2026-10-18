using LivePhoto.Core.Analysis;
using LivePhoto.Core.Jpeg;
using LivePhoto.Core.Mp4;

namespace LivePhoto.Core.Repair;

/// <summary>单条修复的结果。</summary>
public sealed class RepairResult
{
    public required RepairOutcome Outcome { get; init; }

    public string Message { get; init; } = string.Empty;

    public long SourceSize { get; init; }

    public long OutputSize { get; init; }

    public string? SourceSha256 { get; init; }

    public string? OutputSha256 { get; init; }

    public VerifyReport? Report { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public bool Success => Outcome == RepairOutcome.Success;
}

/// <summary>
/// 单文件端到端：只读打开源 → 重检 → 修复 → 校验 → 原子落盘。
/// <para>
/// <b>安全闸门在这里，而不是只在 UI 层。</b>UI 与 OutputPolicy 的校验都只是"早失败"优化，
/// 可以被绕过；这里的复核是真正的最后一道闸。
/// </para>
/// </summary>
public static class RepairOrchestrator
{
    /// <summary>
    /// 执行单条修复。任何异常都被转换为 <see cref="RepairOutcome"/>，不向上抛出。
    /// </summary>
    public static RepairResult Run(
        string srcPath,
        string dstPath,
        RepairOptions options,
        IPixelDecoder? decoder = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        string? srcDir = Path.GetDirectoryName(Path.GetFullPath(srcPath));
        string? dstDir = Path.GetDirectoryName(Path.GetFullPath(dstPath));

        // ---- 安全闸门：输出绝不能落在源目录内（否则会自包含递归）----
        if (srcDir is not null && dstDir is not null && IsSameOrSubPath(dstDir, srcDir))
        {
            return new RepairResult
            {
                Outcome = RepairOutcome.GuardRejected,
                Message = $"输出目录不能位于源目录内（{dstDir}）",
            };
        }

        byte[] data;
        try
        {
            // 只读 + 共享写权限：代码里根本不存在打开源文件写句柄的路径。
            // 1 MB 缓冲 + SequentialScan：远程盘（SMB）上小块读请求会放大往返成本。
            using FileStream fs = new(srcPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                                      1 << 20, FileOptions.SequentialScan);
            data = new byte[fs.Length];
            fs.ReadExactly(data, 0, data.Length);
        }
        catch (Exception ex)
        {
            return new RepairResult { Outcome = RepairOutcome.ReadFailed, Message = $"读取失败：{ex.Message}" };
        }

        string srcSha = Sha256(data);

        // 修复前重新判定，避免"扫描时该修、执行时已不适用"
        AnalysisResult ar = Analyzer.AnalyzeBytes(srcPath, Path.GetFileName(srcPath), data.Length, data);

        // 用户显式勾选 = 明确授权：「可疑」也放行。
        // 窄口径——只提升 Suspicious 一种结论；按定义它必然有内嵌视频，结构底线自动成立。
        // 其余结论（非实况 / 仅外挂 MOV / iPhone 原生 / 已合规 / 损坏）勾选也仍跳过。
        //
        // 注：2026-10-17 判据修正后，「有内嵌视频 + 无标准声明」这一种形态已改判 NeedFix
        // （真机实测：不修就被当成静态图片、视频播不了，属明确缺陷），不再落到 Suspicious，
        // 因此本分支当前<b>没有自动触发路径</b>。保留它是为了将来出现真正的"存疑形态"
        // 时，授权机制仍然有效。
        bool viaOverride = !ar.NeedsFix && options.UserOverride && ar.Condition == PhotoCondition.Suspicious;
        if (!ar.NeedsFix && !viaOverride)
        {
            return new RepairResult
            {
                Outcome = RepairOutcome.Skipped,
                Message = ar.Reason,
                SourceSize = data.Length,
                SourceSha256 = srcSha,
            };
        }

        // ---- 切分三段 ----
        int eoi = JpegReader.FindEoi(data);
        if (eoi < 0)
        {
            return new RepairResult
            {
                Outcome = RepairOutcome.VerifyFailed,
                Message = "找不到 EOI",
                SourceSize = data.Length,
                SourceSha256 = srcSha,
            };
        }

        byte[] srcJpg = new byte[eoi + 2];
        Array.Copy(data, srcJpg, srcJpg.Length);

        // 直接在原数组上定位视频起点（offset 重载），不再整段拷一份 tail ——
        // srcBlk/srcMov 从 data 切片取出，字节与逐段拷贝完全一致。
        int tailStart = eoi + 2;
        int videoStart = Mp4Inspector.FindVideoStart(data, tailStart);
        if (videoStart < 0)
        {
            return new RepairResult
            {
                Outcome = RepairOutcome.VerifyFailed,
                Message = "EOI 之后找不到内嵌 MP4",
                SourceSize = data.Length,
                SourceSha256 = srcSha,
            };
        }

        byte[] srcBlk = data[tailStart..videoStart];
        byte[] srcMov = data[videoStart..];

        byte[] dstMov = srcMov;
        if (options.Mode == RepairMode.Clean)
        {
            int cov = Mp4Inspector.BoxesOf(srcMov).Coverage;
            if (cov < srcMov.Length)
            {
                dstMov = new byte[cov];
                Array.Copy(srcMov, dstMov, cov);
            }
        }

        // ---- 容器补全：内嵌视频缺 ftyp 盒时补回标准头 ----
        // 必须在 RepairJpegPart 之前：XMP 的 Item:Length 取的是 dstMov.Length，
        // 补了 20 字节后声明长度必须跟着变，否则播放器会从错误的位置开始切视频。
        //
        // 补不了就<b>拒绝写出</b>而不是原样吐出去 —— 否则用户看到的是一份"看起来修过、
        // 实际照旧播不出"的文件，比明确报错更糟。
        string? containerNote = null;
        if (!Mp4Inspector.StartsWithFtyp(dstMov))
        {
            byte[] completed = Mp4ContainerRepair.AddFtyp(dstMov, out int patchedOffsets);
            if (patchedOffsets <= 0)
            {
                return new RepairResult
                {
                    Outcome = RepairOutcome.VerifyFailed,
                    Message = "内嵌视频缺 ftyp 盒，但索引表（stco/co64）解析不出来，无法安全补全",
                    SourceSize = data.Length,
                    SourceSha256 = srcSha,
                };
            }

            dstMov = completed;
            containerNote = $"补回 ftyp 盒（+{Mp4ContainerRepair.FtypSize} B，同步平移 {patchedOffsets} 处 chunk 偏移）";
        }

        // ---- 修复（只改 JPEG 部分，视频数据一个字节不动）----
        byte[] dstJpg;
        List<string> notes;
        try
        {
            dstJpg = RepairEngine.RepairJpegPart(
                srcJpg, dstMov.Length, ar.Mime ?? "video/mp4", ar.DurationMs,
                options.MakerNotePolicy, out notes);

            if (containerNote is not null)
            {
                notes.Insert(0, containerNote);
            }
        }
        catch (Exception ex)
        {
            return new RepairResult
            {
                Outcome = RepairOutcome.VerifyFailed,
                Message = $"修复失败：{ex.Message}",
                SourceSize = data.Length,
                SourceSha256 = srcSha,
            };
        }

        ct.ThrowIfCancellationRequested();

        // ---- 校验：不过就根本不落盘 ----
        VerifyReport report = RepairVerifier.Verify(
            srcJpg, srcBlk, srcMov,
            dstJpg, srcBlk, dstMov,
            decoder);

        if (!report.Passed)
        {
            string why = string.Join("; ", report.Failures.Select(f => $"{f.Name}: {f.Detail}"));
            return new RepairResult
            {
                Outcome = RepairOutcome.VerifyFailed,
                Message = $"校验未通过，拒绝写出 → {why}",
                SourceSize = data.Length,
                SourceSha256 = srcSha,
                Report = report,
                Notes = notes,
            };
        }

        // ---- 原子落盘 ----
        string? tmp = null;
        try
        {
            if (dstDir is not null)
            {
                Directory.CreateDirectory(dstDir);
            }

            tmp = dstPath + ".tmp";
            using (FileStream fs = new(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                                       1 << 20, FileOptions.SequentialScan))
            {
                fs.Write(dstJpg);
                fs.Write(srcBlk);
                fs.Write(dstMov);
            }

            File.Move(tmp, dstPath, overwrite: false);
            tmp = null;
        }
        catch (Exception ex)
        {
            if (tmp is not null && File.Exists(tmp))
            {
                try
                {
                    File.Delete(tmp);
                }
                catch
                {
                    // 清理失败不影响主错误的返回
                }
            }

            return new RepairResult
            {
                Outcome = RepairOutcome.WriteFailed,
                Message = $"写入失败：{ex.Message}",
                SourceSize = data.Length,
                SourceSha256 = srcSha,
                Report = report,
                Notes = notes,
            };
        }

        long outSize = dstJpg.Length + srcBlk.Length + dstMov.Length;

        // 成品哈希：对刚落盘的三段缓冲区做增量哈希，不再把成品读回来。
        // 写进文件的就是这三段，所以哈希值与"读回文件再算"完全一致（黄金锚点测试
        // b65b518c… 锁死了这一点）；省掉的是远程盘上每文件一次全量网络读。
        using System.Security.Cryptography.IncrementalHash ih =
            System.Security.Cryptography.IncrementalHash.CreateHash(
                System.Security.Cryptography.HashAlgorithmName.SHA256);
        ih.AppendData(dstJpg);
        ih.AppendData(srcBlk);
        ih.AppendData(dstMov);
        string outSha = Convert.ToHexString(ih.GetHashAndReset()).ToLowerInvariant();

        return new RepairResult
        {
            Outcome = RepairOutcome.Success,
            // 经用户勾选放行的可疑文件带审计前缀，日志/CSV 报告可与常规修复区分
            Message = (viaOverride ? "[可疑·勾选授权] " : string.Empty)
                      + $"{data.Length} → {outSize} B ({outSize - data.Length:+0;-#})",
            SourceSize = data.Length,
            OutputSize = outSize,
            SourceSha256 = srcSha,
            OutputSha256 = outSha,
            Report = report,
            Notes = notes,
        };
    }

    /// <summary>判断 child 是否与 parent 相同、或是其子孙路径（含大小写不敏感比较）。</summary>
    public static bool IsSameOrSubPath(string child, string parent)
    {
        string c = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
        string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        StringComparison cmp = StringComparison.OrdinalIgnoreCase;
        return c.Equals(p, cmp) ||
               c.StartsWith(p + Path.DirectorySeparatorChar, cmp);
    }

    private static string Sha256(byte[] data) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();
}
