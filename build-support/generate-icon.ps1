# 从同一矢量源生成 ICO；无需额外图像库或字体。
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$assetRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\MarkColorDemo\Assets'
$stream = [IO.File]::OpenRead((Join-Path $assetRoot 'CmykIcon.xaml'))
try {
    $resources = [Windows.Markup.XamlReader]::Load($stream)
} finally {
    $stream.Dispose()
}
$drawing = $resources['CmykAppMark']
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()
foreach ($size in $sizes) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    $context.DrawImage($drawing, [Windows.Rect]::new(0, 0, $size, $size))
    $context.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $buffer = [IO.MemoryStream]::new()
    try {
        $encoder.Save($buffer)
        $frames += ,$buffer.ToArray()
    } finally {
        $buffer.Dispose()
    }
}
# ICO 目录使用小端数值；256 像素以字节 0 表示，每项指向一帧 PNG。
$output = [IO.File]::Create((Join-Path $assetRoot 'CmykMark.ico'))
$writer = [IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    for ($index = 0; $index -lt $frames.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) {
        $writer.Write([byte[]]$frame)
    }
} finally {
    $writer.Dispose()
}
[IO.File]::WriteAllBytes((Join-Path $assetRoot 'CmykMark.png'), $frames[-1])
Write-Host "CMYK icon generated: $($sizes -join ', ') px."
