using System.Collections.Generic;

namespace Multipleer.Network.Sync
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

        /// <summary>CLIENT: true if this seq is newer than the last applied for the surface. Does NOT mark
        /// (call <see cref="Mark"/> after a successful apply) so a failed apply can be retried by a re-send.</summary>
        public bool ShouldApply(ushort surfaceId, uint seq)
        {
            _clientLast.TryGetValue(surfaceId, out var last);
            return seq > last;
        }

        /// <summary>CLIENT: record the last applied seq for a surface.</summary>
        public void Mark(ushort surfaceId, uint seq)
        {
            _clientLast.TryGetValue(surfaceId, out var last);
            if (seq > last) _clientLast[surfaceId] = seq;
        }

        public void Reset()
        {
            _hostNext.Clear();
            _clientLast.Clear();
        }
    }
}
