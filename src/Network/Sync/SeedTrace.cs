using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// TEMPORARY breadcrumb tracer for the host save-load geoscape SEED/FLUSH — the path that dies
    /// silently (suspected uncatchable Mono StackOverflowException) on the FIRST full channel seed after
    /// a co-op save-reload. Each <see cref="Mark"/> names the step just ENTERED; the LAST line in the log
    /// before the process EOF names the offending step/channel.
    ///
    /// Durability: lines carry the "[Multiplayer]" prefix, so <c>MultiplayerLog</c> mirrors each one into
    /// &lt;persistentDataPath&gt;/Multiplayer/multiplayer.log through a StreamWriter with AutoFlush=true —
    /// i.e. every breadcrumb is handed to the OS BEFORE the next step runs and survives the hard death
    /// (Unity's own Player.log is buffered and would lose the tail). No separate file writer is needed.
    ///
    /// Spam control: Marks fire only inside an ARMED window opened at each seed entry point
    /// (<see cref="Arm"/> from the GeoSite fresh-map rebind and BroadcastAllChannels), then auto-silence
    /// after <see cref="ArmTicks"/> host ticks — steady-state play never spams the log or the frame.
    /// Delete this file and its call sites once the SO is pinned.
    /// </summary>
    internal static class SeedTrace
    {
        // Master gate — ON while we hunt the seed-death SO. Set false to silence everything.
        public const bool Enabled = true;

        private const int ArmTicks = 1200;   // ~20 s at 60 fps
        private static int _budget;

        /// <summary>True while enabled and inside the armed window — cheap guard for concat-heavy marks.</summary>
        public static bool Active => Enabled && _budget > 0;

        /// <summary>Open the trace window at a seed entry point. Idempotent re-arm.</summary>
        public static void Arm() { if (Enabled) _budget = ArmTicks; }

        /// <summary>Age the window one host tick. Cheap; call unconditionally from the host tick.</summary>
        public static void FrameTick() { if (_budget > 0) _budget--; }

        /// <summary>Record a breadcrumb when <see cref="Active"/>.</summary>
        public static void Mark(string step)
        {
            if (!Active) return;
            Debug.Log("[Multiplayer] SeedTrace: " + step);
        }
    }
}
