using System.Collections.Generic;
using System.Diagnostics;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// A GAP MUST NEVER BE PERMANENT, AND IT MUST NEVER BE A WAIT ON A HUMAN.
    ///
    /// The journal head can name a window whose native raise this peer has not produced yet — the payload
    /// arrived before the local queue caught up. The gate holds, but only for a bounded, armed interval
    /// that ends BY ITSELF (§7.6: waiting on work that ends by itself is allowed; waiting on another
    /// human's ACTION never is). The authoritative resolution is still the host-minted void record of
    /// §A.5 — without an explicit void, two peers time out differently and diverge (FIX gap-fill, §2.5).
    /// This timer is the safety net under that void, not a replacement for it.
    ///
    /// A BCL Stopwatch deliberately, for the same reason RailMeta's digest clock is one:
    /// UnityEngine.Time is an ECall that throws outside the player, and RailCheck and RailSim both
    /// execute this class in-process.
    /// </summary>
    internal static class WindowGap
    {
        /// <summary>How long a gap may hold the drain. Generous against the measured 363 ms inter-channel
        /// skew and still far under a player's patience.</summary>
        internal const double SelfReleaseSeconds = 2.0;

        private static readonly Dictionary<uint, double> _armedAt = new Dictionary<uint, double>();
        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        private static readonly HashSet<uint> _logged = new HashSet<uint>();

        /// <summary>TRUE = the gap has outlived its armed interval and the drain proceeds anyway. Arms the
        /// timer on first sight of <paramref name="pos"/>.</summary>
        internal static bool SelfReleased(uint pos) => SelfReleasedAt(pos, _clock.Elapsed.TotalSeconds);

        /// <summary>The decision itself, with the clock INJECTED so a law and the harness execute the real
        /// one rather than reading its IL.</summary>
        internal static bool SelfReleasedAt(uint pos, double now)
        {
            double armed;
            if (!_armedAt.TryGetValue(pos, out armed)) { _armedAt[pos] = now; return false; }
            if (now - armed < SelfReleaseSeconds) return false;
            if (_logged.Add(pos))
                MpLog.LogWarning("[MP][windows] journal position " + pos + " self-released after " +
                                 SelfReleaseSeconds + "s — its raise never reached this peer's native " +
                                 "queue. The authoritative resolution is a host-minted void; this timer " +
                                 "exists so a gap can never be permanent");
            return true;
        }

        /// <summary>A position that was voided or presented no longer needs a timer.</summary>
        internal static void Forget(uint pos) { _armedAt.Remove(pos); _logged.Remove(pos); }

        internal static void Reset() { _armedAt.Clear(); _logged.Clear(); }
    }
}
