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
# Multiplayer.Core's sources are compiled straight into Multiplayer.dll (see Multiplayer.csproj):
# PP's mod loader loads the entry DLL via Assembly.Load(byte[]) with no AssemblyResolve handler,
# so a separate Multiplayer.Core.dll would never resolve at enable time. Nothing else to copy.
Copy-Item (Join-Path $root "meta.json") $dest -Force
$assets = Join-Path $root "Assets"
if (Test-Path $assets) { Copy-Item $assets $dest -Recurse -Force }
Write-Host "Deployed Multiplayer to $dest"
