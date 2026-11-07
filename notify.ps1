<#
=====================================================================
 notify.ps1 — AutoDefrag Tray v2.0  原生通知小工具 (v2.1 Toast 版)
=====================================================================
 运行环境 : Windows PowerShell 5.1 / Windows 10 & 11
 依赖     : WinRT 投影 (系统自带, 无第三方模块)

 职责:
   弹出一条 Windows 原生 Toast 通知, 供 app.ps1 / engine.ps1 调用。

 v2.1 关键改进(解决"开机时通知被顶掉/一闪而过"):
   1. 改用 WinRT 的 ToastNotification (而非 NotifyIcon 气泡):
      Toast 由系统的 WpnService 持有并绘制, 属主进程退出后通知依然存在,
      因此【本脚本可以立刻退出, 不再需要 Start-Sleep 阻塞】。
   2. XML 里使用 duration="long" + scenario="reminder":
      - duration="long"  -> 拉长系统默认展示时长;
      - scenario="reminder" -> 通知强制常驻在屏幕上, 直到用户点击"关闭"。
      (reminder 场景要求至少一个按钮, 故附带一个 activationType="system"
       arguments="dismiss" 的关闭按钮)
   3. 用 ToastNotification.ExpirationTime = now + TimeoutSeconds:
      到时由系统自动清掉, 展示与超时全部交给 Windows, 本脚本零等待。

 用法:
   powershell -NoProfile -ExecutionPolicy Bypass -File .\notify.ps1 `
       -Title "AutoDefrag" -Message "C 盘整理完成" -Level Info -TimeoutSeconds 30

 参数:
   -Title / -Message / -Level(Info|Warning|Error) / -TimeoutSeconds(10-120, 默认30)

 兜底:
   若 WinRT 不可用(极罕见, 例如系统通知被彻底禁用), 退化为 NotifyIcon 气泡。
   气泡是"进程持有"的, 必须驻留才画得出来 —— 该分支才会短暂等待, 属必要时才走的路。
=====================================================================
#>

#Requires -Version 5.1

[CmdletBinding()]
param(
    [string]$Title = 'AutoDefrag',

    [Parameter(Mandatory = $true)][string]$Message,

    [ValidateSet('Info', 'Warning', 'Error')][string]$Level = 'Info',

    # 通知存活时长(秒)。与 config.json 的 NotificationTimeout 对应, 范围 10-120。
    [int]$TimeoutSeconds = 30
)

# 参数归一化: 越界一律夹到合法区间
if ($TimeoutSeconds -lt 10)  { $TimeoutSeconds = 10 }
if ($TimeoutSeconds -gt 120) { $TimeoutSeconds = 120 }

function ConvertTo-XmlText {
    # Toast 的 XML 里 & < > 必须转义, 否则 LoadXml 会抛异常
    param([AllowEmptyString()][string]$Text = '')
    return [System.Security.SecurityElement]::Escape($Text)
}

function Show-ToastNotification {
    <#
      主路径: WinRT Toast。
      显示与超时全部由系统处理, 本函数返回后脚本即可退出。
      返回 $true 表示已成功交给系统; $false 表示 WinRT 不可用需走兜底。
    #>
    try {
        [void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
        [void][Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime]
    }
    catch {
        return $false
    }

    # 未打包脚本应用没有自己的 AUMID, 借用 PowerShell 自身的 AUMID 即可正常弹出
    $appId = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe'

    $t = ConvertTo-XmlText $Title
    $m = ConvertTo-XmlText $Message

    # duration="long" 拉长展示; scenario="reminder" 强制常驻到用户关闭;
    # 关闭按钮满足 reminder 场景"至少一个按钮"的要求。
    $xml = @"
<toast duration="long" scenario="reminder">
    <visual>
        <binding template="ToastGeneric">
            <text>$t</text>
            <text>$m</text>
        </binding>
    </visual>
    <actions>
        <action content="关闭" activationType="system" arguments="dismiss" />
    </actions>
</toast>
"@

    try {
        $doc = New-Object Windows.Data.Xml.Dom.XmlDocument
        $doc.LoadXml($xml)

        $toast = New-Object Windows.UI.Notifications.ToastNotification $doc

        # 到期时间交给系统: 到时自动从屏幕/通知中心移除, 无需本进程等待
        $toast.ExpirationTime = [DateTimeOffset]::Now.AddSeconds($TimeoutSeconds)

        $notifier = [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId)
        $notifier.Show($toast)
        return $true
    }
    catch {
        return $false
    }
}

function Show-BalloonFallback {
    <#
      兜底路径: NotifyIcon 气泡(WinRT 不可用时的最后手段)。
      气泡随进程存在, 必须驻留一段时间才画得出来 —— 只有走到这里才会等待。
      为非 UI 进程调用, 用 DoEvents 泵消息而不是纯 Start-Sleep, 保证气泡真能渲染。
    #>
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing

    switch ($Level) {
        'Warning' { $sysIcon = [System.Drawing.SystemIcons]::Warning;     $tip = 'Warning' }
        'Error'   { $sysIcon = [System.Drawing.SystemIcons]::Error;       $tip = 'Error'   }
        default   { $sysIcon = [System.Drawing.SystemIcons]::Information; $tip = 'Info'    }
    }

    $icon = New-Object System.Windows.Forms.NotifyIcon
    try {
        $icon.Icon    = $sysIcon
        $icon.Visible = $true

        $ms = [Math]::Min($TimeoutSeconds * 1000, 30000)   # 气泡上限 30s
        $icon.ShowBalloonTip($ms, $Title, $Message, $tip)

        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while ((Get-Date) -lt $deadline) {
            [System.Windows.Forms.Application]::DoEvents()
            Start-Sleep -Milliseconds 200
        }
    }
    finally {
        try { $icon.Visible = $false; $icon.Dispose() } catch { }
    }
}

# ---- 主流程: 先试 Toast(零等待, 立刻退出); 不行才走兜底 ----
if (-not (Show-ToastNotification)) {
    Show-BalloonFallback
}
