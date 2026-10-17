# 用 PowerShell token 计算缩进；保留 Here-String/多行字面量，改写后核对 token 等价。
# 写回 UTF-8 BOM，使 Windows PowerShell 5.1 正确读取中文；-Check 不写回。
param([switch]$Check)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$files = @(Get-ChildItem -LiteralPath $projectRoot -Filter '*.ps1' -File) + @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File)
$changed = 0
foreach ($file in $files) {
    $text = [IO.File]::ReadAllText($file.FullName)
    $tokens = $null
    $errors = $null
    [void][Management.Automation.Language.Parser]::ParseInput($text, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw "PowerShell syntax error: $($file.Name): $($errors[0].Message)" }
    $lines = $text -split '\r?\n'
    $depth = 0
    $continuation = $false
    for ($line = 1; $line -le $lines.Count; $line++) {
        # Here-string、跨行字符串和块注释保留内容与缩进，不把示例/内嵌 C# 当 PowerShell。
        $literal = @($tokens | Where-Object {
            $_.Extent.StartLineNumber -lt $line -and $_.Extent.EndLineNumber -ge $line
        })
        if ($literal.Count) { continue }
        $onLine = @($tokens | Where-Object { $_.Extent.StartLineNumber -eq $line -and $_.Kind -ne 'NewLine' -and $_.Kind -ne 'EndOfInput' })
        $closing = 0
        foreach ($token in $onLine) {
            if ($token.Kind -in @('RCurly', 'RParen')) { $closing++ } else { break }
        }
        $content = $lines[$line - 1].Trim()
        $level = [Math]::Max(0, $depth - $closing)
        if ($continuation -and $closing -eq 0) { $level++ }
        $lines[$line - 1] = if ($content) { (' ' * (4 * $level)) + $content } else { '' }
        foreach ($token in $onLine) {
            if ($token.Kind -in @('LCurly', 'AtCurly', 'LParen', 'AtParen', 'DollarParen')) { $depth++ }
            if ($token.Kind -in @('RCurly', 'RParen')) { $depth-- }
        }
        $continuation = $content.EndsWith('|') -or $content.EndsWith('`')
    }
    $formatted = ($lines -join "`r`n").TrimEnd() + "`r`n"
    $newTokens = $null
    $newErrors = $null
    [void][Management.Automation.Language.Parser]::ParseInput($formatted, [ref]$newTokens, [ref]$newErrors)
    if ($newErrors.Count) { throw "Formatter produced invalid PowerShell: $($file.Name)" }
    # 校验非换行 token 的种类和文本，防止整理破坏转义、命令参数或字符串。
    $oldValues = @($tokens | Where-Object Kind -ne 'NewLine' | ForEach-Object { "$($_.Kind):$($_.Text -replace '\r\n', "`n")" })
    $newValues = @($newTokens | Where-Object Kind -ne 'NewLine' | ForEach-Object { "$($_.Kind):$($_.Text -replace '\r\n', "`n")" })
    if (($oldValues -join "`0") -cne ($newValues -join "`0")) { throw "Formatter changed script tokens: $($file.Name)" }
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191
    if ($formatted -ceq $text -and $hasBom) { continue }
    $changed++
    Write-Host "PowerShell layout: $($file.Name)"
    if (!$Check) { [IO.File]::WriteAllText($file.FullName, $formatted, [Text.UTF8Encoding]::new($true)) }
}
if ($Check -and $changed -gt 0) { throw "$changed PowerShell files need formatting." }
Write-Host "PowerShell syntax/layout checked: $($files.Count) files."
