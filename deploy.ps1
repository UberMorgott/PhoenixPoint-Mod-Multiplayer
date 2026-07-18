$ErrorActionPreference = 'Stop'
# Path-agnostic: resolves relative to this script's folder, so it works before AND after the
# root folder is renamed to Multiplayer (only the folder name differs; the layout is identical).
$root = $PSScriptRoot
$proj = Join-Path $root "Multiplayer.csproj"
$out  = Join-Path $root "bin\Release"
# Deploy targets: the main install (always) + each local co-op test instance (skipped if absent,
# so this script still works on a machine with no instances). Instances hold REAL mod copies,
# not junctions - see tools\sync-mods.ps1 for why.
$mainRoot  = "D:\Steam\steamapps\common\Phoenix Point"
$instRoots = @("D:\PP-Instance2", "D:\PP-Instance3")

dotnet build $proj -c Release
if ($LASTEXITCODE) { throw "build failed ($LASTEXITCODE)" }

$assets = Join-Path $root "Assets"
foreach ($modRoot in @($mainRoot) + $instRoots) {
    if ($modRoot -ne $mainRoot -and -not (Test-Path $modRoot)) {
        Write-Host "Skipped $modRoot (instance not present)"; continue
    }
    $dest = Join-Path $modRoot "Mods\Multiplayer"
    # Never deploy through a link - that would write into another target's copy.
    if ((Test-Path $dest) -and (((Get-Item $dest -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        Write-Host "Skipped $dest (junction - run tools\sync-mods.ps1 first)"; continue
    }
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item "$out\Multiplayer.dll" $dest -Force
    if (Test-Path "$out\Multiplayer.pdb") { Copy-Item "$out\Multiplayer.pdb" $dest -Force }
    # Single self-contained assembly: PP's mod loader loads the entry DLL via Assembly.Load(byte[])
    # with no AssemblyResolve handler, so sibling DLLs would never resolve at enable time.
    Copy-Item (Join-Path $root "meta.json") $dest -Force
    if (Test-Path $assets) { Copy-Item $assets $dest -Recurse -Force }
    Write-Host "Deployed Multiplayer to $dest"
}
