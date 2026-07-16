# Architecture — rail rewrite (recon-grounded + mandate v2, 2026-07-16)

Sources: recon workflow (4 readers over decompile + old repo + docs/research) + developer mandate
`docs/MANDATE-v2.md` (verbatim, binding). Where mandate wording and recon facts diverge, the
reconciliation below is authoritative.

## Mandate ↔ recon reconciliation (the one real conflict)

Mandate З5/З8 says "Serialize(current) → Diff(previous, current)". Literal blob-diffing is NOT
feasible (recon): serializer ObjectIDs are session-local nondeterministic ints
(`SerializationWriter._object2ID`, hash-collection traversal order); wire format not structurally
diffable; `SerializationReader.ReadObjects` always builds NEW graphs (`Activator.CreateInstance`).
**Implementation of the law:** the diff engine walks the LIVE game object graph, addressed by
stable game IDs, using the save serializer's TYPE METADATA (`[SerializeType]` + AQN field
discovery) to enumerate persistent fields generically — this preserves the mandate's goals
(generalized enumeration kills "forgot the field"; canonical byte-identical deltas via sorted-ID
traversal + fixed field order; TFTV fields ride free) without depending on blob determinism.
Serializer blobs remain ONLY as payloads for structural creates (spawn → blob → deserialize →
attach → fire native added-event).

## What IS proven (old-repo code)

- Per-entity blob roundtrip: `TacticalDeploySync.SerializeGraph/DeserializeGraph`
  (old repo `src/Sync/Tactical/TacticalDeploySync.cs:1490-1629`) → extracted to
  `src/Rail/SerializerRoundtrip.cs`.
- Live hot-apply by field-copy onto running instances: `PersonnelChannel.Apply` →
  `PersonnelReflection.ApplySoldierState`; spawn path `TacticalActorLifecycleSync.HandleActorSpawn`
  → `ActorSpawner.SpawnActor`.
- Stable game-side addressing: `GeoSite.SiteId`, `GeoTacUnitId`, `GeoVehicle.VehicleID`,
  `PPFactionDef` / `ResearchDef` GUIDs.

## Rail design

**Value-delta (universal apply: set fields on live instance + fire native event) covers:**
`Wallet._resources` + `ResourcesChanged`; `ResearchElement` progress + `_researchQueue` order;
`FactionDiplomacy` values; `GeoSite` state/owner/production + `StateChanged`/`OwnerChanged`;
`GeoVehicle` pos (`GeoNavComponent`)/HP/name; ALL of `GeoCharacter` (plain class, no scene
binding — cleanest); `ItemManufacturing` progress; `Timing.Now`; mist/event vars.

**Structural appliers (hand-written, explicit list — the ONLY bespoke sync code allowed;
identity boundary per law 3):** site spawn/destroy (GO lifecycle + `GeoMap.SiteAdded/Removed`);
vehicle add/loss (scene binding + `VehicleAdded/Removed`); soldier hire/death (`_tacUnits`
registry + `CharacterAdded/Died`); base build (facility graph); haven zone built/destroyed;
research-complete reward chain; manufacture-complete item creation; mission start/end deployment.

**Wire:** SyncProtocol envelope over SurfaceRouter (quarried), SurfaceSeq + IntentDedup per
surface, CRC per path-subtree backstop (diverged subtree resent alone). Join/reconnect = native
save transfer (SaveTransferCoordinator, quarried). Journal (law 9): written post-pipeline,
observational, debug builds OK.

## Spike — split A/B (mandate §3 proof list)

The load-bearing assumption: save serializer usable as live-graph mechanism AND applied state
preserves runtime invariants (caches, subscribers, scheduler, Unity views, backrefs are NOT
serialized — the false-green scenario "CRC matches, game corrupted" is the top danger).

**Spike A (in flight, first in-game gate): host starts research → client bar moves, no reload.**
- Host, on research start: `blob = SerializeGraph(new[]{ researchElement }, quiet:true)`
  (configured serializer + `Timing.RunUntilComplete`, `TimeSlice(3600f)`, `ByRef<byte[]>` via
  `new object[]{null}`). Send `(factionDefGuid, researchDefGuid, blob)`.
- Client: defer to game loop (no `Timing.Current` in network callbacks); locate LIVE element by
  `ResearchDef` key; field-copy progress; fire faction research event → GeoscapeView repaints.
- Periodic host ticks = pure value delta (defGuid + progress ints, no blob).
- Logs blob byte + object count (risk 1 probe).

**Spike B (next batch, before rail generalization):**
1. Structural-apply probe: create/destroy an entity with a live Unity view on the client
   (site spawn or vehicle add) via blob + native added/removed event.
2. Runtime-invariant checklist after Apply: events/subscribers fire; UI reacts; scheduler/timers
   alive; cached dicts/lookups consistent; Unity views bound; backrefs intact.
3. Idempotence: Apply(delta); Apply(delta) → same state.
4. Out-of-order: late seq after newer seq → no damage.
5. No dangling refs after entity destroy (top Unity hazard).

**Fork (mandate §3):** live-apply works broadly → continue in this repo. Works only for part of
the graph → rail narrows to that part, remainder strangler-style in the OLD repo; this repo
becomes reference or is discarded. (Deviation from mandate: spike runs HERE, not in the old repo —
transport was already quarried in; the fork itself is preserved.)

## Migration #1 — Research (src/Rail/ResearchSync.cs, 2026-07-16)

- **Host→all (GeoResearch 0xAA, observe = native event subs + ≤2 Hz poll, zero Harmony):** start
  (OnResearchStarted → serializer blob; >u16 → value-only fallback), progress (poll value delta),
  queue-order snapshot (poll — the catch-all for cancel/reorder/queue-add: `Research.Cancel` of a
  non-current element fires NO native event), complete (OnResearchCompleted → id delta).
- **Client→host intents (GeoResearchIntent 0xAB):** intent-capture prefixes on
  `Research.AddResearchToQueue/Cancel/PutInFromOfQueue/PutUpInQueue/PutDownInQueue/InsertAtPosition`
  (all UIModuleResearch entry points route there). Client: native call BLOCKED, intent sent. Host:
  IntentDedup → validate → run the SAME native method; observe seams broadcast the outcome; invalid
  intent = silent reject (logged). Echo loop closed by `SyncApplyScope` (src/Rail/SyncApplyScope.cs):
  every client apply wraps itself in it; the capture seam passes native through inside it.
- **Sim gating (law 4b):** `Research.Update` prefix-skipped on the client (clock not frozen — the
  local hourly tick would double-progress and locally complete research).
- **Reward-chain boundary (law 3):** client completion stamps `ResearchElement._state` via the
  BACKING FIELD — never the native State setter, whose `Complete()` runs the reward chain
  (ApplyRewards / RewardReputation / Wallet.Give) = host-only logic. Presentation fired directly:
  native completed modal (`GeoscapeView.OnFactionResearchCompleted`) + log line
  (`GeoscapeLog.Faction_ResearchCompleted`).
- **Known limitations (accepted, resolved by later subsystems):** reward side-effects (resources,
  reputation, manufacture unlocks) reach the client only via their own subsystems (wallet =
  migration #2, …); dependent-research reveal/unlock cascades arrive with that research's start
  blob, not at completion (client pedia/stats/GeoscapeEventSystem completion hooks do NOT fire);
  NPC-faction research is frozen on the client (Research.Update gated for ALL factions); client
  start-affordability UI reads the client-local wallet until wallet sync lands.

## Verification (mandate §4)

- Stage 1 differential sim harness: SimCluster/InMemoryTransport host+client, randomized command
  sequences (research/build/cancel/move/produce/trade/pause/resume/save...), after every applied
  step CRC(host)==CRC(client) + trace (seed, step, intent, delta, entity, field). Gates every
  commit. Feasibility note: check quarry `Multiplayer.GameTests`/test infra for reusable headless
  bootstrap before building from scratch.
- Stage 2 in-game 2-instance gate per subsystem.
- Done = stage 1 green + stage 2 passed + legacy counterpart not ported.

## Top risks + cheapest early tests

1. **Graph-chase blowup** — serializer walks all non-embedded refs from a root; one blob may drag
   the whole level. Spike A logs byte/object count; if huge → DTO boundary, blobs only for creates.
2. **Client double-execution** — client sim not frozen by nature (clock advance drives its
   scheduler); applied delta + local tick = divergence. Wallet delta test with sim-gating seam
   active (migration step 2).
3. **Reflection-copy burden per type** — blobs deserialize to NEW instances; each type needs a
   field-copier. If 3rd copier still costs days → value-tuples on wire for value deltas, blobs
   only for structural creates. (Metadata-guided generic copier is the intended escape — same
   serializer metadata as the diff engine.)
4. **False-green** — CRC matches but runtime invariants broken. Spike B checklist is the probe;
   in-game gates stay mandatory regardless of harness green.

## Migration order (mandate §6 — ascending structural complexity; WIP limit 1)

1. Research (almost no identity) — end-to-end first.
2. Wallet/Resources (pure value) + sim-gating seam + risk-2 test.
3. Manufacturing (queue, minimal identity).
4. Diplomacy (mostly values).
5. Personnel (structural begins: soldiers, inventory, refs).
6. Aircraft.
7. GeoSites (spawn/despawn, fog, Unity views).
8. Mission generation — last.
Tactical: quarantined port from quarry, mostly as-is, `src/Tactical/` — separate track.

## Quarry transfer list (verbatim, ~45 files — landed by skeleton stage)

Transport (ITransport, TransportType, DirectTransport, CompositeTransport, SteamTransport,
StunTransport, SteamInvite); MessageLayer (PacketType, NetworkMessage, MessageSerializer);
Lobby (LobbyController, SessionLifecycle, SlotAllocator, SteamConnect, ParityManifest → upgrade
to BLOCKING per law 10, SessionManager, NetworkEngine, ClientIdentity, HostLeaveHandler,
SessionNotifier, LobbyPanel, LobbyTheme, MultiplayerUI, UiToolkit, NativeWidgetFactory,
LoadOverlayController, LoadOverlayVisibility, ChatLog, MainMenuPatches); Sync primitives
(SurfaceSeq, IntentDedup, SurfaceIds, SyncKind, SyncProtocol, SurfaceRouter, ISyncSink);
Bootstrap (MultiplayerMain, meta.json, Multiplayer.csproj, deploy.ps1, MultiplayerLog,
TftvLateBinder, Crc32); SaveTransferCoordinator + SaveTransferMath; connect-code utils
(ConnectCode, InviteCode, UnifiedCode, SmartJoinParser, JoinPlan, LanIpResolver, UpnpPortMapper).
EXTRACT: SerializerRoundtrip.cs (done by skeleton stage). STUB: NetworkEngine.Sync.
