using System.Collections.Generic;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// SHARED per-surface sequencing primitive (unified backbone spec §2.2, "ONE Seq"). HOST: monotonic
    /// per-surface seq source for live outcomes/deltas. CLIENT: last-writer-wins guard. PURE (no engine
    /// types) → unit-tested. One instance per live session per side; reset on teardown.
    ///
    /// Seq is assigned PER SURFACE (each surfaceId has an independent monotonic stream) so an outcome on one
    /// surface never suppresses an outcome on another. The host emits over a reliable, per-peer ORDERED
    /// transport, so a strictly-greater check is a sufficient last-writer-wins guard (a stale duplicate/
    /// re-send is dropped; nothing newer can be overtaken).
    ///
    /// Lifted verbatim from the tactical-only TacticalLiveSeq so BOTH the tactical live rail and the geoscape
    /// envelope surfaces share ONE seq abstraction. TacticalLiveSeq now derives from this and only adds the
    /// tactical-specific BeginDeployCaptureMission hook.
    /// </summary>
    public class SurfaceSeq
    {
        private readonly Dictionary<ushort, uint> _hostNext = new Dictionary<ushort, uint>();
        private readonly Dictionary<ushort, uint> _clientLast = new Dictionary<ushort, uint>();

        /// <summary>HOST: take the next monotonic seq for a surface (starts at 1).</summary>
        public uint Next(ushort surfaceId)
        {
            _hostNext.TryGetValue(surfaceId, out var cur);
            uint next = cur + 1;
            _hostNext[surfaceId] = next;
            return next;
        }

        /// <summary>
        /// A HOST STREAM THAT BEGINS AGAIN AT 1 IS A NEW EPOCH, NOT A STALE RE-DELIVERY.
        ///
        /// Several streams are per-BATTLE, not per-session: <see cref="Reset"/> runs on both peers at
        /// tactical teardown and <see cref="Next"/> hands out 1 again for the next battle. The two resets
        /// are NOT simultaneous, and one of the host's messages is by construction the last thing sent
        /// AFTER the receiver has torn its own battle down — <c>TacticalTurnSync</c>'s "battle LEFT, every
        /// peer follows", which exists precisely to reach a peer that has not left yet. Measured
        /// 2026-08-07 21:22:28: the client reset at .540 (its own GoToGeoscape → FinishLevel →
        /// TacticalTurnSync.Reset) and applied the host's trailing leave at .658, whose <see cref="Mark"/>
        /// put <c>_clientLast[0x80]</c> back to the OLD battle's cursor. Every 0x80 message of the NEXT
        /// battle then arrived with a smaller seq and was dropped with no log at all: no turn cursor, no
        /// mission end, no leave. The host pressed "return to geoscape", parked on "Waiting for players…"
        /// for 18 minutes, and the ally sat on a battle summary nothing had told it to leave.
        ///
        /// PURE and total so it is falsifiable headless. <c>last &lt;= 1</c> is deliberately NOT a restart:
        /// with <c>last == 0</c> the ordinary <c>seq &gt; last</c> already accepts, and with
        /// <c>last == 1</c> a second seq 1 is the genuine duplicate this guard exists to drop.
        /// </summary>
        public static bool IsStreamRestart(uint seq, uint last) => seq == 1 && last > 1;

        /// <summary>CLIENT: the last seq applied for a surface (0 = nothing yet). Diagnostics only —
        /// a caller that wants to SAY a stream restarted needs the cursor it restarted from.</summary>
        public uint LastApplied(ushort surfaceId)
        {
            _clientLast.TryGetValue(surfaceId, out var last);
            return last;
        }

        /// <summary>CLIENT: true if this seq is newer than the last applied for the surface, or the host's
        /// stream restarted (see <see cref="IsStreamRestart"/>). Does NOT mark (call <see cref="Mark"/>
        /// after a successful apply) so a failed apply can be retried by a re-send.</summary>
        public bool ShouldApply(ushort surfaceId, uint seq)
        {
            _clientLast.TryGetValue(surfaceId, out var last);
            return seq > last || IsStreamRestart(seq, last);
        }

        /// <summary>CLIENT: record the last applied seq for a surface. A restart REWINDS the cursor — it
        /// is the whole point: without it the very next message of the new stream is dropped again.</summary>
        public void Mark(ushort surfaceId, uint seq)
        {
            _clientLast.TryGetValue(surfaceId, out var last);
            if (seq > last || IsStreamRestart(seq, last)) _clientLast[surfaceId] = seq;
        }

        public void Reset()
        {
            _hostNext.Clear();
            _clientLast.Clear();
        }
    }
}
