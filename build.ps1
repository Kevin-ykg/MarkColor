# 构建入口：VS MSBuild 编译统一解决方案 → Native 回归 → 可选发布 WPF。
# OpenCvDir 可覆盖 SDK 路径；配置保持 x64，DLL/PDB 的复制规则由工程 targets 负责。
param(
    [string]$OpenCvDir = $env:OpenCV_DIR,
    [ValidateSet('Release','Debug')][string]$Configuration = 'Release',
    [switch]$NoPublish
)
$ErrorActionPreference = 'Stop'
$demoRoot = $PSScriptRoot
# 子进程统一使用 Path 环境变量名，避免同时存在 PATH/Path 时旧 MSBuild 报重复键。
function Invoke-BuildTool([string]$File, [string[]]$ToolArgs) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $File
    $startInfo.UseShellExecute = $false
    if ($startInfo.PSObject.Properties.Name -contains 'ArgumentList') {
        foreach ($item in $ToolArgs) { [void]$startInfo.ArgumentList.Add($item) }
    } else {
        # Windows PowerShell 5.1 没有 ArgumentList，按 Windows CRT 规则转义参数。
        $quotedArgs = foreach ($item in $ToolArgs) { '"' + ([regex]::Replace($item, '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"' }
        $startInfo.Arguments = $quotedArgs -join ' '
    }
    if ($startInfo.PSObject.Properties.Name -contains 'Environment') { $childEnvironment = $startInfo.Environment }
    else { $childEnvironment = $startInfo.EnvironmentVariables }
    $toolSearchPath = $childEnvironment['PATH']
    [void]$childEnvironment.Remove('PATH')
    $childEnvironment['Path'] = $toolSearchPath
    $process = [System.Diagnostics.Process]::Start($startInfo)
    try { $process.WaitForExit(); if ($process.ExitCode -ne 0) { throw "$File failed with exit code $($process.ExitCode)." } }
    finally { $process.Dispose() }
}

# 定位完整 VS MSBuild；dotnet build 不能代替这里的 C++ 项目构建。
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Installer / vswhere.exe not found.' }
$vsRoot = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsRoot) { throw 'Install Visual Studio with Desktop development with C++ and .NET desktop development.' }
$msbuild = Join-Path $vsRoot 'MSBuild\Current\Bin\amd64\MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuild)) { throw "Visual Studio MSBuild not found: $msbuild" }
$commonArgs = @('/nologo', '/nr:false', '/v:minimal', "/p:Configuration=$Configuration", '/p:Platform=x64', "/p:RestoreConfigFile=$(Join-Path $demoRoot 'NuGet.Config')")
if ($OpenCvDir) {
    $sdkDir = (Resolve-Path -LiteralPath $OpenCvDir).Path
    if (Test-Path -LiteralPath (Join-Path $sdkDir 'include\opencv2\core.hpp')) {
        $sdkLibDir = Get-ChildItem -Path (Join-Path $sdkDir 'x64\vc*\lib') -Directory | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    } else { $sdkLibDir = $sdkDir }
    if (-not $sdkLibDir) { throw 'Specify the OpenCV build root or x64/vc*/lib directory.' }
    $sdkRoot = Split-Path (Split-Path (Split-Path $sdkLibDir))
    $sdkBin = Join-Path (Split-Path $sdkLibDir) 'bin'
    $worldLibs = @(Get-ChildItem -LiteralPath $sdkLibDir -Filter 'opencv_world*.lib' | Where-Object BaseName -Match '^opencv_world[0-9]+$')
    if ($worldLibs.Count -ne 1) { throw 'Expected exactly one Release opencv_world*.lib in the SDK lib directory.' }
    $sdkVersion = $worldLibs[0].BaseName.Substring('opencv_world'.Length)
    $commonArgs += @("/p:OpenCvRoot=$sdkRoot", "/p:OpenCvLibDir=$sdkLibDir", "/p:OpenCvBinDir=$sdkBin", "/p:OpenCvVersion=$sdkVersion")
}
Write-Host "Building unified solution: $Configuration|x64"
Invoke-BuildTool $msbuild (@((Join-Path $demoRoot 'MarkColorDemo.sln'), '/restore', '/m', '/t:Build') + $commonArgs)
Invoke-BuildTool (Join-Path $demoRoot "build\msbuild\tests\$Configuration\native_tests.exe") @()
if (-not $NoPublish) {
    $publishDir = Join-Path $demoRoot 'publish'
    if ($Configuration -eq 'Debug') { $publishDir = Join-Path $publishDir 'Debug' }
    Invoke-BuildTool $msbuild (@((Join-Path $demoRoot 'src\MarkColorDemo\MarkColorDemo.csproj'), '/t:Publish', '/p:NoBuild=true', '/p:BuildProjectReferences=false', '/p:SelfContained=false', "/p:PublishDir=$publishDir\") + $commonArgs)
    if (Test-Path -LiteralPath (Join-Path $demoRoot 'licenses')) { Copy-Item -LiteralPath (Join-Path $demoRoot 'licenses') -Destination $publishDir -Recurse -Force }
    Write-Host "Published: $publishDir\MarkColorDemo.exe"
}
Write-Host "F5 output: $(Join-Path $demoRoot "src\MarkColorDemo\bin\$Configuration\net10.0-windows\MarkColorDemo.exe")"
