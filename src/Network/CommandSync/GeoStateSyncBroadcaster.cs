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

        // PERF (host-lag fix): cadence of the whole faction×vehicle snapshot/diff WALK. The expensive native
        // RecordVehicleState used to run at 60Hz per vehicle; gate it to ~10Hz instead. Discrete-change
        // (arrival/departure/HP) detection latency becomes ≤0.1s — fine for a strategic geoscape mirror. The
        // 0.5s continuous flush above is an exact multiple of this, so the two cadences stay coherent.
        private const float SnapshotIntervalSeconds = 0.1f;

        private static float _accum;          // continuous-flush accumulator (0.5s)
        private static float _snapshotAccum;  // snapshot/diff WALK accumulator (0.1s)

        // [DIAGB] TEMPORARY (logging only, no behavior change). ~1s accumulator so the per-snapshot summary
        // line is throttled to roughly 1/sec rather than firing every 0.1s snapshot tick. Reverted with the DIAG set.
        private static float _diagLogAccum;

        // ONE shared diff/seq core — a single monotonic seq line per (FactionGuid,VehicleID) across BOTH channels.
        private static readonly GeoVehicleStateDiffer _differ = new GeoVehicleStateDiffer();

        // Continuous-ONLY changed records awaiting the throttled unreliable flush; latest record per identity wins.
        private static readonly Dictionary<(string, int), GeoVehicleStateRecord> _continuousPending
            = new Dictionary<(string, int), GeoVehicleStateRecord>();

        // PERF dirty pre-check: last CHEAP signature recorded per identity (managed pos/rot + Travelling/
        // CurrentSite/HitPoints). On a snapshot tick, if a vehicle's fresh cheap sig matches its last one
        // within epsilon, we SKIP the expensive RecordVehicleState entirely (steady-state idle = alloc-free).
        private static readonly Dictionary<(string, int), GeoVehicleCheapSig> _lastSig
            = new Dictionary<(string, int), GeoVehicleCheapSig>();

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

                // PERF: gate the expensive faction×vehicle snapshot/diff WALK to ~10Hz (was every frame).
                // Both accumulators advance by real dt every frame so the 0.5s flush below still fires on
                // schedule even on frames where no snapshot is taken.
                _snapshotAccum += deltaTime;
                bool doSnapshot = _snapshotAccum >= SnapshotIntervalSeconds;
                if (doSnapshot) _snapshotAccum = 0f;

                List<GeoVehicleStateRecord> reliableRecords = null; // discrete transitions, sent NOW

                // [DIAGB] TEMPORARY (logging only). Per-snapshot counters for the throttled summary line below.
                // Decide ONCE per tick whether this snapshot's summary should be logged (≈1/sec) so the heavier
                // travelling-list build stays off the hot path on the other ~9 snapshots/sec.
                _diagLogAccum += deltaTime;
                bool diagLogThisTick = doSnapshot && _diagLogAccum >= 1f;
                if (diagLogThisTick) _diagLogAccum = 0f;
                int diagVehicles = 0, diagTravelling = 0, diagRecorded = 0, diagBcastReliable = 0, diagBcastUnreliable = 0;
                List<string> diagTravellingIds = diagLogThisTick ? new List<string>() : null;

                if (doSnapshot)
                {
                    foreach (var faction in factions)
                    {
                        if (faction == null) continue;
                        var vehicles = AccessTools.Property(faction.GetType(), "Vehicles")?.GetValue(faction) as IEnumerable;
                        if (vehicles == null) continue;

                        foreach (var v in vehicles)
                        {
                            if (v == null) continue;
                            diagVehicles++; // [DIAGB] count every vehicle walked this snapshot

                            // PERF dirty pre-check: a CHEAP managed signature (no native fill, no alloc) skips the
                            // expensive RecordVehicleState when nothing relevant moved. Guards pos/rot (epsilon) +
                            // the cheap discrete triggers Travelling/CurrentSite/HitPoints. (DestinationSites-only
                            // re-routes self-heal on the next discrete change — see GeoVehicleCheapSig.)
                            if (GeoBridge.TryGetCheapVehicleSignature(v, out var sig))
                            {
                                // [DIAGB] note Travelling craft (only when we will actually log this tick).
                                if (diagTravellingIds != null && sig.Travelling)
                                {
                                    diagTravelling++;
                                    diagTravellingIds.Add($"{sig.FactionGuid ?? "EMPTY"}#{sig.VehicleID}");
                                }
                                var sigKey = (sig.FactionGuid ?? "", sig.VehicleID);
                                if (_lastSig.TryGetValue(sigKey, out var prevSig) && !SigChanged(prevSig, sig))
                                    continue; // unchanged since last snapshot → skip the heavy record
                                _lastSig[sigKey] = sig; // remember for next tick (first-seen also records below)
                            }

                            var snap = GeoBridge.RecordVehicleState(v);
                            diagRecorded++; // [DIAGB] passed the cheap pre-check → expensive record taken
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

                    // DISCRETE channel: immediate, reliable, ordered. Never throttled (now at snapshot cadence).
                    if (reliableRecords != null && reliableRecords.Count > 0)
                    {
                        engine.BroadcastGeoStateDiff(new GeoStateDiff { Records = reliableRecords }, reliable: true);
                        diagBcastReliable++; // [DIAGB] one reliable envelope sent this tick
                        // [DIAGB] one line per actual send (NOT throttled — discrete sends are rare).
                        foreach (var r in reliableRecords)
                            Debug.Log($"[Multipleer] DIAGB send: {(string.IsNullOrEmpty(r.FactionGuid) ? "EMPTY" : r.FactionGuid)}#{r.VehicleID} reliable=true mask={r.ChangedMask} seq={r.Seq} records={reliableRecords.Count}");
                    }
                }

                // CONTINUOUS channel: throttled accumulator → one batched unreliable envelope on flush.
                _accum += deltaTime;
                if (_accum >= FlushIntervalSeconds)
                {
                    _accum = 0f;
                    if (_continuousPending.Count > 0)
                    {
                        var contList = new List<GeoVehicleStateRecord>(_continuousPending.Values);
                        engine.BroadcastGeoStateDiff(new GeoStateDiff { Records = contList }, reliable: false);
                        diagBcastUnreliable++; // [DIAGB] one unreliable envelope sent this flush
                        // [DIAGB] one line per actual send (NOT throttled — flushes are ~2/sec at most).
                        foreach (var r in contList)
                            Debug.Log($"[Multipleer] DIAGB send: {(string.IsNullOrEmpty(r.FactionGuid) ? "EMPTY" : r.FactionGuid)}#{r.VehicleID} reliable=false mask={r.ChangedMask} seq={r.Seq} records={contList.Count}");
                        _continuousPending.Clear();
                    }
                }

                // [DIAGB] TEMPORARY throttled (~1/sec) snapshot summary — see, on the host, whether the walk
                // observes Travelling craft and whether anything was recorded/broadcast this tick.
                if (diagLogThisTick)
                {
                    var travel = diagTravellingIds != null && diagTravellingIds.Count > 0
                        ? string.Join(",", diagTravellingIds) : "";
                    Debug.Log($"[Multipleer] DIAGB snapshot: vehicles={diagVehicles} travelling=[{travel}] recorded={diagRecorded} bcastReliable={diagBcastReliable} bcastUnreliable={diagBcastUnreliable}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Multipleer] GeoStateSyncBroadcaster.Tick failed: {ex.Message}");
            }
        }

        // Cheap dirty test: did the vehicle's signature change since its last recorded snapshot? Continuous
        // pos/rot/range use the SAME epsilon as the differ (so the pre-check and the differ agree — a sig the
        // differ would treat as unchanged is also skipped here); the discrete triggers compare exactly.
        private static bool SigChanged(in GeoVehicleCheapSig a, in GeoVehicleCheapSig b)
        {
            const float eps = GeoVehicleStateDiffer.Epsilon;
            if (Math.Abs(a.PosX - b.PosX) > eps || Math.Abs(a.PosY - b.PosY) > eps || Math.Abs(a.PosZ - b.PosZ) > eps)
                return true;
            if (Math.Abs(a.RotX - b.RotX) > eps || Math.Abs(a.RotY - b.RotY) > eps
                || Math.Abs(a.RotZ - b.RotZ) > eps || Math.Abs(a.RotW - b.RotW) > eps)
                return true;
            if (Math.Abs(a.RangeRemaining - b.RangeRemaining) > eps)
                return true;
            return a.Travelling != b.Travelling || a.CurrentSiteId != b.CurrentSiteId || a.HitPoints != b.HitPoints;
        }

        // Drop all per-identity diff/seq state + the pending buffer so a fresh co-op session starts clean.
        // Not wired to any session boundary yet (no Task-10 bullet; TimeSyncBroadcaster has none either) —
        // exposed for a future session-reset hook.
        public static void ResetState()
        {
            _differ.Reset();
            _continuousPending.Clear();
            _lastSig.Clear();
            _accum = 0f;
            _snapshotAccum = 0f;
            _diagLogAccum = 0f; // [DIAGB] TEMPORARY — reverted with the DIAG set
        }
    }
}
