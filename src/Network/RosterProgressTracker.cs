using System.Collections.Generic;

namespace Multipleer.Network
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

        public bool IsDone(byte slot) => _done.Contains(slot);

        /// <summary>All expected slots have reported LoadComplete.</summary>
        public bool AllDone(IEnumerable<byte> expectedSlots)
        {
            foreach (var s in expectedSlots)
                if (!_done.Contains(s)) return false;
            return true;
        }
    }
}
