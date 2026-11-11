# Builds a release anyone can unzip and run: the app, the .NET and Windows App
# SDK runtimes, and the map data of every game, all in one folder.
#
#   powershell -ExecutionPolicy Bypass -File make-release.ps1 [-Version 1.0.0]
#
# Output: release\InteractiveMap\InteractiveMap.exe
#         release\InteractiveMap-<version>-win-x64.zip
param(
    [string]$Version = "1.0.0",
    # the website checkout that holds the generated map data
    [string]$Maps = (Join-Path (Split-Path $PSScriptRoot -Parent) "interactive-map\pages\gacha games\maps")
)
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$out = Join-Path $here "release\InteractiveMap"
$zip = Join-Path $here "release\InteractiveMap-$Version-win-x64.zip"

if (-not (Test-Path $Maps)) { throw "Map data not found: $Maps" }
Get-Process InteractiveMap -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$out*" } | Stop-Process -Force
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

# 1. the app
dotnet publish (Join-Path $here "InteractiveMap.csproj") -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -o $out -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# 2. the map data: only what the client reads, not the web pages or the extraction tools
$parts = @(
    @("Wuthering Waves", "js", "data.js"),
    @("Wuthering Waves", "tiles", "*"),
    @("Wuthering Waves", "assets\icons", "*"),
    @("Neverness to Everness", ".", "mapdata.js"),
    @("Neverness to Everness", "assets\map\tiles", "*"),
    @("Neverness to Everness", "assets\mapicons", "*"),
    @("Neverness to Everness", "assets\anomalies", "*"),
    @("Neverness to Everness", "assets\items", "*")
)
foreach ($p in $parts) {
    $src = Join-Path (Join-Path $Maps $p[0]) $p[1]
    $dst = Join-Path (Join-Path (Join-Path $out "maps") $p[0]) $p[1]
    if (-not (Test-Path $src)) { throw "Missing map data: $src" }
    $flags = @("/NFL", "/NDL", "/NJH", "/NJS", "/NP")
    if ($p[2] -eq "*") { $flags += "/E" }
    robocopy $src $dst $p[2] @flags | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copy failed: $src" }
}
$global:LASTEXITCODE = 0

# 3. one zip
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($out, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)

$files = Get-ChildItem $out -Recurse -File
"folder: $out  ({0:N0} MB, {1} files)" -f (($files | Measure-Object Length -Sum).Sum / 1MB), $files.Count
"zip:    $zip  ({0:N0} MB)" -f ((Get-Item $zip).Length / 1MB)
