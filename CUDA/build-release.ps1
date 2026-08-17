[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$sevenZipProject = Join-Path $projectRoot 'third_party\7zip\CPP\7zip\Bundles\Alone2'
$sevenZipExe = Join-Path $sevenZipProject 'b\g_x64\7zz.exe'
$sevenZipFmProject = Join-Path $projectRoot 'third_party\7zip\CPP\7zip\Bundles\Fm'
$sevenZipFmExe = Join-Path $sevenZipFmProject 'x64\7zFM.exe'
$releaseDir = Join-Path $projectRoot 'release\GPUZIP-win-x64'

$preflateCommit = '3bcd33441849a8a25d2b26077128f4e3a88d071b'
$preflateSource = Join-Path $projectRoot '.build\preflate-rs'
$preflateDll = Join-Path $preflateSource 'target\release\preflate_rs_0_7.dll'
$preflateLicense = Join-Path $preflateSource 'LICENSE'

if (-not (Test-Path $sevenZipExe)) {
    Push-Location $sevenZipProject
    try {
        New-Item -ItemType Directory -Force -Path 'b\g_x64' | Out-Null
        $gcc = (Get-Command gcc.exe -ErrorAction Stop).Source
        & $gcc -c empty_resource.c -o b\g_x64\resource.o
        if ($LASTEXITCODE -ne 0) { throw "7-Zip resource stub build failed with exit code $LASTEXITCODE" }
        & mingw32-make.exe -j8 USE_ASM= CFLAGS_WARN_WALL=-Wall -f ..\..\cmpl_gcc_x64.mak
        if ($LASTEXITCODE -ne 0) { throw "7-Zip build failed with exit code $LASTEXITCODE" }
    }
    finally { Pop-Location }
}

if (-not (Test-Path $sevenZipFmExe)) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { throw "vswhere.exe was not found; cannot build 7zFM.exe" }
    $vsInstall = (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1)
    if (-not $vsInstall) { throw "Visual C++ build tools were not found; cannot build 7zFM.exe" }
    $vsDevCmd = Join-Path $vsInstall 'Common7\Tools\VsDevCmd.bat'
    if (-not (Test-Path $vsDevCmd)) { throw "VsDevCmd.bat was not found: $vsDevCmd" }
    $buildCommand = "call `"$vsDevCmd`" -no_logo -arch=x64 -host_arch=x64 && cd /d `"$sevenZipFmProject`" && nmake /nologo PLATFORM=x64"
    & $env:ComSpec /d /c $buildCommand
    if ($LASTEXITCODE -ne 0) { throw "7-Zip File Manager build failed with exit code $LASTEXITCODE" }
}
if (-not (Test-Path $sevenZipFmExe)) { throw "7zFM.exe was not produced: $sevenZipFmExe" }

# Build Microsoft's Apache-2.0 preflate-rs container codec from a pinned commit.
# It losslessly exposes DEFLATE streams inside ZIP/MSIX so stronger compression
# can be used while recreating the original container bytes exactly.
Get-Command git.exe -ErrorAction Stop | Out-Null
Get-Command cargo.exe -ErrorAction Stop | Out-Null
Write-Host "Rust: $(& rustc --version)"
Write-Host "Cargo: $(& cargo --version)"

if (-not (Test-Path (Join-Path $preflateSource '.git'))) {
    if (Test-Path $preflateSource) { Remove-Item $preflateSource -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Split-Path $preflateSource) | Out-Null
    & git clone --filter=blob:none --no-checkout https://github.com/microsoft/preflate-rs.git $preflateSource
    if ($LASTEXITCODE -ne 0) { throw "Could not clone microsoft/preflate-rs" }
}
& git -C $preflateSource fetch --depth 1 origin $preflateCommit
if ($LASTEXITCODE -ne 0) { throw "Could not fetch pinned preflate-rs commit" }
& git -C $preflateSource checkout --force --detach $preflateCommit
if ($LASTEXITCODE -ne 0) { throw "Could not checkout pinned preflate-rs commit" }
& cargo build --locked --manifest-path (Join-Path $preflateSource 'Cargo.toml') --release -p preflate_rs_0_7
if ($LASTEXITCODE -ne 0) { throw "preflate-rs DLL build failed" }
if (-not (Test-Path $preflateDll)) { throw "preflate_rs_0_7.dll was not produced: $preflateDll" }
if (-not (Test-Path $preflateLicense)) { throw "preflate-rs LICENSE was not found" }
$env:GPUZIP_PREFLATE_DLL = $preflateDll

Copy-Item (Join-Path $projectRoot 'third_party\7zip\DOC\license.txt') (Join-Path $projectRoot '7zip-license.txt') -Force

$coreProject = Join-Path $projectRoot 'src\GpuZip.Core\GpuZip.Core.csproj'
$selfTestProject = Join-Path $projectRoot 'tests\GpuZip.SelfTest\GpuZip.SelfTest.csproj'
$appProject = Join-Path $projectRoot 'src\GpuZip.App\GpuZip.App.csproj'

& dotnet restore $coreProject --source https://api.nuget.org/v3/index.json
if ($LASTEXITCODE -ne 0) { throw "Core restore failed" }
& dotnet restore $selfTestProject -r win-x64 --source https://api.nuget.org/v3/index.json
if ($LASTEXITCODE -ne 0) { throw "Self-test restore failed" }
& dotnet restore $appProject -r win-x64 --source https://api.nuget.org/v3/index.json
if ($LASTEXITCODE -ne 0) { throw "WPF app restore failed" }

& dotnet build $coreProject -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "Core build failed" }
& dotnet run --project $selfTestProject -c Release -r win-x64 --no-restore
if ($LASTEXITCODE -ne 0) { throw "Self-test failed" }

if (Test-Path $releaseDir) { Remove-Item -LiteralPath $releaseDir -Recurse -Force }
& dotnet publish $appProject -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:PublishReadyToRun=false --no-restore -o $releaseDir
if ($LASTEXITCODE -ne 0) { throw "WPF desktop publish failed" }

$publishedSevenZipDir = Join-Path $releaseDir 'Tools\7zip'
New-Item -ItemType Directory -Force -Path $publishedSevenZipDir | Out-Null
Copy-Item $sevenZipFmExe (Join-Path $publishedSevenZipDir '7zFM.exe') -Force

$publishedPreflateDir = Join-Path $releaseDir 'Tools\preflate'
New-Item -ItemType Directory -Force -Path $publishedPreflateDir | Out-Null
Copy-Item $preflateDll (Join-Path $publishedPreflateDir 'preflate_rs_0_7.dll') -Force
Copy-Item $preflateLicense (Join-Path $publishedPreflateDir 'LICENSE.txt') -Force

$appExe = Join-Path $releaseDir 'GpuZip.App.exe'
$publishedSevenZip = Join-Path $releaseDir 'Tools\7zip\7zz.exe'
$publishedSevenZipFm = Join-Path $releaseDir 'Tools\7zip\7zFM.exe'
$publishedPreflate = Join-Path $releaseDir 'Tools\preflate\preflate_rs_0_7.dll'
if (-not (Test-Path $appExe)) { throw "Published WPF executable was not found: $appExe" }
if (-not (Test-Path $publishedSevenZip)) { throw "Bundled 7-Zip executable is missing from publish output" }
if (-not (Test-Path $publishedSevenZipFm)) { throw "Bundled 7-Zip File Manager is missing from publish output" }
if (-not (Test-Path $publishedPreflate)) { throw "Bundled Preflate codec is missing from publish output" }

& $sevenZipExe i | Select-Object -First 3
"7-Zip File Manager: $sevenZipFmExe"
"Preflate codec: $preflateDll"
Get-ChildItem $releaseDir -File -Recurse | Measure-Object -Property Length -Sum | ForEach-Object {
    "Release files: $($_.Count); bytes: $($_.Sum)"
}
"GPUZIP WPF release: $releaseDir"
