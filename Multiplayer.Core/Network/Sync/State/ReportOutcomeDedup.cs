namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// PURE (Unity-free) consecutive-duplicate guard for mirrored MISSION-OUTCOME modals (Batch-2 P3, interim
    /// until the Batch-3 P5 occurrence-id lands on the whole 0x69 rail). The 0x69 report channel has NO
    /// occurrence id yet, and the STUN reliable transport deliberately sends twice — without a guard a
    /// double-delivered outcome would queue TWO identical persistent modals back-to-back on the client.
    /// The guard keys on the ENCODED payload bytes and blocks only an IMMEDIATE repeat (the transport
    /// double-send arrives back-to-back): a different outcome in between re-arms, so a genuinely repeated
    /// outcome later in the campaign (same site, same def, same result) is never falsely dropped.
    /// Reset at the save-transfer boundary (SyncEngine.ResetEventMirror) with the other mirror state.
    /// </summary>
    public sealed class ReportOutcomeDedup
    {
        private byte[] _last;

        /// <summary>True iff <paramref name="encodedPayload"/> differs from the immediately-preceding accepted
        /// outcome payload (which it then replaces). A duplicate back-to-back delivery returns false.</summary>
        public bool ShouldShow(byte[] encodedPayload)
        {
            if (encodedPayload == null) return false;
            if (_last != null && SameBytes(_last, encodedPayload)) return false;
            _last = (byte[])encodedPayload.Clone();
            return true;
        }

        /// <summary>Boundary reset (save-transfer / reload): forget the last outcome.</summary>
        public void Reset() => _last = null;

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }
}
