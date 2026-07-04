using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Network.MessageLayer;
using UnityEngine;

namespace Multipleer.Network.Sync.State
{
    /// <summary>
    /// Inc4 S2 — HOST-DRIVEN TRAVEL MIRROR (design spec §4/§6:
    /// docs/superpowers/specs/2026-07-02-multipleer-inc4-client-sim-freeze-design.md).
    ///
    /// S1 froze the client geoscape sim CLOCK (<c>Timing.Paused=true</c>), so the client's
    /// <c>GeoNavComponent.NavigateRoutine</c> — a frame-updateable on the now-paused geo Timing — is DEFERRED
    /// (TimingScheduler.CallUpdateable skips a paused NextFrame updateable) and local vehicle travel stops
    /// advancing: the client's <c>PivotTransform.localRotation</c> is never touched. S2 restores visible travel
    /// WITHOUT unfreezing the sim: the HOST periodically broadcasts each MOVING vehicle's ABSOLUTE globe
    /// placement and the client replays it as a pure display. The client never re-navigates, never integrates
    /// its own motion (canon: client = pure mirror; host-authoritative).
    ///
    /// WHY a placement mirror, not a relayed <c>StartTravel</c> (spec §2/§4, roadmap Inc2): under the frozen
    /// clock, replaying a <c>StartTravel{path}</c> via <c>NavigateRoutine</c> can't render (the routine is a
    /// deferred frame-updateable) without a parallel slaved clock, AND path-replay is client-side motion
    /// integration — the exact "client never simulates" violation. The absolute mirror is drift-free by
    /// construction (absolute values + last-writer-wins <see cref="SurfaceSeq"/>) and path/speed/TFTV-agnostic.
    ///
    /// MOST-NATIVE placement: it mirrors exactly what <c>NavigateRoutine</c> itself writes each travel tick —
    /// <c>PivotTransform.localRotation</c> (the SOLE globe-position determinant; the GlobeMarker icon + 3D mesh
    /// both hang off the pivot — GeoNavComponent.cs:111) plus <c>Surface.localEulerAngles</c> (heading —
    /// GeoNavComponent.cs:213) — keyed by the COMPOSITE (owner-faction def-name hash, VehicleID). Mirroring the
    /// LOCAL pivot rotation is frame-of-reference-robust across the two instances' globe hierarchies. Idle
    /// vehicle = 0 bytes (per-vehicle signature skip), so the surface is free at rest and light in motion
    /// (mirrors the tactical actor-state position mirror <c>TacticalActorStateSync</c>).
    ///
    /// FIX 2026-07-04 (#2): the original mirrored <c>Surface.position</c>/<c>Surface.rotation</c> — a DERIVED
    /// world value of the GlobeOffset child. Writing it on the frozen client left the pivot rotation untouched,
    /// so the pivot-parented GlobeMarker never moved (pipeline shipped+applied 25-27 vehicles/poll yet nothing
    /// moved). Confirmed via decompile: travel writes only the pivot rotation, never Surface.position.
    ///
    /// FIX 2026-07-04 (#3, DIAG-proven): <c>VehicleID</c> is only per-FACTION unique
    /// (<c>GeoFaction.cs:2008 ++_lastVehicleIndex</c>) — the in-game DIAG showed the host shipping FIVE different
    /// vehicles (PX_Mantis/DA_Blimp/NJ_Rhino/SYN_Icarus…) all as "id=1" and the client applying ALL of them to
    /// ONE vehicle (its id-keyed lookup is last-writer-wins), which landed back on its own parked rotation each
    /// batch → zero visible movement; the host's per-id signature slot churned identically → all 25 vehicles
    /// re-shipped every poll. Mirror keys are now the composite <see cref="GeoVehiclePos.Key"/>
    /// (owner faction def asset name hashed via <see cref="GeoVehiclePos.StableOwnerKey"/> — identical assets on
    /// both instances — plus VehicleID).
    ///
    /// SMOOTHING 2026-07-04 (Inc4 S2 travel-mirror gate #4): the ~4 Hz poll cadence made the client STEP — raw
    /// snapshots were written straight to the transform, so between polls the vehicle sat still then jumped. The
    /// client now BUFFERS each inbound snapshot into <see cref="VehicleInterpolator"/> (keyed by the same composite
    /// key) and a per-frame driver (<see cref="ClientInterpolateTick"/>, reusing the existing
    /// <c>NetworkEngine.Update → SyncEngine.Tick</c> hook — no new MonoBehaviour) renders a point
    /// <see cref="InterpDelaySeconds"/> behind the newest snapshot, slerping the pivot rotation + shortest-arc-
    /// lerping the heading between the two straddling snapshots (clamp-and-hold on a starved buffer, never
    /// extrapolate). Motion is native-smooth regardless of the emit rate; the wire/host side is unchanged.
    ///
    /// This is the reflection boundary (game types resolved by name via <c>AccessTools</c>); the pure wire codec
    /// + change-detection signature live in <see cref="GeoVehicleSnapshot"/> / <see cref="GeoVehiclePos"/>, and the
    /// pure snapshot-interpolation math in <see cref="VehicleInterpolator"/> (all unit-tested).
    /// </summary>
    public static class GeoVehicleMirror
    {
        // ─── CLIENT: snapshot interpolation (Inc4 S2 smoothing) ──────────────────────────────────────────────
        // Render latency behind the newest snapshot: ~1.5 × the nominal ~0.25 s (4 Hz, VehiclePollTickInterval=15
        // @60fps) emit interval, so the two snapshots straddling the render clock are always in the buffer and the
        // client interpolates between them (rather than extrapolating past the newest). Slow geoscape travel makes
        // this ~0.375 s of visual latency imperceptible.
        private const double InterpDelaySeconds = 0.375;
        // Purge a key not refreshed within this window (destroyed/despawned vehicle, or one that stopped travelling
        // so the host no longer ships it) — many missed polls, far below a real gap.
        private const double StaleTtlSeconds = 5.0;
        private static readonly VehicleInterpolator _interp = new VehicleInterpolator(InterpDelaySeconds, StaleTtlSeconds);
        // Per composite key → the live vehicle Component to write each frame. Refreshed on every inbound batch (so a
        // re-created/rebound vehicle re-resolves); pruned in lock-step with the interpolator on stale/destroy.
        private static readonly Dictionary<long, Component> _clientTargets = new Dictionary<long, Component>();
        private static readonly List<long> _purgeScratch = new List<long>();
        // Bind-once reflection cache (mirrors GeoRuntime / EventReflection). Game types are stable per session.
        private static FieldInfo _mapField;        // GeoLevelController.Map  (public GeoMap field)
        private static PropertyInfo _vehiclesProp;  // GeoMap.Vehicles         (IList<GeoVehicle>)
        private static FieldInfo _vehicleIdField;    // GeoVehicle.VehicleID    (public int field)
        private static PropertyInfo _surfaceProp;    // GeoVehicle.Surface      (GlobeOffset child Transform; heading source)
        private static PropertyInfo _ownerProp;      // GeoVehicle.Owner        (GeoFaction — owner identity for the composite key)
        private static PropertyInfo _factionDefProp; // GeoFaction.Def          (GeoFactionDef : BaseDef : ScriptableObject → .name)

        // HOST: last-broadcast signature per composite (OwnerId, VehicleID) key — skip a vehicle whose placement
        // is unchanged since the last flush so a PARKED vehicle produces ZERO bytes.
        private static readonly Dictionary<long, string> _lastSig = new Dictionary<long, string>();

        // ─── DIAGNOSTICS (Inc4 S2 travel-mirror gate #3, 2026-07-04) — pure logging, ZERO behaviour change ──
        // The pipeline ships+applies 25 vehicles/poll yet the client shows no movement; the decompile has
        // already PROVEN the write field (pivot.localRotation) + target (mesh/marker hang off the pivot) are
        // correct and that NOTHING re-writes the pivot on the frozen client (paused ⇒ NavigateRoutine's
        // NextFrame yield is skipped, TimingScheduler.cs:484). These probes localize the last unknowns at
        // RUNTIME: (a) does the client write HOLD across a poll or get REVERTED (stomp), (b) is the resolved
        // instance the visible/active scene actor, (c) do host & client agree on the pivot quat AND world
        // position for the SAME VehicleID (frame-alignment), (d) are the moving vehicles even viewer-visible.
        // Throttled to 1 sample / DIAG_EVERY polls for the lowest-N VehicleIDs in the batch (host+client pick
        // the SAME ids from the SAME batch, so lines pair up 1:1). Remove once the break is localized (spec §8).
        private const int DIAG_EVERY = 8;                 // ~1 log burst / 2 s at the ~4 Hz poll
        private const int DIAG_TRACK = 3;                 // lowest-3 composite keys in the batch
        private static int _diagHostPoll;
        private static int _diagClientApply;
        private static readonly Dictionary<long, Quaternion> _diagClientPrevWritten = new Dictionary<long, Quaternion>();
        private static PropertyInfo _pIsVisible, _pOwnedByViewer, _pTravelling, _pCurrentSite;

        /// <summary>Drop all per-session state (host signature cache + client interpolation buffers + live-object
        /// cache + diag caches). Called from the <see cref="Multipleer.Network.Sync.SyncEngine"/> constructor, which
        /// is recreated per session — so a new session never inherits a previous one's buffered snapshots (whose
        /// stale seq/placement would otherwise reject or misplace the fresh vehicles until the TTL purge).</summary>
        public static void ResetForNewSession()
        {
            _lastSig.Clear();
            _interp.Clear();
            _clientTargets.Clear();
            _diagClientPrevWritten.Clear();
            _diagHostPoll = 0;
            _diagClientApply = 0;
        }

        // ─── HOST: poll every moving vehicle + broadcast the changed batch ─────────────────────────────────

        /// <summary>HOST (throttled from <c>SyncEngine.Tick</c>): read each map vehicle's globe placement (pivot
        /// <c>localRotation</c> + heading euler), signature-skip unchanged vehicles, and broadcast the moving ones
        /// on the <c>GeoVehiclePos</c> surface with a fresh <see cref="SurfaceSeq"/> value. No-op off-host / not in
        /// geoscape (guard reset then).</summary>
        public static void HostPollAndBroadcast(NetworkEngine engine, SurfaceSeq seq)
        {
            if (engine == null || !engine.IsActive || !engine.IsHost || seq == null) return;
            try
            {
                var vehicles = ResolveVehicles();
                if (vehicles == null) { _lastSig.Clear(); return; }   // left geoscape / mid-load → reset guard

                var changed = new List<GeoVehiclePos>();
                var liveKeys = new HashSet<long>();
                foreach (var v in vehicles)
                {
                    if (v == null) continue;
                    // heading = Surface.localEulerAngles (X/Y/Z); pivotRot = PivotTransform.localRotation (QX..QW).
                    if (!TryReadPlacement(v, out int ownerId, out int id, out Vector3 heading, out Quaternion pivotRot)) continue;
                    var rec = new GeoVehiclePos(ownerId, id, heading.x, heading.y, heading.z,
                                                pivotRot.x, pivotRot.y, pivotRot.z, pivotRot.w);
                    liveKeys.Add(rec.Key);
                    string sig = GeoVehiclePos.Signature(rec);
                    if (_lastSig.TryGetValue(rec.Key, out var prev) && prev == sig) continue;   // unchanged → skip (idle = 0 bytes)
                    _lastSig[rec.Key] = sig;
                    changed.Add(rec);
                }

                // Drop signatures for vehicles that left the map (destroyed/despawned) so a re-created key
                // re-ships from scratch.
                if (_lastSig.Count > liveKeys.Count)
                {
                    var stale = new List<long>();
                    foreach (var k in _lastSig.Keys) if (!liveKeys.Contains(k)) stale.Add(k);
                    foreach (var k in stale) _lastSig.Remove(k);
                }

                if (changed.Count == 0) return;

                uint s = seq.Next(SurfaceIds.GeoVehiclePos);
                byte[] payload = GeoVehicleSnapshot.Encode(s, changed);
                engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope,
                    SyncProtocol.EncodeEnvelope(SurfaceIds.GeoVehiclePos, SyncKind.StateSnapshot, payload)));
                Debug.Log("[Multipleer][geo] HOST broadcast geo.vehiclepos seq=" + s + " vehicles=" + changed.Count);
                DiagHost(vehicles, changed);   // DIAG: throttled per-vehicle transform probe (host side)
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][geo] HostPollAndBroadcast failed: " + ex.Message); }
        }

        // ─── CLIENT: apply a host vehicle-placement batch ──────────────────────────────────────────────────

        /// <summary>CLIENT inbound (<c>GeoVehiclePos</c>): seq-guard, resolve each composite (OwnerId, VehicleID)
        /// key against the live map, and BUFFER its host-absolute placement (<c>PivotTransform.localRotation</c> +
        /// <c>Surface.localEulerAngles</c>) into <see cref="_interp"/> stamped with the arrival time — it is NOT
        /// written to the transform here (that would step at the ~4 Hz poll rate). <see cref="ClientInterpolateTick"/>
        /// replays it smoothly every frame. Also caches the live vehicle Component per key for that per-frame write.
        /// Buffers ONLY when the client sim is FROZEN (S1): otherwise the client runs its OWN native travel and
        /// mirroring would fight the local navigate. No-op on host.</summary>
        public static void HandleVehiclePos(byte[] payload, SurfaceSeq seq)
        {
            if (seq == null) return;
            var engine = NetworkEngine.Instance;
            // Apply exactly when the sim is frozen (active client + flag ON) — the same gate the freeze itself
            // uses. Host / single-player / flag-OFF / freeze-inactive → the local sim owns travel; do NOT mirror.
            if (!ClientSimFreeze.ShouldFreeze(ClientSimFreeze.Enabled,
                    engine != null, engine != null && engine.IsActive, engine != null && engine.IsHost))
                return;
            if (!GeoVehicleSnapshot.TryDecode(payload, out uint s, out var vehicles))
            { Debug.LogError("[Multipleer][geo] geo.vehiclepos decode failed"); return; }
            if (!seq.ShouldApply(SurfaceIds.GeoVehiclePos, s)) return;   // stale/dup seq → drop

            try
            {
                // Build the client's composite-key → live vehicle lookup once (the map is small).
                // KEY = (OwnerId, VehicleID): VehicleID alone is per-faction and COLLIDES across factions.
                var byKey = new Dictionary<long, object>();
                var live = ResolveVehicles();
                if (live != null)
                    foreach (var v in live)
                    {
                        if (v == null) continue;
                        if (TryReadId(v, out int id) && TryReadOwnerKey(v, out int ownerId))
                            byKey[GeoVehiclePos.MakeKey(ownerId, id)] = v;
                    }

                DiagClient(byKey, vehicles);   // DIAG: read current pivot + newest-target held-check BEFORE buffering

                // BUFFER (don't write): stamp each snapshot with the arrival time into the interpolator and cache the
                // live Component so the per-frame driver can write it. The transform is moved by ClientInterpolateTick.
                double arrival = Time.unscaledTime;
                int buffered = 0;
                foreach (var rec in vehicles)
                {
                    if (!byKey.TryGetValue(rec.Key, out var vehicle) || !(vehicle is Component comp) || comp == null) continue;
                    _clientTargets[rec.Key] = comp;
                    _interp.Push(rec.Key, s, rec, arrival);
                    buffered++;
                }
                seq.Mark(SurfaceIds.GeoVehiclePos, s);
                if (buffered > 0)
                    Debug.Log("[Multipleer][geo] CLIENT buffered geo.vehiclepos seq=" + s + " vehicles=" + buffered);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][geo] HandleVehiclePos failed: " + ex.Message); }
        }

        // ─── CLIENT: per-frame interpolation driver ──────────────────────────────────────────────────────────

        /// <summary>CLIENT per-frame (driven from <c>SyncEngine.Tick</c> on the non-host — the SAME
        /// <c>NetworkEngine.Update</c> hook the sim-freeze clock-pin rides, so no new MonoBehaviour): render every
        /// buffered vehicle at <c>now - InterpDelaySeconds</c>, slerping the pivot rotation + shortest-arc-lerping
        /// the heading between the two straddling snapshots and writing the result to the live pivot/Surface
        /// transforms. Purges stale keys (and destroyed Components) so the buffers/live-object cache stay bounded and
        /// in-sync. Self-gated on the SAME <see cref="ClientSimFreeze.ShouldFreeze"/> gate as the buffering: host /
        /// flag-OFF / no session → clears state and no-ops (so a flag-OFF rollback leaves ZERO behaviour). Fully
        /// try/caught: never throws into the frame loop.</summary>
        public static void ClientInterpolateTick(NetworkEngine engine)
        {
            if (!ClientSimFreeze.ShouldFreeze(ClientSimFreeze.Enabled,
                    engine != null, engine != null && engine.IsActive, engine != null && engine.IsHost))
            {
                if (_clientTargets.Count > 0) _clientTargets.Clear();
                if (_interp.Count > 0) _interp.Clear();
                return;
            }
            if (_clientTargets.Count == 0) return;   // nothing mirrored yet

            try
            {
                double now = Time.unscaledTime;

                // Purge keys not refreshed within the stale TTL, keeping the live-object cache in lock-step.
                _purgeScratch.Clear();
                if (_interp.PurgeStale(now, _purgeScratch) > 0)
                    foreach (var k in _purgeScratch) _clientTargets.Remove(k);

                using (SyncApplyScope.Enter())
                {
                    _purgeScratch.Clear();   // reused for destroyed-Component pruning this frame
                    foreach (var kv in _clientTargets)
                    {
                        Component comp = kv.Value;
                        if (comp == null) { _purgeScratch.Add(kv.Key); continue; }   // vehicle destroyed → prune
                        if (_interp.TrySample(kv.Key, now, out var sample))
                            WritePlacement(comp, sample);
                    }
                    foreach (var k in _purgeScratch) { _clientTargets.Remove(k); _interp.Remove(k); }
                }
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][geo] ClientInterpolateTick failed: " + ex.Message); }
        }

        // ─── Reflection helpers ─────────────────────────────────────────────────────────────────────────────

        /// <summary>The live <c>GeoMap.Vehicles</c> collection (all factions), or null when not in geoscape.</summary>
        private static IEnumerable ResolveVehicles()
        {
            object geo = GeoRuntime.Instance.GeoLevel();
            if (geo == null) return null;
            if (_mapField == null) _mapField = AccessTools.Field(geo.GetType(), "Map");
            object map = _mapField?.GetValue(geo);
            if (map == null) return null;
            if (_vehiclesProp == null) _vehiclesProp = AccessTools.Property(map.GetType(), "Vehicles");
            return _vehiclesProp?.GetValue(map, null) as IEnumerable;
        }

        private static void EnsureVehicleReflection(Type vehicleType)
        {
            if (_vehicleIdField == null) _vehicleIdField = AccessTools.Field(vehicleType, "VehicleID");
            if (_surfaceProp == null) _surfaceProp = AccessTools.Property(vehicleType, "Surface");
        }

        private static bool TryReadId(object vehicle, out int id)
        {
            id = 0;
            try
            {
                EnsureVehicleReflection(vehicle.GetType());
                if (_vehicleIdField == null) return false;
                id = Convert.ToInt32(_vehicleIdField.GetValue(vehicle));
                return true;
            }
            catch { return false; }
        }

        /// <summary>Stable owner-faction key: <c>GeoVehicle.Owner</c> (GeoFaction) → <c>Def</c> (GeoFactionDef :
        /// BaseDef : ScriptableObject) → asset <c>name</c> → FNV-1a hash. Def asset names are identical across
        /// the two instances (same build, host-replicated state), so host and client derive the SAME key for the
        /// same faction. Unresolvable owner → key 0 (symmetric on both ends).</summary>
        private static bool TryReadOwnerKey(object vehicle, out int ownerId)
        {
            ownerId = 0;
            try
            {
                if (_ownerProp == null) _ownerProp = AccessTools.Property(vehicle.GetType(), "Owner");
                object owner = _ownerProp?.GetValue(vehicle, null);
                if (owner == null) return true;   // key 0 fallback — still resolvable, symmetric host/client
                if (_factionDefProp == null) _factionDefProp = AccessTools.Property(owner.GetType(), "Def");
                var def = _factionDefProp?.GetValue(owner, null) as UnityEngine.Object;
                ownerId = GeoVehiclePos.StableOwnerKey(def != null ? def.name : null);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Read a vehicle's {OwnerId, VehicleID, Surface.localEulerAngles (heading),
        /// PivotTransform.localRotation (globe placement)} — the exact state <c>NavigateRoutine</c> writes each
        /// travel tick, plus the composite-key owner half. The pivot is the vehicle's own transform
        /// (<c>GeoActor.PivotTransform => base.transform</c>), reached by casting the component. Returns false
        /// when the id / owner / pivot is unreadable, or the pivot rotation is NaN.</summary>
        private static bool TryReadPlacement(object vehicle, out int ownerId, out int id, out Vector3 heading, out Quaternion pivotRot)
        {
            ownerId = 0; id = 0; heading = Vector3.zero; pivotRot = Quaternion.identity;
            if (!TryReadId(vehicle, out id)) return false;
            if (!TryReadOwnerKey(vehicle, out ownerId)) return false;
            if (!(vehicle is Component comp) || comp == null) return false;   // GeoVehicle is a UnityEngine.Component
            Transform pivot = comp.transform;                                 // == GeoActor.PivotTransform (base.transform)
            if (pivot == null) return false;
            pivotRot = pivot.localRotation;
            if (float.IsNaN(pivotRot.x) || float.IsNaN(pivotRot.y) || float.IsNaN(pivotRot.z) || float.IsNaN(pivotRot.w))
                return false;
            EnsureVehicleReflection(vehicle.GetType());
            if (_surfaceProp != null && _surfaceProp.GetValue(vehicle, null) is Transform surface && surface != null)
                heading = surface.localEulerAngles;   // facing; absent-Surface tolerated (heading stays zero)
            return true;
        }

        /// <summary>Write one interpolated placement to a vehicle's transforms: set <c>PivotTransform.localRotation</c>
        /// (the SOLE position determinant — moves the GlobeMarker + mesh exactly as <c>NavigateRoutine</c> does) and
        /// <c>Surface.localEulerAngles</c> (heading) from the blended <see cref="VehicleInterpolator.Sample"/>.
        /// Idempotent; returns true when the pivot rotation was set.</summary>
        private static bool WritePlacement(Component comp, VehicleInterpolator.Sample s)
        {
            if (comp == null) return false;
            Transform pivot = comp.transform;                                 // == GeoActor.PivotTransform
            if (pivot == null) return false;
            pivot.localRotation = new Quaternion(s.QX, s.QY, s.QZ, s.QW);
            EnsureVehicleReflection(comp.GetType());
            if (_surfaceProp != null && _surfaceProp.GetValue(comp, null) is Transform surface && surface != null)
                surface.localEulerAngles = new Vector3(s.X, s.Y, s.Z);
            return true;
        }

        // ─── DIAGNOSTICS impl (pure logging; every call fully try/caught so it can never throw into the game) ──

        private static void EnsureDiagReflection(Type t)
        {
            if (_pIsVisible == null) _pIsVisible = AccessTools.Property(t, "IsVisible");
            if (_pOwnedByViewer == null) _pOwnedByViewer = AccessTools.Property(t, "IsOwnedByViewer");
            if (_pTravelling == null) _pTravelling = AccessTools.Property(t, "Travelling");
            if (_pCurrentSite == null) _pCurrentSite = AccessTools.Property(t, "CurrentSite");
        }

        private static string DiagProp(PropertyInfo p, object v)
        {
            try { return p?.GetValue(v, null)?.ToString() ?? "null"; } catch { return "err"; }
        }

        private static string DiagQuat(Quaternion q)
            => "(" + q.x.ToString("F4") + "," + q.y.ToString("F4") + "," + q.z.ToString("F4") + "," + q.w.ToString("F4") + ")";

        private static string DiagV3(Vector3 v)
            => "(" + v.x.ToString("F2") + "," + v.y.ToString("F2") + "," + v.z.ToString("F2") + ")";

        private static string DiagParentChain(Transform t)
        {
            if (t == null) return "none";
            var sb = new StringBuilder();
            int guard = 0;
            for (Transform c = t; c != null && guard < 8; c = c.parent, guard++)
            {
                if (sb.Length > 0) sb.Append(" < ");
                sb.Append(c.name);
            }
            return sb.ToString();
        }

        // The n lowest composite keys in the batch — host & client compute this identically so DIAG lines pair up.
        private static HashSet<long> LowestKeys(IList<GeoVehiclePos> batch, int n)
        {
            var keys = new List<long>(batch.Count);
            foreach (var b in batch) keys.Add(b.Key);
            keys.Sort();
            if (keys.Count > n) keys.RemoveRange(n, keys.Count - n);
            return new HashSet<long>(keys);
        }

        private static string DiagKey(int ownerId, int vehicleId)
            => ownerId.ToString("X8") + ":" + vehicleId;

        /// <summary>HOST probe: for the lowest-N shipped keys, log the pivot quat being shipped + its WORLD pos +
        /// parent frame + visibility/owner/travel/site, so the client's paired line can be compared 1:1.</summary>
        private static void DiagHost(IEnumerable vehicles, List<GeoVehiclePos> changed)
        {
            try
            {
                if (changed == null || changed.Count == 0) return;
                if ((++_diagHostPoll % DIAG_EVERY) != 0) return;
                var tracked = LowestKeys(changed, DIAG_TRACK);
                foreach (var v in vehicles)
                {
                    if (v == null || !TryReadId(v, out int id) || !TryReadOwnerKey(v, out int ownerId)) continue;
                    if (!tracked.Contains(GeoVehiclePos.MakeKey(ownerId, id))) continue;
                    if (!(v is Component comp) || comp == null) continue;
                    Transform pivot = comp.transform;
                    EnsureDiagReflection(v.GetType());
                    Debug.Log("[Multipleer][geo][DIAG-H] key=" + DiagKey(ownerId, id) + " name=" + comp.name
                        + " active=" + comp.gameObject.activeInHierarchy
                        + " ship=" + DiagQuat(pivot.localRotation)
                        + " worldPos=" + DiagV3(pivot.position)
                        + " parent=" + DiagParentChain(pivot.parent)
                        + " parentWRot=" + (pivot.parent != null ? DiagQuat(pivot.parent.rotation) : "none")
                        + " vis=" + DiagProp(_pIsVisible, v)
                        + " mine=" + DiagProp(_pOwnedByViewer, v)
                        + " travel=" + DiagProp(_pTravelling, v)
                        + " site=" + DiagProp(_pCurrentSite, v));
                }
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][geo][DIAG-H] " + ex.Message); }
        }

        /// <summary>CLIENT probe (called BEFORE the apply loop, so localRotation is the pre-write value):
        /// for the lowest-N composite keys, compare the CURRENT pivot rotation to the value we wrote LAST poll —
        /// HELD ⇒ the write survived (no stomp); REVERTED ⇒ something reset the pivot between polls. Also logs
        /// the value about to be written, world pos, parent frame, active state and visibility so it pairs 1:1
        /// with DIAG-H for the same key. Updates the per-key prev-written cache every poll (accurate held-check).</summary>
        private static void DiagClient(Dictionary<long, object> byKey, List<GeoVehiclePos> batch)
        {
            try
            {
                if (batch == null || batch.Count == 0) return;
                bool fire = (++_diagClientApply % DIAG_EVERY) == 0;
                var tracked = LowestKeys(batch, DIAG_TRACK);
                foreach (var rec in batch)
                {
                    if (!tracked.Contains(rec.Key)) continue;
                    if (!byKey.TryGetValue(rec.Key, out var vo) || !(vo is Component comp) || comp == null) continue;
                    Transform pivot = comp.transform;
                    Quaternion before = pivot.localRotation;                                 // pre-write (this poll)
                    Quaternion willWrite = new Quaternion(rec.QX, rec.QY, rec.QZ, rec.QW);
                    string held = "n/a(first)";
                    if (_diagClientPrevWritten.TryGetValue(rec.Key, out var prev))
                        held = Quaternion.Angle(before, prev) < 0.05f
                            ? "HELD"
                            : "REVERTED(before=" + DiagQuat(before) + " prevWrote=" + DiagQuat(prev) + ")";
                    _diagClientPrevWritten[rec.Key] = willWrite;
                    if (fire)
                    {
                        EnsureDiagReflection(vo.GetType());
                        Debug.Log("[Multipleer][geo][DIAG-C] key=" + DiagKey(rec.OwnerId, rec.VehicleId) + " name=" + comp.name
                            + " active=" + comp.gameObject.activeInHierarchy
                            + " held=" + held
                            + " willWrite=" + DiagQuat(willWrite)
                            + " worldPos=" + DiagV3(pivot.position)
                            + " parent=" + DiagParentChain(pivot.parent)
                            + " parentWRot=" + (pivot.parent != null ? DiagQuat(pivot.parent.rotation) : "none")
                            + " vis=" + DiagProp(_pIsVisible, vo)
                            + " mine=" + DiagProp(_pOwnedByViewer, vo)
                            + " travel=" + DiagProp(_pTravelling, vo)
                            + " site=" + DiagProp(_pCurrentSite, vo));
                    }
                }
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][geo][DIAG-C] " + ex.Message); }
        }
    }
}
