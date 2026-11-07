<#
=====================================================================
 app.ps1 — AutoDefrag Tray v2.0  主进程
 【第四步：整合 run_hidden.vbs / notify.ps1 / 每日定时调度】
=====================================================================
 运行环境 : Windows PowerShell 5.1 (STA) / Windows 10 & 11
 依赖     : System.Windows.Forms + System.Drawing (系统自带)

 本文件职责:
   1. 单实例保护(Data\app.pid) + PID 心跳
   2. 系统托盘图标(NotifyIcon)与右键菜单
   3. 设置窗体: 读写 Data\config.json + 写注册表 Run 键实现开机自启
   4. 触发 engine.ps1 执行真实磁盘整理(UI 只负责触发, 执行全部交给引擎)
   5. 看门狗: 轮询 engine 是否结束, 读取其结果文件并展示 + 原生通知
   6. 每日调度(DailyTime): "0"/"00:00"=开机即静默执行; 正常时间=到点执行, 开机已过点则补跑;
      由 Data\last_run.txt(engine 在 Clean 跑完后写入)保证"每日只执行一次"

 设计约束:
   - 本文件【不包含】任何 defrag 调用与 CPU 挂起逻辑, 那些都在 engine.ps1;
   - app.ps1 与 engine.ps1 之间只通过"命令行参数 + 结果文件"通信;
   - defrag.exe 需管理员权限, engine.ps1 一律经 -Verb RunAs 提权拉起;
     RunAs 无法重定向标准输出, 故结果一律由 engine 写文件、本程序轮询回收。

 启动方式: run_hidden.vbs 静默唤起, 或
   powershell -NoProfile -ExecutionPolicy Bypass -File .\app.ps1
=====================================================================
#>

#Requires -Version 5.1

[CmdletBinding()]
param(
    # 由 run_hidden.vbs 传入(带此开关 = 走 Run 键/开机自启链路)。
    # 仅用于日志标注"开机自启已验证", 不改变任何行为。
    [switch]$LaunchedByVbs
)

#region 0. 依赖加载与路径常量
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# 脚本根目录: 优先取脚本自身所在目录, 便于整个目录搬迁
$script:Root       = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$script:DataDir    = Join-Path $script:Root 'Data'
$script:LogDir     = Join-Path $script:Root 'Logs'
$script:LogFile    = Join-Path $script:LogDir  'app.log'
$script:PidFile    = Join-Path $script:DataDir 'app.pid'
$script:ConfigFile = Join-Path $script:DataDir 'config.json'
$script:EnginePath = Join-Path $script:Root    'engine.ps1'
$script:ResultFile = Join-Path $script:DataDir 'last_result.txt'     # engine(Clean) 产物
$script:StatusFile = Join-Path $script:DataDir 'drive_status.json'   # engine(Status) 产物
$script:LastRunFile = Join-Path $script:DataDir 'last_run.txt'       # 每日一次锁(engine 写入)
$script:NotifyPath = Join-Path $script:Root    'notify.ps1'          # 原生气泡通知

# 开机自启: 注册表当前用户 Run 键(无需管理员权限)
$script:RunKey       = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$script:RunValueName = 'AutoDefrag'

$script:Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# ---------------------------------------------------------------------
# 托盘相关对象统一挂在 $script: 作用域。
# 原因: PowerShell 把脚本块挂到 .NET 事件上后, 回调执行时【看不到】定义处的
#       函数局部变量 —— 局部变量在函数返回后即消失, 回调里会读到 $null。
#       所以凡是 Click/Tick 回调要访问的对象, 都必须放到 $script: 作用域。
# ---------------------------------------------------------------------
$script:Config         = $null    # 运行期配置(启动时由 Get-AppConfig 载入)
$script:notifyIcon     = $null
$script:appContext     = $null
$script:trayMenu       = $null
$script:driveSubMenu   = $null
$script:heartbeatTimer = $null
$script:setChk         = $null   # 设置窗体: 开机自启复选框
$script:setNum         = $null   # 设置窗体: CPU 阈值输入框
$script:setTime        = $null   # 设置窗体: 每日检查时间输入框
$script:setNotify      = $null   # 设置窗体: 通知停留时长输入框
$script:engineProc     = $null   # 最近一次启动的 engine.ps1 进程(用于防重入 + 看门狗)
$script:engineMode     = $null   # 最近一次 engine 的动作: Clean / Status
$script:engineIsAuto   = $false  # 本次 engine 是否由"定时/开机"自动触发
$script:watchdogTimer  = $null   # 看门狗定时器: 轮询 engine 是否结束
$script:dailyTimer     = $null   # 每日定时检查定时器
$script:dailyTriggeredDate = $null  # 本次会话内已触发过清理的日期(防止 30s 重复触发)
#endregion

#region 1. 日志工具

function Initialize-AppEnvironment {
    # 确保 Data / Logs 目录存在(首次运行或目录被清理时)
    foreach ($dir in @($script:DataDir, $script:LogDir)) {
        if (-not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
    }
}

function Write-AppLog {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [ValidateSet('INFO', 'WARN', 'ERROR')][string]$Level = 'INFO'
    )
    $line = '[{0}] [{1}] {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Level, $Message
    try {
        # 直接用 .NET 追加, 规避 PS5.1 中 Add-Content -Encoding UTF8 反复写 BOM 的坑
        [IO.File]::AppendAllText($script:LogFile, $line + [Environment]::NewLine, $script:Utf8NoBom)
    }
    catch { }
}

function Get-ParentProcessName {
    <#
      取本进程的父进程名, 用于判断"是谁把我拉起来的":
        - wscript.exe -> 由 run_hidden.vbs(Run 键/开机自启) 拉起
        - 其它(如 powershell.exe / explorer.exe) -> 手动启动
      失败返回 '未知'(不影响主流程, 仅用于日志标注)。
    #>
    try {
        $me = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId=$PID" -ErrorAction Stop
        if ($me -and $me.ParentProcessId) {
            $parent = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId=$($me.ParentProcessId)" -ErrorAction Stop
            if ($parent -and $parent.Name) { return [string]$parent.Name }
        }
    }
    catch { }
    return '未知'
}

function Write-StartupBanner {
    <#
      在日志里打一条醒目的"启动分隔带", 便于核对开机自启是否真的触发。
      启动来源判定:
        1. 优先看 -LaunchedByVbs 开关 —— run_hidden.vbs 主动传参, 最可靠;
        2. 没有该开关时退化为看父进程名(父进程已退出则显示"未知")。
      重启或注销重登后, 若日志末尾出现新的分隔带且标注"开机自启(Run 键)",
      即证明 Run 键在登录时确实被系统执行了。
    #>
    $bootTime = (Get-Date).AddMilliseconds(-[Environment]::TickCount)

    if ($LaunchedByVbs) {
        $parent  = '(由 run_hidden.vbs 拉起)'
        $verdict = '开机自启(Run 键)'
    }
    else {
        $parent  = Get-ParentProcessName
        $verdict = "手动/其它启动(父进程=$parent)"
    }

    Write-AppLog '================================================================'
    Write-AppLog ('启动会话 | 本次开机于 {0} | 会话ID={1} | PID={2} | 启动来源={3} | 判定={4}' -f `
                  $bootTime.ToString('yyyy-MM-dd HH:mm:ss'), (Get-Process -Id $PID).SessionId, $PID, $parent, $verdict)
    Write-AppLog ('程序版本 v2.1 | 脚本根目录={0}' -f $script:Root)
}
#endregion

#region 2. 配置读写 (Data\config.json)

function Get-DefaultConfig {
    # config.json 的出厂默认值
    return [pscustomobject]@{
        CpuThreshold        = 70       # CPU 触发阈值(%)
        DailyTime           = '03:00'  # 每日检查时间(HH:mm)
        AutoStart           = $true    # 开机自启
        NotificationTimeout = 30       # 通知停留时长(秒), 10-120
    }
}

function Get-AppConfig {
    <#
      读取 config.json。任何字段缺失/损坏都回退到默认值, 保证程序永远能起来。
      返回 PSCustomObject: CpuThreshold / DailyTime / AutoStart / NotificationTimeout
    #>
    $def = Get-DefaultConfig
    $cfg = [pscustomobject]@{
        CpuThreshold        = $def.CpuThreshold
        DailyTime           = $def.DailyTime
        AutoStart           = $def.AutoStart
        NotificationTimeout = $def.NotificationTimeout
    }

    if (-not (Test-Path -LiteralPath $script:ConfigFile)) { return $cfg }

    try {
        # 用 .NET 读, 自动识别 BOM/编码
        $text = [IO.File]::ReadAllText($script:ConfigFile)
        if ([string]::IsNullOrWhiteSpace($text)) { return $cfg }
        $json = $text | ConvertFrom-Json
    }
    catch {
        Write-AppLog ('config.json 解析失败, 改用默认配置: ' + $_.Exception.Message) -Level WARN
        return $cfg
    }

    if ($null -ne $json.CpuThreshold)        { $cfg.CpuThreshold        = [int]$json.CpuThreshold }
    if ($json.DailyTime)                     { $cfg.DailyTime           = [string]$json.DailyTime }
    if ($null -ne $json.AutoStart)           { $cfg.AutoStart           = [bool]$json.AutoStart }
    if ($null -ne $json.NotificationTimeout) { $cfg.NotificationTimeout = [int]$json.NotificationTimeout }

    # 兜底校正: 阈值越界 / 时间格式非法 / 通知时长越界 都退回默认
    # DailyTime 合法值: "0" / "00:00"(=开机即跑) 或 HH:mm 24 小时制
    if ($cfg.CpuThreshold -lt 10 -or $cfg.CpuThreshold -gt 100) { $cfg.CpuThreshold = $def.CpuThreshold }
    if ($cfg.DailyTime -notmatch '^(0|00:00|([01]\d|2[0-3]):[0-5]\d)$') { $cfg.DailyTime = $def.DailyTime }
    if ($cfg.NotificationTimeout -lt 10 -or $cfg.NotificationTimeout -gt 120) { $cfg.NotificationTimeout = $def.NotificationTimeout }

    return $cfg
}

function Save-AppConfig {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][int]$CpuThreshold,
        [Parameter(Mandatory = $true)][string]$DailyTime,
        [Parameter(Mandatory = $true)][bool]$AutoStart,
        [Parameter(Mandatory = $true)][int]$NotificationTimeout
    )
    $payload = [ordered]@{
        CpuThreshold        = $CpuThreshold
        DailyTime           = $DailyTime
        AutoStart           = $AutoStart
        NotificationTimeout = $NotificationTimeout
    }
    $json = $payload | ConvertTo-Json
    [IO.File]::WriteAllText($script:ConfigFile, $json, $script:Utf8NoBom)
    Write-AppLog ('配置已写入 config.json: CpuThreshold={0}, DailyTime={1}, AutoStart={2}, NotificationTimeout={3}s' -f `
                  $CpuThreshold, $DailyTime, $AutoStart, $NotificationTimeout)

    # 内存配置同步刷新, 后续"开始清理"直接读 $script:Config
    $script:Config = Get-AppConfig
}

function Set-AutoStart {
    <#
      开机自启的真实落地: 写 HKCU\...\Run 键。
      Run 值指向 run_hidden.vbs(由它再静默唤起 app.ps1), 这样开机不会闪黑窗口。
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][bool]$Enabled)

    if ($Enabled) {
        $value = 'wscript.exe "{0}"' -f (Join-Path $script:Root 'run_hidden.vbs')
        if (-not (Test-Path -Path $script:RunKey)) {
            New-Item -Path $script:RunKey -Force | Out-Null
        }
        Set-ItemProperty -Path $script:RunKey -Name $script:RunValueName -Value $value -Type String
        Write-AppLog ('已开启开机自启 -> {0}={1}' -f $script:RunValueName, $value)
    }
    else {
        Remove-ItemProperty -Path $script:RunKey -Name $script:RunValueName -ErrorAction SilentlyContinue
        Write-AppLog '已关闭开机自启(已移除 Run 键)'
    }
}
#endregion

#region 3. 单实例保护与心跳

function Assert-SingleInstance {
    <#
      .SYNOPSIS
        单实例断言。返回 $true = 本进程成功接管; $false = 已有实例在运行, 本进程应退出。
      .DESCRIPTION
        读取 Data\app.pid:
          - PID 对应进程仍活着 -> 放弃(返回 $false)
          - PID 是"僵尸"(进程已不存在) -> 接管并覆盖写入自己的 PID
    #>
    if (Test-Path -LiteralPath $script:PidFile) {
        $raw = ''
        try { $raw = (Get-Content -LiteralPath $script:PidFile -ErrorAction Stop | Select-Object -First 1) }
        catch { $raw = '' }

        $oldPid = 0
        if ($raw -and [int]::TryParse(([string]$raw).Trim(), [ref]$oldPid) -and $oldPid -gt 0 -and $oldPid -ne $PID) {
            if (Get-Process -Id $oldPid -ErrorAction SilentlyContinue) {
                return $false     # 老实例还活着
            }
            # 否则是僵尸 PID: 旧进程已消失, 继续往下走接管逻辑
        }
    }

    # ---- 接管: 覆盖写入自己的 PID ----
    Set-Content -LiteralPath $script:PidFile -Value $PID -Encoding ASCII
    return $true
}

function Remove-AppPidFile {
    # 只删除"属于自己的" PID 文件, 避免误删已接管者的文件
    try {
        if (Test-Path -LiteralPath $script:PidFile) {
            $cur = (Get-Content -LiteralPath $script:PidFile -ErrorAction Stop | Select-Object -First 1)
            if ($cur -and ([string]$cur).Trim() -eq "$PID") {
                Remove-Item -LiteralPath $script:PidFile -Force -ErrorAction SilentlyContinue
            }
        }
    }
    catch { }
}

function Start-Heartbeat {
    <#
      心跳定时器: 每 30s 刷新一次 PID 文件。
      若发现 PID 文件内容已不是自己(被别的实例顶替), 则本实例主动退出。
    #>
    $script:heartbeatTimer = New-Object System.Windows.Forms.Timer
    $script:heartbeatTimer.Interval = 30000
    $script:heartbeatTimer.Add_Tick({
        try {
            $cur = (Get-Content -LiteralPath $script:PidFile -ErrorAction Stop | Select-Object -First 1)
            if ($cur -and ([string]$cur).Trim() -ne "$PID") {
                Write-AppLog 'PID 文件已被其它实例接管, 本实例退出' -Level WARN
                Exit-App
                return
            }
        }
        catch { }

        # 刷新心跳
        Set-Content -LiteralPath $script:PidFile -Value $PID -Encoding ASCII -ErrorAction SilentlyContinue
    })
    $script:heartbeatTimer.Start()
}
#endregion

#region 4. 通用 UI 工具

function Show-MessageBox {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [string]$Title = 'AutoDefrag Tray',
        [System.Windows.Forms.MessageBoxButtons]$Buttons = 'OK',
        [System.Windows.Forms.MessageBoxIcon]$Icon = 'Information'
    )
    [void][System.Windows.Forms.MessageBox]::Show($Text, $Title, $Buttons, $Icon)
}

function Open-AppLog {
    # 先确保文件存在, 否则记事本会先弹一个"找不到文件"的确认框
    if (-not (Test-Path -LiteralPath $script:LogFile)) {
        [IO.File]::WriteAllText($script:LogFile, '', $script:Utf8NoBom)
    }
    Start-Process -FilePath 'notepad.exe' -ArgumentList $script:LogFile
}

function Exit-App {
    Write-AppLog '用户请求退出'
    try {
        if ($script:notifyIcon) {
            $script:notifyIcon.Visible = $false   # 必须先隐藏, 否则图标会残留到鼠标划过才消失
            $script:notifyIcon.Dispose()
            $script:notifyIcon = $null
        }
    }
    catch { }
    # 结束消息循环; Application::Run 返回后进入 finally 做统一清理
    [System.Windows.Forms.Application]::Exit()
}
#endregion

#region 5. 设置窗体

function Show-SettingsForm {
    $form = New-Object System.Windows.Forms.Form
    $form.Text            = 'AutoDefrag 设置'
    $form.ClientSize      = New-Object System.Drawing.Size(360, 275)
    $form.StartPosition   = 'CenterScreen'
    $form.FormBorderStyle = 'FixedDialog'
    $form.MaximizeBox     = $false
    $form.MinimizeBox     = $false

    # --- 开机自启 ---
    $chk = New-Object System.Windows.Forms.CheckBox
    $chk.Text     = '开机自启(写入当前用户 Run 键)'
    $chk.Location = New-Object System.Drawing.Point(20, 20)
    $chk.Size     = New-Object System.Drawing.Size(290, 24)
    $chk.Checked  = [bool]$script:Config.AutoStart

    # --- CPU 触发阈值 ---
    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Text     = 'CPU 触发阈值(%):'
    $lbl.Location = New-Object System.Drawing.Point(20, 62)
    $lbl.Size     = New-Object System.Drawing.Size(120, 22)

    $num = New-Object System.Windows.Forms.NumericUpDown
    $num.Location = New-Object System.Drawing.Point(150, 60)
    $num.Size     = New-Object System.Drawing.Size(80, 24)
    $num.Minimum  = 10
    $num.Maximum  = 100
    $num.Value    = [decimal]$script:Config.CpuThreshold

    # --- 每日检查时间 ---
    $lbl2 = New-Object System.Windows.Forms.Label
    $lbl2.Text     = '每日检查时间:'
    $lbl2.Location = New-Object System.Drawing.Point(20, 100)
    $lbl2.Size     = New-Object System.Drawing.Size(120, 22)

    $txtTime = New-Object System.Windows.Forms.TextBox
    $txtTime.Location = New-Object System.Drawing.Point(150, 98)
    $txtTime.Size     = New-Object System.Drawing.Size(80, 24)
    $txtTime.Text     = [string]$script:Config.DailyTime

    $hint = New-Object System.Windows.Forms.Label
    $hint.Text      = '格式 HH:mm(24 小时制), 例如 03:00；填 0 表示"开机即静默执行"。'
    $hint.ForeColor = [System.Drawing.Color]::Gray
    $hint.Location  = New-Object System.Drawing.Point(20, 126)
    $hint.Size      = New-Object System.Drawing.Size(320, 32)

    # --- 通知停留时长(秒) ---
    $lbl3 = New-Object System.Windows.Forms.Label
    $lbl3.Text     = '通知停留时长(秒):'
    $lbl3.Location = New-Object System.Drawing.Point(20, 166)
    $lbl3.Size     = New-Object System.Drawing.Size(130, 22)

    $numNotify = New-Object System.Windows.Forms.NumericUpDown
    $numNotify.Location = New-Object System.Drawing.Point(150, 164)
    $numNotify.Size     = New-Object System.Drawing.Size(80, 24)
    $numNotify.Minimum  = 10
    $numNotify.Maximum  = 120
    $numNotify.Value    = [decimal]$script:Config.NotificationTimeout

    # --- 保存按钮 ---
    $btn = New-Object System.Windows.Forms.Button
    $btn.Text     = '保存'
    $btn.Location = New-Object System.Drawing.Point(130, 213)
    $btn.Size     = New-Object System.Drawing.Size(100, 32)
    $btn.Add_Click({
        # 回调看不到函数局部变量, 所以读 $script: 里的控件引用
        $auto   = [bool]$script:setChk.Checked
        $cpu    = [int]$script:setNum.Value
        $time   = ([string]$script:setTime.Text).Trim()
        $notify = [int]$script:setNotify.Value

        if ($time -notmatch '^(0|00:00|([01]\d|2[0-3]):[0-5]\d)$') {
            Show-MessageBox -Text '每日检查时间格式不正确：请填 24 小时制的 HH:mm(如 03:00)，或填 0 表示开机即执行。' -Icon Warning
            return
        }
        if ($notify -lt 10 -or $notify -gt 120) {
            Show-MessageBox -Text '通知停留时长请设置在 10 到 120 秒之间。' -Icon Warning
            return
        }

        try {
            Save-AppConfig -CpuThreshold $cpu -DailyTime $time -AutoStart $auto -NotificationTimeout $notify
            Set-AutoStart   -Enabled $auto
        }
        catch {
            Write-AppLog ('保存设置失败: ' + $_.Exception.Message) -Level ERROR
            Show-MessageBox -Text ('保存失败：' + $_.Exception.Message) -Icon Error
            return
        }

        Show-MessageBox -Text ("设置已保存。`n开机自启 = {0}`nCPU 触发阈值 = {1}%`n每日检查时间 = {2}`n通知停留时长 = {3} 秒" -f `
                               $(if ($auto) { '是' } else { '否' }), $cpu, $time, $notify)
    })

    # 把回调要用的控件挂到 $script: 作用域(见 region 0 说明)
    $script:setChk    = $chk
    $script:setNum    = $num
    $script:setTime   = $txtTime
    $script:setNotify = $numNotify

    $form.Controls.AddRange(@($chk, $lbl, $num, $lbl2, $txtTime, $hint, $lbl3, $numNotify, $btn))
    try {
        [void]$form.ShowDialog()   # 模态显示, 关闭后返回
    }
    finally {
        $script:setChk    = $null
        $script:setNum    = $null
        $script:setTime   = $null
        $script:setNotify = $null
        $form.Dispose()
    }
}
#endregion

#region 6. 引擎调度(拉起 engine.ps1 + 看门狗回收结果)

function Show-Notification {
    <#
      调 notify.ps1 弹原生通知(v2.1: WinRT Toast, 常驻到用户关闭或超时)。
      与托盘图标解耦: 即便 UI 暂不可用, 通知也能出现。
      notify.ps1 显示完全交给系统, 自身立刻退出, 不占后台资源。
      停留时长取自 config.json 的 NotificationTimeout。
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [string]$Title = 'AutoDefrag',
        [ValidateSet('Info', 'Warning', 'Error')][string]$Level = 'Info'
    )
    try {
        Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script:NotifyPath,
            '-Title', $Title, '-Message', $Message, '-Level', $Level,
            '-TimeoutSeconds', "$([int]$script:Config.NotificationTimeout)"
        ) | Out-Null
    }
    catch {
        Write-AppLog ('弹出通知失败: ' + $_.Exception.Message) -Level WARN
    }
}

function Test-EngineRunning {
    <#
      判断最近一次拉起的 engine 是否仍在运行。
      engine 是提权进程, 非提权的 HasExited 查询可能因权限不足抛异常,
      此时退化为"按 PID 查进程是否存在"。
    #>
    if (-not $script:engineProc) { return $false }
    try {
        if ($script:engineProc.HasExited) { return $false }
        return $true
    }
    catch {
        return [bool](Get-Process -Id $script:engineProc.Id -ErrorAction SilentlyContinue)
    }
}

function Start-EngineProcess {
    <#
      .SYNOPSIS
        拉起 engine.ps1 执行 Clean(整理)或 Status(只读探测)。
      .DESCRIPTION
        - defrag.exe 需要管理员权限, 而本程序经 Run 键启动是非提权的,
          因此这里统一用 -Verb RunAs 提权(是否弹 UAC 取决于系统设置);
        - RunAs 无法重定向标准输出, 故执行结果一律由 engine 写文件,
          本程序用看门狗轮询进程 + 读取结果文件来回收。
      .NOTES
        本函数只负责"启动", 不弹任何用户提示; 提示由调用方(菜单)决定。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][ValidateSet('Clean', 'Status')][string]$Mode,

        # Clean 时的目标盘符; Status 可省略(engine 自行枚举本地固定磁盘)
        [string[]]$Drives,

        # 由"开机/定时"自动触发时为真: 完成后只弹气泡通知, 不弹 MessageBox 打断用户
        [switch]$Auto
    )

    if (Test-EngineRunning) {
        Write-AppLog '已有清理/探测任务在运行, 拒绝重复启动' -Level WARN
        return
    }

    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script:EnginePath,
                 '-Action', $Mode)
    if ($Drives -and $Drives.Count -gt 0) {
        $argList += @('-TargetDrive', ($Drives -join ','))
    }
    $argList += @('-CpuThreshold', "$([int]$script:Config.CpuThreshold)")

    try {
        $script:engineProc  = Start-Process -FilePath 'powershell.exe' -ArgumentList $argList `
                                            -Verb RunAs -PassThru -WindowStyle Hidden
        $script:engineMode    = $Mode
        $script:engineIsAuto  = [bool]$Auto
        Write-AppLog ('已拉起 engine.ps1 (PID={0}, Mode={1}, Auto={2}, 目标={3}, 阈值={4}%)' -f `
                      $script:engineProc.Id, $Mode, $script:engineIsAuto, ($Drives -join ','), $script:Config.CpuThreshold)
    }
    catch {
        Write-AppLog ('拉起 engine.ps1 失败: ' + $_.Exception.Message) -Level ERROR
        Show-MessageBox -Text ('启动引擎失败：' + $_.Exception.Message) -Icon Error
        $script:engineProc = $null
        $script:engineMode = $null
        $script:engineIsAuto = $false
    }
}

function Show-CleanResult {
    # engine(Clean) 彻底跑完后: 读 last_result.txt 展示
    # (该函数只在看门狗确认 engine 进程已退出后才被调用)
    # 注意: 清理完成的【通知由 engine.ps1 自己发出】(它才确切知道 defrag 跑完的时刻),
    #       这里不再重复发通知, 以免同一件事弹两条。
    [CmdletBinding()]
    param([switch]$Auto)

    $msg = '清理任务已结束, 但未找到结果文件。'
    if (Test-Path -LiteralPath $script:ResultFile) {
        try { $msg = ([IO.File]::ReadAllText($script:ResultFile)).Trim() } catch { }
    }
    Write-AppLog ('清理结果: ' + $msg)

    # 自动(开机/定时)"静默执行": 只留日志, 不弹 MessageBox 打断用户
    if ($Auto) { return }
    Show-MessageBox -Text $msg -Title '清理结果'
}

function Show-DriveStatusResult {
    # engine(Status) 结束后: 读取 drive_status.json 展示
    if (-not (Test-Path -LiteralPath $script:StatusFile)) {
        Show-MessageBox -Text '未能获取磁盘状态(结果文件缺失)。' -Icon Warning
        return
    }
    try {
        $json = ([IO.File]::ReadAllText($script:StatusFile)) | ConvertFrom-Json
    }
    catch {
        Show-MessageBox -Text ('磁盘状态解析失败：' + $_.Exception.Message) -Icon Error
        return
    }

    $lines = @('探测时间：' + [string]$json.ProbeTime, '')
    foreach ($d in @($json.Drives)) {
        $frag = if ($null -ne $d.FragPercent)  { '{0}%'    -f $d.FragPercent }  else { '--' }
        $free = if ($null -ne $d.FreeSpaceGB)  { '{0} GB'  -f $d.FreeSpaceGB }  else { '--' }
        $size = if ($null -ne $d.VolumeSizeGB) { '{0} GB'  -f $d.VolumeSizeGB } else { '--' }
        $lines += ('{0}   碎片率 {1}   剩余 {2} / 共 {3}' -f $d.Drive, $frag, $free, $size)
    }
    Show-MessageBox -Text ($lines -join [Environment]::NewLine) -Title '磁盘状态'
}

function Start-EngineWatchdog {
    <#
      看门狗定时器: 每 5s 检查一次 engine 是否已退出。
      RunAs 提权进程无法 WaitForExit(权限不足), 故用轮询。
      engine 退出后按本次动作(Clean/Status)读取对应结果文件并展示。
      注意: 定时器保持常驻运行(不 Stop), 这样后续每次任务都能被回收;
            engineProc 为空时直接空转返回, 开销可忽略。
    #>
    $script:watchdogTimer = New-Object System.Windows.Forms.Timer
    $script:watchdogTimer.Interval = 5000
    $script:watchdogTimer.Add_Tick({
        if (-not $script:engineProc) { return }
        if (Test-EngineRunning)      { return }

        # engine 已退出 -> 回收结果(先取出本次动作与触发方式)
        $mode   = $script:engineMode
        $isAuto = $script:engineIsAuto
        $script:engineProc   = $null
        $script:engineMode   = $null
        $script:engineIsAuto = $false

        Write-AppLog ('engine 已退出, 回收结果(Mode={0}, Auto={1})' -f $mode, $isAuto)
        if ($mode -eq 'Status') { Show-DriveStatusResult }
        else                    { Show-CleanResult -Auto:$isAuto }
    })
    $script:watchdogTimer.Start()
}
#endregion

#region 6b. 每日调度(开机即跑 / 定时到点 / 每日一次锁)

function Test-ImmediateDailyTime {
    # DailyTime 为 "0" 或 "00:00" 时, 语义是"开机即静默执行"
    $t = ([string]$script:Config.DailyTime).Trim()
    return ($t -eq '0' -or $t -eq '00:00')
}

function Get-DailyTargetTime {
    # 今天的目标时刻(以 config.json 的 DailyTime 为准); "0"/"00:00" 视为今天 00:00
    if (Test-ImmediateDailyTime) { return (Get-Date).Date }

    $parts = @(([string]$script:Config.DailyTime) -split ':')
    $h = 0; $m = 0
    [void][int]::TryParse([string]$parts[0], [ref]$h)
    if ($parts.Count -gt 1) { [void][int]::TryParse([string]$parts[1], [ref]$m) }
    return (Get-Date).Date.AddHours($h).AddMinutes($m)
}

function Get-LastRunDate {
    # 读 last_run.txt 里的日期字符串(yyyy-MM-dd); 不存在/为空返回 $null
    try {
        if (Test-Path -LiteralPath $script:LastRunFile) {
            $raw = ([IO.File]::ReadAllText($script:LastRunFile)).Trim()
            if ($raw) { return $raw }
        }
    }
    catch { }
    return $null
}

function Test-RunToday {
    # 每日一次锁: last_run.txt 的日期 == 今天 -> $true(今日已执行)
    $last = Get-LastRunDate
    if (-not $last) { return $false }
    return ($last -eq (Get-Date -Format 'yyyy-MM-dd'))
}

function Get-FixedDriveLetters {
    # 本机本地固定磁盘盘符(不含 U 盘/光驱/网络盘), 供定时清理使用
    $list = @()
    try {
        $disks = Get-CimInstance -ClassName Win32_LogicalDisk -Filter 'DriveType=3' -ErrorAction Stop
        foreach ($d in $disks) { $list += [string]$d.DeviceID }
    }
    catch {
        $list = @(Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue |
                  Where-Object { $_.Root } |
                  ForEach-Object { ([string]$_.Root).TrimEnd('\') })
    }
    return @($list | Where-Object { $_ })
}

function Start-CleanAllFixedDrives {
    # 自动(开机/定时)触发: 后台清理本机所有本地固定磁盘; 已有任务在跑则静默跳过
    if (Test-EngineRunning) {
        Write-AppLog '每日调度: 已有任务在运行, 本次跳过' -Level WARN
        return
    }
    $drives = @(Get-FixedDriveLetters)
    if ($drives.Count -eq 0) {
        Write-AppLog '每日调度: 未发现可清理的固定磁盘' -Level WARN
        return
    }
    Write-AppLog ('每日调度触发清理: 目标={0}' -f ($drives -join ','))
    # 先记下"本会话今日已触发", 避免启动/失败时 Timer 每 30s 反复重试
    $script:dailyTriggeredDate = (Get-Date -Format 'yyyy-MM-dd')
    Start-EngineProcess -Mode Clean -Drives $drives -Auto
}

function Invoke-DailyDecision {
    <#
      .SYNOPSIS 返回本次调度决定: done / run / wait
      .DESCRIPTION
        - 先查"每日一次锁"(last_run.txt == 今天) -> done(今日已执行)
        - DailyTime 为 "0"/"00:00" -> 开机即跑 -> run
        - 正常时间: now >= 目标时刻(到点, 或开机时已错过 -> 补跑) -> run; 否则 wait
    #>
    if (Test-RunToday)          { return 'done' }
    if ($script:dailyTriggeredDate -eq (Get-Date -Format 'yyyy-MM-dd')) { return 'done' }
    if (Test-ImmediateDailyTime) { return 'run' }
    if ((Get-Date) -ge (Get-DailyTargetTime)) { return 'run' }
    return 'wait'
}

function Invoke-StartupSchedule {
    <#
      开机/启动时调度:
        - 今天已跑过 -> 弹原生通知"今日磁盘维护已完成, 无需重复执行"(不弹 MessageBox, 免打断)
        - 应当跑(开机即跑 / 已过点补跑) -> 立即后台执行
        - 未到点 -> 交给 Timer 到点触发
    #>
    $decision = Invoke-DailyDecision
    Write-AppLog ('开机调度判定: {0} (DailyTime={1})' -f $decision, $script:Config.DailyTime)

    switch ($decision) {
        'done'  { Show-Notification -Message '今日磁盘维护已完成，无需重复执行' -Level Info }
        'run'   { Start-CleanAllFixedDrives }
        default { }   # wait: 交给 Start-DailyTimer 的 Timer 到点触发
    }
}

function Start-DailyTimer {
    <#
      后台定时器(每 30s 检查一次, 常驻不 Stop, 以便跨天自动进入下一天):
        - 已到/已过点且今天没跑 -> 触发清理
        - 今天已跑 -> 锁生效, 什么都不做
        - 未到点 -> 继续等待
    #>
    $script:dailyTimer = New-Object System.Windows.Forms.Timer
    $script:dailyTimer.Interval = 30000
    $script:dailyTimer.Add_Tick({
        if ((Invoke-DailyDecision) -ne 'run') { return }
        Start-CleanAllFixedDrives
    })
    $script:dailyTimer.Start()
}
#endregion

#region 6c. 托盘图标与右键菜单

function Build-TrayMenu {
    $script:trayMenu = New-Object System.Windows.Forms.ContextMenuStrip

    # -------- 菜单项 1: 查看磁盘状态 --------
    $itemStatus = New-Object System.Windows.Forms.ToolStripMenuItem
    $itemStatus.Text = '查看磁盘状态'
    $itemStatus.Add_Click({
        # 异步: 拉起 Status 动作的 engine 只读探测, 结束后由看门狗弹出结果
        Start-EngineProcess -Mode Status
    })
    [void]$script:trayMenu.Items.Add($itemStatus)

    # -------- 菜单项 2: 清理指定磁盘(子菜单) --------
    $script:driveSubMenu = New-Object System.Windows.Forms.ToolStripMenuItem
    $script:driveSubMenu.Text = '清理指定磁盘'

    # 遍历本机所有文件系统盘符, 每个盘符一个可勾选项
    $drives = @(Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue | Sort-Object Name)
    foreach ($d in $drives) {
        # 显示名用 Root 去掉末尾反斜杠: C:\ -> C:
        $label = if ($d.Root) { ([string]$d.Root).TrimEnd('\') } else { [string]$d.Name }

        $mi = New-Object System.Windows.Forms.ToolStripMenuItem
        $mi.Text         = $label
        $mi.Tag          = $label
        $mi.CheckOnClick = $true    # 点击自动在 勾选/取消 间切换
        $mi.Checked      = $false
        [void]$script:driveSubMenu.DropDownItems.Add($mi)
    }

    # 子菜单底部分隔线 + "开始清理勾选项"
    [void]$script:driveSubMenu.DropDownItems.Add((New-Object System.Windows.Forms.ToolStripSeparator))

    $itemRun = New-Object System.Windows.Forms.ToolStripMenuItem
    $itemRun.Text = '开始清理勾选项'
    $itemRun.Add_Click({
        $checked = @()
        foreach ($it in $script:driveSubMenu.DropDownItems) {
            if (($it -is [System.Windows.Forms.ToolStripMenuItem]) -and $it.Checked) {
                $checked += [string]$it.Tag
            }
        }
        if ($checked.Count -eq 0) {
            Show-MessageBox -Text '未勾选任何磁盘。请先在“清理指定磁盘”子菜单中勾选目标磁盘。' -Icon Warning
            return
        }
        if (Test-EngineRunning) {
            Show-MessageBox -Text '已有清理任务正在运行, 请等它结束再试。' -Icon Warning
            return
        }
        # UI 只负责提示与触发, 真实整理全部交给 engine.ps1
        Show-MessageBox -Text ("清理已开始, 若 CPU 过载将自动挂起。`n`n目标磁盘：{0}`nCPU 阈值：{1}%" -f `
                               ($checked -join '，'), [int]$script:Config.CpuThreshold)
        Start-EngineProcess -Mode Clean -Drives $checked
    })
    [void]$script:driveSubMenu.DropDownItems.Add($itemRun)
    [void]$script:trayMenu.Items.Add($script:driveSubMenu)

    # -------- 菜单项 3: 设置 --------
    $itemSettings = New-Object System.Windows.Forms.ToolStripMenuItem
    $itemSettings.Text = '设置'
    $itemSettings.Add_Click({ Show-SettingsForm })
    [void]$script:trayMenu.Items.Add($itemSettings)

    # -------- 分隔线 --------
    [void]$script:trayMenu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))

    # -------- 菜单项 4: 查看日志 --------
    $itemLog = New-Object System.Windows.Forms.ToolStripMenuItem
    $itemLog.Text = '查看日志'
    $itemLog.Add_Click({ Open-AppLog })
    [void]$script:trayMenu.Items.Add($itemLog)

    # -------- 菜单项 5: 退出 --------
    $itemExit = New-Object System.Windows.Forms.ToolStripMenuItem
    $itemExit.Text = '退出'
    $itemExit.Add_Click({ Exit-App })
    [void]$script:trayMenu.Items.Add($itemExit)
}

function New-TrayIcon {
    # 消息循环上下文(无主窗体, 纯托盘程序)
    $script:appContext = New-Object System.Windows.Forms.ApplicationContext

    $script:notifyIcon = New-Object System.Windows.Forms.NotifyIcon
    $script:notifyIcon.Icon             = [System.Drawing.SystemIcons]::Application  # 系统自带图标, 保证可见
    $script:notifyIcon.Text             = 'AutoDefrag Tray'                          # 提示文本上限 63 字符
    $script:notifyIcon.ContextMenuStrip = $script:trayMenu
    $script:notifyIcon.Visible          = $true
}
#endregion

#region 7. 主流程

Initialize-AppEnvironment

if (-not (Assert-SingleInstance)) {
    Write-AppLog '检测到已有实例在运行, 本实例退出' -Level WARN
    exit 0
}

# 单实例通过后立刻打"启动分隔带"(被拒绝的重复启动不会污染日志)
Write-StartupBanner

try {
    # 必须在创建任何控件之前调用, 否则窗体不会使用视觉样式
    [System.Windows.Forms.Application]::EnableVisualStyles()

    # 载入配置, 并把注册表 Run 键同步成配置里的期望状态(配置是唯一真相来源)
    $script:Config = Get-AppConfig
    Write-AppLog ('配置已载入: CpuThreshold={0}, DailyTime={1}, AutoStart={2}, NotificationTimeout={3}s' -f `
                  $script:Config.CpuThreshold, $script:Config.DailyTime, $script:Config.AutoStart, $script:Config.NotificationTimeout)
    Set-AutoStart -Enabled ([bool]$script:Config.AutoStart)

    Build-TrayMenu
    New-TrayIcon
    Start-Heartbeat
    Start-EngineWatchdog

    # 启动即判定: 今日已跑过 -> 提示; 应当跑(开机即跑/已过点补跑) -> 立即开始; 未到点 -> 交给 Timer
    Invoke-StartupSchedule
    Start-DailyTimer

    Write-AppLog ('托盘已就绪 (PID={0})' -f $PID)

    # 进入消息循环: 阻塞在这里, 直到 Exit-App 调用 Application::Exit()
    [System.Windows.Forms.Application]::Run($script:appContext)
}
catch {
    Write-AppLog ('运行异常: ' + $_.Exception.Message) -Level ERROR
    try {
        [void][System.Windows.Forms.MessageBox]::Show(
            ('托盘程序发生异常：' + [Environment]::NewLine + $_.Exception.Message),
            'AutoDefrag Tray', 'OK', 'Error')
    }
    catch { }
}
finally {
    # ---- 统一清理 ----
    foreach ($t in @($script:heartbeatTimer, $script:watchdogTimer, $script:dailyTimer)) {
        if ($t) { try { $t.Stop(); $t.Dispose() } catch { } }
    }
    if ($script:notifyIcon) {
        try { $script:notifyIcon.Visible = $false; $script:notifyIcon.Dispose() } catch { }
    }
    Remove-AppPidFile
    Write-AppLog '托盘程序已退出'
}
#endregion
