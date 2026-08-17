[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$sevenZipProject = Join-Path $projectRoot 'third_party\7zip\CPP\7zip\Bundles\Alone2'
$sevenZipExe = Join-Path $sevenZipProject 'b\g_x64\7zz.exe'
$releaseDir = Join-Path $projectRoot 'release\GPUZIP-win-x64'

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

Copy-Item (Join-Path $projectRoot 'third_party\7zip\DOC\license.txt') (Join-Path $projectRoot '7zip-license.txt') -Force

# Keep the .slnx for newer Visual Studio/MSBuild, but do not depend on its parser
# in the pinned .NET 8 release build. Restore the required projects directly.
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

$appExe = Join-Path $releaseDir 'GpuZip.App.exe'
$publishedSevenZip = Join-Path $releaseDir 'Tools\7zip\7zz.exe'
if (-not (Test-Path $appExe)) { throw "Published WPF executable was not found: $appExe" }
if (-not (Test-Path $publishedSevenZip)) { throw "Bundled 7-Zip executable is missing from publish output" }

& $sevenZipExe i | Select-Object -First 3
Get-ChildItem $releaseDir -File -Recurse | Measure-Object -Property Length -Sum | ForEach-Object {
    "Release files: $($_.Count); bytes: $($_.Sum)"
}
"GPUZIP WPF release: $releaseDir"
