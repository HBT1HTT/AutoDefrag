namespace AutoDefrag.Rewrite;

/// <summary>卷几何信息（来自 FSCTL_GET_NTFS_VOLUME_DATA，失败时用 GetDiskFreeSpaceW 回退）。</summary>
public sealed record VolumeGeometry(
    int BytesPerSector,
    int SectorsPerCluster,
    int ClusterBytes,
    long TotalClusters,
    long FreeClusters,
    long MftStartLcn)
{
    public long TotalBytes => TotalClusters * (long)ClusterBytes;
}

/// <summary>文件的一个物理区段；StartLcn 为 -1 表示稀疏/空洞。</summary>
public sealed record Extent(long StartVcn, long StartLcn, long LengthClusters)
{
    public bool IsSparse => StartLcn < 0;
    public string Text => IsSparse ? $"稀疏@{StartVcn}+{LengthClusters}" : $"{StartLcn}+{LengthClusters}";
}

/// <summary>单个文件的 Runlist 统计结果。</summary>
public sealed record FileLayout(
    string Path,
    long SizeBytes,
    int ExtentCount,
    long TotalClusters,
    long LargestExtentClusters,
    long FragmentedClusters,
    bool HasSparse)
{
    public double FragmentationPercent =>
        TotalClusters <= 0 ? 0 : FragmentedClusters * 100.0 / TotalClusters;
}

/// <summary>一段连续空闲簇。</summary>
public sealed record FreeRun(long StartLcn, long LengthClusters);

/// <summary>备用数据流（ADS）项。</summary>
public sealed record AdsStream(string Name, long SizeBytes);

/// <summary>全卷空闲布局。</summary>
public sealed class VolumeLayout
{
    public long TotalClusters { get; init; }
    public long FreeClusters { get; init; }
    public int FreeRunCount { get; init; }
    public long MaxFreeRunClusters { get; init; }
    public long MaxFreeRunStartLcn { get; init; }
    public long RewriteUpperBoundClusters { get; init; }

    public double FreeFragmentationPercent =>
        FreeClusters <= 0 ? 0 : (1.0 - (double)MaxFreeRunClusters / FreeClusters) * 100.0;
}

/// <summary>只读分析汇总。</summary>
public sealed class AnalysisResult
{
    public required VolumeGeometry Geometry { get; init; }
    public VolumeLayout? Volume { get; init; }
    public List<FileLayout> Files { get; init; } = new();
    public Dictionary<string, int> SkipCounts { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public int FilesScanned { get; set; }
    public int FilesProbed { get; set; }
    public int FilesUnreadable { get; set; }
    public int SkippedDirectories { get; set; }
    public int CountedDirectories { get; set; }
    public int EnumerateFailures { get; set; }
    public int AttributeFailures { get; set; }
    public bool ScanTruncated { get; set; }

    public long AnalyzedClusters { get; set; }
    public long AnalyzedFragmentedClusters { get; set; }

    public double FileFragmentationPercent =>
        AnalyzedClusters <= 0 ? 0 : AnalyzedFragmentedClusters * 100.0 / AnalyzedClusters;

    public int FragmentedFileCount => Files.Count(f => f.ExtentCount >= 2);

    public void AddSkip(string reason, int count = 1)
    {
        SkipCounts.TryGetValue(reason, out var current);
        SkipCounts[reason] = current + count;
    }
}

/// <summary>排序后的候选文件。</summary>
public sealed class CandidateFile
{
    public required FileLayout Layout { get; init; }
    public long Score { get; init; }
    public string ScoreReason { get; init; } = string.Empty;
}

/// <summary>单次重写任务的参数；dry-run 只是把 DryRun 打开。</summary>
public sealed class RewriteOptions
{
    /// <summary>只生成计划，不动盘。</summary>
    public bool DryRun { get; init; }

    /// <summary>单次最多处理多少个文件。</summary>
    public int MaxFiles { get; init; } = 40;

    /// <summary>单次最多复制多少字节。</summary>
    public long MaxBytes { get; init; } = 8L * 1024 * 1024 * 1024;

    /// <summary>小于该簇数的文件不参与（默认 8 簇）。</summary>
    public int MinFileClusters { get; init; } = 8;

    /// <summary>枚举上限，避免在超大卷上扫太久。</summary>
    public int MaxScanFiles { get; init; } = 200_000;

    /// <summary>Runlist 查询上限（只对大文件查询）。</summary>
    public int MaxExtentProbes { get; init; } = 20_000;

    /// <summary>扫描时间预算（秒）。</summary>
    public int ScanSeconds { get; init; } = 90;

    /// <summary>只扫描该目录（用于小规模验证）；null 表示整卷。</summary>
    public string? ScanRoot { get; init; }

    /// <summary>文件级碎片率低于该值不参与整理；0 表示不按碎片率过滤（全部合格候选）。</summary>
    public double MinFragmentationPercent { get; init; } = 0.0;

    /// <summary>可回收碎片簇折合的字节数低于该值不参与：避免为几 MB 收益重读重写整文件。</summary>
    public long MinReclaimBytes { get; init; } = 4L * 1024 * 1024;

    /// <summary>执行前额外预留的空闲簇数（0 表示按 0.5% 自动计算）。</summary>
    public long SafetyReserveClusters { get; init; }

    /// <summary>出现该文件即停止后续文件（当前文件不强行中断）。</summary>
    public string StopFlagFile { get; init; } = AppPaths.StopFlagFile;

    /// <summary>本轮采用的整理方式说明，仅用于报告展示（例如“自动 → 本程序整理（存储块 64KB）”）。</summary>
    public string EngineDecision { get; init; } = string.Empty;

    /// <summary>可选 CPU 守护：分析/枚举阶段由它采样，命中暂停阈值时原地等待。</summary>
    public CpuGuard? Guard { get; init; }

    public static RewriteOptions Default => new();

    public static RewriteOptions Quick => new()
    {
        ScanSeconds = 20,
        MaxScanFiles = 40_000,
        MaxExtentProbes = 6_000,
        MinFragmentationPercent = 0
    };

    public static RewriteOptions Compare => new()
    {
        ScanSeconds = 10,
        MaxScanFiles = 20_000,
        MaxExtentProbes = 2_000,
        MinFragmentationPercent = 0
    };
}

/// <summary>单个文件的处理结果。</summary>
public sealed class RewriteFileResult
{
    public required string Path { get; init; }
    public string Status { get; init; } = string.Empty;   // 成功 / 跳过 / 失败 / 已回滚
    public string Reason { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string BeforeExtents { get; init; } = string.Empty;
    public string AfterExtents { get; init; } = string.Empty;
    public int BeforeExtentCount { get; init; }
    public int AfterExtentCount { get; init; }
    public long BeforeFragmentedClusters { get; init; }
    public long AfterFragmentedClusters { get; init; }
    public string HashMode { get; init; } = string.Empty;
    public double ElapsedSeconds { get; init; }
}

/// <summary>一次重写任务的汇总。</summary>
public sealed class RewriteSummary
{
    public List<RewriteFileResult> Results { get; } = new();
    public bool Stopped { get; set; }
    public long RewrittenBytes { get; set; }

    public int Succeeded => Results.Count(r => r.Status == "成功");
    public int Failed => Results.Count(r => r.Status is "失败" or "已回滚");
    public int Skipped => Results.Count(r => r.Status == "跳过");

    public long ReclaimedFragmentedClusters =>
        Results.Where(r => r.Status == "成功")
            .Sum(r => Math.Max(0, r.BeforeFragmentedClusters - r.AfterFragmentedClusters));

    public Dictionary<string, int> SkipReasons
    {
        get
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var result in Results.Where(r => r.Status == "跳过"))
            {
                map.TryGetValue(result.Reason, out var count);
                map[result.Reason] = count + 1;
            }
            return map;
        }
    }
}
