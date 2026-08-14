<#
.SYNOPSIS
  Cuts a release: preconditions -> version bump -> green gates -> commit -> annotated tag
  -> (with -Publish) push + GitHub Release with assets.

  Never pushes unless -Publish. Without it, it prints the exact push command; pushing stays
  a deliberate human step.

.PARAMETER Version
  Bare version without the suffix, e.g. "0.9.12". Omit to auto-bump the patch of meta.json.

.PARAMETER Suffix
  Release-name suffix used by the tag and the CHANGELOG header ("beta" -> v0.9.12-beta).
  meta.json always stores the bare version.

.PARAMETER Publish
  After tagging: push main + tag, pack artifacts/Multiplayer-<name>.zip and create the
  GitHub pre-release (notes = the '## <name>' CHANGELOG section) with zip/dll/pdb assets.
  Requires gh on PATH.

.PARAMETER WhatIf
  Dry run: prints every step, changes nothing, runs no build, calls gh not at all.

.EXAMPLE
  pwsh -File tools/release.ps1 -WhatIf
  pwsh -File tools/release.ps1 -Version 0.9.12 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'
  pwsh -File tools/release.ps1 -Version 0.9.12 -GameDir 'D:\Steam\steamapps\common\Phoenix Point' -Publish
#>
param(
    [string]$Version,
    [string]$Suffix = 'beta',
    [string]$GameDir,
    [switch]$Publish,
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
if ($Publish -and -not (Get-Command gh -ErrorAction SilentlyContinue)) { Die "-Publish needs the GitHub CLI 'gh' on PATH." }

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

# --- publish: push + packed GitHub pre-release -------------------------------
if ($Publish) {
    $art = Join-Path $root 'artifacts'
    $stage = Join-Path $art 'Multiplayer'
    $zip = Join-Path $art "Multiplayer-$name.zip"
    $dll = Join-Path $root 'bin\Release\Multiplayer.dll'
    $pdb = Join-Path $root 'bin\Release\Multiplayer.pdb'
    # em dash matches every published release title; kept ASCII in source
    $title = "$tag $([char]0x2014) Cooperative Multiplayer"

    if ($WhatIf) {
        Step "WOULD PUSH:    git -C `"$root`" push origin main $tag"
        Step "WOULD PACK:    $zip (Multiplayer/{meta.json,Multiplayer.dll,Multiplayer.pdb})"
        Step "WOULD PUBLISH: gh release create $tag --title `"$title`" --prerelease + zip/dll/pdb"
    } else {
        if (-not (Test-Path $dll)) { Die "no $dll after the gates - deploy.ps1 did not produce a Release build." }
        Run "push main + $tag" { git -C $root push origin main $tag }

        Step "pack $zip"
        Remove-Item $art -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Path $stage -Force | Out-Null
        Copy-Item $dll $stage
        if (Test-Path $pdb) { Copy-Item $pdb $stage }
        Copy-Item $meta $stage
        Compress-Archive -Path $stage -DestinationPath $zip -Force

        # notes = the '## <name>' CHANGELOG section body, up to the next '## ' or EOF
        $lines = Get-Content $changelog
        $from = ($lines | Select-String -Pattern "^## $([regex]::Escape($name))\s*$" | Select-Object -First 1).LineNumber
        $body = @()
        for ($i = $from; $i -lt $lines.Count -and $lines[$i] -notmatch '^## '; $i++) { $body += $lines[$i] }
        $notes = Join-Path $art 'notes.md'
        Set-Content $notes -Value ($body -join "`n")

        $assets = @($zip, $dll)
        if (Test-Path $pdb) { $assets += $pdb }
        Step "gh release create $tag"
        $url = gh release create $tag @assets --title $title --notes-file $notes --prerelease
        if ($LASTEXITCODE -ne 0) { Die "gh release create failed (exit $LASTEXITCODE) - the tag is pushed, publish by hand." }

        Write-Host ""
        Write-Host "Done. Released:" -ForegroundColor Green
        Write-Host "  $url"
    }
    return
}

Write-Host ""
Write-Host "Done. Push is a separate, deliberate step:" -ForegroundColor Green
Write-Host "  git -C `"$root`" push origin main $tag"
Write-Host "Or let the script push and publish the GitHub release:"
Write-Host "  pwsh -File tools/release.ps1 -Version $Version -Suffix $Suffix -Publish"
