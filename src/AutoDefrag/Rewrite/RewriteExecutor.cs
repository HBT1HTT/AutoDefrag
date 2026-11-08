using System.IO;
using System.Security.Cryptography;

namespace AutoDefrag.Rewrite;

/// <summary>
/// 单文件重写事务：
///   预检 → 快照元数据(属性/时间/ACL/ADS) → 复制到同目录临时文件 → 校验(大小+哈希)
///   → 还原元数据并回读比对 → 原子替换 → 复核 Runlist → 删除备份释放旧簇。
/// 任一步失败都保留原文件、删除临时文件；替换后复核失败则用备份恢复。
/// 每个文件独立事务，失败不影响其它文件；文件之间不做并行。
/// </summary>
public static class RewriteExecutor
{
    /// <summary>全量哈希的字节上限（2GiB）；超过则按首/中/尾抽样。</summary>
    private const long FullHashLimitBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>抽样时每段的字节数。</summary>
    private const int SampleChunkBytes = 4 * 1024 * 1024;

    public static RewriteFileResult Execute(
        string path, RewritePlan plan, CpuGuard? guard = null, Action<string, long>? onPhase = null)
    {
        var started = DateTime.UtcNow;
        var geometry = plan.Geometry;

        onPhase?.Invoke("预检", 0);
        guard?.Check();
        var beforeLayout = TryReadLayout(path, out var beforeExtents);
        if (beforeLayout is null)
        {
            return Skip(path, "无法读取整理前的区段", started, 0, string.Empty, 0);
        }

        // ---------------------------------------------------------- 1. 预检
        var precheck = Precheck(path);
        if (precheck is not null)
        {
            Logger.Info($"跳过 {path}: {precheck}");
            return Skip(path, precheck, started, beforeLayout.SizeBytes,
                NtfsNative.ExtentsText(beforeExtents), beforeLayout.ExtentCount);
        }

        var fileInfo = new FileInfo(path);
        if (fileInfo.Length <= 0)
            return Skip(path, "空文件", started, 0, NtfsNative.ExtentsText(beforeExtents), beforeLayout.ExtentCount);

        // 空间检查（再算一次，因为前面的文件可能已经消耗了空闲）
        var volume = VolumeAnalyzer.ReadVolumeLayout(plan.Drive, plan.Options);
        var clusterBytes = geometry.ClusterBytes;
        var freeBytes = (volume?.FreeClusters ?? geometry.FreeClusters) * (long)clusterBytes;
        var reserveBytes = Math.Max(16L, (volume?.FreeClusters ?? geometry.FreeClusters) / 200) * clusterBytes;
        if (freeBytes - reserveBytes < fileInfo.Length)
        {
            var reason = "可用空间不足";
            Logger.Warn($"跳过 {path}: 需要 {DiskService.FormatBytes(fileInfo.Length)}，可用不足（保留安全余量 {DiskService.FormatBytes(reserveBytes)}）");
            return Skip(path, reason, started, fileInfo.Length,
                NtfsNative.ExtentsText(beforeExtents), beforeLayout.ExtentCount);
        }

        // ---------------------------------------------------------- 2. 快照元数据
        var attributes = fileInfo.Attributes;
        var creation = fileInfo.CreationTimeUtc;
        var lastWrite = fileInfo.LastWriteTimeUtc;
        var lastAccess = fileInfo.LastAccessTimeUtc;
        var sddl = NtfsNative.TryGetSddl(path);
        var ads = NtfsNative.TryGetNamedStreams(path);

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileName(path);
        var token = Guid.NewGuid().ToString("N")[..8];
        var tempPath = Path.Combine(directory, $".{name}.adf-tmp-{token}");
        var backupPath = Path.Combine(directory, $".{name}.adf-bak-{token}");

        var hashMode = fileInfo.Length <= FullHashLimitBytes ? "全量SHA256" : "抽样SHA256(首/中/尾)";
        var result = new RewriteFileResult
        {
            Path = path,
            SizeBytes = fileInfo.Length,
            BeforeExtents = NtfsNative.ExtentsText(beforeExtents),
            BeforeExtentCount = beforeLayout.ExtentCount,
            BeforeFragmentedClusters = beforeLayout.FragmentedClusters,
            HashMode = hashMode
        };

        Logger.Info($"[重写] 开始 {path} 大小={DiskService.FormatBytes(fileInfo.Length)} 区段={beforeLayout.ExtentCount} 碎片={beforeLayout.FragmentationPercent:0.##}% 校验={hashMode}");

        var originalHash = (byte[]?)null;
        try
        {
            // -------------------------------------------------- 3. 复制到临时文件
            onPhase?.Invoke("复制", 0);
            originalHash = CopyWithHash(path, tempPath, fileInfo.Length, guard, onPhase);
            Logger.Info($"[重写] 复制完成 {path} -> 临时文件，原始哈希={Convert.ToHexString(originalHash)[..16]}…");
        }
        catch (Exception ex)
        {
            Cleanup(tempPath, backupPath);
            Logger.Error($"[重写] 复制失败 {path}: {ex.Message}");
            return Fail(result, "复制失败: " + ex.Message, started, NtfsNative.ExtentsText(beforeExtents), beforeLayout);
        }

        try
        {
            // -------------------------------------------------- 4. 校验
            var tempInfo = new FileInfo(tempPath);
            if (tempInfo.Length != fileInfo.Length)
            {
                Cleanup(tempPath, backupPath);
                return Fail(result, $"复制后大小不一致（{tempInfo.Length} != {fileInfo.Length}）", started,
                    NtfsNative.ExtentsText(beforeExtents), beforeLayout);
            }

            onPhase?.Invoke("校验", 0);
            var copiedHash = ComputeHash(tempPath, fileInfo.Length, guard, onPhase);
            if (!AreEqual(originalHash, copiedHash))
            {
                Cleanup(tempPath, backupPath);
                return Fail(result, "哈希校验不一致", started, NtfsNative.ExtentsText(beforeExtents), beforeLayout);
            }

            // 校验新位置的区段数：没有减少就没有整理收益，直接放弃并保留原文件
            if (!NtfsNative.TryGetExtents(tempPath, out var tempExtents, out var tempError))
            {
                Cleanup(tempPath, backupPath);
                return Fail(result, "无法读取临时文件区段: " + tempError, started,
                    NtfsNative.ExtentsText(beforeExtents), beforeLayout);
            }

            var tempLayout = NtfsNative.BuildLayout(tempPath, fileInfo.Length, tempExtents);
            if (tempLayout.ExtentCount >= beforeLayout.ExtentCount)
            {
                Cleanup(tempPath, backupPath);
                return Skip(path,
                    $"新位置区段数没有减少（{beforeLayout.ExtentCount} -> {tempLayout.ExtentCount}），放弃重写",
                    started, fileInfo.Length, NtfsNative.ExtentsText(beforeExtents), beforeLayout.ExtentCount);
            }

            // -------------------------------------------------- 5. 还原元数据
            File.SetCreationTimeUtc(tempPath, creation);
            File.SetLastWriteTimeUtc(tempPath, lastWrite);
            File.SetLastAccessTimeUtc(tempPath, lastAccess);
            File.SetAttributes(tempPath, attributes);

            if (ads.Count > 0)
            {
                CopyAds(path, tempPath, ads);
            }

            if (sddl is not null)
            {
                if (!NtfsNative.TrySetSddl(tempPath, sddl, out var sddlError))
                {
                    Cleanup(tempPath, backupPath);
                    return Fail(result, "ACL 还原失败: " + sddlError, started,
                        NtfsNative.ExtentsText(beforeExtents), beforeLayout);
                }

                var applied = NtfsNative.TryGetSddl(tempPath);
                if (applied is null || !string.Equals(NormalizeSddl(applied), NormalizeSddl(sddl), StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warn($"[重写] {path} ACL 回读不一致\n  期望: {sddl}\n  实际: {applied ?? "(读取失败)"}");
                    Cleanup(tempPath, backupPath);
                    return Fail(result, "ACL 回读比对不一致", started, NtfsNative.ExtentsText(beforeExtents), beforeLayout);
                }
            }
            else
            {
                Logger.Warn($"[重写] {path} 读不到安全描述符，按目录继承默认权限继续（已在报告标注）");
            }

            // -------------------------------------------------- 6. 原子替换
            onPhase?.Invoke("替换", fileInfo.Length);
            File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);

            // 替换会合并目标文件的元数据，这里再显式按快照还原一次 ACL 并回读比对
            if (sddl is not null)
            {
                if (!NtfsNative.TrySetSddl(path, sddl, out var replaceSddlError))
                {
                    Logger.Warn($"[重写] {path} 替换后 ACL 还原失败: {replaceSddlError}，回滚");
                    return Rollback(result, path, backupPath, "替换后 ACL 还原失败", started, beforeLayout, beforeExtents);
                }

                var finalSddl = NtfsNative.TryGetSddl(path);
                if (finalSddl is null ||
                    !string.Equals(NormalizeSddl(finalSddl), NormalizeSddl(sddl), StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warn($"[重写] {path} 替换后 ACL 回读不一致，回滚");
                    return Rollback(result, path, backupPath, "替换后 ACL 回读不一致", started, beforeLayout, beforeExtents);
                }
            }

            // -------------------------------------------------- 7. 替换后复核
            if (guard is not null)
            {
                guard.Phase = "复核";
                guard.Check();
            }

            if (!NtfsNative.TryGetExtents(path, out var afterExtents, out var afterError))
            {
                Logger.Warn($"[重写] {path} 替换后读取区段失败: {afterError}，尝试用备份恢复");
                return Rollback(result, path, backupPath, "替换后无法读取区段", started, beforeLayout, beforeExtents);
            }

            var afterLayout = NtfsNative.BuildLayout(path, fileInfo.Length, afterExtents);
            if (afterLayout.ExtentCount >= beforeLayout.ExtentCount)
            {
                Logger.Warn($"[重写] {path} 替换后区段数没有减少（{beforeLayout.ExtentCount} -> {afterLayout.ExtentCount}），回滚");
                return Rollback(result, path, backupPath, "替换后区段数没有减少", started, beforeLayout, beforeExtents);
            }

            // -------------------------------------------------- 8. 释放旧空间
            try
            {
                if (File.Exists(backupPath)) File.Delete(backupPath);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[重写] {path} 旧数据备份删除失败（不影响新文件）: {ex.Message}；备份={backupPath}");
            }

            // 时间戳/属性最终确认（File.Replace 可能带进目标文件的部分元数据）
            TryRestoreTimestamps(path, creation, lastWrite, lastAccess, attributes);

            var done = new RewriteFileResult
            {
                Path = path,
                Status = "成功",
                Reason = string.Empty,
                SizeBytes = fileInfo.Length,
                BeforeExtents = NtfsNative.ExtentsText(beforeExtents),
                AfterExtents = NtfsNative.ExtentsText(afterExtents),
                BeforeExtentCount = beforeLayout.ExtentCount,
                AfterExtentCount = afterLayout.ExtentCount,
                BeforeFragmentedClusters = beforeLayout.FragmentedClusters,
                AfterFragmentedClusters = afterLayout.FragmentedClusters,
                HashMode = hashMode,
                ElapsedSeconds = (DateTime.UtcNow - started).TotalSeconds
            };

            Logger.Info(
                $"[重写] 成功 {path}: 区段 {beforeLayout.ExtentCount}->{afterLayout.ExtentCount}, " +
                $"可回收碎片 {beforeLayout.FragmentedClusters}->{afterLayout.FragmentedClusters} 簇, " +
                $"before=[{NtfsNative.ExtentsText(beforeExtents)}] after=[{NtfsNative.ExtentsText(afterExtents)}]");
            return done;
        }
        catch (Exception ex)
        {
            Logger.Error($"[重写] 异常 {path}: {ex.Message}");
            Cleanup(tempPath, backupPath);
            return Fail(result, "异常: " + ex.Message, started, NtfsNative.ExtentsText(beforeExtents), beforeLayout);
        }
    }

    // ---------------------------------------------------------------- 复制与校验

    /// <summary>顺序复制主数据流，同时计算原始文件的哈希；每 32MB 检查一次 CPU 守护。</summary>
    private static byte[] CopyWithHash(
        string source, string target, long length, CpuGuard? guard, Action<string, long>? onPhase)
    {
        // 源文件用 FileShare.Read 打开：复制期间其他进程只能读，不能改/删，保证哈希与内容对应
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 1 << 20, FileOptions.SequentialScan);

        // 一次性预分配全部长度：让 NTFS 用单次分配请求挑选足够大的连续空闲区。
        // 如果按 1MB 边写边分配，分配器会为每个小块单独挑洞，反而把文件写得更碎。
        output.SetLength(length);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        long copied = 0;
        var sinceCheck = 0;
        while (copied < length)
        {
            var want = (int)Math.Min(buffer.Length, length - copied);
            var read = input.Read(buffer, 0, want);
            if (read <= 0) break;
            output.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
            copied += read;
            onPhase?.Invoke("复制", copied);

            sinceCheck += read;
            if (guard is not null && sinceCheck >= 32 * 1024 * 1024)
            {
                sinceCheck = 0;
                guard.Check();
            }
        }

        output.Flush(flushToDisk: true);
        return hash.GetHashAndReset();
    }

    /// <summary>计算目标文件的哈希：小文件全量，大文件首/中/尾抽样。</summary>
    private static byte[] ComputeHash(
        string path, long length, CpuGuard? guard, Action<string, long>? onPhase = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1 << 20, FileOptions.SequentialScan);

        // 全量用 1MB 分块，便于插入 CPU 守护检查
        if (length <= FullHashLimitBytes)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20];
            long readBytes = 0;
            var sinceCheck = 0;
            while (readBytes < length)
            {
                var want = (int)Math.Min(buffer.Length, length - readBytes);
                var read = stream.Read(buffer, 0, want);
                if (read <= 0) break;
                sha.AppendData(buffer, 0, read);
                readBytes += read;
                onPhase?.Invoke("校验", readBytes);

                sinceCheck += read;
                if (guard is not null && sinceCheck >= 32 * 1024 * 1024)
                {
                    sinceCheck = 0;
                    guard.Check();
                }
            }

            return sha.GetHashAndReset();
        }

        using var sample = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chunk = new byte[SampleChunkBytes];
        var offsets = new[] { 0L, Math.Max(0, length / 2 - SampleChunkBytes / 2), Math.Max(0, length - SampleChunkBytes) };
        foreach (var offset in offsets.Distinct())
        {
            stream.Position = offset;
            var remaining = (int)Math.Min(SampleChunkBytes, length - offset);
            var read = 0;
            while (read < remaining)
            {
                var got = stream.Read(chunk, read, remaining - read);
                if (got <= 0) break;
                read += got;
            }
            sample.AppendData(chunk, 0, read);
            guard?.Check();
        }

        return sample.GetHashAndReset();
    }

    private static bool AreEqual(byte[]? a, byte[]? b)
    {
        if (a is null || b is null || a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    private static void CopyAds(string source, string target, IReadOnlyList<AdsStream> streams)
    {
        foreach (var stream in streams)
        {
            var sourcePath = source + ":" + stream.Name;
            var targetPath = target + ":" + stream.Name;

            using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
            Logger.Info($"[重写] 已复制备用数据流 {stream.Name} ({stream.SizeBytes} 字节)");
        }
    }

    // ---------------------------------------------------------------- 预检 / 回滚

    private static string? Precheck(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception ex) { return "读取属性失败: " + ex.Message; }

        if ((attributes & FileAttributes.ReadOnly) != 0) return "只读文件";
        if (VolumeAnalyzer.IsUnsupportedAttribute(attributes, out var attributeReason)) return attributeReason;

        var links = NtfsNative.TryGetLinkCount(path);
        if (links > 1) return $"硬链接文件（{links} 个链接）";

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 1, FileOptions.None);
            _ = stream.Length;
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) == 32)
        {
            return "文件正在使用";
        }
        catch (UnauthorizedAccessException)
        {
            return "没有访问权限";
        }
        catch (Exception ex)
        {
            return "无法独占打开: " + ex.Message;
        }

        return null;
    }

    private static RewriteFileResult Rollback(
        RewriteFileResult partial, string path, string backupPath, string reason,
        DateTime started, FileLayout before, IReadOnlyList<Extent> beforeExtents)
    {
        try
        {
            if (File.Exists(backupPath))
            {
                File.Replace(backupPath, path, null, ignoreMetadataErrors: true);
                Logger.Warn($"[重写] 已用备份恢复原文件: {path}");
                return new RewriteFileResult
                {
                    Path = path,
                    Status = "已回滚",
                    Reason = reason,
                    SizeBytes = partial.SizeBytes,
                    BeforeExtents = partial.BeforeExtents,
                    AfterExtents = NtfsNative.ExtentsText(beforeExtents),
                    BeforeExtentCount = partial.BeforeExtentCount,
                    AfterExtentCount = before.ExtentCount,
                    BeforeFragmentedClusters = partial.BeforeFragmentedClusters,
                    AfterFragmentedClusters = before.FragmentedClusters,
                    HashMode = partial.HashMode,
                    ElapsedSeconds = (DateTime.UtcNow - started).TotalSeconds
                };
            }

            Logger.Error($"[重写] 备份不存在，无法回滚: {path}（备份={backupPath}）");
            return Fail(partial, reason + "（备份缺失，未回滚）", started, partial.BeforeExtents, before);
        }
        catch (Exception ex)
        {
            Logger.Error($"[重写] 回滚失败 {path}: {ex.Message}；备份保留在 {backupPath}");
            return Fail(partial, reason + "（回滚失败，备份保留）", started, partial.BeforeExtents, before);
        }
    }

    private static void Cleanup(string tempPath, string backupPath)
    {
        try
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
        catch (Exception ex)
        {
            Logger.Warn($"清理临时文件失败 {tempPath}: {ex.Message}");
        }

        // 备份只在替换成功后才会产生；失败路径下若有残留也一并清理（原文件仍在）
        try
        {
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
        catch (Exception ex)
        {
            Logger.Warn($"清理备份文件失败 {backupPath}: {ex.Message}");
        }
    }

    private static void TryRestoreTimestamps(string path, DateTime creation, DateTime lastWrite, DateTime lastAccess, FileAttributes attributes)
    {
        try
        {
            File.SetCreationTimeUtc(path, creation);
            File.SetLastWriteTimeUtc(path, lastWrite);
            File.SetLastAccessTimeUtc(path, lastAccess);
            File.SetAttributes(path, attributes);
        }
        catch (Exception ex)
        {
            Logger.Warn($"复核时恢复时间戳/属性失败 {path}: {ex.Message}");
        }
    }

    private static FileLayout? TryReadLayout(string path, out List<Extent> extents)
    {
        extents = new List<Extent>();
        try
        {
            var size = new FileInfo(path).Length;
            if (!NtfsNative.TryGetExtents(path, out extents, out _)) return null;
            return NtfsNative.BuildLayout(path, size, extents);
        }
        catch
        {
            return null;
        }
    }

    private static RewriteFileResult Skip(
        string path, string reason, DateTime started, long size, string extents, int extentCount) =>
        new()
        {
            Path = path,
            Status = "跳过",
            Reason = reason,
            SizeBytes = size,
            BeforeExtents = extents,
            BeforeExtentCount = extentCount,
            ElapsedSeconds = (DateTime.UtcNow - started).TotalSeconds
        };

    private static RewriteFileResult Fail(
        RewriteFileResult partial, string reason, DateTime started, string extents, FileLayout before) =>
        new()
        {
            Path = partial.Path,
            Status = "失败",
            Reason = reason,
            SizeBytes = partial.SizeBytes,
            BeforeExtents = extents,
            AfterExtents = extents,
            BeforeExtentCount = partial.BeforeExtentCount,
            AfterExtentCount = before.ExtentCount,
            BeforeFragmentedClusters = partial.BeforeFragmentedClusters,
            AfterFragmentedClusters = before.FragmentedClusters,
            HashMode = partial.HashMode,
            ElapsedSeconds = (DateTime.UtcNow - started).TotalSeconds
        };

    /// <summary>
    /// SDDL 字符串比对前的轻量规范化：大小写与空白，并去掉 DACL 段的 AI/AR/AP 控制标志。
    /// 从父目录继承的 DACL 会带 AI（auto-inherited），与显式设置后得到的 ACE 列表是同一组权限，
    /// 若把 AI 当作差异会误判 ACL 还原失败；ACE 列表本身仍然逐个比对。
    /// </summary>
    private static string NormalizeSddl(string sddl)
    {
        var compact = string.Join(' ', sddl.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        return compact
            .Replace("D:ARAI", "D:")
            .Replace("D:AI", "D:")
            .Replace("D:AR", "D:")
            .Replace("D:AP", "D:");
    }
}
