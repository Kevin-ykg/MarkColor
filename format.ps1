# 自有源码格式入口：默认改写；-Check 只验证，不更新被检查的源码。
# 各语言工具分别校验语法/布局；第三方 SDK 和编译生成目录不在整理范围内。
param([switch]$Check)

$ErrorActionPreference = 'Stop'
$demoRoot = $PSScriptRoot
$formatterCommand = Get-Command clang-format -ErrorAction SilentlyContinue
if ($formatterCommand) {
    $formatterPath = $formatterCommand.Source
} else {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'clang-format not found. Install the Visual Studio C++ tools or add clang-format to Path.' }
    $vsRoot = & $vswhere -latest -products '*' -property installationPath
    $formatterPath = Join-Path $vsRoot 'VC\Tools\Llvm\x64\bin\clang-format.exe'
}
if (-not (Test-Path -LiteralPath $formatterPath)) { throw "clang-format not found: $formatterPath" }

$nativeSources = @(Get-ChildItem -LiteralPath (Join-Path $demoRoot 'native') -Recurse -File |
    Where-Object { $_.Extension -in @('.cpp', '.h') } | Select-Object -ExpandProperty FullName)
$nativeArguments = @('--style=file')
if ($Check) { $nativeArguments += @('--dry-run', '--Werror') } else { $nativeArguments += '-i' }
& $formatterPath @nativeArguments @nativeSources
if ($LASTEXITCODE -ne 0) { throw 'C++ formatting check failed.' }

# Roslyn AST expands control blocks and separates members; whitespace-only formatting missed these rules.
$managedArguments = @('run', '--project', (Join-Path $demoRoot 'build-support\SourceFormatter'), '--', $demoRoot)
if ($Check) { $managedArguments += '--check' }
& dotnet @managedArguments
if ($LASTEXITCODE -ne 0) { throw 'C# formatting check failed.' }
& (Join-Path $demoRoot 'build-support\format-xml.ps1') -Check:$Check
& (Join-Path $demoRoot 'build-support\format-scripts.ps1') -Check:$Check
Write-Host 'C++, C#, XML and PowerShell formatting completed.'
