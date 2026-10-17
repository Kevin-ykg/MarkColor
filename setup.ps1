# 环境准备：Check 只检查；Development 准备 SDK/开发工具并构建；Run 准备运行环境。
# 下载文件在执行前校验固定摘要或发布者签名；默认目录位于项目内，避免依赖个人路径。
param(
    [ValidateSet('Check','Run','Development')][string]$Mode = 'Check',
    [ValidateSet('Community','Professional','Enterprise')][string]$VisualStudioEdition = 'Community',
    [string]$OpenCvSource,
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$projectRoot = $PSScriptRoot
$dependencies = Join-Path $projectRoot 'dependencies'
$downloads = Join-Path $dependencies 'downloads'
$sdk = Join-Path $dependencies 'opencv\build'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'

# 缓存只在摘要匹配时复用；缺失或不匹配时重新下载并复核。
function Get-VerifiedFile([string]$Url, [string]$Path, [string]$Hash, [string]$Algorithm='SHA512') {
    New-Item -ItemType Directory -Path (Split-Path $Path) -Force | Out-Null
    if (!(Test-Path -LiteralPath $Path) -or (Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash -ne $Hash) {
        Write-Host "Downloading $Url"
        Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $Path
    }
    if ((Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash -ne $Hash) { throw "Checksum mismatch: $Path" }
}

# 私有安装不改系统 PATH；SDK 模式供开发，Desktop Runtime 模式供运行。
function Install-DotNet([string]$Destination, [switch]$Sdk) {
    $metadata = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
    $release = $metadata.releases | Where-Object { $_.'release-version' -eq $metadata.'latest-release' } | Select-Object -First 1
    $parts = @('runtime','windowsdesktop'); if ($Sdk) { $parts = @('sdk') }
    foreach ($part in $parts) {
        $file = $release.$part.files | Where-Object { $_.rid -eq 'win-x64' -and $_.url.EndsWith('.zip') } | Select-Object -First 1
        if (!$file) { throw "Official .NET metadata does not contain $part win-x64 zip." }
        $archive = Join-Path $downloads ([IO.Path]::GetFileName($file.url))
        Get-VerifiedFile $file.url $archive $file.hash
        Expand-Archive -LiteralPath $archive -DestinationPath $Destination -Force
    }
}

# 需要同时具备 x64 C++ 工具和 MSBuild 的受支持 VS 实例。
function Find-VisualStudio {
    if (Test-Path -LiteralPath $vswhere) {
        & $vswhere -latest -version '[18.0,19.0)' -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 Microsoft.Component.MSBuild -property installationPath
    }
}

if (![Environment]::Is64BitOperatingSystem) { throw 'Windows x64 is required.' }
if ($Mode -eq 'Check') {
    Write-Host "Project: $projectRoot"
    Write-Host "OpenCV SDK ready: $(Test-Path -LiteralPath (Join-Path $sdk 'include\opencv2\core.hpp'))"
    Write-Host "Visual Studio 2026 C++: $(Find-VisualStudio)"
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnet) { & $dotnet.Source --list-runtimes; & $dotnet.Source --list-sdks }
    Write-Host "Private runtime ready: $(Test-Path -LiteralPath (Join-Path $projectRoot 'publish\runtime\dotnet.exe'))"
    return
}

if ($Mode -eq 'Development') {
    if (!(Test-Path -LiteralPath (Join-Path $sdk 'include\opencv2\core.hpp'))) {
        if ($OpenCvSource) {
            $source = (Resolve-Path -LiteralPath $OpenCvSource).Path
            if (!(Test-Path -LiteralPath (Join-Path $source 'include\opencv2\core.hpp'))) { throw 'OpenCvSource must be the OpenCV build folder.' }
            New-Item -ItemType Directory -Path (Split-Path $sdk) -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $sdk -Recurse -Force
        } else {
            $archive = Join-Path $downloads 'opencv-4.5.4-vc14_vc15.exe'
            Get-VerifiedFile 'https://github.com/opencv/opencv/releases/download/4.5.4/opencv-4.5.4-vc14_vc15.exe' $archive 'd49f6a8ef304de4f5617baf8d9ece51b53a76b3cf5ce26377e4ed7632f0ac467' 'SHA256'
            $extract = Start-Process -FilePath $archive -ArgumentList @('-y', ('-o"' + $dependencies + '"')) -WindowStyle Hidden -Wait -PassThru
            if ($extract.ExitCode -ne 0) { throw "OpenCV extraction failed: $($extract.ExitCode)" }
        }
    }
    if (!(Find-VisualStudio)) {
        New-Item -ItemType Directory -Path $downloads -Force | Out-Null
        $installer = Join-Path $downloads 'vs_setup.exe'
        Invoke-WebRequest -UseBasicParsing -Uri "https://aka.ms/vs/18/release/vs_$($VisualStudioEdition.ToLowerInvariant()).exe" -OutFile $installer
        $signature = Get-AuthenticodeSignature -LiteralPath $installer
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw 'Visual Studio installer signature is invalid.' }
        Write-Host 'Installing Visual Studio C++ and .NET desktop workloads. Windows may request administrator permission.'
        $process = Start-Process -FilePath $installer -ArgumentList '--passive --wait --norestart --includeRecommended --add Microsoft.VisualStudio.Workload.NativeDesktop --add Microsoft.VisualStudio.Workload.ManagedDesktop' -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -notin @(0,3010)) { throw "Visual Studio setup failed: $($process.ExitCode)" }
        if ($process.ExitCode -eq 3010) { Write-Host 'Restart Windows, then run setup-dev.cmd again.'; return }
    }
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (!$dotnet -or !(& $dotnet.Source --list-sdks | Where-Object { $_ -match '^10\.' })) {
        $localDotNet = Join-Path $dependencies 'dotnet'
        Install-DotNet $localDotNet -Sdk
        $env:DOTNET_ROOT = $localDotNet
        $env:DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR = $localDotNet
        $env:Path = "$localDotNet;$env:Path"
    }
    & (Join-Path $projectRoot 'build.ps1') -Configuration Release -OpenCvDir $sdk
    Write-Host 'Development environment ready. Open MarkColorDemo.sln, x64, Debug, WPF startup project.'
    return
}

$app = Join-Path $projectRoot 'publish'
if (!(Test-Path -LiteralPath (Join-Path $app 'MarkColorDemo.dll'))) { throw 'Published application missing. Run setup-dev.cmd or copy a complete portable package.' }
$runtime = Join-Path $app 'runtime'
if (!(Test-Path -LiteralPath (Join-Path $runtime 'dotnet.exe'))) { Install-DotNet $runtime }
if (!(Test-Path -LiteralPath (Join-Path $app 'vcruntime140.dll'))) {
    New-Item -ItemType Directory -Path $downloads -Force | Out-Null
    $redist = Join-Path $downloads 'vc_redist.x64.exe'
    Invoke-WebRequest -UseBasicParsing -Uri 'https://aka.ms/vc14/vc_redist.x64.exe' -OutFile $redist
    $signature = Get-AuthenticodeSignature -LiteralPath $redist
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw 'VC runtime installer signature invalid.' }
    $process = Start-Process -FilePath $redist -ArgumentList '/install /passive /norestart' -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -notin @(0,3010,1638)) { throw "VC runtime installation failed: $($process.ExitCode)" }
}
if (!$NoLaunch) { & (Join-Path $runtime 'dotnet.exe') (Join-Path $app 'MarkColorDemo.dll') }
