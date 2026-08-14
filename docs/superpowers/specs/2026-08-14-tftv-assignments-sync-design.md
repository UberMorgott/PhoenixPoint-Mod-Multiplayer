# TFTV BaseRework ASSIGNMENTS — host-authoritative sync

- Date: 2026-08-14
- Status: approved for implementation (user delegated all remaining decisions)
- Upstream: TFTV 1.1.4.5 (`refs/TFTV-src`, HEAD `fc8add3`), installed and verified in `E:\Dev Games\PP-Instance2`

## 1. Problem

- TFTV `BaseRework` force-unlocks the vanilla Recruits tab and rebuilds it as "ASSIGNMENTS" (`TFTVBaseRework/Data.cs:314-332`, title from `TFTV_AircraftRework_Localization.csv:232`).
- Personnel assignment state lives in TFTV statics, not in the vanilla graph, so the rail never sees it:
  - `PersonnelData._assignments` — `Dictionary<int, PersonnelInfo>`, key `GeoCharacter.Id` (`Data.cs:270`), public accessor `Assignments` (`Data.cs:271`).
  - `TrainingFacilityRework.RecruitSessions` — `List<RecruitTrainingSession>` (`TrainingFacilityRework.cs:76`).
  - `TrainingFacilityRework._appliedStatLevels` — `Dictionary<int,int>` (`TrainingFacilityRework.cs:82`).
- It only persists through the game's mod-save hooks (`TFTVGeoscape.cs:90-91`), i.e. inside `ModData`, which the rail REFUSES by design (`docs/rail-baseline.txt:610`, rationale `RailMeta.cs:290-312`).
- Consequence today: assignments diverge silently between peers, and because each assigned worker is worth +4 research/production (`ResearchAndManufacturing.cs:14,114-117`), the campaign economies drift apart.
- The stated exit for exactly this case already exists: "mods register their own `M#<name>` rail root" (`RailMeta.cs:310-311`).

## 2. Decisions taken

- Authority model: **host is the only simulator**. Client automation is muted; state arrives as a mirror; UI repaints reactively. (User choice.)
- Scope: **assignments AND training sessions** in one iteration. (User choice.)
- Transport: **mod-root `M#assign` on the shared value rail 0xAC**. No new surface id — the geoscape band 0xA0-0xBF is exhausted and that exhaustion is law-held (`SyncEngineStub.cs:41`, `DeployPrep.cs:15-16`).
- Client gestures: **new ops in the existing `GeoPersonnelIntent` family**, not a new surface.
- Rejected alternatives:
  - Shipping TFTV's own snapshot DTOs as a blob and calling `LoadAssignmentsSnapshot` / `LoadRecruitSessionsSnapshot` on the client. Cheaper, but the apply is load-shaped (clear-then-overwrite) and unsafe to repeat at walk cadence — the exact reason `ModData` is excluded. Also `LoadAssignmentsSnapshot` silently drops unmatched characters and does `_assignments.Add` without a duplicate check (`Data.cs:836-859`).
  - Replaying gestures on the client. Impossible: `CreateOperativeFromCivilian` calls `CharacterGenerator.GenerateUnit` (`TrainingFacilityRework.cs:661-673`), so the client would mint a different character with a different id, and client-allocated ids must never reach the wire (`PersonnelSync.cs:762-767`).

## 3. What is NOT replicated, and why

- **New `GeoCharacter` objects are not our problem.** They land in `PhoenixFaction.Characters`, which the rail already mirrors as vanilla entities (the same graph `PersonnelSync` addresses via `U#<id>`). Non-deterministic stat generation happens host-side only; the finished operative arrives as an ordinary delta. No descriptor codec is needed.
- **Auto-Assign toggle is not in this surface.** `SetAutoAssignEnabled` writes `GeoscapeEventSystem` variable `TFTV_BaseRework_AutoAssignEnabled` (`Data.cs:586-599`), already covered by `EventVariableCapture` (`src/Rail/EventSync.cs`, `0xB4 op 2`) — the `TFTV_HelmetsOff` precedent recorded in `L149:58-73`.
- **`FacilitySlotPools` are not replicated.** They live in a `ConditionalWeakTable` (`Workers.cs:202`) and are derived, not authoritative. See §6 — they are recomputed on the client instead.
- **`_pendingPostRecruitStatApply`** is currently never written by TFTV (read and cleared only). Mirrored anyway because it is two ints wide and the code path is live.

## 4. State shape — `M#assign`

- New file `src/Rail/AssignSync.cs`, namespace consistent with the other `*Sync` files.
- `[SerializeType(SerializeMembersByDefault = SerializeMembersType.SerializeAll)] public sealed class AssignState`. **The attribute is mandatory** — without it `RailMeta.HasPersistentMembers` is false, the walk emits zero fields and the root never crosses the wire (`DiffEngine.cs:1113-1117`, the 2026-08-13 bug recorded in `DeployPrep.cs:16-24`).
- Fields are **dictionaries keyed by `GeoCharacter.Id`**, not parallel lists. Step 0 (below) established that a `List<T>` member emits as one whole-field wire entry, while a `Dictionary<simpleKey, leaf>` classifies as `LeafDict` and emits **per-key deltas** (`RailTypes.cs:345-346`, `IsSimpleKey` accepts `int` — `RailMeta.cs:884-885`). Moving one worker then costs one sub-key, not a full pool resend. `ManufactureSync.ScrapCartState` (`ManufactureSync.cs:1006-1011`) is the live precedent.
  - `Dictionary<int, byte> AssignRole` — value of `PersonnelAssignment`.
  - `Dictionary<int, string> AssignSpec` — `SpecializationDef.name`, empty string when none.
  - `Dictionary<int, string> SessionSpec`, `Dictionary<int, double> SessionStartHour`, `Dictionary<int, double> SessionDurationHours`, `Dictionary<int, int> SessionTargetLevel`, `Dictionary<int, int> SessionStartLevel`, `Dictionary<int, int> SessionVirtualLevel`, `Dictionary<int, int> SessionSpPaid`, `Dictionary<int, bool> SessionCompleted`, `Dictionary<int, bool> SessionWasDismissed` — the session set is exactly the key set of `SessionSpec`.
  - `Dictionary<int, int> AppliedStatLevel`, `Dictionary<int, int> PendingStatLevel`.
- Keeping every field a dictionary of leaves also keeps the root free of game types, so the codec stays executable headless inside a law.
- Step 0 result (resolved, do not re-litigate): all leaf types used here are supported — `bool`/`int`/`byte`/`double`/`string` (`RailMeta.LeafKindOf`, `RailMeta.cs:889-944`). Limits are 65535 entries per collection (`RailMeta.cs:1165`) and 64 KB per string (`WireString.Prose`); entries over `DiffEngine.MaxValueBytes` (8192) fragment automatically. Both ceilings are orders of magnitude above a personnel pool.
- The applier must tolerate a key present in one dictionary and absent from another (treat as "not a session" / default), rather than indexing blind: each dictionary is an independent wire entry.
- `StartLevel` and `PersonnelId` are carried even though TFTV's own save DTO omits them (`TrainingFacilityRework.cs:1045-1058`). Omitting them is an upstream defect that makes level progress read differently after a reload (`:314-316`); our mirror must not inherit it.

## 5. Host side

- Rebuild the mirror from TFTV statics once per host tick, inside the existing `SyncEngine.Tick()` ordering, before `DiffEngine.HostTick(_engine)` so a change is announced in the same frame it is observed.
- Rebuild is a pure projection: read `PersonnelData.Assignments`, `TrainingFacilityRework.GetActiveRecruitSessions()` plus the two stat dictionaries, sort by id, write into `AssignState`. Sorting is required — TFTV iterates `Dictionary.Values` unsorted when it writes its own snapshot (`Data.cs:791`) and when it evicts (`Data.cs:1267`).
- Reflection surface (TFTV types are never referenced by assembly):
  - `TFTV.TFTVBaseRework.PersonnelData` — `Assignments` is `public static` (`Data.cs:271`), so no private access needed for the read.
  - `TFTV.TFTVBaseRework.PersonnelInfo` is `internal` (`Data.cs:30`) — read its public fields `Id`, `Assignment`, `TrainingSpec` reflectively.
  - `TFTV.TFTVBaseRework.TrainingFacilityRework` — `GetActiveRecruitSessions()` is public (`:187`); `_appliedStatLevels` / `_pendingPostRecruitStatApply` are private statics (`:79`, `:82`).
- Ghost sessions are mirrored verbatim. A session whose trainee died is never removed by TFTV and keeps occupying a slot (`AdvanceAllTraining:303` only drops null refs, which a dead `GeoCharacter` is not). The client must accept the host's set as-is and never reconstruct it.

## 6. Client side — apply

- Apply order, all inside `using (SyncApplyScope.Enter())`:
  1. Reconcile `PersonnelData.Assignments` against the mirror **in place**: update existing entries, add missing, remove surplus. Never `Clear()` — clear-then-refill is the load-shaped pattern this design exists to avoid, and it would drop the `Character` references the panel dereferences.
  2. Resolve each id to a `GeoCharacter` via the rail's own resolver (`IdentityResolver.Resolve(geo, "U#" + id, null)`), matching how `PersonnelSync.cs:910-930` does it. An unresolved id is skipped and logged, not fatal — the character delta may simply not have landed yet, and the next mirror delta will retry.
  3. Rebuild `TrainingFacilityRework.RecruitSessions` from the mirror, reattaching `Character` references the same way. `SpecializationDef` by name through TFTV's `DefCache`, mirroring `Data.cs:856`.
  4. Restore both stat dictionaries.
  5. **Call `PersonnelData.ResyncWorkSlots(phoenix)`.** This is not optional and is the single most easily missed step: economy output is computed from `FacilitySlotPools.UsedSlots`, and `UsedSlots` is only ever derived from `_assignments` here (`Data.cs:895-906` → `Workers.SetUsedSlots:331`). Writing the dictionary alone leaves research/production stale.
  6. Call `Workers.RefreshInfoBar()` so the `used/provided` readout and `GeoFaction.UpdateProduction` follow (`Workers.cs:350-355`).
  7. `OpenUiRepaint.MarkDirty()`.
- Do **not** call `RestoreAssignments` (`Data.cs:874-893`) on the client: it is not a restore, it runs `EnforceLivingCapacity` and `TryAutoAssignUnassignedPersonnel` and would move people locally. Only its harmless conjunct `ResyncWorkSlots` is wanted, and step 5 calls that directly.

## 7. Client side — muting

- All mutes gate on the same single door, `IntentRail.ShouldRunNative()` (`IntentRail.cs:94-100`). No local copy of the host/client decision.
- Mute set, each a TFTV-gated Harmony patch and therefore each MUST be listed in `TftvLateBinder._patchClasses` (`TftvLateBinder.cs:32-39`) or `L373` arm (a) goes red and the guard would die silently:

| Target | `file:line` in TFTV | Why |
|---|---|---|
| `PersonnelData.TryAutoAssignUnassignedPersonnel` | `Data.cs:610` | Single funnel for auto-assign; muting here covers six callers (`Data.cs:517,701,886,1197`, `Workers.cs:37`, `Panel.cs:159`) |
| `PersonnelData.EnforceLivingCapacity` | `Data.cs:1244` | Mass eviction on housing pressure |
| `PersonnelData.AttachCharacter` | `Data.cs:505` | Inserts a row on every character attach |
| `Data.GeoPhoenixFaction_RegenerateNakedRecruits_PersonnelSync.Postfix` | `Data.cs:966` | Calls `GenerateRandomUnit` (`:996`) and mints client-local ids |
| `TrainingFacilityRework.DailyUpdateTraining` | `TrainingFacilityRework.cs:909` | Advances training clocks |
| `TrainingFacilityRework.DailyUpdateTrainingDeferredFallback` | `TrainingFacilityRework.cs:958` | Same, deferred stats |
| `TrainingFacilityRework.ForceRecruitProgressUpdate` | `TrainingFacilityRework.cs:267` | `IsRecruitTrainingComplete` is NOT a getter — it mutates the session (`:255`), and three UI paths call it (`Panel.cs:739`, `Helpers.cs:277`, `Harmony.cs:362`) |
| `PersonnelManagementUI.TryOpenNextCompletedDeployment` | `UI/Harmony.cs:319` | Timer-driven; calls `FinalizeRecruitTrainingForUI` straight from a UI hook (`:281`) and would mint a local operative with different stats. Also frame-count dependent (`:251-263`) |

- `ResyncWorkSlots` is explicitly NOT muted — it is a pure recompute and the client needs it.
- `ClearAssignments` is NOT muted — both peers must clear on the same boundaries (`TFTVTactical.cs:282`, `TFTVGeoscape.cs:495`).

## 8. Client side — gestures

- New ops on `SurfaceIds.GeoPersonnelIntent`, alongside the existing `OpTftvRedeploy` / `OpTftvTrainDeploy` / `OpTftvPromote` (`PersonnelSync.cs:83-95`):
  - `OpAssignMove` — payload `[charId:int][role:byte]`. Captures `PersonnelData.AssignWorker` (`Data.cs:714`) and `UnassignFromWork` (`Data.cs:1201`), i.e. every plus / minus / multi-select / drag-drop, all of which funnel through `Panel.MovePersonnelToColumn` (`Panel.cs:579-644`).
  - `OpAssignTrain` — payload `[charId:int][specName:WireString.ReadKey][targetLevel:int]`. Captures `TrainingFacilityRework.QueueCharacterTrainingAutoFacility` (`:1179`).
  - `OpAssignFinalize` — payload `[charId:int][early:bool]`. Captures `FinalizeRecruitTraining` (`:355`).
- No cancel op: TFTV has no cancel. The only exit from Training is `FinalizeRecruitTraining(early: true)` with a partial SP refund (`Helpers.cs:298-320`), and moving back to Unassigned is explicitly refused upstream (`Panel.cs`, `MovePersonnelToColumn`).
- Capture shape is the established one (`PersonnelSync.cs:707-730`): `if (IntentRail.ShouldRunNative()) return true;` then `IntentRail.Send(...)` then `return false`.
- Host handler validates **only against host state** — character resolves, belongs to `geo.PhoenixFaction`, role is a defined enum value, SP/slot availability re-derived host-side. **No balance value crosses the wire** (`PersonnelSync.cs:69-77`). Rejection goes through `IntentRail.Reject(surface, peer, why, prefixes…)`, never a bare log.
- Strings on the wire use `WireString.ReadKey` only (L414/L420). `BinaryReader.ReadString()` is forbidden.

## 9. Lifecycle wiring — `src/Rail/SyncEngineStub.cs`

- `AssignSync.Register()` in the `SyncEngine` constructor, next to `MistSync` / `DeployPrep` (`:52-59`).
- `AssignSync.RegisterIntents()` with the other intent families (`:22-49`).
- `AssignSync.ResetForReloadBoundary()` in `ResetForReloadBoundary()` (`:251-265`), placed **before** `DiffEngine.ResetForReloadBoundary()` exactly as `MistSync` is (`:260-262`). Mandatory: a mod root that is non-empty at a reload boundary gets captured by the post-reload baseline walk, is therefore never emitted, and the client stays permanently stale until a full resend (`IdentityResolver.cs:205-206`).
- `AssignSync.Reset()` in `DetachAllChannels()` (`:218-249`).
- Host projection call in `SyncEngine.Tick()` (`:104-216`) with a comment justifying its position in the ordering — that ordering is load-bearing and commented line by line.
- Nothing is written to any save. The mod does not touch saves (`HANDOFF.md` §4); joiners are re-seeded by the existing `BroadcastAllChannels()` full resend (`:267-276`).

## 10. Reactive repaint

- `UIStateRosterRecruits` is currently absent from `UiNativeRepaint.Table` (`UiEventMap.cs:429-595`), so the ASSIGNMENTS screen falls back to Exit+Enter — a full screen teardown that happens to rebuild the panel through TFTV's `EnterState` postfix.
- Add a `UiNativeRepaint.Table` entry for `UIStateRosterRecruits` that invokes TFTV's own `PersonnelManagementUI.RefreshPanel()` (`UI/Panel.cs:27-34`, private — reach it with `AccessTools.Method`) and returns `true`, suppressing the fallback. That is the same shape as the manufacturing entry (`UiEventMap.cs:434`).
- Resolve-all-first: if the method does not resolve, return `false` and let the fallback run rather than half-repainting (the `UIStateEditSoldier` discipline, `UiEventMap.cs:497-506`).
- Per `L149` arm (c), the repaint entry must not name a TFTV type from a method reachable by `Program.Callees` of a rail-side reseed helper. Keep the TFTV lookup inside the table lambda / a late-bound helper, so load order stays irrelevant.

## 11. Laws

Two new file laws. Numbers are assigned, not `max+1`; the implementer must confirm the chosen numbers are unused and never reuse a retired one (`docs/laws.md:376-382`).

- **`L441_AssignmentsAreHostAuthoritative`** — authority + lifecycle.
  - Positive control, then negative arms, then a `premise-changed` guard with `yield break`, then `Program.Callees` wiring checks — the `L433` formula.
  - Decisions extracted as pure functions with no game types so the law can execute them headless (the `LoadBarrierAuthority.cs` pattern, 22 lines):
    - `AssignAuthority.AcceptAssignMove(bool isHost, bool sessionActive, bool characterResolved, bool isPhoenix, bool roleDefined)`
    - `AssignAuthority.ClientMutesAutomation(bool sessionActive, bool isHost, bool inApplyScope)`
  - Arms: a non-host peer cannot mutate; a client inside apply scope must run native; an unresolved or non-Phoenix character is refused; an undefined role byte is refused.
  - Wiring: every mute patch prefix in §7 reaches `IntentRail.ShouldRunNative`; the host handler reaches `AssignAuthority.AcceptAssignMove`; the reject path reaches `IntentRail.Reject`.
- **`L442_AssignmentMirrorRewiresTheSlotPools`** — codec + convergence.
  - Codec round-trip on `AssignState` encode/decode, executed headless.
  - The arm that matters: the client apply method must reach `ResyncWorkSlots`. Without it the dictionary is right and the economy is wrong, and nothing else in the suite would notice.
  - Guard arm asserting the apply method never reaches `ClearAssignments` or `RestoreAssignments`.
- Guard text must contain the literal `premise-changed` in a `yield return` — that is the string `tools/law-integrity.ps1:96-106` reliably matches. `tools/vacuity-exempt.txt` stays empty.

## 12. Bookkeeping that must not be forgotten

- `tools/law-count.txt`: `files=239` → `241`.
- `tools/RailCheck/Program.cs:452`: `ExpectedLawRegistrations = 299` → `301`.
- Two `Add(laws, () => L44x_….Check());` lines in `Program.cs`. Never `laws.AddRange(...)`.
- `ExpectedExecutionIdentityDigest` (`Program.cs:453`) — take the new value from the RailCheck abort message.
- `$expectedRegistrationDigest` (`tools/law-integrity.ps1:42`) — take the new value from the FAIL message.
- `docs/rail-baseline.txt` gains the `M#assign` root; review the diff before `--update`, and commit the snapshot with the change.
- New patch classes appended to `TftvLateBinder._patchClasses`.
- Stale upstream citations to correct while in these files (they now misquote 1.1.4.5): `TacticalCommandSync.cs:769` → `TFTVVanillaFixes/Tactical/MadSkunkyFixes.cs:111-142`; `PersonnelSync.cs:605` → `TFTVVanillaFixes/Geoscape/GeoscapeVanillaFixes.cs:293-294`; `L149:32` → `TFTVUI/Personnel/Loadouts.cs:156-238`; `L372:223` — the `99` cap lives at `AircraftReworkMissionDeployment.cs:163`, not `TFTVConfig.cs:64`.

## 13. Verification

- `$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH` before any build.
- `.\deploy.ps1 -GameDir 'E:\Dev Games\PP-Instance2'`
- `dotnet run -c Debug --project tools/RailCheck` — expect `RAILCHECK GREEN … laws-run=301/301 law-violations=0`.
- `pwsh -NoProfile -File tools/law-integrity.ps1` — expect `laws: 241 file(s) + 60 inline = 301` and `law-integrity: OK`.
- `tools/mutation-runner.ps1` over the new registrations.
- **Semantic kill, mandatory** — this surface is authority + codec + lifecycle, so a compile-valid `src/` mutation is required per `CLAUDE.md` and `docs/laws.md:347`:
  - Delete the `ResyncWorkSlots` call from the client apply → `L442` RED, everything else green; restore → GREEN.
  - Invert the `ShouldRunNative` guard in one mute prefix → `L441` RED; restore → GREEN.
  - Record both in `docs/laws.md`.
- No verification claim without the command and its output.

## 14. Risks

- `RailMeta` leaf classification of the parallel lists is unconfirmed — §4 step 0 blocks on it, with the `MistSync` packed-string fallback ready.
- `PersonnelInfo` is `internal`, so the projection and the apply both depend on reflection over TFTV internals; an upstream rename breaks them. Mitigated by the `premise-changed` arm in `L441`.
- The client apply resolves ids against the vanilla character graph, which may lag the mirror by a delta. Handled by skip-and-retry, not by blocking.
- TFTV 1.1.4.5 removed `CancelRecruitTraining` and `GetRecruitRemainingDays` and changed `GetRecruitRemainingHours` to `double`. We bind none of them; noted so a future binding does not reintroduce the breakage.
