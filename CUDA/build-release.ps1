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

& dotnet restore (Join-Path $projectRoot 'GpuZip.slnx') --source https://api.nuget.org/v3/index.json
if ($LASTEXITCODE -ne 0) { throw "Restore failed" }

& dotnet build (Join-Path $projectRoot 'src\GpuZip.Core\GpuZip.Core.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw "Core build failed" }
& dotnet run --project (Join-Path $projectRoot 'tests\GpuZip.SelfTest\GpuZip.SelfTest.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw "Self-test failed" }

if (Test-Path $releaseDir) { Remove-Item -LiteralPath $releaseDir -Recurse -Force }
& dotnet publish (Join-Path $projectRoot 'src\GpuZip.App\GpuZip.App.csproj') -c Release -r win-x64 --self-contained true -p:Platform=x64 -o $releaseDir --source https://api.nuget.org/v3/index.json
if ($LASTEXITCODE -ne 0) { throw "WinUI publish failed" }

& $sevenZipExe i | Select-Object -First 3
Get-ChildItem $releaseDir -File | Measure-Object -Property Length -Sum | ForEach-Object {
    "Release files: $($_.Count); bytes: $($_.Sum)"
}
"GPUZIP release: $releaseDir"
