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
# Updated deliberately 2026-08-15: L526 ADDED (dismissal scope is a declared property of a family,
# never a special case: undeclared and null are LOCAL, the mission family is GLOBAL, no method outside
# WindowJournal decides a scope from a mission state name, DropUnservableQueued never reaches
# WindowJournal.ApplyVoid, and GeoModalMirror.HostMintVoid exists) -- 338 -> 339 registrations, one
# new identity string. Nothing retired or amputated: the scope table and the host-minted void are new
# subjects and no existing law lost one.
# Earlier the same day: L525 ADDED (the save gate reads only the local cursor, and an
# autosave always proceeds) -- 337 -> 338 registrations, one new identity string. Nothing retired or
# amputated: the player-initiated save gate is the mod's first save gate, so no law lost a subject.
# Earlier the same day: L524 ADDED (an unread journal entry is removed only by being read or
# by a host-minted void) -- 336 -> 337 registrations, one new identity string. The same commit deleted
# GeoWindowCoverage.QueueCap/TrimQueue and AMPUTATED the five bound-* arms of the INLINE L82, whose only
# subject those two were; L82 keeps its arbiter/seam arms and its registration, so it does not move the
# count and the amputation is not a retirement. Earlier the same day: L523 ADDED (durable means answered exactly once, not ordered) and
# THREE laws RETIRED -- 338 -> 336 registrations, identity set changed. L496 (it legitimised a duplicate
# presentation gate, R7), L380 (a priority window suspends before it preempts) and L406 (a confirm puts
# the preparation window first) all assert the second ordering system this commit deletes. Earlier the
# same day: L522 ADDED (a client never sorts a window queue) and L507 RETIRED
# (the provisional window ordinal is back-filled -- its subject, WindowOrder.Stamp/StampAt and the
# per-request order key, is deleted). 338 registrations either side; the identity SET changed, which is
# exactly what this digest is for. Earlier the same day:
# L521 ADDED (the append is screen-independent) -- 337 -> 338
# registrations, one new identity string. Earlier the same day: L520 ADDED (the only publication of a window is the QueryStateSwitch
# postfix) -- 336 -> 337 registrations, one new identity string. Earlier the same day:
# L516 ADDED (an off-screen strip never vouches for its rows -- the
# top-right activity label stopped following research on clients) -- 335 -> 336 registrations, one new
# identity string. Earlier the same day: L514 ADDED (the roster list repaints on the same mirrored
# level-up), 334 -> 335; L513 ADDED (no peer lifts before its boundary releases), 333 -> 334;
# L512 ADDED (the crew strip repaints on a mirrored level-up), 332 -> 333.
$expectedRegistrationDigest = 'f21ff29348c2bf68c2ff663f21de1a3ee09a620599f2e0a11e6d2fd8097b1231'
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
