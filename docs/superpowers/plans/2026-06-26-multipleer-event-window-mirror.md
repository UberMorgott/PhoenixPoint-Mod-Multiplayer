# Multipleer — Host→Client Report-Window Mirror (implementation plan)

Status: PLAN (ready to execute), 2026-06-26. Reports-first; digest deferred.
Maps to unified-backbone spec **Increment 4 "collapse the freeze"**, riding the Inc1 rail discipline.
Spec: `Multipleer\docs\superpowers\specs\2026-06-26-multipleer-unified-sync-backbone-design.md`.

## 1. Goal

User's words: *"host broadcasts EXACTLY its own geoscape event windows to all clients, natively;
client purely mirrors and raises none of its own."* This plan delivers that for the **report /
outcome modals** (mission outcome, research complete, base activated, Pandoran reveal, diplomacy
share), mirroring the ALREADY-SHIPPED `GeoscapeEvent`-dialog replication pattern. The deeper
"freeze the client sim" rework (Inc4 proper) is deferred; this is the low-risk per-window broadcast
that proves the shape first.

Discipline (unchanged from spec §0): additive-first, behind a NEW gate defaulting **OFF**
(byte-for-byte unchanged until in-game-validated), ONE chokepoint surface, client never re-executes.

## 2. Context — verified file:line anchors

Reference pattern to MIRROR (already shipped, works):
- Host Postfix broadcast + client Prefix suppress: `EventRaisedDisplayPatch`
  (`Multipleer\src\Harmony\Sync\EventDisplayPatches.cs:30-118`). Prefix returns `true` when
  `SyncApplyScope.IsApplying` (engine replay never blocked, :62); Postfix returns when
  `IsApplying` (never re-broadcast a reconstructed window, :74).
- Broadcast: `SyncEngine.BroadcastEventRaised` (`Multipleer\src\Network\Sync\SyncEngine.cs:488`,
  `PacketType.EventRaised` 0x65) / `BroadcastEventDismiss` (:504, 0x66).
- Client apply: `SyncEngine.OnEventRaised` (:336) / `OnEventDismiss` (:419) → reconstruct refs by
  id → `EventDisplay.Show/ShowResult/Dismiss` (`Multipleer\src\Network\Sync\State\EventDisplay.cs`)
  under `SyncApplyScope.Enter()`.
- Packet dispatch switch: `NetworkEngine.cs:547-555` (`case PacketType.EventRaised → Sync?.OnEventRaised`).
- Hard client suppression of LOCAL raises: `ClientEventRaiseChokepointPatch`
  (`Multipleer\src\Harmony\Sync\EventRaiseChokepointPatch.cs`) + `SuppressEvents` flag.
- Reusable reward-line display snapshot (text-only, no re-apply): `RewardDisplaySnapshot` /
  `RewardDisplayReflection` (`Multipleer\src\Network\Sync\State\`), already used for event result cards.
- Site identity by id: `GeoSite.SiteId` (public int field, default -1) + `GeoSiteReflection`
  (`...\State\GeoSiteReflection.cs`: `ResolveSiteById` :183, `BuildIdentity` :229, `SpawnMirrorSite` :364).
- Gate precedent: `GeoRailGate.Enabled` (`Multipleer\src\Network\Sync\GeoRailGate.cs`) + default
  test `Multipleer.Tests\GeoRailGateTests.cs` (xUnit).
- Free `PacketType` byte: 0x67 `SyncEnvelope`, **0x68 retired (do-NOT-reuse)** → next free **0x69**
  (`Multipleer\src\Network\MessageLayer\PacketType.cs:58-68`).

The CHOKEPOINT all report windows funnel through (decompile
`decompiled\AssemblyCSharp\...\PhoenixPoint.Geoscape.View\GeoscapeView.cs`):
- `GeoscapeView.OpenModalPersistent(ModalType modalType, object modalData=null, int priority=100)`
  (**:849**) → `new UIStateGeoModal(modalType, handler, modalData){Persistent=true}` →
  `_viewSwichQuery.QueryStateSwitch(... PauseGame=true)`.
- `GeoscapeView.OpenModal(ModalType, DialogCallback, object modalData, int priority, bool forceOnTop,
  bool replaceTop)` (**:868**) → `new UIStateGeoModal(...)` → stack/query push.
- Report openers (all route through the two above):
  - win/return-from-tactical: `UIStateInitial.cs:105-112` → `OpenModalPersistent(GetMissionOutcomeModal(lastMission), lastMission, int.MaxValue)`. **`lastMission` comes from local `GeoLevel` params — NOT set on a client that didn't run the battle → root cause the client shows nothing.**
  - `GetMissionOutcomeModal(mission)` type map: GeoscapeView.cs:1800-1881.
  - cancelled base/ancient defence: `OnSiteMissionCancelled` :1930-1940.
  - base activated: `PxFaction_OnBaseActivated` :1961-1968 → `GeoPhoenixBaseOutcome` (null data).
  - Pandoran reveal: `UIStateInitial.cs:118/122` → `PandoranRevealResult` (GeoSite | null).
  - research complete: `OnFactionResearchCompleted` :1980-1992 → `GeoResearchComplete`
    (`!SuppressEvents`-gated → client never opens locally).
  - diplomacy share: `PxFaction_ResearchShared` :1994-2005 → `DiplomacyResearchBrief` (`!SuppressEvents`).
  - interception result: `ShowInterceptionResult` :780-783 → `InterceptionOutcome` (GeoAirMission).
- Modal payload classes: `GeoResearchCompleteData{ResearchElement, SwitchToResearchState}`;
  `DiplomacyResearchRewardData{GeoFaction Faction, IEnumerable<ResearchElement> Researches, int DiplomacyShareLevel}`;
  `MissionRewardDescription{ResourcePack Resources, List<RewardDiplomacyChange> DiplomacyChange}`
  (the outcome modal's reward lines); `GeoMission.Site` (GeoSite), `GeoMission.MissionDef`
  (TacMissionTypeDef), `GeoMission.Reward.ApplyResult` (MissionRewardDescription).
- `ModalType` enum: `decompiled\...\PhoenixPoint.Common.Utils\ModalType.cs`.
- Client-close callback safety: `ResearchCompleteModalHandler` (:2108-2121) can fire
  `ToCutsceneState`/`ToResearchState` on confirm → client must replay with a NEUTRAL callback (see §4.5).

## 3. Per-modal payload table (what to mirror, and how)

Legend: **A** = reconstructable on client from already-synced ids (cheap). **B** = needs extra
payload fields carried in the packet. **C** = not worth mirroring v1 (defer / out of scope).

| Modal (ModalType) | Opener | modalData (live refs) | Class | Wire payload |
|---|---|---|---|---|
| GeoPhoenixBaseOutcome (6) | PxFaction_OnBaseActivated :1965 | `null` | **A** | modalType only |
| PandoranRevealResult (25) | UIStateInitial :118/122 | `GeoSite` or null | **A** | modalType + siteId (-1=none, already synced) |
| GeoResearchComplete (14) | OnFactionResearchCompleted :1987 | `GeoResearchCompleteData{ResearchElement}` | **A** | modalType + researchDefId (ResearchChannel already syncs research; resolve element by id) |
| DiplomacyResearchBrief (38) | PxFaction_ResearchShared :1998 | `DiplomacyResearchRewardData{Faction,Researches,level}` | **A/B** | modalType + factionId + researchDefId[] + shareLevel:i32 |
| Mission OUTCOME modals: GeoHavenAttackOutcome(1), GeoAlienBaseOutcome(3), GeoScavengeOutcome(5), GeoPhoenixBaseDefenseOutcome(12), GeoAmbushOutcome(16), HavenInfiltrateOutcome(18), GeoPhoenixBaseInfestationOutcome(21), AncientSiteAttack/DefenceOutcome(27/29), InfestedHavenOutcome(37), BehemothAttackOutcome(35) | UIStateInitial :112 / OnSiteMissionCancelled :1934/1938 | `GeoMission` (live `.Site`, `.MissionDef`, `.Reward.ApplyResult`) | **B** | modalType + siteId + missionDefId + **rewardDisplayBlob** (reuse `RewardDisplaySnapshot.Encode` of `MissionRewardDescription`) |
| InterceptionOutcome (33) | ShowInterceptionResult :782 | `GeoAirMission` (air-combat state) | **C** (phase 2) | air-combat result not yet modelled in co-op; defer |
| AlienResearchBrief (23) | OnNewAlienIntlligence :2017 | `AlienIntelligenceBriefData` (Researches+Context) | **C** | heavier payload; defer to digest/phase 2 |
| All *Brief / Excavate / Purchase / Recruit / DualClassPicker / ResourcePayment / GameDemoEnd | various | interactive DECISIONS, not reports | **C** | out of scope — belongs to the intent/decision layer, not window-mirror |

Phase split: ship the **A** rows first (one packet, trivial reconstruct, proves the rail+gate+
re-entrancy), then the **B** mission-outcome row (adds rewardDisplayBlob + minimal-mission rebuild),
then revisit **C**.

## 4. Mechanism (mirror of the EventRaised pattern)

### 4.1 Chokepoint patch (one surface, both directions)
New file `Multipleer\src\Harmony\Sync\ReportModalMirrorPatches.cs`, two `[HarmonyPatch]` on the SAME
two native methods (resolve via `AccessTools.Method` with EXACT param types — see
`harmony-accesstools-exact-param-match`: `OpenModalPersistent(ModalType,object,int)` and
`OpenModal(ModalType,DialogCallback,object,int,bool,bool)`):
- **Host Postfix** (`OpenModalPersistent` + `OpenModal`): early-return if
  `!ReportMirrorGate.Enabled` || `SyncApplyScope.IsApplying` || engine null/!ActiveSession/!IsHost.
  Else `ReportModalClassifier.TryBuild(modalType, modalData, out payload)`; if it is a whitelisted
  report type → `engine.Sync.BroadcastReportModal(payload)`. Unknown/decision modals → ignored
  (never broadcast something the client can't safely mirror).
- **Client Prefix** (same two methods): return `true` if
  `!ReportMirrorGate.Enabled` || `SyncApplyScope.IsApplying`; if `engine.IsActiveSession && !IsHost`
  **and** `modalType` ∈ mirrored whitelist → return `false` (suppress the LOCAL window). This is a
  belt (most report openers are already `lastMission`-absent or `SuppressEvents`-gated on the client),
  matching `EventRaisedDisplayPatch.Prefix`. Non-whitelisted modals are left native.
- All wrapped in best-effort try/catch; on any failure native runs (fail-open).

### 4.2 New PacketType + dispatch
- `PacketType.ReportModalShow = 0x69` (host→all). Add `case PacketType.ReportModalShow:
  Sync?.OnReportModalShow(msg.Payload); break;` to `NetworkEngine.cs` switch (next to EventRaised).
- No `ReportModalDismiss` in v1: a report is a one-way notice; each peer closes its own copy locally
  with OK (no shared decision to correlate). (Re-evaluate only if a client close mutates host state — see §4.5.)

### 4.3 Wire payload schema (`SyncProtocol` encode/decode, pure, Unity-free)
`EncodeReportModal/TryDecodeReportModal`:
```
[modalType:u8][variantTag:u8][siteId:i32][priority:i32]
[defId: u16 len + UTF8]              // researchDefId | missionDefId | "" 
[extraIds: u16 count + (u16 len+UTF8)*]   // diplomacy researchDefId[]; factionId folded in defId
[rewardBlob: u16 len + bytes]        // RewardDisplaySnapshot.Encode(...) or empty
```
`variantTag` ∈ {NullData, SiteOnly, Research, Diplomacy, MissionOutcome} selects the client rebuild
path. Reuse `RewardDisplaySnapshot.Encode/Decode` verbatim for `rewardBlob` (already battle-tested
for event result cards).

### 4.4 Client replay (`SyncEngine.OnReportModalShow`)
- `if (_engine.IsHost) return;` (authority).
- decode → reconstruct modalData by id:
  - NullData → `null`.
  - SiteOnly → `GeoSiteReflection.ResolveSiteById(rt, siteId)` (or null; spawn inert mirror if absent
    + identity carried, reusing the event path's `SpawnMirrorSite`).
  - Research → build `GeoResearchCompleteData{ ResearchElement = resolve-by-defId, SwitchToResearchState=false }`.
  - Diplomacy → build `DiplomacyResearchRewardData{ Faction=resolve-by-id, Researches=resolve-by-defId[], DiplomacyShareLevel=level }`.
  - MissionOutcome → minimal reconstruct: resolve `GeoMission` for `siteId` if the client already has
    it; else build a thin display-only mission carrier; arm reward lines via
    `RewardDisplayReflection.SetPending` from `rewardBlob` (same one-shot-by-reference contract as the
    event result card).
- `using (SyncApplyScope.Enter()) GeoModalDisplay.Show(rt, modalType, modalData, priority);`

### 4.5 `GeoModalDisplay` (NEW, mirrors `EventDisplay`)
- Reflectively invoke `GeoscapeView.OpenModalPersistent(modalType, modalData, priority)` (cached
  `MethodInfo`, via `GeoLevelController.View`). Because it runs under `SyncApplyScope.Enter()`:
  the host-side Postfix sees `IsApplying` → no re-broadcast; the client-side Prefix sees `IsApplying`
  → returns true → the native window actually opens. This is the EXACT re-entrancy contract verified
  in `EventDisplay`/`EventRaisedDisplayPatch`.
- **Callback safety:** the native `OpenModalPersistent` builds its own `ModalResultCallback` →
  for completed missions it early-returns (GeoscapeView.cs:804-807) and GeoPhoenixBaseOutcome/
  PandoranRevealResult are no-ops (:843-845) — safe. **GeoResearchComplete** routes to
  `ResearchCompleteModalHandler` which can `ToCutsceneState`/`ToResearchState` on confirm — UNSAFE on a
  client. Decision: for the Research variant, the client opens it through `OpenModal` with an EXPLICIT
  no-op `DialogCallback` (text-only close) instead of `OpenModalPersistent`, OR pre-null
  `SwitchToResearchState` (already false) and confirm that `TriggerCutscene` is null in our test set.
  (Open question Q2.)

### 4.6 Gate
`Multipleer\src\Network\Sync\ReportMirrorGate.cs`:
```csharp
public static class ReportMirrorGate { public static bool Enabled = false; } // SHIPPED OFF
```
While OFF: host Postfix broadcasts nothing, client Prefix suppresses nothing → byte-for-byte
unchanged. Flip to `true` (one-line dev edit + recompile) only after in-game verify, exactly like
GeoRailGate slice-1.

## 5. Relationship to the deeper Inc4 "freeze client sim"

The spec's Inc4 endgame is to FREEZE the client's geoscape producers so every window simply follows
mirrored host state (no local openers to suppress). We do **per-window broadcast first** because:
(1) it is precisely the user's ask — "broadcast EXACTLY its own windows"; (2) it reuses the proven
`EventRaised` shape (chokepoint Postfix/Prefix + id-reconstruct + `SyncApplyScope`), so risk is low
and the diff is small/additive; (3) freezing the sim is a much larger, higher-risk change that needs
full host→client geoscape state replication landed first. This mirror is the additive stepping-stone;
when the freeze lands, the client Prefix-suppress arms become redundant and retire (one per commit).

## 6. Ordered task list (each small, additive, testable)

1. Add `ReportMirrorGate.Enabled=false` + xUnit `ReportMirrorGateTests.Enabled_DefaultsOff`.
2. Add `PacketType.ReportModalShow=0x69`; wire `NetworkEngine` dispatch → `Sync?.OnReportModalShow`.
3. `SyncProtocol.EncodeReportModal/TryDecodeReportModal` + pure round-trip unit tests per variantTag.
4. `ReportModalClassifier.TryBuild(modalType, modalData, out payload)` (whitelist + variantTag map) + unit tests (whitelisted → built; brief/decision → rejected).
5. `GeoModalDisplay.Show(rt, modalType, modalData, priority)` (reflection bridge, cached MethodInfo, `OpenModalPersistent`/`OpenModal` + neutral callback for Research).
6. `SyncEngine.BroadcastReportModal` (host) + `OnReportModalShow` (client reconstruct under `SyncApplyScope`).
7. `ReportModalMirrorPatches` (host Postfix + client Prefix on `OpenModalPersistent`+`OpenModal`, gated).
8. **Phase-A slice:** wire variants NullData/SiteOnly/Research/Diplomacy end-to-end; build green; commit.
9. **Phase-B slice:** add MissionOutcome variant (siteId+missionDefId+rewardDisplayBlob, minimal-mission rebuild, `RewardDisplayReflection.SetPending`); unit tests; build green; commit.
10. Strip/guard `[Multipleer]` DIAG logs to one concise line per broadcast/apply (spec §9).

Each task ends on green `dotnet test` → commit-on-green to inner `main` (no push), per project rule.

## 7. In-game test checklist (2-instance DirectIP 127.0.0.1, gate flipped ON for the run)

- [ ] Host completes a **research** → host shows GeoResearchComplete; **client shows the SAME modal**,
      no cutscene/state-switch on client close; no duplicate/desync modal.
- [ ] Host activates a **new base from exploration** → GeoPhoenixBaseOutcome mirrors on client.
- [ ] Host wins a **haven-defense** that reveals Pandoran sites → PandoranRevealResult mirrors (right site).
- [ ] Host triggers a **diplomacy research share** → DiplomacyResearchBrief mirrors (faction + researches + level).
- [ ] (Phase B) Host returns from a **mission** → mission-outcome modal mirrors on client with the
      SAME reward lines (resources/diplomacy), correct site + mission name.
- [ ] Client NEVER raises a report the host didn't (watch for stray/duplicate modals).
- [ ] Gate OFF run = behaviour identical to today (regression guard).

## 8. Risks / open questions

- **Q1 (top):** MissionOutcome reconstruct depth. The native outcome binder reads off a live
  `GeoMission`/`Reward.ApplyResult`. Decision needed: (a) resolve the real client-side `GeoMission`
  by `siteId` and only inject the reward display blob, vs (b) build a thin display-only mission
  carrier. (a) is cleaner if the mission object actually exists on the sim-live client at outcome
  time; (b) is safer if it doesn't. Resolve by probing `GeoSite`→mission availability on the client
  in the first phase-B in-game run.
- **Q2:** Research-modal client-close callback (`ResearchCompleteModalHandler` → cutscene/research
  state, GeoscapeView.cs:2108-2121). Plan: open the client copy via `OpenModal` with an explicit
  no-op `DialogCallback`. Confirm no other whitelisted modal's persistent callback mutates client state.
- **Q3 (alignment, not blocking):** dedicated `PacketType.ReportModalShow` (0x69) faithfully mirrors
  EventRaised, but the backbone spec §2.1/§6.1 wants new geoscape traffic on the unified `SyncEnvelope`
  (0x67) rail behind `GeoRailGate`. Decision: ship on the dedicated packet now (proven shape, minimal),
  and fold onto the envelope rail in the later Inc1-convergence pass — note it so it isn't forgotten.
</content>
</invoke>
