using System.Runtime.InteropServices;

namespace AutoDefrag;

/// <summary>
/// 与旧 engine.ps1 完全相同的底层方案：
///  - GetSystemTimes 求差分得到系统 CPU 占用（与任务管理器同源，零 WMI 开销）；
///  - ntdll!NtSuspendProcess / NtResumeProcess 整体冻结 defrag.exe（引用计数语义）；
///  - OpenProcess 需要 PROCESS_SUSPEND_RESUME(0x0800) 权限。
/// </summary>
internal static class Native
{
    public const uint PROCESS_SUSPEND_RESUME = 0x0800;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("ntdll.dll")]
    public static extern int NtSuspendProcess(IntPtr hProcess);

    [DllImport("ntdll.dll")]
    public static extern int NtResumeProcess(IntPtr hProcess);

    [StructLayout(LayoutKind.Sequential)]
    public struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static bool TrySuspend(int processId, out string error)
    {
        error = string.Empty;
        var handle = OpenProcess(PROCESS_SUSPEND_RESUME, false, processId);
        if (handle == IntPtr.Zero)
        {
            error = $"OpenProcess 失败 (Win32Error={Marshal.GetLastWin32Error()})";
            return false;
        }
        try
        {
            var status = NtSuspendProcess(handle);
            if (status != 0)
            {
                error = $"NtSuspendProcess 失败, NTSTATUS=0x{status:X8}";
                return false;
            }
            return true;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static bool TryResume(int processId, out string error)
    {
        error = string.Empty;
        var handle = OpenProcess(PROCESS_SUSPEND_RESUME, false, processId);
        if (handle == IntPtr.Zero)
        {
            error = $"OpenProcess 失败 (Win32Error={Marshal.GetLastWin32Error()})";
            return false;
        }
        try
        {
            var status = NtResumeProcess(handle);
            if (status != 0)
            {
                error = $"NtResumeProcess 失败, NTSTATUS=0x{status:X8}";
                return false;
            }
            return true;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // ---------------------------------------------------------------- 卷簇大小

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetDiskFreeSpaceW(
        string lpRootPathName,
        out uint lpSectorsPerCluster,
        out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters,
        out uint lpTotalNumberOfClusters);

    /// <summary>返回卷的簇大小（字节）；失败返回 0。用于识别 &gt;4KB 的超大簇卷。</summary>
    public static int GetClusterSize(string driveLetter)
    {
        var root = driveLetter.TrimEnd('\\') + "\\";
        if (!GetDiskFreeSpaceW(root, out var sectorsPerCluster, out var bytesPerSector, out _, out _))
            return 0;

        var size = (long)sectorsPerCluster * bytesPerSector;
        return size is > 0 and <= int.MaxValue ? (int)size : 0;
    }

    // ---------------------------------------------------------------- 介质检测

    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const uint PropertyStandardQuery = 0;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public uint PropertyId;
        public uint QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceSeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;

        [MarshalAs(UnmanagedType.U1)]
        public bool IncursSeekPenalty;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        ref StoragePropertyQuery lpInBuffer,
        uint nInBufferSize,
        out DeviceSeekPenaltyDescriptor lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    /// <summary>
    /// 查询卷所在介质是否有“寻道惩罚”：机械硬盘为 true，SSD / 闪存为 false。
    /// 无法查询（权限、虚拟磁盘、驱动不支持）时返回 null，调用方按 SSD 保守处理。
    /// </summary>
    public static bool? TryGetIncursSeekPenalty(string driveLetter)
    {
        var devicePath = @"\\.\" + driveLetter.TrimEnd('\\');
        var handle = CreateFileW(devicePath, 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle == IntPtr.Zero) return null;

        try
        {
            var query = new StoragePropertyQuery
            {
                PropertyId = StorageDeviceSeekPenaltyProperty,
                QueryType = PropertyStandardQuery,
                AdditionalParameters = 0
            };

            if (!DeviceIoControl(
                    handle,
                    IoctlStorageQueryProperty,
                    ref query,
                    (uint)Marshal.SizeOf<StoragePropertyQuery>(),
                    out var descriptor,
                    (uint)Marshal.SizeOf<DeviceSeekPenaltyDescriptor>(),
                    out _,
                    IntPtr.Zero))
            {
                return null;
            }

            return descriptor.IncursSeekPenalty;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}

/// <summary>系统整体 CPU 采样器（首次调用建立基准并返回 0）。</summary>
internal sealed class SystemCpuSampler
{
    private ulong _prevIdle, _prevKernel, _prevUser;
    private bool _hasPrevious;

    public double Sample()
    {
        if (!Native.GetSystemTimes(out var idle, out var kernel, out var user))
            return 0.0;

        var i = ToUInt64(idle);
        var k = ToUInt64(kernel);
        var u = ToUInt64(user);

        if (!_hasPrevious)
        {
            _prevIdle = i;
            _prevKernel = k;
            _prevUser = u;
            _hasPrevious = true;
            return 0.0;
        }

        var dIdle = i - _prevIdle;
        var dKernel = k - _prevKernel;
        var dUser = u - _prevUser;

        _prevIdle = i;
        _prevKernel = k;
        _prevUser = u;

        var total = dKernel + dUser; // kernel 时间已包含 idle
        if (total == 0) return 0.0;

        var busy = (double)(total - dIdle) / (double)total * 100.0;
        return Math.Clamp(busy, 0.0, 100.0);
    }

    private static ulong ToUInt64(Native.FileTime ft) =>
        ((ulong)ft.High << 32) | ft.Low;
}
