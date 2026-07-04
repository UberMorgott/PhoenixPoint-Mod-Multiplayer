using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
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
    /// and local vehicle travel stops advancing. S2 restores visible travel WITHOUT unfreezing the sim: the
    /// HOST periodically broadcasts each MOVING vehicle's ABSOLUTE world placement and the client applies it as
    /// a pure display. The client never re-navigates, never integrates its own motion (canon: client = pure
    /// mirror; host-authoritative).
    ///
    /// WHY a position mirror, not a relayed <c>StartTravel</c> (spec §2/§4, roadmap Inc2): under the frozen
    /// clock, replaying a <c>StartTravel{path}</c> via <c>NavigateRoutine</c> can't render (the routine is a
    /// deferred frame-updateable) without a parallel slaved clock, AND path-replay is client-side motion
    /// integration — the exact "client never simulates" violation. The absolute mirror is drift-free by
    /// construction (absolute values + last-writer-wins <see cref="SurfaceSeq"/>) and path/speed/TFTV-agnostic.
    ///
    /// MOST-NATIVE placement: it mirrors exactly the pair the game's own <c>GeoVehicle.RecordInstanceData</c>/
    /// <c>ProcessInstanceData</c> round-trips to persist/restore a vehicle — <c>Surface.position</c> +
    /// <c>Surface.rotation</c> — keyed by the save-persisted <c>GeoVehicle.VehicleID</c>. Idle vehicle = 0 bytes
    /// (per-vehicle signature skip), so the surface is free at rest and light in motion (mirrors the tactical
    /// actor-state position mirror <c>TacticalActorStateSync</c>).
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
        private static PropertyInfo _surfaceProp;    // GeoVehicle.Surface      (Transform, world placement)

        // HOST: last-broadcast signature per VehicleID — skip a vehicle whose placement is unchanged since the
        // last flush so a PARKED vehicle produces ZERO bytes (only genuinely-moving vehicles are shipped).
        private static readonly Dictionary<int, string> _lastSig = new Dictionary<int, string>();

        // ─── HOST: poll every moving vehicle + broadcast the changed batch ─────────────────────────────────

        /// <summary>HOST (throttled from <c>SyncEngine.Tick</c>): read each map vehicle's world placement,
        /// signature-skip unchanged vehicles, and broadcast the moving ones on the <c>GeoVehiclePos</c> surface
        /// with a fresh <see cref="SurfaceSeq"/> value. No-op off-host / not in geoscape (guard reset then).</summary>
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
                    if (!TryReadPlacement(v, out int id, out Vector3 p, out Quaternion q)) continue;
                    liveIds.Add(id);
                    var rec = new GeoVehiclePos(id, p.x, p.y, p.z, q.x, q.y, q.z, q.w);
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
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][geo] HostPollAndBroadcast failed: " + ex.Message); }
        }

        // ─── CLIENT: apply a host vehicle-placement batch ──────────────────────────────────────────────────

        /// <summary>CLIENT inbound (<c>GeoVehiclePos</c>): seq-guard, resolve each <c>VehicleID</c> against the
        /// live map, and set its <c>Surface.position</c>/<c>rotation</c> to the host-absolute values under
        /// <see cref="SyncApplyScope"/>. Applies ONLY when the client sim is FROZEN (S1): otherwise the client
        /// runs its OWN native travel and overwriting it would fight the local navigate. No-op on host.</summary>
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

        /// <summary>Read a vehicle's {VehicleID, Surface.position, Surface.rotation}. Returns false when the id
        /// or the Surface transform is unreadable, or the position is NaN (a half-initialized transform).</summary>
        private static bool TryReadPlacement(object vehicle, out int id, out Vector3 pos, out Quaternion rot)
        {
            id = 0; pos = Vector3.zero; rot = Quaternion.identity;
            if (!TryReadId(vehicle, out id)) return false;
            if (_surfaceProp == null) return false;
            if (!(_surfaceProp.GetValue(vehicle, null) is Transform surface) || surface == null) return false;
            pos = surface.position;
            rot = surface.rotation;
            if (float.IsNaN(pos.x) || float.IsNaN(pos.y) || float.IsNaN(pos.z)) return false;
            return true;
        }

        /// <summary>Set the client vehicle's world placement to the host-absolute values (the same fields native
        /// <c>ProcessInstanceData</c> restores). Idempotent; returns true when the Surface transform was set.</summary>
        private static bool ApplyPlacement(object vehicle, GeoVehiclePos rec)
        {
            EnsureVehicleReflection(vehicle.GetType());
            if (_surfaceProp == null) return false;
            if (!(_surfaceProp.GetValue(vehicle, null) is Transform surface) || surface == null) return false;
            surface.position = new Vector3(rec.X, rec.Y, rec.Z);
            surface.rotation = new Quaternion(rec.QX, rec.QY, rec.QZ, rec.QW);
            return true;
        }
    }
}
