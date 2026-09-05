<#
.SYNOPSIS
    Voice In アプリのアイコン (.ico) を生成する。

.DESCRIPTION
    マイク（音声入力）を想起させる図案を、16 / 32 / 48 / 256 px の
    複数解像度を含む単一の .ico ファイルとして生成する。

    各解像度は共通のパラメトリック描画関数で「その解像度ごとに」個別に
    描画している（256px で描いたものを単純に縮小しているのではない）。
    そのため 16px のような小さいサイズでも線が潰れにくい。

    配色はアプリ本体 (Ui/OverlayWindow.xaml.cs の SetState、Ui/TrayIcons.cs の
    GetColors) と揃えている:
      - 背景: Catppuccin Mocha の Base(#1e1e2e) → Crust(#11111b) グラデーション
      - 縁取り: Gemini プロバイダのアイドル時の青 #4285F4
                (Ui/TrayIcons.cs の "idle-gemini" ボーダー色と同じ RGB)
      - マイク本体: Catppuccin Mocha の Text(#cdd6f4)

    外部のアイコン素材は一切使用せず、すべて System.Drawing (GDI+) の
    図形描画 (円・弧・直線) のみで構成している。

.PARAMETER OutputPath
    出力する .ico のパス。既定値は Assets/VoiceIn.ico
    (VoiceIn.csproj の <ApplicationIcon> から参照される想定)。

.EXAMPLE
    pwsh -File .\Assets\icon\New-VoiceInIcon.ps1
    既定の Assets\VoiceIn.ico を再生成する。デザインを変更したくなったら
    このスクリプトの色・形状パラメータを編集して再実行すればよい。
#>

[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\VoiceIn.ico")
)

Add-Type -AssemblyName System.Drawing.Common

# ---- 配色 ----------------------------------------------------------------
# 背景グラデーション: Catppuccin Mocha Base -> Crust
$script:BgTop    = [System.Drawing.Color]::FromArgb(255, 30, 30, 46)   # #1e1e2e
$script:BgBottom = [System.Drawing.Color]::FromArgb(255, 17, 17, 27)   # #11111b
# 縁取り: Gemini アイドル時の青 (Ui/TrayIcons.cs の idle-gemini border と同じ RGB)
$script:RingColor = [System.Drawing.Color]::FromArgb(255, 66, 133, 244) # #4285F4
# マイク本体: Catppuccin Mocha Text
$script:MicColor = [System.Drawing.Color]::FromArgb(255, 205, 214, 244) # #cdd6f4

function New-IconFrame {
    <#
        指定した一辺のピクセル数 (Size) でマイクのアイコン図案を 1 枚描画し、
        Bitmap を返す。呼び出し側で Dispose すること。
    #>
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)

        # --- 背景円 (グラデーション) ---
        $margin = [Math]::Max(0.5, $Size * 0.04)
        $circleRect = New-Object System.Drawing.RectangleF($margin, $margin, ($Size - 2 * $margin), ($Size - 2 * $margin))
        $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $circleRect, $script:BgTop, $script:BgBottom, [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
        $g.FillEllipse($bgBrush, $circleRect)
        $bgBrush.Dispose()

        # --- 縁取りリング ---
        $ringWidth = [Math]::Max(1.0, $Size * 0.075)
        $ringRect = New-Object System.Drawing.RectangleF(
            ($circleRect.X + $ringWidth / 2), ($circleRect.Y + $ringWidth / 2),
            ($circleRect.Width - $ringWidth), ($circleRect.Height - $ringWidth))
        $ringPen = New-Object System.Drawing.Pen($script:RingColor, $ringWidth)
        $g.DrawEllipse($ringPen, $ringRect)
        $ringPen.Dispose()

        # --- マイク本体 (カプセル型ヘッド) ---
        $capW = $Size * 0.30
        $capH = $Size * 0.40
        $capX = ($Size - $capW) / 2.0
        $capY = $Size * 0.18
        $capRadius = $capW / 2.0
        $centerX = $Size / 2.0

        $micBrush = New-Object System.Drawing.SolidBrush($script:MicColor)

        $capPath = New-Object System.Drawing.Drawing2D.GraphicsPath
        $capPath.AddArc($capX, $capY, $capW, ($capRadius * 2), 180, 180)
        $capPath.AddArc($capX, ($capY + $capH - $capRadius * 2), $capW, ($capRadius * 2), 0, 180)
        $capPath.CloseFigure()
        $g.FillPath($micBrush, $capPath)
        $capPath.Dispose()

        # --- マイクスタンド (支柱 + 台座) ---
        # 16px のような極小サイズでは、古典的な U 字クリップ (細い弧) は
        # ダウンサンプル時に周囲と溶け合って潰れてしまう (実測して確認済み)。
        # そのため 16px 相当以下では直線的な塗りつぶし図形 (首 + 台座の矩形のみ) に
        # 単純化し、32px 以上では弧を使った本来のマイクスタンド (U字クリップ) を描く。
        if ($Size -le 20) {
            $neckW = [Math]::Max(1.6, $Size * 0.10)
            $neckTop = $capY + $capH - $Size * 0.02
            $neckBottom = $Size * 0.82
            $neckRect = New-Object System.Drawing.RectangleF(
                ($centerX - $neckW / 2.0), $neckTop, $neckW, ($neckBottom - $neckTop))
            $g.FillRectangle($micBrush, $neckRect)

            $baseW = $Size * 0.34
            $baseH = [Math]::Max(1.4, $Size * 0.09)
            $baseRect = New-Object System.Drawing.RectangleF(
                ($centerX - $baseW / 2.0), ($neckBottom - $baseH), $baseW, $baseH)
            $g.FillRectangle($micBrush, $baseRect)
        }
        else {
            $standWidth = $Size * 0.085
            $standPen = New-Object System.Drawing.Pen($script:MicColor, $standWidth)
            $standPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $standPen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round

            $standRect = New-Object System.Drawing.RectangleF(
                ($capX - $Size * 0.07), ($capY + $capH * 0.32),
                ($capW + $Size * 0.14), ($capH * 0.95))
            $g.DrawArc($standPen, $standRect, 20, 140)

            $stemTopY = $standRect.Y + $standRect.Height * 0.90
            $stemBottomY = $Size * 0.83
            $g.DrawLine($standPen, $centerX, $stemTopY, $centerX, $stemBottomY)

            $baseHalfWidth = $Size * 0.14
            $g.DrawLine($standPen, ($centerX - $baseHalfWidth), $stemBottomY, ($centerX + $baseHalfWidth), $stemBottomY)

            $standPen.Dispose()
        }

        $micBrush.Dispose()

        return $bmp
    } finally {
        $g.Dispose()
    }
}

function ConvertTo-PngBytes {
    param([System.Drawing.Bitmap]$Bitmap)
    $ms = New-Object System.IO.MemoryStream
    $Bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return , $bytes
}

# ---- 各解像度を描画 --------------------------------------------------------
# 16px: タスクバー・エクスプローラの小アイコン表示で使われる。
# 32px: デスクトップアイコン・Alt+Tab 等。
# 48px: 大アイコン表示。
# 256px: エクスプローラの特大アイコン・インストーラーのショートカット等。
$sizes = @(16, 32, 48, 256)
$frames = @()
foreach ($size in $sizes) {
    Write-Host "Rendering ${size}x${size} frame..."
    $bmp = New-IconFrame -Size $size
    $pngBytes = ConvertTo-PngBytes -Bitmap $bmp
    $frames += [PSCustomObject]@{ Size = $size; Bytes = $pngBytes }
    $bmp.Dispose()
}

# ---- ICO コンテナ組み立て --------------------------------------------------
# ICO ファイルは ICONDIR ヘッダ + ICONDIRENTRY の配列 + 各フレームの画像データ
# から成る。各フレームの画像データは Windows Vista 以降でサポートされる
# PNG 形式で格納する (256px を無圧縮 BMP DIB で持つと巨大になるため、
# 全フレームを PNG に統一している)。
$outStream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($outStream)

# ICONDIR (6 bytes)
$writer.Write([UInt16]0)              # reserved, must be 0
$writer.Write([UInt16]1)              # image type: 1 = icon
$writer.Write([UInt16]$frames.Count)  # number of images

$headerSize = 6 + 16 * $frames.Count
$offset = $headerSize
foreach ($f in $frames) {
    # 256px は ICO 仕様上 1 バイトに収まらないため 0 で表す。
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $writer.Write([Byte]$dim)               # width
    $writer.Write([Byte]$dim)               # height
    $writer.Write([Byte]0)                  # color count (0 = >=8bpp)
    $writer.Write([Byte]0)                  # reserved
    $writer.Write([UInt16]1)                # color planes
    $writer.Write([UInt16]32)               # bits per pixel
    $writer.Write([UInt32]$f.Bytes.Length)  # size of image data
    $writer.Write([UInt32]$offset)          # offset of image data
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) {
    $writer.Write($f.Bytes)
}
$writer.Flush()

$outDir = Split-Path -Parent $OutputPath
if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}
[System.IO.File]::WriteAllBytes($OutputPath, $outStream.ToArray())
$writer.Dispose()
$outStream.Dispose()

$finalSize = (Get-Item $OutputPath).Length
Write-Host "Icon written to: $OutputPath ($finalSize bytes, $($frames.Count) frames: $($sizes -join ', '))"
