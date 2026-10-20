# 解析并格式化 XAML/MSBuild XML；只处理自有源码路径，保留属性值和 XML 注释。
# 两空格缩进、属性逐行；-Check 在有差异时失败，不写回被检查文件。
param([switch]$Check)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$roots = @('src', 'native', 'tests', 'build-support') | ForEach-Object { Join-Path $projectRoot $_ }
$extensions = @('.xaml', '.csproj', '.vcxproj', '.filters', '.props', '.targets', '.config')
$files = @(Get-ChildItem -LiteralPath $roots -Recurse -File | Where-Object {
    $_.Extension -in $extensions -and $_.FullName -notmatch '\\(bin|obj)\\'
}) + @(Get-Item -LiteralPath (Join-Path $projectRoot 'NuGet.Config'))
$changed = 0
foreach ($file in $files) {
    $text = [IO.File]::ReadAllText($file.FullName)
    $document = [Xml.XmlDocument]::new()
    # 本项目 XML 无需保留混合文本空白；若以后加入依赖空格的文本节点，先扩展验证再使用此格式器。
    $document.PreserveWhitespace = $false
    $document.LoadXml($text)
    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Indent = $true
    $settings.IndentChars = '  '
    $settings.NewLineChars = "`r`n"
    $settings.NewLineOnAttributes = $true
    $settings.OmitXmlDeclaration = !$text.TrimStart().StartsWith('<?xml')
    $settings.Encoding = [Text.UTF8Encoding]::new($false)
    $buffer = [IO.MemoryStream]::new()
    $writer = [Xml.XmlWriter]::Create($buffer, $settings)
    try {
        $document.Save($writer)
        $writer.Flush()
        $formatted = [Text.Encoding]::UTF8.GetString($buffer.ToArray()).TrimEnd() + "`r`n"
    } finally {
        $writer.Dispose()
        $buffer.Dispose()
    }
    if ($formatted -ceq $text) { continue }
    $changed++
    Write-Host "XML layout: $($file.FullName.Substring($projectRoot.Length + 1))"
    if (!$Check) { [IO.File]::WriteAllText($file.FullName, $formatted, [Text.UTF8Encoding]::new($false)) }
}
if ($Check -and $changed -gt 0) { throw "$changed XML files need formatting." }
Write-Host "XML layout checked: $($files.Count) files."
