# 生成 AutoDefrag 多尺寸 ICO：拟物风硬盘图标（金属盘体 + 盘片 + 磁头臂 + 指示灯）
# 每个尺寸按自身像素网格单独绘制，而不是把 256 直接缩小，保证 16px 仍然可辨认。
# 用法: powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $root 'src\AutoDefrag\Assets'
$previewDir = Join-Path $root 'docs\previews\1.8.0'
foreach ($d in @($outDir, $previewDir)) {
    if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
}
$outFile = Join-Path $outDir 'AutoDefrag.ico'

function New-RoundedRectPath([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = [Math]::Min($r * 2, [Math]::Min($w, $h))
    $p.AddArc([single]$x, [single]$y, [single]$d, [single]$d, 180, 90)
    $p.AddArc([single]($x + $w - $d), [single]$y, [single]$d, [single]$d, 270, 90)
    $p.AddArc([single]($x + $w - $d), [single]($y + $h - $d), [single]$d, [single]$d, 0, 90)
    $p.AddArc([single]$x, [single]($y + $h - $d), [single]$d, [single]$d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-EllipsePath([double]$cx, [double]$cy, [double]$r) {
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $p.AddEllipse([single]($cx - $r), [single]($cy - $r), [single]($r * 2), [single]($r * 2))
    return $p
}

# 一个尺寸的完整绘制：尺寸越小，细节越少，避免糊成一团
function New-DiskIcon([int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [double]$size
    $pad = $s * 0.035
    $bodyW = $s - $pad * 2
    $body = New-RoundedRectPath $pad $pad $bodyW $bodyW ($s * 0.17)

    # ---- 金属盘体：竖向拉丝渐变（上亮下暗，模拟顶面反光） ----
    $bodyRect = [System.Drawing.RectangleF]::new([single]$pad, [single]$pad, [single]$bodyW, [single]$bodyW)
    $metal = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $bodyRect,
        [System.Drawing.Color]::FromArgb(255, 243, 247, 251),
        [System.Drawing.Color]::FromArgb(255, 137, 149, 162),
        [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $blend = [System.Drawing.Drawing2D.ColorBlend]::new(4)
    $blend.Colors = @(
        [System.Drawing.Color]::FromArgb(255, 246, 250, 253),
        [System.Drawing.Color]::FromArgb(255, 213, 222, 231),
        [System.Drawing.Color]::FromArgb(255, 170, 181, 193),
        [System.Drawing.Color]::FromArgb(255, 128, 140, 153))
    $blend.Positions = @(0.0, 0.42, 0.78, 1.0)
    $metal.InterpolationColors = $blend
    $g.FillPath($metal, $body)

    # ---- 盘体倒角：左上高光 + 右下阴影 ----
    $bevelInset = $s * 0.045
    $bevel = New-RoundedRectPath ($pad + $bevelInset) ($pad + $bevelInset) ($bodyW - $bevelInset * 2) ($bodyW - $bevelInset * 2) ($s * 0.13)
    $penHi = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(150, 255, 255, 255), [single][Math]::Max(1, $s * 0.012))
    $penLo = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(90, 40, 50, 62), [single][Math]::Max(1, $s * 0.012))
    $g.DrawPath($penHi, $bevel)
    $g.DrawPath($penLo, $bevel)

    # ---- 盘片凹槽 + 镜面盘片 ----
    $cx = $pad + $bodyW * 0.435
    $cy = $pad + $bodyW * 0.455
    $pr = $bodyW * 0.305
    $fine = $size -ge 48
    $mid = $size -ge 24

    if ($size -lt 32) {
        # 小尺寸走简化画法：深色盘仓 + 高亮盘片 + 中心轴，保证 16px 下不是一团灰
        $bezelR = $bodyW * 0.345
        $bezelBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 62, 72, 84))
        $g.FillEllipse($bezelBrush, [single]($cx - $bezelR), [single]($cy - $bezelR),
            [single]($bezelR * 2), [single]($bezelR * 2))

        $smallPlatterR = $bodyW * 0.285
        $smallPlatter = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 222, 230, 239))
        $g.FillEllipse($smallPlatter, [single]($cx - $smallPlatterR), [single]($cy - $smallPlatterR),
            [single]($smallPlatterR * 2), [single]($smallPlatterR * 2))

        $smallHoleR = $bodyW * 0.095
        $smallHole = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 76, 88, 102))
        $g.FillEllipse($smallHole, [single]($cx - $smallHoleR), [single]($cy - $smallHoleR),
            [single]($smallHoleR * 2), [single]($smallHoleR * 2))

        $bezelBrush.Dispose(); $smallPlatter.Dispose(); $smallHole.Dispose()
        $metal.Dispose(); $body.Dispose(); $bevel.Dispose(); $penHi.Dispose(); $penLo.Dispose()
        $g.Dispose()
        return $bmp
    }

    $tray = New-EllipsePath $cx $cy ($pr * 1.13)
    $trayBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 58, 68, 80))
    $g.FillPath($trayBrush, $tray)

    $platter = New-EllipsePath $cx $cy $pr
    $pg = [System.Drawing.Drawing2D.PathGradientBrush]::new($platter)
    $pg.CenterPoint = [System.Drawing.PointF]::new([single]($cx - $pr * 0.28), [single]($cy - $pr * 0.32))
    $pg.CenterColor = [System.Drawing.Color]::FromArgb(255, 236, 243, 250)
    $pg.SurroundColors = @([System.Drawing.Color]::FromArgb(255, 118, 132, 148))
    $g.FillPath($pg, $platter)

    if ($mid) {
        # 盘片同心纹路（数据区）
        $penRing = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(70, 250, 253, 255), [single][Math]::Max(1, $s * 0.008))
        foreach ($k in 0.42, 0.63, 0.82) {
            $g.DrawEllipse($penRing, [single]($cx - $pr * $k), [single]($cy - $pr * $k),
                [single]($pr * $k * 2), [single]($pr * $k * 2))
        }
        $penRing.Dispose()
    }

    # 主轴
    $spindleR = $pr * 0.215
    $spindleBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 205, 214, 224))
    $g.FillEllipse($spindleBrush, [single]($cx - $spindleR), [single]($cy - $spindleR),
        [single]($spindleR * 2), [single]($spindleR * 2))
    $holeR = $spindleR * 0.48
    $holeBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 74, 86, 100))
    $g.FillEllipse($holeBrush, [single]($cx - $holeR), [single]($cy - $holeR),
        [single]($holeR * 2), [single]($holeR * 2))

    # ---- 磁头臂（32px 以下省略，避免小尺寸糊） ----
    if ($size -ge 32) {
        $pivotX = $pad + $bodyW * 0.845
        $pivotY = $pad + $bodyW * 0.775
        $headX = $cx + $pr * 0.72
        $headY = $cy + $pr * 0.46
        $armWidth = [single]($bodyW * 0.062)

        $penArmLo = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 92, 104, 118), [single]($armWidth + $s * 0.014))
        $penArmLo.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $penArmLo.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $penArm = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 224, 231, 239), $armWidth)
        $penArm.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $penArm.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawLine($penArmLo, [single]$pivotX, [single]$pivotY, [single]$headX, [single]$headY)
        $g.DrawLine($penArm, [single]$pivotX, [single]$pivotY, [single]$headX, [single]$headY)

        $pivotR = $bodyW * 0.082
        $pivotBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 206, 215, 226))
        $g.FillEllipse($pivotBrush, [single]($pivotX - $pivotR), [single]($pivotY - $pivotR),
            [single]($pivotR * 2), [single]($pivotR * 2))
        $pivotHoleR = $pivotR * 0.45
        $g.FillEllipse($holeBrush, [single]($pivotX - $pivotHoleR), [single]($pivotY - $pivotHoleR),
            [single]($pivotHoleR * 2), [single]($pivotHoleR * 2))

        $headR = [single]($s * 0.026)
        $g.FillEllipse($holeBrush, [single]($headX - $headR), [single]($headY - $headR),
            [single]($headR * 2), [single]($headR * 2))

        $penArmLo.Dispose(); $penArm.Dispose(); $pivotBrush.Dispose()
    }

    # ---- 右上角指示灯（24px 起；小尺寸只画纯色点） ----
    if ($size -ge 24) {
        $ledX = $pad + $bodyW * 0.815
        $ledY = $pad + $bodyW * 0.175
        $ledR = $s * 0.037
        if ($fine) {
            $glowR = $ledR * 2.4
            $glow = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(55, 90, 224, 120))
            $g.FillEllipse($glow, [single]($ledX - $glowR), [single]($ledY - $glowR),
                [single]($glowR * 2), [single]($glowR * 2))
            $glow.Dispose()
        }
        $led = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 74, 214, 118))
        $g.FillEllipse($led, [single]($ledX - $ledR), [single]($ledY - $ledR),
            [single]($ledR * 2), [single]($ledR * 2))
        $ledHighlight = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(170, 226, 255, 234))
        $g.FillEllipse($ledHighlight, [single]($ledX - $ledR * 0.45), [single]($ledY - $ledR * 0.55),
            [single]($ledR * 0.8), [single]($ledR * 0.7))
        $led.Dispose(); $ledHighlight.Dispose()
    }

    # ---- 四角螺丝（仅大尺寸，增强拟物感） ----
    if ($fine) {
        $screwR = $s * 0.019
        $screwFill = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 196, 205, 216))
        $screwPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(120, 60, 72, 86), [single][Math]::Max(1, $s * 0.006))
        foreach ($p in @(
                @(0.115, 0.115), @(0.885, 0.115), @(0.115, 0.885), @(0.885, 0.885))) {
            $sx = $s * $p[0]
            $sy = $s * $p[1]
            $g.FillEllipse($screwFill, [single]($sx - $screwR), [single]($sy - $screwR),
                [single]($screwR * 2), [single]($screwR * 2))
            $g.DrawEllipse($screwPen, [single]($sx - $screwR), [single]($sy - $screwR),
                [single]($screwR * 2), [single]($screwR * 2))
        }
        $screwFill.Dispose(); $screwPen.Dispose()
    }

    $metal.Dispose(); $body.Dispose(); $bevel.Dispose(); $penHi.Dispose(); $penLo.Dispose()
    $tray.Dispose(); $trayBrush.Dispose(); $platter.Dispose(); $pg.Dispose()
    $spindleBrush.Dispose(); $holeBrush.Dispose()
    $g.Dispose()
    return $bmp
}

# 每个尺寸单独绘制（像素对齐更好），ICO 内使用 PNG 压缩帧
$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$frames = @()
foreach ($s in $sizes) {
    $bmp = New-DiskIcon $s
    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += [pscustomobject]@{ Size = $s; Bytes = $ms.ToArray() }
    $ms.Dispose(); $bmp.Dispose()
}

# 组装 ICO 容器
$ms = [System.IO.MemoryStream]::new()
$bw = [System.IO.BinaryWriter]::new($ms)
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$f.Bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $bw.Write($f.Bytes) }
$bw.Flush()
[IO.File]::WriteAllBytes($outFile, $ms.ToArray())
$bw.Dispose(); $ms.Dispose()

# 256 母版同时作为窗口/标题栏图标资源
$master = New-DiskIcon 256
$master.Save((Join-Path $outDir 'icon-256.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$master.Save((Join-Path $previewDir 'icon-256.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$master.Dispose()

$small = New-DiskIcon 16
$small.Save((Join-Path $previewDir 'icon-16.png'), [System.Drawing.Imaging.ImageFormat]::Png)

# 16px 放大 8 倍（最近邻），方便人眼检查小尺寸辨识度
$zoom = [System.Drawing.Bitmap]::new(128, 128, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$zg = [System.Drawing.Graphics]::FromImage($zoom)
$zg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$zg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
$zg.DrawImage($small, 0, 0, 128, 128)
$zg.Dispose()
$zoom.Save((Join-Path $previewDir 'icon-16-zoom8.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$zoom.Dispose()
$small.Dispose()

Write-Host ("已生成 {0} ({1} 个尺寸, {2:N1} KB)" -f $outFile, $frames.Count, ((Get-Item $outFile).Length / 1KB))
