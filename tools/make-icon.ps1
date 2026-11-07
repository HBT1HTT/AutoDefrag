# 生成 AutoDefrag 多尺寸 ICO（256 母版 -> 高质量缩放）
# 用法: powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing

$outDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\AutoDefrag\Assets'
if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
$outFile = Join-Path $outDir 'AutoDefrag.ico'

function New-RoundedRectPath([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-MasterBitmap([int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # 背景: 圆角方块 + 蓝色对角渐变（Win11 风格克制配色）
    $pad = [double]($size * 0.039)
    $rectW = [double]($size - $pad * 2)
    $bg = New-RoundedRectPath $pad $pad $rectW $rectW ([double]($size * 0.156))
    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.PointF]::new(0, 0),
        [System.Drawing.PointF]::new([single]$size, [single]$size),
        [System.Drawing.Color]::FromArgb(255, 47, 124, 246),
        [System.Drawing.Color]::FromArgb(255, 10, 74, 190))
    $g.FillPath($brush, $bg)

    # 磁盘: 三环 + 中心点（白色）
    $cx = [double]($size / 2)
    $cy = [double]($size * 0.475)
    $white = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
    $penOuter = [System.Drawing.Pen]::new($white, [single]($size * 0.055))
    $penInner = [System.Drawing.Pen]::new($white, [single]($size * 0.035))
    $penMid   = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(150, 255, 255, 255), [single]($size * 0.014))

    $rOuter = [double]($size * 0.285)
    $rMid   = [double]($size * 0.205)
    $rInner = [double]($size * 0.105)
    $g.DrawEllipse($penOuter, [single]($cx - $rOuter), [single]($cy - $rOuter), [single]($rOuter * 2), [single]($rOuter * 2))
    $g.DrawEllipse($penMid,   [single]($cx - $rMid),   [single]($cy - $rMid),   [single]($rMid * 2),   [single]($rMid * 2))
    $g.DrawEllipse($penInner, [single]($cx - $rInner), [single]($cy - $rInner), [single]($rInner * 2), [single]($rInner * 2))
    $g.FillEllipse([System.Drawing.Brushes]::White, [single]($cx - $size * 0.028), [single]($cy - $size * 0.028), [single]($size * 0.056), [single]($size * 0.056))

    # 右下角状态徽标: 绿色圆 + 白色对勾（表示"已优化"）
    $bx = [double]($size * 0.775)
    $by = [double]($size * 0.775)
    $br = [double]($size * 0.118)
    $g.FillEllipse([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 108, 203, 95)),
                   [single]($bx - $br), [single]($by - $br), [single]($br * 2), [single]($br * 2))
    $penCheck = [System.Drawing.Pen]::new([System.Drawing.Color]::White, [single]($size * 0.032))
    $penCheck.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $pts = @(
        [System.Drawing.PointF]::new([single]($bx - $br * 0.50), [single]($by + $br * 0.02)),
        [System.Drawing.PointF]::new([single]($bx - $br * 0.12), [single]($by + $br * 0.40)),
        [System.Drawing.PointF]::new([single]($bx + $br * 0.52), [single]($by - $br * 0.42))
    )
    $g.DrawLines($penCheck, $pts)

    $penOuter.Dispose(); $penInner.Dispose(); $penMid.Dispose(); $penCheck.Dispose()
    $brush.Dispose(); $bg.Dispose()
    $g.Dispose()
    return $bmp
}

# 母版 256，缩放到各尺寸（PNG 压缩帧）
$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$master = New-MasterBitmap 256
$frames = @()
foreach ($s in $sizes) {
    $bmp = [System.Drawing.Bitmap]::new($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.DrawImage($master, 0, 0, $s, $s)
    $g.Dispose()

    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += [pscustomobject]@{ Size = $s; Bytes = $ms.ToArray() }
    $ms.Dispose(); $bmp.Dispose()
}
$master.Dispose()

# 组装 ICO 容器
$ms = [System.IO.MemoryStream]::new()
$bw = [System.IO.BinaryWriter]::new($ms)
$bw.Write([UInt16]0)          # reserved
$bw.Write([UInt16]1)          # type = icon
$bw.Write([UInt16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $bw.Write([byte]$dim)     # width
    $bw.Write([byte]$dim)     # height
    $bw.Write([byte]0)        # color count
    $bw.Write([byte]0)        # reserved
    $bw.Write([UInt16]1)      # planes
    $bw.Write([UInt16]32)     # bit count
    $bw.Write([UInt32]$f.Bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $bw.Write($f.Bytes) }
$bw.Flush()
[IO.File]::WriteAllBytes($outFile, $ms.ToArray())
$bw.Dispose(); $ms.Dispose()

Write-Host ("已生成 {0} ({1} 个尺寸, {2:N1} KB)" -f $outFile, $frames.Count, ((Get-Item $outFile).Length / 1KB))

# 导出 256/32 预览图，便于目视检查
$previewMaster = New-MasterBitmap 256
$previewMaster.Save((Join-Path $outDir 'icon-preview-256.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$previewMaster.Dispose()
