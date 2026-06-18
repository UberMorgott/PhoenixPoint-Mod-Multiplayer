using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Network.MessageLayer;
using UnityEngine;

namespace Multipleer.Sync.Tactical
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

        // Client re-entrancy: true only while OUR own ClientLaunchMission is driving the native
        // LaunchTacticalGame. The LaunchTacticalGame prefix uses this to ALLOW the deploy-driven launch
        // while GATING (blocking) any spontaneous client-initiated launch (spec §8: client never self-launches).
        private static bool _clientLaunchInProgress;
        public static bool ClientLaunchInProgress => _clientLaunchInProgress;

        // ─── Reflection cache ──────────────────────────────────────────────
        private static Type _tlcType;          // TacticalLevelController
        private static Type _tacActorBaseType; // TacticalActorBase
        private static Type _tacGameParamsType;// TacticalGameParams
        private static Type _tacLevelInstType; // TacLevelInstanceData
        private static Type _serializerType;   // Base.Serialization.General.Serializer
        private static Type _byRefType;        // Base.Utils.ByRef<>
        private static Type _timeSliceType;    // Base.Utils.TimeSlice
        private static bool _reflectionReady;

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
                Debug.Log("[Multipleer][tac] OnTacticalLaunch site=" + _launchingSiteId);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] OnTacticalLaunch failed: " + ex); _launchingSiteId = -1; }
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
            if (!_reflectionReady) { Debug.LogError("[Multipleer][tac] HostOnLevelReady: reflection not ready"); return; }

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
                    Debug.LogError("[Multipleer][tac] HostOnLevelReady: could not schedule deferred capture — capturing immediately (may be early)");
                    HostCaptureAndBroadcast(tacticalLevelController);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer][tac] HostOnLevelReady failed: " + ex);
            }
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
        /// after OnLevelStart fully ran ⇒ level fully initialized).</summary>
        private static IEnumerator DeferredCaptureCrt(object tacticalLevelController)
        {
            int frames = 0;
            while (true)
            {
                bool ready = false;
                try { ready = ToBool(GetProp(tacticalLevelController, "HasAnyTurnStarted")); }
                catch (Exception ex) { Debug.LogError("[Multipleer][tac] DeferredCaptureCrt: read HasAnyTurnStarted failed: " + ex); }

                var decision = TacticalDeployReadinessGate.Decide(ready, frames, CaptureReadyMaxFrames);
                if (decision == TacticalDeployReadinessGate.Decision.CaptureReady ||
                    decision == TacticalDeployReadinessGate.Decision.CaptureTimeout)
                {
                    if (decision == TacticalDeployReadinessGate.Decision.CaptureTimeout)
                        Debug.LogError("[Multipleer][tac] DeferredCaptureCrt: readiness gate timed out after " +
                                       frames + " frames — capturing anyway (fail-safe)");
                    else
                        Debug.Log("[Multipleer][tac] DeferredCaptureCrt: level ready after " + frames +
                                  " frame(s) → capturing deploy");
                    HostCaptureAndBroadcast(tacticalLevelController);
                    yield break;
                }

                frames++;
                yield return TimingNextFrameValue();
            }
        }

        /// <summary>The native <c>NextUpdate.NextFrame</c> value a tactical coroutine yields between frames.
        /// Resolved by reflection (the mod has no compile-time game binding here); null falls back to a plain
        /// yield, which the game's Timing still advances one frame.</summary>
        private static object TimingNextFrameValue()
        {
            try
            {
                var nu = AccessTools.TypeByName("Base.Core.NextUpdate");
                var f = nu != null ? AccessTools.Field(nu, "NextFrame") : null;
                if (f != null) return f.GetValue(null);
                var p = nu != null ? AccessTools.Property(nu, "NextFrame") : null;
                if (p != null) return p.GetValue(null, null);
            }
            catch { }
            return null;
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
                if (best == null) { Debug.LogError("[Multipleer][tac] InvokeTimingStart: no Start overload found"); return false; }
                var bp = best.GetParameters();
                var args = new object[bp.Length];
                args[0] = crt;
                for (int i = 1; i < bp.Length; i++) args[i] = Type.Missing;
                best.Invoke(timing, args);
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] InvokeTimingStart failed: " + ex); return false; }
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
            if (!_reflectionReady) { Debug.LogError("[Multipleer][tac] HostCaptureAndBroadcast: reflection not ready"); return; }

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
                    Debug.LogError("[Multipleer][tac] HostCaptureAndBroadcast: null gameParams/snapshot — skipping deploy");
                    return;
                }

                byte[] gpBytes = SerializeGraph(new[] { gameParams });
                byte[] snapBytes = SerializeGraph(new[] { snapshot });
                if (gpBytes == null || snapBytes == null)
                {
                    Debug.LogError("[Multipleer][tac] HostCaptureAndBroadcast: native serialize failed — skipping deploy");
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
                LiveTlc = tacticalLevelController;       // live-rail handle for tac.move/tac.turn
                LiveSeq = new TacticalLiveSeq();         // fresh per-mission seq stream
                IntentDedup = new TacticalIntentDedup();
                Debug.Log("[Multipleer][tac] HOST broadcast tac.deploy site=" + siteId +
                          " gpBytes=" + gpBytes.Length + " snapBytes=" + snapBytes.Length +
                          " actors=" + table.Count);
                DumpActorTable("HOST", table);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer][tac] HostCaptureAndBroadcast failed: " + ex);
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
                Debug.LogError("[Multipleer][tac] OnDeployReceived: decode failed (" + (payload?.Length ?? 0) + " bytes)");
                return;
            }
            if (p.MissionSiteId == _hydratedSiteId) return;   // duplicate reliable double-send → ignore

            _pendingClientDeploy = p;
            Debug.Log("[Multipleer][tac] CLIENT received tac.deploy site=" + p.MissionSiteId +
                      " gpBytes=" + p.GameParamsBytes.Length + " snapBytes=" + p.SnapshotBytes.Length +
                      " actors=" + p.ActorTable.Count);

            try { ClientLaunchMission(p); }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] ClientLaunchMission failed: " + ex); }
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
            if (geo == null) { Debug.LogError("[Multipleer][tac] ClientLaunchMission: no GeoLevelController"); return; }

            object mission = ResolveGeoMissionBySiteId(geo, p.MissionSiteId);
            if (mission == null)
            {
                Debug.LogError("[Multipleer][tac] ClientLaunchMission: no GeoMission for site " + p.MissionSiteId +
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
                Debug.LogError("[Multipleer][tac] ClientLaunchMission: could not resolve a GeoSquad for site " +
                               p.MissionSiteId + " — aborting client launch (would NRE in PrepareTacticalGame)");
                return;
            }
            object gameParams = InvokePrepareTacticalGame(mission, site, squad);
            if (gameParams == null)
            {
                Debug.LogError("[Multipleer][tac] ClientLaunchMission: PrepareTacticalGame returned null");
                return;
            }
            // LaunchTacticalGame(GeoMission, PlayTacticalGameLevelResult). Flag this as OUR deploy-driven
            // launch so the gate prefix lets it through (a spontaneous client launch is blocked).
            var launch = AccessTools.Method(geo.GetType(), "LaunchTacticalGame");
            if (launch == null) { Debug.LogError("[Multipleer][tac] ClientLaunchMission: LaunchTacticalGame not found"); return; }
            _clientLaunchInProgress = true;
            try { launch.Invoke(geo, new[] { mission, gameParams }); }
            finally { _clientLaunchInProgress = false; }
            Debug.Log("[Multipleer][tac] CLIENT launched tactical mission for site " + p.MissionSiteId);
        }

        /// <summary>
        /// CLIENT: our tactical level reached Playing. If we have a pending host deploy, restore its snapshot
        /// (<c>ProcessInstanceData</c>), rebuild the NetId dict from the host actor table, and arm mirror mode.
        /// </summary>
        public static void ClientOnLevelReady(object tacticalLevelController)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return;
            if (_pendingClientDeploy == null || tacticalLevelController == null) return;
            EnsureReflection();

            var p = _pendingClientDeploy;
            try
            {
                // 1) Restore the full battle from the host snapshot (deserialize → ProcessInstanceData).
                object snapshotObj = DeserializeGraph(p.SnapshotBytes, _tacLevelInstType);
                if (snapshotObj == null)
                {
                    Debug.LogError("[Multipleer][tac] ClientOnLevelReady: snapshot deserialize failed");
                    return;
                }
                // ProcessInstanceData is private → AccessTools method invoke.
                var process = AccessTools.Method(_tlcType, "ProcessInstanceData", new[] { _tacLevelInstType });
                if (process == null) { Debug.LogError("[Multipleer][tac] ClientOnLevelReady: ProcessInstanceData not found"); return; }
                process.Invoke(tacticalLevelController, new[] { snapshotObj });

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

                // 3) Arm mirror mode (frozen pure mirror).
                _mirrorArmed = true;
                _hydratedSiteId = p.MissionSiteId;
                _pendingClientDeploy = null;
                LiveTlc = tacticalLevelController;   // live-rail handle for applying tac.move/tac.turn
                LiveSeq = new TacticalLiveSeq();     // fresh per-mission client guard

                // 4) Enter the INITIAL turn from the restored snapshot (ProcessInstanceData already set the
                //    snapshot's _currentFactionIndex). If turn 0 is the player faction, the client must enter
                //    its player turn now — a host tac.turn for turn 0 may have been broadcast before this
                //    client finished hydrating (it has no LiveTlc yet → dropped), so we self-enter here.
                try { TacticalTurnSync.ClientEnterInitialTurn(tacticalLevelController); }
                catch (Exception ex) { Debug.LogError("[Multipleer][tac] ClientEnterInitialTurn failed: " + ex); }

                Debug.Log("[Multipleer][tac] CLIENT hydrated tac.deploy site=" + p.MissionSiteId +
                          " matched=" + matched + "/" + p.ActorTable.Count + " removedExtras=" + removed +
                          " mirror=ARMED");
                DumpActorTable("CLIENT", p.ActorTable);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer][tac] ClientOnLevelReady failed: " + ex);
            }
        }

        /// <summary>Disarm mirror mode + clear per-mission state (mission exit). Idempotent.</summary>
        public static void OnMissionExit()
        {
            _mirrorArmed = false;
            _pendingClientDeploy = null;
            _lastBroadcastSiteId = int.MinValue;
            _captureScheduledSiteId = int.MinValue;
            _hydratedSiteId = int.MinValue;
            _chunkReassembler = new ChunkReassembler();   // drop any half-received chunk set
            Registry = new TacticalActorRegistry();
            LiveTlc = null;
            LiveSeq = new TacticalLiveSeq();
            IntentDedup = new TacticalIntentDedup();
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
        private static bool HandleTacticalEnvelope(byte surfaceId, byte[] payload)
        {
            if (surfaceId == (byte)TacticalSurfaceIds.TacDeploy)
            {
                try { OnDeployReceived(payload); } catch (Exception ex) { Debug.LogError("[Multipleer][tac] inbound failed: " + ex); }
                return true;
            }
            if (surfaceId == (byte)TacticalSurfaceIds.TacDeployChunk)
            {
                try
                {
                    if (TacticalDeployChunkCodec.TryDecode(payload, out var frag))
                    {
                        byte[] full = _chunkReassembler.Accept(frag);
                        if (full != null)
                        {
                            Debug.Log("[Multipleer][tac] CLIENT reassembled tac.deploy site=" + frag.SiteId +
                                      " gen=" + frag.DeployGeneration + " chunks=" + frag.ChunkCount +
                                      " totalLen=" + full.Length);
                            OnDeployReceived(full);
                        }
                    }
                    else Debug.LogError("[Multipleer][tac] inbound chunk decode failed (" + (payload?.Length ?? 0) + " bytes)");
                }
                catch (Exception ex) { Debug.LogError("[Multipleer][tac] inbound chunk failed: " + ex); }
                return true;
            }

            // ─── LIVE outcome / intent rail (Inc 2/4) ─────────────────────────────────────────────
            // Intents (client→host) land on the host; outcomes (host→all) land on clients. Each handler
            // is side-guarded internally, so a stray envelope on the wrong side is a clean no-op.
            if (surfaceId == (byte)TacticalSurfaceIds.TacIntentMove)
            {
                try { TacticalMoveSync.HostOnMoveIntent(payload); } catch (Exception ex) { Debug.LogError("[Multipleer][tac] tac.intent.move failed: " + ex); }
                return true;
            }
            if (surfaceId == (byte)TacticalSurfaceIds.TacMove)
            {
                try { TacticalMoveSync.ClientOnMove(payload); } catch (Exception ex) { Debug.LogError("[Multipleer][tac] tac.move failed: " + ex); }
                return true;
            }
            if (surfaceId == (byte)TacticalSurfaceIds.TacIntentEndTurn)
            {
                try { TacticalTurnSync.HostOnEndTurnIntent(payload); } catch (Exception ex) { Debug.LogError("[Multipleer][tac] tac.intent.endturn failed: " + ex); }
                return true;
            }
            if (surfaceId == (byte)TacticalSurfaceIds.TacTurn)
            {
                try { TacticalTurnSync.ClientOnTurn(payload); } catch (Exception ex) { Debug.LogError("[Multipleer][tac] tac.turn failed: " + ex); }
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
                // the handler is site-id idempotent).
                byte[] envelope = Network.Sync.SyncProtocol.EncodeEnvelope(
                    (byte)TacticalSurfaceIds.TacDeploy, Network.Sync.SyncKind.StateSnapshot, payload);
                engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope, envelope));
                return;
            }

            // Over the cap → chunk. Each fragment is its own TacDeployChunk envelope; the client reassembles
            // by (siteId,generation), order-independent + duplicate-idempotent.
            var chunks = TacticalDeployChunkCodec.Split(siteId, generation, payload);
            foreach (var chunkPayload in chunks)
            {
                byte[] envelope = Network.Sync.SyncProtocol.EncodeEnvelope(
                    (byte)TacticalSurfaceIds.TacDeployChunk, Network.Sync.SyncKind.StateSnapshot, chunkPayload);
                engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope, envelope));
            }
            Debug.Log("[Multipleer][tac] HOST chunked tac.deploy site=" + siteId + " gen=" + generation +
                      " totalLen=" + payload.Length + " chunks=" + chunks.Count);
        }

        // ─── Native Serializer round-trip (coroutine-pumped to completion) ─────────────────
        // The game Serializer is a coroutine (IEnumerator<NextUpdate>); off the main mission flow we drive
        // it synchronously with a generous TimeSlice so it completes in one pump. Verified API (decompile
        // 2026-06-17): new Serializer(object context); Write(IEnumerable<object>, string ".b",
        // ByRef<byte[]> dest, TimeSlice) :562; Read(ByRef<IEnumerable<object>>, TimeSlice, string ".b",
        // byte[] src, string section=null) :700. ByRef<T>.Value field; new TimeSlice(float seconds).

        private static byte[] SerializeGraph(object[] graph)
        {
            EnsureReflection();
            if (_serializerType == null || _byRefType == null || _timeSliceType == null) return null;
            try
            {
                object serializer = Activator.CreateInstance(_serializerType, new object[] { (object)null });
                // ByRef<byte[]> dest
                Type byRefBytes = _byRefType.MakeGenericType(typeof(byte[]));
                object dest = Activator.CreateInstance(byRefBytes);
                // TimeSlice slice = new TimeSlice(large) → effectively unbounded single pump.
                object slice = Activator.CreateInstance(_timeSliceType, new object[] { 3600f });

                var write = AccessTools.Method(_serializerType, "Write",
                    new[] { typeof(IEnumerable<object>), typeof(string), byRefBytes, _timeSliceType });
                if (write == null) { Debug.LogError("[Multipleer][tac] Serializer.Write(byte[]) overload not found"); return null; }

                IEnumerable<object> objects = graph;
                var en = (IEnumerator)write.Invoke(serializer, new object[] { objects, ".b", dest, slice });
                Pump(en);

                var valueField = byRefBytes.GetField("Value");
                return valueField?.GetValue(dest) as byte[];
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] SerializeGraph failed: " + ex); return null; }
        }

        private static object DeserializeGraph(byte[] bytes, Type expectedType)
        {
            EnsureReflection();
            if (_serializerType == null || _byRefType == null || _timeSliceType == null) return null;
            if (bytes == null || bytes.Length == 0) return null;
            try
            {
                object serializer = Activator.CreateInstance(_serializerType, new object[] { (object)null });
                Type byRefEnum = _byRefType.MakeGenericType(typeof(IEnumerable<object>));
                object outRef = Activator.CreateInstance(byRefEnum);
                object slice = Activator.CreateInstance(_timeSliceType, new object[] { 3600f });

                // Read(ByRef<IEnumerable<object>> objects, TimeSlice slice, string formatExt, byte[] srcData, string section=null)
                var read = AccessTools.Method(_serializerType, "Read",
                    new[] { byRefEnum, _timeSliceType, typeof(string), typeof(byte[]), typeof(string) });
                if (read == null) { Debug.LogError("[Multipleer][tac] Serializer.Read(byte[]) overload not found"); return null; }

                var en = (IEnumerator)read.Invoke(serializer, new object[] { outRef, slice, ".b", bytes, null });
                Pump(en);

                var valueField = byRefEnum.GetField("Value");
                var result = valueField?.GetValue(outRef) as IEnumerable<object>;
                if (result == null) return null;
                foreach (var o in result)
                    if (o != null && (expectedType == null || expectedType.IsInstanceOfType(o))) return o;
                return null;
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] DeserializeGraph failed: " + ex); return null; }
        }

        // Drive a NextUpdate coroutine to completion. With a 1-hour TimeSlice it should finish in one pump,
        // but loop defensively (bounded) in case the serializer yields.
        private static void Pump(IEnumerator en)
        {
            if (en == null) return;
            int guard = 0;
            while (en.MoveNext())
            {
                if (++guard > 1_000_000)
                {
                    Debug.LogError("[Multipleer][tac] Serializer pump exceeded guard — aborting");
                    break;
                }
            }
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
            if (getActors == null) { Debug.LogError("[Multipleer][tac] GetActors<TacticalActorBase> not found"); return list; }
            var actors = getActors.Invoke(map, new object[] { null }) as IEnumerable;
            if (actors == null) return list;
            foreach (var a in actors)
                if (a != null) list.Add(new TacticalActorAdapter(a));
            return list;
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
                    else Debug.LogError("[Multipleer][tac] ReconcileUnmatchedActors: ActorSpawner.DestroyActor not found — extra actor left in place");
                }
                catch (Exception ex) { Debug.LogError("[Multipleer][tac] ReconcileUnmatchedActors: destroy failed: " + ex); }
            }
            if (removed > 0) Debug.Log("[Multipleer][tac] CLIENT removed " + removed + " unmatched (client-rolled) actor(s)");
            return removed;
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
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] ResolveMissionSiteId failed: " + ex); }
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
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] ResolveGeoMissionBySiteId failed: " + ex); }
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
                if (existing != null) { Debug.Log("[Multipleer][tac] ResolveClientSquad: using mission.Squad"); return existing; }

                // 2) Build the default deployment squad from a viewer-owned vehicle at the site.
                object vehicle = FindViewerVehicleAtSite(site);
                if (vehicle == null)
                {
                    Debug.LogError("[Multipleer][tac] ResolveClientSquad: no viewer-owned vehicle at site");
                    return null;
                }
                object owner = GetProp(vehicle, "Owner");

                // GetDefaultDeploymentSetup(GeoFaction faction, IGeoCharacterContainer priorityContainer=null)
                // → IEnumerable<GeoCharacter>; the vehicle is the priority container.
                var getSetup = ResolveGetDefaultDeploymentSetup(mission.GetType());
                if (getSetup == null) { Debug.LogError("[Multipleer][tac] ResolveClientSquad: GetDefaultDeploymentSetup(GeoFaction,…) not found"); return null; }
                var setupPars = getSetup.GetParameters();
                object[] setupArgs = setupPars.Length >= 2 ? new[] { owner, vehicle } : new[] { owner };
                object deployment = getSetup.Invoke(mission, setupArgs);
                if (deployment == null) { Debug.LogError("[Multipleer][tac] ResolveClientSquad: deployment set is null"); return null; }

                // new GeoSquad(IEnumerable<GeoCharacter>)
                var squadType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSquad");
                if (squadType == null) { Debug.LogError("[Multipleer][tac] ResolveClientSquad: GeoSquad type not found"); return null; }
                object squad = Activator.CreateInstance(squadType, new[] { deployment });
                Debug.Log("[Multipleer][tac] ResolveClientSquad: built squad from vehicle deployment setup");
                return squad;
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] ResolveClientSquad failed: " + ex); return null; }
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
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] FindViewerVehicleAtSite failed: " + ex); return null; }
        }

        private static bool ToBool(object o) => o is bool b && b;

        // ─── Small reflection utilities ────────────────────────────────────
        private static object GeoLevelController()
        {
            // Reuse GeoRuntime's resolution (GameUtl.CurrentLevel → GeoLevelController component).
            return Network.Sync.GeoRuntime.Instance.GeoLevel();
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
            sb.Append("[Multipleer][tac] ").Append(side).Append(" actorTable:");
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
