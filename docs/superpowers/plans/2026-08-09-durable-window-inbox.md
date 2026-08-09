# Durable Window Inbox Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: use `superpowers:executing-plans`. Execute Tasks 1-18 strictly in order; never parallelize edits to shared files.

**Goal:** Replace ephemeral Geoscape window delivery with a host-authoritative, campaign-save-persisted, per-player durable inbox for actively enrolled players that survives tactical play and native campaign save/load while retaining native rendering, immediate open-UI repaint, and progress when every other player is AFK. Disconnect ends the current epoch; reconnect enrolls a new epoch without prior windows.

**Architecture:** A pure reducer owns occurrence identity, membership-epoch entitlements, lifecycle revisions, shared effect/launch transactions, total order, and tombstones. A campaign save root appended at Phoenix Point's native save-provider seam carries an immutable snapshot plus journal. Existing window classes become capture/presentation carriers indexed by occurrence. DWI reuses surface `0xB9` with reserved inner op/message values and bounded snapshot pages; `SyncProtocol` remains the existing three-argument envelope encoder and continues minting its cross-surface ordinal internally.

**Tech stack:** C# 7.3, .NET Framework 4.7.2, Harmony, Phoenix Point native Geoscape UI and serializer, existing `0x67` `SyncProtocol`/`SurfaceRouter`/`IntentRail`, and `tools/RailCheck`.

---

## Grounded contracts that must not be guessed

- `SyncProtocol.EncodeEnvelope(byte surfaceId, SyncKind kind, byte[] payload)` is the only encoder (`src/Rail/SyncProtocol.cs:33-50`). It rejects payloads larger than `ushort.MaxValue`, writes `[surfaceId:u8][kind:u8][RailOrdinal.Mint():u32][len:u16][payload]`, and has no caller-supplied ordinal overload. Preserve this signature and header exactly.
- Geoscape `0xA0-0xBF` is treated as fully allocated (`src/Rail/SyncEngineStub.cs:41-60`). DWI therefore uses **existing surface `SurfaceIds.GeoWindowIntent = 0xB9`**, not a new surface or protocol-band expansion. Existing `WindowQueueSync` op `1=advance` and `2=deploy` remain unchanged. Reserve DWI inner op/message values `0x40=LifecycleCommand`, `0x41=SharedAnswer`, `0x42=ReconcileRequest`, `0x43=SnapshotPage`, `0x44=Delta`, `0x45=Refusal`, and `0x46=TransportAck`. `SyncKind.ActionRequest` carries `0x40-0x42`; `Snapshot` carries `0x43`; `StateDelta` carries `0x44-0x45`; ACK is transport bookkeeping only.
- A snapshot page body is bounded to 48 KiB before envelope encoding and carries `[op=0x43][snapshotId:Guid][membership][snapshotEpoch:u64][pageOrdinal:u16][pageCount:u16][contentLength:u16][content][pageHash:u32]`. A peer applies nothing until all pages for one `(snapshotId,membership,snapshotEpoch)` arrive and hash/ordinal/count validation succeeds; reordered/duplicate pages dedupe, gaps request the missing snapshot, and a superseding epoch discards an incomplete assembly.
- Native campaign save flow is `PhoenixSaveManager.SaveGame` -> `Level.WriteSavegame` -> `GeoLevelSavegame.GetObjectsToWrite()` -> native `Contents` section; load calls `GeoLevelSavegame.SetReadObjects(IEnumerable<object>)` (`decompiled/.../PhoenixSaveManager.cs:190-215`, `Base.Levels/Level.cs:168-182`, `PhoenixPoint.Geoscape.Entities/GeoLevelSavegame.cs:21-37`). Patch those exact two provider methods:
  - `GetObjectsToWrite` postfix appends exactly one `[SerializeType] DurableInboxSaveRoot` to the returned `Contents` object sequence. The root carries key/magic `Multiplayer.DurableInbox/v1`, schema, immutable snapshot bytes, journal records, snapshot revision, and CRC. Native save replacement is the atomic file boundary and automatically enters the exact blob read/transferred by `SaveTransferCoordinator.HostSerializeAndSendCrt` (`src/SaveTransfer/SaveTransferCoordinator.cs:621-641`). No filesystem sidecar, static-only state, or `PlayerPrefs` is allowed.
  - `SetReadObjects` prefix extracts/validates that root before native level reconstruction; the original method still sees its own `GeoLevelSavegame.Data`. After level objects exist, resolve stable subjects, replay the journal over the snapshot, quarantine unresolved entries, and install the ledger before started-Geoscape reconciliation. A missing section means a pre-DWI save and starts an empty ledger; corrupt/unknown schema fails closed and never marks delivery.
- Current mission completion capture is `GeoFaction.OnMissionRewardApplied(GeoFactionRewardApplyResult, GeoMission)`, reached from the `GeoSite.OnMissionCompleted` delegate after reward apply (`src/Rail/MissionOutcomeMirror.cs:94-102,613-617`). Completion cleanup must enter DWI at a named prefix/postfix pair around that seam: prefix snapshots immutable predecessor links before outcome creation; postfix terminalizes only those links, then captures a new outcome occurrence.
- Current collaborative equipment authority already funnels through `EquipSync.SetItemsCapturePatch` -> `SurfaceIds.GeoEquipIntent`/`OpSetItems=8` -> host `HandleIntent`; augment/scrap use ops 10/12 (`src/Rail/EquipSync.cs:74-90,128-159`). Soldier/container reassignment uses `PersonnelSync.OpReassign=4` (`src/Rail/PersonnelSync.cs:75-86,128-143`). DWI observes successful host applies and assigns a monotonic preparation revision; it must not invent a duplicate command rail. Validation remains in those existing host handlers, with stale occurrence/preparation revisions rejected before apply.
- `MissionArrivalNav.Step` currently calls `GeoscapeView.LaunchMission` directly (`src/Rail/MissionSync.cs:1027-1035`). DWI must remove/gate that forced navigation: arrival records/revalidates a priority occurrence only; only `DurableInboxEngine.MayPresent` on the Geoscape display surface may create the native carrier.
- Actual `SyncEngine` Geoscape router families are `EventPopup`, `GeoModalMirror`, `CutsceneMirror` (`0xBA`), `MissionOutcomeMirror` (`0xBB`), and `MarketplaceSync` (`0xBF`) (`src/Rail/SyncEngineStub.cs:76-94`). The registry must classify every routed presentation family as durable or explicit local-only; future router additions fail the coverage law.

## Execution and unrelated-change safety

- Work directly in `E:\DEV\PhoenixPoint\Multiplayer2` on `main`; no branch or worktree.
- Before Task 1 and before every commit, run:

  ```powershell
  $repo='E:\DEV\PhoenixPoint\Multiplayer2'
  git -C $repo status --short --branch
  if ($LASTEXITCODE -ne 0) { throw 'git status failed' }
  git -C $repo diff --cached --quiet
  if ($LASTEXITCODE -ne 0) { throw 'index is not empty; stop' }
  Get-ChildItem 'E:\DEV\PhoenixPoint\.claude\green-pending','E:\DEV\PhoenixPoint\Multiplayer2\.claude\green-pending' -File -ErrorAction SilentlyContinue
  ```

- At planning time these unrelated unstaged files exist and must remain byte-for-byte unstaged unless a task explicitly owns a named hunk: `src/Lobby/PingMarkers.cs`, `src/Tactical/TacticalCommandSync.cs`, `tools/RailCheck/L344_APingRevealsNothingUnfound.cs`, `tools/RailCheck/L358_ABodyPartRidesWithTheOrderThatAimedAtIt.cs`, and `tools/RailCheck/Program.cs`. The former sentinel is archived as `E:\DEV\PhoenixPoint\Multiplayer2\.claude\green-pending\multiplayer2-aim.txt.stale-20260809`; no active `multiplayer2-aim.txt` remains. Do not move, delete, or trigger the archived sentinel.
- Save SHA-256 plus `git diff --binary -- <path>` for every unrelated dirty file before Task 1; compare hashes/diffs after each slice. Never use `git add -A`, `git add -p`, `git commit -a`, checkout, reset, stash, or clean.
- `tools/RailCheck/Program.cs` is shared and already dirty. For each slice, create a reviewed zero-context patch against `git show HEAD:tools/RailCheck/Program.cs` containing only that slice's exact `Add(laws, () => L<n>_...Check())` lines; apply it with `git apply --cached --check --unidiff-zero <patch>` then `git apply --cached --unidiff-zero <patch>`. Verify `git diff --cached -- tools/RailCheck/Program.cs` contains only those registrations. Delete the temporary patch before commit. This is deterministic and noninteractive.
- Every new file-backed law is registered once, increments `tools/law-count.txt`, and contains `POSITIVE CONTROL` or `premise-changed`. L376-L401 map one-to-one to DWI-01..26. A slice is GREEN only when its production symbols exist and its assigned law executes the claimed boundary.
- Every commit uses this fail-fast shape, with the task's explicit paths/message substituted. It refuses a nonempty index, checks every native exit code, stages only named files plus the reviewed Program patch, verifies the cached name list and diff, writes the sentinel only after checks/staging are correct, commits, verifies `HEAD` changed, and removes the sentinel only after success:

  ```powershell
  $ErrorActionPreference='Stop'; $repo='E:\DEV\PhoenixPoint\Multiplayer2'; $sentinel='E:\DEV\PhoenixPoint\.claude\green-pending\multiplayer2.txt'
  git -C $repo diff --cached --quiet; if ($LASTEXITCODE -ne 0) { throw 'index not empty' }
  git -C $repo add -- <EXPLICIT_PATHS>; if ($LASTEXITCODE -ne 0) { throw 'stage failed' }
  git -C $repo apply --cached --check --unidiff-zero <PROGRAM_PATCH>; if ($LASTEXITCODE -ne 0) { throw 'Program patch check failed' }
  git -C $repo apply --cached --unidiff-zero <PROGRAM_PATCH>; if ($LASTEXITCODE -ne 0) { throw 'Program patch apply failed' }
  git -C $repo diff --cached --name-only; if ($LASTEXITCODE -ne 0) { throw 'cached-name check failed' }
  git -C $repo diff --cached --check; if ($LASTEXITCODE -ne 0) { throw 'cached diff check failed' }
  $before=(git -C $repo rev-parse HEAD); if ($LASTEXITCODE -ne 0) { throw 'HEAD read failed' }
  New-Item -ItemType Directory -Force (Split-Path $sentinel) | Out-Null
  [IO.File]::WriteAllText($sentinel,"E:/DEV/PhoenixPoint/Multiplayer2`n<COMMIT_MESSAGE>`n")
  git -C $repo commit -m '<COMMIT_MESSAGE>'; if ($LASTEXITCODE -ne 0) { throw 'commit failed; sentinel retained' }
  $after=(git -C $repo rev-parse HEAD); if ($LASTEXITCODE -ne 0 -or $after -eq $before) { throw 'commit not verified; sentinel retained' }
  Remove-Item $sentinel -Force
  ```

- Run exact verification commands with `&&` so failure stops the chain:

  ```powershell
  pwsh -File "E:\DEV\PhoenixPoint\Multiplayer2\tools\law-integrity.ps1" && dotnet build "E:\DEV\PhoenixPoint\Multiplayer2\Multiplayer.csproj" -c Release && dotnet run -c Debug --project "E:\DEV\PhoenixPoint\Multiplayer2\tools\RailCheck"
  ```

---

## Requirement-to-task matrix

| Requirement | Law | Final GREEN task | Production boundary exercised |
|---|---:|---:|---|
| DWI-01 creation-set entitlement | L376 | 2 | serialized membership + `CreateOccurrence` |
| DWI-02 ACK is not lifecycle | L377 | 1 | codec + reducer ACK transition |
| DWI-03 display gate | L378 | 5 | `MayPresent` + native arrival gate |
| DWI-04 durable host order | L379 | 4 | native save blob + retransmit reconstruction |
| DWI-05 preempt/resume | L380 | 6 | scheduler + checkpointed carrier swap |
| DWI-06 exactly-once shared choice/effect | L381 | 8 | durable effect transaction/recovery |
| DWI-07 per-player locked-result retention | L382 | 8 | lifecycle + open repaint |
| DWI-08 canonical result/reward after load | L383 | 4 | saved ledger reconstruction with reordered definitions |
| DWI-09 local offer Cancel | L384 | 9 | native Cancel capture + reducer |
| DWI-10 atomic Start successor | L385 | 9 | successor transaction + carrier registry |
| DWI-11 collaborative edits/repaint | L386 | 10 | existing Equip/Personnel funnels + revision/repaint |
| DWI-12 first-valid launch/no quorum | L387 | 14 | recoverable launch transaction + native result boundary |
| DWI-13 source departure | L388 | 12 | source callback/revalidation + repaint/teardown |
| DWI-14 linked completion cleanup | L389 | 13 | `OnMissionRewardApplied` adapter + generation links |
| DWI-15 sole carrier survives failure | L390 | 9 | store fault between successor/effect and teardown |
| DWI-16 active-epoch revisions/save-load | L391 | 2 | reducer monotonicity/tombstone precedence |
| DWI-17 tactical checkpoint/restore | L392 | 15 | `LevelTeardown` + loaded ledger + started Geoscape |
| DWI-18 exhaustive taxonomy | L393 | 5 | router-derived family table |
| DWI-19 compaction proof | L394 | 3 | save/journal/cursor/replay references |
| DWI-20 all carrier classes | L395 | 7 | occurrence-indexed `RemoveAllCarriers` |
| DWI-21 Deferred Back | L396 | 11 | deployment Back capture + revision gate |
| DWI-22 durable membership/no ACK | L397 | 2 | enrollment/removal reducer |
| DWI-23 no late-join history | L398 | 2 | enrollment/create transaction order |
| DWI-24 reconnect creates clean epoch | L399 | 16 | paged enrollment snapshot reconciliation |
| DWI-25 enrollment/create serialization | L400 | 2 | one host transaction sequencer |
| DWI-26 exact priority registry | L401 | 5 | closed raiser token + patched modal comparison |

---

### Task 1: Immutable identity, codec, and minimal lifecycle reducer

**Files:** create `src/Rail/DurableInboxModel.cs`, `src/Rail/DurableInboxCodec.cs`, `src/Rail/DurableInboxReducer.cs`; create L377; modify `Program.cs`, `law-count.txt`.

- [ ] Add RED L377 that decodes a real `TransportAck` and executes `DurableInboxReducer.Apply`; it must prove queued/open/read/dismissed/choice states are byte-for-byte unchanged while an explicit lifecycle command changes only its named member/occurrence. Positive controls detect ACK-as-read and event-id-only identity.
- [ ] Define normalized immutable value types with constructors, copied/sorted/deduplicated subject storage, `IEquatable<T>`, ordinal equality, stable hash/order, and namespace validation:

  ```csharp
  internal readonly struct MembershipId : IEquatable<MembershipId> { internal readonly string PlayerGuid; internal readonly ulong Epoch; }
  internal readonly struct OccurrenceId : IEquatable<OccurrenceId> { internal readonly string EventId; internal readonly string TriggerId; internal IReadOnlyList<string> SubjectIds { get; } }
  internal readonly struct HostOrderKey : IEquatable<HostOrderKey> { internal readonly ulong CampaignOrdinal; internal readonly string TriggerId; }
  internal readonly struct CanonicalChoiceId : IEquatable<CanonicalChoiceId> { internal readonly OccurrenceId Occurrence; internal readonly string Value; }
  internal readonly struct CanonicalResultId : IEquatable<CanonicalResultId> { internal readonly OccurrenceId Occurrence; internal readonly string Value; }
  internal readonly struct CanonicalRewardItemId : IEquatable<CanonicalRewardItemId> { internal readonly OccurrenceId Occurrence; internal readonly string SubjectId; internal readonly string Value; }
  internal static class DurableInboxCodec { internal static byte[] Encode(InboxMessage message); internal static bool TryDecode(byte[] payload, out InboxMessage message, out string refusal); }
  internal static class DurableInboxReducer { internal static ReduceResult Apply(HostLedger ledger, InboxCommand command); }
  ```

- [ ] Codec includes schema/kind, full occurrence namespace, result ID, reward IDs, membership, order, lifecycle/tombstone revisions, bounded strings/collections, and rejects duplicates, empty subjects, trailing bytes, unknown kinds/versions, and foreign canonical namespaces. Equality must hold across separately decoded subject arrays.
- [ ] Add structural coverage pinning the real three-argument `SyncProtocol.EncodeEnvelope` signature, internal `RailOrdinal.Mint()`, header order, u16 limit, and no caller-supplied ordinal overload.
- [ ] Run the exact verification chain. Commit `feat(inbox): add canonical identity codec and ack reducer`.

### Task 2: Membership, creation-set entitlement, and revision authority

**Files:** modify model/reducer; create L376, L391, L397-L400; modify Program/count.

- [ ] RED laws execute one sequencer for enrollment, creation, disconnect-driven epoch end, reconnect enrollment, and authoritative removal. L376 must include enrolled active, loading, tactical, and non-Geoscape epochs but exclude disconnected ended epochs; retransmit dedupes; a new trigger is distinct; later epochs receive none.
- [ ] Implement `Enroll`, `CreateOccurrence`, `ApplyLifecycle`, and `EndMembership`. `CreateOccurrence` snapshots only committed active epochs. Disconnect calls host-serialized `EndMembership`; reconnect mints and enrolls a new epoch with no prior entitlements. AFK/pause/load/tactical/non-Geoscape presence alone is not removal. Revisions are monotonic, terminal states never regress, and tombstone beats peer update.
- [ ] Positive controls reject Steam/slot identity, backlog scan on join, disconnect retained as passive presence, stale epoch/revision, and any ACK/quorum input.
- [ ] Verify. Commit `feat(inbox): serialize membership entitlement and revisions`.

### Task 3: Campaign ledger store and compaction proof

**Files:** create `src/Rail/DurableInboxStore.cs`; create L394; modify reducer, Program/count.

- [ ] RED L394 keeps a tombstone while any save snapshot, journal record, peer cursor, incomplete snapshot, or wire replay can name it; no TTL. Positive control proves a time-based deletion fails.
- [ ] Implement clone-apply-validate-swap `Commit(HostLedger expected, HostLedger next)` and append-only in-memory journal records with CRC/revision. A failed validation/write injection leaves the prior ledger and journal intact. `CanCompact` requires explicit proof over all reference classes.
- [ ] Verify. Commit `feat(inbox): add transactional ledger and compaction proof`.

### Task 4: Native campaign save/load round trip

**Files:** create `src/Rail/DurableInboxSave.cs`; modify `DurableInboxStore.cs`; create/complete L379 and L383; modify Program/count.

- [ ] RED tests call the production save-root codec, serialize `DurableInboxSaveRoot` through the configured Phoenix serializer shape, reconstruct snapshot+journal, and prove host order plus canonical choice/result/reward identities survive. Reorder live definition choices/rewards after save; unknown IDs quarantine instead of rebinding.
- [ ] Add `[SerializeType(SerializeMembersByDefault=SerializeMembersType.SerializeAll)] DurableInboxSaveRoot` with magic `Multiplayer.DurableInbox/v1`, schema, snapshot, journal, revision, and CRC. Add Harmony patches on `GeoLevelSavegame.GetObjectsToWrite` and `SetReadObjects` exactly as grounded above. Assert exactly one root in native `Contents` and no sidecar/PlayerPrefs path.
- [ ] Add a save-blob transfer control: the same byte array returned by `ReadSavegameBinary` contains the root and reconstructs it on the client load path before reconciliation.
- [ ] Verify DWI-04/08 only here. Commit `feat(inbox): persist ledger in native campaign saves`.

### Task 5: Exhaustive taxonomy, closed raisers, and Geoscape display gate

**Files:** create `src/Rail/DurableWindowRegistry.cs`; modify `GeoWindowCoverage.cs`, `WindowOrder.cs`, `MissionSync.cs`; create L378/L393/L401; modify Program/count.

- [ ] Replace caller strings/booleans with a closed `NativeRaiserToken` minted only by named Harmony capture adapters for `ShowMissionBriefing -> OpenModalPersistent`, `ToDeploymentState`, and `PrepareDeployAsset`. The adapter computes equality against the actual patched `GetMissionBriefModal(modalData)` result; callers cannot assert `liveModalMatches=true`.
- [ ] Registry table explicitly classifies `EventPopup` durable; each `GeoModalMirror` declared family durable or local-only; `CutsceneMirror` durable ordinary/local playback; `MissionOutcomeMirror` durable outcome; `MarketplaceSync` explicit local-only marketplace presentation unless a stable occurrence trigger is added. Unknown router family is loud failure. L393 derives expected families from actual `SyncEngine` router registrations.
- [ ] Implement `MayPresent` only for fully started Geoscape display surface. Personnel/research/manufacturing/diplomacy/base/aircraft tabs, tactical, loading, lobby accrue only. Priority may preempt only ordinary open Geoscape content.
- [ ] Gate/remove `MissionArrivalNav.Step -> view.LaunchMission`; it enqueues/revalidates priority only. L378 structurally walks the real call path and fails if arrival can invoke `LaunchMission` without `MayPresent`.
- [ ] Verify all registry exclusions. Commit `feat(inbox): classify and gate durable native windows`.

### Task 6: Priority suspension and unchanged resume

**Files:** create `src/Rail/DurableInboxEngine.cs`; modify `WindowOrder.cs`, `WindowQueueSync.cs`, `EventPopup.cs`, `GeoModalMirror.cs`; create L380; modify Program/count.

- [ ] RED L380 opens ordinary content with checkpoint, injects registered priority, and requires durable `Suspended(PriorityPreemption)` before replacement. Callback counts stay zero; same occurrence/result phase/selection/read checkpoint resumes; invalidated suspended content does not.
- [ ] Implement scheduler methods `TryPresentNext`, `TryPreempt`, and `TryResumeSuspended`. Native queue identity is only a carrier adapter. Restore with `EventPopup.RepaintDialog` or exact modal rebuild, never destructive generic Exit/Enter.
- [ ] Verify repaint route and commit `feat(inbox): suspend and resume priority windows`.

### Task 7: Occurrence-indexed carrier registry before terminal transitions

**Files:** modify `DurableInboxEngine.cs`, `EventPopup.cs`, `GeoModalMirror.cs`, `CutsceneMirror.cs`, `MissionOutcomeMirror.cs`, `WindowQueueSync.cs`, `DeploymentWindow.cs`; create L395; modify Program/count.

- [ ] RED L395 registers native current, native pending, mod queued/suspended/deferred, wire/replay, and tactical-held carriers for one occurrence. Deleting current only is the positive-control failure.
- [ ] Implement `RemoveAllCarriers(OccurrenceId, TerminalReason, committedRevision, out refusal)`. It refuses without a committed terminal revision, removes every indexed carrier idempotently, and invokes no choice/dismiss/Cancel/back/completion/reward/launch callback.
- [ ] Every later terminal task must call this one primitive; no scattered deletion is allowed. Verify. Commit `feat(inbox): index and remove every carrier safely`.

### Task 8: Shared choice with durable effect recovery

**Files:** modify reducer/store/engine, `EventSync.cs`, `EventPopup.cs`, `UiEventMap.cs`; create L381/L382; strengthen L383; modify Program/count.

- [ ] Model `Unanswered -> EffectPending(effectToken, canonical choice/result/rewards, winner) -> EffectApplied -> ChoiceLocked`. Persist `EffectPending` before native effect; native replay is idempotent by occurrence-scoped effect token; persist `EffectApplied/ChoiceLocked` after success. Startup/save-load recovery replays only pending tokens and never double grants.
- [ ] RED injects crash/exception after pending commit, after native effect, and before response delivery. Retry/recovery applies one effect and returns the stored result. Invalid answer leaves `Unanswered`.
- [ ] All open copies repaint immediately through `UiEventMap.Fire -> OpenUiRepaint -> EventPopup.RepaintDialog`; queued/suspended copies update first. Winner/losers and actively enrolled peers outside the display gate retain independent result entitlements.
- [ ] Verify. Commit `feat(inbox): recover shared effects exactly once`.

### Task 9: Local offer Cancel and atomic shared Start

**Files:** modify reducer/store/engine, `MissionSync.cs`, `WindowQueueSync.cs`; create L384/L385/L390; modify Program/count.

- [ ] RED proves local Cancel changes only one lifecycle and never calls `GeoMission.Cancel`; first valid Start persists one successor, fresh entitlements for every then-enrolled epoch (including predecessor-dismissed), order, and predecessor link before calling `RemoveAllCarriers`.
- [ ] Inject store failure before and after successor preparation. The offer and at least one carrier survive every failed transition; concurrent Starts dedupe.
- [ ] Open predecessor screens repaint/close through terminal delta plus the Task 7 primitive. No peer readiness exists. Verify. Commit `feat(inbox): transition offers to shared preparation atomically`.

### Task 10: Existing collaborative edit funnels and immediate repaint

**Files:** modify `EquipSync.cs`, `PersonnelSync.cs`, reducer/engine, `UiEventMap.cs`, `OpenUiRepaint.cs`, `DeploymentWindow.cs`; create L386; modify Program/count.

- [ ] RED sends `EquipSync.OpSetItems=8` and `PersonnelSync.OpReassign=4` with `(OccurrenceId, expectedPreparationRevision)` context, tests existing host validation/apply, rejects stale revisions, and observes two already-open preparation UIs plus queued/suspended copies.
- [ ] Do not add another edit opcode. On successful existing host apply, increment preparation revision and route touched mission/container/character/equipment via `UiEventMap.Fire`; call callback-free `DeploymentWindowClose.RepaintDeploymentScreen`, preserve valid local selection, prune invalid entries, rerun native validity/button logic.
- [ ] Verify. Commit `feat(inbox): version and repaint collaborative preparation edits`.

### Task 11: Deferred Back transaction

**Files:** modify reducer/engine, `DeploymentWindow.cs`, `WindowOrder.cs`; create L396; modify Program/count.

- [ ] RED proves Back commits `Deferred` before local navigation, never calls mission Cancel, and cannot reopen in the same tick. Transport churn within an active connection does not clear deferral; disconnect ends the epoch, so a later reconnect has no old deferral to restore.
- [ ] Only explicit local re-entry or a newer material preparation revision requeues. Terminal transitions use Task 7 removal. Verify. Commit `feat(inbox): defer preparation without cancelling mission`.

### Task 12: Source departure revalidation

**Files:** modify reducer/engine, `MissionSync.cs`, `DeploymentWindow.cs`; create L388; modify Program/count.

- [ ] Use the grounded departure seam `VehicleSync.TravelCapturePatch`/host `HandleTravelTo` -> native `GeoVehicle.StartTravel(List<GeoSite>)`, followed by `TravelRepaintPatch.Postfix` (`src/Rail/VehicleSync.cs:200-210,458-493,595-606`), to submit one DWI source-revalidation command after the host write. Client rail applies already repaint on `CurrentSite`/`Travelling`/`DestinationSites`; both paths must converge on the same reducer adapter.
- [ ] RED removes one of two sources and occupants, repaints every open preparation, updates queued/suspended/deferred state, and retains the occurrence. Last/uniquely bound source commits tombstone then calls Task 7 removal without Cancel. Unrelated departure changes nothing.
- [ ] Verify. Commit `feat(inbox): prune deployment sources before invalidation`.

### Task 13: Generation-linked mission completion

**Files:** modify reducer/engine, `MissionOutcomeMirror.cs`, `MissionSync.cs`; create L389; modify Program/count.

- [ ] Add named Harmony adapter at `GeoFaction.OnMissionRewardApplied`: prefix captures the mission's immutable predecessor generation links before outcome creation; postfix submits completion, removes only linked predecessors through Task 7, then captures a new outcome trigger/order outside the removal set.
- [ ] L389 structurally proves the patch reaches the reducer and behaviorally rejects cleanup by mission ID alone. A completion outcome remains deliverable.
- [ ] Verify. Commit `feat(inbox): terminalize linked mission generations`.

### Task 14: Recoverable first-valid launch and native success boundary

**Files:** modify reducer/store/engine, `MissionSync.cs`, `DeploymentWindow.cs`; create L387; strengthen L355; modify Program/count.

- [ ] Model `Preparing -> Launching(attemptId, validated facts)`. Persist `Launching`, then invoke a grounded `GeoMission.Launch(GeoSquad)` adapter that reports success only after the native call returns and the existing tactical-entry/load-boundary arm is accepted. On throw/refusal before announcement, persist rollback to `Preparing`, retain/rebuild carriers, repaint current facts, and expose refusal.
- [ ] Only after native success persist `Launched`, call Task 7 removal, then announce/continue the existing load boundary exactly once. Duplicate commands observe the existing attempt/result. Do not terminalize or remove carriers before success.
- [ ] Remove countdown/veto/readiness/cancel-launch authority. L387 injects native failure after `Launching` persistence but before boundary announcement and proves `Preparing` remains retryable; AFK peers never block.
- [ ] Verify. Commit `feat(inbox): launch once through a recoverable transaction`.

### Task 15: Tactical checkpoint and loaded-ledger ordering

**Files:** modify `LevelTeardown.cs`, `SaveTransferCoordinator.cs`, store/engine, `ReplenishSync.cs`; create L392; modify Program/count.

- [ ] RED commits `Open -> Suspended(LevelTeardown)` and exact checkpoint before `GeoTeardownResetGate.Prefix` calls `ToLoadingState`. Teardown uses Task 7 without callbacks but retains durable ledger.
- [ ] On load, extract/save-root snapshot before native `SetReadObjects`, install resolved ledger after level objects exist, then allow only this peer's started-Geoscape reconciliation. Preserve the existing load-completion barrier; never use it for campaign decisions.
- [ ] Late pre-save replay cannot reopen tombstoned content. Verify. Commit `feat(inbox): restore checkpoints across level boundaries`.

### Task 16: Paged enrollment snapshot/delta over existing 0xB9

**Files:** modify codec/engine, `WindowQueueSync.cs`, `SyncEngineStub.cs`, `SessionManager.cs`; strengthen L377/L391/L399; no `SurfaceIds` or `SyncProtocol` behavior change.

- [ ] Add router dispatch on existing `0xB9` by `SyncKind` plus reserved op `0x40-0x46`; preserve existing ops 1/2. Add a structural law that fails on any new DWI surface constant or change to the three-argument envelope contract.
- [ ] RED reconstructs a snapshot larger than 64 KiB using 48 KiB pages; delivers pages reordered/duplicated, interrupts with a gap, retransmits, supersedes an incomplete snapshot, and applies atomically only when complete. ACK/cursor affects retransmission only.
- [ ] Reconnect with the same PlayerGuid host-serially ends any stale prior epoch, mints/enrolls a new epoch, receives zero prior backlog, and pages only occurrences created after that enrollment. Stale prior-epoch pages/revisions are rejected. Native campaign save/load continuity remains covered by Task 4/15 and must not be implemented by reconnect restore. Verify. Commit `feat(inbox): reconcile paged enrollment snapshots on window surface`.

### Task 17: Capture migration and exhaustive family teardown

**Files:** modify `EventPopup.cs`, `EventSync.cs`, `GeoModalMirror.cs`, `CutsceneMirror.cs`, `MissionOutcomeMirror.cs`, `MarketplaceSync.cs`, `WindowOrder.cs`, `ReplenishSync.cs`, store/engine; strengthen L376/L379/L383/L390/L393.

- [ ] Convert every Task 5 durable family capture to `OccurrenceDraft` with persisted trigger; explicitly leave classified local-only families outside DWI. New router families must break L393 until classified.
- [ ] Remove ephemeral authority (`_held`, `_unanswered`, event-keyed rewards, process-only order, queue caps) only after durable adoption. No historical `GeoscapeEventRecord` scan.
- [ ] Migration order is persist occurrence/entitlements/order/lifecycle/canonical IDs, bind native carrier, then remove legacy carrier. Unprovable identity stays legacy; unresolved canonical identity quarantines and cannot grant twice.
- [ ] Verify. Commit `refactor(inbox): make every routed window an explicit carrier`.

### Task 18: Integrated laws, docs, deploy, and live smoke

**Files:** all L376-L401 and relevant existing laws; then `docs/laws.md`, `README.md`, design status line only.

- [ ] Run integrated production scenarios: tactical accrual for an active epoch; disconnect teardown plus clean reconnect enrollment; priority resume; shared-effect crash recovery; local Cancel/shared Start; collaborative edit/Deferred Back/failed then successful launch; partial/last source departure; completion outcome ordering; >64 KiB post-enrollment snapshot; save/load with reordered definitions.
- [ ] Required existing PASS set: L84, L93, L101, L106, L107, L109, L114, L117, L124, L135, L141, L144, L163, L164, L171, L175, L176, L183, L194, L195, L197, L260, L261, L337, L350, L351, L352, L354, L355, L370.
- [ ] Run exact integrity/build/RailCheck chain. The audited baseline is `files=192, inline=60` (252 registered laws); after exactly 26 new file-backed laws, the expected final count is `files=218, inline=60` (278 registered laws). Every L376-L401 is registered exactly once with a positive control.
- [ ] Commit integrated law fixes as `test(inbox): enforce durable lifecycle integration`.
- [ ] Update docs only after GREEN. Deploy with `pwsh -File "E:\DEV\PhoenixPoint\Multiplayer2\deploy.ps1"`; run one host/two-client smoke covering the scenarios above and inspect stable occurrence/member/revision/order, one effect, one launch, checkpoint-before-teardown, page reconstruction, and repaint logs. No readiness/quorum/veto/drop/callback-on-force-close lines.
- [ ] Commit docs as `docs(inbox): record durable window lifecycle`.

---

## Final acceptance and self-review checklist

- [ ] Matrix maps DWI-01..26 exactly once to L376-L401 and a final GREEN task; codec-only tests do not claim save/load or entitlement behavior.
- [ ] No placeholders remain: scan for `TODO|TBD|FIXME|<EXPLICIT_PATHS>|<PROGRAM_PATCH>|<COMMIT_MESSAGE>|X:\\path|EncodeEnvelope\(byte, byte, uint` and allow only the three documented commit-template tokens inside the generic template, never inside a task command.
- [ ] Type/signature scan confirms `CanonicalResultId`, immutable copied subject equality, `DurableInboxReducer` in Task 1 for L377, and only `EncodeEnvelope(byte, SyncKind, byte[])`.
- [ ] Surface allocation is exact: DWI uses `0xB9` ops `0x40-0x46`; no new `0xA0-0xBF` ID; pages remain below u16 envelope size and reconstruct atomically.
- [ ] Persistence is executable at `GeoLevelSavegame.GetObjectsToWrite/SetReadObjects`, native `Contents`, magic `Multiplayer.DurableInbox/v1`, snapshot+journal+CRC, and the exact save blob transferred to peers. No sidecar or PlayerPrefs.
- [ ] Effect and launch failure windows are recoverable: `EffectPending -> EffectApplied`; `Preparing -> Launching -> Preparing|Launched`; carrier removal occurs only after the corresponding successful durable boundary.
- [ ] Carrier registry exists before Start/invalidation/completion/launch tasks and covers current, pending, queued, open, suspended, deferred, replay/wire, and tactical-held carriers without callbacks.
- [ ] Registry coverage is derived from actual router registrations and includes EventPopup, GeoModalMirror, CutsceneMirror, MissionOutcomeMirror, and MarketplaceSync; each family is durable or explicit local-only.
- [ ] MissionArrivalNav cannot force `LaunchMission`; all presentation passes the fully started Geoscape gate.
- [ ] Collaborative edits reuse grounded EquipSync/PersonnelSync commands and repaint already-open deployment UI immediately through the native callback-free seam.
- [ ] Tasks 1-18 are strictly sequential. Each has one transaction/invariant focus, one named RED/GREEN boundary, an exact repaint/teardown route where visible state changes, and one atomic sentinel commit boundary.
- [ ] Exact commands are fail-fast, noninteractive, index-clean, explicit-path only, and preserve unrelated dirty files/sentinels. No `git add -p`, `git add -A`, `commit -a`, reset, checkout, stash, or clean.
- [ ] Final checks include `git diff --check`, plan placeholder scan, law/matrix coverage scan, Release build, RailCheck, deploy, and three-instance logs before documentation status changes.
