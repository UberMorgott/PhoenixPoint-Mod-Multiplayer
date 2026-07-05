using System;
using System.Collections;
using System.Collections.Generic;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Inc4 S2 — HOST-DRIVEN TRAVEL-METADATA MIRROR (surface <see cref="SurfaceIds.GeoVehicleTravel"/> 0xA6).
    ///
    /// The 0xA5 position mirror moves the frozen client's vehicle mesh, but the native yellow ROUTE LINE
    /// (<c>UIStateVehicleSelected.DrawCurrentPath</c> → <c>GeoscapeView.DrawVehiclePathLinks</c>/
    /// <c>UpdateVehicleFirstPathLink</c>) reads <c>Travelling</c> / <c>CurrentSite</c> / <c>DestinationSites</c>
    /// — nav state the sim-frozen client never updates (its <c>GeoNavComponent</c> never runs), so it stays
    /// frozen at join while the vehicle visibly moves. Result: the line's origin was pinned to a stale
    /// <c>CurrentSite</c> and its waypoints never popped as they were passed (Symptom B).
    ///
    /// The HOST reads each vehicle's travel metadata, signature-skips the unchanged (a genuine transition —
    /// travel start / waypoint passed / stop — is rare, so this surface is near-silent), and broadcasts the
    /// changed batch. The CLIENT writes ONLY the display-feeding backing fields (via
    /// <see cref="VehicleTravelReflection.ApplyTravelMeta"/>) so the native line draws from the same
    /// authoritative state the host reads — it NEVER navigates or simulates (canon: client = pure mirror).
    /// Gated on the SAME <see cref="ClientSimFreeze"/> feature flag as S1/0xA5 (flag-OFF = zero new traffic,
    /// legacy client-sim path). The pure wire codec + change signature live in <see cref="GeoVehicleTravelMeta"/>
    /// / <see cref="GeoVehicleTravelSnapshot"/> (unit-tested); this is the game-bound engine glue.
    /// </summary>
    public static class GeoVehicleTravelMirror
    {
        // HOST: last-broadcast signature per composite key — skip a vehicle whose travel metadata is unchanged.
        private static readonly Dictionary<long, string> _lastSig = new Dictionary<long, string>();

        /// <summary>Drop per-session host state (recreated per session in the <see cref="SyncEngine"/> ctor).</summary>
        public static void ResetForNewSession() => _lastSig.Clear();

        // ─── HOST: poll each vehicle's travel metadata + broadcast the changed batch ─────────────────────────

        public static void HostPollAndBroadcast(NetworkEngine engine, SurfaceSeq seq)
        {
            if (engine == null || !engine.IsActive || !engine.IsHost || seq == null) return;
            try
            {
                var rt = GeoRuntime.Instance;
                IEnumerable vehicles = VehicleTravelReflection.AllVehicles(rt);
                if (vehicles == null) { _lastSig.Clear(); return; }   // left geoscape / mid-load → reset guard

                var changed = new List<GeoVehicleTravelMeta>();
                var liveKeys = new HashSet<long>();
                foreach (var v in vehicles)
                {
                    if (v == null) continue;
                    if (!VehicleTravelReflection.TryReadTravelMeta(rt, v, out var meta)) continue;
                    liveKeys.Add(meta.Key);
                    string sig = GeoVehicleTravelMeta.Signature(meta);
                    bool known = _lastSig.TryGetValue(meta.Key, out var prev);
                    if (known && prev == sig) continue;   // unchanged → skip (0 bytes)
                    _lastSig[meta.Key] = sig;
                    // Suppress the initial "parked, never travelled, no route, pristine hull" state — nothing
                    // for the client to draw or clear. We still record its signature above so it never re-ships.
                    // Once a vehicle travels (or a previously-shipped one must be cleared), it ships. WA-3: a
                    // DAMAGED or repairing vehicle ships its initial state too (the HP tail is real display
                    // state); a pristine one stays 0 bytes exactly like the pre-WA-3 walk.
                    if (!known && !meta.Travelling && (meta.DestSiteIds == null || meta.DestSiteIds.Length == 0)
                        && (meta.Health == null || meta.Health.IsPristine))
                        continue;
                    changed.Add(meta);
                }

                // Drop signatures for vehicles that left the map so a re-created key re-ships from scratch.
                if (_lastSig.Count > liveKeys.Count)
                {
                    var stale = new List<long>();
                    foreach (var k in _lastSig.Keys) if (!liveKeys.Contains(k)) stale.Add(k);
                    foreach (var k in stale) _lastSig.Remove(k);
                }

                if (changed.Count == 0) return;

                uint s = seq.Next(SurfaceIds.GeoVehicleTravel);
                byte[] payload = GeoVehicleTravelSnapshot.Encode(s, changed);
                engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope,
                    SyncProtocol.EncodeEnvelope(SurfaceIds.GeoVehicleTravel, SyncKind.StateSnapshot, payload)));
                Debug.Log("[Multiplayer][geo] HOST broadcast geo.vehicletravel seq=" + s + " vehicles=" + changed.Count);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][geo] GeoVehicleTravelMirror.HostPollAndBroadcast failed: " + ex.Message); }
        }

        // ─── CLIENT: apply a host travel-metadata batch (display-only) ───────────────────────────────────────

        public static void HandleTravelMeta(byte[] payload, SurfaceSeq seq)
        {
            if (seq == null) return;
            var engine = NetworkEngine.Instance;
            // Apply exactly when the sim is frozen (active client + flag ON) — the same gate the 0xA5 mirror uses.
            // Host / single-player / flag-OFF / freeze-inactive → the local sim owns travel; do NOT mirror.
            if (!ClientSimFreeze.ShouldFreeze(ClientSimFreeze.Enabled,
                    engine != null, engine != null && engine.IsActive, engine != null && engine.IsHost))
                return;
            if (!GeoVehicleTravelSnapshot.TryDecode(payload, out uint s, out var vehicles))
            { Debug.LogError("[Multiplayer][geo] geo.vehicletravel decode failed"); return; }
            if (!seq.ShouldApply(SurfaceIds.GeoVehicleTravel, s)) return;   // stale/dup seq → drop

            try
            {
                var rt = GeoRuntime.Instance;
                int applied = 0;
                bool healthApplied = false;
                using (SyncApplyScope.Enter())
                    foreach (var meta in vehicles)
                        if (VehicleTravelReflection.ApplyTravelMeta(rt, meta))
                        {
                            applied++;
                            if (meta.Health != null) healthApplied = true;
                        }
                seq.Mark(SurfaceIds.GeoVehicleTravel, s);
                // WA-3: an HP/repair value write is invisible until the aircraft HP bar repaints (it only
                // repaints from GeoVehicle.OnMaintenanceChanged, which the silent write deliberately skips) —
                // kick the native deferred repaint (UIModuleVehicleSelection._refreshVehicleBars).
                if (healthApplied) GeoUiRefresh.RefreshVehicleBars(rt);
                if (applied > 0)
                    Debug.Log("[Multiplayer][geo] CLIENT applied geo.vehicletravel seq=" + s + " vehicles=" + applied
                              + (healthApplied ? " (health tail applied + HP-bar kick)" : ""));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][geo] GeoVehicleTravelMirror.HandleTravelMeta failed: " + ex.Message); }
        }
    }
}
