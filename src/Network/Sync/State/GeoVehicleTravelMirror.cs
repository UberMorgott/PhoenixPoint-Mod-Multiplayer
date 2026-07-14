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

        // HOST: remaining forced re-ship polls per composite key — a newly-appeared DAMAGED vehicle (stolen /
        // post-interception craft acquired mid-session) whose one-shot HP tail may land on the client BEFORE its
        // #6 identity mirror spawns the vehicle (ApplyTravelMeta.ResolveVehicle no-ops → HP lost on a change-only
        // surface). While a key is in this map its meta re-ships every poll (bypassing the unchanged-sig skip)
        // until the countdown drains, outlasting the #6 spawn delivery. See GeoVehicleTravelMeta.NeedsReshipWindow.
        private static readonly Dictionary<long, int> _reship = new Dictionary<long, int>();

        /// <summary>Drop per-session host state (recreated per session in the <see cref="SyncEngine"/> ctor).</summary>
        public static void ResetForNewSession() { _lastSig.Clear(); _reship.Clear(); }

        /// <summary>HOST: force the NEXT poll to re-ship every vehicle's travel metadata (drop the signature
        /// cache) — driven from the host resync chokepoint
        /// (<see cref="Multiplayer.Network.Sync.SyncEngine.BroadcastAllChannels"/>) when a client (re)joins /
        /// (re)enters the geoscape. The initial parked-at-site CurrentSite snapshot ships exactly once per
        /// session; a late joiner wasn't listening then, so without this its native POI interaction stays dead
        /// until the ship next travels. Idempotent; leaves any open re-ship window intact.</summary>
        public static void ArmFullReship() => _lastSig.Clear();

        // ─── HOST: poll each vehicle's travel metadata + broadcast the changed batch ─────────────────────────

        public static void HostPollAndBroadcast(NetworkEngine engine, SurfaceSeq seq)
        {
            if (engine == null || !engine.IsActive || !engine.IsHost || seq == null) return;
            try
            {
                var rt = GeoRuntime.Instance;
                IEnumerable vehicles = VehicleTravelReflection.AllVehicles(rt);
                if (vehicles == null) { _lastSig.Clear(); _reship.Clear(); return; }   // left geoscape / mid-load → reset guard

                var changed = new List<GeoVehicleTravelMeta>();
                var liveKeys = new HashSet<long>();
                foreach (var v in vehicles)
                {
                    if (v == null) continue;
                    if (!VehicleTravelReflection.TryReadTravelMeta(rt, v, out var meta)) continue;
                    liveKeys.Add(meta.Key);
                    string sig = GeoVehicleTravelMeta.Signature(meta);
                    bool known = _lastSig.TryGetValue(meta.Key, out var prev);
                    bool forced = _reship.TryGetValue(meta.Key, out int reshipLeft);   // in an open re-ship window
                    if (known && prev == sig && !forced) continue;   // unchanged & no window → skip (0 bytes)
                    _lastSig[meta.Key] = sig;
                    // Re-ship window bookkeeping: a NEW damaged craft (stolen / post-interception) opens one so its
                    // HP tail re-delivers after the #6 identity mirror spawns the client vehicle (else the one-shot
                    // change-only tail lands pre-spawn → ResolveVehicle no-ops → mirror stuck at BaseStats HP).
                    if (GeoVehicleTravelMeta.NeedsReshipWindow(known, meta.Health))
                        _reship[meta.Key] = GeoVehicleTravelMeta.ReshipWindowPolls;
                    else if (forced && --reshipLeft <= 0) _reship.Remove(meta.Key);
                    else if (forced) _reship[meta.Key] = reshipLeft;
                    // Suppress the initial "idle mid-air, never travelled, no route, pristine hull, NOT parked at
                    // a site" state — nothing for the client to draw. We still record its signature above so it
                    // never re-ships. A vehicle PARKED AT A SITE (CurrentSiteId >= 0) SHIPS its initial state:
                    // the client's native POI interaction reads GeoVehicle.CurrentSite (GeoSite.Vehicles =>
                    // CurrentSite==this; UIStateVehicleSelected contextual-menu / site abilities), which stays
                    // null — dead site interaction — until the first 0xA6 carries it. Before this, only a
                    // fly-away+return travel change shipped CurrentSite (POI-dead-on-load bug). WA-3: a DAMAGED/
                    // repairing vehicle ships its initial HP tail too; a pristine idle-mid-air one stays 0 bytes.
                    if (!known && !meta.Travelling && (meta.DestSiteIds == null || meta.DestSiteIds.Length == 0)
                        && (meta.Health == null || meta.Health.IsPristine) && meta.CurrentSiteId < 0)
                        continue;
                    changed.Add(meta);
                }

                // Drop signatures + re-ship windows for vehicles that left the map so a re-created key re-ships
                // from scratch (a stale re-ship entry would otherwise force a spurious re-ship if the key returns).
                if (_lastSig.Count > liveKeys.Count)
                {
                    var stale = new List<long>();
                    foreach (var k in _lastSig.Keys) if (!liveKeys.Contains(k)) stale.Add(k);
                    foreach (var k in stale) { _lastSig.Remove(k); _reship.Remove(k); }
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
