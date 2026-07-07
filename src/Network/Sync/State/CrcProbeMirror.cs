using System;
using System.Collections.Generic;
using Multiplayer.Network.MessageLayer;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Inc5 part 1 — rolling CRC divergence probe, GAME-GLUE side (the pure window/compare brain is
    /// <see cref="DivergenceMonitor"/>, the canonical subset images + round codec are <see cref="CrcSubsetCrc"/>/
    /// <see cref="CrcProbeCodec"/> in Core). Silent client-mirror divergence is otherwise invisible until
    /// something visibly breaks — this makes it LOUD, cheaply:
    ///
    ///   HOST: once per in-game hour (<c>GeoLevelController.HourTicked</c> — the mist-channel cadence
    ///   precedent, subscribed via the existing <see cref="ResearchStateReflection.SubscribeHourlyTick"/>
    ///   glue with the ResearchChannel instance-rebind guard) it reads each hand-picked DETERMINISTIC state
    ///   subset (wallet / site identities / roster id set / completed research set — never a whole-blob
    ///   serialization, which is float-format + dict-order nondeterministic), CRCs them and broadcasts one
    ///   tiny versioned round on the <c>GeoCrcProbe</c> (0xA9) surface of the existing 0x67 envelope rail
    ///   (no new packet family). The round seq rides the shared <see cref="SurfaceSeq"/> stream.
    ///
    ///   CLIENT: recomputes the SAME subsets over its own mirrored state and feeds the pure monitor —
    ///   a confirmed divergence (2 consecutive mismatching rounds; one can race an in-flight echo) logs
    ///   LOUD with the subset name + raises the native toast (<see cref="SessionNotifier.ShowToast"/>).
    ///   Mid-tactical / mid-load rounds are skipped (no geoscape to hash); the rca-3 reload boundary arms
    ///   the monitor's grace window (every peer just loaded the same blob — mirror correct by construction).
    ///
    /// DETECTION ONLY by design: no auto-resync here (that is the reconnect/self-heal increment — the fix
    /// is a full host re-transfer, not a per-subset patch). Gated on <c>ClientSimFreeze.Enabled</c> like
    /// the 0xA5-0xA7 mirrors: an UNfrozen client legitimately simulates (diverges by design), so flag-OFF
    /// rollback = zero new traffic and zero false alarms.
    /// </summary>
    public sealed class CrcProbeMirror
    {
        private object _hourToken;    // opaque HourTicked token (ResearchStateReflection glue)
        private object _boundLevel;   // the GeoLevelController the token is bound to (reload rebind guard)
        private bool _due;            // an in-game hour elapsed since the last round (set on HourTicked)

        // ─── HOST ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Host per-Tick pump (idempotent): (re)bind the hourly tick on the live geoscape, and when
        /// an hour has elapsed collect + broadcast one probe round. No-op off-geoscape / before bind.</summary>
        public void HostTick(NetworkEngine engine, SurfaceSeq seq)
        {
            if (engine == null || !engine.IsHost || seq == null) return;
            var rt = GeoRuntime.Instance;
            var geo = rt.GeoLevel();
            if (geo == null) return;   // not in geoscape (tactical / mid-load) → no rounds

            // Rebind when the live GeoLevelController changes (reload builds a fresh one — the ResearchChannel
            // stale-instance lesson): never leave the subscription on a dead level.
            if (!ReferenceEquals(geo, _boundLevel))
            {
                Detach();
                _hourToken = ResearchStateReflection.SubscribeHourlyTick(rt, () => _due = true);
                if (_hourToken == null) return;   // couldn't bind yet → retry next Tick
                _boundLevel = geo;
            }

            if (!_due) return;
            _due = false;

            var entries = CollectLocalCrcs(rt);
            if (entries.Count == 0) return;       // nothing readable this hour → skip the round
            uint round = seq.Next(SurfaceIds.GeoCrcProbe);
            engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope,
                SyncProtocol.EncodeEnvelope(SurfaceIds.GeoCrcProbe, SyncKind.StateSnapshot,
                    CrcProbeCodec.Encode(round, entries))));
            Debug.Log("[Multiplayer][geo] HOST crc-probe round=" + round + " " + Describe(entries));
        }

        /// <summary>Drop the hourly subscription (session teardown / level rebind). Idempotent.</summary>
        public void Detach()
        {
            if (_hourToken != null) ResearchStateReflection.Unsubscribe(_hourToken);
            _hourToken = null;
            _boundLevel = null;
            _due = false;
        }

        // ─── CLIENT ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Client: one inbound probe round. Seq-guarded (reliable-transport double-send drop);
        /// skipped whole while mid-tactical/mid-load or inside the post-reload grace window (inside the
        /// monitor). A subset the client cannot read locally right now is skipped, never counted.</summary>
        public void HandleProbe(byte[] payload, SurfaceSeq seq, DivergenceMonitor monitor)
        {
            if (seq == null || monitor == null) return;
            if (!CrcProbeCodec.TryDecode(payload, out uint round, out var entries)) return;
            if (!seq.ShouldApply(SurfaceIds.GeoCrcProbe, round)) return;
            seq.Mark(SurfaceIds.GeoCrcProbe, round);

            var rt = GeoRuntime.Instance;
            if (rt.GeoLevel() == null) return;    // client mid-tactical / mid-load → skip this geoscape round

            int now = Environment.TickCount;
            foreach (var (subsetId, hostCrc) in entries)
            {
                if (!TryComputeLocal(rt, subsetId, out uint localCrc)) continue;   // unreadable ≠ mismatch
                var verdict = monitor.Observe(subsetId, hostCrc, localCrc, now);
                string name = CrcSubsetIds.Name(subsetId);
                switch (verdict)
                {
                    case CrcVerdict.Diverged:
                        // The loud flag: exactly once per divergence episode. Detection only — the resync
                        // lever is the (later) reconnect/self-heal full re-transfer.
                        Debug.LogError("[Multiplayer][geo] CLIENT STATE DIVERGENCE subset=" + name
                                       + " round=" + round
                                       + " hostCrc=0x" + hostCrc.ToString("X8")
                                       + " localCrc=0x" + localCrc.ToString("X8")
                                       + " (mirror drifted from host truth; persists until a reload/re-transfer)");
                        SessionNotifier.ShowToast("Co-op sync divergence detected: " + name);
                        break;
                    case CrcVerdict.Recovered:
                        Debug.Log("[Multiplayer][geo] CLIENT crc-probe subset=" + name + " RECOVERED round=" + round);
                        break;
                    case CrcVerdict.Mismatch:
                        // First miss in a window — commonly a probe racing an in-flight state echo.
                        Debug.LogWarning("[Multiplayer][geo] CLIENT crc-probe subset=" + name
                                         + " mismatch round=" + round
                                         + " hostCrc=0x" + hostCrc.ToString("X8")
                                         + " localCrc=0x" + localCrc.ToString("X8")
                                         + " (transient? confirmed on the next round)");
                        break;
                    // Match / GraceSkip / StillDiverged: silent (hourly steady-state must not spam the log).
                }
            }
        }

        // ─── Shared subset collection (both sides run the SAME reads + canonical Core images) ─────────

        /// <summary>Read + CRC every currently-readable subset. A subset whose source is unavailable is
        /// simply absent from the round (host) / skipped (client) — never a fabricated value.</summary>
        internal static List<(byte subsetId, uint crc)> CollectLocalCrcs(GeoRuntime rt)
        {
            var entries = new List<(byte, uint)>(4);
            if (TryComputeLocal(rt, CrcSubsetIds.Wallet, out uint c1)) entries.Add((CrcSubsetIds.Wallet, c1));
            if (TryComputeLocal(rt, CrcSubsetIds.Sites, out uint c2)) entries.Add((CrcSubsetIds.Sites, c2));
            if (TryComputeLocal(rt, CrcSubsetIds.Roster, out uint c3)) entries.Add((CrcSubsetIds.Roster, c3));
            if (TryComputeLocal(rt, CrcSubsetIds.Research, out uint c4)) entries.Add((CrcSubsetIds.Research, c4));
            return entries;
        }

        private static bool TryComputeLocal(GeoRuntime rt, byte subsetId, out uint crc)
        {
            crc = 0;
            try
            {
                switch (subsetId)
                {
                    case CrcSubsetIds.Wallet:
                    {
                        var slots = WalletApplier.Snapshot(rt);
                        if (slots == null) return false;
                        crc = CrcSubsetCrc.Wallet(slots);
                        return true;
                    }
                    case CrcSubsetIds.Sites:
                    {
                        if (!GeoSiteReflection.TryReadCrcIdentities(rt, out var sites)) return false;
                        crc = CrcSubsetCrc.Sites(sites);
                        return true;
                    }
                    case CrcSubsetIds.Roster:
                    {
                        var rosters = PersonnelReflection.SnapshotSiteRosters(rt);
                        if (rosters == null || rosters.Count == 0) return false;   // empty = unbound, not "no soldiers"
                        var ids = new List<long>();
                        foreach (var r in rosters) ids.AddRange(r.UnitIds);
                        crc = CrcSubsetCrc.Roster(ids);
                        return true;
                    }
                    case CrcSubsetIds.Research:
                    {
                        var snap = ResearchStateReflection.Snapshot(rt);
                        if (snap == null) return false;
                        crc = CrcSubsetCrc.Research(snap.Completed);
                        return true;
                    }
                    default:
                        return false;   // unknown future host subset → skip (forward-compat)
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][geo] crc-probe subset " + CrcSubsetIds.Name(subsetId)
                               + " read failed (skipped): " + ex.Message);
                crc = 0;
                return false;
            }
        }

        private static string Describe(List<(byte subsetId, uint crc)> entries)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var (id, crc) in entries)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(CrcSubsetIds.Name(id)).Append("=0x").Append(crc.ToString("X8"));
            }
            return sb.ToString();
        }
    }
}
