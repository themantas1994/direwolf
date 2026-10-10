<#
.SYNOPSIS
  Builds the portable ZIP of Dire Wolf Station.

.DESCRIPTION
  Publishes the WPF application for win-x64 and zips it into windows-gui/dist.
  -SelfContained (default) bundles the .NET runtime so nothing has to be installed;
  use -SelfContained:$false for a small ZIP that needs the .NET 10 Desktop Runtime.
  -DireWolfDir adds direwolf.exe (and its data files) from a Dire Wolf build so the ZIP
  is a complete station; without it the user points the GUI at their own direwolf.exe.

.EXAMPLE
  ./windows-gui/scripts/package.ps1 -DireWolfDir build/src
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [bool]$SelfContained = $true,
    [string]$DireWolfDir = ""
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$repo = Resolve-Path (Join-Path $root "..")
$proj = Join-Path $root "src/DireWolfGui/DireWolfGui.csproj"
[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$version = $props.Project.PropertyGroup.Version
$name = "DireWolfStation-$version-$Runtime" + ($(if ($SelfContained) { "" } else { "-fdd" }))
$stage = Join-Path $root "out/$name"
$dist = Join-Path $root "dist"

if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage, $dist | Out-Null

dotnet publish $proj -c $Configuration -r $Runtime --self-contained:$SelfContained `
    -p:PublishSingleFile=$SelfContained -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Copy-Item (Join-Path $repo "LICENSE") (Join-Path $stage "LICENSE.txt")
Copy-Item (Join-Path $root "docs/USER_GUIDE.md") $stage -ErrorAction SilentlyContinue
Copy-Item (Join-Path $root "THIRD_PARTY_NOTICES.md") $stage -ErrorAction SilentlyContinue

if ($DireWolfDir -ne "") {
    $dw = Join-Path $stage "direwolf"
    New-Item -ItemType Directory -Force $dw | Out-Null
    foreach ($f in @("direwolf.exe", "gen_packets.exe", "atest.exe", "kissutil.exe", "decode_aprs.exe")) {
        $p = Join-Path $DireWolfDir $f
        if (Test-Path $p) { Copy-Item $p $dw }
    }
    foreach ($f in @("tocalls.yaml", "symbols-new.txt", "symbolsX.txt")) {
        Copy-Item (Join-Path $repo "data/$f") $dw
    }
    Copy-Item (Join-Path $repo "conf/generic.conf") (Join-Path $dw "direwolf-sample.conf") -ErrorAction SilentlyContinue
    Get-ChildItem $DireWolfDir -Filter "*.dll" | Copy-Item -Destination $dw
}

$zip = Join-Path $dist "$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
Write-Host "Created $zip"
