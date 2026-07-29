# RCA — geoscape replication coverage: (A) vehicle flight, (B) planet-level state

> Read-only RCA, 2026-07-29, Multiplayer2 @ `fc8bc04`. Every `file:line` verified in this pass.
> Repo paths relative to `E:\DEV\PhoenixPoint\Multiplayer2\`; decompile paths relative to
> `E:\DEV\PhoenixPoint\decompiled\AssemblyCSharp\Assembly-CSharp\src\`.
>
> Deployment status: nothing since `fc8bc04` deployed yet — deployed DLL is 477184 B
> @ 2026-07-29 13:12:13. Entire chain awaits one in-game retest once geoscape + event windows land.

Predictions about what the classifier WOULD do after a change are marked `PREDICTED` — only a
regenerated `docs/rail-baseline.txt` settles those.

---

## 0. Two corrections to the premises

1. **The vehicle ROUTE already rides.** `docs/rail-baseline.txt:450`
   `+ LeafList DestinationSites (List'1) unordered=no apply=IList`, plus `:469 Travelling`,
   `:449 CurrentSite`, `:460 RangeRemaining`, `:467 TacUnits`, `:472 Weapons`, `:453 Modules`,
   `:471 VehicleID`, `:451 HitPoints`, `:454 Name`, `:448 CanRedirect`. "Route never travels" is
   only true of the client→host direction (no intents). Host→client, the route IS mirrored.
2. **`Pos`/`Rot` are the wrong fields, and not "unmirrored" for the stated reason.** They are
   excluded as `read-only` (`docs/rail-baseline.txt:459/:461`) because `ActorComponent.Pos`/`.Rot`
   are get-only (`Base.Entities/ActorComponent.cs:41-43`) and are NOT the vehicle's carrier.
   The carrier is `SurfacePos`/`SurfaceRot` — excluded as `dto-twin unresolved`
   (`docs/rail-baseline.txt:465-466`), because the live twin is a **hop through a property**:
   `GeoVehicle.RecordInstanceData:1052-1053` writes `Surface.position` / `Surface.rotation`, and
   `Surface` is `public override Transform Surface { get; protected set; }`
   (`PhoenixPoint.Geoscape.Entities/GeoVehicle.cs:89`, assigned `:315`).

---

## 1. Seam map — the generic mechanisms that exist today

| # | Mechanism | `file:line` | Covers | Does NOT cover |
|---|---|---|---|---|
| S1 | **Root table** — the one hand-written entry-point list (`T` clock, `TA` anchor, `F#`, `S#`, `U#`, `V#@owner`, `ES`, `MG`, `MK`, `M#<mod>`) | `src/Rail/IdentityResolver.cs:185-242` | anything reachable *below* a declared root | `GeoLevelController` itself, `GeoSitesMapper`, `InterceptionGameController`, `PhoenixStatisticsManager`, `GeoBehemothActor` |
| S2 | **Key shape** — one ID-probe table `SiteId/VehicleID/ResearchID/FacilityId/Id/Def` + `RootRef` + per-owner qualifier | `src/Rail/IdentityResolver.cs:37`, `:115-148` | naming collection elements and cross-tree refs | any type with none of those members → collection aborts as a walk incident (`DiffEngine.cs:686-689`) |
| S3 | **Metadata classifier + DTO bridge** — field tables from the game's own `GetSerializedMembers`, or a sibling/nested `*InstanceData` DTO resolved onto live members | `src/Rail/RailMeta.cs:640-654` (members), `:669-688` (`FindBridge`), `:725-765` (`ResolveLive`), `:770-786` (`ResolveAliasChain`), `:709-717` (`_twinAliases`) | every value that the save serializer would ship AND whose live twin a convention can reach | name-mismatched DTOs (`GeoLevelController`→`GeoLevelInstanceData`); hops through a **property** (`hop` is typed `FieldInfo`, `RailMeta.cs:779-780`) |
| S4 | **Structural set-diff** — root-key/element-path appear/vanish between walks → create/destroy blob | host `src/Rail/DiffEngine.cs:92` (`StructuralPrefixes = { "U#" }`), `:99-102` (`StructuralElemTypes = { GeoPhoenixFacility }`), `:854-862`, `:871-908`; client `src/Rail/GenericApplier.cs:169-244`, `:276-309` | `U#` GeoCharacter create/destroy, `…_facilities#<id>` create/destroy | `V#`/`S#` (MonoBehaviour actors), and **a Descend FIELD going null↔non-null** — a third shape the set-diff has no representation for |
| S5 | **Intent rail** — one engine, family = surface byte (`0xAB` research, `0xAE` manufacture, `0xAF` personnel, `0xB0` time, `0xB1` base, `0xB3` equip) + block-first posture gate | `src/Rail/IntentRail.cs:94-100` (`ShouldRunNative`), `:107-134` (`Send`), `:140-192` (dispatch), `:202-227` (`Reject`) | those six families | **no vehicle family at all** — route/board/mount/rename/refuel have no client→host path (`src/Rail/SurfaceIds.cs:41-45` are retired tombstones) |
| S6 | **Sim gates (law 4b)** | `src/Rail/ClientSimGate.cs:29-39` (`LevelHourlyUpdateCrt`), `:62-84`, `:91-101`, `:116-125`, `:140-172` | hourly income/haven/base/research/manufacture/recruit/repair, equip write-back, stat commit, facility power | **`GeoNavComponent` movement** — see gap A1 |
| S7 | **Derive/repaint** — per-kind native event + native rebuild table + one-flush coalescing | `src/Rail/UiEventMap.cs`, `src/Rail/OpenUiRepaint.cs`, `ARCHITECTURE.md:214-244` | wallet/research/roster/equip/base/vehicle screens | nothing structural to report here |
| S8 | **Forced re-emit / census / resync** — scoped `ForceReemit(prefix)`, dict census, full resend on gap | `src/Rail/DiffEngine.cs:167-184`, `:741-760`, `:148-156`; client `GenericApplier.cs:180-181` | converging a client after a reject, pruning extra dict keys, seq-gap recovery | **reconciliation**: nothing ever *notices* divergence. Diff is host-now vs host-before (`DiffEngine.cs:458-461`); baseline emits nothing (`:507-517`). The law-7 CRC-per-subtree backstop is declared and **unbuilt** — stated in the repo itself at `src/Rail/SurfaceIds.cs:40` |

---

## 2. Gap table

Layer codes: **RD** root-declaration · **KS** key-shape · **ST** structural create/destroy ·
**LC** leaf/twin codec · **CI** client-intent · **DR** derive-repaint · **RC** reconciliation.

### (A) aircraft / vehicle flight

| # | Symptom user sees | State (`type.field`) | Layer | Evidence `file:line` | Generic fix in the shared mechanism | Blast radius & risk | Silent swallow today? |
|---|---|---|---|---|---|---|---|
| A1 | Client's aircraft **never moves**: it sits at the departure site while the host's flies; after a mid-flight join it flies on its own timeline and never re-syncs | `GeoVehicle.Surface.position/.rotation`; and locally-written `Travelling`, `RangeRemaining` | LC + missing sim-gate (S6) | carrier: `GeoVehicle.cs:1052-1053`, `:1077-1078`; twin unresolved: `docs/rail-baseline.txt:465-466`; hop restricted to fields: `RailMeta.cs:779-780`; client-side authoritative writer: `GeoNavComponent.cs:88/:94/:116/:138` inside `NavigateRoutine:86-143`, reachable on a client via `GeoVehicle.OnLevelStart:383-390` after the join-load | (i) widen `ResolveAliasChain`'s `hop` from `FieldInfo` to `MemberInfo` so a property hop resolves (`RailMeta.cs:770-786`, `RailField.HopFi`); (ii) two rows in the existing `_twinAliases` data table (`RailMeta.cs:709-717`): `GeoVehicle.SurfacePos → Surface.position`, `GeoVehicle.SurfaceRot → Surface.rotation`; (iii) ONE sim-gate prefix on the movement funnel `GeoNavComponent.Navigate` (`GeoNavComponent.cs:152-157`), phrased generically: block on a client outside `SyncApplyScope` **only when `IdentityResolver.RootRef(NavActor.Actor) != null`** (= only actors whose position the rail mirrors) | hop widening touches every `_twinAliases` row (6 today) → RailCheck `L14 hop-mechanics` is the belt. Gate hits `GeoNavComponent.Navigate`'s other callers: `GeoBehemothActor.cs:325/:351/:365/:706` and the resume path `GeoNavComponent.cs:185` — the `RootRef != null` phrasing leaves the Behemoth exactly as it is today (not a root → keeps dead-reckoning) and auto-extends when it becomes one. Traffic: 2 changed fields × flying vehicle per 0.5 s (~54 worst case vs the ~890 measured churn) = negligible. Motion becomes 2 Hz stepping — presentation-only smoothing is a later, separate concern | **Yes for the divergence** (no log line anywhere says "client vehicle position differs"). **No for the twin gap**: `GenericApplier.cs:367` emits a one-time `dto-twin gap: … has no live counterpart` — but only if an entry for that field is ever shipped, and it never is (host-side exclusion), so in practice the only trace is the `docs/rail-baseline.txt` line |
| A2 | Client cannot **send an aircraft anywhere**; clicking a destination does nothing, or moves it only locally | route gesture → `GeoVehicle.StartTravel` / `TravelTo` (`GeoVehicle.cs:518-530`, `:544-577`) | CI | no vehicle family registered: `src/Rail/IntentRail.cs:59` table is fed only by the six surfaces listed in `src/Rail/SurfaceIds.cs:46-53`; retired vehicle surfaces `SurfaceIds.cs:41-45` | ONE new IntentRail family (`0xB4`) registering ops on the **model funnels**, block-first through the existing `IntentRail.ShouldRunNative` (`IntentRail.cs:94-100`): `travelTo(vehicleRef, siteRef)`, `addCrew/removeCrew(vehicleRef, charId)`, `mountModule/unmountModule(vehicleRef, slot, defGuid)`. No new plumbing — nonce/dedup/dispatch/reject already exist | Medium: three capture seams. Crew add/remove already have block-first prefixes for the *containers* (`PersonnelSync.cs:328-346` on `GeoVehicle.AddCharacter/RemoveCharacter`) — check for double-capture before adding a fourth. Worthless before A1 lands (the client would send a route it cannot see executed) | **Yes** — a client gesture that reaches no seam produces no log line at all |
| A3 | An aircraft **manufactured / gifted / captured on the host never appears** on the client (and a destroyed one never disappears) | root key `V#<id>@<ownerGuid>` appearing/vanishing | ST + RD | `DiffEngine.cs:92` (`StructuralPrefixes = { "U#" }`), `:854-862`, `:889-907`; native create `GeoFaction.cs:2004-2019` / `:2021-2035`; re-id on capture `GeoFaction.cs:2037-2044`; registry `GeoMap.cs:241`, `:517-533` | Third structural PAYLOAD shape in the same `EmitStructural`/`ApplyStructural` pair: MonoBehaviour actors cannot ride a native blob (RailCheck `L3 unity-object-blobbed`), so a `V#` create ships `[defGuid][ownerFactionGuid][GeoVehicleInstanceData blob]` and the client replays the game's own spawn — `DefRepository.Instantiate<GeoVehicle>(def)` + `Map.SetActorRootParent` + `DoEnterPlay()` + `ProcessInstanceData(dto)` (the wholesale `ProcessInstanceData` refusal in `ARCHITECTURE.md:169-174` is about REPEAT-apply on a live actor; on a **virgin** actor it is exactly the save-load semantics the game itself uses). Destroy = native `GeoVehicle.Destroy` (`GeoVehicle.cs:597+`) | Largest of the set. `GeoFaction.CreateVehicle:2008` bumps `_lastVehicleIndex`, so the client must NOT use the native id-assigning entry point — take the id from the payload. Capture (`TakeOverVehicle:2041`) re-issues the id ⇒ destroy+create pair, already visible as a root-census/incident line | **Partly no**: `DiffEngine.cs:894-896` logs `structural: create of 'V#…' not enabled — not mirrored` once per key. Then **yes**: every value delta for that vehicle afterwards dies at `GenericApplier.cs:356` `entity not found: V#…`, once per path, and nothing ever retries |
| A4 | Crew/loadout of an aircraft **looks right but is a snapshot**: an item removed on the host stays on the client | any `Descend`/`EntityList`/`Leaf` under a path that VANISHES on the host | RC | `DiffEngine.cs:467-468` — the tombstone loop skips every entry with `SubKey.Length == 0`, i.e. only DICT subkeys are ever deleted; a vanished path emits nothing | see B1 (same root cause) | — | **Yes, fully** |

### (B) planet-level state

| # | Symptom user sees | State (`type.field`) | Layer | Evidence `file:line` | Generic fix in the shared mechanism | Blast radius & risk | Silent swallow today? |
|---|---|---|---|---|---|---|---|
| B1 | **Anything removed on the host is never removed on the client** — a completed mission stays on the map, a scrapped item stays in a list, a dead entity's subtree persists | every non-dict field under a path that stops existing | RC | `DiffEngine.cs:467-468` (only `SubKey != ""` tombstones), `:625-627` (Descend `val == null` → `break`, no entry, no incident), `:458-461` (diff is host-now vs host-before) | Build the law-7 **CRC-per-subtree backstop** that the repo already names as unbuilt (`SurfaceIds.cs:40`): host ships `(rootKey, crc32-of-its-ordered-encoded-pairs)` at low cadence on `0xAC`; client re-encodes its own subtree with the same `Crc32` (already used by RailCheck `L13 crc-diverged` offline) and calls the existing `DiffEngine.ForceReemit(rootKey)` + census on mismatch | Medium. Reuses `Crc32`, `ForceReemit`, `AddCensus` — no new wire concept beyond one message byte. Cost = one extra hash pass per root per cadence tick. Does NOT heal a *missing* structural entity (the client cannot create one) but it makes the divergence **visible and named**, which is the whole class today | **Yes, this IS the swallow.** Missing line: nothing anywhere compares host and client state |
| B2 | **New missions never appear** on the client; and a finished mission never disappears | `GeoSite.ActiveMission` null↔`GeoMission` | ST | covered as a value: `docs/rail-baseline.txt:415` `+ Descend ActiveMission (GeoMission)`; live member `PhoenixPoint.Geoscape.Entities/GeoSite.cs:101`; host-side creator `GeoSite.SetActiveMission:776-785` → `RegisterMission:787+`; structural enable table has no Descend shape: `DiffEngine.cs:854-862` (`StructuralEnabled` only understands root prefixes and `'.'`-containing element paths) | Add the **Descend shape** to the SAME set-diff: in `VisitEntity`'s Descend arm (`DiffEngine.cs:625-627`) record `path.Field` into `_walkRoots` when the field is structurally enabled, so both `null→obj` and `obj→null` fall out of the existing `_prevRoots` comparison (`:871-908`) with zero new wire concept. Apply side: one wiring row beside `ApplyFacilityCreate` (`GenericApplier.cs:276-309`) — assign the field, then the game's own `RegisterMission` (private; `SetActiveMission` itself throws when already set, `GeoSite.cs:778-781`) | Medium. `GeoMission` has many subclasses (`GeoAlienBaseMission`, `GeoAmbushMission`, `GeoScavengingMission`, `GeoAncientSiteMission`, `GeoInfestationCleanseMission`, `GeoPhoenixBaseDefenseMission`, `GeoCustomMission`, `GeoUpdateableMission`) → the blob must round-trip a polymorphic element (RailCheck `L5`/`L6` territory; expect husk arguments per subclass in the baseline). `RegisterMission` subscribes `OnMissionActivated → GeoLevel.LaunchTacticalGame` — on a client that must stay inert until the client is the one launching | **Half**: every delta under the missing path logs `entity not found: S#12.SerializationData.ActiveMission…` once (`GenericApplier.cs:356`). The **removal** half is fully silent (B1) |
| B3 | **Nothing from `GeoLevelController` is mirrored**: mission-scheduler aggro, sub-factions, dead soldiers, the geoscape log, Phoenixpedia, difficulty, mist, tutorial, `NextTacUnitId` | `GeoLevelInstanceData.*` (`PhoenixPoint.Geoscape/GeoLevelInstanceData.cs:21-76`) | RD **+ S3 discovery** | not a root: `IdentityResolver.cs:185-242`. AND — even if declared — `FindBridge(GeoLevelController)` returns null: the sibling probe looks for `…Levels.GeoLevelControllerInstanceData`/`…InstaceData` (`RailMeta.cs:673-674`), the real DTO is `PhoenixPoint.Geoscape.GeoLevelInstanceData` (different name AND namespace), and `GeoLevelController` declares no nested `*InstanceData` (checked) and carries no `[SerializeType]` (`PhoenixPoint.Geoscape.Levels/GeoLevelController.cs:51`) → `SerializedMembers` = 0 → `VisitEntity` bails at `DiffEngine.cs:564` with a `(type): no persistent members` incident | TWO changes, one commit: (i) `FindBridge` gains a rung between the sibling probe and the nested fallback — *the type's own parameterless `RecordInstanceData()` whose return type implements `IInstanceData`* (`GeoLevelController.RecordInstanceData():387-422` returns `GeoLevelInstanceData : IInstanceData`). Generic, uses the game's own record contract, no name list. Use `AccessTools.Method(cur, "RecordInstanceData", new Type[0])` — exact param match matters. (ii) one root row `"GL"` in `IdentityResolver.Roots`, yielded **after** `ES`/`MG`/`MK` | **Widest radius in the repo** — `FindBridge` is consulted for every visited type, so classification can move anywhere; that is exactly what `docs/rail-baseline.txt` + `L18 baseline-drift` exist to make reviewable. Plus two traps (T1, T2 below) that must be closed in the same commit | **Yes in effect**: the incident line only ever reaches `rail-coverage.txt`, and only if the root is declared — today the type is simply absent, and absence has no line |
| B4 | New **sites** (scavenging, ambush, ancient, alien base) never appear on the client | root key `S#<id>`; allocator `GeoSitesMapper._nextSiteId` | ST + RD | `DiffEngine.cs:92`; `PhoenixPoint.Geoscape.Entities.Sites/GeoSitesMapper.cs:180`, `:198`, `:201`, `:207` | same third payload shape as A3 (`GeoSite` is MonoBehaviour-bound too: `[defGuid][GeoSiteInstaceData blob]` + native spawn + `ProcessInstanceData` on the virgin actor). `_nextSiteId` needs the same treatment as `_nextTacUnitId` (T3) | Larger than A3 — a site owns nested components (`GeoHaven`/`GeoPhoenixBase`/`GeoAlienBase`/`GeoScavengingSite`/`GeoHarvestingSite`, dispatched at `docs/rail-baseline.txt:417/:424-425/:432/:437`) | **Partly no**: `DiffEngine.cs:894-896` logs the not-enabled create once; then `entity not found: S#…` per path |
| B5 — **LANDED** `09c565d` (root `"ST"`, law L33) | **Statistics** diverge (mission counts, kills, soldier stats) | `GeoLevelInstanceData.Statistics` (`GeoLevelInstanceData.cs:60`) | RD | recorded as a **clone of a GAME-component object**, not a level member: `GeoLevelController.cs:414` `(PhoenixStatistics)GameUtl.GameComponent<PhoenixStatisticsManager>().GetStatistics().Clone()`. `ResolveLive` has no live `GeoLevelController` member of that type ⇒ stays `bridge-unresolved` even after B3 (**CONFIRMED** against the regenerated baseline, `09c565d`) | its own root row, exactly like `MK`/`ES`: `"ST"` → `GameUtl.GameComponent<PhoenixStatisticsManager>().GetStatistics()`. `PhoenixStatistics` is `[SerializeType(SerializeAll, Embedded = true)]` (`PhoenixPoint.Common.Core/PhoenixStatistics.cs:10`) so the direct classifier handles it with no bridge | Small, and **SETTLED**: the dict members classify `EntityList … unordered=yes apply=ICollection<T>` over `KeyValuePair<…>` elements, every new element type came back `husk=none roundtrip=ok`, and the root added **no** new exclusion (`excluded 85→85`) | **Yes** (no root ⇒ no line) — closed by the `"ST"` row |
| B6 | **Air missions / interception** never mirror | — | boundary (L-A gate 1) | `GeoLevelInstanceData.CurrentAirMission:66` is **dead**: it is the only occurrence of the identifier outside `GeoAirMission.cs` itself and `RecordInstanceData:394-419` never assigns it. The live state lives on `InterceptionGameController`, a `MonoBehaviour` with **no `[SerializeType]` and no `*InstanceData`** (`PhoenixPoint.Geoscape.Interception/InterceptionGameController.cs:23`, `:94`) | **None available.** The game does not persist interception, so boundary-law L-A gate 1 refuses it: the value rail cannot express it by construction. Air combat is a real-time minigame in the same class as tactical — a separate project, not a rail gap. Record this as a declared non-goal | — | n/a |
| B7 — **DECIDED 2026-07-29: DECLARED EXCLUSION, refused on evidence (§6)** | **Other mods' geoscape state (TFTV) never mirrors** | `GeoLevelInstanceData.ModData` (`Dictionary<string, ModInstanceData>`, `GeoLevelInstanceData.cs:70`) | RD (derived root) | populated only at record time by `ModManager.RecordGeoscapeInstanceData:730-744` (`PhoenixPoint.Modding/ModManager.cs`); `ModInstanceData` = `{ TypeName, JsonData }` (`PhoenixPoint.Modding/ModInstanceData.cs`); no live `GeoLevelController` member ⇒ unresolvable by any convention even after B3. TFTV **does** use this API: `refs/TFTV-src/TFTV/TFTVGeoscape.cs:105` `class TFTVGeoscape : ModGeoscape`, `:224 RecordGeoscapeInstanceData`, `:309 ProcessGeoscapeInstanceData`; TFTV also patches the apply path (`refs/TFTV-src/TFTV/TFTVCriticalStuff.cs:111-151`) | Reuse the **synthesized-DTO-root pattern that already exists** for the clock (`IdentityResolver.cs:192-193` yields `TimeAnchor.HostDto(...)`, client resolves `TimeAnchor.ClientDto(...)` at `:342`): a `"MD"` root holding one `Dictionary<string, ModInstanceData>`; host fills it per mod, client applies via the game's own `ModManager.ProcessGeoscapeInstanceData` (public, `ModManager.cs:746-767`). Every mod, including TFTV, rides one mechanism — no per-mod code, and `IdentityResolver.RegisterModRoot` (`:166-175`) stays for OUR own state | **REFUSED — see §6, which CORRECTS clause (a) below.** (a) Filling the root calls every mod's `RecordGeoscapeInstanceData` **every walk** — understated here as "log flood + per-walk JSON": TFTV's record hook also **MUTATES campaign state** (`TFTVRevenant.cs:1786-1792` increments the `"Revenant_Spotted"` event variable), so no cadence makes it a pure read. (b) `ProcessGeoscapeInstanceData` is a **load-shaped** apply run over ALL mods at once; repeat-applying it is the same trap as the refused wholesale `ProcessInstanceData` (`ARCHITECTURE.md:169-174`). Needs per-mod opt-in | **Yes** (no root ⇒ no line) |

---

## 3. Ordered plan — batches of 1-3, each independently deployable and in-game testable

Next free harness law number at time of writing was **L24**; as of 2026-07-29 the highest landed
is **L25** (`crc-backstop`), next free = **L26** (event-window work claims L26). Law numbers for
unimplemented batches below are from the original draft — renumber at implementation time.

### Batch 1 — vehicle flight becomes a pure mirror *(gap A1)* — LANDED `404d696`

> L14 extended and falsified both ways.

1. `RailMeta.ResolveAliasChain` (`RailMeta.cs:770-786`): widen `hop` from `FieldInfo` to
   `MemberInfo` so a **property** hop resolves (`Surface` is an auto-property, `GeoVehicle.cs:89`);
   plumb through `RailField.HopFi`.
2. Two rows in `_twinAliases` (`RailMeta.cs:709-717`), cited to `GeoVehicle.cs:1052-1053/:1077-1078`.
3. One client-only sim-gate prefix on `GeoNavComponent.Navigate` (`GeoNavComponent.cs:152-157`),
   condition = client ∧ `!SyncApplyScope.Active` ∧ `IdentityResolver.RootRef(NavActor.Actor) != null`.
- Regenerate `docs/rail-baseline.txt` in the SAME commit (boundary-law L-F).
- **Law**: extend `L14` — `hop-mechanics` must assert a property hop round-trips (get→set→get through
  `Surface.position`), and `twin-coercion` must assert `GeoVehicle.SurfacePos`/`SurfaceRot` are
  **covered, not `dto-twin unresolved`**.
- **Falsify**: revert the `MemberInfo` widening → `ResolveAliasChain` returns null for
  `Surface.position` → `L14 twin-coercion` must go RED naming
  `GeoVehicle.SurfacePos`; and `L18 baseline-drift` must go RED on the two twin-table lines.
- **In-game gate**: host sends an aircraft on a 3-hop route; client sees it move, and sees it land at
  the same site. Then client joins mid-flight and must NOT free-run (log: zero
  `[MP][diag] SetPowered`-style local writes; assert `Travelling`/`RangeRemaining` only change on apply).

### Batch 2 — `GeoLevelController` becomes a root *(gap B3, + the two traps)* — LANDED

> Law **L28** `root-owned-instance-two-paths` / `undeclared-root-reach` / `stale-root-reach-declaration`
> / `gl-not-last` / `undeclared-exclusion`, every arm falsified. Baseline: `types 60→63`,
> `covered 247→253`, `excluded 64→83` — the GL table is **covered 5/24**
> (CurrentDifficultyLevel, DeadSoldiers, ExtraGameSettings, NextTacUnitId, StartingPopulation).
> Four DECLARED exclusions: `TacUnits`, `ModData`, `ContextHelpData`, `GeoscapeLog`.
>
> **Three corrections to this section, found by executing it:**
> 1. `RailType.Build`'s bridge arm has **no component-dispatch rung** — the `> dispatch X ->
>    GetComponent(T)` in the twin tables is the CLIENT resolve path (`IdentityResolver.cs:288-292`),
>    fed on the host by walking a RECORDED DTO object (`GeoSite.SerializationData`).
>    `GeoLevelController` has no such property, so `MissionSchedulerData` / `EventSystemInstanceData` /
>    `MistData` / `TutorialInstanceData` / `PhoenixpediaData` /
>    `DynamicDifficultySystemInstanceData` / `MarketplaceInstanceData` are now NAMED
>    `bridge-unresolved` exclusions, not coverage. **MissionScheduler still does not mirror** ⇒ "new
>    missions never appear" is NOT fixed by this batch. Next batch = one generic host-side rung (a
>    `GetComponent(valType.DeclaringType)` accessor kind on `RailField`, the closure seeded with the
>    component type so its coverage stays baseline-visible). Trap T1's double-visit with `ES`/`MK` is
>    therefore not armed YET — it arms with that rung, which is why L28 landed first.
> 2. `TacUnits` does **not** resolve today (T1's `PREDICTED` settled): `TwinTypeCompatible`'s
>    live-dict-from-DTO-list rung needs `DictKeyMember(IGeoTacUnit, GeoTacUnitId)` and
>    `GetSerializedMembers` yields nothing for an INTERFACE. The landmine is disarmed only by a lookup
>    that fails ⇒ the declared opt-out is what actually holds it (falsified: removing it leaves
>    `reason='bridge-unresolved'`, and L28 goes RED).
> 3. `GeoscapeLog` DOES resolve (onto live `Log`) and had to be excluded: its keyless
>    `List<GeoscapeLogEntry>` blob rebuilds entries whose whole content is rail-refused
>    `LocalizedTextBind`, and that type is a CLASS ⇒ `GenerateMessage()` NREs on `Text == null`
>    (`GeoscapeLogEntry.cs:11-13`, `:23-25`). **The husk gate misses this class entirely**: `HuskScan`
>    counts every `GetSerializedMembers` name as carried (`RailMeta.cs:2020-2023`), so a member the
>    RAIL excludes but the SAVE serializes is never a husk — a generic gap deserving its own batch and
>    law. Mirroring the log needs a text-key codec for `LocalizedTextBind` first.

1. `FindBridge` (`RailMeta.cs:669-688`): new rung = own parameterless `RecordInstanceData()`
   returning an `IInstanceData`.
2. `IdentityResolver.Roots` (`:185-242`): yield `"GL"` → `geo`, **after** `ES`/`MG`/`MK`.
3. Declared exclusions for `GeoLevelInstanceData.TacUnits` (T1) and anything the regenerated
   baseline shows newly riding that is presentation or host-only (T2 candidates).
- **Law** (originally drafted as L24): `root-owned-instance-two-paths` — no instance reachable from a
  declared root may be reachable from a *second* declared root's member closure (static walk of the
  classified tables from the root list) — the double-visit/path-migration detector.
- **Law**: extend `L20`-style declared-exclusion assertion: `GeoLevelInstanceData.TacUnits` must be
  `Excluded`, with the reason string present.
- **Falsify**: delete the `TacUnits` exclusion → the new assertion goes RED naming
  `GeoLevelInstanceData.TacUnits -> _tacUnits`; reorder `"GL"` before `"ES"` → law goes RED naming
  `GeoscapeEventSystem reachable from GL and ES`.
- **In-game gate**: host takes a haven-attack hit ⇒ client's geoscape LOG shows the entry; host loses
  a soldier ⇒ client's memorial/dead-soldier list matches; `rail-coverage.txt` shows a `GL` root with
  the expected covered set and **no** `TacUnits` line.

### Batch 3 — reconciliation backstop *(gaps B1, A4 — and a safety net under every later batch)* — LANDED `3ad18a3`

> Law L25 `crc-backstop`, falsified both ways.

1. CRC-per-root-subtree on `0xAC` (new message byte beside `MsgDelta`/`MsgStructural`,
   `DiffEngine.cs:38-40`), host side computed from the ordered encoded pairs it already has.
2. Client compares its own re-encode; mismatch → existing `ForceReemit(rootKey)` + census, and ONE
   named log line per diverged root.
- **Law**: promote `L13 crc-diverged` from field-codec-only to subtree level — construct two tables
  differing by one entry and assert the subtree CRCs differ.
- **Falsify**: make the client's CRC ignore one field class → `L13` must go RED naming that class.
- **In-game gate**: host completes a mission (`ActiveMission` → null) ⇒ client logs a CRC divergence
  for `S#<id>` even though B2 is not landed yet. That is the proof the class is now *visible*.

### Batch 4 — structural Descend create/destroy *(gap B2, "new missions appear")*

1. Descend shape in `_walkRoots` + `StructuralEnabled` (`DiffEngine.cs:625-627`, `:854-862`).
2. One apply-side wiring row for `GeoSite.ActiveMission` next to `ApplyFacilityCreate`
   (`GenericApplier.cs:276-309`).
- **Law** (originally drafted as L25, now taken by `crc-backstop` — renumber at implementation):
  `structural-descend-unenabled` — every Descend field whose declared type is blob-reconstructable
  and which is **nullable at runtime** must be either structurally enabled or listed as a declared
  opt-out — the static twin of the "path vanished, nothing shipped" bug.
- **Falsify**: remove `GeoSite.ActiveMission` from the enable/opt-out list → law goes RED naming it.
- **In-game gate**: host triggers a haven defence ⇒ the mission marker appears on the client; host
  completes it ⇒ the marker disappears.

### Batch 5 — `V#` / `S#` actor create/destroy *(gaps A3, B4)*

Third structural payload shape (`[defGuid][ownerGuid][DTO blob]` + native spawn + virgin-actor
`ProcessInstanceData`), `V#` first (smaller closure), `S#` second.
- **Law** (originally drafted as L26): `structural-actor-payload` — a structurally-enabled root whose
  type is a `UnityEngine.Object` must declare the DTO-payload shape, never the graph blob (the
  apply-side twin of `L3 unity-object-blobbed`).
- **Falsify**: enable `V#` with the blob payload → law RED naming `GeoVehicle`.
- **In-game gate**: host finishes manufacturing an aircraft ⇒ it appears in the client's roster with
  crew and modules; host scraps it ⇒ it disappears.

### Batch 6 — vehicle intent family `0xB4` *(gap A2)*

`travelTo` / crew / module ops, block-first through `IntentRail.ShouldRunNative`.
- **Law**: extend `L12 intent-prefix` to the new family; **falsify** by dropping the family's
  reconverge registration → `L12` RED.
- **In-game gate**: client routes an aircraft, boards a soldier, mounts a module — all three visible
  on the host and on a third peer.

### Batch 7 — `"ST"` statistics root *(B5)* — LANDED `09c565d`; `"MD"` ModData root *(B7)* — REFUSED

> Law **L33** `root-covers-nothing` / `stale-empty-root-declaration`, every arm falsified. Baseline:
> `types 82→93`, `covered 373→441`, `excluded 85→85` (unchanged — the root added no new opt-out),
> husk sweep `31→44` types, all new element types `husk=none roundtrip=ok`. `PhoenixStatistics`
> classifies `[direct] covered=5/5` off its own `[SerializeType(SerializeAll, Embedded)]`; no bridge.

1. `IdentityResolver.RootKinds`: one row `("ST", typeof(PhoenixStatistics), geo => GameUtl
   .GameComponent<PhoenixStatisticsManager>()?.GetStatistics())`, slotted after `"MK"` and before
   `"GL"` (which stays last — `L28 gl-not-last`). The client resolves it through the SAME table row
   (`ResolveRoot`), so no apply-side wiring exists to forget.
2. `"ST"` is the first root whose instance does not hang off the level — the RCA's prediction is
   CONFIRMED: `GeoLevelInstanceData.Statistics` is a CLONE of the manager's object
   (`GeoLevelController.cs:414`) restored through `SetStatistics` (`:583`), so the GL bridge member has
   no live twin and remains `bridge-unresolved` in the regenerated baseline. The live object is
   reachable only through the game component, which is what the root row does.
3. **Per-campaign, not per-peer** (the question this batch had to settle): everything per-session lives
   on the MANAGER MonoBehaviour — `_achievements`, `_geoAchievementsTracker`, `_geoPlayerStatsTracker`,
   `_tacticalLevelController`, `MinAgeAtRecruitment`, `ExplosiveWeaponTagDef` — and none of it is inside
   `GetStatistics()`'s object, so the root cannot reach it. Every member of `PhoenixStatistics` /
   `GeoscapeStats` / `SoldierStats` is a save-persisted campaign counter, a Def ref, a `TimeUnit` or a
   `ResourcePack`; no UI handle, no `LocalizedTextBind`, no live entity ref (checked member by member).
   Mirroring also SETTLES a genuine per-peer roll rather than creating one: `SoldierStats.DateOfBirth`
   is `UnityEngine.Random.Range` on whichever peer creates the stat
   (`PhoenixStatisticsManager.cs:680`), and `GeoscapeStats.CurrentDate` is written from the writer's own
   clock (`:235`).
- **Law**: `L33` — a declared root whose classification silently finds nothing is INERT (walk enters,
  emits nothing, no line anywhere). Generalizes `L31 actor-root-uncovered` to every root, including
  bridge-covered ones; `"MG"` becomes a DECLARED empty root with its reason and a declaration that
  stops being true is RED.
- **Falsified**: `("ST", typeof(BaseStatistics), …)` — the natural mistake, since that IS
  `GetStatistics()`'s declared return type and it has no members — goes RED naming `root 'ST'
  (BaseStatistics)`; dropping `"MG"`'s declaration goes RED naming it (same arm, stated as such);
  declaring `"ST"` empty goes RED `stale-empty-root-declaration … now covers 5 member(s)`; a
  declaration key that is not a root kind goes RED on the dead-key branch.
- **L28 needed no code change**: it reads `RootKinds` directly, so the new row is swept automatically.
  And `TypeClosure` skips `Excluded` fields, so `GL` does not reach `PhoenixStatistics` today — if a
  future twin-alias ever resolves `GeoLevelInstanceData.Statistics` onto something live, L28 goes RED by
  itself with `undeclared-root-reach: root 'ST' … reachable from the LATER root 'GL'`.
- **Honest gap**: RailCheck never touches a live `GeoLevelController` (`tools/RailCheck/Program.cs:19`)
  — this is schema, not convergence. The four new dict-pair element types round-trip
  `live-gated (pair key …)`, i.e. their encode→decode is NOT asserted headless (a Def / `GeoTacUnitId`
  pair key needs a live `DefRepository`), the same declared gap class as the 3 pre-existing
  `live-gated` entries.
- **In-game gate**: host completes a mission and loses a soldier ⇒ the client's statistics screen shows
  the same mission count and the same memorial entry; `rail-coverage.txt` shows an `ST` root.

`"MD"` (B7) is **refused**, not deferred — see §6.

---

## 4. Traps

**T1 — `GeoLevelController` as a root: does the walk blow up, alias, or double-visit?**
Not unbounded (the `MaxDepth 12` / `MaxEntities 50000` brakes and the reference `visited` set already
hold — `DiffEngine.cs:554-555`, `:553`), but **two real hazards**:
- *Double-visit / silent path migration.* `GeoLevelInstanceData` reaches, via the existing
  nested-component dispatch rung, `GeoscapeEventSystem` (`:34`), `GeoMarketplace` (`:68`),
  `GeoPhoenixpedia` (`:48`), `MistRendererSystem` (`:40`), `GeoscapeTutorial` (`:44`),
  `DynamicDifficultySystem` (`:36`), `GeoMissionScheduler` (`:54`) — three of which are ALREADY roots
  (`ES`, `MG`, `MK`, `IdentityResolver.cs:236-238`). The `visited` set makes the second visit a
  **silent return** (`DiffEngine.cs:553`), so whichever path is walked first owns every field and the
  other path's entries vanish from the snapshot with **no tombstone and no incident**. Deterministic
  only because the root ORDER is fixed → yield `"GL"` last, and add law (originally L24).
- *The `TacUnits` landmine.* `GeoLevelInstanceData.TacUnits` is `List<IGeoTacUnit>` (`:30`); the live
  registry is `private readonly Dictionary<GeoTacUnitId, IGeoTacUnit> _tacUnits`
  (`GeoLevelController.cs:162`). `ResolveLive`'s `_name` backing-field rung (`RailMeta.cs:744-748`)
  finds `_tacUnits`, and `TwinTypeCompatible`'s live-`Dictionary`-from-DTO-`List` rung
  (`RailMeta.cs:799-810`) licenses exactly that shape when the key is re-derivable from one element
  member — `IGeoTacUnit.Id` is `GeoTacUnitId`. So this **plausibly resolves** (`PREDICTED` — the
  regenerated baseline is the arbiter) and would then rebuild the entire tac-unit registry from an
  `EntityList` blob every time it changed: husked `GeoCharacter`s replacing live ones, colliding with
  the `U#` structural applier that owns exactly this registry (`GenericApplier.cs:206-214`). **Must
  land as a declared exclusion in the same commit**, in the shape `ActorInstanceData.TimingData`
  already uses (`docs/rail-baseline.txt:36`).
- Lower-severity, verify against the regenerated baseline: `MissionToComplete` (`:32`) — `PREDICTED`
  excluded, because the unique-type fallback is ambiguous between `_missionToComplete` and
  `CurrentMission` (`GeoLevelController.cs:308`, `:336`); `GeoscapeLog` (`:46`) — `PREDICTED`
  resolves via the unique-type fallback onto `Log` (`GeoLevelController.cs:316`), and its payload is
  `[SerializeMember] private readonly List<GeoscapeLogEntry> _entries`
  (`PhoenixPoint.Geoscape.Levels/GeoscapeLog.cs:36-37`) — a **keyless** list, so expect either an
  `EntityList` husk argument or a walk incident, not free coverage; `ContextHelpData` (`:62`),
  `MistData` (`:40`), `TutorialInstanceData` (`:44`) — presentation-ish, decide explicitly rather
  than by accident. `Serializer` blow-up risk is nil: nothing here is blob-encoded except via the
  existing `EntityList` path, which the harness already sweeps (`L15 nested-husk`).

**T2 — does mirroring position fight the client's own sim?**
Yes, and that is precisely why batch 1 pairs the twin rows with the gate. `GeoNavComponent.NavigateRoutine`
(`GeoNavComponent.cs:86-143`) is an **authoritative** writer, not presentation: it sets
`NavActor.Travelling` (`:88`, `:94`, `:138`), `NavActor.RangeRemaining` (`:116`, `:135`) — both
rail-covered leaves (`docs/rail-baseline.txt:469`, `:460`) — drives `PivotTransform.localRotation`
(`:111`), and fires `Arrived` (`:139`) → `GeoVehicle.OnArrived:327-350` →
`CurrentSite.VehicleArrived(this)` + `OnArrivedAtDestination`, i.e. **gameplay outcomes on a
projector client** (law 3). It is reachable on a client today: `GeoVehicle.OnLevelStart:383-390`
re-issues `Navigation.Navigate(path)` whenever the loaded save has `Travelling && destinations`, which
is exactly the mid-flight join case. The diff rail cannot correct a client-local mutation until the
HOST value changes (`DiffEngine.cs:458-461`), so this divergence is permanent.
Rejected alternative: mirror the route only and **re-issue `Navigate` on the client** as an
"apply-side native rewire" (the game's own `OnLevelStart` move). It gives smooth motion and ~zero
extra traffic, but it deliberately re-enables the authoritative writes above and double-fires arrival
outcomes on every peer. Not worth it — the gate + 2 Hz mirror keeps the client a projector.

**T3 — is `_nextTacUnitId` an allocator problem rather than a replication problem?**
Both, and the allocator half is primary. `GeoLevelController._nextTacUnitId` (`:158`) is bumped only
by `CreateTacUnitId():1521-1526`; the true invariant is **the client never allocates** (enforced by
the hourly gate `ClientSimGate.cs:29-39` for `GenerateRecruits` and by block-first personnel intents).
Replication is a *belt*, and a cheap one: `GeoLevelInstanceData.NextTacUnitId` (`:26`) resolves onto
`_nextTacUnitId` by the plain backing-field convention, so it rides for free the moment batch 2 lands
— and it is host truth, so mirroring it can only tighten the invariant while the client never
allocates. Do **not** treat it as the fix: if a client ever does allocate, a mirrored counter silently
rewinds and re-issues live ids. Same shape, not yet addressed: `GeoSitesMapper._nextSiteId`
(`GeoSitesMapper.cs:180`, `:198`, `:207`) and `GeoFaction._lastVehicleIndex` (`GeoFaction.cs:73`,
recorded at `:376` so `PREDICTED` already riding on the `F#` root) — the latter is why batch 5's
client-side create must take the id from the payload instead of calling `CreateVehicle`
(`GeoFaction.cs:2008` bumps the counter).

**T4 — does `ModData` contain unserializable / mod-owned graphs?**
No: `ModInstanceData` is `{ string TypeName; string JsonData; }`
(`PhoenixPoint.Modding/ModInstanceData.cs`) — two strings, trivially leaf-encodable, and unchanged
mod state produces byte-identical values so an idle mod costs nothing on the wire. The danger is
entirely on the two ends, not in the payload: **record** is a per-walk call into every mod's
`RecordGeoscapeInstanceData` (`ModManager.cs:730-744`), which for TFTV allocates a JSON graph and
logs a line (`refs/TFTV-src/TFTV/TFTVGeoscape.cs:224-226`); **apply** is `ProcessGeoscapeInstanceData`
(`ModManager.cs:746-767`), a load-shaped path that loops over ALL mods and hands each a freshly
deserialized object — repeat-applying it into a running mod is the same class of bug as the
wholesale `ProcessInstanceData` that was audited and refused (`ARCHITECTURE.md:169-174`), and TFTV
already Harmony-patches that exact method (`refs/TFTV-src/TFTV/TFTVCriticalStuff.cs:111-151`), so our
extra invocations run through their patch too. Verdict: mechanically expressible, behaviourally
risky — last batch, change-driven cadence only, per-mod opt-in.

---

## 5. `UNVERIFIED` / `PREDICTED` claims

- Every `PREDICTED` classification outcome in T1/T3/B5/B7 — they are readings of
  `RailMeta.ResolveLive`/`TwinTypeCompatible`/`BuildField` logic, not of a regenerated baseline. The
  authority is `dotnet run -c Debug -- --update` in `tools/RailCheck`.
- **UNVERIFIED**: the exact native spawn the SAVE-LOAD path uses for a `GeoVehicle`/`GeoSite` actor
  (batch 5 payload shape). Grounded alternatives read: `GeoFaction.CreateVehicle:2004-2019` and
  `CreateVehicleAtPosition:2021-2035` (runtime create), and `GeoMap.SetActorRootParent` /
  `GeoMap.cs:863` for parenting. The load-time actor instantiation itself (`Base.Levels` /
  `ActorCreateData` / `SceneObjectIds`) was not read.
- **UNVERIFIED**: whether writing `Surface.position`/`Surface.rotation` alone yields correct VISUALS
  (heading, animator state `Animator.SetInteger("State", …)` at `GeoVehicle.cs:337`/`:582`) while the
  client's `PivotTransform.localRotation` is not driven by nav. The game's own restore does exactly
  these two writes (`GeoVehicle.cs:1077-1078`), which is the best available grounding — an in-game
  look is the only real check.
- **UNVERIFIED**: whether `GeoBehemothActor` reaches `GeoNavComponent.Navigate` on a client in
  practice (its callers are read: `GeoBehemothActor.cs:325/:351/:365/:706`) — the batch 1 gate
  condition is written so the answer does not matter.
- ~~**UNVERIFIED**: whether `GeoPhoenixpedia.InstanceData` / `MistRendererSystem.MistRendererInstanceData`
  / `GeoscapeTutorial.InstanceData` implement `IInstanceData`~~ — **SETTLED 2026-07-29: they do NOT.**
  All three (plus `GeoMissionScheduler.InstanceData`) are plain `public class` with no base list
  (`GeoPhoenixpedia.cs:25`, `MistRendererSystem.cs:24`, `GeoscapeTutorial.cs:41`,
  `GeoMissionScheduler.cs:36`), so batch 2's record-contract rung is INERT for them — they keep riding
  `FindBridge`'s nested probe and the baseline widened by the `GL` row alone (+3 types). Same check
  disarmed the hijack risk on the two roots that already exist: `GeoscapeEventSystem
  .RecordInstanceData()` (`:660`) and `GeoFaction.RecordInstanceData()` (`:354`) return **void**.


---

## 6. B7 `ModData` — DECIDED: declared exclusion, refused on evidence (2026-07-29)

The exclusion `30b6155` added as a precaution is now a **decision**. T4's original verdict
("mechanically expressible, behaviourally risky — last batch, change-driven cadence, per-mod opt-in")
was too generous on the record side: reading TFTV's real implementation shows the game's mod-save API is
not a read/write pair at all, but two SAVE-TIME hooks with side effects.

**Deciding evidence, both ends verified in `refs/TFTV-src`:**

1. **RECORD mutates the very state we replicate.** `ModManager.RecordGeoscapeInstanceData`
   (`ModManager.cs:730-744`) calls every mod's `RecordGeoscapeInstanceData()` once per invocation.
   TFTV's (`TFTVGeoscape.cs:224-240`) opens with `TFTVRevenant.RecordUpkeep.UpdateRevenantTimer
   (Controller)`, which writes `daysRevenantLastSeen`, flips `revenantSpawned` and **increments the
   geoscape event variable `"Revenant_Spotted"`** (`TFTVRevenant.cs:1786-1792`) — a variable the rail
   already mirrors under the `ES` root. It also clears behemoth routing state
   (`JustInCaseBehemothScenicRouteAndTargetClear`) and reconciles operative affinities. At walk cadence
   (~2/s) that is a save hook fired ~7200x/hour, each one editing campaign state and feeding its own
   churn back into the diff. This is not a budget question — it is a correctness one, and it kills the
   record side outright. (The original RCA framing, "logs a line + allocates JSON", understated it.)
2. **APPLY cannot be made repeat-safe from our side.** `ProcessGeoscapeInstanceData`
   (`ModManager.cs:746-767`) loops ALL mods and hands each a freshly deserialized object. TFTV's
   override (`TFTVGeoscape.cs:317-345`) begins with
   `TFTVCommonMethods.ClearInternalVariablesOnStateChangeAndLoad()` and then overwrites ~60 static
   fields wholesale — i.e. resetting another mod's live runtime to a snapshot, the exact trap that made
   the wholesale `ProcessInstanceData` unacceptable (`ARCHITECTURE.md:169-174`). The idempotence would
   have to live in THEIR code, so we cannot demonstrate it, and law "repeat-apply safety must be
   demonstrated, not asserted" therefore refuses it. TFTV additionally patches that method and shows a
   modal `MessageBox` when a mod's entry is missing (`TFTVCriticalStuff.cs:110-142`), so every extra
   invocation of ours can put a dialog on the player's screen.
3. **A per-mod opt-in does not rescue it.** Opting a mod IN still calls both of its hooks with the
   behaviour above; opting mods OUT by name is the per-mod hand-sync the PRIME DIRECTIVE forbids.

**Cost of keeping the exclusion, stated plainly:** a mod's own geoscape state does not mirror at all.
Under TFTV that means base-defence / containment-breach schedules, infestation ownership and haven
population, delirium and revenant timers, new-game option flags and portrait data all stay host-local —
a client sees vanilla-shaped state where TFTV drives gameplay. That is a real hole, and it is the price
of not calling another mod's save hooks 7200 times an hour.

**The exit is a different mechanism, and it already exists.** `IdentityResolver.RegisterModRoot`
(`src/Rail/IdentityResolver.cs:166-175`) lets a mod register its own state object as an `"M#<name>"`
root, which then rides the ordinary walk / diff / apply with per-field granularity and no save hooks —
the same road our own `ScrapCartState` takes. Mirroring TFTV means TFTV (or a shim mod) publishing a
root, not us laundering their save blob.

**Law**: no new arm — `L28`'s declared-exclusion sweep (`RootOwnershipLaw`, the
`{ TacUnits, ModData, ContextHelpData, GeoscapeLog }` loop) already asserts through
`RailMeta.OptOutReason` that `GeoLevelController.ModData` is an EXPLICIT opt-out with its reason string
present, and goes RED (`declared-exclusion-absent` / `undeclared-exclusion`) if the row is deleted or
degrades into an accidental `bridge-unresolved`. A second law over the same fact would be decoration;
this batch updated the reason STRING with the evidence above instead.
