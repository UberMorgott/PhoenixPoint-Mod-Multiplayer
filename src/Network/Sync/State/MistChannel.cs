using System;
using System.Collections.Generic;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// State channel #8 — MIST field mirror (WA-1, gap 4a). The client geoscape sim is FROZEN, so its
    /// <c>MistRendererSystem</c> hourly producer (a sim-Timing updateable) never runs and the mist never
    /// spreads/recedes on the client. The host echoes the native serialization gift instead: once per in-game
    /// hour (cheap <c>_hoursPassed</c> poll — the producer increments it) it runs
    /// <c>RecordInstanceData()</c>, content-hash-skips an unchanged field, and ships the deflate bytes; the
    /// client redraws via the native <c>ProcessInstanceData</c> path (see <see cref="MistReflection"/>).
    ///
    /// CHUNKED EMISSION on the standard rail: one emission can exceed a safe packet (late-game field is
    /// hundreds of KB; 32 KB is the proven per-message bound across the three transports — see
    /// <c>SaveTransferCoordinator.ChunkSize</c>), so it is split into ≤<see cref="ChunkBytes"/> chunks and
    /// each chunk rides ONE ordinary versioned ch#8 flush on the GeoState (0xA1) rail — no new packet family
    /// (canon). <see cref="AttachHost"/> doubles as the per-tick pump (it is invoked every host Tick BEFORE
    /// the dirty-flush): it re-marks the channel dirty while chunks are queued, so the queue drains one chunk
    /// per Tick (~1.4 MB/s @60fps — a full late-game emission lands in well under a second).
    /// <see cref="MistReassembler"/> makes the client apply last-wins + idempotent; a chunk lost on the
    /// best-effort transport just leaves that emission incomplete until the next hourly one supersedes it.
    ///
    /// JOIN semantics: the join save-blob already carries the mist (it is part of the native save), so a late
    /// joiner starts correct; <see cref="Snapshot"/> with an EMPTY queue re-enqueues the latest full emission
    /// (BroadcastAllChannels → initial-snapshot path), which existing clients drop by emission seq — the same
    /// re-ship-idempotent discipline as <see cref="GeoVehicleChannel"/>.
    ///
    /// Gated on <c>ClientSimFreeze.Enabled</c> like the 0xA5/0xA6/0xA7 mirrors: flag-OFF rollback = ZERO new
    /// traffic (the unfrozen client then runs its own mist producer, legacy path).
    /// </summary>
    public sealed class MistChannel : IStateChannel
    {
        public byte ChannelId => SurfaceIds.MistChannel;   // 8 (#7 RESERVED for the P7 objectives channel)

        /// <summary>Chunk payload size: header + 24 KB stays safely under the proven 32 KB per-message bound.</summary>
        public const int ChunkBytes = 24 * 1024;
        private const int PollTickInterval = 60;           // ~1 s @60fps between _hoursPassed checks
        private const int MinRecordIntervalMs = 5000;      // bound the 8 MB deflate cost at max game speed

        // ─── HOST state ────────────────────────────────────────────────────────────────────────────────────
        private int _pollTick;
        private int _lastHours = int.MinValue;             // last _hoursPassed value seen (hour-edge detect)
        private bool _hasHash;
        private ulong _lastHash;                           // content hash of the last EMITTED field (send-dedup)
        private uint _emitSeq;                             // per-session emission sequence (client last-wins key)
        private int _lastRecordMs;                         // Environment.TickCount of the last RecordInstanceData
        private readonly Queue<byte[]> _sendQueue = new Queue<byte[]>();   // encoded chunks awaiting flush
        private List<byte[]> _lastEmission;                // latest full emission (join-time re-ship)

        // ─── CLIENT state ──────────────────────────────────────────────────────────────────────────────────
        private readonly MistReassembler _reassembler = new MistReassembler();

        // ─── IStateChannel ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>Host per-tick pump (AttachHost is called every Tick, idempotent by contract): drain the
        /// chunk queue via dirty re-marks, and once per in-game hour record + hash-compare + (re)chunk.</summary>
        public void AttachHost(SyncEngine eng)
        {
            if (eng == null) return;
            if (!ClientSimFreeze.Enabled) return;          // flag-OFF rollback → zero new traffic

            // Chunks pending → keep the channel dirty so the flush loop (which runs AFTER the AttachHost pass
            // in Tick) ships the next one this very tick.
            if (_sendQueue.Count > 0) eng.MarkChannelDirty(ChannelId);

            if (++_pollTick < PollTickInterval) return;
            _pollTick = 0;

            if (!MistReflection.TryReadHours(GeoRuntime.Instance, out int hours)) return;   // not in geoscape / unbound
            if (hours == _lastHours) return;                                                // no in-game hour passed

            // Bound the deflate cost: at max game speed hours tick every couple of real seconds — record at
            // most once per MinRecordIntervalMs; the skipped hours coalesce into the next record (absolute
            // state, last-wins). unchecked diff = Environment.TickCount wrap-safe.
            int now = Environment.TickCount;
            if (_lastRecordMs != 0 && unchecked(now - _lastRecordMs) < MinRecordIntervalMs) return;

            if (!MistReflection.TryRecord(GeoRuntime.Instance, out int recHours, out var mist, out var repeller))
            { _lastHours = hours; return; }                // nothing recorded (empty field) → recheck next hour
            _lastRecordMs = now;
            _lastHours = hours;

            ulong hash = MistBlob.ContentHash(mist, repeller);
            if (_hasHash && hash == _lastHash) return;     // field unchanged → zero bytes this hour
            _hasHash = true;
            _lastHash = hash;

            var blob = MistBlob.Encode(recHours, mist, repeller);
            var chunks = MistChunkCodec.EncodeAll(++_emitSeq, blob, ChunkBytes);
            if (chunks == null) return;                    // over the sanity bound → skip (logged upstream on decode side)
            _lastEmission = chunks;
            _sendQueue.Clear();                            // a fresh emission supersedes any partially-sent one
            foreach (var c in chunks) _sendQueue.Enqueue(c);
            eng.MarkChannelDirty(ChannelId);
            UnityEngine.Debug.Log("[Multiplayer][geo] HOST mist emission seq=" + _emitSeq + " hours=" + recHours
                                  + " blobBytes=" + blob.Length + " chunks=" + chunks.Count);
        }

        /// <summary>Host: ship the next queued chunk (one per flush). Empty queue → re-enqueue the latest full
        /// emission (join-time initial snapshot via BroadcastAllChannels; existing clients drop it by seq); no
        /// emission yet → null (flush no-ops).</summary>
        public byte[] Snapshot(GeoRuntime rt)
        {
            if (_sendQueue.Count == 0)
            {
                if (_lastEmission == null || _lastEmission.Count == 0) return null;
                foreach (var c in _lastEmission) _sendQueue.Enqueue(c);
            }
            return _sendQueue.Dequeue();
        }

        /// <summary>Client: buffer the chunk; on the final one, decode + drive the native redraw. Last-wins and
        /// idempotent via <see cref="MistReassembler"/> (a re-shipped or duplicate emission never re-applies).</summary>
        public void Apply(GeoRuntime rt, byte[] data)
        {
            if (!MistChunkCodec.TryDecode(data, out uint seq, out int count, out int idx, out var slice)) return;
            if (!_reassembler.Push(seq, count, idx, slice, out var blob)) return;
            if (!MistBlob.TryDecode(blob, out int hours, out var mist, out var repeller)) return;
            MistReflection.Apply(rt, hours, mist, repeller);
        }

        public void DetachHost()
        {
            _sendQueue.Clear();
            _lastEmission = null;
            _pollTick = 0;
            _lastHours = int.MinValue;
            _hasHash = false;
            _lastRecordMs = 0;
        }
    }
}
