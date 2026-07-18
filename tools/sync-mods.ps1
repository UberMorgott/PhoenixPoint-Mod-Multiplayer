<#
sync-mods.ps1 — mirror the main install's mod set into every local co-op test instance.

WHY THIS EXISTS
  Instances used to get their mods via `mklink /J` junctions into the Steam install and the
  workshop dir. That gave free auto-pickup, but it meant all instances wrote the SAME physical
  files: `D:\PP-Instance2\Mods\2872311902\TFTV.log` and the workshop copy had ONE identical NTFS
  file id (0x...2f000000000097). Three instances racing one file at startup = TFTV aborting its
  init on whichever lost, and its log truncated by whichever started last.
  Each instance now owns REAL copies. This script restores the auto-pickup the junctions gave.

WHEN TO RUN
  After installing or updating ANY workshop mod, or after adding a mod to the main install.
  One command, all instances match. Idempotent — re-running with nothing changed copies nothing.

SOURCES   <game>\Mods\*  +  <workshop>\content\839770\*   (both are read-only here, never written)
TARGETS   D:\PP-Instance2\Mods , D:\PP-Instance3\Mods

EXCLUDED — stays per-instance, never copied, never purged:
  *.log   Mod logs (TFTV.log, TFTV-N.log, ...) are written INTO the mod dir at runtime. Copying
          them would re-merge the exact state we split apart; purging them would delete the
          instance's own log. /XF keeps them out of both the copy pass and the /MIR purge.

NOT TOUCHED AT ALL — already per-instance, lives elsewhere:
  ModConfig.json / Options.jopt live in
  %USERPROFILE%\AppData\LocalLow\Snapshot Games Inc\Phoenix Point\Steam\<steamid>\ , and each
  instance runs a distinct Goldberg steamid, so they are already isolated. launch-instance.bat
  seeds them on first run. This script deliberately stays out of LocalLow.

SAFETY
  Refuses to write into any target that is a junction/symlink, so it can never mirror back
  through a link into the Steam workshop or the main install.
#>
$ErrorActionPreference = 'Stop'

$game      = "D:\Steam\steamapps\common\Phoenix Point"
$workshop  = "D:\Steam\steamapps\workshop\content\839770"
$instances = @("D:\PP-Instance2", "D:\PP-Instance3")

function Test-Link($p) {
    (Test-Path $p) -and (((Get-Item $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
}

$sources = @(Get-ChildItem "$game\Mods" -Directory -Force -ErrorAction SilentlyContinue) +
           @(Get-ChildItem $workshop    -Directory -Force -ErrorAction SilentlyContinue)
if (-not $sources) { throw "no source mods found under '$game\Mods' or '$workshop'" }
Write-Host "sync-mods: $($sources.Count) source mods -> $($instances.Count) instances"

foreach ($inst in $instances) {
    if (-not (Test-Path $inst)) { Write-Host "  skip $inst (instance not present)"; continue }

    $modsRoot = Join-Path $inst "Mods"
    if (Test-Link $modsRoot) {
        Write-Host "  REFUSING $modsRoot - it is a junction; remove it with: cmd /c rmdir `"$modsRoot`""
        continue
    }
    New-Item -ItemType Directory -Force -Path $modsRoot | Out-Null

    $changed = @()
    $blocked = @()
    foreach ($s in $sources) {
        $dst = Join-Path $modsRoot $s.Name
        if (Test-Link $dst) { $blocked += $s.Name; continue }

        robocopy $s.FullName $dst /MIR /XF *.log /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
        $rc = $LASTEXITCODE
        if ($rc -ge 8) { throw "robocopy failed (exit $rc) for '$($s.FullName)' -> '$dst'" }
        # bit 0 = files copied, bit 1 = extras purged by /MIR. 0 = already identical.
        if ($rc -band 3) { $changed += $s.Name }
    }

    if ($changed)      { Write-Host "  $inst : updated $($changed.Count) -> $($changed -join ', ')" }
    elseif (-not $blocked) { Write-Host "  $inst : already up to date" }
    if ($blocked) {
        Write-Host "  $inst : REFUSED $($blocked.Count) still-junctioned -> $($blocked -join ', ')"
        Write-Host "           remove each with: cmd /c rmdir `"<path>`"  (no /s - deletes only the link)"
    }
}
