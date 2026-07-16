# Architecture — rail rewrite (recon-grounded, 2026-07-16)

Recon verdict (4-reader workflow over decompile + old repo + docs/research):

## What is NOT feasible (do not attempt)

- **Raw serialized-graph diffing.** Serializer ObjectIDs are session-local nondeterministic ints
  (`SerializationWriter._object2ID`, hash-collection traversal order); wire format is not
  structurally diffable; `SerializationReader.ReadObjects` always builds NEW graphs via
  `Activator.CreateInstance` — never in-place. Two processes holding the same logical state
  produce different blobs.

## What IS feasible (proven by old-repo code)

- Per-entity blob roundtrip: `TacticalDeploySync.SerializeGraph/DeserializeGraph`
  (old repo `src\Sync\Tactical\TacticalDeploySync.cs:1490-1629`) — extract into
  `src/Rail/SerializerRoundtrip.cs`.
- Live hot-apply by field-copy onto running instances: `PersonnelChannel.Apply` →
  `PersonnelReflection.ApplySoldierState`; spawn path `TacticalActorLifecycleSync.HandleActorSpawn`
  → `ActorSpawner.SpawnActor`.
- Stable game-side addressing: `GeoSite.SiteId`, `GeoTacUnitId`, `GeoVehicle.VehicleID`,
  `PPFactionDef` / `ResearchDef` GUIDs.
- Mod-agnostic serialization: `[SerializeType]` + assembly-qualified-name generic reflection —
  TFTV types serialize without our code knowing them.

## Rail design

**Value-delta (universal apply: set fields on live instance + fire native event) covers:**
`Wallet._resources` + `ResourcesChanged`; `ResearchElement` progress + `_researchQueue` order;
`FactionDiplomacy` values; `GeoSite` state/owner/production + `StateChanged`/`OwnerChanged`;
`GeoVehicle` pos (`GeoNavComponent`)/HP/name; ALL of `GeoCharacter` (plain class, no scene
binding — cleanest); `ItemManufacturing` progress; `Timing.Now`; mist/event vars.

**Structural appliers (hand-written, explicit list — the ONLY bespoke sync code allowed):**
site spawn/destroy (GO lifecycle + `GeoMap.SiteAdded/Removed`); vehicle add/loss (scene binding +
`VehicleAdded/Removed`); soldier hire/death (`_tacUnits` registry + `CharacterAdded/Died`);
base build (facility graph); haven zone built/destroyed; research-complete reward chain;
manufacture-complete item creation; mission start/end deployment.

**Wire:** SyncProtocol envelope over SurfaceRouter (quarried), SurfaceSeq + IntentDedup per
surface, CRC backstop. Join/reconnect = native save transfer (SaveTransferCoordinator, quarried).

## Spike (first in-game gate): host starts research → client bar moves, no reload

- Host, on `GeoFaction.ResearchStarted`: `blob = SerializeGraph(new[]{ researchElement },
  quiet:true)` (configured serializer + `Timing.RunUntilComplete`, `TimeSlice(3600f)`,
  `ByRef<byte[]>` via `new object[]{null}`). Send `(factionDefGuid, researchDefGuid, blob)`.
- Client: defer apply via scheduling-gate pattern (network callback has no `Timing.Current`);
  `DeserializeGraph(blob, typeof(ResearchElement), quiet:true)`; find LIVE element in faction
  `Research` queue by `ResearchDef` key; field-copy progress onto it; fire faction research event
  so GeoscapeView (push-model) repaints.
- Then periodic host ticks = pure value delta (defGuid + progress ints, no blob).
- **Log blob byte count + object count** (risk 1 test below).

## Quarry transfer list (verbatim, ~45 files)

Transport (ITransport, TransportType, DirectTransport, CompositeTransport, SteamTransport,
StunTransport, SteamInvite); MessageLayer (PacketType, NetworkMessage, MessageSerializer);
Lobby (LobbyController, SessionLifecycle, SlotAllocator, SteamConnect, ParityManifest,
SessionManager, NetworkEngine, ClientIdentity, HostLeaveHandler, SessionNotifier, LobbyPanel,
LobbyTheme, MultiplayerUI, UiToolkit, NativeWidgetFactory, LoadOverlayController,
LoadOverlayVisibility, ChatLog, MainMenuPatches); Sync primitives (SurfaceSeq, IntentDedup,
SurfaceIds, SyncKind, SyncProtocol, SurfaceRouter, ISyncSink); Bootstrap (MultiplayerMain,
meta.json, Multiplayer.csproj, deploy.ps1, MultiplayerLog, TftvLateBinder, Crc32);
SaveTransferCoordinator + SaveTransferMath; connect-code utils (ConnectCode, InviteCode,
UnifiedCode, SmartJoinParser, JoinPlan, LanIpResolver, UpnpPortMapper).

EXTRACT (not copy): SerializeGraph/DeserializeGraph/ResolveGameSerializer +
TacticalHydrateSchedulingGate → one new `src/Rail/SerializerRoundtrip.cs`.
STUB: `NetworkEngine.Sync` property (new-rail sink). Everything else stays in the quarry.

## Top risks + cheapest early tests

1. **Graph-chase blowup** — serializer walks all non-embedded refs from a root; one
   `ResearchElement` blob may drag the whole level. Day-1 test: serialize one element, log
   byte/object count; if huge → boundary via DTO structs, blobs reserved for structural creates.
2. **Client double-execution** — client sim not frozen by nature; applied delta + local sim tick =
   divergence/duplicates. Test: one wallet delta with sim-gating seam active; verify no second
   event cascade, no local tick fighting host value.
3. **Reflection-copy burden per type** — deserialized blobs are NEW instances; each entity type
   needs a field-copier. Test: copier for ResearchElement (spike), time-box a GeoSite copier;
   if 3rd type still costs days → pivot: value-tuples on wire for value deltas, blobs only for
   structural creates.

## Migration order (each step = in-game gate before next)

1. Skeleton: quarry transfer builds green, mod loads, lobby + DirectIP connect works.
2. Spike: research live delta (above).
3. Wallet/resources value-deltas + sim-gating seam (risk 2 test).
4. Remaining value-delta subsystems (diplomacy, sites, vehicles, characters, manufacturing).
5. Structural appliers one by one.
6. Intents (client actions → host authorize → delta out).
7. Tactical: port from quarry mostly as-is, quarantined under `src/Tactical/`.
