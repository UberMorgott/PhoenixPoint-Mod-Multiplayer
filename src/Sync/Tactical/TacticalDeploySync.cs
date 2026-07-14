using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Base.Core;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using UnityEngine;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// Increment-1 tactical DEPLOY-SYNC orchestrator (spec §8). One static facade that:
    ///   • HOST: on the tactical level reaching Playing (turn 0 ready), captures the full battle
    ///     (TacticalGameParams + TacLevelInstanceData via the native game <c>Serializer</c>), builds the
    ///     NetId actor table, and broadcasts <c>tac.deploy</c> to every client over the live action rail.
    ///   • CLIENT: receives <c>tac.deploy</c>, launches the SAME mission from host params, and — once its
    ///     tactical scene is built (Playing) — restores the snapshot (<c>ProcessInstanceData</c>), rebuilds
    ///     the NetId dict from the host actor table, and ARMS mirror mode (frozen pure mirror).
    ///
    /// All game types are reached by name via <see cref="AccessTools"/> (the mod has no compile-time game
    /// binding outside this layer's reflection), mirroring <c>GeoRuntime</c> / <c>TacticalPatches</c>.
    /// The pure, unit-tested cores are <see cref="TacticalActorRegistry"/> (NetId) and
    /// <see cref="TacticalDeployCodec"/> (wire). This file is the engine glue — in-game verified.
    /// </summary>
    public static class TacticalDeploySync
    {
        // ─── Mirror-mode state (read by the suppress patches) ──────────────
        // Armed on the CLIENT once it has hydrated a host-synced tactical mission; disarmed on exit.
        // The host is NEVER in mirror mode — it runs the authoritative sim.
        private static bool _mirrorArmed;
        public static bool MirrorArmed => _mirrorArmed;
        // A client hydrate is scheduled/in-flight (the hydrate DEFERS onto a coroutine, so _pendingClientDeploy
        // is not cleared until it runs). Dedups the two Playing seams that can both fire the same frame —
        // TacticalLevelStateChangedPatch (direct launch) + CurtainShowPatch (save-loaded entry). Reset on exit.
        private static bool _hydrateScheduled;

        // ─── Batch-1 feature flag: tactical mission ENTRY via mid-tactical save transfer ────
        // When ON, the host ships a byte-identical mid-tactical save at deploy-ready and the client BUILDS
        // its battle from those exact bytes (ClientLoadCrt → PrepareEntryFromBlobCrt → EnterLevel → hydrate)
        // instead of self-launching + reconciling. Default OFF in code (rollback safety); the dev-deployed
        // build flips it ON in MultiplayerMain.OnModEnabled so the in-game gate exercises the new path.
        public static bool UseSaveTransferEntry = false;

        // CLIENT: the host tac.deploy actor table, retained for the MISSION lifetime (not just the one-shot
        // hydrate) so late-bound actors can still receive their host deploy position. Null off-mission.
        private static List<TacticalActorRegistry.ActorRow> _missionActorTable;

        /// <summary>True only when this instance is a synced-session CLIENT inside a mirrored tactical
        /// mission — the gate every client-side suppress patch checks.</summary>
        public static bool IsClientMirroring
        {
            get
            {
                var e = NetworkEngine.Instance;
                return e != null && e.IsActive && !e.IsHost && _mirrorArmed;
            }
        }

        // Host: guard so a single mission broadcasts tac.deploy exactly once (OnLevelStateChanged →
        // Playing can fire/restart; the reliable transport also double-sends). Keyed by mission site id.
        private static int _lastBroadcastSiteId = int.MinValue;
        // Host: monotonic deploy-generation counter, stamped into every chunk so the client groups a fragment
        // set correctly and discards a stale partial set if a newer deploy/resync of the same site starts.
        private static int _deployGeneration;
        // Client: order-independent, idempotent chunk reassembler for the over-cap chunked deploy path.
        private static ChunkReassembler _chunkReassembler = new ChunkReassembler();
        // Captured at LaunchTacticalGame time (still on the geoscape, GeoMission.Site reachable). The
        // tactical level itself has no cross-instance-stable site id (TacMissionData.MissionId is a
        // per-launch GUID), so we must stamp it here. -1 until a launch is observed.
        private static int _launchingSiteId = -1;
        // Client: the pending deploy we received but haven't hydrated yet (waiting for scene Playing).
        private static TacticalDeployCodec.DeployPayload _pendingClientDeploy;
        private static int _hydratedSiteId = int.MinValue;

        // The live NetId registry for the current mission (both sides). Rebuilt per deploy.
        public static TacticalActorRegistry Registry { get; private set; } = new TacticalActorRegistry();

        // ─── LIVE outcome rail state (Inc 2/4: move + end-turn), shared by the move/turn sync modules ──
        // Cached live TacticalLevelController for this mission (stamped at host capture / client hydrate),
        // so the network-driven move/turn appliers can reach Factions/_currentFactionIndex without a fresh
        // scene scan. Cleared on mission exit.
        public static object LiveTlc { get; private set; }
        // TS1 gate: true once the host has captured + broadcast the turn-0 deploy snapshot for the current
        // mission, so the ActorEnteredPlay chokepoint only mirrors POST-deploy actors (deploy-time actors ride
        // the 0x80 snapshot). Reset with _lastBroadcastSiteId on mission exit.
        public static bool HostHasBroadcastDeploy => _lastBroadcastSiteId != int.MinValue;
        // Host: monotonic per-surface seq source for tac.move / tac.turn. Client: last-writer-wins guard.
        public static TacticalLiveSeq LiveSeq { get; private set; } = new TacticalLiveSeq();
        // Host: drops a double-sent client intent (move/end-turn) so it never applies twice.
        public static TacticalIntentDedup IntentDedup { get; private set; } = new TacticalIntentDedup();

        /// <summary>Resolve a live <c>TacticalActorBase</c> from a NetId via the registry (returns the
        /// adapter's wrapped actor object, or null). Used by both the host intent-apply and the client
        /// outcome-mirror.</summary>
        public static object ResolveLiveActor(int netId)
        {
            if (Registry != null && Registry.TryGet(netId, out var actorRef) && actorRef is TacticalActorAdapter ad)
                return ad.Actor;
            // LATE-SPAWN RECOVERY (root cause of "wrong start / no actor for netId N"): the client deploy match
            // (MatchAndRegister) is a ONE-SHOT snapshot taken the instant HasAnyTurnStarted flips true, but actor
            // spawning is still in flight then (observed match count swings 118/141 ↔ 139/143 across runs). Player
            // soldiers (and other actors) that spawn AFTER the snapshot are never bound, so every live rail that
            // resolves through here (move/equip/combat/…) would miss them. A soldier/vehicle netId (< MintBase) IS
            // its GeoUnitId (spec §4), so we can rebind it GeoUnitId-EXACT against the CURRENT live actor set with
            // no extra wire data, REUSING the existing ClientTryLazyRebind matcher. Client-only (the host registry
            // is authoritative + kept complete via HostEnsureLiveActorsRegistered); minted Pandoran netIds
            // (>= MintBase) need a position, so they stay on the actorstate-flush lazy path (which passes one).
            if (IsClientMirroring && netId > 0 && netId < TacticalActorRegistry.MintBase)
            {
                object rebound = ClientTryLazyRebind(netId, false, 0f, 0f, 0f);
                if (rebound != null) return rebound;
            }
            return null;
        }

        /// <summary>The NetId for a live actor. Soldiers/vehicles use NetId == GeoUnitId (the shared-save id),
        /// so a client sending a move-intent for its own soldier needs no reverse map: read GeoUnitId. For a
        /// minted (Pandoran) NetId we fall back to a registry scan by reference. Returns -1 if unknown.</summary>
        public static int NetIdForLiveActor(object actor)
        {
            if (actor == null) return -1;
            try
            {
                // Fast path: GeoUnitId != 0 ⇒ NetId == GeoUnitId (registry scheme, spec §4).
                var adapter = new TacticalActorAdapter(actor);
                int geoId = adapter.GeoUnitId;
                if (geoId != 0) return geoId;
                // Minted-id fallback: scan the registry for the entry whose adapter wraps this same actor.
                if (Registry != null)
                    foreach (var kv in Registry.Entries)
                        if (kv.Value is TacticalActorAdapter a && ReferenceEquals(a.Actor, actor))
                            return kv.Key;
            }
            catch { }
            return -1;
        }

        /// <summary>HOST coverage (actorstate flush): ground every LIVE map actor that is missing from the
        /// registry into it via the host id-minting path (<see cref="TacticalActorRegistry.AssignHost"/> — the
        /// host stays the SOLE netId minter), so a mid-mission spawn or a deploy-drift actor that never bound
        /// still rides the per-actor state flush (its AP/WP/status broadcast). Called at the TOP of the host
        /// flush tick, BEFORE walking <c>Registry.Entries</c> (mutating the registry mid-enumeration would throw).
        /// No-op for already-registered actors. Returns the count newly registered.</summary>
        public static int HostEnsureLiveActorsRegistered()
        {
            var reg = Registry;
            object tlc = LiveTlc;
            if (reg == null || tlc == null) return 0;
            int added = 0;
            try
            {
                // Already-registered underlying actors. The adapter has NO value-equality, so a fresh adapter
                // never compares equal to the registry's stored one — key the membership set on the WRAPPED
                // actor object's reference identity instead.
                var known = new HashSet<object>();
                foreach (var kv in reg.Entries)
                    if (kv.Value is TacticalActorAdapter a && a.Actor != null) known.Add(a.Actor);

                foreach (var aref in EnumerateActorRefs(tlc))
                {
                    var adapter = aref as TacticalActorAdapter;
                    object actor = adapter?.Actor;
                    if (actor == null || known.Contains(actor)) continue;
                    int netId = reg.AssignHost(adapter);   // GeoUnitId for soldiers, minted id for Pandorans
                    known.Add(actor);
                    added++;
                    Debug.Log("[Multiplayer][tac] lazy-registered netId=" + netId + " geoUnitId=" + adapter.GeoUnitId);
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostEnsureLiveActorsRegistered failed: " + ex); }
            return added;
        }

        /// <summary>CLIENT coverage (actorstate apply): a host netId whose actor is NOT in the registry —
        /// deploy position drift left it unbound, or it is a mid-mission spawn. Try a LAZY re-bind against the
        /// current live map using the SAME matcher as deploy (<see cref="TacticalActorRegistry.TryLazyRebind"/>:
        /// GeoUnitId-EXACT preferred; position fallback within <see cref="TacticalActorRegistry.PosEpsilon"/> for
        /// a minted Pandoran). Only UNBOUND live actors are offered as candidates so the position fallback can
        /// never steal an already-bound actor. The client NEVER mints — it binds the host-authored netId. Returns
        /// the now-bound live actor object, or null if nothing matched (actor truly absent on this client →
        /// caller drops as before).</summary>
        public static object ClientTryLazyRebind(int netId, bool hasPos, float px, float py, float pz)
        {
            var reg = Registry;
            object tlc = LiveTlc;
            if (reg == null || tlc == null) return null;
            try
            {
                var candidates = UnboundLiveCandidates(reg, tlc);
                if (candidates.Count == 0) return null;

                if (!reg.TryLazyRebind(netId, hasPos, new ActorPos(px, py, pz), candidates)) return null;
                // Direct registry read (NOT ResolveLiveActor): ResolveLiveActor now falls back INTO this method on
                // a miss, so a failed rebind here would recurse infinitely. TryLazyRebind just bound netId, so a
                // plain TryGet resolves it.
                object bound = reg.TryGet(netId, out var boundRef) && boundRef is TacticalActorAdapter boundAd ? boundAd.Actor : null;
                if (bound != null)
                {
                    Debug.Log("[Multiplayer][tac] lazy-rebound netId=" + netId +
                              " via " + (netId < TacticalActorRegistry.MintBase ? "GeoUnitId" : "pos"));
                    // Late-bind PLACEMENT: the one-shot hydrate placement already ran, so this actor still
                    // stands at its client-native spawn cell. Apply the retained host deploy position now.
                    // If the triggering message carries a fresher pos (actorstate delta), the caller applies
                    // it right after and simply overwrites this — deploy pos is only stale if the actor
                    // moved, and a move is exactly what ships a fresher pos.
                    ApplyStoredDeployPos(netId, bound);
                }
                return bound;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ClientTryLazyRebind failed: " + ex); return null; }
        }

        // Client re-entrancy: true only while OUR own ClientLaunchMission is driving the native
        // LaunchTacticalGame. The LaunchTacticalGame prefix uses this to ALLOW the deploy-driven launch
        // while GATING (blocking) any spontaneous client-initiated launch (spec §8: client never self-launches).
        private static bool _clientLaunchInProgress;
        public static bool ClientLaunchInProgress => _clientLaunchInProgress;

        // Client launch-stall watchdog (RCA 2026-07-11, 3-instance test): a deploy-driven native launch
        // whose scene never reaches Playing leaves ONLY a silent "no live TLC" drop-flood — one loud line
        // instead. Armed after a successful ClientLaunchMission; disarmed by ClientOnLevelReady (Playing
        // arrived) / OnMissionExit; fires ONCE. Ticked from TacticalLoadPhaseSync.Tick (per-frame pump).
        private const float LaunchStallSeconds = 60f;
        private static float _launchStallDeadline;   // 0 = disarmed (Time.realtimeSinceStartup based)
        private static int _launchStallSiteId;

        // Entry-via-save stall watchdog (batch-1): a client that suppressed self-launch (UseSaveTransferEntry)
        // and is waiting on the host save-transfer hangs FOREVER if the host's tactical save write aborted
        // before SendBlob/OpenBarrier (no chunks + no barrier → the reveal/kick fallbacks never arm). Armed
        // when the deploy is stashed under the flag; self-disarms once the transfer arrives / the level builds
        // / the deploy hydrates; on timeout falls back to the legacy ClientLaunchMission. Ticked from
        // TacticalLoadPhaseSync.Tick alongside the launch-stall watchdog.
        private const float EntryTransferStallSeconds = 60f;
        private static float _entryStallDeadline;   // 0 = disarmed (Time.realtimeSinceStartup based)

        // ─── Reflection cache ──────────────────────────────────────────────
        private static Type _tlcType;          // TacticalLevelController
        private static Type _tacActorBaseType; // TacticalActorBase
        private static Type _tacGameParamsType;// TacticalGameParams
        private static Type _tacLevelInstType; // TacLevelInstanceData
        private static Type _serializerType;   // Base.Serialization.General.Serializer
        private static Type _byRefType;        // Base.Utils.ByRef<>
        private static Type _timeSliceType;    // Base.Utils.TimeSlice
        private static bool _reflectionReady;
        // The engine's ONE configured Serializer (Context = SerializationComponent + InitCustomTypes +
        // ValidateSerializedObject). Resolved lazily, cached for the session.
        private static MethodInfo _gameComponentSerComp;  // GameUtl.GameComponent<SerializationComponent>()
        private static PropertyInfo _serCompSerializerProp;// SerializationComponent.Serializer getter

        private static void EnsureReflection()
        {
            if (_reflectionReady) return;
            _tlcType = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.TacticalLevelController");
            _tacActorBaseType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.TacticalActorBase");
            _tacGameParamsType = AccessTools.TypeByName("PhoenixPoint.Common.Levels.Params.TacticalGameParams");
            _tacLevelInstType = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.TacLevelInstanceData");
            _serializerType = AccessTools.TypeByName("Base.Serialization.General.Serializer");
            _byRefType = AccessTools.TypeByName("Base.Utils.ByRef`1");
            _timeSliceType = AccessTools.TypeByName("Base.Utils.TimeSlice");
            _reflectionReady = _tlcType != null && _serializerType != null;
        }

        /// <summary>
        /// Called from the <c>GeoLevelController.LaunchTacticalGame(GeoMission, ...)</c> prefix on BOTH sides,
        /// while still on the geoscape (so <c>GeoMission.Site.SiteId</c> is reachable). Stamps the launching
        /// mission's stable cross-instance site id, used later by the level-ready capture/hydrate.
        /// </summary>
        public static void OnTacticalLaunch(object geoMission)
        {
            try
            {
                object site = GetProp(geoMission, "Site");
                var f = site != null ? AccessTools.Field(site.GetType(), "SiteId") : null;
                _launchingSiteId = f != null ? (int)f.GetValue(site) : -1;
                Debug.Log("[Multiplayer][tac] OnTacticalLaunch site=" + _launchingSiteId);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] OnTacticalLaunch failed: " + ex); _launchingSiteId = -1; }

            // Host: begin pinging the client loading INDICATOR (geoscape→tactical). Host-only + idempotent
            // inside HostBeginLoad; the client shows a curtain on receipt and never sends. Stops when the deploy
            // snapshot is broadcast (HostCaptureAndBroadcast → HostEndLoad).
            try { TacticalLoadPhaseSync.HostBeginLoad(); }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] load-phase begin failed: " + ex); }
        }

        // ─── HOST: capture + broadcast (called from DeployLaunchPatches level-ready postfix) ──

        // Host: guard so the deferred capture coroutine is started at most once per mission/site.
        private static int _captureScheduledSiteId = int.MinValue;

        /// <summary>
        /// HOST: the tactical level reached Playing. Do NOT capture synchronously — the Playing postfix runs
        /// BEFORE the scheduled <c>OnLevelStart</c> coroutine has finished initializing the level, so an
        /// immediate <c>RecordInstanceData</c> NREs on a half-built level (FactionVision null-faction actors,
        /// then <c>TacAchievementTracker._level</c>), aborting the deploy. Instead, DEFER: start a coroutine on
        /// the level's Timing that waits (via <see cref="TacticalDeployReadinessGate"/>) until the level is
        /// genuinely turn-0 ready (<c>HasAnyTurnStarted</c>), then capture + broadcast cleanly. Idempotent per
        /// mission. No-op off-host / off-session. (RCA 2026-06-18, 2-instance log decode.)
        /// </summary>
        public static void HostOnLevelReady(object tacticalLevelController)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (tacticalLevelController == null) return;
            EnsureReflection();
            if (!_reflectionReady) { Debug.LogError("[Multiplayer][tac] HostOnLevelReady: reflection not ready"); return; }

            try
            {
                int siteId = _launchingSiteId >= 0 ? _launchingSiteId : ResolveMissionSiteId(tacticalLevelController);
                if (siteId == _lastBroadcastSiteId) return;        // already broadcast this mission
                if (siteId == _captureScheduledSiteId) return;     // capture already scheduled for this mission
                _captureScheduledSiteId = siteId;

                // Defer the capture onto the level's Timing until the level is turn-0 ready (gate-driven).
                if (!StartDeferredCapture(tacticalLevelController))
                {
                    // Could not start a coroutine (no Timing) → fall back to an immediate best-effort capture.
                    Debug.LogError("[Multiplayer][tac] HostOnLevelReady: could not schedule deferred capture — capturing immediately (may be early)");
                    HostCaptureAndBroadcast(tacticalLevelController);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] HostOnLevelReady failed: " + ex);
            }
        }

        /// <summary>
        /// HOST: an F2 mid-session save-load landed in TACTICAL (rca-4 post-reload re-seed; coordination
        /// seam for rca-6 tactical save-load bind) — re-run the level-ready capture/broadcast seed against
        /// the LIVE tactical level, exactly like the normal launch postfix. The reload boundary already ran
        /// <see cref="OnMissionExit"/> (via <c>SyncEngine.ResetForReloadBoundary</c>), so the per-mission
        /// once-guards (<c>_lastBroadcastSiteId</c>/<c>_captureScheduledSiteId</c>) are clear and the seed
        /// fires fresh; the readiness gate then defers the capture until the loaded level is turn-0 ready.
        /// No-op when the current level is not tactical, off-host, or off-session (HostOnLevelReady guards).
        /// </summary>
        public static void HostReseedAfterLoad()
        {
            var tlc = LiveTacticalLevelController();
            if (tlc == null) return; // loaded save is not tactical — nothing to seed here
            Debug.Log("[Multiplayer][tac] HostReseedAfterLoad: reloaded save is TACTICAL → re-run level-ready seed");
            HostOnLevelReady(tlc);
        }

        /// <summary>
        /// HOST: start a game coroutine on the tactical level's Timing that polls
        /// <see cref="TacticalDeployReadinessGate"/> each frame and fires the capture once the level is ready
        /// (or a bounded fail-safe timeout elapses). Returns false if no Timing could be resolved.
        /// </summary>
        private static bool StartDeferredCapture(object tacticalLevelController)
        {
            object timing = GetProp(tacticalLevelController, "Timing");
            if (timing == null)
            {
                var timingType = AccessTools.TypeByName("Base.Core.Timing");
                var currentProp = timingType != null ? AccessTools.Property(timingType, "Current") : null;
                timing = currentProp?.GetValue(null, null);
            }
            if (timing == null) return false;

            var crt = DeferredCaptureCrt(tacticalLevelController);
            return InvokeTimingStart(timing, crt);
        }

        // ~600 frames ≈ 10 s @ 60 fps: a generous fail-safe budget — turn 0 normally starts within a frame
        // or two of Playing, so the ready path fires almost immediately; the budget only guards a pathological
        // mission that never flips HasAnyTurnStarted.
        private const int CaptureReadyMaxFrames = 600;

        /// <summary>HOST capture coroutine: each frame, ask the readiness gate whether to wait, capture, or
        /// fail-safe-capture. Reads <c>TacticalLevelController.HasAnyTurnStarted</c> (set true at turn-0 entry,
        /// after OnLevelStart fully ran ⇒ level fully initialized). MUST return <c>IEnumerator&lt;NextUpdate&gt;</c>
        /// (not a bare <c>IEnumerator</c>) so the compiler-emitted state machine implements the generic
        /// interface the native <c>Timing.Start(IEnumerator&lt;NextUpdate&gt;, …)</c> overload binds to —
        /// a non-generic iterator throws an ArgumentException at the reflective invoke.</summary>
        private static IEnumerator<NextUpdate> DeferredCaptureCrt(object tacticalLevelController)
        {
            int frames = 0;
            while (true)
            {
                bool ready = false;
                try { ready = ToBool(GetProp(tacticalLevelController, "HasAnyTurnStarted")); }
                catch (Exception ex) { Debug.LogError("[Multiplayer][tac] DeferredCaptureCrt: read HasAnyTurnStarted failed: " + ex); }

                var decision = TacticalDeployReadinessGate.Decide(ready, frames, CaptureReadyMaxFrames);
                if (decision == TacticalDeployReadinessGate.Decision.CaptureReady ||
                    decision == TacticalDeployReadinessGate.Decision.CaptureTimeout)
                {
                    if (decision == TacticalDeployReadinessGate.Decision.CaptureTimeout)
                        Debug.LogError("[Multiplayer][tac] DeferredCaptureCrt: readiness gate timed out after " +
                                       frames + " frames — capturing anyway (fail-safe)");
                    else
                        Debug.Log("[Multiplayer][tac] DeferredCaptureCrt: level ready after " + frames +
                                  " frame(s) → capturing deploy");
                    HostCaptureAndBroadcast(tacticalLevelController);
                    yield break;
                }

                frames++;
                yield return NextUpdate.NextFrame;
            }
        }

        /// <summary>Invoke the simplest <c>Timing.Start(IEnumerator&lt;NextUpdate&gt;, …optional)</c> overload
        /// (mirrors TacticalTurnSync.InvokeStart): first param is the coroutine, all trailing params optional.</summary>
        private static bool InvokeTimingStart(object timing, IEnumerator crt)
        {
            try
            {
                MethodInfo best = null;
                foreach (var m in timing.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != "Start") continue;
                    var pars = m.GetParameters();
                    if (pars.Length < 1) continue;
                    if (!typeof(IEnumerator).IsAssignableFrom(pars[0].ParameterType)) continue;
                    bool restOptional = true;
                    for (int i = 1; i < pars.Length; i++) if (!pars[i].IsOptional) { restOptional = false; break; }
                    if (!restOptional) continue;
                    if (best == null || pars.Length < best.GetParameters().Length) best = m;
                }
                if (best == null) { Debug.LogError("[Multiplayer][tac] InvokeTimingStart: no Start overload found"); return false; }
                var bp = best.GetParameters();
                var args = new object[bp.Length];
                args[0] = crt;
                for (int i = 1; i < bp.Length; i++) args[i] = Type.Missing;
                best.Invoke(timing, args);
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] InvokeTimingStart failed: " + ex); return false; }
        }

        /// <summary>
        /// HOST: capture the deploy snapshot from a fully-initialized tactical level and broadcast it. Idempotent
        /// per mission (only the first capture for a given site broadcasts). Called from the deferred
        /// readiness coroutine once the level is turn-0 ready.
        /// </summary>
        private static void HostCaptureAndBroadcast(object tacticalLevelController)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (tacticalLevelController == null) return;
            EnsureReflection();
            if (!_reflectionReady) { Debug.LogError("[Multiplayer][tac] HostCaptureAndBroadcast: reflection not ready"); return; }

            try
            {
                // Site id was stamped on the geoscape at LaunchTacticalGame (the tactical level has no
                // cross-instance-stable id). Fall back to a geoscape scan only if the stamp is missing.
                int siteId = _launchingSiteId >= 0 ? _launchingSiteId : ResolveMissionSiteId(tacticalLevelController);
                if (siteId == _lastBroadcastSiteId) return;   // already broadcast this mission

                // 1) The two native [SerializeType] graphs.
                object gameParams = GetProp(tacticalLevelController, "TacticalGameParams");
                object snapshot = Invoke(tacticalLevelController, "RecordInstanceData"); // public TacLevelInstanceData
                if (gameParams == null || snapshot == null)
                {
                    Debug.LogError("[Multiplayer][tac] HostCaptureAndBroadcast: null gameParams/snapshot — skipping deploy");
                    return;
                }

                byte[] gpBytes = SerializeGraph(new[] { gameParams });
                byte[] snapBytes = SerializeGraph(new[] { snapshot });
                if (gpBytes == null || snapBytes == null)
                {
                    Debug.LogError("[Multiplayer][tac] HostCaptureAndBroadcast: native serialize failed — skipping deploy");
                    return;
                }

                // 2) NetId actor table over the live actor set (deploy order).
                Registry = new TacticalActorRegistry();
                var actors = EnumerateActorRefs(tacticalLevelController);
                var table = Registry.BuildActorTable(actors);

                // 3) Frame + broadcast over the live action rail (host→all). A new generation per broadcast
                //    lets the client group/replace a chunked fragment set unambiguously.
                var payload = TacticalDeployCodec.Encode(siteId, gpBytes, snapBytes, table);
                int generation = ++_deployGeneration;
                BroadcastDeploy(siteId, generation, payload);

                _lastBroadcastSiteId = siteId;
                TacticalLoadPhaseSync.HostEndLoad();     // load-phase indicator: deploy sent → stop the host ping
                LiveTlc = tacticalLevelController;       // live-rail handle for tac.move/tac.turn/tac.vision
                // Do NOT recreate LiveSeq here: the level is already turn-0-ready and the pre-deploy tac.turn
                // (seq=1) has been emitted on the live stream. Recreating it would rewind _hostNext, so the
                // next turn re-emits seq=1 and the client's strict seq>last guard drops it ("turn doesn't end").
                // The stream is created once per mission (ctor + OnMissionExit reset) and must survive the
                // capture. (RCA 2026-06-20, 2-instance log decode.)
                LiveSeq.BeginDeployCaptureMission();     // capture-time seq hook (no rewind)
                IntentDedup = new TacticalIntentDedup();
                // Inc Vision: the host vision push is driven by an always-registered Harmony postfix on
                // TacticalLevelController.FactionKnowledgeChanged (VisionBroadcastPatch) — no event subscription
                // to wire here. Reset the per-mission chattiness guard so the first push always goes out, then push
                // an initial snapshot so the client is seeded with vision from turn 0 (before any change fires).
                TacticalVisionSync.HostResetBroadcastGuard();
                TacticalVisionSync.HostBroadcastVision();
                // Inc T1: start the generic per-actor STATE-DELTA flush heartbeat on the level Timing (AP/WP +
                // statuses → all peers). Self-terminates at mission exit (it watches LiveTlc). Additive — runs
                // ALONGSIDE the existing per-action surfaces as a redundant convergence layer.
                TacticalActorStateSync.HostStartFlush(tacticalLevelController);
                // 0x99: seed the FULL objective state set with the actor seed (turn-0 AND the reload-into-
                // tactical path, rca-6 — a reloaded save carries mid-mission objective states the client's
                // freshly built list must be stamped with).
                TacticalObjectiveSync.HostSeedAfterDeploy(tacticalLevelController);
                Debug.Log("[Multiplayer][tac] HOST broadcast tac.deploy site=" + siteId +
                          " gpBytes=" + gpBytes.Length + " snapBytes=" + snapBytes.Length +
                          " actors=" + table.Count);
                DumpActorTable("HOST", table);

                // [Batch-1 entry-via-save] After the tac.deploy broadcast, hand every client a byte-identical
                // mid-tactical save so it BUILDS its tactical level from the host's exact state (positions/
                // loot/objectives/turn) instead of self-launching + reconciling. Flag-gated (default OFF);
                // SaveTransferCoordinator self-gates the full precondition set (host/tactical/no-transfer) and
                // owns the write + ship. Additive: the tac.deploy broadcast above is untouched (still drives
                // the live NetId registry + move/vision/turn rails after the save-built level hydrates).
                if (UseSaveTransferEntry)
                {
                    try
                    {
                        var st = NetworkEngine.Instance?.SaveTransfer;
                        bool started = st != null && st.HostBeginTacticalEntryTransfer();
                        Debug.Log("[Multiplayer][tac] entry-via-save: HostBeginTacticalEntryTransfer started=" + started);
                        // A never-started transfer is the same wedge as a failed write: the reveal-hold armed at
                        // LAUNCH would park the host curtain forever and clients would wait on a save that never
                        // comes (live 2026-07-13) → release the hold + notify clients now.
                        if (!started) st?.AbortTacticalEntryTransfer("transfer failed to start");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError("[Multiplayer][tac] entry-via-save start failed: " + ex);
                        try { NetworkEngine.Instance?.SaveTransfer?.AbortTacticalEntryTransfer("start exception: " + ex.Message); }
                        catch { /* abort is best-effort — the log line above already surfaced the failure */ }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] HostCaptureAndBroadcast failed: " + ex);
            }
        }

        // ─── CLIENT: receive tac.deploy ────────────────────────────────────

        /// <summary>
        /// CLIENT inbound: a <c>tac.deploy</c> arrived. Decode + stash it, then kick the local mission
        /// launch from the host's mission/site. The actual snapshot restore + mirror-arm happens later, on
        /// the client's own Playing transition (<see cref="ClientOnLevelReady"/>), once the scene is built.
        /// </summary>
        public static void OnDeployReceived(byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return;   // clients only
            if (!TacticalDeployCodec.TryDecode(payload, out var p))
            {
                Debug.LogError("[Multiplayer][tac] OnDeployReceived: decode failed (" + (payload?.Length ?? 0) + " bytes)");
                return;
            }
            if (p.MissionSiteId == _hydratedSiteId) return;   // duplicate reliable double-send → ignore

            // N3 guard: a still-pending deploy for a DIFFERENT site means we never finished hydrating the
            // previous one before a new mission's deploy arrived. Surface it, then keep the newest (matching
            // the existing latest-wins overwrite intent — same-site double-sends are already dropped above).
            if (_pendingClientDeploy != null && _pendingClientDeploy.MissionSiteId != p.MissionSiteId)
                Debug.LogWarning("[Multiplayer][tac] overwriting pending deploy site " +
                                 _pendingClientDeploy.MissionSiteId + " with " + p.MissionSiteId);

            _pendingClientDeploy = p;
            Debug.Log("[Multiplayer][tac] CLIENT received tac.deploy site=" + p.MissionSiteId +
                      " gpBytes=" + p.GameParamsBytes.Length + " snapBytes=" + p.SnapshotBytes.Length +
                      " actors=" + p.ActorTable.Count);

            // ARRIVAL GATE (RCA round-5): the client normally enters its tactical level through the co-op
            // load barrier (curtain Loaded→Playing) BEFORE this chunked deploy finishes reassembling. In
            // that real flow ClientOnLevelReady already fired-and-skipped (its _pendingClientDeploy was
            // still null), and there is NO geoscape left to launch from (ClientLaunchMission would hit
            // "no GeoLevelController"). So if a tactical level is already live, hydrate IT directly;
            // otherwise fall back to the legacy deploy-driven geoscape launch.
            object liveTlc = LiveTacticalLevelController();
            var decision = TacticalDeployArrivalGate.Decide(liveTlc != null);
            if (decision == TacticalDeployArrivalGate.Decision.HydrateExisting)
            {
                // The live level was already built by the client's OWN native tactical load (it loaded the
                // transferred tactical save → BeforePlaying→PrepareLevel→ProcessInstanceData ran on a FRESH
                // faction set, populating Vision.KnownActors / effects / turn / AI from the SAME shared host
                // state). Re-running ProcessInstanceData on this already-populated level is BOTH redundant
                // and fatal: TacticalFactionVision.ProcessInstanceData does KnownActors.AddRange (add-only ⇒
                // "An item with the same key has already been added"), and TacticalFaction would double-apply
                // FactionEffects. So on THIS path we skip the snapshot ProcessInstanceData and only rebuild
                // the NetId registry + reconcile + arm mirror (the deploy snapshot's actual job — the actor
                // set/positions ride the registry + tac.move rail, NOT ProcessInstanceData). (RCA 2026-06-18.)
                Debug.Log("[Multiplayer][tac] CLIENT deploy arrived with live tactical level → hydrating existing level (no relaunch, skip redundant ProcessInstanceData)");
                // Stage-3: the client is ALREADY in tactical (no native load to hand off to) → end + lift any
                // load-phase curtain immediately so we don't leave it hanging over the live level.
                try { TacticalLoadPhaseSync.ClientAbortCurtain("hydrate-existing"); } catch { }
                // alreadyLoaded=true travels WITH this call (formerly the free _hydrateLevelAlreadyLoaded static):
                // the level was natively loaded ⇒ ClientHydrateNow skips the redundant snapshot ProcessInstanceData.
                try { ClientOnLevelReady(liveTlc, alreadyLoaded: true); }
                catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ClientOnLevelReady (late-deploy) failed: " + ex); }
            }
            else if (UseSaveTransferEntry)
            {
                // [Batch-1 entry-via-save] No self-launch: the host is shipping a mid-tactical save that will
                // BUILD this client's tactical level (SaveChunk/SaveDone → ClientLoadCrt → PrepareEntryFromBlobCrt
                // → EnterLevel on BEGIN → Playing → ClientOnLevelReady(alreadyLoaded:true) hydrates it). The deploy
                // is already stashed in _pendingClientDeploy above — just wait for the transfer to build the level.
                // (LaunchTacticalGameGatePatch already blocks any spontaneous client launch on this path.)
                Debug.Log("[Multiplayer][tac] CLIENT deploy stashed; awaiting host save-transfer to build the tactical level (UseSaveTransferEntry)");
                // Arm the stall watchdog: if the host save write aborts (no chunks ever arrive), fall back to
                // the legacy self-launch instead of hanging forever behind the load indicator.
                _entryStallDeadline = Time.realtimeSinceStartup + EntryTransferStallSeconds;
            }
            else
            {
                // Legacy path: the client has NO live tactical level → it must launch fresh, and the snapshot
                // ProcessInstanceData IS required (it restores faction-level state onto that fresh level,
                // exactly like the game's own save-load at TacticalLevelController BeforePlaying:550). The
                // deferred Playing-transition postfix later calls ClientOnLevelReady(level, alreadyLoaded: false).
                try { ClientLaunchMission(p); }
                catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ClientLaunchMission failed: " + ex); }
            }
        }

        /// <summary>
        /// CLIENT: launch the SAME tactical mission the host launched. Resolve our own GeoMission by the
        /// host's site id (shared campaign save → same GeoSite/SiteId), then drive the native
        /// <c>GeoLevelController.LaunchTacticalGame</c>. The map plot regenerates deterministically from the
        /// shared <c>Site.MapPlotInstanceData</c>/<c>RandomSeed</c>; the host's full snapshot
        /// (positions/HP/turn) is applied on the client Playing transition.
        ///
        /// NOTE (spec §9 runtime-risk): client launch without a fresh geoscape "activate" is the part most
        /// likely to need an in-game tweak. If the native launch needs more priming, prime Site/GeoMission
        /// here. Verified statically: LaunchTacticalGame(mission, PlayTacticalGameLevelResult) is reachable
        /// and PrepareTacticalGame builds the params from the resolved Site.
        /// </summary>
        private static void ClientLaunchMission(TacticalDeployCodec.DeployPayload p)
        {
            EnsureReflection();
            var geo = GeoLevelController();
            if (geo == null) { Debug.LogError("[Multiplayer][tac] ClientLaunchMission: no GeoLevelController"); return; }

            object mission = ResolveGeoMissionBySiteId(geo, p.MissionSiteId);
            if (mission == null)
            {
                Debug.LogError("[Multiplayer][tac] ClientLaunchMission: no GeoMission for site " + p.MissionSiteId +
                               " — the client may not yet see this mission. Will rely on its own launch path.");
                return;
            }

            // Build the level params from the resolved mission's site (PrepareTacticalGame). Reach the
            // native protected PrepareTacticalGame(GeoSite, GeoSquad) to obtain a PlayTacticalGameLevelResult.
            // CRITICAL (FIX 2): PrepareTacticalGame derefs squad.Units (GeoMission.cs:325) → a null squad NREs
            // and the client never reaches a tactical level. Resolve the client's REAL GeoSquad first.
            object site = GetProp(mission, "Site");
            object squad = ResolveClientSquad(mission, site);
            if (squad == null)
            {
                Debug.LogError("[Multiplayer][tac] ClientLaunchMission: could not resolve a GeoSquad for site " +
                               p.MissionSiteId + " — aborting client launch (would NRE in PrepareTacticalGame)");
                return;
            }
            object gameParams = InvokePrepareTacticalGame(mission, site, squad);
            if (gameParams == null)
            {
                Debug.LogError("[Multiplayer][tac] ClientLaunchMission: PrepareTacticalGame returned null");
                return;
            }
            // LaunchTacticalGame(GeoMission, PlayTacticalGameLevelResult). Flag this as OUR deploy-driven
            // launch so the gate prefix lets it through (a spontaneous client launch is blocked).
            var launch = AccessTools.Method(geo.GetType(), "LaunchTacticalGame");
            if (launch == null) { Debug.LogError("[Multiplayer][tac] ClientLaunchMission: LaunchTacticalGame not found"); return; }
            _clientLaunchInProgress = true;
            try { launch.Invoke(geo, new[] { mission, gameParams }); }
            catch
            {
                // The native launch threw → NO native level load will ever take over (or lift) the curtain.
                // ABORT it here (a hand-off would orphan it: ClientHandoff drops the watchdog without a lift),
                // then rethrow so the OnDeployReceived caller logs the failure as before.
                try { TacticalLoadPhaseSync.ClientAbortCurtain("client launch failed"); } catch { }
                throw;
            }
            finally { _clientLaunchInProgress = false; }
            // Stage-3 hand-off AFTER a successful launch: our download bar yields to the native level-load bar
            // under the SAME curtain (the native path reassigns the bar source itself — the SetLoadingLevel
            // idiom). EndDownloadBar only — no lift.
            try { TacticalLoadPhaseSync.ClientHandoff(); } catch { }
            Debug.Log("[Multiplayer][tac] CLIENT launched tactical mission for site " + p.MissionSiteId);
            // Arm the stall watchdog: from here the NATIVE load must reach Playing (which fires the
            // OnLevelStateChanged postfix → ClientOnLevelReady → disarm). Silence past the deadline = stall.
            _launchStallDeadline = Time.realtimeSinceStartup + LaunchStallSeconds;
            _launchStallSiteId = p.MissionSiteId;
        }

        /// <summary>Per-frame stall check (pumped from <see cref="TacticalLoadPhaseSync.Tick"/>): logs ONE loud
        /// warning if the native tactical load never reached Playing after a deploy-driven client launch
        /// (RCA 2026-07-11: instance stalled with only a silent "no live TLC" flood). Cheap early-out when idle.</summary>
        public static void ClientLaunchStallTick()
        {
            if (_launchStallDeadline == 0f || Time.realtimeSinceStartup < _launchStallDeadline) return;
            _launchStallDeadline = 0f;   // fire once, then disarm
            Debug.LogWarning("[Multiplayer][tac] tactical load stalled — Playing never reached after launch (site " +
                             _launchStallSiteId + ")");
        }

        /// <summary>Per-frame stall check (pumped from <see cref="TacticalLoadPhaseSync.Tick"/>) for the
        /// entry-via-save wait: if the host save-transfer never arrives (host write aborted before SendBlob/
        /// OpenBarrier → no chunks, no barrier, no reveal/kick fallback), fall back to the legacy self-launch
        /// instead of hanging forever behind the load indicator. Self-disarms the instant a transfer arrives /
        /// the level builds / the deploy hydrates. Cheap early-out when idle.</summary>
        public static void ClientEntryTransferStallTick()
        {
            if (_entryStallDeadline == 0f) return;

            var st = NetworkEngine.Instance?.SaveTransfer;
            bool transferArrived = st != null && st.TransferActive;   // first SaveChunk sets _rxTotalBytes>0
            bool liveTactical = LiveTacticalLevelController() != null;
            bool stillPending = _pendingClientDeploy != null;

            // Progressing normally (chunks flowing / level built / already hydrated) → stop watching, no fallback.
            if (!stillPending || transferArrived || liveTactical) { _entryStallDeadline = 0f; return; }

            if (!TacticalEntryStallGate.ShouldFallbackToSelfLaunch(
                    Time.realtimeSinceStartup >= _entryStallDeadline, stillPending, transferArrived, liveTactical))
                return;   // deadline not reached yet — keep waiting

            _entryStallDeadline = 0f;
            var p = _pendingClientDeploy;
            Debug.LogError("[Multiplayer][tac] entry-via-save STALLED: no host save-transfer within " +
                           EntryTransferStallSeconds + "s — falling back to legacy self-launch (site " +
                           p.MissionSiteId + ")");
            try { ClientLaunchMission(p); }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] entry-stall fallback ClientLaunchMission failed: " + ex); }
        }

        /// <summary>
        /// CLIENT: the host's tac-entry save transfer ABORTED (<c>PacketType.EntryTransferAbort</c>) — it will
        /// never build our tactical level. Drop the stashed deploy + stall watchdog, release the geo-transition
        /// gate and lift the co-op curtain so the player returns to the live geoscape mirror. Deliberately NO
        /// legacy self-launch: the sim-frozen mirror often has no GeoMission for the site (live failure
        /// 2026-07-13 — the 60s watchdog's fallback failed exactly there and left every peer wedged).
        /// </summary>
        public static void ClientAbortEntryWait(string reason)
        {
            _pendingClientDeploy = null;
            _entryStallDeadline = 0f;
            try { Multiplayer.Network.Sync.State.GeoTransitionGate.InTransition = false; } catch { }
            try { TacticalLoadPhaseSync.ClientAbortCurtain("host entry-transfer abort: " + reason); } catch { }
        }

        /// <summary>
        /// CLIENT: our tactical level reached Playing (or a late deploy arrived into the already-live level).
        /// Drive the hydrate, but NOT inline: the native <c>Serializer.Read</c> coroutine reads
        /// <c>Timing.Current</c>, which throws unless the caller is inside a running <c>IUpdateable</c> tick
        /// (Timing.cs:41-44). Driven straight from the network inbound callback (round-6 break:
        /// "Timing.Current should be called from inside a running IUpdateable" → mirror never armed), it
        /// threw. So we DEFER the whole hydrate body onto the level's <c>Timing</c> as a coroutine — exactly
        /// the way the host serialize is pumped inside its deferred-capture coroutine — so the serializer
        /// runs inside a running IUpdateable. If no Timing resolves, fall back to an inline best-effort
        /// hydrate (mirrors the host's <see cref="StartDeferredCapture"/> immediate fallback).
        /// </summary>
        /// <param name="alreadyLoaded">True when the level was ALREADY natively loaded (the late-deploy-into-a-
        /// live-level arrival path) ⇒ <see cref="ClientHydrateNow"/> SKIPS the redundant snapshot
        /// ProcessInstanceData. Threaded explicitly (replaces the former free <c>_hydrateLevelAlreadyLoaded</c>
        /// static) so the flag travels with the call instead of as hidden cross-method state.</param>
        public static void ClientOnLevelReady(object tacticalLevelController, bool alreadyLoaded)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return;
            _launchStallDeadline = 0f;   // level reached Playing (or live-level hydrate) → stall watchdog off
            if (_pendingClientDeploy == null || tacticalLevelController == null) return;
            // Idempotent across the two Playing seams (direct-launch TacticalLevelStateChangedPatch +
            // save-loaded CurtainShowPatch): the hydrate is deferred, so _pendingClientDeploy is not cleared
            // until it runs — without this a 2nd same-frame call would schedule a 2nd hydrate (double
            // ProcessInstanceData / double-arm). Reset in OnMissionExit.
            if (_mirrorArmed || _hydrateScheduled) return;
            _hydrateScheduled = true;
            EnsureReflection();

            object timing = ResolveTiming(tacticalLevelController);
            var decision = TacticalHydrateSchedulingGate.Decide(timing != null);
            if (decision == TacticalHydrateSchedulingGate.Decision.DeferOnTiming)
            {
                Debug.Log("[Multiplayer][tac] CLIENT hydrate → deferring onto level Timing (serializer needs a running IUpdateable)");
                if (InvokeTimingStart(timing, ClientHydrateCrt(tacticalLevelController, alreadyLoaded))) return;
                Debug.LogError("[Multiplayer][tac] CLIENT hydrate: Timing.Start failed — hydrating inline (may throw on serializer)");
            }
            else
            {
                Debug.LogError("[Multiplayer][tac] CLIENT hydrate: no Timing resolvable — hydrating inline (may throw on serializer)");
            }
            ClientHydrateNow(tacticalLevelController, alreadyLoaded);
        }

        /// <summary>Save-loaded tactical entry (geo→tac save-transfer): the level restores ALREADY in Playing, so
        /// native <c>TacticalLevelController.OnLevelStateChanged</c> never fires and TacticalLevelStateChangedPatch
        /// never arms the client mirror. <c>CurtainShowPatch</c> calls this at its own Playing seam instead. Reuses
        /// <see cref="ClientOnLevelReady"/> with <c>alreadyLoaded:true</c> (the native save-load already ran
        /// ProcessInstanceData); the client/host/session/pending/dup guards there make it a safe no-op on the host,
        /// off-session, with no pending deploy, or when the direct-launch path already armed.</summary>
        public static void ClientOnLevelReadyFromCurtain()
        {
            ClientOnLevelReady(LiveTacticalLevelController(), alreadyLoaded: true);
        }

        /// <summary>Resolve a <c>Base.Core.Timing</c> to start the client hydrate coroutine on: prefer the live
        /// tactical level's own Timing, else the ambient <c>Timing.Current</c>. Mirrors
        /// <see cref="StartDeferredCapture"/>'s timing resolution.</summary>
        private static object ResolveTiming(object tacticalLevelController)
        {
            object timing = GetProp(tacticalLevelController, "Timing");
            if (timing == null)
            {
                var timingType = AccessTools.TypeByName("Base.Core.Timing");
                var currentProp = timingType != null ? AccessTools.Property(timingType, "Current") : null;
                try { timing = currentProp?.GetValue(null, null); } catch { timing = null; }
            }
            return timing;
        }

        /// <summary>CLIENT hydrate coroutine: runs the hydrate body inside a running IUpdateable (so the native
        /// serializer's <c>Timing.Current</c> read resolves). MUST be <c>IEnumerator&lt;NextUpdate&gt;</c> (not a
        /// bare IEnumerator) so the emitted state machine binds to the native
        /// <c>Timing.Start(IEnumerator&lt;NextUpdate&gt;, …)</c> overload — see the host
        /// <see cref="DeferredCaptureCrt"/> note. The work runs on the first pump (no inter-frame wait), then
        /// the coroutine ends.</summary>
        private static IEnumerator<NextUpdate> ClientHydrateCrt(object tacticalLevelController, bool alreadyLoaded)
        {
            // Wait until the level is genuinely turn-0 ready before hydrating — the SAME readiness gate the host
            // uses before capture (TacticalDeployReadinessGate / HasAnyTurnStarted). The Playing postfix fires
            // BEFORE OnLevelStart's DeployForTurn has spawned the client's actors, so an immediate hydrate (a)
            // enumerated ZERO actors → matched=0/N, and (b) armed mirror mode MID-deployment, so the
            // IsClientMirroring suppression patches corrupted the in-flight native DoSpawnActor → NRE cascade in
            // EquipmentComponent.OnActorEnteredPlay. Mirror is NOT armed during the initial native load, so the
            // native turn-0 runs unmolested and flips HasAnyTurnStarted after DeployForTurn+NextTurnCrt — poll it
            // with the same bounded fail-safe. (RCA 2026-07-11, friend's client log: matched=0/143 + spawn NRE.)
            // On the late-deploy-into-a-live-level path (alreadyLoaded from OnDeployReceived) HasAnyTurnStarted is
            // already true, so this returns CaptureReady on frame 0 — no added delay.
            int frames = 0;
            while (true)
            {
                bool ready = false;
                try { ready = ToBool(GetProp(tacticalLevelController, "HasAnyTurnStarted")); }
                catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ClientHydrateCrt: read HasAnyTurnStarted failed: " + ex); }

                var decision = TacticalDeployReadinessGate.Decide(ready, frames, CaptureReadyMaxFrames);
                if (decision != TacticalDeployReadinessGate.Decision.Wait)
                {
                    if (decision == TacticalDeployReadinessGate.Decision.CaptureTimeout)
                        Debug.LogError("[Multiplayer][tac] ClientHydrateCrt: readiness gate timed out after " +
                                       frames + " frames — hydrating anyway (fail-safe)");
                    else
                        Debug.Log("[Multiplayer][tac] ClientHydrateCrt: level ready after " + frames +
                                  " frame(s) → hydrating");
                    break;
                }
                frames++;
                yield return NextUpdate.NextFrame;
            }
            ClientHydrateNow(tacticalLevelController, alreadyLoaded);
            yield break;
        }

        /// <summary>
        /// CLIENT: restore the pending host snapshot (<c>ProcessInstanceData</c>), rebuild the NetId dict from
        /// the host actor table, and arm mirror mode. MUST run inside a running IUpdateable (the native
        /// serializer reads <c>Timing.Current</c>) — see <see cref="ClientOnLevelReady"/>.
        /// </summary>
        private static void ClientHydrateNow(object tacticalLevelController, bool alreadyLoaded)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return;
            if (_pendingClientDeploy == null || tacticalLevelController == null) return;
            EnsureReflection();

            var p = _pendingClientDeploy;
            try
            {
                // 1) Restore the full battle from the host snapshot (deserialize → ProcessInstanceData) — but
                //    ONLY when this is a FRESH level (legacy launch-then-hydrate path). When the client is
                //    hydrating an already-live level (the real co-op flow), the client's own native tactical
                //    load already ran ProcessInstanceData on a fresh faction set from the SAME shared host
                //    state, so re-running it here throws (TacticalFactionVision.ProcessInstanceData →
                //    KnownActors.AddRange is add-only ⇒ duplicate-key ArgumentException) and would double-apply
                //    FactionEffects. The deploy snapshot's actual contribution — the NetId actor table +
                //    positions — is applied below via the registry + reconcile (+ the live tac.move rail), NOT
                //    via ProcessInstanceData. (RCA 2026-06-18: matches the game's own load pattern, which runs
                //    ProcessInstanceData exactly once on a freshly-created level — TacticalLevelController:550.)
                if (alreadyLoaded)
                {
                    Debug.Log("[Multiplayer][tac] ClientHydrateNow: level already natively loaded → skipping redundant snapshot ProcessInstanceData (registry rebuild only)");
                }
                else
                {
                    // LEGACY / should-not-run: since 2026-07-11 both callers pass alreadyLoaded:true (the client
                    // always builds its tactical level natively before hydrating), so this fragile snapshot
                    // ProcessInstanceData path is dead. It empty-graphs on a real client (host round-trips the
                    // same bytes; client Serializer.Read → ByRef.Value==null). Kept ONLY as a forensic tripwire:
                    // a regression re-entering here stays visible via the [tac] failure signature below.
                    Debug.LogWarning("[Multiplayer][tac] ClientHydrateNow: LEGACY snapshot-hydrate path hit (expected dead) — attempting fragile deserialize");
                    object snapshotObj = DeserializeGraph(p.SnapshotBytes, _tacLevelInstType);
                    if (snapshotObj == null)
                    {
                        Debug.LogError("[Multiplayer][tac] ClientHydrateNow: snapshot deserialize failed");
                        return;
                    }
                    // ProcessInstanceData is private → AccessTools method invoke.
                    var process = AccessTools.Method(_tlcType, "ProcessInstanceData", new[] { _tacLevelInstType });
                    if (process == null) { Debug.LogError("[Multiplayer][tac] ClientHydrateNow: ProcessInstanceData not found"); return; }
                    process.Invoke(tacticalLevelController, new[] { snapshotObj });
                }

                // 2) Rebuild the NetId dict: match the host actor table onto our restored actors.
                Registry = new TacticalActorRegistry();
                var restored = EnumerateActorRefs(tacticalLevelController);
                int matched = Registry.MatchAndRegister(p.ActorTable, restored);

                // 2b) DUPLICATE-ACTOR RECONCILE (FIX 2): the client's fresh deploy rolls its OWN enemy
                //     participants via SharedData.Random (TacParticipantSpawn.cs:330/488) — that RNG is
                //     time-seeded + not serialized, so the client enemy SET can differ from the host's, and
                //     ProcessInstanceData (TacticalLevelController.cs:586) restores only faction-level data
                //     (turn/relations/vision/AI), NOT the actor set. Any restored actor NOT matched to a host
                //     actorTable row is a client-only extra (a duplicate/divergent Pandoran) → destroy it so
                //     the client never carries phantom enemies. Soldiers always match (shared save → identical
                //     GeoUnitId), so only genuinely client-rolled extras are removed.
                int removed = ReconcileUnmatchedActors(restored);

                // 3) Arm mirror mode (frozen pure mirror). RETAIN the host actor table for the mission's
                //    lifetime: late-spawned actors bound after this one-shot hydrate (lazy rebind / rematch
                //    retry) still need their host deploy POSITION from it — discarding it here was why a
                //    late-bound soldier stood at its client-native spawn cell (wrong placement).
                _missionActorTable = p.ActorTable;
                _mirrorArmed = true;
                _hydratedSiteId = p.MissionSiteId;
                _pendingClientDeploy = null;
                LiveTlc = tacticalLevelController;   // live-rail handle for applying tac.move/tac.turn
                LiveSeq = new TacticalLiveSeq();     // fresh per-mission client guard

                // 3b) INITIAL PLACEMENT: drive every MATCHED actor to the host's authoritative deploy
                //     position. The client's native deploy rolls its own spawn cells, so even a matched
                //     soldier can stand on a different cell than the host's (user-visible "soldiers unload
                //     at wrong positions"). Reuses ApplyMirrorPosition (no-op/walk/teleport per distance);
                //     runs AFTER mirror-arm because it gates on IsClientMirroring. Pandorans matched by
                //     position are already within epsilon → cheap no-op.
                int placed = ApplyDeployPositions(p.ActorTable);

                // 4) Enter the INITIAL turn from the restored snapshot (ProcessInstanceData already set the
                //    snapshot's _currentFactionIndex). If turn 0 is the player faction, the client must enter
                //    its player turn now — a host tac.turn for turn 0 may have been broadcast before this
                //    client finished hydrating (it has no LiveTlc yet → dropped), so we self-enter here.
                try { TacticalTurnSync.ClientEnterInitialTurn(tacticalLevelController); }
                catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ClientEnterInitialTurn failed: " + ex); }

                // 5) 0x99: drain any objective payloads that raced the scene load (the host's deploy-time
                //    objective SEED always arrives while the client is still loading) — after the fresh
                //    LiveSeq above, so the queued seqs re-apply cleanly in arrival order.
                try { TacticalObjectiveSync.ClientApplyPending(); }
                catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ClientApplyPending failed: " + ex); }

                Debug.Log("[Multiplayer][tac] CLIENT hydrated tac.deploy site=" + p.MissionSiteId +
                          " matched=" + matched + "/" + p.ActorTable.Count + " removedExtras=" + removed +
                          " placed=" + placed + " mirror=ARMED");
                DumpActorTable("CLIENT", p.ActorTable);

                // 6) LATE-SPAWN RETRY: actors still spawning when the one-shot match above ran never bound
                //    (observed 118/141 vs 139/143 swings). Proactively re-run the SAME matcher on a bounded
                //    cadence so they get matched + placed without waiting for their first inbound message
                //    (the ResolveLiveActor lazy-rebind stays as the per-message backstop).
                if (matched < p.ActorTable.Count)
                {
                    object timing = ResolveTiming(tacticalLevelController);
                    if (timing == null || !InvokeTimingStart(timing, ClientRematchCrt()))
                        Debug.LogError("[Multiplayer][tac] rematch retry could not start — lazy-rebind fallback only");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] ClientHydrateNow failed: " + ex);
            }
        }

        /// <summary>Disarm mirror mode + clear per-mission state (mission exit). Idempotent.
        /// <paramref name="isReloadBoundary"/>=true marks the SyncEngine reload-boundary sweep (a save-load),
        /// which — since df9a8d4's entry-via-save — is ALSO how the client ENTERS tactical. In that case an
        /// in-flight ENTRY deploy is already stashed but not yet hydrated (hydrate fires at the post-reload
        /// Playing seam), so wiping it here drops 100% of tac.* (the df9a8d4 regression). A pending deploy is
        /// nulled the instant hydrate arms (see ClientHydrateNow), so <c>_pendingClientDeploy != null</c> here
        /// uniquely means "entry deploy awaiting hydrate"; a genuine mid-mission F2/save reload has none
        /// (already hydrated → null), so preserving is a no-op there. Genuine mission-END
        /// (<c>isReloadBoundary=false</c>, TacticalLevelEndPatch) always clears it.</summary>
        public static void OnMissionExit(bool isReloadBoundary = false)
        {
            // Keep a stashed-but-unhydrated ENTRY deploy (+ its entry/launch watchdogs) alive across a
            // reload-boundary sweep so the post-reload Playing seam can still hydrate. Narrow: only on the
            // reload-boundary path, only while a deploy is actually pending. (df9a8d4 regression.)
            bool preserveEntryDeploy = isReloadBoundary && _pendingClientDeploy != null;

            // (a) Reset the lifecycle-critical + purely-local statics FIRST — BEFORE any EXTERNAL guard reset
            //     below can throw. The Inc-T1 HostStartFlush coroutine self-stops by watching LiveTlc, so if an
            //     external reset threw and skipped `LiveTlc = null`, that coroutine would LEAK into the next
            //     mission. Doing the local teardown up-front makes the lifecycle reset exception-proof. (N1.)
            _mirrorArmed = false;
            _hydrateScheduled = false;
            _missionActorTable = null;   // rematch/lazy-bind coroutines watch this → self-stop
            if (!preserveEntryDeploy)
            {
                _pendingClientDeploy = null;
                _launchStallDeadline = 0f;
                _entryStallDeadline = 0f;
            }
            _lastBroadcastSiteId = int.MinValue;
            _captureScheduledSiteId = int.MinValue;
            _hydratedSiteId = int.MinValue;
            _chunkReassembler = new ChunkReassembler();   // drop any half-received chunk set
            Registry = new TacticalActorRegistry();
            LiveTlc = null;                               // Inc T1: the flush coroutine watches this → self-stops
            LiveSeq = new TacticalLiveSeq();
            IntentDedup = new TacticalIntentDedup();

            // (b) EXTERNAL guard resets — each isolated in its OWN try/catch so one throwing does NOT skip the
            //     others (the lifecycle statics above already reset, so a throw here can no longer leak the
            //     flush coroutine). (N1.)
            try { TacticalVisionSync.HostResetBroadcastGuard(); }                  // Inc Vision: clear the per-mission chattiness guard
            catch (Exception ex) { Debug.LogError($"[Multiplayer][tac] OnMissionExit external reset failed: {ex}"); }
            try { TacticalActorStateSync.HostResetFlushGuard(); }                  // Inc T1: clear the per-actor state-delta signatures
            catch (Exception ex) { Debug.LogError($"[Multiplayer][tac] OnMissionExit external reset failed: {ex}"); }
            try { Multiplayer.Harmony.Tactical.ClientStatusMirrorGuards.Reset(); }  // Feature B: drop inert-mirror tracking
            catch (Exception ex) { Debug.LogError($"[Multiplayer][tac] OnMissionExit external reset failed: {ex}"); }
            try { TacticalFireAnimSync.Reset(); }                                  // Feature C: drop any stuck replay-guard depth
            catch (Exception ex) { Debug.LogError($"[Multiplayer][tac] OnMissionExit external reset failed: {ex}"); }
            try { TacticalMeleeAnimSync.Reset(); }                                 // Feature C (melee): symmetry (Phase 1 stateless)
            catch (Exception ex) { Debug.LogError($"[Multiplayer][tac] OnMissionExit external reset failed: {ex}"); }
            try { TacticalSurfaceSync.Reset(); }                                   // TS3: drop the pending ground-surface coalesce buffer
            catch (Exception ex) { Debug.LogError($"[Multiplayer][tac] OnMissionExit external reset failed: {ex}"); }
            try { TacticalStructDamageSync.Reset(); }                              // TS6: drop the pending struct-damage buffer
            catch (Exception ex) { Debug.LogError($"[Multiplayer][tac] OnMissionExit external reset failed: {ex}"); }
            try { TacticalObjectiveSync.Reset(); }                                 // 0x99: drop the diff cache + pre-hydrate queue
            catch (Exception ex) { Debug.LogError($"[Multiplayer][tac] OnMissionExit external reset failed: {ex}"); }
            try { TacticalLoadPhaseSync.Reset(); }                                 // load-phase indicator: stop host ping + lift any client curtain
            catch (Exception ex) { Debug.LogError($"[Multiplayer][tac] OnMissionExit external reset failed: {ex}"); }
            try { TacticalMoveSync.ResetOriginNativeMove(); }                      // rca-jetjump: drop origin-native-move windows
            catch (Exception ex) { Debug.LogError($"[Multiplayer][tac] OnMissionExit external reset failed: {ex}"); }
            try { TacticalTurnSync.IsClientEnemyTurn = false; } catch { }          // Inc3: clear enemy-turn cinematic-camera flag
        }

        // ─── Wire send + inbound (rides the 0x67 SyncEnvelope rail) ─────────────────────────
        // tac.deploy is a host→ALL one-way push of a large, idempotent snapshot — NOT a request/apply
        // action. It rides the SAME SyncEnvelope (0x67) inbound chokepoint the geoscape sync uses, but
        // BYPASSES the action-relay's shared SequenceTracker via a tactical fast-path hook on
        // SurfaceRouter (TacticalInbound). Rationale (grounded): the action-apply path gates on a SINGLE
        // global monotonic seq shared with geoscape; a request-free host push has no way to assign a
        // correct fresh seq from the tactical module, and forcing one would poison post-mission geoscape
        // ordering. The hook is null unless tactical-init armed it, so it is INERT for the geoscape/event
        // sync. The kind byte is StateSnapshot (host→all push semantics); the hook keys on the tactical
        // surfaceId, so it never touches geoscape action/channel surfaces.

        /// <summary>Arm/disarm the SurfaceRouter tactical fast-path (called from tactical init).</summary>
        public static void ArmInboundHook()
            => Network.Sync.SurfaceRouter.TacticalInbound = HandleTacticalEnvelope;

        public static void DisarmInboundHook()
            => Network.Sync.SurfaceRouter.TacticalInbound = null;

        /// <summary>SurfaceRouter fast-path: returns true if this surface is a tactical surface it consumed
        /// (so the router stops). The raw envelope payload is the tac codec payload (no seq wrapper).
        ///   • TacDeploy (0x80): a complete single-envelope snapshot → OnDeployReceived directly.
        ///   • TacDeployChunk (0x81): one FRAGMENT of an over-cap snapshot → feed the reassembler; only the
        ///     fragment that completes the (siteId,deployGeneration) set yields the whole payload (idempotent).
        /// </summary>
        private static bool HandleTacticalEnvelope(ulong senderPeerId, byte surfaceId, byte[] payload)
        {
            if (surfaceId == (byte)TacticalSurfaceIds.TacDeploy)
            {
                // Stage-2: a single-envelope deploy arrived → relabel "Downloading mission…" + bar to 1.0.
                try { TacticalLoadPhaseSync.ClientOnDeploySingle(); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] load-phase (single) failed: " + ex); }
                try { OnDeployReceived(payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] inbound failed: " + ex); }
                return true;
            }
            if (surfaceId == (byte)TacticalSurfaceIds.TacLoadPhase)
            {
                // Stage-1: host-loading progress heartbeat (DISPLAY-only) → show/keep the client curtain.
                try { TacticalLoadPhaseSync.HandleLoadPhase(payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.load.phase failed: " + ex); }
                return true;
            }
            if (surfaceId == (byte)TacticalSurfaceIds.TacDeployChunk)
            {
                try
                {
                    if (TacticalDeployChunkCodec.TryDecode(payload, out var frag))
                    {
                        // Stage-2: drive "Downloading mission…" by received-chunks/total (before reassembly).
                        try { TacticalLoadPhaseSync.ClientOnDeployChunk(frag.DeployGeneration, frag.ChunkIndex, frag.ChunkCount); }
                        catch (Exception lex) { Debug.LogError("[Multiplayer][tac] load-phase (chunk) failed: " + lex); }
                        byte[] full = _chunkReassembler.Accept(frag);
                        if (full != null)
                        {
                            Debug.Log("[Multiplayer][tac] CLIENT reassembled tac.deploy site=" + frag.SiteId +
                                      " gen=" + frag.DeployGeneration + " chunks=" + frag.ChunkCount +
                                      " totalLen=" + full.Length);
                            OnDeployReceived(full);
                        }
                    }
                    else Debug.LogError("[Multiplayer][tac] inbound chunk decode failed (" + (payload?.Length ?? 0) + " bytes)");
                }
                catch (Exception ex) { Debug.LogError("[Multiplayer][tac] inbound chunk failed: " + ex); }
                return true;
            }

            // ─── LIVE outcome / intent rail (Inc 2/4) ─────────────────────────────────────────────
            // Intents (client→host) land on the host; outcomes (host→all) land on clients. Each handler
            // is side-guarded internally, so a stray envelope on the wrong side is a clean no-op.
            if (surfaceId == (byte)TacticalSurfaceIds.TacIntentMove)
            {
                try { TacticalMoveSync.HostOnMoveIntent(senderPeerId, payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.intent.move failed: " + ex); }
                return true;
            }
            if (surfaceId == (byte)TacticalSurfaceIds.TacMoveStart)
            {
                try { TacticalMoveSync.ClientOnMoveStart(payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.move.start failed: " + ex); }
                return true;
            }
            if (surfaceId == (byte)TacticalSurfaceIds.TacMove)
            {
                try { TacticalMoveSync.ClientOnMove(payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.move failed: " + ex); }
                return true;
            }
            if (surfaceId == (byte)TacticalSurfaceIds.TacIntentEndTurn)
            {
                try { TacticalTurnSync.HostOnEndTurnIntent(senderPeerId, payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.intent.endturn failed: " + ex); }
                return true;
            }
            if (surfaceId == (byte)TacticalSurfaceIds.TacTurn)
            {
                try { TacticalTurnSync.ClientOnTurn(payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.turn failed: " + ex); }
                return true;
            }

            // ─── LIVE combat/damage rail (Inc 3a) ─────────────────────────────────────────────────
            if (surfaceId == (byte)TacticalSurfaceIds.TacIntentAbility)
            {
                try { TacticalCombatSync.HostOnAbilityIntent(senderPeerId, payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.intent.ability failed: " + ex); }
                return true;
            }
            if (surfaceId == (byte)TacticalSurfaceIds.TacDamage)
            {
                try { TacticalCombatSync.HandleDamage(payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.damage failed: " + ex); }
                return true;
            }

            // ─── TS2: GENERIC (non shoot/melee) ability-INTENT rail (0x8E) ────────────────────────────
            // Client→host generic ability intent (heal/recover-will/rally/psychic-scream/…). Lands on the host;
            // it re-resolves the ability by def guid + Activates it authoritatively (outcome rides 0x8F + tac.damage
            // + TS1 spawn). Side-gated internally, so a stray envelope on a client is a clean no-op.
            if (surfaceId == (byte)TacticalSurfaceIds.TacIntentGeneric)
            {
                try { TacticalCombatSync.HostOnGenericIntent(senderPeerId, payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.intent.generic failed: " + ex); }
                return true;
            }

            // ─── Feature C: client-side ATTACK ANIMATION rail (tac.fire.start) ────────────────────────
            // Host→all START push; client-only play (the handler is side-gated internally, so a stray
            // envelope on the host is a clean no-op). DAMAGE stays on tac.damage (0x88).
            if (surfaceId == (byte)TacticalSurfaceIds.TacFireStart)
            {
                try { TacticalFireAnimSync.ClientOnFireStart(payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.fire.start failed: " + ex); }
                return true;
            }

            // ─── Feature C (melee): client-side MELEE ANIMATION rail (tac.melee.start) ────────────────
            // Host→all START push; client-only play (replays the native BashCrt swing, damage/return-fire/charge
            // neutered). DAMAGE stays on tac.damage (0x88). Side-gated internally, so a stray envelope on the host
            // is a clean no-op.
            if (surfaceId == (byte)TacticalSurfaceIds.TacMeleeStart)
            {
                try { TacticalMeleeAnimSync.ClientOnMeleeStart(payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.melee.start failed: " + ex); }
                return true;
            }

            // ─── rca-jetjump: ORIGIN-NATIVE MOVE presentation replay rail (tac.nativemove) ────────────
            // Host→all START push; client-only play (each NON-origin peer replays the native JetJump flight; the
            // origin de-dups via its open window). Side-gated internally, so a stray envelope on the host is a
            // clean no-op. POSITION stays owned by the 0x8F flush + the move's OnPlayingActionEnd reconcile.
            if (surfaceId == (byte)TacticalSurfaceIds.TacNativeMove)
            {
                try { TacticalMoveSync.ClientOnNativeMove(payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.nativemove failed: " + ex); }
                return true;
            }

            // ─── TS7: PRESENTATION polish rail (enemy-turn camera follow + AoE/explosion VFX) ─────────
            // Host→all outcome pushes; client-only play (each handler is side-gated internally, so a stray envelope
            // on the host is a clean no-op). PRESENTATION ONLY — no state; damage already mirrors via tac.damage.
            if (surfaceId == (byte)TacticalSurfaceIds.TacCameraHint)
            {
                try { TacticalEnemyTurnCamera.ClientOnCameraHint(payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.camerahint failed: " + ex); }
                return true;
            }
            if (surfaceId == (byte)TacticalSurfaceIds.TacVfx)
            {
                try { TacticalVfxSync.HandleVfx(payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.vfx failed: " + ex); }
                return true;
            }

            // ─── LIVE vision rail (Inc Vision) ────────────────────────────────────────────────────
            if (surfaceId == (byte)TacticalSurfaceIds.TacVision)
            {
                try { TacticalVisionSync.HandleVision(payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.vision failed: " + ex); }
                return true;
            }

            // ─── LIVE equip/weapon-swap rail (Inc Equip) ──────────────────────────────────────────
            // Intent (client→host) lands on the host; outcome (host→all) lands on clients. Each handler is
            // side-guarded internally, so a stray envelope on the wrong side is a clean no-op.
            if (surfaceId == (byte)TacticalSurfaceIds.TacIntentEquip)
            {
                try { TacticalEquipSync.HostOnEquipIntent(senderPeerId, payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.intent.equip failed: " + ex); }
                return true;
            }
            if (surfaceId == (byte)TacticalSurfaceIds.TacEquip)
            {
                try { TacticalEquipSync.HandleEquip(payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.equip failed: " + ex); }
                return true;
            }

            // ─── LIVE overwatch-arm rail (Inc Overwatch) ──────────────────────────────────────────
            // Intent (client→host) lands on the host; state (host→all) lands on clients. Each handler is
            // side-guarded internally, so a stray envelope on the wrong side is a clean no-op.
            if (surfaceId == (byte)TacticalSurfaceIds.TacIntentOverwatch)
            {
                try { TacticalOverwatchSync.HostOnArmIntent(senderPeerId, payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.intent.overwatch failed: " + ex); }
                return true;
            }
            if (surfaceId == (byte)TacticalSurfaceIds.TacOverwatchState)
            {
                try { TacticalOverwatchSync.HandleOverwatchState(payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.overwatch.state failed: " + ex); }
                return true;
            }

            // ─── LIVE generic per-actor STATE-DELTA spine (Inc T1) ────────────────────────────────
            // Host→all state delta (AP/WP + status set). Client-only apply; the handler is IsHost-gated
            // internally, so a stray envelope on the host is a clean no-op.
            if (surfaceId == (byte)TacticalSurfaceIds.TacActorState)
            {
                try { TacticalActorStateSync.HandleActorState(payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.actorstate failed: " + ex); }
                return true;
            }

            // ─── TS1: mid-battle actor SPAWN / DESPAWN mirror ─────────────────────────────────────
            // Host→all outcome pushes; client-only apply (each handler is side-gated internally, so a stray
            // envelope on the host is a clean no-op).
            if (surfaceId == (byte)TacticalSurfaceIds.TacActorSpawn)
            {
                try { TacticalActorLifecycleSync.HandleActorSpawn(payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.actor.spawn failed: " + ex); }
                return true;
            }
            if (surfaceId == (byte)TacticalSurfaceIds.TacActorDespawn)
            {
                try { TacticalActorLifecycleSync.HandleActorDespawn(payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.actor.despawn failed: " + ex); }
                return true;
            }

            // ─── TS3: GROUND-SURFACE / VOLUME mirror (fire / goo / acid / mist) ────────────────────
            // Host→all outcome push; client-only apply (side-gated internally, so a stray envelope on the host is
            // a clean no-op). The client re-applies the native voxel type at the mirrored cells (display + LoS);
            // DAMAGE stays on tac.damage (0x88) / 0x8F.
            if (surfaceId == (byte)TacticalSurfaceIds.TacSurface)
            {
                try { TacticalSurfaceSync.HandleSurface(payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.surface failed: " + ex); }
                return true;
            }

            // ─── TS4: MISSION-CONCLUSION mirror (evac + objectives + outcome / game-over) ──────────
            // Host→all reliable conclusion push; client-only apply (side-gated internally, so a stray envelope on the
            // host is a clean no-op). The client repaints objective state + rides the native game-over flow back to
            // geoscape; the outcome MODAL stays owned by the geoscape popup-mirror (0x69) → no double-outcome.
            if (surfaceId == (byte)TacticalSurfaceIds.TacMissionEnd)
            {
                try { TacticalMissionEndSync.HandleMissionEnd(payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.missionend failed: " + ex); }
                return true;
            }

            // ─── 0x99: LIVE mission-objective mirror (scripted mission events part 1) ───────────────
            // Host→all reliable state/progress/add push; client-only apply (side-gated internally, so a stray
            // envelope on the host is a clean no-op). The client value-stamps objective state + kicks the native
            // objectives-HUD refresh; completion logic stays host-owned (mission END stays on TS4 0x95).
            if (surfaceId == (byte)TacticalSurfaceIds.TacObjective)
            {
                try { TacticalObjectiveSync.HandleObjective(payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.objective failed: " + ex); }
                return true;
            }

            // ─── MID-MISSION INVENTORY-TRANSFER (tactical loot UI re-enable) ───────────────────────
            // Intent (client→host) lands on the host; it validates + applies the loot batch natively then broadcasts
            // the surviving set. Apply (host→all) lands on clients; each re-runs the SAME native moves under the apply
            // scope. Each handler is side-gated internally, so a stray envelope on the wrong side is a clean no-op.
            if (surfaceId == (byte)TacticalSurfaceIds.TacInventoryIntent)
            {
                try { TacticalInventorySync.HostOnInventoryIntent(senderPeerId, payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.intent.inventory failed: " + ex); }
                return true;
            }
            if (surfaceId == (byte)TacticalSurfaceIds.TacInventoryApply)
            {
                try { TacticalInventorySync.HandleInventoryApply(payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.inventory failed: " + ex); }
                return true;
            }

            // ─── rca-inventory part 2: WORLD-VISUAL crate-open mirror (tac.crate.open) ──────────────
            // Host→all presentation push; client-only apply (side-gated internally, so a stray envelope on the host
            // is a clean no-op). The client flips its crate mirror's world-visual (lid anim + blue highlight off) via
            // the native CrateComponent.Open(); loot contents stay owned by 0x9A/0x9B, the loot UI stays origin-only.
            if (surfaceId == (byte)TacticalSurfaceIds.TacCrateOpen)
            {
                try { TacticalCrateSync.HandleCrateOpen(payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.crate.open failed: " + ex); }
                return true;
            }

            // ─── rca-inventory part 3: THROWABLE/CONSUMABLE item-destroy mirror (tac.item.destroy) ───
            // Host→all removal push; client-only apply (side-gated internally, so a stray envelope on the host is a
            // clean no-op). The client removes the SAME phantom item from its mirror inventory via the native
            // Destroy(), so a thrown grenade / spent consumable can never be re-activated on a stale mirror.
            if (surfaceId == (byte)TacticalSurfaceIds.TacItemDestroy)
            {
                try { TacticalItemDestroySync.ClientOnItemDestroy(payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.item.destroy failed: " + ex); }
                return true;
            }

            // ─── TS6: STRUCTURAL-DESTRUCTION mirror (destructibles: cover / LoS / nav) ──────────────
            // Host→all outcome push; client-only apply (side-gated internally, so a stray envelope on the host is a
            // clean no-op). The client re-applies the SAME native damage to the SAME destructible (resolved by its
            // deterministic SceneObjectId guid) → native destruction cascade; DAMAGE to actors stays on tac.damage (0x88).
            if (surfaceId == (byte)TacticalSurfaceIds.TacStructDamage)
            {
                try { TacticalStructDamageSync.HandleStructDamage(payload); } catch (Exception ex) { Debug.LogError("[Multiplayer][tac] tac.structdamage failed: " + ex); }
                return true;
            }
            return false;
        }

        /// <summary>
        /// HOST: frame + broadcast the deploy snapshot over the tactical rail. If the codec payload fits a
        /// single envelope (≤ <see cref="TacticalDeployChunkCodec.SingleEnvelopeMax"/>) it ships as ONE
        /// TacDeploy envelope (unchanged from before). Otherwise it is SPLIT into TacDeployChunk fragments —
        /// each safely under <c>EncodeEnvelope</c>'s u16 cap — and each fragment is broadcast as its own
        /// envelope; the client reassembles. (EncodeEnvelope throws on a payload &gt; 65535, which silently
        /// swallowed the whole hundreds-of-KB tactical snapshot before this fix.)
        /// </summary>
        private static void BroadcastDeploy(int siteId, int generation, byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null) return;
            payload = payload ?? new byte[0];

            if (payload.Length <= TacticalDeployChunkCodec.SingleEnvelopeMax)
            {
                // Small snapshot → single envelope. The client recognizes TacDeploy via the TacticalInbound
                // hook and hands the payload straight to OnDeployReceived (no tracker / per-action dedup —
                // the handler is site-id idempotent). Routed through the shared TacticalMoveSync helper,
                // which wraps payload -> EncodeEnvelope(surfaceId, StateSnapshot) -> SyncEnvelope (same wire).
                TacticalMoveSync.BroadcastToAll(engine, TacticalSurfaceIds.TacDeploy, payload);
                return;
            }

            // Over the cap → chunk. Each fragment is its own TacDeployChunk envelope; the client reassembles
            // by (siteId,generation), order-independent + duplicate-idempotent.
            var chunks = TacticalDeployChunkCodec.Split(siteId, generation, payload);
            foreach (var chunkPayload in chunks)
            {
                // Same shared helper as the single-envelope path: payload -> EncodeEnvelope(TacDeployChunk,
                // StateSnapshot) -> SyncEnvelope (byte-identical wire to the prior inline encode).
                TacticalMoveSync.BroadcastToAll(engine, TacticalSurfaceIds.TacDeployChunk, chunkPayload);
            }
            Debug.Log("[Multiplayer][tac] HOST chunked tac.deploy site=" + siteId + " gen=" + generation +
                      " totalLen=" + payload.Length + " chunks=" + chunks.Count);
        }

        // ─── Native Serializer round-trip (coroutine-pumped to completion) ─────────────────
        // The game Serializer is a coroutine (IEnumerator<NextUpdate>); off the main mission flow we drive
        // it synchronously with a generous TimeSlice so it completes in one pump. Verified API (decompile
        // 2026-06-17): new Serializer(object context); Write(IEnumerable<object>, string ".b",
        // ByRef<byte[]> dest, TimeSlice) :562; Read(ByRef<IEnumerable<object>>, TimeSlice, string ".b",
        // byte[] src, string section=null) :700. ByRef<T>.Value field; new TimeSlice(float seconds).
        //
        // CONTRACT (verified Serializer.cs decompile, ref caller NamedValueStore.cs:149/185):
        //   • ext ".b" → BinReadStream/BinWriteStream; Write ext MUST equal Read ext (both ".b"). ✓
        //   • section: the 4-arg Write wraps the graph in ONE section named SerializationID.ContentsKey.Name
        //     ("Contents", Serializer.cs:593). Read(byte[]) with section==null defaults to that same
        //     "Contents" key (Serializer.cs:680-683) — so passing null is CORRECT (matches the ref caller,
        //     which also omits section ⇒ null). ✓
        //   • ByRef<IEnumerable<object>>.Value is the right accessor — the ref caller reads
        //     objects.Value?.FirstOrDefault() (NamedValueStore.cs:150). ✓
        //   THE ACTUAL BUG (RCA 2026-06-18, mirrored vs NavConsoleCommands.cs:32/58 — the real Write-then-
        //   Read-back twin): the Serializer INSTANCE, not the args. Every working caller round-trips through
        //   GameUtl.GameComponent<SerializationComponent>().Serializer (Context = SerializationComponent,
        //   built new Serializer(this) @ SerializationComponent.cs:81). Our `new Serializer(null)` had a NULL
        //   Context, so reading any Def ref ran BaseDef.ResolveOrCreateBaseDef →
        //   serObj.Serializer.GetContext<SerializationComponent>().Repo (BaseDef.cs:124) → null.Repo → NRE
        //   inside the Read coroutine → silent abort → ByRef.Value==null (the empty graph the probe saw).
        //   FIX: both Write and Read now use ResolveGameSerializer() (the shared configured instance).

        // Reentrancy guard so the host self-roundtrip self-test (which calls DeserializeGraph) cannot
        // re-trigger another self-test. DeserializeGraph never calls SerializeGraph, so there is no real
        // recursion; this flag only suppresses the redundant probe log during the self-test read.
        private static bool _inSelfRoundtrip;

        // Cheap FNV-1a-style checksum (length-mixed) for end-to-end byte-identity confirmation. No LINQ.
        private static string BytesHash(byte[] b)
        {
            if (b == null) return "null";
            uint h = 2166136261u;
            for (int i = 0; i < b.Length; i++) { h ^= b[i]; h *= 16777619u; }
            return "len=" + b.Length + " fnv=" + h.ToString("x8");
        }

        private static string GraphTypeNames(object[] graph)
        {
            if (graph == null) return "null";
            var names = new List<string>(graph.Length);
            for (int i = 0; i < graph.Length; i++) names.Add(graph[i]?.GetType().Name ?? "null");
            return string.Join(",", names.ToArray());
        }

        /// <summary>
        /// Resolve the engine's ONE configured <c>Serializer</c> instance — the SAME object every working
        /// game caller round-trips through (<c>NavConsoleCommands</c>, <c>SerializationCommands</c>,
        /// <c>NamedValueStore</c> all use <c>GameUtl.GameComponent&lt;SerializationComponent&gt;().Serializer</c>;
        /// NavConsoleCommands.cs:32/58 is the exact Write-then-Read-back twin of our path). It is built with
        /// <c>new Serializer(this)</c> (SerializationComponent.cs:81) — Context = the SerializationComponent —
        /// then primed with <c>InitCustomTypes</c> + <c>ValidateSerializedObject</c>.
        ///
        /// ROOT CAUSE this replaces: our former <c>new Serializer(null)</c> had a NULL Context. Reading any
        /// Def reference runs <c>BaseDef.ResolveOrCreateBaseDef</c> ([SerializeCustomCreate]), which does
        /// <c>serObj.Serializer.GetContext&lt;SerializationComponent&gt;().Repo</c> (BaseDef.cs:124). With a
        /// null Context that returns null → NRE inside the Read coroutine → the read aborts and
        /// <c>objects.Value</c> stays null ⇒ the empty graph the probe saw (no outer exception because the
        /// driving coroutine swallows it). Def-laden graphs (TacLevelInstanceData/TacticalGameParams) hit it
        /// immediately. The byte[] Write/Read overloads are unchanged — only the Serializer INSTANCE changes.
        /// Returns null if the SerializationComponent isn't reachable yet (caller logs + skips).
        /// </summary>
        private static object ResolveGameSerializer()
        {
            try
            {
                if (_gameComponentSerComp == null)
                {
                    var serCompType = AccessTools.TypeByName("Base.Serialization.SerializationComponent");
                    var gameUtlType = AccessTools.TypeByName("Base.Core.GameUtl");
                    if (serCompType == null || gameUtlType == null)
                    {
                        Debug.LogError("[Multiplayer][tac] ResolveGameSerializer: SerializationComponent/GameUtl type not found");
                        return null;
                    }
                    var gc = AccessTools.Method(gameUtlType, "GameComponent");
                    _gameComponentSerComp = gc != null ? gc.MakeGenericMethod(serCompType) : null;
                    _serCompSerializerProp = AccessTools.Property(serCompType, "Serializer");
                }
                if (_gameComponentSerComp == null || _serCompSerializerProp == null)
                {
                    Debug.LogError("[Multiplayer][tac] ResolveGameSerializer: GameComponent<>/Serializer accessor not found");
                    return null;
                }
                object serComp = _gameComponentSerComp.Invoke(null, null);
                object serializer = serComp != null ? _serCompSerializerProp.GetValue(serComp, null) : null;
                if (serializer == null)
                    Debug.LogError("[Multiplayer][tac] ResolveGameSerializer: SerializationComponent/Serializer is null (not initialized yet?)");
                return serializer;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ResolveGameSerializer failed: " + ex); return null; }
        }

        // quiet=true (PersonnelBlob per-soldier round-trips): skip the probe logs + the host
        // self-roundtrip — a per-soldier self-read would DOUBLE the serializer work on every hourly
        // roster flush. Default (false) keeps the tac.deploy path byte- and log-identical.
        internal static byte[] SerializeGraph(object[] graph, bool quiet = false)
        {
            EnsureReflection();
            if (_serializerType == null || _byRefType == null || _timeSliceType == null) return null;
            try
            {
                // PROBE: host input graph (element count + concrete type names).
                if (!quiet)
                    Debug.Log("[Multiplayer][tac] Write input graph count=" + (graph?.Length ?? -1) +
                              " types=[" + GraphTypeNames(graph) + "]");
                // Use the engine's configured Serializer (Context = SerializationComponent) — see
                // ResolveGameSerializer. A `new Serializer(null)` writes Def refs that the paired Read can
                // never reconstruct (null Context → NRE in BaseDef.ResolveOrCreateBaseDef → empty graph).
                object serializer = ResolveGameSerializer();
                if (serializer == null) { Debug.LogError("[Multiplayer][tac] SerializeGraph: no game Serializer — skipping"); return null; }
                // ByRef<byte[]> dest. Base.Utils.ByRef<T> has a SINGLE ctor `ByRef(T value = default)` —
                // an optional param, NO parameterless ctor — so Activator.CreateInstance(Type) (no args)
                // throws MissingMethodException (RCA 2026-06-18 — this aborted the whole host deploy). Pass
                // the argument explicitly (default(byte[]) == null), mirroring the engine's own
                // `new ByRef<bool>(value:false)` (Serializer.cs:566).
                Type byRefBytes = _byRefType.MakeGenericType(typeof(byte[]));
                object dest = Activator.CreateInstance(byRefBytes, new object[] { null });
                // TimeSlice slice = new TimeSlice(large) → effectively unbounded single pump.
                object slice = Activator.CreateInstance(_timeSliceType, new object[] { 3600f });

                var write = AccessTools.Method(_serializerType, "Write",
                    new[] { typeof(IEnumerable<object>), typeof(string), byRefBytes, _timeSliceType });
                if (write == null) { Debug.LogError("[Multiplayer][tac] Serializer.Write(byte[]) overload not found"); return null; }

                IEnumerable<object> objects = graph;
                // Drive the native serializer coroutine via the engine's own synchronous runner
                // (Base.Core.Timing.RunUntilComplete) — it spins up a Timing + TimingScheduler so the
                // serializer's nested `yield return Timing.Current.Call(...)` work actually executes.
                // A bare while(MoveNext()) had no active scheduler ⇒ Timing.Current threw / nested work
                // never ran ⇒ dest stayed null (RCA 2026-06-18). Serializer.Write returns
                // IEnumerator<NextUpdate>, matching RunUntilComplete's param (verified ReportIssueData.cs:33).
                var coroutine = (IEnumerator<NextUpdate>)write.Invoke(serializer, new object[] { objects, ".b", dest, slice });
                Timing.RunUntilComplete(coroutine);

                var valueField = byRefBytes.GetField("Value");
                byte[] outBytes = valueField?.GetValue(dest) as byte[];

                // PROBE: output length + checksum (host-sent bytes, compare to client-received bytesHash).
                if (!quiet)
                    Debug.Log("[Multiplayer][tac] Write output destLen=" + (outBytes?.Length ?? -1) +
                              " bytesHash " + BytesHash(outBytes));

                // PROBE: in-process host self-roundtrip. Read our OWN bytes right back. If this returns a
                // non-empty graph the Read contract is sound and the empty-on-client is an env/type problem;
                // if it returns null the contract itself is broken. Guarded so it runs once (not inside the
                // self-test's own DeserializeGraph — which never calls back into SerializeGraph anyway).
                if (outBytes != null && !_inSelfRoundtrip && !quiet)
                {
                    _inSelfRoundtrip = true;
                    try
                    {
                        Type elemType = (graph != null && graph.Length > 0 && graph[0] != null) ? graph[0].GetType() : null;
                        object self = DeserializeGraph(outBytes, elemType);
                        Debug.Log("[Multiplayer][tac] HOST self-roundtrip result=" +
                                  (self == null ? "NULL" : self.GetType().Name) + " expectedType=" +
                                  (elemType?.Name ?? "any"));
                    }
                    catch (Exception sx) { Debug.LogError("[Multiplayer][tac] HOST self-roundtrip threw: " + sx); }
                    finally { _inSelfRoundtrip = false; }
                }

                return outBytes;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] SerializeGraph failed: " + ex); return null; }
        }

        // quiet=true (PersonnelBlob): skip the probe logs (per-soldier hourly reads would spam).
        internal static object DeserializeGraph(byte[] bytes, Type expectedType, bool quiet = false)
        {
            EnsureReflection();
            if (_serializerType == null || _byRefType == null || _timeSliceType == null) return null;
            if (bytes == null || bytes.Length == 0) return null;
            try
            {
                // PROBE: checksum of the bytes handed to Read (client-received OR host self-test input).
                // Compare to the host "Write output bytesHash" to confirm byte-identity end-to-end.
                if (!quiet)
                    Debug.Log("[Multiplayer][tac] Read input bytesHash " + BytesHash(bytes) +
                              " expectedType=" + (expectedType?.Name ?? "any"));
                // MUST be the same engine-configured Serializer the host wrote with (Context =
                // SerializationComponent). A null-Context Serializer NREs in BaseDef.ResolveOrCreateBaseDef
                // while reconstructing any Def ref → silent abort → empty graph. See ResolveGameSerializer.
                object serializer = ResolveGameSerializer();
                if (serializer == null) { Debug.LogError("[Multiplayer][tac] DeserializeGraph: no game Serializer — returning null"); return null; }
                // ByRef<IEnumerable<object>> outRef — same single-optional-ctor trap as SerializeGraph's
                // ByRef<byte[]>: pass the arg explicitly (default == null), else Activator.CreateInstance
                // throws MissingMethodException. (Would have blown up on the CLIENT right after the host
                // serialize bug was cleared.)
                Type byRefEnum = _byRefType.MakeGenericType(typeof(IEnumerable<object>));
                object outRef = Activator.CreateInstance(byRefEnum, new object[] { null });
                object slice = Activator.CreateInstance(_timeSliceType, new object[] { 3600f });

                // Read(ByRef<IEnumerable<object>> objects, TimeSlice slice, string formatExt, byte[] srcData, string section=null)
                var read = AccessTools.Method(_serializerType, "Read",
                    new[] { byRefEnum, _timeSliceType, typeof(string), typeof(byte[]), typeof(string) });
                if (read == null) { Debug.LogError("[Multiplayer][tac] Serializer.Read(byte[]) overload not found"); return null; }

                // Same engine-synchronous driver as the host Write path (see SerializeGraph): the native
                // Read coroutine yields nested Timing.Current.Call(...) work that needs a live scheduler.
                var coroutine = (IEnumerator<NextUpdate>)read.Invoke(serializer, new object[] { outRef, slice, ".b", bytes, null });
                Timing.RunUntilComplete(coroutine);

                var valueField = byRefEnum.GetField("Value");
                var result = valueField?.GetValue(outRef) as IEnumerable<object>;

                // PROBE (moved BEFORE the null-guard so it ALWAYS fires): distinguish empty-graph (Value==null)
                // from a typed-but-mismatched graph. Materialize once when non-null; reuse below.
                List<object> graphList = result == null ? null : new List<object>(result);
                if (!quiet)
                    Debug.Log("[Multiplayer][tac] Read graph " +
                              (result == null ? "NULL (ByRef.Value==null → empty graph)"
                                              : ("count=" + graphList.Count + " types=[" +
                                                 string.Join(",", graphList.ConvertAll(o => o?.GetType().Name).ToArray()) + "]")));
                if (result == null) return null;
                foreach (var o in graphList)
                    if (o != null && (expectedType == null || expectedType.IsInstanceOfType(o))) return o;
                return null;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] DeserializeGraph failed: " + ex); return null; }
        }

        // ─── Actor enumeration + identity helpers ──────────────────────────

        /// <summary>Enumerate every live tactical actor as an <see cref="IActorRef"/> (adapter wrapping
        /// TacticalActorBase): GeoUnitId via the struct's int conversion, position via actor.Pos.</summary>
        private static List<IActorRef> EnumerateActorRefs(object tacticalLevelController)
        {
            var list = new List<IActorRef>();
            object map = GetProp(tacticalLevelController, "Map");
            if (map == null) return list;
            // TacticalMap.GetActors<TacticalActorBase>() (inherited from BaseMap).
            var getActors = AccessTools.Method(map.GetType(), "GetActors", null, new[] { _tacActorBaseType });
            if (getActors == null) { Debug.LogError("[Multiplayer][tac] GetActors<TacticalActorBase> not found"); return list; }
            var actors = getActors.Invoke(map, new object[] { null }) as IEnumerable;
            if (actors == null) return list;
            foreach (var a in actors)
                if (a != null) list.Add(new TacticalActorAdapter(a));
            return list;
        }

        /// <summary>DIAG: compact list of the CURRENT live-map actor GeoUnitIds (+ count) so a "no actor for
        /// netId N" failure log can show which soldiers/actors ARE present — revealing a late-spawn / GeoUnitId
        /// mismatch at a glance on the next in-game run. Best-effort; never throws to the caller.</summary>
        public static string DescribeLiveActorIds()
        {
            object tlc = LiveTlc;
            if (tlc == null) return "liveGeoIds=<no-live-tlc>";
            try
            {
                var ids = new List<int>();
                foreach (var aref in EnumerateActorRefs(tlc))
                    if (aref is TacticalActorAdapter a) ids.Add(a.GeoUnitId);
                ids.Sort();
                return "liveGeoIds[" + ids.Count + "]=" + string.Join(",", ids.ConvertAll(i => i.ToString()).ToArray());
            }
            catch (Exception ex) { return "liveGeoIds=<err:" + ex.Message + ">"; }
        }

        /// <summary>TS1: enumerate the LIVE map actor objects (unwrapped <c>TacticalActorBase</c>) for the current
        /// mission — the reference set the despawn sweep compares the registry against. Empty off-mission.</summary>
        public static List<object> HostLiveActorObjects()
        {
            var result = new List<object>();
            object tlc = LiveTlc;
            if (tlc == null) return result;
            foreach (var aref in EnumerateActorRefs(tlc))
                if (aref is TacticalActorAdapter ad && ad.Actor != null) result.Add(ad.Actor);
            return result;
        }

        /// <summary>
        /// CLIENT (FIX 2): destroy every restored actor that did NOT match a host actorTable row — i.e. a
        /// client-only extra produced by the client's own fresh deploy roll. Uses the grounded native removal
        /// <c>Base.Entities.ActorSpawner.DestroyActor(ActorComponent)</c> (RemoveActorEffect.cs:29 →
        /// ActorSpawner.cs:30: OnExitPlay + Destroy GameObject). Returns the count removed. Best-effort: a
        /// failure to destroy one actor never aborts the hydrate.
        /// </summary>
        private static int ReconcileUnmatchedActors(List<IActorRef> restored)
        {
            int removed = 0;
            if (restored == null) return 0;
            var destroy = ResolveDestroyActor();
            foreach (var a in restored)
            {
                if (a == null) continue;
                if (Registry.NetIdOf(a).HasValue) continue;   // matched to a host row → keep
                var adapter = a as TacticalActorAdapter;
                object actor = adapter?.Actor;
                if (actor == null) continue;
                try
                {
                    if (destroy != null) { destroy.Invoke(null, new[] { actor }); removed++; }
                    else Debug.LogError("[Multiplayer][tac] ReconcileUnmatchedActors: ActorSpawner.DestroyActor not found — extra actor left in place");
                }
                catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ReconcileUnmatchedActors: destroy failed: " + ex); }
            }
            if (removed > 0) Debug.Log("[Multiplayer][tac] CLIENT removed " + removed + " unmatched (client-rolled) actor(s)");
            return removed;
        }

        /// <summary>CLIENT: live actors NOT yet bound in the registry (reference-identity on the wrapped
        /// actor — a fresh adapter has no value-equality with the registry's stored one). The shared
        /// candidate builder for lazy rebind + the rematch retry.</summary>
        private static List<IActorRef> UnboundLiveCandidates(TacticalActorRegistry reg, object tlc)
        {
            var known = new HashSet<object>();
            foreach (var kv in reg.Entries)
                if (kv.Value is TacticalActorAdapter a && a.Actor != null) known.Add(a.Actor);
            var candidates = new List<IActorRef>();
            foreach (var aref in EnumerateActorRefs(tlc))
            {
                object actor = (aref as TacticalActorAdapter)?.Actor;
                if (actor == null || known.Contains(actor)) continue;
                candidates.Add(aref);
            }
            return candidates;
        }

        /// <summary>CLIENT: drive every table row whose netId is bound to the host's deploy position via
        /// <see cref="TacticalMoveSync.ApplyMirrorPosition"/> (no-op when already within epsilon). Returns
        /// the count actually moved/snapped. Direct registry read — never the lazy-rebinding resolver.</summary>
        private static int ApplyDeployPositions(List<TacticalActorRegistry.ActorRow> rows)
        {
            int applied = 0;
            if (rows == null || Registry == null) return 0;
            foreach (var row in rows)
            {
                object actor = Registry.TryGet(row.NetId, out var aref) && aref is TacticalActorAdapter ad ? ad.Actor : null;
                if (actor == null) continue;
                try { if (TacticalMoveSync.ApplyMirrorPosition(actor, new Vector3(row.X, row.Y, row.Z), forceSnap: true)) applied++; }
                catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ApplyDeployPositions failed for netId " + row.NetId + ": " + ex); }
            }
            return applied;
        }

        /// <summary>CLIENT: apply the retained host deploy position (+DIAG) to ONE late-bound actor.
        /// Position only — the deploy table carries no facing; facing converges via the actorstate rail.</summary>
        private static void ApplyStoredDeployPos(int netId, object actor)
        {
            var table = _missionActorTable;
            if (table == null || actor == null) return;
            foreach (var row in table)
            {
                if (row.NetId != netId) continue;
                bool applied = false;
                try { applied = TacticalMoveSync.ApplyMirrorPosition(actor, new Vector3(row.X, row.Y, row.Z), forceSnap: true); }
                catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ApplyStoredDeployPos failed: " + ex); }
                string name = GetProp(actor, "DisplayName") as string ?? actor.GetType().Name;
                Debug.Log("[Multiplayer][tac][DIAG] late-bind netId=" + netId + " '" + name + "' applied deploy pos=(" +
                          row.X.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "," +
                          row.Y.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "," +
                          row.Z.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ") applied=" + applied);
                return;
            }
        }

        // Rematch retry cadence: ~1 s between attempts (frame-based, same NextFrame pump as the other
        // deploy coroutines), hard-capped. ponytail: frame-counted seconds assume ~60 fps; good enough
        // for a retry cadence — switch to realtime if it ever matters.
        private const int RematchMaxTries = 10;
        private const int RematchIntervalFrames = 60;

        /// <summary>CLIENT: bounded proactive rematch for actors that spawned AFTER the one-shot hydrate
        /// match — once per ~second, up to <see cref="RematchMaxTries"/>, until the full host table is
        /// bound. Newly bound actors immediately get their host deploy position. Self-stops on mission
        /// exit (watches <see cref="_missionActorTable"/>/<see cref="LiveTlc"/>, both nulled there).</summary>
        private static IEnumerator<NextUpdate> ClientRematchCrt()
        {
            for (int attempt = 1; attempt <= RematchMaxTries; attempt++)
            {
                for (int f = 0; f < RematchIntervalFrames; f++) yield return NextUpdate.NextFrame;

                var table = _missionActorTable;
                var reg = Registry;
                object tlc = LiveTlc;
                if (table == null || reg == null || tlc == null) yield break;   // mission exited → self-stop

                var unmatched = new List<TacticalActorRegistry.ActorRow>();
                foreach (var row in table)
                    if (!reg.TryGet(row.NetId, out _)) unmatched.Add(row);
                if (unmatched.Count == 0)
                {
                    Debug.Log("[Multiplayer][tac] rematch: full actor table bound (attempt " + attempt + ")");
                    yield break;
                }

                int bound = 0, placedNow = 0;
                try
                {
                    var candidates = UnboundLiveCandidates(reg, tlc);
                    if (candidates.Count > 0) bound = reg.MatchAndRegister(unmatched, candidates);
                    if (bound > 0) placedNow = ApplyDeployPositions(unmatched);
                }
                catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ClientRematchCrt attempt " + attempt + " failed: " + ex); }
                if (bound > 0)
                    Debug.Log("[Multiplayer][tac] rematch attempt " + attempt + ": bound " + bound +
                              " late actor(s), placed " + placedNow + ", stillUnmatched=" + (unmatched.Count - bound));
            }
            Debug.LogWarning("[Multiplayer][tac] rematch: gave up after " + RematchMaxTries +
                             " attempts — remaining actors rely on lazy rebind");
        }

        private static MethodInfo _destroyActorMethod;
        private static bool _destroyActorResolved;
        private static MethodInfo ResolveDestroyActor()
        {
            if (_destroyActorResolved) return _destroyActorMethod;
            _destroyActorResolved = true;
            var spawnerType = AccessTools.TypeByName("Base.Entities.ActorSpawner");
            // public static void DestroyActor(ActorComponent actor)
            _destroyActorMethod = spawnerType != null ? AccessTools.Method(spawnerType, "DestroyActor") : null;
            return _destroyActorMethod;
        }

        /// <summary>Resolve the mission's <c>GeoSite.SiteId</c> — the stable cross-instance identifier (the
        /// shared campaign save gives the SAME SiteId on host + client). Grounded path: the geoscape
        /// <c>GeoLevelController.Map.AllSites</c>, find the site whose <c>ActiveMission</c> is non-null and
        /// currently launching/active (the one that triggered this tactical level), read its
        /// <c>SiteId</c>. (TacMissionData.MissionId is a per-launch GUID → NOT cross-instance, so unused.)</summary>
        private static int ResolveMissionSiteId(object tacticalLevelController)
        {
            try
            {
                object mission = FindActiveMissionSite(out int siteId);
                if (mission != null) return siteId;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ResolveMissionSiteId failed: " + ex); }
            return -1;
        }

        private static object ResolveGeoMissionBySiteId(object geo, int siteId)
        {
            // Find the GeoSite with this SiteId among Map.AllSites, return its ActiveMission.
            try
            {
                foreach (var site in AllSites(geo))
                {
                    var f = AccessTools.Field(site.GetType(), "SiteId");
                    if (f != null && (int)f.GetValue(site) == siteId)
                        return GetProp(site, "ActiveMission");
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ResolveGeoMissionBySiteId failed: " + ex); }
            return null;
        }

        /// <summary>Scan Map.AllSites for the first site carrying an ActiveMission (the launching mission).
        /// Returns the GeoMission and its site's SiteId via out-param.</summary>
        private static object FindActiveMissionSite(out int siteId)
        {
            siteId = -1;
            var geo = GeoLevelController();
            if (geo == null) return null;
            foreach (var site in AllSites(geo))
            {
                object mission = GetProp(site, "ActiveMission");
                if (mission == null) continue;
                var f = AccessTools.Field(site.GetType(), "SiteId");
                siteId = f != null ? (int)f.GetValue(site) : -1;
                return mission;
            }
            return null;
        }

        /// <summary>Enumerate <c>GeoLevelController.Map.AllSites</c> (grounded accessor).</summary>
        private static IEnumerable<object> AllSites(object geo)
        {
            object map = GetProp(geo, "Map");
            var sites = map != null ? GetProp(map, "AllSites") as IEnumerable : null;
            if (sites == null) yield break;
            foreach (var s in sites) if (s != null) yield return s;
        }

        private static object InvokePrepareTacticalGame(object mission, object site, object squad)
        {
            // protected PlayTacticalGameLevelResult PrepareTacticalGame(GeoSite site, GeoSquad squad).
            // squad MUST be non-null (the native body derefs squad.Units at GeoMission.cs:325).
            var m = AccessTools.Method(mission.GetType(), "PrepareTacticalGame");
            if (m == null) return null;
            var pars = m.GetParameters();
            object[] args;
            if (pars.Length == 2) args = new[] { site, squad };
            else { args = new object[pars.Length]; if (pars.Length >= 1) args[0] = site; if (pars.Length >= 2) args[1] = squad; }
            return m.Invoke(mission, args);
        }

        /// <summary>
        /// CLIENT (FIX 2): resolve the REAL <c>GeoSquad</c> that will fight this mission, so the native
        /// <c>PrepareTacticalGame(site, squad)</c> doesn't NRE on <c>squad.Units</c> (GeoMission.cs:325).
        /// Grounded resolution chain (canonical launch path = GeoSite.cs:1173-1174 + GeoMission.cs:141/1517/676):
        ///   1. <c>mission.Squad</c> — if the mission was already <c>Launch()</c>'d locally, reuse it.
        ///   2. else find a viewer-owned, non-travelling <c>GeoVehicle</c> at the site and build
        ///      <c>new GeoSquad(mission.GetDefaultDeploymentSetup(vehicle.Owner, vehicle))</c> — exactly how
        ///      the engine's own <c>launch_mission</c> console path forms the squad.
        /// Returns null only if no squad can be formed (caller aborts rather than NRE).
        /// </summary>
        private static object ResolveClientSquad(object mission, object site)
        {
            try
            {
                // 1) Mission already carries a squad (locally launched / primed) → use it.
                object existing = GetProp(mission, "Squad");
                if (existing != null) { Debug.Log("[Multiplayer][tac] ResolveClientSquad: using mission.Squad"); return existing; }

                // 2) Build the default deployment squad from a viewer-owned vehicle at the site.
                object vehicle = FindViewerVehicleAtSite(site);
                if (vehicle == null)
                {
                    Debug.LogError("[Multiplayer][tac] ResolveClientSquad: no viewer-owned vehicle at site");
                    return null;
                }
                object owner = GetProp(vehicle, "Owner");

                // GetDefaultDeploymentSetup(GeoFaction faction, IGeoCharacterContainer priorityContainer=null)
                // → IEnumerable<GeoCharacter>; the vehicle is the priority container.
                var getSetup = ResolveGetDefaultDeploymentSetup(mission.GetType());
                if (getSetup == null) { Debug.LogError("[Multiplayer][tac] ResolveClientSquad: GetDefaultDeploymentSetup(GeoFaction,…) not found"); return null; }
                var setupPars = getSetup.GetParameters();
                object[] setupArgs = setupPars.Length >= 2 ? new[] { owner, vehicle } : new[] { owner };
                object deployment = getSetup.Invoke(mission, setupArgs);
                if (deployment == null) { Debug.LogError("[Multiplayer][tac] ResolveClientSquad: deployment set is null"); return null; }

                // new GeoSquad(IEnumerable<GeoCharacter>)
                var squadType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSquad");
                if (squadType == null) { Debug.LogError("[Multiplayer][tac] ResolveClientSquad: GeoSquad type not found"); return null; }
                object squad = Activator.CreateInstance(squadType, new[] { deployment });
                Debug.Log("[Multiplayer][tac] ResolveClientSquad: built squad from vehicle deployment setup");
                return squad;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ResolveClientSquad failed: " + ex); return null; }
        }

        /// <summary>Pick the <c>GetDefaultDeploymentSetup(GeoFaction, IGeoCharacterContainer)</c> overload
        /// (NOT the <c>GetDefaultDeploymentSetup(IEnumerable&lt;GeoCharacter&gt;)</c> one). Disambiguate by a
        /// first parameter named/typed like a faction.</summary>
        private static MethodInfo ResolveGetDefaultDeploymentSetup(Type missionType)
        {
            MethodInfo best = null;
            foreach (var m in missionType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "GetDefaultDeploymentSetup") continue;
                var pars = m.GetParameters();
                if (pars.Length >= 1 && pars[0].ParameterType.Name.Contains("GeoFaction")) return m;
                if (best == null && pars.Length >= 1 && pars[0].ParameterType.Name.Contains("Faction")) best = m;
            }
            return best;
        }

        /// <summary>Find a viewer-owned, non-travelling <c>GeoVehicle</c> docked at the site (the aircraft that
        /// brought the squad). Grounded: <c>GeoSite.Vehicles</c> where <c>v.IsOwnedByViewer &amp;&amp; !v.Travelling</c>
        /// (mirrors GeoMission.GetLocalAircraft, GeoMission.cs:678).</summary>
        private static object FindViewerVehicleAtSite(object site)
        {
            try
            {
                var vehicles = GetProp(site, "Vehicles") as IEnumerable;
                if (vehicles == null) return null;
                object fallback = null;
                foreach (var v in vehicles)
                {
                    if (v == null) continue;
                    bool ownedByViewer = ToBool(GetProp(v, "IsOwnedByViewer"));
                    bool travelling = ToBool(GetProp(v, "Travelling"));
                    if (ownedByViewer && !travelling) return v;
                    if (fallback == null && ownedByViewer) fallback = v;
                }
                return fallback;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] FindViewerVehicleAtSite failed: " + ex); return null; }
        }

        private static bool ToBool(object o) => o is bool b && b;

        // ─── Small reflection utilities ────────────────────────────────────
        private static object GeoLevelController()
        {
            // Reuse GeoRuntime's resolution (GameUtl.CurrentLevel → GeoLevelController component).
            return Network.Sync.GeoRuntime.Instance.GeoLevel();
        }

        /// <summary>The live <c>TacticalLevelController</c> if the current level is a tactical level, else
        /// null. Mirrors <c>GeoRuntime.GeoLevel()</c>: <c>GameUtl.CurrentLevel()</c> → GetComponent of the
        /// tactical-level type. Used by the deploy ARRIVAL gate to detect the real co-op flow, where the
        /// client is already in its tactical level when the host's chunked deploy reassembles.</summary>
        private static object LiveTacticalLevelController()
        {
            EnsureReflection();
            if (_tlcType == null) return null;
            try
            {
                var level = Network.Sync.GeoRuntime.Instance.CurrentLevel();
                if (level is UnityEngine.Component comp)
                    return comp.GetComponent(_tlcType);   // null if current level isn't tactical
                return null;
            }
            catch { return null; }
        }

        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            var p = AccessTools.Property(obj.GetType(), name);
            if (p != null) return p.GetValue(obj, null);
            var f = AccessTools.Field(obj.GetType(), name);
            return f?.GetValue(obj);
        }

        private static object Invoke(object obj, string method)
        {
            var m = AccessTools.Method(obj.GetType(), method);
            return m?.Invoke(obj, null);
        }

        private static void DumpActorTable(string side, List<TacticalActorRegistry.ActorRow> table)
        {
            // Inc-1 runtime-verification (spec §4/§8): dump GeoUnitId→NetId so the 2-instance test can
            // confirm a 1:1 identical mapping on host + client.
            var sb = new System.Text.StringBuilder();
            sb.Append("[Multiplayer][tac] ").Append(side).Append(" actorTable:");
            foreach (var r in table)
                sb.Append(" {net=").Append(r.NetId).Append(",geo=").Append(r.GeoUnitId)
                  .Append(",pos=(").Append(r.X.ToString("0.0")).Append(',').Append(r.Y.ToString("0.0"))
                  .Append(',').Append(r.Z.ToString("0.0")).Append(")}");
            Debug.Log(sb.ToString());
        }
    }

    /// <summary>Live adapter: wraps a <c>TacticalActorBase</c> as the engine-free <see cref="IActorRef"/>
    /// the pure registry consumes. GeoUnitId via the <c>GeoTacUnitId</c> struct's implicit int conversion
    /// (read through its private <c>_id</c> field for robustness); position via <c>actor.Pos</c>.</summary>
    public sealed class TacticalActorAdapter : IActorRef
    {
        private readonly object _actor;
        public object Actor => _actor;

        public TacticalActorAdapter(object actor) { _actor = actor; }

        public int GeoUnitId
        {
            get
            {
                try
                {
                    var p = AccessTools.Property(_actor.GetType(), "GeoUnitId");
                    object gid = p?.GetValue(_actor, null);
                    if (gid == null) return 0;
                    // GeoTacUnitId._id (private readonly int)
                    var idField = AccessTools.Field(gid.GetType(), "_id");
                    if (idField != null) return (int)idField.GetValue(gid);
                    // Fallback: implicit int conversion operator.
                    return Convert.ToInt32(gid);
                }
                catch { return 0; }
            }
        }

        public ActorPos Position
        {
            get
            {
                try
                {
                    var p = AccessTools.Property(_actor.GetType(), "Pos");
                    object posObj = p?.GetValue(_actor, null);
                    if (posObj is Vector3 v) return new ActorPos(v.x, v.y, v.z);
                }
                catch { }
                return new ActorPos(0, 0, 0);
            }
        }
    }
}
