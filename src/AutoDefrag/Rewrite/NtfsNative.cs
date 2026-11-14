using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AutoDefrag.Rewrite;

/// <summary>
/// NTFS 只读查询与文件元数据 P/Invoke：
///  - 卷句柄 + FSCTL_GET_NTFS_VOLUME_DATA / FSCTL_GET_VOLUME_BITMAP；
///  - 文件句柄 + FSCTL_GET_RETRIEVAL_POINTERS（解析 Runlist）；
///  - 硬链接数、备用数据流（ADS）、安全描述符（SDDL）。
/// 所有方法都不写盘；写操作在 RewriteExecutor 里通过普通文件 API 完成。
/// </summary>
internal static class NtfsNative
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileReadAttributes = 0x00000080;

    private const uint FsctlGetNtfsVolumeData = 0x00090064;
    // FSCTL_GET_VOLUME_BITMAP = CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 27, METHOD_NEITHER, FILE_ANY_ACCESS)
    // 注意是 METHOD_NEITHER（+3），常量是 0x9006F；写成 0x9006C 会一直返回 ERROR_INVALID_FUNCTION(1)。
    private const uint FsctlGetVolumeBitmap = 0x0009006F;
    private const uint FsctlGetRetrievalPointers = 0x00090073;

    private const int ErrorMoreData = 234;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorHandleEof = 38;
    private const int ErrorSharingViolation = 32;
    private const int ErrorAccessDenied = 5;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    // ---------------------------------------------------------------- 卷信息

    /// <summary>读取 NTFS 卷几何。失败时回退到 GetDiskFreeSpaceW（只缺 MFT 起点）。</summary>
    public static VolumeGeometry? TryGetGeometry(string drive)
    {
        var devicePath = @"\\.\" + drive.TrimEnd('\\');
        using var handle = CreateFileW(
            devicePath, GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

        if (!handle.IsInvalid)
        {
            var inPtr = IntPtr.Zero;
            var outPtr = Marshal.AllocHGlobal(128);
            try
            {
                if (DeviceIoControl(handle, FsctlGetNtfsVolumeData, inPtr, 0, outPtr, 128, out _, IntPtr.Zero))
                {
                    var buffer = new byte[96];
                    Marshal.Copy(outPtr, buffer, 0, buffer.Length);

                    var totalClusters = BitConverter.ToInt64(buffer, 16);
                    var freeClusters = BitConverter.ToInt64(buffer, 24);
                    var bytesPerSector = BitConverter.ToInt32(buffer, 40);
                    var bytesPerCluster = BitConverter.ToInt32(buffer, 44);
                    var mftStartLcn = BitConverter.ToInt64(buffer, 64);

                    if (bytesPerCluster > 0 && totalClusters > 0)
                    {
                        return new VolumeGeometry(
                            bytesPerSector, bytesPerCluster / Math.Max(bytesPerSector, 1), bytesPerCluster,
                            totalClusters, freeClusters, mftStartLcn);
                    }
                }
                else
                {
                    Logger.Warn($"{drive} FSCTL_GET_NTFS_VOLUME_DATA 失败: Win32Error={Marshal.GetLastWin32Error()}");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(outPtr);
            }
        }
        else
        {
            Logger.Warn($"{drive} 打开卷句柄失败: Win32Error={Marshal.GetLastWin32Error()}（稍后尝试 GetDiskFreeSpaceW）");
        }

        return FallbackGeometry(drive);
    }

    private static VolumeGeometry? FallbackGeometry(string drive)
    {
        if (!GetDiskFreeSpaceW(drive.TrimEnd('\\') + "\\",
                out var sectorsPerCluster, out var bytesPerSector, out var freeClusters, out var totalClusters))
        {
            Logger.Warn($"{drive} GetDiskFreeSpaceW 也失败: Win32Error={Marshal.GetLastWin32Error()}");
            return null;
        }

        if (sectorsPerCluster == 0 || bytesPerSector == 0) return null;
        var clusterBytes = checked((int)(sectorsPerCluster * bytesPerSector));
        return new VolumeGeometry(
            (int)bytesPerSector, (int)sectorsPerCluster, clusterBytes,
            totalClusters, freeClusters, 0);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetDiskFreeSpaceW(
        string lpRootPathName,
        out uint lpSectorsPerCluster,
        out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters,
        out uint lpTotalNumberOfClusters);

    // ---------------------------------------------------------------- 卷位图

    public sealed record VolumeBitmap(long StartingLcn, byte[] Bits, long BitmapSizeBytes);

    /// <summary>读取全卷分配位图：bit=1 已分配，bit=0 空闲。</summary>
    public static VolumeBitmap? TryGetVolumeBitmap(string drive, VolumeGeometry geometry)
    {
        var devicePath = @"\\.\" + drive.TrimEnd('\\');
        using var handle = CreateFileW(
            devicePath, GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            Logger.Warn($"{drive} 打开卷句柄读取位图失败: Win32Error={Marshal.GetLastWin32Error()}");
            return null;
        }

        var needed = (geometry.TotalClusters + 7) / 8;
        var size = (int)Math.Min(needed + 64, 512L * 1024 * 1024);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var inBuffer = new byte[8]; // StartingLcn = 0
            var outBuffer = new byte[16 + size];
            var inPin = GCHandle.Alloc(inBuffer, GCHandleType.Pinned);
            var outPin = GCHandle.Alloc(outBuffer, GCHandleType.Pinned);
            try
            {
                var ok = DeviceIoControl(
                    handle, FsctlGetVolumeBitmap,
                    inPin.AddrOfPinnedObject(), (uint)inBuffer.Length,
                    outPin.AddrOfPinnedObject(), (uint)outBuffer.Length,
                    out var returnedBytes, IntPtr.Zero);

                var error = Marshal.GetLastWin32Error();
                if (!ok)
                {
                    if (error is ErrorMoreData or ErrorInsufficientBuffer)
                    {
                        // 缓冲区不够时按头字段提示扩容：该字段在不同版本上可能是位图字节数或覆盖簇数，
                        // 这里只在“看起来像簇数”时才除以 8，避免把 24M 簇当成 24MB 字节数。
                        var hint = BitConverter.ToInt64(outBuffer, 8);
                        var wanted = hint > 0
                            ? (hint <= geometry.TotalClusters * 2L ? (hint + 7) / 8 : hint)
                            : (long)size * 2;
                        var next = Math.Min(wanted + 64, 512L * 1024 * 1024);
                        if (next > size)
                        {
                            size = (int)next;
                            continue;
                        }
                    }

                    // 非管理员/受保护进程下 Windows 对卷位图请求返回 1（ERROR_INVALID_FUNCTION），而不是 5；
                    // 此时空闲布局视为“未知”，计划仍然只依据文件区段 + 卷几何，不受影响。
                    Logger.Warn($"{drive} 读取卷位图失败: Win32Error={error}（{DescribeBitmapError(error)}；" +
                                $"当前进程管理员={IsElevatedNow()}），本次按“空闲布局未知”处理");
                    return null;
                }

                // 以实际写回的字节数为准：16 字节头（StartingLcn + BitmapSize）+ 位图数据。
                // 旧实现把头部第 8 字节起的字段直接当字节数，在实测中拿到的是簇数（8174），
                // 触发“位图大小异常”而永远读不到布局。
                var payload = (int)returnedBytes - 16;
                if (payload <= 0 || payload > outBuffer.Length - 16)
                {
                    Logger.Warn($"{drive} 位图返回长度异常: {returnedBytes} 字节");
                    return null;
                }

                var bits = new byte[payload];
                Array.Copy(outBuffer, 16, bits, 0, payload);
                return new VolumeBitmap(BitConverter.ToInt64(outBuffer, 0), bits, payload);
            }
            finally
            {
                inPin.Free();
                outPin.Free();
            }
        }

        Logger.Warn($"{drive} 位图读取重试次数用完");
        return null;
    }

    private static string DescribeBitmapError(int error) => error switch
    {
        1 => "ERROR_INVALID_FUNCTION：系统拒绝读取卷位图（非管理员或受保护进程）",
        5 => "ERROR_ACCESS_DENIED：访问被拒绝",
        87 => "ERROR_INVALID_PARAMETER：参数不正确",
        234 => "ERROR_MORE_DATA：缓冲区不足（会自动重试）",
        _ => "未知错误"
    };

    /// <summary>当前进程是否以管理员身份运行；用于区分位图失败原因（不影响功能，只用于日志）。</summary>
    public static bool IsElevatedNow()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- Runlist

    /// <summary>读取文件的物理区段（FSCTL_GET_RETRIEVAL_POINTERS）。</summary>
    public static bool TryGetExtents(string path, out List<Extent> extents, out string error)
    {
        extents = new List<Extent>();
        error = string.Empty;

        using var handle = CreateFileW(
            path, FileReadAttributes, FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var code = Marshal.GetLastWin32Error();
            error = DescribeOpenError(code);
            return false;
        }

        var size = 64 * 1024;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var inBuffer = new byte[8];
            var outBuffer = new byte[size];
            var inPin = GCHandle.Alloc(inBuffer, GCHandleType.Pinned);
            var outPin = GCHandle.Alloc(outBuffer, GCHandleType.Pinned);
            try
            {
                var ok = DeviceIoControl(
                    handle, FsctlGetRetrievalPointers,
                    inPin.AddrOfPinnedObject(), (uint)inBuffer.Length,
                    outPin.AddrOfPinnedObject(), (uint)outBuffer.Length,
                    out var returned, IntPtr.Zero);

                var code = Marshal.GetLastWin32Error();
                if (!ok)
                {
                    if (code == ErrorHandleEof)
                    {
                        return true; // 0 字节文件
                    }

                    if (code is ErrorMoreData or ErrorInsufficientBuffer)
                    {
                        size *= 2;
                        if (size > 16 * 1024 * 1024)
                        {
                            error = "Runlist 超过 16MB，疑似损坏或极度碎片化";
                            return false;
                        }
                        continue;
                    }

                    error = $"FSCTL_GET_RETRIEVAL_POINTERS 失败 Win32Error={code}";
                    return false;
                }

                var available = (int)Math.Min(returned, (uint)outBuffer.Length);
                if (!TryParseRetrievalPointers(outBuffer, available, extents, out error))
                {
                    return false;
                }
                return true;
            }
            finally
            {
                inPin.Free();
                outPin.Free();
            }
        }

        error = "Runlist 读取重试次数用完";
        return false;
    }

    private static bool TryParseRetrievalPointers(byte[] buffer, int length, List<Extent> extents, out string error)
    {
        error = string.Empty;
        if (length < 16)
        {
            error = "Runlist 返回长度不足";
            return false;
        }

        // 实际结构（winioctl.h）：
        //   DWORD ExtentCount; LARGE_INTEGER StartingVcn; struct { LARGE_INTEGER NextVcn; LARGE_INTEGER Lcn; } Extents[];
        // DWORD 后有 4 字节对齐填充，所以 StartingVcn 在偏移 8，Extents 从偏移 16 开始。
        var count = BitConverter.ToInt32(buffer, 0);
        var startingVcn = BitConverter.ToInt64(buffer, 8);
        if (count < 0 || count > 1_000_000)
        {
            error = $"Runlist 区段数量异常: {count}";
            return false;
        }

        if (count == 0)
        {
            return true;
        }

        if (16L + (long)count * 16 > length)
        {
            error = $"Runlist 长度不匹配: count={count}, length={length}";
            return false;
        }

        var previousNext = startingVcn;
        for (var i = 0; i < count; i++)
        {
            var nextVcn = BitConverter.ToInt64(buffer, 16 + i * 16);
            var lcn = BitConverter.ToInt64(buffer, 24 + i * 16);
            var lengthClusters = nextVcn - previousNext;
            if (lengthClusters < 0)
            {
                error = $"Runlist VCN 递减（第 {i} 段: {previousNext} -> {nextVcn}）";
                return false;
            }

            extents.Add(new Extent(previousNext, lcn, lengthClusters));
            previousNext = nextVcn;
        }

        return true;
    }

    private static string DescribeOpenError(int code) => code switch
    {
        ErrorSharingViolation => "文件正在使用（共享冲突）",
        ErrorAccessDenied => "没有访问权限",
        2 => "文件不存在",
        _ => $"打开文件失败 Win32Error={code}"
    };

    /// <summary>由 Runlist 计算碎片指标。</summary>
    public static FileLayout BuildLayout(string path, long sizeBytes, IReadOnlyList<Extent> extents)
    {
        long total = 0;
        long largest = 0;
        var hasSparse = false;

        foreach (var extent in extents)
        {
            if (extent.IsSparse)
            {
                hasSparse = true;
                continue;
            }

            total += extent.LengthClusters;
            if (extent.LengthClusters > largest) largest = extent.LengthClusters;
        }

        var extentCount = extents.Count(e => !e.IsSparse && e.LengthClusters > 0);
        var fragmented = Math.Max(0, total - largest);
        return new FileLayout(path, sizeBytes, extentCount, total, largest, fragmented, hasSparse);
    }

    public static string ExtentsText(IReadOnlyList<Extent> extents)
    {
        if (extents.Count == 0) return "(空)";
        return string.Join(", ", extents.Where(e => e.LengthClusters > 0).Take(12).Select(e => e.Text));
    }

    // ---------------------------------------------------------------- 文件属性

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation info);

    /// <summary>硬链接数量；失败返回 0（调用方按“未知”保守处理）。</summary>
    public static uint TryGetLinkCount(string path)
    {
        using var handle = CreateFileW(
            path, FileReadAttributes, FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) return 0;

        return GetFileInformationByHandle(handle, out var info) ? info.NumberOfLinks : 0;
    }

    // ---------------------------------------------------------------- ADS

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindStreamData
    {
        public long StreamSize;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        public string StreamName;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindFirstStreamW(
        string lpFileName, int infoLevel, out Win32FindStreamData lpFindStreamData, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool FindNextStreamW(IntPtr hFindStream, out Win32FindStreamData lpFindStreamData);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FindClose(IntPtr hFindFile);

    private static readonly IntPtr InvalidHandleValue = new(-1);

    /// <summary>返回命名流（不含 ":$DATA"）；默认流不返回。</summary>
    public static List<AdsStream> TryGetNamedStreams(string path)
    {
        var result = new List<AdsStream>();
        var handle = FindFirstStreamW(path, 0, out var data, 0);
        if (handle == InvalidHandleValue) return result;

        try
        {
            while (true)
            {
                var name = data.StreamName ?? string.Empty;
                if (name.EndsWith(":$DATA", StringComparison.OrdinalIgnoreCase))
                    name = name[..^6];
                if (name.StartsWith(':')) name = name[1..];
                if (name.Length > 0) result.Add(new AdsStream(name, data.StreamSize));

                if (!FindNextStreamW(handle, out data)) break;
            }
        }
        finally
        {
            FindClose(handle);
        }

        return result;
    }

    // ---------------------------------------------------------------- 安全描述符

    private const int SeFileObject = 1;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint GroupSecurityInformation = 0x00000002;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint SecurityInformationAll = 0x00000007;
    private const uint SddlRevision1 = 1;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetNamedSecurityInfoW(
        string pObjectName, int objectType, uint securityInfo,
        out IntPtr ppsidOwner, out IntPtr ppsidGroup, out IntPtr ppDacl, out IntPtr ppSacl,
        out IntPtr ppSecurityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint SetNamedSecurityInfoW(
        string pObjectName, int objectType, uint securityInfo,
        IntPtr psidOwner, IntPtr psidGroup, IntPtr pDacl, IntPtr pSacl);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(
        IntPtr pSecurityDescriptor, uint requestedStringSDRevision, uint securityInformation,
        out IntPtr stringSecurityDescriptor, out uint stringSecurityDescriptorLen);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string stringSecurityDescriptor, uint stringSDRevision,
        out IntPtr securityDescriptor, out uint securityDescriptorSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetSecurityDescriptorOwner(
        IntPtr pSecurityDescriptor, out IntPtr pOwner, out bool bOwnerDefaulted);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetSecurityDescriptorGroup(
        IntPtr pSecurityDescriptor, out IntPtr pGroup, out bool bGroupDefaulted);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetSecurityDescriptorDacl(
        IntPtr pSecurityDescriptor, out bool bDaclPresent, out IntPtr pDacl, out bool bDaclDefaulted);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool EqualSid(IntPtr pSid1, IntPtr pSid2);

    /// <summary>读取 Owner + Group + DACL 的 SDDL 文本；失败返回 null。</summary>
    public static string? TryGetSddl(string path)
    {
        var rc = GetNamedSecurityInfoW(
            path, SeFileObject, SecurityInformationAll,
            out _, out _, out _, out _, out var sd);
        if (rc != 0)
        {
            Logger.Warn($"读取安全描述符失败({path}): rc={rc}");
            return null;
        }

        try
        {
            if (!ConvertSecurityDescriptorToStringSecurityDescriptorW(
                    sd, SddlRevision1, SecurityInformationAll, out var textPtr, out _))
            {
                Logger.Warn($"安全描述符转 SDDL 失败({path}): Win32Error={Marshal.GetLastWin32Error()}");
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(textPtr);
            }
            finally
            {
                LocalFree(textPtr);
            }
        }
        finally
        {
            LocalFree(sd);
        }
    }

    /// <summary>
    /// 把 SDDL 应用到目标文件。Owner/Group 与目标当前值相同时不重复设置：
    /// 修改属主需要 WRITE_OWNER（通常只有管理员有），而对所有者自己来说改 DACL 是隐含允许的，
    /// 这样普通用户在数据卷上也能完成 ACL 还原。
    /// </summary>
    public static bool TrySetSddl(string path, string sddl, out string error)
    {
        error = string.Empty;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl, SddlRevision1, out var sd, out _))
        {
            error = $"SDDL 解析失败 Win32Error={Marshal.GetLastWin32Error()}";
            return false;
        }

        try
        {
            GetSecurityDescriptorOwner(sd, out var owner, out _);
            GetSecurityDescriptorGroup(sd, out var group, out _);
            GetSecurityDescriptorDacl(sd, out var daclPresent, out var dacl, out _);

            var info = DaclSecurityInformation;
            var currentRc = GetNamedSecurityInfoW(
                path, SeFileObject, SecurityInformationAll, out var currentOwner, out var currentGroup,
                out _, out _, out var currentSd);

            if (currentRc == 0)
            {
                try
                {
                    if (owner != IntPtr.Zero && currentOwner != IntPtr.Zero && !EqualSid(owner, currentOwner))
                        info |= OwnerSecurityInformation;
                    if (group != IntPtr.Zero && currentGroup != IntPtr.Zero && !EqualSid(group, currentGroup))
                        info |= GroupSecurityInformation;
                }
                finally
                {
                    LocalFree(currentSd);
                }
            }
            else
            {
                // 读不到当前属主时保守处理：只改 DACL，避免普通用户因 WRITE_OWNER 被拒
                Logger.Warn($"读取当前属主失败({path}): rc={currentRc}，本次只还原 DACL");
            }

            var rc = SetNamedSecurityInfoW(
                path, SeFileObject, info,
                (info & OwnerSecurityInformation) != 0 ? owner : IntPtr.Zero,
                (info & GroupSecurityInformation) != 0 ? group : IntPtr.Zero,
                daclPresent ? dacl : IntPtr.Zero, IntPtr.Zero);

            if (rc != 0)
            {
                error = $"SetNamedSecurityInfo 失败 rc={rc}";
                return false;
            }

            return true;
        }
        finally
        {
            LocalFree(sd);
        }
    }

    /// <summary>用于日志：把 Win32 错误转成简短文本。</summary>
    public static string DescribeWin32(int code) => new Win32Exception(code).Message;
}
