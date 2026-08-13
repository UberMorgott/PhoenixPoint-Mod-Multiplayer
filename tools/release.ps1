<#
.SYNOPSIS
  Cuts a release: preconditions -> version bump -> green gates -> commit -> annotated tag.

  Never pushes. It prints the exact push command; pushing stays a deliberate human step.

.PARAMETER Version
  Bare version without the suffix, e.g. "0.9.12". Omit to auto-bump the patch of meta.json.

.PARAMETER Suffix
  Release-name suffix used by the tag and the CHANGELOG header ("beta" -> v0.9.12-beta).
  meta.json always stores the bare version.

.PARAMETER WhatIf
  Dry run: prints every step, changes nothing, runs no build.

.EXAMPLE
  pwsh -File tools/release.ps1 -WhatIf
  pwsh -File tools/release.ps1 -Version 0.9.12 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'
#>
param(
    [string]$Version,
    [string]$Suffix = 'beta',
    [string]$GameDir,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$meta = Join-Path $root 'meta.json'
$changelog = Join-Path $root 'CHANGELOG.md'

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Die($msg) { Write-Host "REFUSED: $msg" -ForegroundColor Red; exit 1 }
function Run($label, [scriptblock]$cmd) {
    if ($WhatIf) { Step "WOULD RUN: $label"; return }
    Step $label
    & $cmd
    if ($LASTEXITCODE -ne 0) { Die "$label failed (exit $LASTEXITCODE) - nothing committed, nothing tagged." }
}

# --- preconditions -----------------------------------------------------------
$branch = (git -C $root rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne 'main') { Die "on branch '$branch', releases are cut from main." }
if ((git -C $root status --porcelain)) { Die "working tree is dirty. Commit or stash first." }

$current = ([regex]'"Version"\s*:\s*"([^"]+)"').Match((Get-Content $meta -Raw)).Groups[1].Value
if (-not $current) { Die "no Version field in $meta." }
if (-not $Version) {
    $p = $current.Split('.')
    $p[-1] = [int]$p[-1] + 1
    $Version = $p -join '.'
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { Die "version '$Version' is not X.Y.Z (the suffix comes from -Suffix)." }
$name = if ($Suffix) { "$Version-$Suffix" } else { $Version }
$tag = "v$name"

Step "release $current -> $name (tag $tag)"
if (git -C $root tag -l $tag) { Die "tag $tag already exists." }
if (-not (Select-String -Path $changelog -Pattern "^## $([regex]::Escape($name))\s*$" -Quiet)) {
    Die "CHANGELOG.md has no '## $name' section. Write the player-facing notes first."
}

# --- version bump (meta.json is the ONLY place the version lives) ------------
if ($WhatIf) {
    Step "WOULD SET meta.json Version = $Version"
} else {
    Step "meta.json Version = $Version"
    (Get-Content $meta -Raw) -replace '("Version"\s*:\s*")[^"]+(")', "`${1}$Version`${2}" | Set-Content $meta -NoNewline
}

# --- green gates: any RED aborts before anything is committed ----------------
$deploy = @('-File', (Join-Path $root 'deploy.ps1'))
if ($GameDir) { $deploy += @('-GameDir', $GameDir) }
Run 'deploy.ps1 (build + install)' { pwsh @deploy }
Run 'RailCheck laws' { dotnet run -c Debug --project (Join-Path $root 'tools\RailCheck') }
Run 'law-integrity.ps1' { pwsh -File (Join-Path $root 'tools\law-integrity.ps1') }

# --- commit + annotated tag ON that commit -----------------------------------
if ($WhatIf) {
    Step "WOULD COMMIT: git add meta.json CHANGELOG.md && git commit -m 'chore(release): $name'"
    Step "WOULD TAG:    git tag -a $tag -m 'Multiplayer $name' (on that commit)"
} else {
    Run "commit chore(release): $name" { git -C $root add -- meta.json CHANGELOG.md; git -C $root commit -m "chore(release): $name" }
    Run "annotated tag $tag" { git -C $root tag -a $tag -m "Multiplayer $name" }
    $head = (git -C $root rev-parse --short HEAD).Trim()
    if ((git -C $root rev-list -n1 $tag) -ne (git -C $root rev-parse HEAD)) { Die "tag $tag did not land on $head." }
    Step "tagged $tag on $head"
}

Write-Host ""
Write-Host "Done. Push is a separate, deliberate step:" -ForegroundColor Green
Write-Host "  git -C `"$root`" push origin main $tag"
