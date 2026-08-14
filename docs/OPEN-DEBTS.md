# Open debts — deferred work, standing decisions, unverified questions

A working list, not a plan. Each entry: what it is, why it was deferred, the next step, the seam.
Line numbers are HEAD-relative; re-grep the named symbol if they drift.

## 1. Tactical client gates not consolidated

- `ClientAiGate` (src/Tactical/TacticalEntry.cs:253, predicate :258) and
  `ClientAiEvaluationSeamGate` (:360, predicate :367) still derive their own client predicate
  (`engine == null || !engine.IsActiveSession || engine.IsHost`) instead of
  `ClientAuthority.IsClient()` (src/Rail/ClientAuthority.cs:35).
- Deferred deliberately: tactical entry has its own barrier that was not traced end to end, and the
  design called for a tactical soak first.
- Both are listed in law L506's in-source allow-list
  (tools/RailCheck/L506_EveryClientRefusalGateAsksTheOnePredicate.cs:100, rationale :88).
- Next: convert both, extend L506's registry, shrink `AllowedToDerive`, one semantic kill each.

## 2. Panic evaluation ungated on clients

- IL sweep during the L472 work found `PanicAbility.Move`:75 → `TacticalFaction.EvaluateAiActionAsync`
  → `AIFaction.EvaluateActionAsync`: a panicking actor on a CLIENT evaluates its own
  run-to-safest-position.
- Not gated because panic is a reaction riding the 0x82 mirror; closing it blind risks freezing
  panicked actors.
- ASSERTED member of L472's caller set, not an unexamined gap —
  tools/RailCheck/L472_EveryDoorIntoAiEvaluationIsKnownAndGatedOnce.cs:80 (`"PanicAbility.Move"`,
  summary :40/:43).
- Next: decide deliberately (gate it, or document why it must stay open) — needs a tactical session
  with panic to observe.

## 3. STANDING DECISION — deferred gates stay fail-OPEN by design

Not a todo. Do NOT "finish the unification".

- `IntentRail.ShouldRunNative` (~96 call sites, 24 files) must stay fail-OPEN: a wrong capture
  swallows player input permanently and nothing restores it. L506 arm (e) `capture-went-closed`
  actively forbids turning the capture seam fail-closed
  (L506_EveryClientRefusalGateAsksTheOnePredicate.cs:50, check at :215).
- `StatCommitApplyGate` (src/Rail/ClientSimGate.cs:119, predicate :132-138) and `SetItemsApplyGate`
  (:155, predicate :164-169) block the HOST inside `SyncApplyScope`; converting them would let the
  host revert a just-applied delta (the 2026-07-18 / 2026-07-24 bug).
- `TacticalDamageSync.DamageIsSomebodyElses` (src/Tactical/TacticalDamageSync.cs:476) plus the
  duplicate `LiveEngine()` copies additionally require `SaveTransfer.SessionStarted`; closing them
  makes a client apply NO damage before `SessionBegin`.

## 4. Duplicate `LiveEngine()` copies — cleanup, low priority

- src/Tactical/TacticalAimPoseSync.cs:113, src/Tactical/TacticalCommandSync.cs:739,
  src/Tactical/TacticalDamageSync.cs:440 (the shared one, called from 8 other files), and a fourth
  private copy at src/Tactical/TacticalUiRepaint.cs:179.
- Pure cleanup. Dedupe onto `TacticalDamageSync.LiveEngine()` when someone is in that file anyway.

## 5. Info-bar signature has an unmodelled tail

- `OpenUiRepaint.RepaintNeeded` (src/Rail/OpenUiRepaint.cs:211) + `InfoBarKey` (:338, used at :286)
  cover the native draw exactly and TFTV's diplomacy / alien-base sources, but NOT TFTV's three
  "Discovered" diplomacy GameTags (`GameTagsProviderList` exposes no count or enumerator), its
  event-system variable, its void-omen check, or the `SDI_10` record answer.
- Bounded instead by a ONE-SECOND repaint floor, matching the module's own native poll rate; marked
  with a `ponytail:` comment carrying the upgrade path (src/Rail/OpenUiRepaint.cs:335).
- Next: only if a stale info-bar is ever actually observed.

## 6. Unverified in logs — answered by the next co-op session

The host build in play predated the instrumentation, so neither question could be settled.

- Does the client's research gate window at join / save-transfer actually open? The one-shot
  `ClientResearchGate WINDOW` line (src/Rail/ClientSimGate.cs:623) proves it empirically.
- Is the reported "two researches completed back to back" two ids or one id twice? The host-side
  `ResearchSync HOST completed <id> geoT=` line (src/Rail/ResearchSync.cs:561) answers it.
- Next: read both logs after the next co-op session.

## 7. `NetworkEngine.LocalSteamId` is always 0

- `ResolveLocalSteamId` (src/Lobby/NetworkEngine.cs:798) casts a boxed `Steamworks.SteamId` straight
  to `ulong` (:807), which always throws, so `LocalSteamId` (assigned :167, :204) is permanently 0.
- Routed around via `SteamProbe.LocalSteamId()` (src/Transport/SteamInvite.cs:636, class :504) rather
  than fixed, because several identity/ack paths document that it "may be 0".
- Next: fix the cast and re-check those paths.

## 8. Process debt — one working tree, many agents

- Running many fix agents in one shared working tree caused commits to sweep each other's staged
  files, and law numbers collided twice; several commits carry unrelated changesets under one subject.
- Next: serialise agents that touch `tools/RailCheck` bookkeeping, or give each its own git worktree.
