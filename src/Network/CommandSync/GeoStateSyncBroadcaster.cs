using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Multipleer.Network.CommandSync
{
    // SD-AIDR INC-3a — host-only, all-faction vehicle state broadcaster for the 0x35 GeoStateDiff mirror.
    // Generalizes the proven 0x34 TimeSyncBroadcaster (one Timing object, throttled push) to N vehicles
    // across EVERY faction over a single scope/seq/mask packet. Ticked once per frame from
    // NetworkEngine.Update(); host-only (the host owns truth — clients are pure mirrors).
    //
    // Each frame it ENUMERATES geoLevel.Factions (the same IList<GeoFaction> field DescribeVehicles /
    // FindFactionByGuid read) -> per faction GeoFaction.Vehicles -> snapshots each GeoVehicle via the
    // native-RecordInstanceData path (GeoBridge.RecordVehicleState) and DIFFs it through ONE shared pure
    // GeoVehicleStateDiffer (Task 7) — one monotonic seq line per (FactionGuid,VehicleID), epsilon on the
    // continuous pos/rot/range fields, exact on the discrete transitions. mask==0 emits nothing.
    //
    // TWO CHANNELS, ONE SEQ SPACE (the client's ClientGeoStateApplier guards each identity with a SINGLE
    // _lastAppliedSeq, so both channels MUST share the differ's single seq line — newest-wins):
    //   * DISCRETE transition (a record whose mask has any Travelling/CurrentSite/DestinationSites/HitPoints
    //     bit) is sent IMMEDIATELY this frame, RELIABLE+ordered — arrival/departure must be exact and never
    //     lost. The whole changed record is sent (it may also carry the continuous bits that moved the same
    //     frame, so the arrival pos is exact). Its seq is consumed once.
    //   * CONTINUOUS-ONLY change (mask has only pos/rot/range) is BUFFERED per identity (latest wins) and
    //     flushed on a throttled accumulator (~0.5s, tune in-game) as ONE batched UNRELIABLE envelope.
    //     Loss-tolerant: the client seq-guards stale/reordered unreliable packets (drops seq <= last applied).
    //
    // Lossless throttling: because the differ advances its baseline+seq the instant Diff() sees a change, a
    // continuous-only change on a non-flush frame is HELD in _continuousPending with its already-assigned seq
    // (not dropped) and broadcast on the next flush — so no continuous update is lost, only coalesced. When a
    // discrete transition fires for an identity, any older buffered continuous-only record for it is dropped
    // (the combined reliable record carries a newer seq + the exact pos; the client would drop the stale one
    // anyway).
    //
    // Fully defensive (reflection over live game types can throw): the whole tick is try/catch logging-only
    // and never throws into the Update loop, and skips while inbound state is being applied
    // (EntityReplicationScope/CommandRelay) so a mirrored apply never re-enters as a fresh host snapshot.
    //
    // OPEN QUESTIONS (per spec, deferred): per-envelope record cap / split-across-frames to stay under MTU on
    // the unreliable channel (only relevant with many simultaneously-moving alien vehicles — not capped yet);
    // and tuning FlushIntervalSeconds (0.2–0.3s) + optional client interpolation. No session-reset is wired
    // here (TimeSyncBroadcaster has none either); call ResetState() if a future session-boundary hook is added.
    public static class GeoStateSyncBroadcaster
    {
        // Throttle for the CONTINUOUS (unreliable pos/rot/range) channel only — discrete transitions ignore it.
        // Starts at TimeSyncBroadcaster's 0.5s; tune to 0.2–0.3s in-game for smoother vehicle motion.
        private const float FlushIntervalSeconds = 0.5f;

        private static float _accum;

        // ONE shared diff/seq core — a single monotonic seq line per (FactionGuid,VehicleID) across BOTH channels.
        private static readonly GeoVehicleStateDiffer _differ = new GeoVehicleStateDiffer();

        // Continuous-ONLY changed records awaiting the throttled unreliable flush; latest record per identity wins.
        private static readonly Dictionary<(string, int), GeoVehicleStateRecord> _continuousPending
            = new Dictionary<(string, int), GeoVehicleStateRecord>();

        // Call once per frame from the host's NetworkEngine.Update().
        public static void Tick(NetworkEngine engine, float deltaTime)
        {
            // Host-only: the host is the authority; clients never produce 0x35.
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            // Do NOT snapshot/broadcast while applying inbound state or a relayed command (would re-emit
            // a mirrored apply as a fresh host snapshot).
            if (EntityReplicationScope.IsApplying || CommandRelay.IsApplying) return;

            try
            {
                var geoLevel = GeoBridge.GetGeoLevelController();
                if (geoLevel == null) return;

                var factions = AccessTools.Field(geoLevel.GetType(), "Factions")?.GetValue(geoLevel) as IEnumerable;
                if (factions == null) return;

                List<GeoVehicleStateRecord> reliableRecords = null; // discrete transitions, sent NOW

                foreach (var faction in factions)
                {
                    if (faction == null) continue;
                    var vehicles = AccessTools.Property(faction.GetType(), "Vehicles")?.GetValue(faction) as IEnumerable;
                    if (vehicles == null) continue;

                    foreach (var v in vehicles)
                    {
                        if (v == null) continue;

                        var snap = GeoBridge.RecordVehicleState(v);
                        var diffed = _differ.Diff(snap);
                        if (diffed.ChangedMask == 0) continue; // nothing moved for this identity

                        var key = (diffed.FactionGuid ?? "", diffed.VehicleID);

                        if (GeoVehicleStateDiffer.DiscreteBits(diffed.ChangedMask) != 0)
                        {
                            // Transition (Travelling/CurrentSite/DestinationSites/HitPoints) — send the whole
                            // changed record reliably this frame. A newer combined record supersedes any older
                            // buffered continuous-only record for this identity.
                            (reliableRecords ?? (reliableRecords = new List<GeoVehicleStateRecord>())).Add(diffed);
                            _continuousPending.Remove(key);
                        }
                        else
                        {
                            // Continuous-only change — hold the freshest record (with its assigned seq) for the
                            // throttled unreliable flush. Latest wins; the held seq keeps it lossless.
                            _continuousPending[key] = diffed;
                        }
                    }
                }

                // DISCRETE channel: immediate, reliable, ordered. Never throttled.
                if (reliableRecords != null && reliableRecords.Count > 0)
                    engine.BroadcastGeoStateDiff(new GeoStateDiff { Records = reliableRecords }, reliable: true);

                // CONTINUOUS channel: throttled accumulator → one batched unreliable envelope on flush.
                _accum += deltaTime;
                if (_accum >= FlushIntervalSeconds)
                {
                    _accum = 0f;
                    if (_continuousPending.Count > 0)
                    {
                        var contList = new List<GeoVehicleStateRecord>(_continuousPending.Values);
                        engine.BroadcastGeoStateDiff(new GeoStateDiff { Records = contList }, reliable: false);
                        _continuousPending.Clear();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Multipleer] GeoStateSyncBroadcaster.Tick failed: {ex.Message}");
            }
        }

        // Drop all per-identity diff/seq state + the pending buffer so a fresh co-op session starts clean.
        // Not wired to any session boundary yet (no Task-10 bullet; TimeSyncBroadcaster has none either) —
        // exposed for a future session-reset hook.
        public static void ResetState()
        {
            _differ.Reset();
            _continuousPending.Clear();
            _accum = 0f;
        }
    }
}
