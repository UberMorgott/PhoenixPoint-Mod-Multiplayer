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

# Updated deliberately 2026-08-15: L554 ADDED (no peer lifts before its own first post-load frame has
# RENDERED, and the host is not exempt. The deadline shipped the day before was re-measured at 21:54 and
# failed: RTT sampled ~0 so the lead was the 400 ms floor, so both clients read the common instant as
# inMs=-258 -- already overdue on arrival -- and still owed one 2038 ms post-load frame each. Host on
# screen 10.646 vs clients 11.862/11.854: the 1.2 s head start was back with every barrier law green. The
# instant is now a FLOOR and the RELEASE is RevealSchedule.MayLift over a ready-set each peer observes for
# itself, fed by the new 0x4D RevealReady the host relays) -- 353 -> 354 registrations, one new identity
# string. Nothing retired. NOT A QUORUM: "ready" is a rendered frame, which an AFK player's machine
# produces anyway; a departed or paused peer leaves GetLiveRosterSlots and so SHRINKS the wait; a peer
# that goes silent without leaving is given up on after RevealSchedule.ReadyGiveUpMs measured on the
# waiting peer's OWN clock. L551 keeps its identity and every arm -- its arm (e) was RE-EXPRESSED, not
# narrowed: it used to forbid the ARM and the TICK alike from consulting the roster, which was right while
# the instant was the whole release; it now owns the MINTING (a floor that moved with the roster would let
# a departure shift everyone else's) and still forbids either seam from touching human readiness.
# Identity ratchet, deliberately independent of law-count.txt. A count alone can be lowered together
# with deleted laws and still says nothing about WHICH contracts survived. This digest covers the sorted
# registration multiset (so sparse ids and many registrations per source file remain valid).
# Updated deliberately 2026-08-15: L551 ADDED (every peer leaves the loading screen at the SAME
# INSTANT: the host used to call PerformDeferredLift in the same frame it broadcast RevealAll, so it
# stood on the geoscape 1.33-1.55 s before its clients, in the clients' load order -- measured on three
# machines. The reveal is now a DEADLINE the host ships and every peer including itself lifts at, with
# the lead derived from the measured worst live-peer RTT and clamped [300,2500] ms) -- 350 -> 351
# registrations, one new identity string. Nothing retired and no barrier law weakened: L94/L143/L433/
# L513 own the barrier itself -- who arms it, who may release it, that the release packet is bound to
# the host's authority, that no peer lifts before its boundary releases -- and all four were GREEN
# through this defect, correctly, because the barrier was doing its job. Nothing in the repo asserted
# what happened on the frame AFTER the release. L433's codec arm was renamed with the payload field
# (serverTicks -> revealDueHostMs) and asserts the same round trip.
# Earlier the same day: L550 ADDED (every host-only PRESENTATION CHANNEL is mirrored or
# locally re-raised: GeoLogNotice.HostOnlyChannels is executed against the SHIPPED ASSEMBLY -- a
# ClientDriven row must have a real IL call to its declared native presenter, a row served from mirrored
# state must not be opted out in RailMeta, nothing may call the host-only GeoscapeLog.AddEntry, and the
# anti-avalanche CURSOR is run in five directions with no game: join presents 0, a rebuild presents 0, an
# append presents 1, the 50-cap front-trim still presents exactly 1, and a backwards log presents 0)
# -- 349 -> 350 registrations, one new identity string. Nothing retired or amputated: L546's domain is
# GeoWindowCoverage.HostAuthoritativeRaisers -- ModalType windows that MINT a journal position -- so the
# centre-top status bar, a SECOND presentation channel that mints nothing and lives outside the journal,
# was in no coverage table at all. That is how every "haven destroyed"/"site captured" notice showed on
# the host and on no client for the life of the mod with every law green.
# Earlier the same day: L548 ADDED (a progress-only delta never rebuilds the top-right
# agenda strip: IL pins that RefreshAgendaTracker keys its TEARDOWN on a row-IDENTITY builder and never on
# OpenUiRepaint.ScopeKey -- a generation that also moves on a ticking countdown -- and the gate is EXECUTED
# in both directions with a real BumpScopeGenerations moving underneath an unchanged identity) -- 347 -> 348
# registrations, one new identity string. Nothing retired or amputated: L492 executes the same gate with
# hand-written strings and therefore never asked WHICH key the runtime hands it, which is how the 1 Hz
# client flicker came back with every arm green. L543 was RE-EXPRESSED, not weakened: the four repaint KEYS
# stay forbidden and the agenda strip's identity builder becomes one of its positive controls.
# Earlier the same day: L547 ADDED (a LOCAL family's dismissal never travels: the ANSWER RELAY
# -- WindowQueueSync.SendAdvance -> the 0xB9 advance intent -> the host's modal.FinishDialog -- asks
# WindowJournal.ScopeOf through the pure MayRelayAnswer, which is EXECUTED in both directions, and the IL
# arms pin that SendAdvance reaches the predicate and the predicate reaches the table) -- 346 -> 347
# registrations, one new identity string. Nothing retired or amputated: L526 owns the TABLE and never asked
# whether the answer relay consults it, which is how dismissing the research window on a client closed the
# host's own copy.
# Earlier the same day: L546 ADDED (a host-minted journal position is PUBLISHED, and a discard
# is never silent: every modal in GeoWindowCoverage.HostAuthoritativeRaisers is declared and never
# LocalOnly, and the real seam is EXECUTED -- mint, hand off, take back, ask GeoModalMirror.PublishRefusal
# with the real rule -- so a Mirrored family publishes and any other family states its discard) --
# 345 -> 346 registrations, one new identity string. Nothing retired or amputated: L520/L521/L524 keep
# every arm; none of them asked whether a MINTED position ever leaves the host.
# Earlier the same day: L545 ADDED (a surface with model or animation state is PATCHED, never
# rebuilt: every name in UiNativeRepaint.ModelAnimationSurfaces has a Table entry, no binding held by
# UiNativeRepaint resolves to one of those surfaces' own doll methods -- the resetAnimation:true paths --
# and OpenUiRepaint.Repaint refuses the Exit+Enter fallback for them) -- 344 -> 345 registrations, one new
# identity string. L156 was RE-POINTED, not weakened: its resolve-all-first arm now names EsUiRefreshNeeded
# instead of the retired EsDisplay binding, same arm count and same claim. Earlier the same day:
# L540 ADDED (a mark is raised by a value that DIFFERS, never by "a
# write happened": GenericApplier.LeafChanged is EXECUTED in both directions on boxed values and on
# blobs, distinct byte arrays with identical contents compare UNCHANGED, and null-vs-value degrades to
# CHANGED) -- 339 -> 340 registrations, one new identity string. Nothing retired or amputated:
# value-inequality change detection at the applier's mark site is a new subject no existing law owned.
# Earlier the same day: L526 ADDED (dismissal scope is a declared property of a family,
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
# exactly what this digest is for. Updated deliberately 2026-08-15:
# L544 ADDED (no rail path segment is produced from a loop index: IdentityResolver.KeyOf is EXECUTED
# and yields NULL for an unkeyable object, a key for a keyed one (positive control) and NULL for a
# negative id, and DiffEngine.VisitEntity still reaches the Incident abort) -- 343 -> 344
# registrations, one new identity string. Nothing retired: prefix subscriptions now DEPEND on element
# addressing being by stable ID and no existing law owned that. Earlier the same day:
# L543 ADDED (no hand-rolled read-set survives beside a declared prefix set: the five signature
# builders are gone, RepaintNeeded/AgendaNeedsRebuild survive as the positive control, and
# InfoBarNeedsRefresh asks the bar's LIVENESS before touching RepaintNeeded's memory) -- 342 -> 343
# registrations, one new identity string. L492/L498/L512/L514 were RE-POINTED, not weakened: the arms
# that IL-scanned AgendaSignature/InfoBarKey/CrewSlotKey now assert the DECLARATION that replaced
# them. Earlier the same day:
# L542 ADDED (the kindless mark sites still repaint everything) -- 341 -> 342 registrations, one new
# identity string. L38 was deliberately NOT extended: its scan is scoped to UiEventMap.Fire and
# widening it would change what an already-green law means, so L542 owns the ~63 kindless MarkDirty()
# sites outside Fire and the two laws partition the claim. Earlier the same day:
# L521 ADDED (the append is screen-independent) -- 337 -> 338
# registrations, one new identity string. Earlier the same day: L520 ADDED (the only publication of a window is the QueryStateSwitch
# postfix) -- 336 -> 337 registrations, one new identity string. Earlier the same day:
# L541 ADDED (an undeclared surface repaints on everything -- safe degradation of the per-surface
# path-prefix declaration) -- 340 -> 341 registrations, one new identity string.
# Earlier: L516 ADDED (an off-screen strip never vouches for its rows -- the
# top-right activity label stopped following research on clients) -- 335 -> 336 registrations, one new
# identity string. Earlier the same day: L514 ADDED (the roster list repaints on the same mirrored
# level-up), 334 -> 335; L513 ADDED (no peer lifts before its boundary releases), 333 -> 334;
# L512 ADDED (the crew strip repaints on a mirrored level-up), 332 -> 333.
# Updated deliberately 2026-08-15: L549 ADDED (the acting peer's own gesture repaints its own persistent
# HUD: IL pins that DiffEngine.FlushOnHostGesture -- N3's third arm, the ONE host-local gesture seam every
# capture family inherits from IntentRail.ShouldRunNative -- reaches OpenUiRepaint.MarkLocalGesture, that
# ResearchSync.CaptureIntent still rides that seam, and that FlushIfDirty has a HUD-only branch to land the
# flag in; the mark state machine is EXECUTED in both directions) -- 348 -> 349 registrations, one new
# identity string. Nothing retired or amputated: L154 owns the same seam's FLUSH (the change ships to the
# CLIENTS this cycle) and never asked whether the peer that ACTED repaints itself, which is how every mark
# site in the repo came to be an APPLY path and the host grew no research row for its own research.
# Updated deliberately 2026-08-15: L552 ADDED (dismissal scope and answer authority are two derivations,
# and no scope key is dead) -- 351 -> 352 registrations, one new identity string. Nothing retired.
# The scope table declared "UIStateGeoMissionBrief", a name NO assembly ships; FamilyOf is stateType.Name
# and every modal is a UIStateGeoModal, so the key could never be hit, the relay gate keyed on it was
# constant-false for every modal, and no client ever emitted an advance -- a client's Accept on
# FactionSoldierJoin (reward.Apply, host-authoritative) did nothing at all. L547 was RE-EXPRESSED, not
# weakened: it keeps its identity and all four arms and now executes MayRelayAnswer's two-input form with
# the authority input pinned false, which is precisely the case it owns. L526 is untouched.
# Updated deliberately 2026-08-15: L553 ADDED (every live window has its own identity, and every answer
# channel resolves its target the same way) -- 352 -> 353 registrations, one new identity string. Two
# measured defects, both general: (1) an identity was the window KIND plus the 0xB7 payload, and an
# asset-deploy payload's entity ref is EMPTY by construction, so two manufactured aircraft shared one
# identity string and a stale answer deployed the WRONG asset -- the identity now carries the host journal
# position of the RAISE (WindowQueueSync.TagRaise/RaiseTagFor, minted at the one seam every pushed window
# passes and already shipped as Raise.JournalPos), so uniqueness comes from the raise and not from a
# per-window table; (2) HandleAdvance answered a QUEUED window and HandleDeploy read only
# _currentStateSwitchRequest, so a host inside the manufacturing screen -- where the prompt is raised FROM
# -- refused every deploy answer and the transport was never placed. Both channels now resolve through
# WindowQueueSync.ResolveAnswerTarget and both take the window out of the queue through TakeQueued
# (answer-once, no second ledger). Nothing retired. L176 keeps all four arms (HandleAdvance still calls
# AnswerQueued directly; AnswerQueued still reads WindowOrder.RequestsField and _dialogHandler and pops no
# screen); L109 arm (g) and the inline L82 identity row were RE-EXPRESSED, not weakened -- they now RAISE
# the window they ask about (TagRaise) before asking, because an identity belongs to a raise.
# Updated deliberately 2026-08-15: L555 ADDED (a describable window is never a hole, and every hole
# states a machine-checkable reason). The Pandoran-reconnaissance window showed on the host alone because
# our own coverage table declared ModalType.PandoranRevealResult a Gap over a GeoSite -- a rail ROOT the
# wire has always been able to name -- and nothing compared that hand-written verdict against
# GeoModalMirror.Describe. A Gap now has to name the payload CLASS its raiser hands over, and
# CanDescribeClass must answer NO for it, so holes are DERIVED instead of declared. 354 -> 355
# registrations, one new identity string. Nothing retired: L48/L49 still keep the tables total, L546 still
# forbids a LocalOnly claim over a host-authoritative raiser, and L106's MirroredData table GAINED the
# twelve kinds that stopped being holes rather than losing an arm.
# Updated deliberately 2026-08-15: L556 ADDED (a window whose answer DISPOSES A SHARED ASSET closes on
# every peer the moment ANY peer answers it; an informational one closes only on the peer who dismissed
# it). A player accepted a soldier offered at a haven and the offer stayed open -- already taken -- on
# every other peer, and the asset-distribution prompt did the same. The cause was that dismissal scope was
# keyed on the FAMILY NAME, and every modal in the game is the one family UIStateGeoModal, so only the
# deployment-preparation screen ever reached a void. Scope is now DERIVED from the RAISE
# (GeoModalMirror.DismissalIsGlobal over AnswerDisposesSharedAsset, sharing NamesEntity), the void names
# the ANSWERED RAISE through its per-raise tag instead of the first unread entry of a name, and it now
# closes an already-OPEN copy as well as pruning the unread backlog. Nothing retired: L526 still makes an
# undeclared family LOCAL, L547 still forbids a LOCAL dismissal travelling, L552 still keeps dismissal
# scope and ANSWER AUTHORITY two derivations. WindowJournal.FamilyScope keeps its single row for the one
# window that carries no describable payload at all.
# Updated deliberately 2026-08-16: L558 ADDED (the agenda tracker mod root "M#agenda" is registered on
# both peers from SyncEngine's constructor, same as MistSync/DeployCountdown/DeployPrep/AssignSync, and
# the client apply patch AgendaTrackerSync.AgendaRowApplyPatch.Prefix actually reads AgendaState.Rows
# instead of short-circuiting the native computation as a silent no-op). Tasks 1-5 landed the feature
# (src/Rail/AgendaTrackerSync.cs); nothing retired.
# Updated deliberately 2026-08-17: L492, L516 and L548 RETIRED -- 358 -> 355 registrations, three identity
# strings gone. All three own ONE mechanism, OpenUiRepaint.AgendaSignature/AgendaNeedsRebuild: the agenda
# strip's hand-written rebuild gate. It is deleted, not weakened, because the "set of rows" it compared was
# an enumeration of exactly vanilla's four row sources -- a row another mod appends from its own
# InitialSetup postfix (TFTV AgendaPatches.cs:363-448) could never move the signature, so the rebuild that
# row was owed was skipped for the whole session. The strip now takes the module's own PUBLIC
# Init(GeoscapeViewContext) full rebuild, once per FLUSHED BATCH (OpenUiRepaint.FlushIfDirty ->
# RefreshPersistentHud -> RefreshAgendaTracker), so no gate remains to be right or wrong about. L543 was
# RESTORED, not weakened: AgendaSignature goes back into its forbidden-builder list (five names again) and
# its positive control keeps RepaintNeeded; L498's arm (f) retired with its subject.
$expectedRegistrationDigest = '92d3d7c06ee174c6346c79fb3b705023d2b51477e9d921d95b6b8cd81e9308d1'
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
