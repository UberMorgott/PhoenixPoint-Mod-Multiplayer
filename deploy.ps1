<#
.SYNOPSIS
  Builds the mod and copies it into a Phoenix Point install's Mods folder.

.PARAMETER GameDir
  Phoenix Point install root. Defaults to the PhoenixPointDir environment variable, then to the
  usual Steam / Epic locations.

.EXAMPLE
  .\deploy.ps1
  .\deploy.ps1 -GameDir "C:\Games\Phoenix Point"
#>
param([string]$GameDir = $env:PhoenixPointDir)

$ErrorActionPreference = 'Stop'
# Path-agnostic: everything resolves relative to this script's folder.
$root = $PSScriptRoot
$proj = Join-Path $root "Multiplayer.csproj"
$out  = Join-Path $root "bin\Release"

# Extra "<install>\Mods" folders to mirror the build into (co-op test instances). Missing ones are
# skipped with a warning, so this script still works on a machine without them.
$extraModRoots = @(
    "D:\PP-Instance2\Mods"
    "D:\PP-Instance3\Mods"
)

if (-not $GameDir) {
    $GameDir = @(
        "${env:ProgramFiles(x86)}\Steam\steamapps\common\Phoenix Point"
        "C:\Steam\steamapps\common\Phoenix Point"
        "D:\Steam\steamapps\common\Phoenix Point"
        "E:\Steam\steamapps\common\Phoenix Point"
        "${env:ProgramFiles(x86)}\Epic Games\PhoenixPoint"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $GameDir -or -not (Test-Path $GameDir)) {
    throw "Phoenix Point install not found. Pass -GameDir '<path>' or set the PhoenixPointDir environment variable."
}

dotnet build $proj -c Release -p:PhoenixPointDir="$GameDir"
if ($LASTEXITCODE) { throw "build failed ($LASTEXITCODE)" }

$mainModRoot = Join-Path $GameDir "Mods"
New-Item -ItemType Directory -Force -Path $mainModRoot | Out-Null  # main root is never skipped
$modRoots = @($mainModRoot) + $extraModRoots
foreach ($modRoot in $modRoots) {
    if (-not (Test-Path $modRoot)) {
        Write-Host "SKIPPED (not found): $modRoot" -ForegroundColor Yellow
        continue
    }
    $dest = Join-Path $modRoot "Multiplayer"
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item "$out\Multiplayer.dll" $dest -Force
    if (Test-Path "$out\Multiplayer.pdb") { Copy-Item "$out\Multiplayer.pdb" $dest -Force }
    # Single self-contained assembly: PP's mod loader loads the entry DLL via Assembly.Load(byte[])
    # with no AssemblyResolve handler, so sibling DLLs would never resolve at enable time.
    Copy-Item (Join-Path $root "meta.json") $dest -Force
    $assets = Join-Path $root "Assets"
    if (Test-Path $assets) { Copy-Item $assets $dest -Recurse -Force }
    Write-Host "Deployed Multiplayer to $dest"
}
