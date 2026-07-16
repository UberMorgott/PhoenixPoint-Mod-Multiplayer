using System.Collections.Generic;

namespace Multiplayer.Network
{
    /// <summary>
    /// Receiver-side co-op load state. Merges RosterProgress rows monotonic-max per slot:
    /// phase only advances; percent only increases within a phase; advancing the phase resets
    /// the percent baseline. Also tracks event-driven done (LoadComplete) for the all-done gate.
    /// Pure logic — no UnityEngine dependency.
    /// </summary>
    public sealed class RosterProgressTracker
    {
        private readonly Dictionary<byte, (byte phase, byte percent)> _state
            = new Dictionary<byte, (byte, byte)>();
        private readonly HashSet<byte> _done = new HashSet<byte>();

        /// <summary>Merge one (slot, phase, percent) observation; returns true if state changed.</summary>
        public bool Merge(byte slot, byte phase, byte percent)
        {
            if (!_state.TryGetValue(slot, out var cur))
            {
                _state[slot] = (phase, percent);
                return true;
            }
            if (phase < cur.phase) return false;                       // stale phase
            if (phase == cur.phase && percent <= cur.percent) return false; // stale percent
            _state[slot] = (phase, percent);
            return true;
        }

        /// <summary>Current (phase, percent) for a slot; (0,0) if never seen.</summary>
        public (byte phase, byte percent) Get(byte slot)
            => _state.TryGetValue(slot, out var v) ? v : ((byte)0, (byte)0);

        public void MarkDone(byte slot) => _done.Add(slot);

        /// <summary>Clear all progress + done state so a fresh co-op session starts clean
        /// (the coordinator reuses one tracker across sessions — avoids stale overlay rows).</summary>
        public void Reset()
        {
            _state.Clear();
            _done.Clear();
        }

        public bool IsDone(byte slot) => _done.Contains(slot);

        /// <summary>All expected slots have reported LoadComplete.</summary>
        public bool AllDone(IEnumerable<byte> expectedSlots)
        {
            foreach (var s in expectedSlots)
                if (!_done.Contains(s)) return false;
            return true;
        }

        /// <summary>Convert a native 0..1 load progress to a clamped, floored 0..100 byte.</summary>
        public static byte ProgressByte(float progress)
        {
            if (progress <= 0f) return 0;
            if (progress >= 1f) return 100;
            return (byte)System.Math.Floor(progress * 100f);
        }

        /// <summary>Phase-2 (native world-load) is active from BEGIN (begun) until this peer's load is done.</summary>
        public static bool InPhase2(bool begun, bool loadCompleteSent) => begun && !loadCompleteSent;

        /// <summary>Combined 0..1 overlay fill across BOTH load phases (2 phases): phase 0 (download) maps its
        /// 0..100% to 0..0.5, phase 1 (native load) to 0.5..1.0. So a peer that finished the instant loopback
        /// download (phase 0, 100%) shows a HALF bar — not full — and the bar only fills completely when it is
        /// actually loaded (phase 1, 100%). Continuous across the phase bump: (0,100) and (1,0) both map to 0.5.</summary>
        public static float CombinedFill(byte phase, byte percent) => (phase * 100 + percent) / 200f;
    }
}
