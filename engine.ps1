<#
=====================================================================
 engine.ps1 — AutoDefrag Tray v2.0  执行引擎
=====================================================================
 运行环境 : Windows PowerShell 5.1
 依赖     : 无第三方模块 (纯 Win32/NT API)

 职责:
   1. 接收 app.ps1 传入的动作、目标磁盘与 CPU 阈值;
   2. Action=Clean  时真实调用系统 defrag.exe 执行 "/O /H":
         - 运行期间实时监控系统 CPU, 连续超阈值 HighSeconds 秒 -> NtSuspendProcess 挂起;
         - CPU 回落并稳定 LowSeconds 秒 -> NtResumeProcess 恢复;
         - 整理前后各探测一次碎片率, 记录"碎片率变化";
   3. Action=Status 时只读探测(不整理)各磁盘的碎片率与剩余空间;
   4. 结束后写日志(Logs\engine.log)、结果文件(Data\last_result.txt 或 Data\drive_status.json),
      Clean 跑完还会写 Data\last_run.txt(今日日期, 作为"每日一次"锁),
      并调用 notify.ps1 发"清理完成"原生通知(停留时长读 config.json 的 NotificationTimeout),
      最后用进程退出码返回执行结果。

 参数:
   -Action        动作。Clean=整理(默认); Status=只读探测碎片率/剩余空间
   -TargetDrive   目标盘符。支持单个 "C:", 也支持多个 "C:,D:"
                  (PowerShell 会把它解析成字符串数组, 引擎按顺序串行处理)
                  Clean 时必填; Status 时可省略(省略则探测所有本地固定磁盘)
   -CpuThreshold  CPU 触发阈值(%), 默认 70
   -HighSeconds   连续超阈值多少秒判定"高负载", 默认 10
   -LowSeconds    CPU 连续低于阈值多少秒判定"已回落", 默认 10

 注意:
   defrag.exe 需要管理员权限。非管理员运行会以非 0 退出码快速失败,
   具体原因会记录在 Logs\engine.log 中。

 用法示例:
   powershell -NoProfile -ExecutionPolicy Bypass -File .\engine.ps1 -Action Clean  -TargetDrive C: -CpuThreshold 70
   powershell -NoProfile -ExecutionPolicy Bypass -File .\engine.ps1 -Action Clean  -TargetDrive C:,D: -CpuThreshold 70
   powershell -NoProfile -ExecutionPolicy Bypass -File .\engine.ps1 -Action Status
=====================================================================
#>

#Requires -Version 5.1

[CmdletBinding()]
param(
    # 动作: Clean=整理磁盘; Status=仅探测磁盘碎片率/剩余空间(只读, 不写磁盘)
    [ValidateSet('Clean', 'Status')]
    [string]$Action = 'Clean',

    # Clean 时必填; Status 时可省略(省略则探测所有本地固定磁盘)
    [string[]]$TargetDrive,

    [int]$CpuThreshold = 70,

    # 按需求"写死 10 秒", 但保留为参数便于按机器性能微调与测试
    [int]$HighSeconds = 10,
    [int]$LowSeconds  = 10
)

#region 0. 路径与常量
$script:Root       = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$script:DataDir    = Join-Path $script:Root 'Data'
$script:LogDir     = Join-Path $script:Root 'Logs'
$script:LogFile    = Join-Path $script:LogDir  'engine.log'
$script:ResultFile = Join-Path $script:DataDir 'last_result.txt'
$script:StatusFile = Join-Path $script:DataDir 'drive_status.json'   # Status 动作的产物
$script:LastRunFile = Join-Path $script:DataDir 'last_run.txt'       # Clean 完成日期(yyyy-MM-dd)

$script:Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
#endregion

#region 1. 原生 API 封装 (来自第一步 PoC 的验证结论)
<#
 API 原理回顾:
  - defrag.exe 内部是多线程的, 要整体"冻结"它, 只能挂起它所有线程。
  - kernel32 没有公开的 SuspendProcess; 采用 ntdll!NtSuspendProcess /
    NtResumeProcess, 它们会给目标进程每个线程的 SuspendCount +1, 计数 > 0
    的线程不会被调度器选中。
  - 该操作是【引用计数】语义: N 次 Suspend 必须 N 次 Resume 才完全恢复。
  - 需要先 OpenProcess(PROCESS_SUSPEND_RESUME = 0x0800) 拿到句柄, 用完关掉。
  - CPU 占用率用 kernel32!GetSystemTimes 求差分, 微秒级开销、零 WMI 查询。
#>
$nativeSource = @'
using System;
using System.Runtime.InteropServices;

public static class Native
{
    // PROCESS_SUSPEND_RESUME = 0x0800: 挂起/恢复进程所需的最小权限
    public const uint PROCESS_SUSPEND_RESUME = 0x0800;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    // ntdll 未文档化服务, 返回 NTSTATUS (0 == STATUS_SUCCESS)
    [DllImport("ntdll.dll")]
    public static extern int NtSuspendProcess(IntPtr hProcess);

    [DllImport("ntdll.dll")]
    public static extern int NtResumeProcess(IntPtr hProcess);

    // ---------------- 系统 CPU 采样 ----------------
    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    private static ulong ToUInt64(FILETIME ft)
    { return ((ulong)ft.dwHighDateTime << 32) | (ulong)ft.dwLowDateTime; }

    private static ulong _prevIdle, _prevKernel, _prevUser;
    private static bool   _hasPrev = false;
    private static readonly object _lock = new object();

    /// <summary>返回自上次调用以来系统整体的 CPU 繁忙百分比 (0-100)。首次调用返回 0 用于建立基准。</summary>
    public static double GetSystemCpuPercent()
    {
        lock (_lock)
        {
            FILETIME idle, kernel, user;
            if (!GetSystemTimes(out idle, out kernel, out user))
                return 0.0;

            ulong i = ToUInt64(idle), k = ToUInt64(kernel), u = ToUInt64(user);

            if (!_hasPrev)
            {
                _prevIdle = i; _prevKernel = k; _prevUser = u;
                _hasPrev = true;
                return 0.0;
            }

            ulong dIdle = i - _prevIdle, dKernel = k - _prevKernel, dUser = u - _prevUser;
            _prevIdle = i; _prevKernel = k; _prevUser = u;

            ulong total = dKernel + dUser;              // kernel 时间里已包含 idle
            if (total == 0) return 0.0;

            double busy = (double)(total - dIdle) / (double)total * 100.0;
            if (busy < 0.0)   busy = 0.0;
            if (busy > 100.0) busy = 100.0;
            return busy;
        }
    }
}
'@

# 幂等加载: 同一会话内重复调用不会因"类型已存在"而报错
if (-not ('Native' -as [type])) {
    Add-Type -TypeDefinition $nativeSource -Language CSharp
}
#endregion

#region 2. 日志

function Initialize-EngineEnvironment {
    foreach ($dir in @($script:DataDir, $script:LogDir)) {
        if (-not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
    }
}

function Write-EngineLog {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [ValidateSet('INFO', 'WARN', 'ERROR')][string]$Level = 'INFO'
    )
    $line = '[{0}] [{1}] [engine] {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Level, $Message
    try {
        [IO.File]::AppendAllText($script:LogFile, $line + [Environment]::NewLine, $script:Utf8NoBom)
    }
    catch { }
}

function Write-EngineResult {
    param(
        [Parameter(Mandatory = $true)][string]$Targets,
        [Parameter(Mandatory = $true)][string]$Summary,
        [Parameter(Mandatory = $true)][int]$OkCount,
        [Parameter(Mandatory = $true)][int]$Total
    )
    # 供 app.ps1 / 后续"查看磁盘状态"读取的最近一次执行结果
    $line = '[{0}] 目标={1} 成功={2}/{3} 明细={4}' -f `
            (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Targets, $OkCount, $Total, $Summary
    try {
        [IO.File]::WriteAllText($script:ResultFile, $line + [Environment]::NewLine, $script:Utf8NoBom)
    }
    catch { }
}

function Write-LastRunDate {
    <#
      Clean 动作【彻底跑完后】才写入 today 的日期(yyyy-MM-dd), 作为"每日只执行一次"的锁。
      注意: 只在 Clean 里调用, Status(只读探测)不写, 否则"查看磁盘状态"会误锁当日任务。
    #>
    try {
        [IO.File]::WriteAllText($script:LastRunFile,
                                (Get-Date -Format 'yyyy-MM-dd') + [Environment]::NewLine,
                                $script:Utf8NoBom)
        Write-EngineLog ('已写入 last_run.txt = {0}  (每日一次锁生效)' -f (Get-Date -Format 'yyyy-MM-dd'))
    }
    catch {
        Write-EngineLog ('写 last_run.txt 失败: ' + $_.Exception.Message) -Level WARN
    }
}

function Get-NotificationTimeout {
    <#
      从 Data\config.json 读取"通知停留时长"(秒), 供调用 notify.ps1 时传入。
      任何缺失/损坏/越界都回退 30。engine 不依赖 app 传参, 自己读配置即可。
    #>
    $cfgFile = Join-Path $script:DataDir 'config.json'
    try {
        if (Test-Path -LiteralPath $cfgFile) {
            $json = ([IO.File]::ReadAllText($cfgFile)) | ConvertFrom-Json
            if ($null -ne $json.NotificationTimeout) {
                $v = [int]$json.NotificationTimeout
                if ($v -ge 10 -and $v -le 120) { return $v }
            }
        }
    }
    catch { }
    return 30
}

function Send-Notification {
    <#
      调用 notify.ps1 弹出系统原生 Toast 通知。
      - notify.ps1 会用 WinRT 把通知交给系统(WpnService)持有, 自身立刻退出, 不占资源;
      - 停留时长由 config.json 的 NotificationTimeout 决定。
      只在 Clean 全部跑完后调用(这正是"defrag 彻底结束"的时刻)。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [string]$Title = 'AutoDefrag',
        [ValidateSet('Info', 'Warning', 'Error')][string]$Level = 'Info'
    )
    $notifyPath = Join-Path $script:Root 'notify.ps1'
    if (-not (Test-Path -LiteralPath $notifyPath)) {
        Write-EngineLog ('未找到 notify.ps1, 跳过通知') -Level WARN
        return
    }
    $timeout = Get-NotificationTimeout
    try {
        Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $notifyPath,
            '-Title', $Title, '-Message', $Message, '-Level', $Level,
            '-TimeoutSeconds', "$timeout"
        ) | Out-Null
        Write-EngineLog ('已发出通知(停留 {0}s): {1}' -f $timeout, $Message)
    }
    catch {
        Write-EngineLog ('发送通知失败: ' + $_.Exception.Message) -Level WARN
    }
}
#endregion

#region 3. 进程挂起 / 恢复 封装

function Suspend-ExternalProcess {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process)

    $handle = [Native]::OpenProcess([Native]::PROCESS_SUSPEND_RESUME, $false, $Process.Id)
    if ($handle -eq [IntPtr]::Zero) {
        $code = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        throw ('OpenProcess 失败 (PID={0}, Win32Error={1})' -f $Process.Id, $code)
    }
    try {
        $status = [Native]::NtSuspendProcess($handle)
        if ($status -ne 0) { throw ('NtSuspendProcess 失败, NTSTATUS=0x{0:X8}' -f $status) }
    }
    finally {
        # 挂起状态保存在内核线程对象上, 关闭句柄不会解除挂起
        [void][Native]::CloseHandle($handle)
    }
}

function Resume-ExternalProcess {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process)

    $handle = [Native]::OpenProcess([Native]::PROCESS_SUSPEND_RESUME, $false, $Process.Id)
    if ($handle -eq [IntPtr]::Zero) {
        $code = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        throw ('OpenProcess 失败 (PID={0}, Win32Error={1})' -f $Process.Id, $code)
    }
    try {
        $status = [Native]::NtResumeProcess($handle)
        if ($status -ne 0) { throw ('NtResumeProcess 失败, NTSTATUS=0x{0:X8}' -f $status) }
    }
    finally {
        [void][Native]::CloseHandle($handle)
    }
}
#endregion

#region 4. 单个磁盘的"整理 + CPU 守护"闭环

function Invoke-DefragWithCpuGuard {
    <#
      .SYNOPSIS
        启动 defrag.exe 整理指定盘符, 期间按 CPU 占用自动挂起/恢复, 直到其退出。
      .OUTPUTS
        PSCustomObject: Drive / ExitCode / Suspended / Resumed / StartTime / EndTime / Status
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Drive,
        [Parameter(Mandatory = $true)][int]$CpuThreshold,
        [Parameter(Mandatory = $true)][int]$HighSeconds,
        [Parameter(Mandatory = $true)][int]$LowSeconds
    )

    $startTime  = Get-Date
    $suspCount  = 0
    $resCount   = 0
    $result     = $null

    Write-EngineLog ('开始整理 {0}   参数: /O /H   CPU阈值={1}%' -f $Drive, $CpuThreshold)

    # ---- 4.1 启动真实 defrag.exe ----
    # 等价于: Start-Process "defrag.exe" -ArgumentList "$Drive /O /H" -PassThru -WindowStyle Hidden
    $proc = $null
    try {
        $proc = Start-Process -FilePath 'defrag.exe' `
                              -ArgumentList @($Drive, '/O', '/H') `
                              -PassThru -WindowStyle Hidden
    }
    catch {
        Write-EngineLog ('启动 defrag.exe 失败: ' + $_.Exception.Message) -Level ERROR
        return [pscustomobject]@{
            Drive = $Drive; ExitCode = $null; Suspended = 0; Resumed = 0
            StartTime = $startTime; EndTime = (Get-Date); Status = 'LaunchFailed'
        }
    }
    Write-EngineLog ('defrag.exe 已启动, PID={0}' -f $proc.Id)

    # ---- 4.2 CPU 监控与挂起/恢复循环 ----
    [void][Native]::GetSystemCpuPercent()   # 预热: 建立首个采样基准
    Start-Sleep -Milliseconds 1000

    $state     = 'Running'   # Running / Suspended
    $highCount = 0           # 连续高于阈值的秒数
    $lowCount  = 0           # 连续低于阈值的秒数

    while (-not $proc.HasExited) {
        Start-Sleep -Seconds 1
        $cpu = [Native]::GetSystemCpuPercent()

        if ($state -eq 'Running') {
            if ($cpu -gt $CpuThreshold) {
                $highCount++
                if ($highCount -ge $HighSeconds) {
                    Suspend-ExternalProcess -Process $proc
                    $suspCount++
                    $state = 'Suspended'; $highCount = 0; $lowCount = 0
                    Write-EngineLog ('CPU 连续 > {0}% 达 {1}s  ->  挂起 defrag (PID={2}, 当前CPU={3:N1}%)' -f `
                                     $CpuThreshold, $HighSeconds, $proc.Id, $cpu) -Level WARN
                }
            }
            else { $highCount = 0 }
        }
        else {
            # 已挂起: 等 CPU 回落
            if ($cpu -le $CpuThreshold) {
                $lowCount++
                if ($lowCount -ge $LowSeconds) {
                    Resume-ExternalProcess -Process $proc
                    $resCount++
                    $state = 'Running'; $highCount = 0; $lowCount = 0
                    Write-EngineLog ('CPU 回落并稳定 {0}s  ->  恢复 defrag (PID={1}, 当前CPU={2:N1}%)' -f `
                                     $LowSeconds, $proc.Id, $cpu)
                }
            }
            else { $lowCount = 0 }
        }
    }

    # ---- 4.3 结束清理 ----
    # 防御性: 若进程在挂起态下被外部结束(极罕见), 先恢复再收尾
    if ($state -eq 'Suspended') {
        try { Resume-ExternalProcess -Process $proc } catch { }
    }

    $proc.WaitForExit()                      # 已退出, 立即返回, 同时确保 ExitCode 可用
    $exitCode = $proc.ExitCode
    $status   = if ($exitCode -eq 0) { 'OK' } else { 'Failed' }
    $endTime  = Get-Date

    Write-EngineLog ('{0} 整理结束: exit={1}, 挂起={2}次, 恢复={3}次, 用时={4:N0}s' -f `
                     $Drive, $exitCode, $suspCount, $resCount, ($endTime - $startTime).TotalSeconds)

    $result = [pscustomobject]@{
        Drive     = $Drive
        ExitCode  = $exitCode
        Suspended = $suspCount
        Resumed   = $resCount
        StartTime = $startTime
        EndTime   = $endTime
        Status    = $status
    }
    return $result
}
#endregion

#region 5. 磁盘状态探测(碎片率 / 剩余空间)

function Get-FixedDrives {
    <#
      取本机"本地固定磁盘"盘符列表(不含 U 盘/光驱/网络盘)。
      优先 Win32_LogicalDisk DriveType=3; 失败则退化为 Get-PSDrive。
    #>
    $list = @()
    try {
        $disks = Get-CimInstance -ClassName Win32_LogicalDisk -Filter 'DriveType=3' -ErrorAction Stop
        foreach ($d in $disks) { $list += [string]$d.DeviceID }   # 形如 "C:"
    }
    catch {
        $list = @(Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue |
                  Where-Object { $_.Root } |
                  ForEach-Object { ([string]$_.Root).TrimEnd('\') })
    }
    return @($list | Where-Object { $_ })
}

function Get-DefragAnalyzeReport {
    <#
      运行只读的 defrag.exe <Drive> /A (分析, 不整理), 返回其控制台输出文本。
      用重定向到临时文件的方式抓取输出, 避免依赖当前控制台编码。
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Drive)

    $tmp = Join-Path $env:TEMP ('autodefrag_probe_{0}.txt' -f [guid]::NewGuid().ToString('N'))
    try {
        Start-Process -FilePath 'defrag.exe' -ArgumentList @($Drive, '/A') `
                      -Wait -PassThru -WindowStyle Hidden `
                      -RedirectStandardOutput $tmp -ErrorAction Stop | Out-Null
        if (Test-Path -LiteralPath $tmp) {
            return [IO.File]::ReadAllText($tmp)
        }
    }
    catch {
        Write-EngineLog ('defrag /A 执行失败({0}): {1}' -f $Drive, $_.Exception.Message) -Level WARN
    }
    finally {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    }
    return ''
}

function Get-FragPercent {
    <#
      从 defrag 报告里解析"碎片率(%)"。报告段每行形如  Key = Value。
      兼容两种情况:
        - "Total fragmented space = 3%"         -> 直接取百分数
        - "Total fragmented space = 1.2 GB"     -> 按 容量 / 卷大小 * 100 换算
      若系统语言非英文(键名本地化), 退化为"报告中任意 NN% 取值"。
      解析不出一律返回 $null, 由上层显示为 "--"。
    #>
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Report,
        [double]$VolumeSizeBytes = 0
    )

    if ([string]::IsNullOrWhiteSpace($Report)) { return $null }

    $anyPercent = $null   # 兜底: 报告里第一个 "= NN%" 形式的值
    foreach ($rawLine in ($Report -split "`r?`n")) {
        $line = $rawLine.Trim()
        if (-not $line) { continue }
        $eq = $line.IndexOf('=')
        if ($eq -lt 0) { continue }

        $key = $line.Substring(0, $eq).Trim()
        $val = $line.Substring($eq + 1).Trim()
        if (-not $val) { continue }

        $isFragKey = ($key -match 'fragmented\s+space')

        if ($val -match '^([\d\.,]+)\s*%$') {
            $num = [double](($matches[1]) -replace ',', '')
            if ($isFragKey) { return $num }         # 精确命中, 直接返回
            if ($null -eq $anyPercent) { $anyPercent = $num }
            continue
        }

        if ($isFragKey -and $VolumeSizeBytes -gt 0 -and $val -match '^([\d\.,]+)\s*(TB|GB|MB|KB|B)$') {
            $unit = $matches[2]
            $mult = switch ($unit) {
                'TB'    { 1099511627776 }
                'GB'    { 1073741824 }
                'MB'    { 1048576 }
                'KB'    { 1024 }
                default { 1 }
            }
            $bytes = [double](($matches[1]) -replace ',', '') * $mult
            return [math]::Round($bytes / $VolumeSizeBytes * 100, 2)
        }
    }
    return $anyPercent
}

function Get-DriveStatus {
    <#
      探测单个磁盘: 容量 / 剩余空间 / 碎片率。
      容量与剩余空间走 Win32_LogicalDisk(与系统语言无关, 最可靠);
      碎片率走 defrag /A 报告解析。
      返回 PSCustomObject: Drive / VolumeSizeGB / FreeSpaceGB / FragPercent / Probed
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Drive)

    $sizeBytes = 0
    $sizeGB    = $null
    $freeGB    = $null
    $frag      = $null

    try {
        $disk = Get-CimInstance -ClassName Win32_LogicalDisk -Filter ("DeviceID='{0}'" -f $Drive) -ErrorAction Stop
        if ($disk) {
            if ($disk.Size)                { $sizeBytes = [double]$disk.Size; $sizeGB = [math]::Round($sizeBytes / 1GB, 2) }
            if ($null -ne $disk.FreeSpace) { $freeGB = [math]::Round([double]$disk.FreeSpace / 1GB, 2) }
        }
    }
    catch { }

    try {
        $report = Get-DefragAnalyzeReport -Drive $Drive
        $frag   = Get-FragPercent -Report $report -VolumeSizeBytes $sizeBytes
    }
    catch {
        Write-EngineLog ('探测 {0} 碎片率失败: {1}' -f $Drive, $_.Exception.Message) -Level WARN
    }

    return [pscustomobject]@{
        Drive        = $Drive
        VolumeSizeGB = $sizeGB
        FreeSpaceGB  = $freeGB
        FragPercent  = $frag            # $null => 探测失败(前端显示 "--")
        Probed       = ($null -ne $frag)
    }
}

function Write-DriveStatusFile {
    # 把探测结果写成 JSON, 供 app.ps1"查看磁盘状态"读取
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][object[]]$Statuses)

    $payload = [ordered]@{
        ProbeTime = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
        Drives    = @($Statuses)
    }
    try {
        $json = $payload | ConvertTo-Json -Depth 4
        [IO.File]::WriteAllText($script:StatusFile, $json, $script:Utf8NoBom)
    }
    catch {
        Write-EngineLog ('写 drive_status.json 失败: ' + $_.Exception.Message) -Level ERROR
    }
}

function Format-FragValue {
    # 把可能为 $null 的碎片率格式化成展示用字符串
    param($Value)
    if ($null -eq $Value) { return '--' }
    return ('{0}%' -f $Value)
}
#endregion

#region 6. 主流程

Initialize-EngineEnvironment

# 6.1 规范化盘符: 去掉首尾空白与末尾反斜杠 (C:\ -> C:)
$drives = @()
foreach ($entry in $TargetDrive) {
    foreach ($part in ([string]$entry -split '[,;\s]+')) {
        $t = $part.Trim().TrimEnd('\')
        if ($t) { $drives += $t }
    }
}

# =====================================================================
# 6.2 动作分发
# =====================================================================

if ($Action -eq 'Status') {
    # ---------- 只读探测: 碎片率 + 剩余空间 ----------
    # 未指定盘符时探测本机所有本地固定磁盘
    if ($drives.Count -eq 0) { $drives = @(Get-FixedDrives) }

    if ($drives.Count -eq 0) {
        Write-EngineLog '未发现可探测的磁盘, 引擎退出' -Level ERROR
        exit 2
    }

    Write-EngineLog ('================ 引擎启动(状态探测): 目标={0} ================' -f ($drives -join ','))

    $statuses = @()
    foreach ($drive in $drives) {
        $statuses += Get-DriveStatus -Drive $drive
    }

    Write-DriveStatusFile -Statuses $statuses

    $summary = ($statuses | ForEach-Object {
                   '{0}(碎片={1},剩余={2}GB/共{3}GB)' -f `
                       $_.Drive,
                       (Format-FragValue $_.FragPercent),
                       $(if ($null -ne $_.FreeSpaceGB)   { $_.FreeSpaceGB }   else { '--' }),
                       $(if ($null -ne $_.VolumeSizeGB) { $_.VolumeSizeGB } else { '--' })
               }) -join '; '

    Write-EngineLog ('状态探测完成: {0}' -f $summary)
    # 同时写一份 last_result.txt, 便于 app 用同一套逻辑展示
    Write-EngineResult -Targets ($drives -join ',') -Summary $summary `
                       -OkCount $statuses.Count -Total $statuses.Count
    exit 0
}

# ---------- Action = Clean: 真实整理 ----------
if ($drives.Count -eq 0) {
    Write-EngineLog '未指定有效磁盘, 引擎退出' -Level ERROR
    exit 2
}

Write-EngineLog ('================ 引擎启动: 目标={0}  CPU阈值={1}%  触发={2}s/恢复={3}s ================' -f `
                 ($drives -join ','), $CpuThreshold, $HighSeconds, $LowSeconds)

# 6.3 按顺序串行处理每个磁盘(并发整理会让磁盘寻道互相干扰)
#     每个磁盘: 前置碎片率 -> defrag /O /H(CPU 守护) -> 后置碎片率 -> 记录变化
$results = @()
foreach ($drive in $drives) {
    $preFrag = $null
    try { $preFrag = (Get-DriveStatus -Drive $drive).FragPercent } catch { }

    $r = Invoke-DefragWithCpuGuard -Drive $drive -CpuThreshold $CpuThreshold `
                                    -HighSeconds $HighSeconds -LowSeconds $LowSeconds

    $postFrag = $null
    try { $postFrag = (Get-DriveStatus -Drive $drive).FragPercent } catch { }

    $r = $r | Add-Member -NotePropertyName PreFrag  -NotePropertyValue $preFrag  -PassThru
    $r = $r | Add-Member -NotePropertyName PostFrag -NotePropertyValue $postFrag -PassThru
    $results += $r
}

# 6.4 汇总
$okCount   = @($results | Where-Object { $_.Status -eq 'OK' }).Count
$total     = $results.Count
$summary   = ($results | ForEach-Object {
                '{0}(exit={1},挂起={2},恢复={3},碎片{4}->{5})' -f `
                    $_.Drive, $_.ExitCode, $_.Suspended, $_.Resumed,
                    (Format-FragValue $_.PreFrag), (Format-FragValue $_.PostFrag)
             }) -join '; '
$totalSusp = ($results | Measure-Object -Property Suspended -Sum).Sum
$totalRes  = ($results | Measure-Object -Property Resumed   -Sum).Sum

Write-EngineLog ('全部完成: 成功={0}/{1}  累计挂起={2}次 恢复={3}次   明细: {4}' -f `
                 $okCount, $total, $totalSusp, $totalRes, $summary)

Write-EngineResult -Targets ($drives -join ',') -Summary $summary -OkCount $okCount -Total $total

# 6.4b 只有 defrag 全部跑完后, 才落"每日一次锁"
Write-LastRunDate

# 6.4c 通知用户: engine 才确切知道 defrag 彻底结束的时刻, 完成通知由这里发
if ($okCount -eq $total) {
    Send-Notification -Message ('磁盘清理已完成（{0} 个磁盘）' -f $total) -Level Info
}
else {
    Send-Notification -Message ('磁盘清理结束：成功 {0}/{1}，有 {2} 个磁盘未完成' -f $okCount, $total, ($total - $okCount)) -Level Warning
}

# 6.5 用退出码返回执行结果: 0=全部成功; 1=有失败; 2=参数错误
if ($okCount -eq $total) { exit 0 } else { exit 1 }
#endregion
