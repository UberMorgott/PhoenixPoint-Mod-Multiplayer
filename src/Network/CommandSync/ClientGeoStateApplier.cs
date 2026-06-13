using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Multipleer.Network.CommandSync
{
    // SD-AIDR INC-3a (Task 9): client-only apply of a host 0x35 GeoStateDiff envelope — the all-faction
    // vehicle state mirror. Model on ClientEntityOpApplier.cs:16-93 (client gate + EntityReplicationScope
    // + GeoLevel null-guard + per-op branch) and ClientTimeMirror.cs:9-19 (whole-apply try/catch, host
    // owns truth). The host walks all factions x vehicles, diffs vs last-sent and broadcasts only CHANGED
    // records (unreliable continuous pos/rot/range, reliable discrete transitions); this is the pure
    // mirror that writes those values back.
    //
    // Per record (INC-3a produces ONLY GeoStateScope.Vehicle; other scopes are forward-compat — the codec
    // already self-delimits / skips them at decode):
    //   (a) SEQ GUARD — per-(FactionGuid,VehicleID) lastAppliedSeq; drop record.Seq <= lastApplied so a
    //       stale/out-of-order UNRELIABLE pos packet never clobbers a newer one (newest-wins).
    //   (b) RESOLVE — GeoBridge.FindVehicleByFactionAndId (strict non-empty guid for non-Phoenix; Task 4
    //       bug fix). Absent vehicle => its 0x36 VehicleCreated has not applied yet => skip gracefully
    //       WITHOUT advancing the seq; the next periodic host push self-heals once the vehicle exists.
    //   (c) APPLY — FIRST mirror of an identity (and, in a later slice, a CRC-flagged full correction,
    //       spec 6) => HEAVY GeoBridge.ApplyVehicleStateFull (native ProcessInstanceData: fills unsynced
    //       fields, fires no events, starts no NavigateRoutine); thereafter LIGHT GeoBridge.ApplyVehicleState
    //       (direct reflected setters, cheap per-tick). The client NEVER calls StartTravel on a mirrored
    //       vehicle — travel is Travelling=true + DestinationSites + per-tick pos/rot/range pushes
    //       (last-writer-wins overwrites any NavigateRoutine output).
    //
    // The whole record loop runs inside using (EntityReplicationScope.Enter()) so any host re-broadcast
    // postfix the native apply trips (HostEntityOpBroadcast.ShouldBroadcast) sees IsApplying and does NOT
    // re-emit. DIAGB: post-apply read-back log (rate-limited ~3/sec PER identity + one-shot unresolved)
    // emitting appliedPos / Travelling / CurrentSite + seq/mask, to confirm the mirror actually wrote vs
    // NavigateRoutine fighting it AND that applies keep flowing (seq>1) past the load.
    // Removed in Phase B (Task 13) after the in-game GATE passes.
    public static class ClientGeoStateApplier
    {
        // Newest-wins seq guard per stable (FactionGuid,VehicleID) identity.
        private static readonly Dictionary<(string, int), ulong> _lastAppliedSeq =
            new Dictionary<(string, int), ulong>();

        // Identities that have had at least one FULL (heavy) mirror — first mirror uses ProcessInstanceData.
        private static readonly HashSet<(string, int)> _firstMirrorDone = new HashSet<(string, int)>();

        // DIAGB per-identity apply RATE GATE — next allowed log time (realtimeSinceStartup) per identity.
        // Caps the read-back log to ~3/sec PER identity so EVERY apply during travel (seq>1, continuous +
        // discrete) is visible without flooding — vs the old every-30th gate which hid post-load applies.
        // Phase-B revert removes the logging; this stays harmless.
        private static readonly Dictionary<(string, int), float> _diagbApplyNextLogTime =
            new Dictionary<(string, int), float>();

        // One-shot DIAG3 gate for the UNRESOLVED (no-vehicle-yet) path — unchanged (no apply happened).
        private static readonly HashSet<(string, int)> _diag3Unresolved = new HashSet<(string, int)>();

        public static void Apply(GeoStateDiff diff)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return; // client-only; host owns truth

            if (diff.Records == null || diff.Records.Count == 0) return;

            try
            {
                var geoLevel = GeoBridge.GetGeoLevelController();
                if (geoLevel == null) { Debug.LogWarning("[Multipleer] GeoStateDiff apply: no GeoLevelController."); return; }

                using (EntityReplicationScope.Enter())
                {
                    foreach (var record in diff.Records)
                        ApplyVehicleRecord(geoLevel, record);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Multipleer] ClientGeoStateApplier apply failed: {ex}");
            }
        }

        private static void ApplyVehicleRecord(object geoLevel, GeoVehicleStateRecord record)
        {
            var identity = (record.FactionGuid ?? "", record.VehicleID);

            // (a) SEQ GUARD: drop stale / out-of-order (the unreliable channel can reorder/duplicate).
            if (_lastAppliedSeq.TryGetValue(identity, out var lastSeq) && record.Seq <= lastSeq) return;

            // (b) RESOLVE by (FactionGuid,VehicleID) — strict non-empty guid for non-Phoenix (Task 4 fix).
            var vehicle = GeoBridge.FindVehicleByFactionAndId(geoLevel, record.FactionGuid, record.VehicleID);
            if (vehicle == null)
            {
                // 0x36 VehicleCreated not applied yet (or removed). Skip WITHOUT advancing the seq so the
                // next periodic push re-applies once the vehicle exists. One-shot DIAG3 for the unresolved id.
                if (_diag3Unresolved.Add(identity))
                    Debug.Log($"[Multipleer] DIAG3 GeoState: id {record.FactionGuid}#{record.VehicleID} UNRESOLVED (no vehicle yet), skip.");
                return;
            }

            // (c) APPLY: first mirror of this identity => HEAVY (ProcessInstanceData) so unsynced fields fill;
            // thereafter LIGHT reflected setters (cheap per-tick).
            //
            // SWITCH-A: drive the NATIVE NavigateRoutine off the mirrored travel intent so pos+heading+arc are
            // native by construction. On a NON-first record the driver runs FIRST so IsNativeTravelling is
            // up to date when ApplyVehicleState decides whether to skip its Surface pos/rot writes (the routine
            // owns those during travel; a Travelling=true start enters native mode, an arrival exits it). On
            // the FIRST mirror the heavy full apply must place the craft first, THEN the driver starts the
            // routine from that placed position. Host-authority transitions (CurrentSite/occupancy) are
            // unaffected — they still land via the suppressed emitters + discrete 0x35.
            bool firstMirror = !_firstMirrorDone.Contains(identity);
            if (firstMirror)
            {
                GeoBridge.ApplyVehicleStateFull(vehicle, record);
                _firstMirrorDone.Add(identity);
                ClientNativeTravelDriver.OnRecord(geoLevel, vehicle, record);
            }
            else
            {
                ClientNativeTravelDriver.OnRecord(geoLevel, vehicle, record);
                GeoBridge.ApplyVehicleState(vehicle, record);
            }

            _lastAppliedSeq[identity] = record.Seq;

            // DIAGB: post-apply read-back — confirms the mirror WROTE the host's values (vs a client
            // NavigateRoutine fighting it) AND that applies keep arriving (seq>1) past the load mirror.
            // Reads back the same accessors GeoBridge writes. Rate-limited to ~3/sec PER identity so EVERY
            // apply (continuous pos + discrete) during travel is visible without flooding.
            float now = Time.realtimeSinceStartup;
            _diagbApplyNextLogTime.TryGetValue(identity, out var nextLogTime);
            if (now >= nextLogTime)
            {
                _diagbApplyNextLogTime[identity] = now + 0.33f;
                var vt = vehicle.GetType();
                var surface = AccessTools.Property(vt, "Surface")?.GetValue(vehicle) as Transform;
                var appliedPos = surface != null ? surface.position : Vector3.zero;
                var travelling = AccessTools.Property(vt, "Travelling")?.GetValue(vehicle);
                var currentSite = AccessTools.Property(vt, "CurrentSite")?.GetValue(vehicle);
                Debug.Log($"[Multipleer] DIAGB apply: id {record.FactionGuid}#{record.VehicleID} RESOLVED " +
                          $"firstMirror={firstMirror} seq={record.Seq} mask={record.ChangedMask} -> " +
                          $"appliedPos={appliedPos} Travelling={travelling} CurrentSite={(currentSite != null ? "set" : "null")}");
            }
        }
    }
}
