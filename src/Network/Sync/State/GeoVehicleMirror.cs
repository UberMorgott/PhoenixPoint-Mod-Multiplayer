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
    /// GeoNavComponent.cs:213) — keyed by the save-persisted <c>GeoVehicle.VehicleID</c>. Mirroring the LOCAL
    /// pivot rotation is frame-of-reference-robust across the two instances' globe hierarchies. Idle vehicle =
    /// 0 bytes (per-vehicle signature skip), so the surface is free at rest and light in motion (mirrors the
    /// tactical actor-state position mirror <c>TacticalActorStateSync</c>).
    ///
    /// FIX 2026-07-04: the original mirrored <c>Surface.position</c>/<c>Surface.rotation</c> — a DERIVED world
    /// value of the GlobeOffset child. Writing it on the frozen client left the pivot rotation untouched, so the
    /// pivot-parented GlobeMarker never moved and every vehicle mismatched the host (in-game gate: pipeline
    /// shipped+applied 25-27 vehicles/poll yet nothing moved). Confirmed via decompile: travel writes only the
    /// pivot rotation, never Surface.position.
    ///
    /// This is the reflection boundary (game types resolved by name via <c>AccessTools</c>); the pure wire codec
    /// + change-detection signature live in <see cref="GeoVehicleSnapshot"/> / <see cref="GeoVehiclePos"/>
    /// (unit-tested).
    /// </summary>
    public static class GeoVehicleMirror
    {
        // Bind-once reflection cache (mirrors GeoRuntime / EventReflection). Game types are stable per session.
        private static FieldInfo _mapField;        // GeoLevelController.Map  (public GeoMap field)
        private static PropertyInfo _vehiclesProp;  // GeoMap.Vehicles         (IList<GeoVehicle>)
        private static FieldInfo _vehicleIdField;    // GeoVehicle.VehicleID    (public int field)
        private static PropertyInfo _surfaceProp;    // GeoVehicle.Surface      (GlobeOffset child Transform; heading source)

        // HOST: last-broadcast signature per VehicleID — skip a vehicle whose placement is unchanged since the
        // last flush so a PARKED vehicle produces ZERO bytes (only genuinely-moving vehicles are shipped).
        private static readonly Dictionary<int, string> _lastSig = new Dictionary<int, string>();

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
        private const int DIAG_TRACK = 3;                 // lowest-3 ids in the batch
        private static int _diagHostPoll;
        private static int _diagClientApply;
        private static readonly Dictionary<int, Quaternion> _diagClientPrevWritten = new Dictionary<int, Quaternion>();
        private static PropertyInfo _pIsVisible, _pOwnedByViewer, _pTravelling, _pCurrentSite;

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
                var liveIds = new HashSet<int>();
                foreach (var v in vehicles)
                {
                    if (v == null) continue;
                    // heading = Surface.localEulerAngles (X/Y/Z); pivotRot = PivotTransform.localRotation (QX..QW).
                    if (!TryReadPlacement(v, out int id, out Vector3 heading, out Quaternion pivotRot)) continue;
                    liveIds.Add(id);
                    var rec = new GeoVehiclePos(id, heading.x, heading.y, heading.z,
                                                pivotRot.x, pivotRot.y, pivotRot.z, pivotRot.w);
                    string sig = GeoVehiclePos.Signature(rec);
                    if (_lastSig.TryGetValue(id, out var prev) && prev == sig) continue;   // unchanged → skip (idle = 0 bytes)
                    _lastSig[id] = sig;
                    changed.Add(rec);
                }

                // Drop signatures for vehicles that left the map (destroyed/despawned) so a re-created VehicleID
                // re-ships from scratch.
                if (_lastSig.Count > liveIds.Count)
                {
                    var stale = new List<int>();
                    foreach (var k in _lastSig.Keys) if (!liveIds.Contains(k)) stale.Add(k);
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

        /// <summary>CLIENT inbound (<c>GeoVehiclePos</c>): seq-guard, resolve each <c>VehicleID</c> against the
        /// live map, and replay its <c>PivotTransform.localRotation</c> (globe position) + <c>Surface.localEulerAngles</c>
        /// (heading) from the host-absolute values under <see cref="SyncApplyScope"/>. Applies ONLY when the client
        /// sim is FROZEN (S1): otherwise the client runs its OWN native travel and overwriting it would fight the
        /// local navigate. No-op on host.</summary>
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
                // Build the client's VehicleID → live vehicle lookup once (the map is small).
                var byId = new Dictionary<int, object>();
                var live = ResolveVehicles();
                if (live != null)
                    foreach (var v in live)
                    {
                        if (v == null) continue;
                        if (TryReadId(v, out int id)) byId[id] = v;
                    }

                DiagClient(byId, vehicles);   // DIAG: read pre-write pivot + held-check BEFORE the apply loop

                int applied = 0;
                using (SyncApplyScope.Enter())
                {
                    foreach (var rec in vehicles)
                    {
                        if (!byId.TryGetValue(rec.VehicleId, out var vehicle) || vehicle == null) continue;
                        if (ApplyPlacement(vehicle, rec)) applied++;
                    }
                }
                seq.Mark(SurfaceIds.GeoVehiclePos, s);
                if (applied > 0)
                    Debug.Log("[Multipleer][geo] CLIENT applied geo.vehiclepos seq=" + s + " vehicles=" + applied);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][geo] HandleVehiclePos failed: " + ex.Message); }
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

        /// <summary>Read a vehicle's {VehicleID, Surface.localEulerAngles (heading), PivotTransform.localRotation
        /// (globe placement)} — the exact state <c>NavigateRoutine</c> writes each travel tick. The pivot is the
        /// vehicle's own transform (<c>GeoActor.PivotTransform => base.transform</c>), reached by casting the
        /// component. Returns false when the id / Surface / pivot is unreadable, or the pivot rotation is NaN.</summary>
        private static bool TryReadPlacement(object vehicle, out int id, out Vector3 heading, out Quaternion pivotRot)
        {
            id = 0; heading = Vector3.zero; pivotRot = Quaternion.identity;
            if (!TryReadId(vehicle, out id)) return false;
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

        /// <summary>Replay the host's globe placement on the client: set <c>PivotTransform.localRotation</c> (the
        /// SOLE position determinant — moves the GlobeMarker + mesh exactly as <c>NavigateRoutine</c> does) and
        /// <c>Surface.localEulerAngles</c> (heading). Idempotent; returns true when the pivot rotation was set.</summary>
        private static bool ApplyPlacement(object vehicle, GeoVehiclePos rec)
        {
            if (!(vehicle is Component comp) || comp == null) return false;
            Transform pivot = comp.transform;                                 // == GeoActor.PivotTransform
            if (pivot == null) return false;
            pivot.localRotation = new Quaternion(rec.QX, rec.QY, rec.QZ, rec.QW);
            EnsureVehicleReflection(vehicle.GetType());
            if (_surfaceProp != null && _surfaceProp.GetValue(vehicle, null) is Transform surface && surface != null)
                surface.localEulerAngles = new Vector3(rec.X, rec.Y, rec.Z);
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

        // The n lowest VehicleIDs in the batch — host & client compute this identically so their DIAG lines pair up.
        private static HashSet<int> LowestIds(IList<GeoVehiclePos> batch, int n)
        {
            var ids = new List<int>(batch.Count);
            foreach (var b in batch) ids.Add(b.VehicleId);
            ids.Sort();
            if (ids.Count > n) ids.RemoveRange(n, ids.Count - n);
            return new HashSet<int>(ids);
        }

        /// <summary>HOST probe: for the lowest-N shipped ids, log the pivot quat being shipped + its WORLD pos +
        /// parent frame + visibility/owner/travel/site, so the client's paired line can be compared 1:1.</summary>
        private static void DiagHost(IEnumerable vehicles, List<GeoVehiclePos> changed)
        {
            try
            {
                if (changed == null || changed.Count == 0) return;
                if ((++_diagHostPoll % DIAG_EVERY) != 0) return;
                var tracked = LowestIds(changed, DIAG_TRACK);
                foreach (var v in vehicles)
                {
                    if (v == null || !TryReadId(v, out int id) || !tracked.Contains(id)) continue;
                    if (!(v is Component comp) || comp == null) continue;
                    Transform pivot = comp.transform;
                    EnsureDiagReflection(v.GetType());
                    Debug.Log("[Multipleer][geo][DIAG-H] id=" + id + " name=" + comp.name
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
        /// for the lowest-N ids, compare the CURRENT pivot rotation to the value we wrote LAST poll —
        /// HELD ⇒ the write survived (no stomp); REVERTED ⇒ something reset the pivot between polls. Also logs
        /// the value about to be written, world pos, parent frame, active state and visibility so it pairs 1:1
        /// with DIAG-H for the same id. Updates the per-id prev-written cache every poll (accurate held-check).</summary>
        private static void DiagClient(Dictionary<int, object> byId, List<GeoVehiclePos> batch)
        {
            try
            {
                if (batch == null || batch.Count == 0) return;
                bool fire = (++_diagClientApply % DIAG_EVERY) == 0;
                var tracked = LowestIds(batch, DIAG_TRACK);
                foreach (var rec in batch)
                {
                    if (!tracked.Contains(rec.VehicleId)) continue;
                    if (!byId.TryGetValue(rec.VehicleId, out var vo) || !(vo is Component comp) || comp == null) continue;
                    Transform pivot = comp.transform;
                    Quaternion before = pivot.localRotation;                                 // pre-write (this poll)
                    Quaternion willWrite = new Quaternion(rec.QX, rec.QY, rec.QZ, rec.QW);
                    string held = "n/a(first)";
                    if (_diagClientPrevWritten.TryGetValue(rec.VehicleId, out var prev))
                        held = Quaternion.Angle(before, prev) < 0.05f
                            ? "HELD"
                            : "REVERTED(before=" + DiagQuat(before) + " prevWrote=" + DiagQuat(prev) + ")";
                    _diagClientPrevWritten[rec.VehicleId] = willWrite;
                    if (fire)
                    {
                        EnsureDiagReflection(vo.GetType());
                        Debug.Log("[Multipleer][geo][DIAG-C] id=" + rec.VehicleId + " name=" + comp.name
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
