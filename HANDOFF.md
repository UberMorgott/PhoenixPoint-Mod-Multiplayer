# Handoff — session 2026-08-10 (durable-window surgery)

Previous handoff (session 2026-08-08) is OBSOLETE and has been replaced by this file.

This session did not add a feature. It **removed** one that had been built against a design document
rather than against the owner's requirements, kept the parts that serve those requirements, and
closed the single real gap. Net `-4542` lines across seven commits on `main`, `bef42ad..ed1d4c0`.

## 0. Verified state at HEAD `ed1d4c0`

```
laws: 206 file(s) + 60 inline = 266; 206 file registration(s); 19 unguarded (19 exempt)
law-integrity: OK

Release build: 0 errors, 0 warnings

LAW VIOLATION  L62 unbanded-name: ClockPhaseProbe (0xC0) …
RAILCHECK GREEN — types=99 polymorphic-codec=no laws-run=266/266 known-violations=1 (baselined)
```

Every shell call needs the first two lines:

```powershell
$env:Path='C:\Program Files\dotnet;'+$env:Path
$env:PhoenixPointDir='E:\Dev Games\PP-Instance2'
pwsh -File tools\law-integrity.ps1
dotnet build Multiplayer.csproj -c Release -v m
dotnet run -c Debug --project tools\RailCheck
```

**NOTHING in this session has been run in a live game.** Not one peer, not one window. The owner
playtests at home. Everything below is grounded in source and in the RailCheck harness, which
reflects over the real game assemblies but presents no UI and connects no peers.

---

## 1. The five requirements, and where each one lives

The owner stated these five, in these words. They are the whole product spec for windows.

| # | Requirement (owner's words) | Where it lives at HEAD | Held by | Pre-DWI? |
|---|---|---|---|---|
| 1 | Окна событий копятся как уведомления в шторке телефона, можно просмотреть позже | native `GeoscapeViewSwitchQuery._viewStateSwitchRequests`, re-sorted by `WindowOrder.Reorder` (`src/Rail/WindowOrder.cs:304`); the mod's own lifecycle is `InboxLifecycle` (`src/Rail/DurableInboxModel.cs:186`), drained one at a time by `DurableInboxEngine.TryPresentNext` (`src/Rail/DurableInboxEngine.cs:413`) behind `DurableWindowRegistry.MayPresent` (`src/Rail/DurableWindowRegistry.cs:313`) | L380, L393 | YES — DWI added the scheduler on top, and the scheduler STAYS |
| 2 | Окно с вариантами ответа: кто первый выбрал — тот решил; остальным read-only, кликабелен только выбранный вариант | `EventPopup.IsFrozen` (`src/Rail/EventPopup.cs:1225`) reads the game's OWN `GeoscapeEventRecord.State`/`SelectedChoice` (`:1230`, `:1233`); `EventPopup.FreezeChoiceButtons` (`:1150`) paints from it and leaves only the winner interactable (`:1156-1158`); host arbitration is `EventSync.Validate` (`src/Rail/EventSync.cs:178`) refusing anything that is not `Triggered` | L44, L45 | YES, entirely. DWI wrapped a two-save campaign-checkpoint transaction around it that bought nothing — removed in `a13c63c` |
| 3 | Окна высадки индивидуальны у каждого; пропадают у ВСЕХ, если самолёт отлетел от точки миссии ИЛИ миссия закончилась | **mission ended:** `WindowOrder.DropResolvedSubjects` (`src/Rail/WindowOrder.cs:376`), called from `:410` under the restore patch. **last aircraft left:** `MissionSync.CaptureVehicleDeparture` (`src/Rail/MissionSync.cs:246`) prefixed onto `GeoVehicle.StartTravel` (`src/Rail/VehicleSync.cs:209-213`), selecting through `MissionSync.BindsDeparture` (`:292`), committed by `CommitCapturedVehicleDeparture` (`:303`) → `SourceRevalidationPlan.Terminal` (`src/Rail/DurableInboxEngine.cs:763`) → `RetryTerminalTeardown` (`:735`) → `RemoveAllCarriers` (`:361`), broadcast so every peer installs the identical tombstone | L388, **L402 (new)** | HALF. The mission-ended half worked. The departure half was the one real gap — see §3 |
| 4 | Окна подготовки к высадке и засады — в самый верх очереди показа | `DurableWindowRegistry.PriorityOf` (`src/Rail/DurableWindowRegistry.cs:316`): `DeploymentPreparing` ⇒ `Deployment` (`:318-319`), the brief families incl. `Modal:GeoAmbushBrief` ⇒ `Priority` (`:327-335`); applied at `WindowOrder.DurablePriorityHead` (`:119`, sorts `PriorityOf` DESC then `HostOrderKey`) | L401 | NO — this is DWI's, and it STAYS |
| 5 | Окно пополнения запасов после миссии — всегда первое | `ReplenishSync.ReplenishRank = 20` (`src/Rail/ReplenishSync.cs:82`), the one-entry rank table (`:86-89`), `RankFor` (`:94`); plus the hold `ReplenishSync.ResupplyVerdictPending` (`:209`) consumed at `EventPopup.cs:715` | L93 | YES, untouched by DWI and by this session |

**Requirement 5 was settled on 2026-08-10: literally first.** The owner answered "Да, первым после
миссии", so `ReplenishSync.ReplenishRank` is `int.MaxValue` (`src/Rail/ReplenishSync.cs:82`), not 20.
It now clears `UIStateGeoCutscene` (100) too; it ties the post-mission outcome modal
(`UIStateInitial:112`, also `int.MaxValue`), which `QueryStateSwitch`:77-82 keeps first because it is
queued first from the same arrival — the native order, you-won panel then resupply. The rank does NOT
promote the yank: `UIStateReplenish` joined `WindowOrder.HeldTransitionStates`
(`src/Rail/WindowOrder.cs:212`), so the 2026-08-07 "a review window waits for the map" ruling still
holds. Held by **L407**; `L163` arm (d) was rewritten from shape to outcome.

## The 2026-08-10 owner decisions (mission-start windows, deployment promotion, resupply)

Recorded in full under `docs/laws.md` → *Mission-start windows and post-mission resupply*. In short:

| decision | where it lives | law |
|---|---|---|
| Per-peer answers ONLY for the mission-START confirmation, no read-only lock, every other family keeps shared first-wins | `GeoWindowCoverage.IsMissionStartConfirmationClass` (`src/Rail/GeoWindowCoverage.cs:478`) / `IsMissionStartConfirmation` (`:485`), matched on `ShowMissionBriefing`:1903 + `GetMissionBriefModal`:1724 | **L404** |
| Therefore the launch must be idempotent — one Confirm or five, `GeoMission.Launch` runs once | `MissionSync.MissionStartAlreadyCommitted` (`src/Rail/MissionSync.cs:416`) + `RunNativeMissionOfferAnswer(..., startCommitted)` (`:440`), wired at `PerPeerModalAnswer.Prefix` (`:1717`) | **L405** |
| Any peer's Confirm puts the preparation window first in EVERY peer's queue (push, no quorum) | `DurableInboxStore.TryStartDeployment`:99-101 mints it for every member; `DurableWindowRegistry.PriorityOf`:318 ranks it `Deployment` (the max) | **L406** |
| Resupply first after a mission, and the client asks on an EDGE rather than racing a frame ceiling | `SurfaceIds.GeoPostMissionCommit` = **0xB2**, host postfix on `GeoMission.Complete` → `ReplenishSync.HostPostMissionTick` broadcast from `SyncEngine.Tick` right after `DiffEngine.HostTick` (`src/Rail/SyncEngineStub.cs:142`); client `OnPostMissionWritesCommitted` → `TryQueueReplenish` | **L407** |

The 180-frame poll is now a bounded SAFETY NET only (a host that never sends), not the mechanism.

---

## 2. What was cut, and why — the seven commits

Read the bodies; they carry the reasoning and the falsification evidence. `git show <sha>`.

| sha | subject | net | substance |
|---|---|---|---|
| `a13c63c` | refactor(inbox): answer shared events through the existing freeze | −2430 | Requirement 2 was already delivered by the native record freeze and still is. The DWI checkpoint transaction wrote **two full campaign saves per dialog** and could freeze the host forever (`DurableEffectPhoenixCheckpointBackend.ReloadCompletesBeforeReturn => false`, no watchdog; `DurableEffectTransactionBarrier` refused `TryEnter` while `DeferredOutbound` was non-empty, so one failed broadcast blocked every later effect). Deleted the four `DurableEffect*.cs`, `EventRewardTransaction.cs`, and their gates in `NetworkEngine`/`IntentRail`/`SyncEngineStub`. Arbitration is now the single `CommitWithCanonical` of `EffectPending`. **Laws deleted: L377, L381** |
| `da02dce` | refactor(inbox): remove epoch bookkeeping no session path reaches | −570 | **Reconnect does not exist in this mod.** The only mention in `src/Lobby` + `src/Transport` is a chat string, `SessionLifecycle.FormatReconnectedNotice` (`src/Lobby/SessionLifecycle.cs:43`, called once from `src/Lobby/SessionManager.cs:443`). `HostInboxSequencer.Enroll`/`EndMembership` had **zero production callers**; `MemberPresence` was a five-valued enum holding one value. `MembershipId` collapses to the player guid — no epoch in memory, on the wire, or in a save. Owner's decision: if reconnect is ever added, **all windows reset, no history**. **Laws deleted: L376, L397, L398, L399, L400** |
| `4234b06` | fix(inbox): recover from a stuck open window and stop pinning verdicts | +97 | Two defects in code that stays. (1) `TryPresentNext` hit a bare `return false` when two entries were `Open` for one member — that member's windows stopped opening FOREVER, silently, and `Open` is committed from four sites. `RepairMultipleOpen` (`src/Rail/DurableInboxEngine.cs:393`) keeps the oldest, requeues the rest, logs once per distinct set. (2) L393 asserted a hand-written 15-value modal table — the L127(d)/L182(d) trap. The family set is now DERIVED from `DurableWindowRegistry.RoutedPresentations` |
| `9624919` | refactor(inbox): drop the ledger save root the native queue already covers | −1172 | The game already persists the window queue and the mod already rides it (§4). The DWI save root duplicated it, wrote itself into **every** save unconditionally, and each journal record was a FULL ledger snapshot with compaction that could never run (`CanCompact`/`CompactionProof` had zero production callers — the only reason L394 was green), so a campaign's saves grew **quadratically** in commits. Root, codec, journal, restore path and the `GetObjectsToWrite` postfix are gone: **the mod now writes nothing into any save.** **Laws deleted: L379, L383, L394** |
| `656d0cd` | refactor(inbox): name the live store for the session it belongs to | +28 | `DurableInboxSaveBridge` → `DurableInboxSession`, moved to `src/Rail/DurableInboxSession.cs`. It never was persistence and the name invited the next reader to put a root back. Mechanical, 16 files. Also closed a vacuity hole: L395's lifecycle arms asserted only the negatives, so gutting `OpenSessionStore` to a no-op read green while no peer got an inbox |
| `ec61557` | chore(laws): name the arms for what they now check | +9/−7 | L391's arm slugs still said `save-load` and `active-epoch`. The arms bite; only the failure text lied. `docs/rail-baseline.txt` needed no edit — L391 has no baselined violation |
| `ed1d4c0` | feat(windows): drop a deployment window whose last source has left | +146 | The one real gap. See §3 |

---

## 3. The one real gap that was closed (requirement 3, second half)

It was **not** a missing mechanism. Every part existed and was wired: the `StartTravel` prefix
snapshot, the recompute, `SourceRevalidationPlan.Terminal = UniquelyBound || After.SourceCount == 0
|| After.OccupantCount == 0`, the one-commit tombstone of every membership copy, `RemoveAllCarriers`,
and the broadcast. **L388 had guarded that ordering all along and stayed green throughout.**

What was missing was **selection**, one redundant conjunct in the binding filter
(`src/Rail/MissionSync.cs`). It demanded that the occurrence carry a bare `RootRef(site)` subject
**in addition to** the stable mission subject. But the stable subject IS the site —
`DurableWindowRegistry.StableMissionSubject` (`src/Rail/DurableWindowRegistry.cs:441`) is
`RootRef(mission.Site) + "|" + MissionDef.Guid`, and the mission is `site.ActiveMission`. So the
extra term pinned nothing and merely required the three-subject shape that only the arrival-watch
occurrences carry. Deployment windows and the modal briefs are minted by `CaptureDeployment` /
`CaptureModal` with the **single** stable subject — so exactly the windows this machinery exists for
were never bound to a departure, never revalidated, and stayed open on every peer.

The fix is the deletion of that conjunct. The predicate is extracted as `MissionSync.BindsDeparture`
(`src/Rail/MissionSync.cs:292`) so a law can reach it, the same shape `UniqueSourceBinding` (`:299`)
already has for L388. ~20 lines, plus `tools/RailCheck/L402_ADepartedLastSourceClosesTheWindowEverywhere.cs`.

Deliberately unchanged: no new opcode, no new rail, no second removal path, and **`GeoMission.Cancel`
is still never called** — the window goes, the mission stays on the map for whoever flies back
(L402 arm `departure-cancelled-the-mission-itself` guards that). Partial departure still only prunes;
that is L388's arm, not this one's.

`L402`'s regression proof: restoring the original `RootRef(site)` conjunct turns
`deployment-window-not-bound-to-the-departure` RED while L388 stays green — which is the evidence
that the gap was real and that L388 never covered selection.

---

## 4. Save state — the behaviour change to look for in the playtest

**The mod writes nothing into any campaign save.** A reloaded save no longer resumes mid-lifecycle
durable window state. That is intended, not an oversight: the native queue is the source of truth
again, exactly as it was before DWI.

The native game already persists the window queue, and the mod already rides it:

- `GeoscapeViewSwitchQuery.GetRestorableData()` writes the queue out, `RestoreData(...)` rebuilds it.
- The mod's prefix is `WindowQueueSync.RestoreDropsResolvedSubjects` (`src/Rail/WindowQueueSync.cs:988-991`).
- It logs `[MP][windows] window-queue restore: N entries in the save, M kept` (`src/Rail/WindowQueueSync.cs:1053`).

That path is **intact and untouched** by this session.

**The store now has a real lifecycle.** Before, `ActiveStore` was assigned only lazily from the save
`Append` — the durable inbox sprang into existence the first time the game autosaved, and
`ReconcileAndInstall` had no production caller at all. Now:

| | seam | `file:line` |
|---|---|---|
| created | `ModManager.OnGeoscapeStart`, beside the latch that already marks the geoscape started; refuses outside an active co-op session | patch `GeoscapeStartedPatch` at `src/Rail/GenericApplier.cs:2202-2203`, call at **`src/Rail/GenericApplier.cs:2210`** → `DurableInboxSession.OpenSessionStore` (`src/Rail/DurableInboxSession.cs:26`, constructs at `:30`) |
| dropped | `GeoLevelController.OnLevelEnd`, beside the carrier teardown that was already there | patch `DurableCarrierLevelTeardownPatch` at `src/Rail/WindowQueueSync.cs:411-412`, `ActiveStore = null` at **`src/Rail/WindowQueueSync.cs:419`** |

Both ends are asserted by L395, including the two arms added in `656d0cd` that catch a gutted or
unhooked creation seam. The seam is **per-peer, not host-only**: `ModManager.OnGeoscapeStart` is the
game's own "the geoscape is built" callback on whichever peer is loading, which is what
requirement 3's per-player drawer depends on.

### The one thing left in `DurableInboxSave.cs`

`src/Rail/DurableInboxSave.cs` is 85 lines and holds three things:

- `DurableInboxCanonicalState` (`:13`) — unrelated to persistence.
- `DurableInboxSaveBlobTransit` (`:65`) — production, used by `SaveTransferCoordinator` for the co-op
  campaign blob handoff. Unrelated to the ledger.
- `DurableInboxLegacyRootFilterPatch` (`:79-83`) — a `null`-filtering **prefix** on
  `GeoLevelSavegame.SetReadObjects`, marked `// ponytail:` at `:74`. **Compatibility shim, not a
  feature.** A campaign save already written by the DWI build carries a `Multiplayer.DurableInbox/v1`
  section whose type no longer resolves; Phoenix skips the unknown section by length but hands
  `SetReadObjects` a `null` in its place, and the native method is not known to tolerate one.
  **Delete it once no DWI-era saves remain.**

---

## 5. Laws

**Ten deleted, one added. `tools/law-count.txt` 215 → 206 files; `inline=60` unchanged.**

| law | deleted in | why it can never fire again |
|---|---|---|
| L376 a creation entitles every committed epoch | `da02dce` | one membership per player, derived from its own entries — "an epoch that missed a creation" has no representation |
| L377 a receipt advances no inbox lifecycle | `a13c63c` | it asserted `DurableInboxReducer`'s transition table, which production deliberately contradicts (`DurableInboxReducer.cs:37-42`). The L127(d)/L182(d) trap. Its receipt half survives in **L396** |
| L379 a host order survives the native save blob | `9624919` | no root is written; host order reaches peers only over the wire codec, covered by **L385**, **L390** |
| L381 a first valid shared answer applies one durable effect | `a13c63c` | every subject it named is deleted. The outcome it protected is still guarded by **L44** and **L45**, which assert outcome rather than shape. **L382 untouched and green** |
| L383 a canonical result never rebinds by list position | `9624919` | both halves were properties of the save codec. No decode, no list to rebind by, nothing stale to quarantine |
| L394 compaction needs proof no durable source can name it | `9624919` | no journal, so nothing to compact; the machinery it tested had no production caller even before |
| L397 a membership ends only by host authority | `da02dce` | nothing can end a membership; `EndMembership` is gone |
| L398 a late enrollment receives no history | `da02dce` | no enrollment path exists |
| L399 a same-epoch reconnect restores only its inbox | `da02dce` | reconnect is not a feature; no second epoch |
| L400 enrollment and creation share one order | `da02dce` | enrollment no longer consumes a committed revision |

**Added: `L402_ADepartedLastSourceClosesTheWindowEverywhere.cs`.** Chosen over a low free number
because `docs/laws.md` records the family as L376..L401 and L87–L90 would scatter it away from the
windows it belongs to. Arms (all falsified for real, one defect at a time, reverted and re-verified
green between each): `deployment-window-not-bound-to-the-departure`,
`arrival-window-not-bound-to-the-departure`, `departure-bound-a-window-of-another-site-or-mission`,
`departure-bound-a-window-that-is-not-a-deployment-or-mission-brief`,
`departure-bound-an-already-removed-window`, `last-source-departure-was-refused`,
`window-survived-for-a-peer-after-its-last-source-left`,
`carrier-was-not-torn-down-once-without-callbacks`, `departure-cancelled-the-mission-itself`,
positive control `control-not-red`, plus four `premise-changed` guards.

Laws **repaired rather than deleted**, keeping their whole subject: L380, L385, L386, L388, L390,
L391, L393, L395, L396. Their save round-trip fixtures became a direct store rebuild and the
`WriteRecord` injection seam became the `ValidateCandidate` preflight. Non-vacuity was re-proven per
law by mutation, not assumed.

`docs/laws.md` does **not** carry table rows for L376+; the family is recorded only as prose under
`## Durable-window inbox allocation clarification (2026-08-10)` (`docs/laws.md:372`). That section
has been corrected by this session — see §8.

---

## 6. Still open / not verified

**Nothing here has been in a running game.** Playtest order, most load-bearing first:

1. **A deployment window that should vanish for everyone.** Two peers, a mission with one aircraft
   parked on the site, both peers looking at the deployment window; fly the aircraft off. Both
   windows must close, the mission must stay on the map. This is the only new behaviour in the
   session and it has never executed outside RailCheck.
2. **Save/reload mid-campaign.** The mod writes nothing now. Confirm the native queue restores as it
   did before DWI — grep `window-queue restore:` and check `N entries … M kept` is sane. A save
   written by the DWI build must still LOAD (that is what the `SetReadObjects` shim is for), and that
   path has never been exercised against a real DWI-era save file.
3. **Shared event answer, two peers racing.** One winner, everyone else read-only, only the winner's
   option clickable, and — the point of `a13c63c` — **no campaign save written by answering a dialog**,
   and no host freeze.
4. **A stuck inbox recovering.** `RepairMultipleOpen` logs one `LogError` per distinct set. If that
   line ever appears, the ledger reached a state that four commit sites can produce; capture the log.
5. **Priority order.** Ambush and preparation briefs ahead of ordinary notices; resupply ahead of
   event windows.

**CLOSED 2026-08-10** — requirement 5 was ambiguous ("ahead of every event window but behind a
cutscene"); the owner ruled "первым после миссии" and the rank is `int.MaxValue` now. See §1 and
`docs/laws.md`. Playtest additions for that day's work:

6. **Two peers confirming the same mission brief.** Both windows stay live and clickable (neither goes
   read-only). The second Confirm must close only that peer's copy and start NOTHING — grep
   `start-already-committed`. Exactly one battle.
7. **One peer confirms, everyone else is on another screen.** Every peer's deployment-preparation
   window must be at the TOP of its queue and appear the moment that peer returns to the map — it must
   not yank anybody off a screen they opened.
8. **Post-mission resupply on a client.** Grep `post-mission writes committed and shipped` on the host
   and `queued the game's own UIStateReplenish on the host's post-mission commit edge` on the client.
   Seeing `the re-ask safety net` instead means 0xB2 never arrived — capture that log.

### Branches

| branch | sha | what |
|---|---|---|
| `main` | `ed1d4c0` | post-surgery. This handoff describes it |
| `dwi-archive` | **`bef42ad`** | the last pre-surgery commit — `build(railcheck): reference the physics module the harness needs`, i.e. `a13c63c`'s parent and DWI's HEAD after Task 12. The full DWI-era history is `git log --oneline dwi-archive` (26 commits back to `0b3ec02 docs(spec): define durable per-player window inbox`) |
| `dwi-task13-wip` | `d855528` | `wip(inbox): unfinished task 13 generation-linked completion`, one commit on top of `bef42ad`. Never merged, never verified |

Neither branch is merged and neither should be without re-reading §2 first — most of what they carry
is the code this session deleted on purpose.

### Greps for a tester

- `[MP][windows] window-queue restore:` — native queue restore, `N entries in the save, M kept`
- `[MP][windows] queue DROPS` — a subject being dropped from the pending list
- `[MP][inbox] source departure batch refused:` — the departure teardown declining; should be silent
- grep `RepairMultipleOpen` / the ledger-corruption `LogError` — must never appear
- `CRC backstop:` — the serious variant is `STILL diverged … after a forced re-emit`
- `Multiplayer.DurableInbox/v1` — should appear in NO newly written save

---

## 7. Rules that bit this work

- **A law that asserts SHAPE rather than OUTCOME is actively harmful.** Three more were caught this
  session and they failed the same way the ready-button pair did: L377 mandated a transition table
  production deliberately contradicts; L393 carried a hand-written 15-value modal set, so making a
  sixteenth modal durable was a CORRECT change that failed the law until the law was edited first;
  L394 was green only because the code it asserted had zero production callers. **A law is green for
  two reasons — the rule holds, or nothing reaches it. Prove which.**
- **A guard on the negatives is not a guard.** L395 asserted "teardown drops the store" and "no
  session mints none", and every arm stayed green with `OpenSessionStore` gutted to a no-op. Assert
  that the seam WORKS, not only that it does not misfire.
- **Never delete a law without also proving the outcome it protected is still held.** Each row in
  §5 names the surviving law or the reason the state is unrepresentable. That is the standard.
- **Grep for production callers before believing a subsystem exists.** `Enroll`, `EndMembership`,
  `CanCompact`, `CompactionProof`, `DurableReferenceClass`, `ReconcileAndInstall` — all zero.
- Explicit pathspecs only; never `git add -A`. Law numbers are ASSIGNED, not max+1. A commit that
  registers a law must include the law FILE.

## 8. Documentation touched by this session

- `HANDOFF.md` — this file, replacing the 2026-08-08 handoff.
- `CHANGELOG.md` — two player-facing bullets under `0.9.6-beta` (deployment windows closing for
  everyone; the save-state change), and the internals law count brought to HEAD.
- `docs/superpowers/plans/2026-08-09-durable-window-inbox.md` and
  `docs/superpowers/specs/2026-08-09-durable-window-inbox-design.md` — **kept**, each with a
  superseded-status block at the top. They are the record of a real decision, not waste.
- `docs/laws.md` — the prose clarification at `:372` corrected: L377/L379/L381/L383/L394 added to the
  deleted list, the "no L402 was created" line withdrawn, and L391's description un-tied from the
  campaign save that no longer carries it.

---

## 9. Audit fixes, 2026-08-10 (transport / save-transfer / structural rail)

- **An oversized structural CREATE is now fragmented, and a failed send never retires a root.**
  A create blob over the u16 envelope made `SyncProtocol.EncodeEnvelope` throw; `SendStructural`
  swallowed it and the caller still did `_prevRoots.Add(key)` — and a create is only ever derived
  from a root's ABSENCE from that set, so the entity was never sent to anyone again. Now
  `DiffEngine.FragmentStructuralBlob` splits it with the entry rail's own frame
  (`RailMeta.EncodeFragment` → `GenericApplier.Reassemble`), flagged on the op byte with
  `DiffEngine.FragmentedBlobFlag` (0x80) — a FLAG, not a first-byte sniff, because a native graph
  blob may legitimately open with `FragmentMarker`. `SendStructural` returns bool; a false retries
  next walk (create: not added; destroy: re-added to `_prevRoots`). A buffered fragment still marks
  its seq, or the next one reads as a gap and asks for a resync. Asserted by four new **L40** arms.
- **A failed save-prepare restores this peer's own campaign identity.**
  `ApplyPrepareLoadGameState` now RETURNS its undo (`LatestLoad`/`_currentGameId`/`_currentDifficulty`/
  `_enabledDlc`/`IsIronmanMode`), and `PrepareEntryFromBlobCrt` runs it plus the
  `_currentGeoscapeSection.ContentObjects` restore from a `finally`. Without it a throw in the
  level-params read left the live SaveManager on the REMOTE save's identity while the player kept
  playing their own game — the next save was written under the wrong campaign id. New law **L408**.
  Note for future readers: an iterator's `finally` body is LIFTED into `<>m__FinallyN` on the state
  machine, so an IL law must read that family, not only `MoveNext` (L408 does).
- **StunTransport dispatches received packets OUTSIDE `_lock`** (`Update`, copy-drain exactly like
  the peer-event drain above it). Holding the lock across arbitrary handler work stalled
  `ReceiveLoop`'s enqueue and every `Send`/`Broadcast`, and a stalled reader drops UDP.
- **`DeserializeSaveChunk` / `DeserializePeerList` bound their wire-declared length/count** against
  the bytes actually present, the same FA-0012 guard `DeserializeJoin` already had.
- **Nicknames are capped at 32 chars** in `ClientInfo.PlayerName`'s setter (JOIN, RENAME and the
  client-side PEER_LIST apply all route through it) plus `HostNickname` and the JOIN boundary.
  Truncate, never reject. One long name used to inflate the roster datagram past the STUN
  fragmentation bound, where it is dropped silently for the WHOLE lobby.
- **NOT fixed, deliberately:** `SurfaceSeq.IsStreamRestart` at `last == 1` — see the new "Known gap
  next to L85" section in `docs/laws.md`. It needs an epoch id, not a widened comparison.

---

## 10. Audit fixes, 2026-08-10 (lobby / durable inbox / join codes)

- **Hosting was dead for every non-Steam (GOG/Epic) player, and nothing said so.**
  `MultiplayerUI.StartHostAndOpenLobby` named `SteamInvite.HostPublish()` in its own body.
  `SteamInvite` holds `Lobby?` statics, so where Facepunch is absent the type cannot load, the JIT
  fails while COMPILING that method, and the throw surfaces at the CALL site — before
  `NetworkEngine.Create()` on the method's first line. CREATE SESSION did nothing at all. Same
  shape in `OnGateJoinFriend` and the invite-overlay button. All three now route through new
  `SteamProbe.HostPublish` / `JoinFriendLobby` / `OpenInviteOverlay` `NoInlining` shims
  (`src/Transport/SteamInvite.cs:409-…`). **The rule is now pinned by L155 arm (g):** NO method of
  `MultiplayerUI` may reference a `SteamInvite` member except `WireSteamInviteCore`, the one core
  written to be the isolated fault. Add a new Steam call anywhere in that class and L155 turns red.
- **A throw in the countdown dispatch used to lock the lobby forever.**
  `ReopenAfterFailedStart` was a LOCAL function inside `OnLobbyPlay`, so `Update`'s dispatch could
  not reach it; and `BroadcastLoadBoundaryBegin` + `DropCurtainEarly` ran after `CommitStart()`
  locked the lobby but outside the try. Both moved inside (order preserved — L143 pins the announce
  order), the local function is now a private method, and the `LobbyCountdown.HostTick` switch in
  `Update` is wrapped so ANY throw routes to it.
- **A live SAVE countdown no longer fires under the native NEW GAME screen.**
  `OnLobbyNewCampaign` gates on `ClientsReady`, which excludes `_saveChosen` and `_hostReady`, so
  the button opened while the five-second SAVE clock ran; `CanStart` stayed true and at zero
  `OnLobbyPlay` loaded the OLD SAVE underneath the open native screen. The press now takes the
  host's OWN cancel (`LobbyCountdown.Cancel(engine, null)`), which also clears the host's ready and
  therefore holds the gate shut for as long as the host is in the native flow. **Not a quorum** —
  nobody waits on anybody; the peers' open lobbies repaint off the CLEAR broadcast
  (`LobbyCountdown.Clear` → 0x49 → `HandleCountdown` → `CountdownPanel`). New law **L410**.
- **A retried shared answer no longer charges the shared wallet twice.**
  `EventSync`'s durable native lambda does `Wallet.Take` then `CompleteEvent`; the engine is
  deliberately non-transactional (`DurableInboxEngine.cs:966-969`) and its catch rolls back only the
  ledger, while the lambda's `record.State == Triggered` re-entry guard still passes — so the retry
  paid again, silently, out of everybody's resources. `EventSync.ClaimWalletCharge` (keyed
  occurrence+choice) makes take-and-complete recover as a unit; cleared at the store swap in
  `DurableInboxSession.ActiveStore`'s setter because trigger ids restart with a new store. A REFUND
  was rejected on purpose: `Wallet.Take` clamps to what the wallet holds (`Wallet.cs:55`), so
  giving the full pack back can mint resources. New law **L409**.
- **`DurableWindowRegistry.EnqueuePriorityOccurrence` / `EnqueueCapturedPriority` no longer throw.**
  `CommitWithCanonical` returns false DETERMINISTICALLY while `_nativeTransitionActive`
  (`DurableInboxStore.cs:391`) and when validation refuses (`:405-411`), so all 32 attempts failed
  together and the `InvalidOperationException` was guaranteed — escaping a Harmony Postfix on a
  native UI transition. Both now log loudly and return `false` / `null`; every caller already
  ignored the value or handled the null, so the window still opens, merely not durable-mirrored.
  `ponytail:` there is no deferred retry because there is no durable per-frame host pump to hang one
  on. If these start appearing in Player.log, add one where the geoscape tick already runs.
- **`ConnectCode` grew a check symbol** (`src/Join/ConnectCode.cs`), the same weighted mod-32 scheme
  `InviteCode` uses. **The format changed: 10 symbols "4-2-4" → 11 symbols "4-3-4"**, and old codes
  are rejected. Safe because codes are session-ephemeral and two builds that disagree cannot play
  together anyway (the parity/version gate refuses the join). `SmartJoinParser` now reads
  `ConnectCode.TotalSymbols` rather than a literal 10.
- **`EvaluateStartGate()` no longer runs every frame all session.** It was an ARGUMENT, evaluated
  before `HostTick`'s own `IsHost` guard, so `RefreshGateFacts` → `GetLobbyRoster` → `BuildPeerList`
  allocated a List per frame on hosts, clients and in-game alike. Short-circuited behind
  `engine.IsHost && _lobbyController.State == LobbyState.HostLobby`. `HostTick` itself still runs
  every frame — the NEW CAMPAIGN route does not read the gate.
- **Clipboard writes are guarded at the one shared sink** (`MultiplayerUI.CopyToClipboard`, plus a
  `ClipboardText()` read helper); the three ad-hoc `GUIUtility.systemCopyBuffer` writes now route
  through it.
- **Not touched:** `RecoverPendingDurableChoices` — the audit's "dead code" claim was ruled FALSE.

## 11. Loopback / same-PC join, 2026-08-10

- **Loopback join was never broken and is not a regression.** `SmartJoinParser.Parse` classifies
  `127.0.0.1`, `127.0.0.1:PORT`, `127.x.x.x:PORT` and `localhost[:PORT]` as `DirectIp`
  (`src/Join/SmartJoinParser.cs:65-72`, verified by running the parser standalone), `JoinPlan.Build`
  gives it a `DirectIP` attempt, and the host listens on `TcpListener(IPAddress.Any, port)`
  (`src/Transport/DirectTransport.cs:141`) — so a second instance on the same box really is answered.
  `IsOwnLoopback` (`SmartJoinParser.cs:44`) only refuses a HEALTHY DirectIP host joining itself; a 2nd
  instance whose bind failed is deliberately let through (`MultiplayerUI.cs:859`).
- **What actually fails on a two-instance box:** the join field prefills a clipboard invite CODE when
  one is present (`MultiplayerUI.JoinPrefill`, `MultiplayerUI.cs:482-484`), and on one machine the
  clipboard ALWAYS holds the host's own code. That code carries the UPnP/STUN **WAN** endpoint
  (`GetSessionInviteCode`), so the same-box joiner hairpins to his own router instead of using the one
  address that always works. Nothing on screen named loopback in that state.
- **Fixed by surfacing, not by parsing:** a permanent sub-text hint under the join field
  (`src/Lobby/NetworkGatePanel.cs`, `LocalHint`) and a `This PC (same machine) — 127.0.0.1:14242`
  row on the host's address card (`LobbyPanel.PaintAddressLines`), which reuses the existing pooled
  copyable `AddressLine` row. `LanIpResolver` still drops 127/8 on purpose — its list is "addresses
  someone ELSE can dial"; the local line is added at the card, not in the resolver.
- **`::1` is still Invalid** and that is honest: the transport is IPv4-only (`IPAddress.Any`,
  `AddressFamily.InterNetwork` filter). Add it only when the transport becomes dual-stack.
