using System;
using System.Collections;
using System.Collections.Generic;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Inc4 S2 — HOST-DRIVEN SITE-EXPLORATION-PROGRESS MIRROR (surface <see cref="SurfaceIds.GeoVehicleExplore"/>
    /// 0xA7).
    ///
    /// The native "explore point-of-interest" progress bar is a per-vehicle
    /// <c>GeoActorProgressionVisualController</c> whose fill is driven by the geoscape <c>Timing.Now</c>. On a
    /// sim-frozen co-op CLIENT (Inc4 S1: <c>Timing.Paused</c>) the bar is NEVER instantiated (the client never runs
    /// the host-authoritative <c>StartExploringCurrentSite</c>) and could not advance anyway (<c>Now</c> frozen), so
    /// the client showed no exploration progress bar while the host did.
    ///
    /// The HOST reads each vehicle's live bar fill, signature-skips the unchanged (quantized to whole percent, so a
    /// non-exploring vehicle is 0 bytes and a moving bar ships ~100 updates over the whole exploration), and
    /// broadcasts the changed batch. The CLIENT re-creates the SAME native bar and anchors its Start/End around the
    /// frozen <c>Now</c> so the native <c>Progression</c> renders exactly the host fraction (a STEP per poll; the
    /// host's advances continuously). Display-only, never drives the frozen sim (canon: client = pure mirror).
    /// Gated on the SAME <see cref="ClientSimFreeze"/> flag as the 0xA5/0xA6 mirrors (flag-OFF = zero new traffic,
    /// legacy client-sim path renders the bar itself). The pure wire codec + change signature live in
    /// <see cref="GeoVehicleExploreMeta"/> / <see cref="GeoVehicleExploreSnapshot"/> (unit-tested); the game-bound
    /// read/render is <see cref="GeoVehicleExploreReflection"/>.
    /// </summary>
    public static class GeoVehicleExploreMirror
    {
        // HOST: last-broadcast signature per composite key — skip a vehicle whose exploration state is unchanged.
        private static readonly Dictionary<long, string> _lastSig = new Dictionary<long, string>();

        /// <summary>Drop per-session host state (recreated per session in the <see cref="SyncEngine"/> ctor).</summary>
        public static void ResetForNewSession() => _lastSig.Clear();

        // ─── HOST: poll each vehicle's exploration progress + broadcast the changed batch ─────────────────────

        public static void HostPollAndBroadcast(NetworkEngine engine, SurfaceSeq seq)
        {
            if (engine == null || !engine.IsActive || !engine.IsHost || seq == null) return;
            try
            {
                var rt = GeoRuntime.Instance;
                IEnumerable vehicles = VehicleTravelReflection.AllVehicles(rt);
                if (vehicles == null) { _lastSig.Clear(); return; }   // left geoscape / mid-load → reset guard

                var changed = new List<GeoVehicleExploreMeta>();
                var liveKeys = new HashSet<long>();
                foreach (var v in vehicles)
                {
                    if (v == null) continue;
                    if (!GeoVehicleExploreReflection.TryReadExploreMeta(rt, v, out var meta)) continue;
                    liveKeys.Add(meta.Key);
                    string sig = GeoVehicleExploreMeta.Signature(meta);
                    bool known = _lastSig.TryGetValue(meta.Key, out var prev);
                    if (known && prev == sig) continue;   // unchanged → skip (0 bytes)
                    _lastSig[meta.Key] = sig;
                    // Suppress the initial "not exploring" state — nothing for the client to draw or clear. We still
                    // record its signature above so it never re-ships. Once a vehicle starts exploring (or a
                    // previously-shipped one must be cleared), it ships.
                    if (!known && !meta.Exploring) continue;
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

                uint s = seq.Next(SurfaceIds.GeoVehicleExplore);
                byte[] payload = GeoVehicleExploreSnapshot.Encode(s, changed);
                engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope,
                    SyncProtocol.EncodeEnvelope(SurfaceIds.GeoVehicleExplore, SyncKind.StateSnapshot, payload)));
                Debug.Log("[Multiplayer][geo] HOST broadcast geo.vehicleexplore seq=" + s + " vehicles=" + changed.Count);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][geo] GeoVehicleExploreMirror.HostPollAndBroadcast failed: " + ex.Message); }
        }

        // ─── CLIENT: apply a host exploration-progress batch (display-only) ──────────────────────────────────

        public static void HandleExplore(byte[] payload, SurfaceSeq seq)
        {
            if (seq == null) return;
            var engine = NetworkEngine.Instance;
            // Apply exactly when the sim is frozen (active client + flag ON) — the same gate the 0xA5/0xA6 mirrors
            // use. Host / single-player / flag-OFF / freeze-inactive → the local sim renders the bar; do NOT mirror.
            if (!ClientSimFreeze.ShouldFreeze(ClientSimFreeze.Enabled,
                    engine != null, engine != null && engine.IsActive, engine != null && engine.IsHost))
                return;
            if (!GeoVehicleExploreSnapshot.TryDecode(payload, out uint s, out var vehicles))
            { Debug.LogError("[Multiplayer][geo] geo.vehicleexplore decode failed"); return; }
            if (!seq.ShouldApply(SurfaceIds.GeoVehicleExplore, s)) return;   // stale/dup seq → drop

            try
            {
                var rt = GeoRuntime.Instance;
                int applied = 0;
                using (SyncApplyScope.Enter())
                    foreach (var meta in vehicles)
                        if (GeoVehicleExploreReflection.ApplyExploreMeta(rt, meta)) applied++;
                seq.Mark(SurfaceIds.GeoVehicleExplore, s);
                if (applied > 0)
                    Debug.Log("[Multiplayer][geo] CLIENT applied geo.vehicleexplore seq=" + s + " vehicles=" + applied);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][geo] GeoVehicleExploreMirror.HandleExplore failed: " + ex.Message); }
        }
    }
}
