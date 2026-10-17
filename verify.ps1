# 验证已构建的指定配置，不隐式重新生成；图库只读，报告/截图写入 verification。
# Native 和托管验证均必须成功；原始退出码非零时立即停止。
param([string]$GalleryRoot = (Split-Path $PSScriptRoot -Parent), [ValidateSet('Release','Debug')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$nativeTest = Join-Path $PSScriptRoot "build\msbuild\tests\$Configuration\native_tests.exe"
$managedTest = Join-Path $PSScriptRoot "build\verification\$Configuration\Verification.exe"
if (-not (Test-Path -LiteralPath $managedTest)) { throw '请先运行 build.ps1。' }
& $nativeTest
if ($LASTEXITCODE -ne 0) { throw 'Native regression failed.' }
& $managedTest $GalleryRoot
if ($LASTEXITCODE -ne 0) { throw 'Gallery / WPF verification failed.' }
