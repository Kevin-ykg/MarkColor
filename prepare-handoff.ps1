<#
.SYNOPSIS
生成含私有运行库、许可证和文件清单的便携交付目录。
.PARAMETER SkipBuild
直接使用已有 publish；调用者须先确认该目录是期望交付的 Release 版本。
.PARAMETER OpenCvSource
项目缺少 OpenCV 时使用的离线 build 目录。输出保存在 dist 的独立时间戳目录。
#>
# 交付入口：打包 Release 程序、私有 .NET/VC 运行库和许可，输出带时间戳的独立目录。
# SkipBuild 表示复用已验证的 publish；最后记录逐文件 SHA-256，便于核对完整复制。
param([string]$OpenCvSource, [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
# SDK 复制到工程相对路径，避免同事电脑依赖开发者的个人盘符。
$sdk = Join-Path $projectRoot 'dependencies\opencv\build'
if ($OpenCvSource -and !(Test-Path -LiteralPath (Join-Path $sdk 'include\opencv2\core.hpp'))) {
    $source = (Resolve-Path -LiteralPath $OpenCvSource).Path
    New-Item -ItemType Directory -Path (Split-Path $sdk) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $sdk -Recurse -Force
}
if (!$SkipBuild) { & (Join-Path $projectRoot 'build.ps1') -Configuration Release }
$output = Join-Path $projectRoot ('dist\MarkColorDemo-portable-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $output | Out-Null
Get-ChildItem -LiteralPath (Join-Path $projectRoot 'publish') -File | Copy-Item -Destination $output
Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination $output -Recurse
$localSdk = Join-Path $projectRoot 'dependencies\dotnet'
$dotnetRoot = if (Test-Path -LiteralPath (Join-Path $localSdk 'dotnet.exe')) { $localSdk } else { Split-Path (Get-Command dotnet).Source }
$runtime = Join-Path $output 'runtime'
New-Item -ItemType Directory -Path $runtime | Out-Null
Copy-Item -LiteralPath (Join-Path $dotnetRoot 'dotnet.exe') -Destination $runtime
foreach ($part in @('host\fxr','shared\Microsoft.NETCore.App','shared\Microsoft.WindowsDesktop.App')) {
    $version = Get-ChildItem -LiteralPath (Join-Path $dotnetRoot $part) -Directory | Where-Object Name -Match '^10\.0\.\d+$' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if (!$version) { throw "Installed .NET 10 component missing: $part" }
    $target = Join-Path $runtime $part
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item -LiteralPath $version.FullName -Destination $target -Recurse
}
foreach ($name in @('LICENSE.txt','ThirdPartyNotices.txt')) {
    if (Test-Path -LiteralPath (Join-Path $dotnetRoot $name)) { Copy-Item -LiteralPath (Join-Path $dotnetRoot $name) -Destination (Join-Path $output 'licenses') }
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsRoot = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$crt = Get-ChildItem -Path (Join-Path $vsRoot 'VC\Redist\MSVC\*\x64\Microsoft.VC*.CRT') -Directory | Sort-Object FullName -Descending | Select-Object -First 1
if (!$crt) { throw 'Visual C++ redistributable CRT folder not found.' }
Get-ChildItem -LiteralPath $crt.FullName -Filter '*.dll' | Copy-Item -Destination $output
Get-ChildItem -LiteralPath $crt.FullName -Filter '*.dll' | Copy-Item -Destination $runtime
$launch = '@echo off' + "`r`n" + 'set DOTNET_MULTILEVEL_LOOKUP=0' + "`r`n" + '"%~dp0runtime\dotnet.exe" "%~dp0MarkColorDemo.dll" %*' + "`r`n" + 'if errorlevel 1 pause'
[IO.File]::WriteAllText((Join-Path $output 'run.cmd'), $launch, [Text.Encoding]::ASCII)
# 同步私有运行库到 publish，项目根目录的启动脚本也能离线运行。
Copy-Item -LiteralPath $runtime -Destination (Join-Path $projectRoot 'publish') -Recurse -Force
Get-ChildItem -LiteralPath $crt.FullName -Filter '*.dll' | Copy-Item -Destination (Join-Path $projectRoot 'publish')
# 文档位于 docs；保留目录层级，避免复制到根目录后破坏文档间相对链接。
$documentation = Join-Path $projectRoot 'docs'
if (Test-Path -LiteralPath $documentation) {
    $documentOutput = Join-Path $output 'docs'
    New-Item -ItemType Directory -Path $documentOutput -Force | Out-Null
    Get-ChildItem -LiteralPath $documentation -Filter '*.md' -File | Copy-Item -Destination $documentOutput
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $output
# README 的界面预览沿用相对路径；交付包无需携带其他本机验证产物。
$previewImage = Join-Path $projectRoot 'verification\demo-preview.png'
if (Test-Path -LiteralPath $previewImage) {
    $previewOutput = Join-Path $output 'verification'
    New-Item -ItemType Directory -Path $previewOutput -Force | Out-Null
    Copy-Item -LiteralPath $previewImage -Destination $previewOutput
}
$manifest = Get-ChildItem -LiteralPath $output -Recurse -File | ForEach-Object {
    [PSCustomObject]@{ Path=$_.FullName.Substring($output.Length+1); Bytes=$_.Length; SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'manifest.json') -Encoding UTF8
Write-Host "Portable folder (copy the entire folder): $output"
Write-Output $output
