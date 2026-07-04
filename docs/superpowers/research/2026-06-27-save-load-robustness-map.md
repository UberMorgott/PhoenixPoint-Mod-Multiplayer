# Multiplayer save/load robustness map — 2026-06-27

GOAL: load saves at ANY moment with NO errors, both with Multiplayer active and without it
(saves must stay valid whether or not the mod is present). Read-only research; TFTV (workshop
2872311902) ALWAYS installed → must stay TFTV-compatible. All file:line below are real-source
(`Multiplayer\src\...`, `refs\TFTV-src\...`) unless marked decompile.

## 1. Save-data compat ("без мода") — does Multiplayer poison the save graph?

- **VERDICT: NO. A save written WITH Multiplayer is a plain PP/TFTV save and loads cleanly
  WITHOUT the mod.** Multiplayer serializes NOTHING into the savegame graph.
- The ONLY savegame serialization is read-only on the host: `SaveTransferCoordinator
  .HostSerializeAndSendCrt` → `game.SaveManager.Serializer.ReadSavegameBinary(metaData, result)`
  (`SaveTransferCoordinator.cs:403`) — reads the host's EXISTING vanilla save to a `byte[]` for
  network transfer. No write/inject. Client side just `ReadMetaData`/`ReadLevelParamsAsync` off
  the received blob in memory (`SaveTransferCoordinator.cs:635-679`).
- **No save-WRITE hook**: no Harmony patch on `WriteSavegame*`/`SaveGame`/serializer write; no
  `[HarmonyPatch]` touches savegame content. (grep WriteSavegame/OnSaveGame → only the
  transfer/intercept/main/UI files, none patch the write path.)
- **No persisted custom state**: no `ISerializable`/`GetObjectData`, no custom `GameComponentDef`
  registered into the persisted graph, no custom `GameTagDef`/def registration. `DefReflection`
  only RESOLVES defs by `BaseDef.Guid` via `DefRepository.GetDef` (`DefReflection.cs:14,69`) — never
  mutates/adds a persisted def. Tactical `DefRepository.Instantiate(BaseDef,…)`
  (`TacticalActorStateSync.cs:763`) builds TRANSIENT runtime Status objects, not saved defs.
- **All co-op runtime state is transient**: network messages (`MessageSerializer.*`), in-memory
  reassembly buffers, and live engine fields — none reach disk. `SuppressEvents` is set on the
  client's live `GeoscapeEventSystem` at runtime (`EventSuppressClientPatches.cs:67`) but that is
  the GAME's own persisted-snapshot field; on the HOST it stays vanilla and the host is the one
  who saves, so a host-written save carries the vanilla value. (Caveat: if a CLIENT ever wrote a
  save mid-session its live `SuppressEvents=true` could bleed into that file — but clients are
  BLOCKED from loading and there is no client save path in co-op; host is authoritative writer.)

## 2. Load-gate & "anytime" — what Multiplayer does on LOAD

Gate stack (commit 8cf7286 + UI intercept):
- `LoadGameConvergenceGatePatch` — Prefix on `PhoenixSaveManager.LoadGame(PPSavegameMetaData)`
  (`SaveLoadInterceptPatch.cs:356-443`), the convergence of CONTINUE / Quickload / pause-LOAD.
- `SaveLoadInterceptPatch.OnLoadGamePressed_Prefix` — UI Load-screen pick intercept
  (`SaveLoadInterceptPatch.cs:165-238`).
- `InGameLoadArmPatch` (pause-menu LOAD arm, `:302`) + `FinishLevelBarrierPatch`
  (`SaveLoadPatches.cs`) which holds vanilla `FinishLevel(LoadLevelGameResult)` until co-op BEGIN.
- Pure predicates in `SessionLifecycle.cs` (unit-tested).

Behavior matrix (who can load WHEN):
- **Non-host / NO active session (incl. mod installed but not in co-op): UNTOUCHED** — gate returns
  `true` → vanilla single-player load (`SaveLoadInterceptPatch.cs:441`). Every suppress/curtain
  patch also no-ops when `engine==null || !IsActive` (`ClientGeoSimSuppressPatch.cs:57`,
  `CurtainShowPatch.cs:44`, `EventSuppressClientPatches.cs:65`). → normal SP load is fully clean.
- **HOST + active lobby + NOT started** → pick captured as lobby base save, NO load
  (`ShouldCaptureAsLobbyPick`, `SessionLifecycle.cs:75`; `SaveLoadInterceptPatch.cs:181`,`:375`).
- **HOST + session STARTED + ≥1 client** → CONTINUE/Quickload/pause-LOAD rerouted to the F2
  host-authoritative in-session reload `HostStartSessionInGame` (re-runs chunked transfer + barrier
  so every client reloads) when `HostLoadGuard` open (no transfer in flight); else BLOCKED
  (`SaveLoadInterceptPatch.cs:383-419`; guard `SessionLifecycle.cs:52`).
- **HOST + session STARTED + 0 clients** → vanilla solo load ALLOWED (no peers to desync)
  (`HostInSessionHasNoClients`, `SessionLifecycle.cs:111`; `:390`).
- **CLIENT in active session → ALL loads BLOCKED** ("Only the host can load", messagebox)
  (`ShouldBlockClientLoad`, `SessionLifecycle.cs:131`; `:427`).
- **"Can you load anytime?"** HOST: YES (lobby pick / mid-session reload / clientless solo).
  CLIENT: NO by design (host-authoritative). NON-co-op player: YES, unchanged.
- Client never solo-loads → it ALWAYS gets pulled in only via the host transfer, which drives entry
  through the in-memory `EnterLevel → FinishLevel` (`SaveTransferCoordinator.cs:868-894`), i.e. a
  FULL client geoscape teardown+rebuild on every co-op apply (this is the TFTV-NRE trigger, §3/§4).

## 3. Load-time exceptions (real vs benign)

- **TFTV NRE (REAL — the target bug).** During the client's forced geoscape teardown/rebuild on
  co-op save-apply, two TFTV UI postfixes run on null backing data:
  - `TFTV.TFTVCapturePandoransGeoscape.RefreshFoodAndMutagenProductionTooltupUI()`
    (`TFTVCapturePandoransGeoscape.cs:117`) — `GameUtl.CurrentLevel()` null at `:121` → NRE; its
    `catch{ TFTVLogger.Error(e); throw; }` (`:199-203`) RE-THROWS → escapes to its non-catching
    Harmony callers (e.g. `TFTVResearch.cs:440`) → error popup (the "~14x" at teardown). NB the
    `UIAnimatedResourceController.RefreshResourceText` postfix caller self-catches (`:219`) so that
    one path is silent — the visible pops come from the rethrowing callers + `TFTVLogger.Error`.
  - `TFTV.TFTVUI.Geoscape.TopInfoBar+TFTV_ODI_meter_patch.Postfix` (on `UIModuleInfoBar
    .UpdatePopulation`, `TopInforBar.cs:128-353`) — `____context.Level`/`populationBar.Find(...)`
    null → NRE; it self-catches (`:349-352`, NO rethrow) so the popup here is from `TFTVLogger
    .Error(e)` LOGGING the NRE, not an unhandled throw. Benign to game state (UI-only) but noisy.
  - No Multiplayer frame in the stack; Multiplayer is only the TRIGGER (forces the rebuild) and did
    not unload.
- **Multiplayer-side load exceptions: all BENIGN (caught + logged, fail-safe).** Every load-path
  member is wrapped: `FinishLevelBarrierPatch.Prefix` try/catch→true (`SaveLoadPatches.cs:56`),
  the whole intercept gate try/catch (`SaveLoadInterceptPatch.cs:202,436`), `ApplyPrepareLoadGame
  State` reflection try/catch (`SaveTransferCoordinator.cs:1141`), `PrepareEntryFromBlobCrt`
  null-guards meta (`:649`), `ClientLoadCrt`/`OnSaveDone` ack `ok=false` on any failure
  (`:604,614`), `PerformDeferredLift` once-guarded + try/catch (`:798-822`). Suppress/curtain
  patches best-effort try/catch, fail-open.
- **Real (non-exception) risk to watch, not a crash**: `ApplyPrepareLoadGameState` sets
  `_enabledDlc/_currentGameId/_currentDifficulty/LatestLoad` by REFLECTION
  (`SaveTransferCoordinator.cs:1118-1145`) — if a PP/TFTV update renames those private fields it
  logs an error and the client geoscape comes up empty (no throw). Currently correct.
- **Transfer integrity**: incomplete/crc-mismatch → `SendLoaded(false)`, client simply does not
  enter (`OnSaveDone:583-599`); reveal deadlock has host-forced + per-peer self-reveal fallbacks
  (`SaveTransferCoordinator.cs:1008-1021`). Not load-time crashes.

## 4. TFTV NRE fix — RECOMMENDATION

**Recommend (a): a NARROW TFTV-compat PREFIX guard on OUR side. Prefer PREFIX-skip over a
finalizer.**
- Targets (resolve reflectively; TFTV-absent → `Prepare()` false → Harmony skips, zero impact —
  mirror the existing `ClientTftvAircraftFreezePatch` Prepare/TargetMethod pattern):
  - `AccessTools.TypeByName("TFTV.TFTVCapturePandoransGeoscape")` →
    `AccessTools.Method(t,"RefreshFoodAndMutagenProductionTooltupUI", Type.EmptyTypes)`.
  - `AccessTools.TypeByName("TFTV.TFTVUI.Geoscape.TopInfoBar+TFTV_ODI_meter_patch")` →
    `AccessTools.Method(t,"Postfix")`. (Patching a TFTV patch-method is fragile under Harmony IL
    caching; the ROBUST equivalent is a guard on the native `UIModuleInfoBar.UpdatePopulation`
    itself — pick whichever binds reliably in-game; both skip the same body.)
- Gate: skip the body (`return false`) only when `engine.IsActiveSession && !engine.IsHost`
  (co-op client) AND backing data is NOT ready — `GameUtl.CurrentLevel()==null` (or its
  `GeoLevelController`/`View` null) for RefreshFood; `____context?.Level==null` for the ODI meter.
  Self-scoping: during NORMAL client play backing data is non-null → TFTV UI runs unchanged; only
  the teardown/rebuild window is skipped. Best-effort try/catch, fail-open (never throw into game).
- **Why PREFIX, not FINALIZER**: a finalizer that returns null only swallows the THROWN exception
  AFTER the body ran — but `TFTVLogger.Error(e)` already executed inside both methods (it is the
  popup source for the self-catching ODI path, which never rethrows). A prefix-skip prevents the
  body entirely → no NRE, no `TFTVLogger.Error`, no popup, for BOTH methods. A finalizer cannot fix
  the ODI case at all.
- **Why NOT (b) avoid-the-teardown**: the client apply IS a genuine full save load (build scene
  binding → `FinishLevel`, `SaveTransferCoordinator.cs:868-894`). On join the client has no/other
  geoscape, so a full rebuild is mandatory — there is no "rebuild in place" without re-architecting
  the proven chunked-transfer + barrier flow (high risk, breaks the working save-transfer). Reject.

## 5. "Anytime load" gaps (when arbitrary-moment load is unsafe in co-op)

- **Mid-TACTICAL host load = highest risk.** The gate does not distinguish tactical vs geoscape; a
  host load mid-tactical reroutes through `HostStartSessionInGame` and clients tear down tactical
  and reload. Full host→client TACTICAL state replication is still incomplete (memory:
  multiplayer-full-state-replication / the pending tactical full-state spine), so loading into/within
  a tactical co-op mission is the least-proven "anytime" case. Geoscape-anytime is safe now.
- **Cross-campaign / roster-mismatch host load**: no campaign-identity check — clients follow the
  host's chosen save unconditionally. The full blob carries the host campaign so clients mirror it;
  acceptable, but a host loading an unrelated save mid-session is jarring (no guard/confirm).
- **Load races**: client loads are BLOCKED, so no client-initiated race; host is sole loader; a 2nd
  host load while a transfer is in flight is blocked by the `transferActive` guard
  (`SessionLifecycle.cs:59`). Clientless-host vanilla solo load is allowed and safe.
- **Session survives the reload by design** (the point of co-op reload) — no teardown of the network
  session on load, which is correct; the only fallout is the §3/§4 TFTV UI NRE during the rebuild.

## MINIMAL FIX PLAN (ordered)

1. **Add the narrow TFTV-compat PREFIX guard (§4 option a).** New `Harmony` class(es) mirroring
   `ClientTftvAircraftFreezePatch` (reflective Prepare/TargetMethod; TFTV-absent safe), skipping the
   two TFTV UI bodies on a co-op client when backing data is null.
   - Risk: LOW (gated co-op-client + null-backing + TFTV-present; fail-open try/catch; UI-only).
   - Test: unit-test the pure gate predicate (mirror `ClientTftvAircraftFreezeGate`). In-game:
     client joins → save transfer → confirm NO TFTV NRE popup, client geoscape renders, and after
     reveal TFTV food/mutagen/ODI UI is intact during normal play.
   - **STANDALONE NOW** — independent of the unified-sync convergence.
2. **(Doc only) Record the load-gate matrix** (host-anytime / client-blocked / non-co-op-untouched)
   + the save-data "no poison" verdict in `docs\README.md` / COOP-SYNC-ROADMAP so it is not
   re-derived. Risk: none. **STANDALONE NOW.**
3. **(Optional hardening) Finalizer belt** on `RefreshFoodAndMutagenProductionTooltupUI` to swallow
   the rethrow path as defense-in-depth — only if step 1's prefix proves insufficient in-game (it
   should not, since prefix-skip pre-empts the body). Risk: low. **STANDALONE**, likely unneeded.
4. **Tactical mid-session "anytime load"** — defer behind the pending full-state TACTICAL spine
   convergence (geoscape-anytime is safe today; tactical reload needs the replication backbone).
   **NEEDS CONVERGENCE**, not standalone.

Net: the user's "load anytime, no errors, with or without mod" goal is met for geoscape by ONE
standalone change (step 1) — save-data is already non-poisoning (§1) and non-co-op load is already
untouched (§2). Tactical-mid-mission load is the only piece gated on the larger convergence.
