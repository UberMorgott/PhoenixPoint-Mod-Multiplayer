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
  Automatically: launch-instance.bat runs it for its OWN instance before every launch, so a
  test copy can never start with stale mods. Manually: after installing or updating ANY workshop
  mod, run it with no args to bring every instance in line at once.
  Idempotent — re-running with nothing changed copies nothing (~1s over an unchanged tree).

PARAMS
  -Instance <path[]>   Sync only these instance roots (launch-instance.bat passes its own).
                       Omitted = every instance below. An explicitly named instance that does
                       not exist is an ERROR (typo guard); an absent DEFAULT one is just skipped.

EXIT CODE
  0 = everything synced (or already current). 1 = at least one target could not be synced
  (missing source, junctioned target, robocopy error). Callers MUST check it: launching an
  instance whose sync failed means testing against stale mods.

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
param([string[]]$Instance)

$ErrorActionPreference = 'Stop'

$game      = "D:\Steam\steamapps\common\Phoenix Point"
$workshop  = "D:\Steam\steamapps\workshop\content\839770"
$explicit  = [bool]$Instance
$instances = if ($explicit) { $Instance } else { @("D:\PP-Instance2", "D:\PP-Instance3") }
$failed    = $false

function Test-Link($p) {
    (Test-Path $p) -and (((Get-Item $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
}

$sources = @(Get-ChildItem "$game\Mods" -Directory -Force -ErrorAction SilentlyContinue) +
           @(Get-ChildItem $workshop    -Directory -Force -ErrorAction SilentlyContinue)
if (-not $sources) {
    Write-Host "sync-mods: ERROR no source mods found under '$game\Mods' or '$workshop'"
    exit 1
}
Write-Host "sync-mods: $($sources.Count) source mods -> $($instances.Count) instance(s)"

foreach ($inst in $instances) {
    if (-not (Test-Path $inst)) {
        if ($explicit) { Write-Host "  ERROR $inst - instance not present"; $failed = $true }
        else           { Write-Host "  skip $inst (instance not present)" }
        continue
    }

    $modsRoot = Join-Path $inst "Mods"
    if (Test-Link $modsRoot) {
        Write-Host "  REFUSING $modsRoot - it is a junction; remove it with: cmd /c rmdir `"$modsRoot`""
        $failed = $true
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
        if ($rc -ge 8) {
            Write-Host "  ERROR robocopy exit $rc : '$($s.FullName)' -> '$dst'"
            $failed = $true
            continue
        }
        # bit 0 = files copied, bit 1 = extras purged by /MIR. 0 = already identical.
        if ($rc -band 3) { $changed += $s.Name }
    }

    if ($changed)      { Write-Host "  $inst : updated $($changed.Count) -> $($changed -join ', ')" }
    elseif (-not $blocked) { Write-Host "  $inst : already up to date" }
    if ($blocked) {
        Write-Host "  $inst : REFUSED $($blocked.Count) still-junctioned -> $($blocked -join ', ')"
        Write-Host "           remove each with: cmd /c rmdir `"<path>`"  (no /s - deletes only the link)"
        $failed = $true
    }
}

if ($failed) { Write-Host "sync-mods: FAILED - see errors above"; exit 1 }
