using System.Collections.Generic;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>Per-observation verdict of the <see cref="DivergenceMonitor"/> window logic.</summary>
    public enum CrcVerdict : byte
    {
        GraceSkip = 0,      // inside the post-reload grace window — comparison skipped
        Match = 1,          // host and local CRC agree (steady state)
        Mismatch = 2,       // first mismatch in a row — could be an in-flight echo, not yet divergence
        Diverged = 3,       // MismatchThreshold consecutive mismatches — the loud-flag TRANSITION
        StillDiverged = 4,  // already flagged; stays quiet until it recovers
        Recovered = 5,      // was flagged, now matches again (e.g. a late echo converged the mirror)
    }

    /// <summary>
    /// Inc5 part 1 — the client-side CRC divergence WINDOW brain (pure, unit-tested; the ReconnectPolicy
    /// Core-slice precedent). One instance per <c>SyncEngine</c>, fed one observation per (round, subset)
    /// by the game-glue probe handler.
    ///
    /// WINDOWING: a probe round can race an in-flight state echo (the host hashes right after mutating,
    /// the client applies a frame later), so ONE mismatch is only a warning — a subset is flagged
    /// DIVERGED after <see cref="MismatchThreshold"/> CONSECUTIVE mismatching rounds (any match resets
    /// its counter). Once flagged it reports <see cref="CrcVerdict.StillDiverged"/> quietly until a
    /// match flips it back (<see cref="CrcVerdict.Recovered"/>) — the loud flag fires exactly once per
    /// divergence episode.
    ///
    /// GRACE: the rca-3 reload boundary (mid-session save-load / co-op save-transfer) arms a real-time
    /// grace window via <see cref="ArmGrace"/> — every peer just loaded the SAME blob, so the mirror is
    /// correct by construction and any pre-reload miss/diverged marks are stale: arming CLEARS them and
    /// skips comparisons until the window elapses. Timestamps are <c>Environment.TickCount</c>-style
    /// signed-ms values (wrap-tolerant unchecked subtraction).
    /// </summary>
    public sealed class DivergenceMonitor
    {
        /// <summary>Post-reload grace: rounds are hourly (seconds of real time at max game speed), so 30 s
        /// real time comfortably covers the blob load + first channel re-seeds without hiding real drift.</summary>
        public const int DefaultGraceMs = 30_000;

        /// <summary>Consecutive mismatching rounds before a subset is flagged diverged.</summary>
        public const int MismatchThreshold = 2;

        private readonly int _graceMs;
        private bool _graceArmed;
        private int _graceArmedAtMs;
        private readonly Dictionary<byte, int> _misses = new Dictionary<byte, int>();     // per-subset consecutive mismatches
        private readonly HashSet<byte> _diverged = new HashSet<byte>();                    // currently-flagged subsets

        public DivergenceMonitor(int graceMs = DefaultGraceMs) => _graceMs = graceMs;

        /// <summary>Arm the grace window at <paramref name="nowMs"/> and drop every pre-boundary mark
        /// (the reload IS the resync — miss counters and diverged flags describe the dead state).</summary>
        public void ArmGrace(int nowMs)
        {
            _graceArmed = true;
            _graceArmedAtMs = nowMs;
            _misses.Clear();
            _diverged.Clear();
        }

        /// <summary>True while the armed grace window is still running. Wrap-tolerant; a negative elapsed
        /// (clock wrapped past the arm point) counts as expired and disarms.</summary>
        public bool InGrace(int nowMs)
        {
            if (!_graceArmed) return false;
            int elapsed = unchecked(nowMs - _graceArmedAtMs);
            if (elapsed >= 0 && elapsed < _graceMs) return true;
            _graceArmed = false;
            return false;
        }

        public bool IsDiverged(byte subsetId) => _diverged.Contains(subsetId);
        public bool AnyDiverged => _diverged.Count > 0;

        /// <summary>Feed one (subset, hostCrc, localCrc) observation of the current round.</summary>
        public CrcVerdict Observe(byte subsetId, uint hostCrc, uint localCrc, int nowMs)
        {
            if (InGrace(nowMs)) return CrcVerdict.GraceSkip;
            if (hostCrc == localCrc)
            {
                _misses.Remove(subsetId);
                return _diverged.Remove(subsetId) ? CrcVerdict.Recovered : CrcVerdict.Match;
            }
            if (_diverged.Contains(subsetId)) return CrcVerdict.StillDiverged;
            _misses.TryGetValue(subsetId, out int n);
            n++;
            if (n >= MismatchThreshold)
            {
                _misses.Remove(subsetId);
                _diverged.Add(subsetId);
                return CrcVerdict.Diverged;
            }
            _misses[subsetId] = n;
            return CrcVerdict.Mismatch;
        }
    }
}
