# Multiplayer2 — PhoenixPoint co-op mod (rail rewrite)

Ground-up rewrite of the co-op campaign mod. Mod name stays **Multiplayer** (same meta.json id,
same DLL name, same target mod folder — deploying this mod REPLACES the old one in the game).
Old repo `E:\DEV\PhoenixPoint\Multiplayer` = **quarry**: reference for seam knowledge (what to
hook, NRE workarounds, game behavior). Read it, never copy legacy sync layers from it
(SyncEngine, channels/codecs, src/Harmony/Sync, Multiplayer.Core/Sync are OFF-LIMITS for porting).

Full design + recon facts: `ARCHITECTURE.md` (read before any code).

## Laws (every agent, every change)

1. **Wire = Intent + Delta, nothing else.** Intent: client → host ("I want X"). Delta: host → all
   ("state changed so"). Snapshot = big delta; initial join = native save transfer (proven, keep).
   New third message kind on the wire = architecture violation.
2. **Addressing = stable GAME ids only.** `GeoSite.SiteId`, `GeoTacUnitId`, `GeoVehicle.VehicleID`,
   faction/def GUIDs (`PPFactionDef`, `ResearchDef`). NEVER serializer ObjectIDs (session-local),
   NEVER list indices (reorder breaks them).
3. **Two delta kinds, no more.** Value-delta: (entityId, field-tuple) → universal apply = set
   fields on the LIVE instance + fire the matching native event. Structural delta: create/destroy
   → explicit hand-written applier (list in ARCHITECTURE.md). Never replace a MonoBehaviour-bound
   instance (GeoSite, GeoVehicle) — patch its fields only.
4. **Client = projector + emitter.** Client never executes game logic, never mutates authoritative
   state outside the single delta `Apply()` path. Client sim is gated (it is NOT frozen by nature —
   clock advance drives its scheduler; gating is an explicit seam). Client RNG is irrelevant by
   construction — all simulation runs on host.
5. **Harmony patches live on 3 seams only:** (a) intent-capture (player action → Intent),
   (b) sim-gating (suppress client simulation/autosave/events), (c) presentation (route visuals).
   A patch that transfers or mirrors state is forbidden — the rail does that.
6. **Reactivity is law #1 (user mandate).** A delta arriving while ANY UI is open repaints that UI
   instantly — fire the native event the view already listens to (GeoscapeView is push-model).
   Never lazy-refresh on next open. Multi-client: all clients see each other's effects immediately.
7. **Ordering & self-heal.** Every surface: SurfaceSeq + IntentDedup + resync-on-gap; CRC backstop
   for drift. Deltas idempotent — applying twice = applying once.
8. **Mod-agnostic by default.** Serializer blobs handle unknown types generically
   (`[SerializeType]` + AQN) — TFTV data rides free. Prefer generic reflection paths over
   per-mod/per-field hardcoding; a hand-listed field is a bug farm ("forgot the grenade").
9. **No test suites.** Build + deploy + in-game gates (user mandate). Every migrated subsystem gets
   an in-game 2-instance gate before the next one starts. Never batch many unverified subsystems.
10. **Progress = what was NOT ported.** A subsystem is done when it works via the rail and its old
    channel/codec/patch counterpart stayed in the quarry. Porting legacy sync code = failure.
11. **Minimal code.** No speculative abstraction, no config for constants, no defensive checks
    without a known failure they guard. Old repo died of boilerplate — don't rebuild it.

## Workflow

- Commit-on-green to inner `main` (this repo), conventional commits, NO feature branches,
  NO push during dev. Deploy via `deploy.ps1` after every green commit (user tests immediately).
- Stage explicit files only — NEVER `git add -A` (parallel sessions on this machine).
- Build = same game-DLL references as old repo's csproj. Serializer roundtrip facts:
  `GameUtl.GameComponent<SerializationComponent>().Serializer` (never `new Serializer(null)`) +
  `Timing.RunUntilComplete` pump, else silently empty graph. Network callbacks have no
  `Timing.Current` — defer applies onto the game loop (TacticalHydrateSchedulingGate pattern).
- Docs = English, compressed bullets, exact names/values.
