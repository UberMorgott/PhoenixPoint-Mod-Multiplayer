# Design spec — the host window journal and scoped reactivity

- Date: 2026-08-15
- Status: APPROVED design. Sections A/B/C are decisions, not proposals. Do not re-open them.
- Audience: a swarm of fresh implementation agents with **zero conversation context**. Everything
  needed to start is in this file. Nothing here may be re-derived by guessing; where a fact is
  missing it is marked **OPEN QUESTION** and must be resolved by reading a real source, not invented.
- Repo: `E:\DEV\PhoenixPoint\Multiplayer2` (Phoenix Point co-op multiplayer mod; C#, Harmony, Unity).
  Windows / PowerShell only.

---

## 0. How to read this document

- Every claim that came from an audit carries a `file:line` anchor. Anchors are as of 2026-08-15;
  if a line has moved, re-locate the SYMBOL (Serena `find_symbol`), do not assume the fact changed.
- "MUST" / "NEVER" are hard. "SHOULD" leaves the implementer a choice they must justify in the commit body.
- Anything not decided here is an **OPEN QUESTION** in §6. Do not fill an open question with an
  invented answer; escalate it or resolve it from a real source and record the source.

---

## 1. Problem statement

Two defects, one root cause each, plus one meta-defect that let both survive a large law suite.

**P1 — window presentation order is not shared.** The host is the authority for game state but not
for the order in which pending windows are shown. Measured in a real 3-instance session
(14:17:41–14:22:43, all three logs complete to shutdown): the host queued **research → event**;
both clients presented **event → research**, 363 ms apart, i.e. exactly one diff cycle. The string
`settled queue re-ordered by rail ordinal` appears **zero** times in all three logs — no peer ever
re-sorted; every peer drained in **local insert order**. The clients were the CORRECT peers (the
event inherited the 0xB6 ordinal, research the later 0xAC ordinal). **The host is the wrong peer.**
Mechanism: on the host both windows are stamped OUTSIDE an apply, so both register provisional and
`RailOrdinal.Mint()` (`src/Rail/RailOrdinal.cs:66-73`) back-fills the WHOLE pending list with ONE
ordinal (the 0xB6 event's) — host research and host event collide and tie to insert order.
Compounding: the client's `SettleSeconds` is 150 ms, shorter than the measured 363 ms inter-channel
skew, so a hold-and-reorder timer could not have saved it either.

**P2 — every replicated change repaints everything.** The rail knows the exact leaf that changed
(`kindId, path, fieldIdx, subKey, value` at `src/Rail/GenericApplier.cs:272-278`) and then throws
it away: `src/Rail/GenericApplier.cs:1272` does `touched.Add(entity)` — path and field are DISCARDED
there, and the very next line `MarkOrderChange(geo, path, entity, field.Name)` proves both were
still in hand. Downstream the information degrades further: `UiEventMap.Fire(touched, geo)`
(`src/Rail/GenericApplier.cs:323`, second site `:508`) sees a set of entity INSTANCES; the switch in
`src/Rail/UiEventMap.cs:75-190` collapses that to a TYPE; `OpenUiRepaint.MarkDirty(Type, geo)`
(`src/Rail/OpenUiRepaint.cs:746`) collapses the type to **one global bool** `_dirty = true`
(`:728`). Consequence: an unrelated peer's manufacturing tick repaints the soldier-edit screen,
which routes to a DESTRUCTIVE native refresh and resets the soldier model and animation.

**P3 (meta) — the law suite cannot see either defect.** Almost every law is STATIC IL analysis: it
asserts a seam's SHAPE, stays green, while behaviour regresses. Proven four times on 2026-08-15:
`L94_LoadBarrier` (67 KB, owner of the load barrier) green across ~8 recurrences; `L507` green while
window order was visibly broken; `L497` green while the level-up cross never repainted; `L496`
actively LEGITIMISED a duplicate gate. `L38` is green with ONE row in `IgnoredKinds` and green for
over-repaint of every undeclared screen, and its "no arm of `Fire` reaches parameterless
`MarkDirty()`" arm covers only `UiEventMap.Fire` — the ~63 kindless call sites are outside its reach.
**No law anywhere executes "an unrelated change did NOT repaint surface X"** — the over-repaint
defect is invisible to the whole law set by construction. `L512`/`L514` say in their own text that
they do not prove visibility (no Unity hierarchy in the harness). `L507`'s specific blind spot: it
executed both roles in ONE process, so a host-only ordering fault could not appear.

---

## 2. Established facts (verified by five read-only audits on 2026-08-15)

These are inputs, not tasks. Do not re-audit them; cite them.

### 2.1 Architecture (settled, NOT up for debate)

- Authoritative host + reflective state-replication rail (`src/Rail/DiffEngine.cs`,
  `src/Rail/GenericApplier.cs`) addressing state by PATH, e.g.
  `S#76.SerializationData.HavenData.AssignedResearchId`.
- Full lockstep determinism is impossible here: ~120 unseeded RNG draws across ~60 geoscape files,
  float drift, and third-party mods (e.g. TFTV) mutating the same state.
- Prior art confirms the choice. Stardew Valley's shipped single-player→co-op retrofit used
  reflective field replication from a root. RimWorld Multiplayer chose lockstep, and its documented
  desync recovery is "the human reconnects" — which violates this project's no-blockers rule.

### 2.2 Window subsystem — current state

**Chokepoint.** A single chokepoint already exists: `GeoscapeViewSwitchQuery.QueryStateSwitch`.
Explicit callers: `src/Rail/EventPopup.cs:992`, `src/Rail/GeoModalMirror.cs:811`,
`src/Rail/WindowQueueSync.cs:388`. Everything else reaches it via `OpenModal`. All requests are
stamped by `ReplenishSync.QueueRankPatch` → `WindowOrder.Stamp` (`src/Rail/ReplenishSync.cs:135-156`).

**Bypasses.** Exactly TWO genuine bypasses exist:
1. save-restore `RestoreData` (`src/Rail/WindowOrder.cs:71`);
2. the mission-outcome modal raised from `UIStateInitial.EnterState:112` (`src/Rail/WindowOrder.cs:427`).

**Raise sites per role.**
- EVENT window: host runs native and the mod CAPTURES (`src/Rail/EventPopup.cs:336-414`); the client
  native raise is BLOCKED (`src/Rail/ClientSimGate.cs:361-370`) and the window is mod-served from the
  0xB6 apply (`src/Rail/EventPopup.cs:992`) carrying the host's priority.
- RESEARCH window: host raises natively — `GeoscapeView.OnFactionResearchCompleted:1980 →
  OpenModal(...,99)` — and the mod only observes (`src/Rail/ResearchSync.cs:555-564`); client sim is
  blocked (`ClientSimGate.ClientResearchGate`) and the window is REPLAYED through the private native
  handler (`src/Rail/ResearchSync.cs:389`).

**Order keys today (two of them, which is the bug).**
- `WindowOrder.OrderKey.Ordinal` (= `RailOrdinal`) is on EVERY request, including research.
- `DurableInboxModel.HostOrderKey:97` covers only durable occurrences, bound at
  `src/Rail/EventPopup.cs:993` via `WindowOrder.BindDurable`.
- Restored requests carry ordinal 0 (`src/Rail/WindowOrder.cs:541-545`).

**THE ROOT ARCHITECTURAL FAULT.** The DurableInbox surgery (2026-08-09→11: commits `93fed1a` +686,
`55e5f82` +321, `ae2099d` +709, `adc31a0` +1271, `d193852`; on top of `60fb0bf` 2026-08-05 +883
introducing `RailOrdinal`/`WindowOrder`, and `81afe12` 2026-08-01 +480) added a **SECOND complete
ordering system** (ledger + `HostOrderKey` + suspend/resume/preemption) BESIDE the existing one
(`RailOrdinal` + settle + reorder) and wired both into the same drain
(`WindowOrder.ReadyToDequeue`). Neither is authoritative, so which one decides depends on whether a
request happens to be durable-bound. Files still live, ~380 KB total:
`DurableInboxCodec.cs`, `DurableInboxEngine.cs`, `DurableInboxModel.cs`, `DurableInboxState.cs`,
`DurableInboxStore.cs`, `DurableWindowRegistry.cs`, `WindowQueueSync.cs` (85.6 KB),
`GeoWindowCoverage.cs` (53 KB), `WindowOrder.cs` (40.6 KB).

**BLOCK/CAPTURE MIXED ON ONE CONCERN — the recurrence mechanism.** The same concern is served by
two or three different strategies at once; this is why fixes keep regressing:
- (a) event window — BLOCK on client (`src/Rail/ClientSimGate.cs:361`) + CAPTURE on host
  (`src/Rail/EventPopup.cs:336`), and the block is conditionally REOPENED for the capture path via
  the `SyncApplyScope.Active` escape (`src/Rail/ClientSimGate.cs:365`);
- (b) research — BLOCK on client (`src/Rail/ClientSimGate.cs:558`) + CAPTURE-by-observation on host
  (`src/Rail/ResearchSync.cs:555`) + a THIRD strategy, REPLAY of the private native handler
  (`src/Rail/ResearchSync.cs:389`);
- (c) host capture doubled — `GeoModalMirror.HostBroadcastQueued` at the `QueryStateSwitch` postfix
  (`src/Rail/GeoWindowCoverage.cs:663-676`) coexists with `EventPopup`'s explicit 0xB6 broadcast
  (`src/Rail/EventPopup.cs:411`);
- (d) ordering authority doubled on one queue — `WindowOrder.ReadyToDequeue` consults
  `DurablePriorityHead`/`HostOrderKey` (`src/Rail/WindowOrder.cs:128-141, 496-499`) AND
  `Reorder`/`RailOrdinal` (`src/Rail/WindowOrder.cs:528`);
- (e) presentation policy doubled then undoubled — commit `dc0a148` deleted the client's
  `DurableWindowRegistry.MayPresent` pre-gate because it was a second gate beside
  `WindowOrder.HoldsForOpenScreen`, and `L496` had been written to LEGITIMISE the pair.

**Queue lifecycle (verified).** Creation, queueing and publication are ALL view-state-independent:
`HoldsForOpenScreen` (`src/Rail/WindowOrder.cs:354-358`) is consulted only from the drain
(`ReadyToDequeue` `:453+`, `HoldsHead` `:325`); `DurableWindowRegistry.MayPresent` (`:367`, `:405`)
gates only client-side durable PRESENTATION. Publication is INDEPENDENT of presentation
(`src/Rail/GeoWindowCoverage.cs:671`; `src/Rail/GeoModalMirror.cs:365-372`, `:1042-1047`,
`:1061-1068`; `HostBroadcast` gates only on host/session/`SyncApplyScope` at
`src/Rail/GeoModalMirror.cs:378-382`) — **but publication IS coupled to a live `GeoscapeView`
existing**: no view → no postfix → no publication. That coupling is the one remaining structural
dependency and §3.A.4 addresses it.

**Per-peer asymmetric removal.** `DeploymentWindow.DropUnservableQueued`
(`src/Rail/DeploymentWindow.cs:490-524`) removes requests whose `MissionBehind()` (`:527`) is
`!Servable()` (`:536`). The predicate reads GAME state, not view state. It is local-only with no
broadcast (`src/Rail/WindowQueueSync.cs:203-214`) and returns early when `view == null` (`:505`).
For a single global order this is a defect: the removal is PER-PEER and ASYMMETRIC.

**Queue cap.** `GeoWindowCoverage.QueueCap = 64` (`src/Rail/GeoWindowCoverage.cs:586`); `TrimQueue`
removes from the TAIL (`:620-635`) — i.e. drops the NEWEST — with an error log on the 1st and every
32nd drop. There is no time-based staleness and no "newest of family" collapse. Other removals:
`DropResolvedSubjects` (`src/Rail/WindowOrder.cs:437-451`, by subject resolved, run before every
drain), `DropUnservableQueued`, and restore-time filtering. Preemption SUSPENDS/RESUMES rather than
deletes (`WindowQueueSync.TryDurablePriorityPreemption`, `src/Rail/WindowOrder.cs:497`).

**One client-only NEVER-CREATED case.** A research completion latched before this peer has ever
stood on a map surface is swallowed (`src/Rail/ResearchSync.cs:117-121`, `:320`, the
`_everOnMapSurface` latch; L475's invariant).

**Tactical boundary.** During a battle `GameUtl.CurrentLevel()` is the tactical level and
`GenericApplier.GeoLevel()` returns null (`src/Rail/GenericApplier.cs:130-136`); there is no
`GeoscapeView`, no `QueryStateSwitch`, no geoscape sim. Vanilla serialises the queue across the
battle: `GeoscapeView.GetStateSwitchInstanceData:1298-1300` → `GeoscapeViewSwitchQuery.RestoreData:39-56`,
replayed at `GeoscapeView.cs:349`. The mod additionally prunes resolved subjects on restore
(`src/Rail/WindowOrder.cs:437-451`).

**Coverage table.** `GeoWindowCoverage` is a TOTAL table over all 41 `ModalType` values;
not-covered surfaces are DECLARED, not unknown: mission-outcome family
(`src/Rail/GeoWindowCoverage.cs:313`, 11 ModalTypes, residual raiser
`GeoscapeView.OnSiteMissionCancelled:1930`), pandoran reveal (`:337`), interception brief/outcome
(`:342`, flagged a campaign-stopper), alien intelligence brief (`:355`), four unraised types (`:361`).

### 2.3 Reactivity subsystem — current state

**Pipeline, with the granularity available at each stage:**

1. `GenericApplier.ApplyEntry` holds `kindId, path, fieldIdx, subKey, value`
   (`src/Rail/GenericApplier.cs:272-278`) — **EXACT LEAF**.
2. `src/Rail/GenericApplier.cs:1272` — `touched.Add(entity)`; **path and field are DISCARDED here**.
   The next line `MarkOrderChange(geo, path, entity, field.Name)` proves both were still in hand.
3. `UiEventMap.Fire(touched, geo)` (`src/Rail/GenericApplier.cs:323`, second site `:508`) —
   **SET OF ENTITY INSTANCES**.
4. `UiEventMap.Fire` switch (`src/Rail/UiEventMap.cs:75-190`): 8 kind arms — Wallet,
   Research/ResearchElement, CharacterProgression, GeoCharacter, GeoPhoenixFacility,
   GeoscapeEventSystem, CharacterIdentity, ItemStorage — each runs a native derive then
   `MarkDirty(entity.GetType(), geo)`. `Timing`/`TimingInstanceData` (`:107-114`) is the only no-op.
   `default:` (`:179-189`) marks and logs once — **TYPE**.
5. `OpenUiRepaint.MarkDirty(Type, geo)` (`src/Rail/OpenUiRepaint.cs:746`) consults
   `UiNativeRepaint.IgnoredKinds` (`src/Rail/UiEventMap.cs:661-665`), else `MarkDirty()` (`:728`)
   → `_dirty = true` — **ONE GLOBAL BOOL; all type information dies here**.
6. `SyncEngine.Tick` → `FlushIfDirty()` (`src/Rail/OpenUiRepaint.cs:893`) with a drag/typing defer
   (`LocalInputInFlight`, ≤300 frames) and coalescing to 1 repaint/frame.
7. `RepaintOpenGeoscapeScreen()` (`:959`) → `RefreshPersistentHud()` → `DropUnservableQueued()` →
   `StageSnapshot.Capture` → `Repaint`.
8. `RefreshPersistentHud` (`:158`) — 5 module refreshes: agenda tracker, info bar, site contextual
   menu, vehicle crew strip, roster slots.
9. `Repaint` (`:~985`) — `UiNativeRepaint.TryRepaint` over 17 Table entries keyed by view-state
   Type, else queued-window SKIP, else Exit+Enter on the live state (`:518-535`).

**Kindless call sites.** ~63 kindless `OpenUiRepaint.MarkDirty()` call sites across 11 syncer files
(AssignSync 9, PersonnelSync 15, MissionSync 12, VehicleSync 6, GenericApplier 8 structural,
IntentRail 3, DeployPrep 2, DeploymentWindow 2, EquipSync 2, TradeSync 1) carry NO path at all —
intent rejects, structural create/destroy, reseeds. These MUST keep an unconditional
"repaint everything" arm.

**Hand-rolled per-surface repaint keys — to be DELETED as path scoping lands.** They are read-sets
written backwards, i.e. exactly the v1 per-widget sync the user abandoned:
`"agenda"`/`AgendaSignature` (`src/Rail/OpenUiRepaint.cs:~465`), `"infobar"`/`InfoBarKey` (`:514`,
which admits its own incompleteness via a 1-second `Time.realtimeSinceStartup` floor),
`"crew"`/`CrewSignature` (`:303`), `"roster"`/`RosterSignature` (`:215`), `CrewSlotKey` (`:~375`,
shared by crew+roster). **KEEP** `RepaintNeeded(strip, key)` (`:439`) as the fallback primitive.

**KNOWN BUG to fix in passing.** `RefreshInfoBar` consults `RepaintNeeded` (`:514`) BEFORE the
`bar == null` / `_context == null` bails (`:~524`), so a key change is consumed and lost while the
module is not yet Init'd. Same class as `efc4782` / L516 (`stripOnScreen && RepaintNeeded(...)`).

**DESTRUCTIVE native refreshes** (recreate GameObjects / restart animation). Scoping alone will NOT
fix these:
- `UIStateEditSoldier.DisplaySoldier` → `UIModuleActorCycle.DisplaySoldier(c, resetAnimation: true)`
  (decompile `UIStateEditSoldier.cs:584`) → `CommonCharacterUtils.ResetCharacterAnimation` =
  `Animator.Play(0,-1,0f)` (`CommonCharacterUtils.cs:66-73`), or `RebuildCharacter` +
  `CharacterLoadingIndicator.SetActive(true)` (`UIModuleActorCycle.cs:638-654`).
  **This is the reported soldier-model/animation reset.**
- `UIStateEditVehicle.DisplaySoldier` → `DisplayVehicle(c, resetAnimation: true)`
  (`UIStateEditVehicle.cs:348`).
- `UiEventMap.ReseedIdentityDisplay` clears `AddonsCharacterBuilder.Addons`
  (`src/Rail/UiEventMap.cs:325`), forcing the `RebuildCharacter` branch.
- `UIModuleFactionAgendaTracker.InitialSetup:144`.
- `UIModuleVehicleSelection.SetCrew:401` → `AircraftCrewController.SetCrew:56` →
  `UIUtil.EnsureActiveComponentsInContainer`.
- `UIModuleBaseLayout.SetupBaseLayout`.
- `UIStateGeoRoster.OnActorStatChanged:364` → `_geoRosterModule.Init(...)`.
- `UIModuleReplenish.Init`.
- The Exit+Enter fallback — documented to have restarted a cutscene 7×.

**NON-DESTRUCTIVE** paints: `GeoRosterItem.UpdateCharacterData():239`,
`UIModuleInfoBar.UpdatePopulation():276-288`, the `BaseStat.StatChangeEvent` echo →
`RefreshCrewBars` (`src/Rail/RailTypes.cs:132-134`, L497),
`UIStateNothingSelected.OnFactionObjectivesChanged`, `UIStateVehicleSelected.OnFactionObjectivesChanged`,
`SetLeftSideInfo`.

**Known-good pattern already in the mod:** `RepaintAugmentScreen` (`src/Rail/UiEventMap.cs:939`)
reaches `UIModuleActorCycle.DisplaySoldier(c, resetAnimation: false, ...)` directly instead of going
through the state's private method. Copy this shape.

**Level-up cross** has TWO paint sites: `AircraftCrewController:140` and `GeoRosterItem:345`
(inside private `UpdateLocations`, owner `UIModuleGeoRoster`, `GeoscapeModulesData:30`).

### 2.4 Logging convention (extracted 2026-08-15 — this is the regimen to follow)

**Sink.** `Multiplayer.Util.MultiplayerLog` writes to
`%USERPROFILE%\AppData\LocalLow\Snapshot Games Inc\Phoenix Point\Multiplayer\multiplayer.log`.
The path is `Application.persistentDataPath` + `Multiplayer/` + `multiplayer.log`
(`src/Bootstrap/MultiplayerLog.cs:61-64`). A second same-machine instance finds the primary locked
and falls back to `multiplayer-2.log`, a third to `multiplayer-3.log`, up to `MaxInstances = 5`
(`:25`, `:72-102`). **Host = `multiplayer.log`** by lock order (first instance up wins), and
`MultiplayerLog.InstanceIndex` (`:45`, set at `:93`) is the SINGLE authoritative instance signal —
never re-derive instance-ness with another probe. Each base name keeps its own 9-slot rotation ring
`multiplayer-prev.log … multiplayer-prev9.log` capped at 64 MB total (`:28-29`, `:127-159`).
Lines are written as `HH:mm:ss.fff <LogType> <line>` (`:194-195`). Every write is lock-guarded and
swallows its own exceptions — logging must never propagate into the game loop (`:198-201`).

**API surface — `Multiplayer.MpLog` (`src/Bootstrap/MpLog.cs`), the mod's ONLY logging door:**

| Method | Signature | When to use |
|---|---|---|
| `MpLog.Log(object message)` | `src/Bootstrap/MpLog.cs:13` | Normal informational line. `message` MUST already start with `[Multiplayer][tag]` or `[MP][tag]`; `Normalize` (`:61-81`) rewrites it to canonical `[MP][tag] …`. This is the dominant form in the codebase. |
| `MpLog.LogWarning(object message)` | `:14` | A recoverable anomaly, a gap, a fallback taken. Never gated by a diagnostic flag. |
| `MpLog.LogError(object message)` | `:15` | A real failure: broken invariant, dropped state, unrecoverable decode. Never gated. |
| `MpLog.Log(string category, string message)` | `:17-18` | Same as above with the tag passed separately; `Format` (`:53-58`) trims, strips brackets, lowercases and emits `[MP][category] message`. Currently **0 call sites in `src/`** — both forms are legal; prefer the prefixed-string form for consistency with the ~1500 existing sites. |
| `MpLog.LogWarning(string category, string message)` | `:20-21` | as above |
| `MpLog.LogError(string category, string message)` | `:23-24` | as above |
| `MpLog.LogError(string message, Exception exception)` | `:26-31` | Failure with a caught exception; the exception is appended on its own line. |

Routing: `MpLog.Write` (`:33-51`) reads `MultiplayerConfig.WriteDedicatedLog` and
`.WritePlayerLog` live, and mirrors to `UnityEngine.Debug.Log/LogWarning/LogError` only when the
Player.log destination is enabled. Empty/blank category falls back to `general` (`:55`).

**Tag taxonomy.** A tag is a lowercase `[a-z0-9-]+` subsystem name chosen by the SUBSYSTEM the line
belongs to, not by the file. The real set in `src/` as of 2026-08-15 (count = occurrences):

```
tac 289 · rail 152 · events 56 · deploy 36 · windows 33 · personnel 32 · mission 29 · modals 17
equip 16 · replenish 16 · return 15 · assign 14 · inbox 14 · market 14 · tac-native 13 · outcome 13
vehicle 13 · uirepaint 12 · sessionend 11 · lobby 10 · cutscene 10 · intent 10 · diag 8 · base 8
hint 8 · pause 7 · site 7 · join 6 · clockphase 6 · scrap 6 · time 6 · cheatlock 5 · teardown 4
scrapcart 4 · research 3 · brief 3 · lan 2 · quickproduce 2 · category 2 · P2PRepair 2
steam-invite 2 · input 2 · inv 2 · mfgdiag 2 · tftv 2 · restart 2 · trade 2 · faction 1
consolelock 1 · logging 1
```

New tags ARE allowed — `Format` accepts any string and nothing enumerates the set — but a new tag
must be a genuinely new subsystem. **For this work use the EXISTING tags:** `[MP][windows]` for the
journal, `[MP][inbox]` for durable/answer-once semantics, `[MP][uirepaint]` for scoped reactivity,
`[MP][rail]` for anything in `GenericApplier`/`DiffEngine`/`RailMeta`. Do not invent
`[MP][journal]` or `[MP][scope]`.

**Suppression / digest mechanics.** Two patterns, both mandatory on any path that can repeat.

1. **Counted once-logger with a 30 s digest** — `RailMeta.CountMiss(string line)`
   (`src/Rail/RailMeta.cs:1456-1466`). Returns `true` on the FIRST sighting of an exact line
   (caller prints it) and counts every later sighting. Capped at 500 distinct families (`:1460`).
   It self-pumps the digest: every call checks a `Stopwatch` (`:1453` — a BCL clock deliberately,
   because `UnityEngine.Time` is an ECall that throws outside the player and RailCheck executes
   this codec in-process) and every `DigestIntervalSec = 30.0` (`:1448`) flushes
   `FlushMissDigest` (`:1472-1488`), which emits
   `[MP][rail] mirror-gap digest (30s): N family(ies) repeated, M suppressed line(s):` followed by
   one `×<total> total (+<delta> since last digest) <line>` per family (`:1481-1487`).
   Opt-in for a caller = wrap the emit: `if (RailMeta.CountMiss(line)) MpLog.LogWarning(line);`
   — exactly `WarnOnce` (`src/Rail/RailMeta.cs:1494-1498`). Callers today:
   `src/Rail/GenericApplier.cs:95-96` (flush + reset at a reload boundary, so the tail is never
   lost), `src/Rail/GenericApplier.cs:2184`, `src/Rail/TacAbilityTargetCodec.cs:307`.
   `ResetMissTally()` (`src/Rail/RailMeta.cs:1492`) clears the tally at a reload boundary — a
   reload is a new run and a family that went quiet must be able to speak again.
2. **Once-per-key dictionary** — the `_crcBehindLogged` pattern in
   `src/Rail/DiffEngine.cs:383` (declaration, `Dictionary<string,uint>` keyed `"<peer>|<root>"`),
   `:430` (cleared at the boundary), `:603-609` (log only when the stored value differs from the
   current touch-seq). Use this when the "same line" is not literally the same string but the same
   logical EVENT for a key, and you want one line per (key, generation).

**Hot-path / per-frame rule.** Informational per-entry or per-tick traces MUST be guarded by
`if (MpDiag.On)` (`src/Net/MpDiag.cs`), and the guard must wrap the STRING CONCATENATION too, not
just the write — "the concatenation, not the write, is the expensive half on the hot paths"
(`src/Net/MpDiag.cs` class doc). 25 guard sites exist today. `MpDiag.On` is one switch for all
investigation-diag families (`[MP][inv]`, `[MP][mfgdiag]`, `[MP][scrap]`, `[MP][uirepaint]`,
`[MP][sessionend]`), on by default for the current testing phase, forced on by the environment
variable `MULTIPLAYER_DIAG` (any non-empty value). Evidence of why this matters: one live
3-instance run logged **23642 `[MP][inv]` lines** on the host and ~1600 `mfgdiag` lines per client,
inflating the log ~3× and costing real frame time in string building alone.
**Warnings and errors are NEVER gated** — a real failure must be visible without re-running with a
flag set (same doc). A repaint or a journal drain runs every frame: any new trace there is
`MpDiag.On`-guarded, or `CountMiss`-suppressed, or both.

**Law L432 — EVERY MOD LOG USES ONE DOOR** (`tools/RailCheck/L432_EveryLogUsesTheOneDoor.cs`).
It asserts, and turns RED when any of these breaks:
- `MpLog.Write`, `MpLog.Normalize`, `MultiplayerConfig.WriteDedicatedLog`,
  `MultiplayerConfig.WritePlayerLog`, `MultiplayerConfig.EnableDiagnosticLogging` and
  `MpDiag.On` all still resolve (`:29-41`, else `L432 premise-changed`);
- all three settings DEFAULT to enabled (`:43-46`, else `L432 defaults-not-on`);
- `Normalize("[Multiplayer][tac] test")` returns exactly `"[MP][tac] test"` (`:48-51`, else
  `L432 prefix-not-canonical`);
- `MpLog.Write` reads BOTH destination fields and `MpDiag.On` reads `EnableDiagnosticLogging`
  (`:53-57`, else `L432 routing-switch-bypassed` / `L432 diagnostic-switch-bypassed`);
- **no method in the whole mod assembly calls `UnityEngine.Debug.Log*` or
  `PhoenixPoint.Modding.ModLogger.Log*` directly** (`:59-82`, else `L432 logging-door-bypassed`).
  Two exemptions only: methods declared on `MpLog` itself, and `MultiplayerLog.Init` (`:78`) —
  the recursion-safe fallback when the dedicated writer failed;
- positive control: `MpLog` still reaches `UnityEngine.Debug` at all (`:83-85`, else
  `L432 player-route-vacuous`).

**Practical rule for the swarm:** one `Debug.Log` anywhere in `src/` turns RailCheck RED. Always
`MpLog`.

**What must NEVER be logged.** There is no automated law on log content, so this is discipline:
never log secrets or credentials, never log a Steam auth ticket or session token, and do not dump
whole payload blobs on a hot path. Steam IDs and peer ids already appear in logs (e.g. the CRC
backstop line at `src/Rail/DiffEngine.cs:606-608`) and are acceptable. **OPEN QUESTION (Q7)**:
whether Steam persona names / avatars introduced in 0.9.15-beta are subject to a stricter rule —
not decided; do not add new persona-name logging until it is.

### 2.5 Prior art — each of these is a DESIGN CONSTRAINT, not trivia

- **Change detection must fire on VALUE INEQUALITY, not on "was written".** Bevy `set_if_neq`
  (https://bevy-cheatbook.github.io/programming/change-detection.html), Unity DOTS chunk version
  numbers (https://docs.unity3d.com/Packages/com.unity.entities@1.0/manual/systems-version-numbers.html).
  Marking on write is the direct cause of the manufacturing-resets-the-soldier symptom.
- **Identity-preserving repaint ("patch, don't rebuild") is a SEPARATE fix from scoping.**
  React's state/animation reset on remount
  (https://react.dev/learn/preserving-and-resetting-state).
- **Missed-dependency failure class:** automatic read tracking only sees synchronous reads, so
  declarations must be STATIC path prefixes, never inferred at runtime
  (https://mobx.js.org/understanding-reactivity.html).
- **Mark during the batch, repaint ONCE at batch end** — two-phase mark/evaluate avoids glitches
  (https://dev.to/milomg/super-charging-fine-grained-reactive-performance-47ph).
- **Prefix subscriptions are proven, but prefix DEPTH is the whole tuning knob;** a too-shallow
  prefix repaints on everything (https://firebase.google.com/docs/database/usage/optimize).
- **Address list elements by STABLE ID, never by index** — index paths churn on insert
  (RFC 6902, https://datatracker.ietf.org/doc/html/rfc6902).
- **Identity/reference memoization is useless on state the game mutates in place** — compare by
  value or hash (https://reselect.js.org/faq/).
- **Total order exists only within ONE partition; do not shard the ordered stream**
  (Kafka, https://kafka.apache.org/081/getting-started/introduction/). Two order keys already IS
  this bug.
- **The value of a sequencer is that CLAIMING THE SEQUENCE IS THE ONLY WAY IN**, so bypasses cannot
  exist by construction (LMAX Disruptor,
  https://lmax-exchange.github.io/disruptor/user-guide/index.html ; single-writer principle,
  https://hftengineer.com/posts/single-writer-principle/).
- **A gap must self-release on an armed timer AND be resolved by an explicit host-minted "void N"
  record** — without an explicit void, two peers time out differently and diverge again
  (FIX gap-fill, https://www.onixs.biz/fix-dictionary/4.4/msgtype_4_4.html ; reordering timers,
  https://patents.google.com/patent/US7839813).
- **Capture-then-reconcile at a GENERIC callback seam** is how RimWorld MP absorbs third-party UI
  raises; enumerating call sites cannot cover a mod you do not control
  (https://deepwiki.com/rwmt/Multiplayer/3-synchronization-system). RimWorld's dominant desync
  source is "unsynchronized interface interactions"
  (https://deepwiki.com/rwmt/Multiplayer/7-determinism-and-desyncs).
- **Presentation must READ replicated state and NEVER WRITE it** (RimWorld's `InInterface` vs
  `Ticking` split, same URL). This is what prevents repaint-generated rail traffic.
- **Opt-in / per-feature manual sync APIs fail:** tModLoader documents that forgetting
  `SyncPlayer`/`SendClientChanges` "will lead to desync"
  (https://github.com/tModLoader/tModLoader/wiki/Basic-Netcode). Universal-by-default beats a good
  API. This is exactly the v1 approach the user abandoned.
- **Incremental patching of retrofit netcode repeatedly ends in a rewrite:** Barotrauma
  (~9-month from-scratch rewrite, https://undertowgames.com/blog/), Project Zomboid (two ground-up
  rewrites, https://projectzomboid.com/blog/news/2021/12/multiplayer-multiplier/).
  **Conclusion adopted: get the TWO seams right once, and touch nothing else.**

---

## 3. The approved design

**Mental model, in the user's own words: phone push notifications with a notification shade.**
The host distributes pushes to everyone. Some pushes are dismissed locally and remain for others.
Mission-related pushes, once acted on by ANYONE, are dismissed for EVERYONE — because the decision
to deploy has already been taken and it is meaningless for the others to accept or refuse; they
instead get a CENTRE-OF-SCREEN button to enter deployment preparation.

### A. The window journal

**A.1 — One authority: the host's order.**
The HOST's queue is the single source of presentation order. The host publishes its ORDERED list of
pending windows. The client does NOT invent a key and does NOT sort — it reconciles its own queue to
the published list. (The user chose this over a hold-and-reorder timer; the measured 363 ms skew vs
the 150 ms `SettleSeconds` shows why the timer approach cannot be made correct by tuning.)

Consequence, from the LMAX/Kafka constraints in §2.5: there is exactly ONE ordered stream and
claiming a position in it is the ONLY way a window can exist. Any code path that can present a
window without a journal position is a bypass and must be closed (§A.7).

**A.2 — Append-only journal, per-peer read cursor.**
The journal is APPEND-ONLY. Each peer has its OWN read cursor. A peer who is not looking accumulates
everything and loses nothing. **Nobody ever waits for another peer** — no quorum, no consensus gate,
no ready-vote, no wait on a human ACTION. (Waiting on a LOAD that ends by itself remains allowed and
is how the curtain/reveal barrier legitimately works — see §7.)

**A.3 — Lifetime: the whole session, across the tactical boundary.**
The journal is owned by the MOD and survives the whole session INCLUDING the tactical boundary; the
native queue is its MIRROR wherever a screen exists. Constraint clarified by the user: peers are
never split across layers — either ALL players are in tactical or ALL are on the geoscape. There is
therefore no "host alone in battle" case. The requirement is only that the journal SURVIVES the
battle rather than being rebuilt from the native serialised queue
(`GeoscapeView.GetStateSwitchInstanceData:1298-1300` → `GeoscapeViewSwitchQuery.RestoreData:39-56`,
replayed at `GeoscapeView.cs:349`).

**A.4 — Creation, queueing and publication are screen-independent.**
They must NEVER depend on which screen the host is currently in. Only DISPLAY is postponed. This is
already true for non-geoscape screens (§2.2 "Queue lifecycle"). The ONE remaining structural
dependency is that publication rides the `QueryStateSwitch` postfix and therefore needs a live
`GeoscapeView` (`src/Rail/GeoWindowCoverage.cs:663-676`). Moving the append to the journal itself —
which exists with or without a view — removes that dependency. The implementer MUST state, in the
commit body, where the journal append now happens and that it is reachable with `GeoLevel() == null`.

**A.5 — Dismissal is a DECLARED PROPERTY of a window family, never a special case.**
Each family declares its dismissal scope: **LOCAL** or **GLOBAL**. **Default is LOCAL.** Only the
MISSION family is GLOBAL. A GLOBAL dismissal is effected by an explicit **host-minted void record**
that removes the entry from every peer's unpresented backlog and closes it if already open — never
by each peer independently deciding (that is the FIX gap-fill constraint in §2.5: without an
explicit void, two peers time out differently and diverge).
**A new window family needs NO new code: undeclared ⇒ local.** The declaration table is the only
place a family's scope may be written; no `if (family == …)` anywhere else.

**A.6 — Removals reconciled with the single global order.**
- The `QueueCap = 64` tail-trim (`src/Rail/GeoWindowCoverage.cs:586`, `:620-635`) is **REMOVED**.
  Dropping the NEWEST directly contradicts accumulation. Replacement: the journal is unbounded in
  the normal case, with a **hard safety bound of 4096 entries**; on breach, log ONE
  `MpLog.LogError("[Multiplayer][windows] …")` line (once per session, `_crcBehindLogged`-style
  once-per-key, §2.4) and stop APPENDING while continuing to serve the existing backlog. Never
  silently drop an accepted entry. Rationale for a bound at all: a runaway raiser must not exhaust
  memory; rationale for it being far above any real session: 64 was reached by normal play.
  **OPEN QUESTION (Q1)**: whether 4096 is the right number — it is a documented guess, not a
  measurement. Measure a long session's journal length before treating it as final.
- `DeploymentWindow.DropUnservableQueued` (`src/Rail/DeploymentWindow.cs:490-524`) currently removes
  per-peer and asymmetrically, from a GAME-state predicate, with no broadcast
  (`src/Rail/WindowQueueSync.cs:203-214`) and an early return when `view == null` (`:505`).
  Under a single global order the removal MUST become a host decision expressed as the same
  host-minted void record as §A.5: the host evaluates `Servable()` (`:536`) and mints the void;
  clients apply it. A client MUST NOT remove a journal entry on its own `Servable()` evaluation.
  The local `DropUnservableQueued` may remain as a NATIVE-queue hygiene step only, and must not
  touch the journal.

**A.7 — What is deleted as ORDERING AUTHORITY.**
- The provisional `RailOrdinal` back-fill for windows (commit `d449ea4`;
  `RailOrdinal.Mint()` at `src/Rail/RailOrdinal.cs:66-73`) — as a WINDOW ordering authority.
  `RailOrdinal` itself stays for whatever else uses it; window order stops consulting it.
- The second system: `DurableInboxModel.HostOrderKey:97`, its binding at
  `src/Rail/EventPopup.cs:993`, `WindowOrder.BindDurable`, `DurablePriorityHead`
  (`src/Rail/WindowOrder.cs:128-141`, `:496-499`) and the suspend/resume preemption
  (`WindowQueueSync.TryDurablePriorityPreemption`, `src/Rail/WindowOrder.cs:497`).
- `WindowOrder.Reorder` (`src/Rail/WindowOrder.cs:528`) and the settle timer as an ordering device.
- **`DurableInbox` may survive ONLY as "answer exactly once" semantics — never as a sorter.**
  Files expected to SHRINK or GO: `src/Rail/DurableInboxCodec.cs`, `src/Rail/DurableInboxEngine.cs`,
  `src/Rail/DurableInboxModel.cs`, `src/Rail/DurableInboxState.cs`, `src/Rail/DurableInboxStore.cs`,
  `src/Rail/DurableWindowRegistry.cs`, `src/Rail/WindowQueueSync.cs` (85.6 KB),
  `src/Rail/GeoWindowCoverage.cs` (53 KB), `src/Rail/WindowOrder.cs` (40.6 KB).
  Deletion over addition: a net-negative diff here is the expected shape of a correct patch.

**A.8 — Close the two bypasses.**
1. save-restore `RestoreData` (`src/Rail/WindowOrder.cs:71`) — restored requests must acquire a
   journal position rather than carrying ordinal 0 (`src/Rail/WindowOrder.cs:541-545`);
2. the mission-outcome modal from `UIStateInitial.EnterState:112` (`src/Rail/WindowOrder.cs:427`).
   Note this belongs to the mission-outcome family that `GeoWindowCoverage` DECLARES not-covered
   (`src/Rail/GeoWindowCoverage.cs:313`, 11 ModalTypes). Closing the bypass means the raise claims a
   journal position; it does NOT mean the whole family becomes rail-covered in this work.

**A.9 — Block vs capture: one strategy per concern.**
The recurrence mechanism in §2.2 is that one concern is served by two or three strategies at once.
The rule going forward: **for each window family, exactly ONE of BLOCK-and-serve or
CAPTURE-and-publish, chosen once and applied to both roles.** The `SyncApplyScope.Active` escape
that conditionally reopens a block (`src/Rail/ClientSimGate.cs:365`) is a symptom of the mixture; it
may only survive if it is the single strategy for that family. Concretely for the two known families
(**OPEN QUESTION (Q2)** — which of the two strategies each family ends on is NOT decided here;
decide it from the code and record the decision in `docs/laws.md` and the commit body):
- event: today BLOCK on client (`src/Rail/ClientSimGate.cs:361-370`) + CAPTURE on host
  (`src/Rail/EventPopup.cs:336-414`) + a doubled host broadcast
  (`src/Rail/GeoWindowCoverage.cs:663-676` vs `src/Rail/EventPopup.cs:411`) — the doubled broadcast
  MUST collapse to one regardless of which strategy wins;
- research: today BLOCK on client (`src/Rail/ClientSimGate.cs:558`) + CAPTURE-by-observation on host
  (`src/Rail/ResearchSync.cs:555-564`) + REPLAY of the private native handler
  (`src/Rail/ResearchSync.cs:389`) — three strategies must become one.

**A.10 — The centre-of-screen "enter deployment preparation" button.**
When a mission entry is globally dismissed because another player acted on it, the remaining peers
get a centre-of-screen button to enter deployment preparation. **This MUST reuse a NATIVE game
widget.** The implementer MUST first locate a suitable existing widget/prefab/style in the decompile
(`E:\DEV\PhoenixPoint\decompiled\AssemblyCSharp`) or in TFTV
(`E:\DEV\PhoenixPoint\refs\TFTV-src`), cite it `file:line`, and instantiate or clone it.
**A hand-rolled overlay is NOT authorised by this spec.** If no native widget can be adapted, that
is an escalation, not a licence to build one — report it and stop.
**OPEN QUESTION (Q3)**: which native widget. Not researched; do not guess.

**A.11 — The never-created case stays as-is.**
A research completion latched before this peer has ever stood on a map surface is swallowed
(`src/Rail/ResearchSync.cs:117-121`, `:320`, `_everOnMapSurface`, L475's invariant). The journal
does NOT change this in this work. **OPEN QUESTION (Q4)**: whether an append-only journal should
make this case disappear entirely (the entry would simply sit unread until the peer arrives). It
plausibly should, but nothing was audited about what L475 would then assert. Do not change it
opportunistically.

### B. Scoped reactivity

**B.1 — Carry the path through.**
`touched` becomes `(entity, path, field)` at `src/Rail/GenericApplier.cs:1272`. `MarkDirty`
accumulates a SET OF TOUCHED PATHS **beside** the existing global bool — the bool is not removed.
Audit estimate: **3 files** — `src/Rail/GenericApplier.cs`, `src/Rail/UiEventMap.cs`,
`src/Rail/OpenUiRepaint.cs`. If a fourth file is needed, say why in the commit body.

**B.2 — Mark dirty ONLY on value inequality.**
Not "a write happened" — the new value must actually differ from the old (Bevy `set_if_neq`, §2.5).
Compare by VALUE or hash, never by reference identity: the game mutates state in place, so
reference memoization is useless here (reselect FAQ, §2.5).

**B.3 — Declarations are STATIC path PREFIXES, opt-in only.**
A surface declares the path prefixes it reads. The declaration is static — never inferred at runtime
(MobX missed-dependency class, §2.5). **NO DECLARATION ⇒ THE SURFACE REPAINTS ON EVERYTHING.**
This preserves universal-by-construction: a forgotten surface degrades to today's behaviour, never
to stale data. Declaration is in the OPT-IN-TO-SCOPE direction ONLY; there is no way to declare
"I read nothing".
Prefix DEPTH is the whole tuning knob (Firebase, §2.5): a prefix that stops at the root repaints on
everything and is not a bug, just a no-op declaration. Paths must address list elements by STABLE ID,
never by index (RFC 6902, §2.5) — an index path churns on insert and silently mis-scopes.
**OPEN QUESTION (Q5)**: whether the current rail path format
(`S#76.SerializationData.HavenData.AssignedResearchId`) is already stable-ID-addressed for every
list it traverses, or whether some segments are index-based. NOT audited. Verify before relying on
prefix matching for any list-bearing path; if index segments exist, either normalise them or leave
those surfaces undeclared (which is safe by B.3).

**B.4 — The global bool STAYS.**
It serves the ~63 kindless `OpenUiRepaint.MarkDirty()` sites (AssignSync 9, PersonnelSync 15,
MissionSync 12, VehicleSync 6, GenericApplier 8 structural, IntentRail 3, DeployPrep 2,
DeploymentWindow 2, EquipSync 2, TradeSync 1) and ALL structural create/destroy. Those sites carry
no path and MUST keep an unconditional repaint-everything arm. Do not "improve" them into the
scoped path in this work.

**B.5 — Mark during the batch, repaint once at batch end.**
Two-phase mark/evaluate (§2.5). The existing coalescing to 1 repaint/frame in `FlushIfDirty`
(`src/Rail/OpenUiRepaint.cs:893`) and the drag/typing defer (`LocalInputInFlight`, ≤300 frames) are
the batch-end evaluate; keep them.

**B.6 — Presentation reads, never writes.**
A repaint MUST NOT mutate replicated state, so a repaint can never generate rail traffic
(RimWorld `InInterface` vs `Ticking`, §2.5).

**B.7 — The engine must know WHICH SURFACE each player is on**, derived universally rather than
case-by-case; peers on the same surface repaint together. **OPEN QUESTION (Q6)**: the universal
derivation. The obvious candidate is the live view-state Type already used as the key of
`UiNativeRepaint.TryRepaint`'s 17-entry Table (`src/Rail/OpenUiRepaint.cs:~985`), but nothing was
audited about whether that Type is available for a peer OTHER than the local one, i.e. whether the
surface a REMOTE player is on is replicated at all today. Resolve this from code before designing
the wire format; do not assume it is already replicated.

**B.8 — Delete the hand-rolled signatures as prefixes land.**
`"agenda"`/`AgendaSignature` (`src/Rail/OpenUiRepaint.cs:~465`), `"infobar"`/`InfoBarKey` (`:514`),
`"crew"`/`CrewSignature` (`:303`), `"roster"`/`RosterSignature` (`:215`), `CrewSlotKey` (`:~375`).
**KEEP** `RepaintNeeded(strip, key)` (`:439`) as the fallback primitive. Do not leave two competing
read-set mechanisms — that is exactly the two-ordering-systems mistake repeated in the UI layer.
Fix in passing: `RefreshInfoBar` consults `RepaintNeeded` (`:514`) BEFORE the `bar == null` /
`_context == null` bails (`:~524`), consuming and losing a key change while the module is not yet
Init'd (same class as `efc4782` / L516).

**B.9 — SEPARATE AND MANDATORY: "patch, don't rebuild".**
Scoping alone does NOT fix the animation reset. Every path with model/animation state must route to
a non-destructive paint:
- `resetAnimation: false` — reach `UIModuleActorCycle.DisplaySoldier(c, resetAnimation: false, …)`
  directly, exactly as `RepaintAugmentScreen` already does (`src/Rail/UiEventMap.cs:939`), instead
  of calling the state's private `DisplaySoldier` (decompile `UIStateEditSoldier.cs:584`) which
  passes `resetAnimation: true` and reaches
  `CommonCharacterUtils.ResetCharacterAnimation` = `Animator.Play(0,-1,0f)`
  (`CommonCharacterUtils.cs:66-73`).
- Same treatment for `UIStateEditVehicle.DisplaySoldier` → `DisplayVehicle(c, resetAnimation: true)`
  (`UIStateEditVehicle.cs:348`).
- `UiEventMap.ReseedIdentityDisplay` clearing `AddonsCharacterBuilder.Addons`
  (`src/Rail/UiEventMap.cs:325`) forces the `RebuildCharacter` branch
  (`UIModuleActorCycle.cs:638-654`) — it must stop being reached by an unrelated change.
- The Exit+Enter fallback (`src/Rail/OpenUiRepaint.cs:518-535`) stays as the last resort for
  undeclared surfaces only, and MUST NOT be reached for any surface with model/animation state.
This is a distinct work item with its own verification (§8, item 9); it is not "done because
scoping landed".

### C. Enforcement

**C.1 — An engine-free deterministic simulation harness over the rail + journal.**
The feasibility condition for deterministic simulation testing is **injectable clock and transport**,
not "can you boot the engine" (TigerBeetle VOPR
https://github.com/tigerbeetle/tigerbeetle/blob/main/docs/internals/vopr.md ; WarpStream
https://www.warpstream.com/blog/deterministic-simulation-testing-for-our-entire-saas ; Antithesis
https://antithesis.com/docs/resources/deterministic_simulation_testing/). The mod's rail and queue
are pure C# and meet that condition — the digest clock is already a BCL `Stopwatch` chosen precisely
so RailCheck can execute the codec in-process (`src/Rail/RailMeta.cs:1449-1453`).

Requirements:
- injectable clock and injectable transport; real production code behind the fakes (no
  reimplementation of the logic under test);
- seeded runs, reproducible from the seed alone;
- assertions on **OBSERVABLE HISTORIES**, not on seam shape (model-based property testing,
  https://medium.com/@tylerneely/reliable-systems-series-model-based-property-testing-e89a433b360).

The properties it must assert:
1. every peer's presentation ORDER is identical;
2. no gap is permanent (a gap self-releases on an armed timer AND is resolved by an explicit
   host-minted void record);
3. a surface whose declared prefixes were untouched **did NOT repaint**;
4. a dismissal marked LOCAL never removed another peer's entry;
5. a GLOBAL dismissal removed it everywhere, including closing it if already open.

Property 3 is the one no law can express today — it is the whole reason this harness exists.

**C.2 — What the harness CANNOT prove.** State this plainly in the harness's own header comment:
it tests **the mod's model, not the game**. It will not catch a wrong Harmony seam, a patch that
does not bind, a native method that behaves differently from the fake, or anything about the Unity
hierarchy (the same limitation `L512`/`L514` already admit). Therefore it MUST be paired with
**recorded-trace replay from a real session**: capture a real 3-instance session's rail + journal
traffic and replay it through the harness, so the model is exercised against real inputs rather than
only generated ones.

**C.3 — Laws for this work must separate the roles.** `L507`'s blind spot was executing host and
client in ONE process. Every new law here MUST execute the property with host and client roles
SEPARATED, so a host-only fault (which is exactly P1) can appear.

---

## 4. Non-goals

- Not rewriting the rail. The architecture in §2.1 is settled.
- Not moving to lockstep, not adding rollback, not adding a desync-recovery reconnect.
- Not making `GeoWindowCoverage`'s declared not-covered families covered: mission-outcome
  (`src/Rail/GeoWindowCoverage.cs:313`), pandoran reveal (`:337`), interception brief/outcome
  (`:342`), alien intelligence brief (`:355`), four unraised types (`:361`). Only the single
  mission-outcome BYPASS in §A.8 is in scope.
- Not per-widget opt-in sync. That was v1 and it was abandoned; tModLoader documents the failure
  mode (§2.5). Universal-by-default stays.
- Not changing `_everOnMapSurface` (§A.11).
- Not building custom UI. Native widgets only (§A.10, §7).
- Not a broad refactor "while we are in there". Barotrauma and Project Zomboid are the warning:
  get the TWO seams right once, touch nothing else.
- Not touching the `~63` kindless `MarkDirty()` sites (§B.4).

---

## 5. Risks

- **R1 — Deleting an ordering system while the other is still wrong.** Removing `HostOrderKey` and
  the `RailOrdinal` back-fill before the journal is authoritative leaves NO order at all. Mitigation:
  work item order in §8 is mandatory — journal lands and is authoritative BEFORE anything is deleted.
- **R2 — The doubled host broadcast.** `GeoModalMirror.HostBroadcastQueued`
  (`src/Rail/GeoWindowCoverage.cs:663-676`) and `EventPopup`'s explicit 0xB6 broadcast
  (`src/Rail/EventPopup.cs:411`) can both append to the journal, producing a duplicate entry where
  today they produce a duplicate broadcast. Collapse to one BEFORE the journal append is added.
- **R3 — Prefix depth too shallow.** A declaration that stops at the root silently reverts a surface
  to repaint-on-everything. It is safe but invisible. Mitigation: the harness property C.1.3 must be
  written per declared surface, so a useless declaration shows up as an untested surface.
- **R4 — Index-based path segments** (Q5) would make prefix matching mis-scope on list insert.
- **R5 — Removing the queue cap** exposes an unbounded accumulation to any runaway raiser. Mitigated
  by the 4096 safety bound (Q1) and the once-per-session error line.
- **R6 — Concurrent agents.** Agents editing this repo in parallel sweep each other's commits and
  collide on law numbers (recorded in project memory). See §7.
- **R7 — A law written to legitimise a duplicate.** `L496` did exactly this. Any new law that
  asserts "both mechanisms agree" instead of "there is one mechanism" is wrong by construction and
  must be rejected in review.

## 6. OPEN QUESTIONS (unresolved — do NOT invent answers)

| # | Question | Where it bites |
|---|---|---|
| **Q1** | Is 4096 the right journal safety bound? It is a documented guess, not a measurement. Measure a long session before treating it as final. | §A.6 |
| **Q2** | For the event family and the research family, which single strategy wins — BLOCK-and-serve or CAPTURE-and-publish? Not decided. Decide from the code, record in `docs/laws.md` and the commit body. | §A.9 |
| **Q3** | Which NATIVE widget provides the centre-of-screen "enter deployment preparation" button? Not researched. Must be found in the decompile or TFTV and cited `file:line` before any UI code is written. | §A.10 |
| **Q4** | Should the append-only journal make the `_everOnMapSurface` never-created case disappear? Plausible, but nothing was audited about what L475 would then assert. Not in scope; do not change opportunistically. | §A.11 |
| **Q5** | Is the rail path format stable-ID-addressed for every list segment, or are some segments index-based? NOT audited. Prefix matching on a list-bearing path is unsafe until this is answered. | §B.3 |
| **Q6** | How is "which surface a player is on" derived universally, and is a REMOTE peer's surface replicated at all today? Not audited. | §B.7 |
| **Q7** | Are Steam persona names / avatars (added 0.9.15-beta) subject to a stricter no-log rule? Not decided. Do not add new persona-name logging until it is. | §2.4 |

---

## 7. Working rules for agents (MANDATORY — read before touching anything)

### 7.1 Ponytail discipline

Load and follow the `ponytail` skill (`Skill` tool, `skill: "ponytail:ponytail"`, level **full**).
If you cannot load it, these are its rules in full and they bind you identically:

> You are a lazy senior developer. Lazy means efficient, not careless. The best code is the code
> never written.
>
> **The ladder — stop at the first rung that holds:** (1) Does this need to exist at all?
> Speculative need = skip it and say so in one line (YAGNI). (2) Is it already in this codebase? A
> helper, util, type or pattern that already lives here → reuse it; re-implementing what is a few
> files over is the most common slop. (3) Does the stdlib do it? Use it. (4) Does a native platform
> feature cover it? (5) Does an already-installed dependency solve it? Never add a new dependency for
> what a few lines can do. (6) Can it be one line? One line. (7) Only then: the minimum code that
> works.
>
> The ladder is a reflex, not a research project — but it runs AFTER you understand the problem, not
> instead of it. Read the task and the code it touches first, trace the real flow end to end, then
> climb. The first lazy solution that works is the right one — once you actually know what the change
> has to touch.
>
> **Bug fix = root cause, not symptom.** A report names a symptom. Before you edit, find every caller
> of the function you are about to touch. One guard in the shared function is a smaller diff than a
> guard in every caller — and patching only the path the ticket names leaves every sibling caller
> broken.
>
> **Rules.** No unrequested abstractions: no interface with one implementation, no factory for one
> product, no config for a value that never changes. No boilerplate, no scaffolding "for later".
> Deletion over addition. Boring over clever. Fewest files possible. Shortest working diff wins — but
> only once you understand the problem; the smallest change in the wrong place is not lazy, it is a
> second bug. Mark deliberate simplifications that cut a real corner with a `ponytail:` comment naming
> the ceiling and the upgrade path.
>
> **Never be lazy about:** understanding the problem, input validation at trust boundaries, error
> handling that prevents data loss, security, accessibility basics, or anything explicitly requested.
>
> **Output:** code first, then at most three short lines — what was skipped and when to add it. An
> explanation the user explicitly asked for (a report, per-phase notes) is not debt; give it in full.

Applied here: this work is expected to produce a **net-negative** diff in `src/Rail/`. §A.7 names
~380 KB of files that should shrink or go. An implementation that only ADDS a journal beside the two
existing ordering systems has failed the task.

### 7.2 Sources of truth — never guess

- Decompiled game: `E:\DEV\PhoenixPoint\decompiled\AssemblyCSharp`.
- Real reference mod source: `E:\DEV\PhoenixPoint\refs\TFTV-src` (github.com/Voland163/TFTV, branch
  `master`). **Prefer real source over the decompile.**
- Authoritative tree map: `E:\DEV\PhoenixPoint\docs\research\source-provenance.md` — consult it
  before citing any `file:line` outside this repo.
- Existing research: `E:\DEV\PhoenixPoint\docs\research\` (index `README.md`) and
  `E:\DEV\PhoenixPoint\docs\superpowers\`. Read these BEFORE digging source; most findings are
  already recorded.

**HARD RULE:** no engine signature, field, enum, constant or behaviour may be guessed. Every one
must be READ from a real source and cited `file:line` in the agent's report. "Intent ≠ runtime
effect" — a comment is not evidence.

**Navigation:** use Serena MCP (`find_symbol`, `search_for_pattern`, `get_symbols_overview`,
`find_referencing_symbols`), not blind grep. For architecture questions, run
`graphify query "<question>"` **from the Multiplayer2 root** — it hits the mod's own code graph
(8670 nodes / 20454 edges as built 2026-08-15) and costs ~9k tokens versus reading many files.
For an unfamiliar external library API, use Context7 MCP before writing code against it.

### 7.3 Logging regimen

Follow §2.4 exactly. Summary of the rules that bind an implementer:
- **`MpLog` only.** A single `UnityEngine.Debug.Log*` or `ModLogger.Log*` call anywhere in the mod
  assembly turns L432 RED (`tools/RailCheck/L432_EveryLogUsesTheOneDoor.cs:59-82`).
- **Tag from the existing taxonomy:** `[MP][windows]` (journal), `[MP][inbox]` (answer-once),
  `[MP][uirepaint]` (scoped reactivity), `[MP][rail]` (applier/diff/codec). Do not invent a tag.
- **Level:** `Log` for a normal event; `LogWarning` for a gap, fallback or recoverable anomaly;
  `LogError` for a broken invariant or dropped state. Warnings and errors are NEVER diag-gated.
- **Hot paths:** any per-frame / per-entry informational trace is wrapped in `if (MpDiag.On)` with
  the string concatenation INSIDE the guard (`src/Net/MpDiag.cs`). 23642 lines in one session is the
  documented cost of getting this wrong.
- **Repeats:** use `if (RailMeta.CountMiss(line)) MpLog.LogWarning(line);`
  (`src/Rail/RailMeta.cs:1456`, `:1494-1498`) for a repeating exact line — you get the 30 s digest
  for free; or the once-per-key dictionary pattern (`src/Rail/DiffEngine.cs:383`, `:430`, `:603-609`)
  when the repeat is per (key, generation). Clear your tally at the reload boundary the way
  `GenericApplier.ResetForReloadBoundary` does (`src/Rail/GenericApplier.cs:95-96`).
- **Never log** secrets, credentials, Steam auth tickets or session tokens; do not dump whole
  payload blobs on a hot path. See Q7 for persona names.

### 7.4 Verification — never claim it without reporting the command AND its output

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
.\deploy.ps1 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'
dotnet run -c Debug --project tools/RailCheck
pwsh -NoProfile -File tools/law-integrity.ps1
```

Current baseline (must be restored before any commit):
```
RAILCHECK GREEN — laws-run=336/336 law-violations=0
laws: 276 file + 60 inline = 336
```
(`tools/law-count.txt` currently reads `files=276`, `inline=60`.)

**NEVER** run RailCheck with `--update` to approve a violation. **NEVER** weaken a law to make a
violation pass. **NEVER** add anything to `tools/vacuity-exempt.txt` — it must stay empty; fix the
unguarded law instead.

Co-op test instances are `D:\PP-Instance2` and `D:\PP-Instance3`. `E:\Dev Games\PP-Instance2` does
not exist on this machine. Never publish or upload unless the user explicitly asks.

### 7.5 Law rules

- Register every law through `Add(laws, () => ...)`. **Never** `laws.AddRange(...)`.
- File law = add `tools/RailCheck/L<n>_<Name>.cs` + its registration + an **executable guard** +
  increment `files=` in `tools/law-count.txt`. Inline law = add the method + registration, increment
  `inline=`. **Prefer file laws.**
- `P<n>` = architecture principle, `L<n>` = executable law. Numbering is SPARSE — pick a number no
  other agent is using (see §7.7).
- Update BOTH identity digests deliberately when the law set changes: a new or renamed law also
  touches `Program.cs` and L193's positive-control constants.
- **Every new or reworded law needs a compile-valid `src/` SEMANTIC mutation kill**: the named law
  goes RED with the mutation applied, and the restored baseline goes GREEN. Record the kill in
  `docs/laws.md`. Synthetic mutations test the harness path only, not production semantics.
  `tools/mutation-runner.ps1` verifies every registration reaches RED; shard with
  `-StartRegistration` and `-Limit`.
- **New laws for this work MUST execute the property with host and client roles SEPARATED** —
  `L507`'s blind spot was executing both roles in one process (§C.3).
- A law that asserts "two mechanisms agree" instead of "there is one mechanism" is wrong by
  construction (`L496` is the precedent). Reject it in review.

### 7.6 Hard product rules

- **NO quorum**, no consensus gate, no ready-vote, no "wait for all peers", no gate that waits on
  another human's ACTION. One player must be able to drive the entire game to completion while every
  other player is AFK. Waiting on a LOAD that ends by itself IS allowed — that is how the
  curtain/reveal barrier legitimately works (`RosterProgressTracker.AllDone(GetRosterSlots())` →
  `RevealAll`): it releases when every LIVE peer finishes loading, and a gone peer shrinks the
  expected set. One player sending everyone into a battle or transition is CORRECT and intended;
  do not "fix" it by adding a wait. Historical anchor: the lobby ready-quorum was deliberately
  REMOVED in `afc111a`; RailCheck laws `L84` (`ready-quorum-back`) and `L114` (`parity-quorum`)
  assert it stays removed.
- **Reactivity is mandatory.** Every replicated-state change must repaint already-open UI.
  Requiring a player to leave a screen and re-enter, or to reload, is a DEFECT, not a cosmetic
  issue. Any change to replicated state must report HOW it repaints an open screen — the repaint
  seam by `file:line`, or an explicit statement that the default `UiEventMap` arm covers it and why.
  "It syncs" is not an acceptable answer. This applies to surfaces added by other mods (e.g. TFTV
  toggles and panels), not just vanilla screens.
- **Native UI is reused, never hand-rolled.** Find the existing PP/TFTV widget/prefab/style and
  instantiate or clone it. Only build custom when no native element can be adapted, and say why.

### 7.7 Commit discipline and parallelism

- Local commits on `main` only. **Never push.** No feature branches.
- Lowercase imperative Conventional Commits with a subsystem scope, e.g.
  `feat(windows): make the host journal the only presentation order`.
- Commit the INSTANT a change is green — never batch, never leave green work uncommitted.
- `TFTV.dll` and `TFTV.meta.json` at the repo root are UNTRACKED and must stay untracked: use
  explicit paths with `git add`, **never `git add -A`**.
- Release only through `tools/release.ps1`, `-WhatIf` first; never `-Publish`, never push tags,
  never rewrite published history without explicit user authorisation.
- Enable hooks once per clone: `git config core.hooksPath .githooks`.
- **PARALLELISM WARNING:** agents editing this repo concurrently sweep each other's commits and
  collide on law numbers. Implementation steps that touch `src/` or `tools/RailCheck` MUST be
  **SERIALISED** — one agent at a time. Read-only work (research, audits, doc reads) may run in
  parallel.

---

## 8. Work breakdown, ordered by dependency

Each item states the files it touches, the law(s) it needs, and its verification. Items 1–11 that
touch `src/` or `tools/RailCheck` are SERIAL (§7.7). Item 0 is read-only and may run in parallel
with nothing else pending.

**0. Resolve the open questions that block later items (READ-ONLY).**
- Files: none. Answers Q2 (§A.9), Q3 (§A.10), Q5 (§B.3), Q6 (§B.7).
- Laws: none.
- Verification: a report citing `file:line` for every answer, from the decompile / TFTV / mod source.
  Q3 must name a concrete native widget or escalate. **Q1, Q4 and Q7 do not block anything and stay
  open.**

**1. Collapse the doubled host broadcast (R2).**
- Files: `src/Rail/GeoWindowCoverage.cs:663-676`, `src/Rail/EventPopup.cs:411`.
- Laws: extend or add a law asserting exactly ONE host publication path per raise. Roles separated.
- Verification: build + `dotnet run -c Debug --project tools/RailCheck` GREEN at 336/336; mutation
  kill for the new/changed law recorded in `docs/laws.md`.

**2. Collapse block-vs-capture to one strategy per family (§A.9, needs Q2).**
- Files: `src/Rail/ClientSimGate.cs:361-370`, `:558`, `src/Rail/EventPopup.cs:336-414`,
  `src/Rail/ResearchSync.cs:389`, `:555-564`.
- Laws: one law per family asserting a single strategy — "for family F there is exactly one of
  block-and-serve or capture-and-publish". Not "the two agree" (R7).
- Verification: RailCheck GREEN; semantic mutation kill; a live 3-instance run showing the window
  still appears on every peer.

**3. Introduce the journal as the single append-only ordered stream (§A.1, A.2, A.4).**
- Files: new `src/Rail/WindowJournal.cs` (or the smallest existing file that can host it — climb the
  ponytail ladder before creating a file); `src/Rail/WindowOrder.cs`; the publication seam moved off
  the `QueryStateSwitch` postfix (`src/Rail/GeoWindowCoverage.cs:663-676`).
- Laws: (a) claiming a journal position is the ONLY way a window can be presented (the LMAX
  single-entrance property); (b) the append path is reachable with `GeoLevel() == null`
  (`src/Rail/GenericApplier.cs:130-136`), i.e. screen-independent.
- Verification: RailCheck GREEN; mutation kill; the harness (item 10) property C.1.1 passing;
  the commit body states where the append happens and that it is view-independent.

**4. Make the client reconcile to the published order; stop the client inventing a key (§A.1).**
- Files: `src/Rail/WindowQueueSync.cs`, `src/Rail/WindowOrder.cs`.
- Laws: "a client never sorts a window queue" — assert no client-side comparison/sort survives.
- Verification: RailCheck GREEN; the 3-instance repro of P1 (host queues research→event) now shows
  the SAME order on all three peers; report the three logs' presentation lines.

**5. Delete the second ordering system (§A.7). ONLY after 3 and 4 are green (R1).**
- Files: `src/Rail/DurableInboxModel.cs` (`HostOrderKey:97`), `src/Rail/EventPopup.cs:993`
  (`BindDurable`), `src/Rail/WindowOrder.cs:128-141`, `:496-499`, `:528`,
  `src/Rail/WindowQueueSync.cs` (`TryDurablePriorityPreemption`),
  `src/Rail/DurableInboxEngine.cs`, `src/Rail/DurableInboxState.cs`,
  `src/Rail/DurableInboxStore.cs`, `src/Rail/DurableInboxCodec.cs`,
  `src/Rail/DurableWindowRegistry.cs`, `src/Rail/RailOrdinal.cs:66-73` (window back-fill only).
- Laws: a law asserting `DurableInbox` provides answer-once semantics and NOT ordering — i.e. no
  ordering comparison reads a durable key. Retire or rewrite `L496` (it legitimised a duplicate) and
  re-examine `L507`.
- Verification: RailCheck GREEN at the adjusted law count (`tools/law-count.txt` updated); mutation
  kill; net-negative diff reported in the commit body.

**6. Remove the tail-trim cap; add the safety bound (§A.6, Q1).**
- Files: `src/Rail/GeoWindowCoverage.cs:586`, `:620-635`.
- Laws: "no code path removes the NEWEST journal entry"; and the safety-bound breach logs once and
  stops appending rather than dropping.
- Verification: RailCheck GREEN; mutation kill; harness run appending > bound and asserting no
  accepted entry was dropped.

**7. Dismissal scope as a declared property; host-minted void records (§A.5, A.6).**
- Files: the family declaration table (new, smallest possible — a static array/dictionary, not a
  class hierarchy), `src/Rail/DeploymentWindow.cs:490-524` (`DropUnservableQueued` becomes
  native-queue hygiene only), `src/Rail/WindowQueueSync.cs:203-214`.
- Laws: (a) an undeclared family is LOCAL; (b) only the host mints a void; (c) no client removes a
  journal entry from a local `Servable()` evaluation.
- Verification: RailCheck GREEN; mutation kill; harness properties C.1.4 and C.1.5.

**8. Close the two bypasses (§A.8).**
- Files: `src/Rail/WindowOrder.cs:71` (`RestoreData`), `:427` (`UIStateInitial.EnterState:112`
  mission-outcome), `:541-545` (ordinal-0 restored requests).
- Laws: extend the item-3 single-entrance law to cover restore and the initial-state raise.
- Verification: RailCheck GREEN; mutation kill; a save/load across a battle showing the restored
  queue in the host's order (the tactical-boundary path,
  `GeoscapeView.GetStateSwitchInstanceData:1298-1300` → `RestoreData:39-56` → `GeoscapeView.cs:349`).

**9. "Patch, don't rebuild" (§B.9). Independent of items 3–8; still SERIAL against them.**
- Files: `src/Rail/UiEventMap.cs:325` (`ReseedIdentityDisplay`), `:939` (`RepaintAugmentScreen`, the
  model to copy), `src/Rail/OpenUiRepaint.cs:518-535` (Exit+Enter fallback).
- Laws: "no repaint path reaches a `resetAnimation: true` overload or `RebuildCharacter` for a
  surface with model/animation state"; and the Exit+Enter fallback is unreachable for those surfaces.
- Verification: RailCheck GREEN; mutation kill; a live 3-instance run where a remote manufacturing
  tick does NOT reset the local soldier model or animation — report the observation.

**10. The deterministic simulation harness (§C).**
- Files: new under `tools/` (e.g. `tools/RailSim/`) — engine-free, injectable clock and transport,
  seeded. Reuse the BCL-clock precedent (`src/Rail/RailMeta.cs:1449-1453`).
- Laws: none by itself; it is a test harness, not a law. It MUST be runnable from the command line
  and its header MUST state the C.2 limitations verbatim.
- Verification: seeded run reproducible from the seed; all five C.1 properties asserted; plus one
  recorded-trace replay from a real session (C.2).

**11. Scoped reactivity (§B.1–B.8). Needs Q5 and Q6; may land after item 10 so its properties are
testable.**
- Files: `src/Rail/GenericApplier.cs:272-278`, `:1272`, `:323`, `:508`; `src/Rail/UiEventMap.cs:75-190`;
  `src/Rail/OpenUiRepaint.cs:728`, `:746`, `:893`, `:959`, `:158`, plus the signature deletions at
  `:215`, `:303`, `:~375`, `:~465`, `:514` and the `RefreshInfoBar` ordering fix at `:514`/`:~524`.
- Laws: (a) mark happens only on value inequality; (b) an undeclared surface repaints on everything
  (the safe-degradation property — assert it, do not assume it); (c) the ~63 kindless sites still
  reach the unconditional arm; (d) no hand-rolled signature survives beside a declared prefix set.
  Extend `L38` so it covers kindless calls outside `UiEventMap.Fire`, or add a law that does.
- Verification: RailCheck GREEN; mutation kill for each new law; harness property C.1.3 (a surface
  whose declared prefixes were untouched did NOT repaint) — this is the property no existing law can
  express and it is the acceptance criterion for the whole of B.

---

## 9. Glossary

- **RailOrdinal** — a monotonically minted sequence number stamped on rail activity
  (`src/Rail/RailOrdinal.cs:66-73` `Mint()`). Used today as a window order key; §A.7 removes it from
  that role. Its `Mint()` back-fills the whole pending provisional list with ONE ordinal, which is
  the mechanism of P1.
- **settle** — a short client-side hold (`SettleSeconds`, 150 ms) before draining the window queue,
  intended to let out-of-order arrivals be re-sorted. Measured inter-channel skew was 363 ms, so the
  hold is too short by construction. Removed as an ordering device by §A.7.
- **drain** — taking the next window off the queue and presenting it. Entry point
  `WindowOrder.ReadyToDequeue` (`src/Rail/WindowOrder.cs:453+`), with `HoldsHead` (`:325`) and
  `HoldsForOpenScreen` (`:354-358`).
- **durable** — an occurrence recorded in the DurableInbox ledger so it is answered exactly once even
  across reloads. `DurableInboxModel.HostOrderKey:97` also made it an ORDER key; §A.7 keeps
  answer-once and deletes the ordering role.
- **surface** — a screen or view the player is looking at, identified today by the live view-state
  Type (the key of `UiNativeRepaint.TryRepaint`'s 17-entry Table,
  `src/Rail/OpenUiRepaint.cs:~985`). §B.7/Q6 covers deriving it universally.
- **kind** — the replicated entity TYPE, as used by `UiEventMap.Fire`'s 8 arms
  (`src/Rail/UiEventMap.cs:75-190`) and by `UiNativeRepaint.IgnoredKinds` (`:661-665`). "Kindless"
  = a `MarkDirty()` call with no type argument (~63 sites, §B.4).
- **root** — the top of a replicated object graph, the unit the CRC backstop compares
  (`src/Rail/DiffEngine.cs:596-611`, `"<peer>|<root>"`).
- **envelope** — the framed rail message carrying a batch of entries between peers; message kinds are
  the byte ids seen in the logs (e.g. `0xB6` event, `0xAC` research).
- **apply** — the client-side act of writing a received rail entry into local state
  (`GenericApplier.ApplyEntry`, `src/Rail/GenericApplier.cs:272-278`), running inside
  `SyncApplyScope`.
- **capture vs block** — two opposite strategies for a window raise. CAPTURE: let the native code run
  and intercept the result to publish it (host, `src/Rail/EventPopup.cs:336-414`). BLOCK: prevent the
  native raise entirely and serve the window from the rail instead (client,
  `src/Rail/ClientSimGate.cs:361-370`). Mixing both on one concern is the recurrence mechanism
  documented in §2.2; §A.9 forbids it.
- **journal** — the new append-only, host-ordered stream of pending windows with a per-peer read
  cursor (§A). It replaces both existing ordering systems.
- **void record** — an explicit, host-minted entry that removes a GLOBALLY-dismissed window from
  every peer's backlog and closes it if open (§A.5). Explicit by design: an implicit per-peer timeout
  diverges (FIX gap-fill, §2.5).
