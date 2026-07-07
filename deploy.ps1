$ErrorActionPreference = 'Stop'
# Path-agnostic: resolves relative to this script's folder, so it works before AND after the
# root folder is renamed to Multiplayer (only the folder name differs; the layout is identical).
$root = $PSScriptRoot
$proj = Join-Path $root "Multiplayer.csproj"
$out  = Join-Path $root "bin\Release"
$dest = "D:\Steam\steamapps\common\Phoenix Point\Mods\Multiplayer"
dotnet build $proj -c Release
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item "$out\Multiplayer.dll" $dest -Force
if (Test-Path "$out\Multiplayer.pdb") { Copy-Item "$out\Multiplayer.pdb" $dest -Force }
# Multiplayer.Core.dll is a SEPARATE referenced assembly (the pure BCL-only logic, extracted 7c35e27) — the mod
# assembly-references it, so PP's mod loader must resolve it from this same mod folder. Ship it (and its pdb).
Copy-Item "$out\Multiplayer.Core.dll" $dest -Force
if (Test-Path "$out\Multiplayer.Core.pdb") { Copy-Item "$out\Multiplayer.Core.pdb" $dest -Force }
Copy-Item (Join-Path $root "meta.json") $dest -Force
$assets = Join-Path $root "Assets"
if (Test-Path $assets) { Copy-Item $assets $dest -Recurse -Force }
Write-Host "Deployed Multiplayer to $dest"
