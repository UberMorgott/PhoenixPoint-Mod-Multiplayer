using System;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// HOST-side counters for the Batch-3 display rails (pure C#, unit-testable — the same shape as
    /// <c>EventOccurrenceIds</c>' plain monotonic counter, never time/Random based).
    ///   • <see cref="NextSeq"/> — the P4 <c>displaySeq</c> (u32, skips 0 = the "unstamped/legacy" wire
    ///     sentinel): ONE monotonic order across ALL mirrored display kinds (event raise 0x65, report modal
    ///     0x69, cutscene), assigned at the host's own <c>GeoscapeViewSwitchQuery.QueryStateSwitch</c>
    ///     fire-time (<c>ViewSwitchQueryStampPatch</c>) so the client can reproduce the host's native display
    ///     order instead of re-deriving one from arrival order.
    ///   • <see cref="NextReportOccId"/> — the P5 report-rail occurrence id (u16, skips 0): stamps every
    ///     0x69/0x6C send so the client's <see cref="ReportOccurrenceDedup"/> makes the STUN reliable
    ///     transport's deliberate double-send an idempotent no-op (the exact pattern EventOccurrenceIds gives
    ///     the 0x65/0x66 rail). Wraparound is harmless for both: only in-flight ids must not collide.
    /// <see cref="Reset"/> runs at the save-transfer boundary (<c>SyncEngine.ResetEventMirror</c>, spec Batch-3
    /// risk note) together with the client-side queue + dedup resets, so post-transfer displays never compare
    /// against pre-transfer stamps.
    /// </summary>
    public static class DisplaySequence
    {
        private static readonly object _lock = new object();
        private static uint _seq;          // monotonic; 0 stays the "unstamped/legacy" wire sentinel
        private static ushort _reportOcc;  // monotonic; 0 stays the "no id/legacy" wire sentinel

        /// <summary>Next unified display-order stamp (u32, never 0).</summary>
        public static uint NextSeq()
        {
            lock (_lock)
            {
                _seq = _seq == uint.MaxValue ? 1u : _seq + 1;
                return _seq;
            }
        }

        /// <summary>Next report-rail (0x69/0x6C) occurrence id (u16, never 0).</summary>
        public static ushort NextReportOccId()
        {
            lock (_lock)
            {
                _reportOcc = _reportOcc == ushort.MaxValue ? (ushort)1 : (ushort)(_reportOcc + 1);
                return _reportOcc;
            }
        }

        /// <summary>Save-transfer boundary / test reset — both counters restart (0 sentinels preserved).</summary>
        public static void Reset()
        {
            lock (_lock) { _seq = 0; _reportOcc = 0; }
        }
    }

    /// <summary>
    /// ONE-SLOT host-side stamp handoff between the <c>GeoscapeViewSwitchQuery.QueryStateSwitch</c> postfix
    /// (<c>ViewSwitchQueryStampPatch</c> — records) and the per-rail broadcast patches that run LATER IN THE
    /// SAME call stack (event raise / report modal / cutscene postfixes — consume). QueryStateSwitch is
    /// synchronous inside every native opener, so the record always precedes its consumer's take and a
    /// single slot cannot be stolen across stacks; the consumer type-matches the pushed view-state name so a
    /// stamp left by a NON-mirrored push (deploy prompt, replenish, …) is never mis-consumed — it is simply
    /// overwritten by the next push. Main-thread only (all geoscape UI), so no lock is needed; pure C#.
    /// </summary>
    public static class DisplayStamp
    {
        private static string _stateTypeName;
        private static uint _seq;
        private static int _priority;

        /// <summary>Record the stamp for the view-state push that just fired (host QueryStateSwitch postfix).</summary>
        public static void Record(string stateTypeName, uint seq, int priority)
        {
            _stateTypeName = stateTypeName;
            _seq = seq;
            _priority = priority;
        }

        /// <summary>
        /// Consume the recorded stamp iff its pushed state-type name contains <paramref name="stateTypeFragment"/>
        /// (e.g. "GeoscapeEvent" / "UIStateGeoModal" / "UIStateGeoCutscene"). False = no matching stamp (native
        /// push skipped / stamp patch unbound) — the caller falls back to a fresh <see cref="DisplaySequence.NextSeq"/>.
        /// </summary>
        public static bool TryTake(string stateTypeFragment, out uint seq, out int priority)
        {
            seq = 0;
            priority = 0;
            if (_stateTypeName == null || stateTypeFragment == null) return false;
            if (_stateTypeName.IndexOf(stateTypeFragment, StringComparison.Ordinal) < 0) return false;
            seq = _seq;
            priority = _priority;
            _stateTypeName = null;   // consume-once
            return true;
        }

        /// <summary>Boundary/test reset.</summary>
        public static void Reset()
        {
            _stateTypeName = null;
            _seq = 0;
            _priority = 0;
        }
    }
}
