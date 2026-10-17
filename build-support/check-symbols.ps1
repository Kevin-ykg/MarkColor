# 调试检查：先核对 Native 与 WPF 目录 DLL/PDB 哈希，再通过 DbgHelp 解析源码行。
# 验证“符号与二进制匹配且含行号”，不启动 VS，也不修改工程调试配置。
param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$demoRoot = Split-Path $PSScriptRoot -Parent
$nativeDir = Join-Path $demoRoot "build\msbuild\native\$Configuration"
$wpfDir = Join-Path $demoRoot "src\MarkColorDemo\bin\$Configuration\net10.0-windows"
foreach ($name in @('MarkColorNative.dll', 'MarkColorNative.pdb')) {
    $source = Join-Path $nativeDir $name
    $deployed = Join-Path $wpfDir $name
    if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $deployed).Hash) { throw "Native/WPF copy mismatch: $name" }
}
if (-not ('MarkColorSymbolProbe' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'SymbolProbe.cs')
}
[MarkColorSymbolProbe]::Verify((Join-Path $wpfDir 'MarkColorNative.dll'))
