using System.IO;

namespace AutoDefrag.Rewrite;

/// <summary>
/// 只读分析器：卷位图 → 空闲布局；文件枚举 → Runlist → 碎片指标。
/// 不写盘、不调用 defrag.exe。
/// </summary>
public static class VolumeAnalyzer
{
    /// <summary>这些目录下的内容一律不参与分析/整理（系统关键或回收站）。</summary>
    private static readonly HashSet<string> SkipDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$RECYCLE.BIN", "System Volume Information", "Windows", "Program Files", "Program Files (x86)",
        "ProgramData", "Recovery", "PerfLogs", "Boot", "EFI", "Config.Msi", "$WinREAgent", "Windows.old"
    };

    private static readonly HashSet<string> SkipFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "pagefile.sys", "hiberfil.sys", "swapfile.sys", "bootmgr", "BOOTNXT", "dumpstack.log",
        "dumpstack.log.tmp", "DumpStack.log", "DumpStack.log.tmp"
    };

    /// <summary>读取卷几何 + 位图 + 文件碎片；任何一步失败都如实返回 null（调用方不猜）。</summary>
    public static AnalysisResult? Analyze(string drive, RewriteOptions options)
    {
        var geometry = NtfsNative.TryGetGeometry(drive);
        if (geometry is null)
        {
            Logger.Warn($"{drive} 无法读取卷几何，跳过分析");
            return null;
        }

        Logger.Info($"{drive} 卷几何: 簇={geometry.ClusterBytes} 字节, 总簇={geometry.TotalClusters}, 空闲簇={geometry.FreeClusters}, MFT起点={geometry.MftStartLcn}");

        var bitmap = NtfsNative.TryGetVolumeBitmap(drive, geometry);
        var volume = bitmap is null
            ? null
            : ReadFreeLayout(geometry, bitmap, options.SafetyReserveClusters, out _);

        var result = new AnalysisResult { Geometry = geometry, Volume = volume };

        var guard = options.Guard;
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(options.ScanSeconds, 1));
        var minBytes = (long)Math.Max(options.MinFileClusters, 1) * geometry.ClusterBytes;
        var root = options.ScanRoot ?? drive;
        if (!root.EndsWith('\\')) root += "\\";

        var stop = false;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0 && !stop)
        {
            // 枚举/目录遍历也是重 I/O：每个目录前采样一次，命中阈值就原地等待
            guard?.Check();

            var directory = stack.Pop();
            result.CountedDirectories++;
            IEnumerator<string>? enumerator = null;
            try
            {
                enumerator = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
            }
            catch (Exception ex)
            {
                result.AddSkip("目录无法访问");
                if (result.SkippedDirectories < 10)
                    Logger.Warn($"无法枚举目录 {directory}: {ex.GetType().Name} {ex.Message}");
                result.SkippedDirectories++;
                continue;
            }

            try
            {
                while (true)
                {
                    bool hasNext;
                    try { hasNext = enumerator.MoveNext(); }
                    catch (Exception ex)
                    {
                        if (result.EnumerateFailures < 10)
                            Logger.Warn($"枚举中断 {directory}: {ex.GetType().Name} {ex.Message}");
                        result.EnumerateFailures++;
                        break;
                    }

                    if (!hasNext) break;

                    if (DateTime.UtcNow > deadline || result.FilesScanned >= options.MaxScanFiles)
                    {
                        result.ScanTruncated = true;
                        stop = true;
                        break;
                    }

                    var entry = enumerator.Current;
                    result.FilesScanned++;

                    // 每 256 个条目采样一次，避免逐条 P/Invoke 的开销
                    if (guard is not null && (result.FilesScanned & 0xFF) == 0)
                        guard.Check();

                    // FileInfo.Attributes 在这个环境下对目录会抛 FileNotFound，目录判断改用 File.GetAttributes
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(entry); }
                    catch (Exception ex)
                    {
                        if (result.AttributeFailures < 10)
                            Logger.Warn($"属性读取失败 {entry}: {ex.GetType().Name} {ex.Message}");
                        result.AttributeFailures++;
                        result.AddSkip("属性读取失败");
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        var name = Path.GetFileName(entry);
                        if (SkipDirectoryNames.Contains(name)) continue;
                        if ((attributes & FileAttributes.System) != 0) continue;
                        stack.Push(entry);
                        continue;
                    }

                    long size;
                    try { size = new FileInfo(entry).Length; }
                    catch { result.AddSkip("大小读取失败"); continue; }

                    var fileName = Path.GetFileName(entry);
                    if (fileName.StartsWith('$') || SkipFileNames.Contains(fileName))
                    {
                        result.AddSkip("系统关键文件");
                        continue;
                    }

                    if (IsUnsupportedAttribute(attributes, out var attributeReason))
                    {
                        result.AddSkip(attributeReason);
                        continue;
                    }

                    if (size < minBytes)
                    {
                        result.AddSkip("文件太小");
                        continue;
                    }

                    if (result.FilesProbed >= options.MaxExtentProbes)
                    {
                        result.ScanTruncated = true;
                        stop = true;
                        break;
                    }
                    result.FilesProbed++;

                    if (!NtfsNative.TryGetExtents(entry, out var extents, out var error))
                    {
                        result.FilesUnreadable++;
                        result.AddSkip("读取Runlist失败");
                        Logger.Info($"分析跳过 {entry}: {error}");
                        continue;
                    }

                    var layout = NtfsNative.BuildLayout(entry, size, extents);
                    if (layout.TotalClusters <= 0)
                    {
                        result.AddSkip("空文件");
                        continue;
                    }

                    result.Files.Add(layout);
                    result.AnalyzedClusters += layout.TotalClusters;
                    result.AnalyzedFragmentedClusters += layout.FragmentedClusters;
                }
            }
            finally
            {
                enumerator?.Dispose();
            }
        }

        if (result.ScanTruncated)
        {
            Logger.Warn($"{drive} 分析达到上限（枚举={result.FilesScanned}, Runlist={result.FilesProbed}, 时间={options.ScanSeconds}s），结果按抽样看待");
        }

        var coverage = geometry.TotalClusters > 0
            ? result.AnalyzedClusters * 100.0 / geometry.TotalClusters
            : 0;
        Logger.Info(
            $"{drive} 分析完成: 目录={result.CountedDirectories}, 跳过目录={result.SkippedDirectories}, 枚举失败={result.EnumerateFailures}, " +
            $"枚举={result.FilesScanned}, Runlist={result.FilesProbed}, " +
            $"读取失败={result.FilesUnreadable}, 已分析簇={result.AnalyzedClusters}, " +
            $"覆盖率={coverage:0.##}%（碎片率仅代表已分析文件）, 文件级碎片率={result.FileFragmentationPercent:0.##}%");

        if (volume is not null)
        {
            Logger.Info(
                $"{drive} 空闲布局: 空闲簇={volume.FreeClusters}, 区间={volume.FreeRunCount}, " +
                $"最大连续={volume.MaxFreeRunClusters} 簇（起点 LCN {volume.MaxFreeRunStartLcn}）, " +
                $"空闲碎片度={volume.FreeFragmentationPercent:0.##}%, 可整理上限={volume.RewriteUpperBoundClusters} 簇");
        }
        else
        {
            Logger.Info($"{drive} 空闲布局未知（未读取到卷位图）：计划只依据文件区段与卷几何，替身位置由 Windows 分配");
        }

        return result;
    }

    /// <summary>只重读卷位图，用于整理后的空闲布局对比。</summary>
    public static VolumeLayout? ReadVolumeLayout(string drive, RewriteOptions options)
    {
        var geometry = NtfsNative.TryGetGeometry(drive);
        if (geometry is null) return null;

        var bitmap = NtfsNative.TryGetVolumeBitmap(drive, geometry);
        return bitmap is null ? null : ReadFreeLayout(geometry, bitmap, options.SafetyReserveClusters, out _);
    }

    private static VolumeLayout ReadFreeLayout(
        VolumeGeometry geometry, NtfsNative.VolumeBitmap bitmap, long safetyReserve, out List<FreeRun> runs)
    {
        runs = new List<FreeRun>();
        var total = geometry.TotalClusters;
        var bits = bitmap.Bits;

        long freeClusters = 0;
        long runStart = -1;
        long runLength = 0;
        long maxRun = 0;
        long maxRunStart = 0;

        for (long lcn = 0; lcn < total; lcn++)
        {
            var index = (int)(lcn >> 3);
            if (index >= bits.Length) break;

            var allocated = (bits[index] & (1 << (int)(lcn & 7))) != 0;
            if (!allocated)
            {
                freeClusters++;
                if (runStart < 0)
                {
                    runStart = lcn;
                    runLength = 0;
                }
                runLength++;
            }
            else if (runStart >= 0)
            {
                runs.Add(new FreeRun(runStart, runLength));
                if (runLength > maxRun)
                {
                    maxRun = runLength;
                    maxRunStart = runStart;
                }
                runStart = -1;
                runLength = 0;
            }
        }

        if (runStart >= 0)
        {
            runs.Add(new FreeRun(runStart, runLength));
            if (runLength > maxRun)
            {
                maxRun = runLength;
                maxRunStart = runStart;
            }
        }

        var reserve = safetyReserve > 0 ? safetyReserve : Math.Max(16, freeClusters / 200);
        var usable = Math.Max(0, freeClusters - reserve);
        var upperBound = Math.Min(maxRun, usable);

        return new VolumeLayout
        {
            TotalClusters = total,
            FreeClusters = freeClusters,
            FreeRunCount = runs.Count,
            MaxFreeRunClusters = maxRun,
            MaxFreeRunStartLcn = maxRunStart,
            RewriteUpperBoundClusters = upperBound
        };
    }

    /// <summary>稀疏/压缩/加密/重解析点/脱机/临时文件都不参与重写。</summary>
    public static bool IsUnsupportedAttribute(FileAttributes attributes, out string reason)
    {
        if ((attributes & FileAttributes.SparseFile) != 0) { reason = "稀疏文件"; return true; }
        if ((attributes & FileAttributes.Compressed) != 0) { reason = "压缩文件"; return true; }
        if ((attributes & FileAttributes.Encrypted) != 0) { reason = "加密文件"; return true; }
        if ((attributes & FileAttributes.ReparsePoint) != 0) { reason = "重解析点"; return true; }
        if ((attributes & FileAttributes.Offline) != 0) { reason = "脱机文件"; return true; }
        if ((attributes & FileAttributes.Temporary) != 0) { reason = "临时文件"; return true; }
        if ((attributes & FileAttributes.System) != 0) { reason = "系统文件"; return true; }
        reason = string.Empty;
        return false;
    }
}
