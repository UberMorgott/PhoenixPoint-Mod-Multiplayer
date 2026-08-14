#!/usr/bin/env pwsh
# Source-level guard over the RailCheck law set. Plain text only: no compilation, no Phoenix Point
# install, so it runs in CI and on any machine. It closes the hole that deleting an L*.cs file
# together with its laws.AddRange line is otherwise invisible — every law encodes a real past bug.
$ErrorActionPreference = 'Stop'

$repo     = Split-Path -Parent $PSScriptRoot
$railDir  = Join-Path $repo 'tools/RailCheck'
$program  = Join-Path $railDir 'Program.cs'
$countF   = Join-Path $repo 'tools/law-count.txt'
$exemptF  = Join-Path $repo 'tools/vacuity-exempt.txt'
$baseline = Join-Path $repo 'docs/rail-baseline.txt'

$problems = [System.Collections.Generic.List[string]]::new()
function Fail([string]$m) { $problems.Add($m) }

$files = Get-ChildItem -Path $railDir -Filter 'L*.cs' | Sort-Object Name
# Registration names drop the underscore sometimes (L79AimAnimAndAbilityRefresh), so match on the id.
$fileIds = @{}
foreach ($f in $files) {
    if ($f.BaseName -match '^L(\d+)') { $fileIds[$Matches[1]] = $f.Name }
    else { Fail "unparseable law file name: $($f.Name)" }
}

$progText = Get-Content -Path $program -Raw
# Every law reaches the run through exactly one Add(laws, () => ...) in Program.cs. Two classes:
#   file-backed  Add(laws, () => L<n>_Name.Check(...))   -> tools/RailCheck/L<n>_*.cs
#   inline       Add(laws, () => SomeLaw(...))           -> a private method in Program.cs
# The shape was laws.AddRange(...) until 2026-08-08. It changed because a law that THREW aborted the
# whole run and every law after it reported nothing -- which reads exactly like passing (Program.Add,
# L193). The old spelling is still matched so a half-rebased Program.cs is reported honestly rather
# than as 100+ orphan files; L193 arm (d) is what forbids it coming back for real.
$regIds = @{}
$inlineRegs = [System.Collections.Generic.List[string]]::new()
$registrationNames = [System.Collections.Generic.List[string]]::new()
foreach ($m in [regex]::Matches($progText, '(?:laws\.AddRange\(|Add\(laws,\s*\(\)\s*=>\s*)\s*([A-Za-z_][A-Za-z0-9_]*)')) {
    $n = $m.Groups[1].Value
    $registrationNames.Add($n)
    if ($n -match '^L(\d+)') { $regIds[$Matches[1]] = $true } else { $inlineRegs.Add($n) }
}

# Identity ratchet, deliberately independent of law-count.txt. A count alone can be lowered together
# with deleted laws and still says nothing about WHICH contracts survived. This digest covers the sorted
# registration multiset (so sparse ids and many registrations per source file remain valid).
$expectedRegistrationDigest = 'fae4894f7a47bc1ee691341d07ee45dc0eb5fa29d615092d784b240e4ef212fc'
$registrationText = (($registrationNames | Sort-Object) -join "`n")
$registrationDigest = [Convert]::ToHexString(
    [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($registrationText))
).ToLowerInvariant()
if ($registrationDigest -ne $expectedRegistrationDigest) {
    Fail "law identity set changed (digest $registrationDigest != committed $expectedRegistrationDigest). A law was added, removed, renamed, or duplicated; review the exact registration diff, then deliberately update the identity digest."
}

# (a) registration parity, both directions
foreach ($id in ($fileIds.Keys | Sort-Object { [int]$_ })) {
    if (-not $regIds.ContainsKey($id)) { Fail "orphan file: $($fileIds[$id]) has no laws.AddRange(L$id...) in Program.cs" }
}
foreach ($id in ($regIds.Keys | Sort-Object { [int]$_ })) {
    if (-not $fileIds.ContainsKey($id)) { Fail "orphan registration: laws.AddRange(L$id...) in Program.cs has no L$id*.cs file" }
}

# (a2) every inline registration resolves to a method that still exists in Program.cs
foreach ($n in ($inlineRegs | Sort-Object -Unique)) {
    if ($progText -notmatch ('(?m)^\s*(private|internal|public|static)[^\r\n]*\s' + [regex]::Escape($n) + '\s*\(')) {
        Fail "orphan inline registration: laws.AddRange($n(...)) has no $n method in Program.cs"
    }
}

# (b) law count tripwire -- two numbers, so the failure names WHICH class shrank
$count = $files.Count
$inlineCount = $inlineRegs.Count
if (-not (Test-Path $countF)) {
    Fail "missing $countF; current counts are files=$count inline=$inlineCount"
} else {
    $want = @{}
    foreach ($line in (Get-Content -Path $countF)) {
        if ($line -match '^\s*(files|inline)\s*=\s*(\d+)\s*$') { $want[$Matches[1]] = [int]$Matches[2] }
    }
    foreach ($pair in @(@('files', $count), @('inline', $inlineCount))) {
        $k, $have = $pair
        if (-not $want.ContainsKey($k)) { Fail "$countF has no '$k=' line (current $k=$have)" }
        elseif ($want[$k] -ne $have) {
            Fail "$k law count $have != expected $($want[$k]). Lowering it needs a deliberate edit of tools/law-count.txt and an explanation in the commit body -- each law encodes a real past bug."
        }
    }
}

# (c) anti-vacuity ratchet: a law with neither guard can pass while checking nothing
$exempt = @()
if (Test-Path $exemptF) {
    $exempt = Get-Content -Path $exemptF | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') }
}
if ($exempt.Count -gt 0) {
    Fail "vacuity exemptions are forbidden; give every listed law an executable guard"
}
$bare = foreach ($f in $files) {
    $t = Get-Content -Path $f.FullName -Raw
    # A marker word in prose proves nothing. Require an executable violation string: either the named
    # premise guard, or a POSITIVE CONTROL section that actually reaches a yield-return arm.
    $premiseGuard = $t -match '(?is)yield\s+return\s+"[^"\r\n]*premise-changed'
    $positiveGuard = $t -match '(?is)yield\s+return\s+"[^"\r\n]*(?:positive-control|control-)'
    if (-not $premiseGuard -and -not $positiveGuard) { $f.Name }
}
foreach ($n in $bare) {
    if ($exempt -notcontains $n) { Fail "vacuity: $n has neither 'premise-changed' nor a 'POSITIVE CONTROL' block" }
}
foreach ($n in $exempt) {
    if ($fileIds.Values -notcontains $n) { Fail "stale exemption: $n listed in tools/vacuity-exempt.txt but no such law file" }
    elseif ($bare -notcontains $n) { Fail "ratchet: $n now has a guard — remove it from tools/vacuity-exempt.txt" }
}

# (d) baseline present
if (-not (Test-Path $baseline)) { Fail "missing docs/rail-baseline.txt" }
elseif ((Get-Item $baseline).Length -eq 0) { Fail "docs/rail-baseline.txt is empty" }

Write-Host "laws: $count file(s) + $inlineCount inline = $($count + $inlineCount); $($regIds.Count) file registration(s); $($bare.Count) unguarded ($($exempt.Count) exempt)"
if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "FAIL  $_" }
    Write-Host "law-integrity: $($problems.Count) problem(s)"
    exit 1
}
Write-Host "law-integrity: OK"
exit 0
