using System;
using System.IO;
using System.Text;

namespace Multipleer.Network.Sync
{
    /// <summary>
    /// The ONE inbound chokepoint for the unified sync skeleton (spec §3). Decodes a
    /// <see cref="SyncProtocol"/> envelope, looks up the surface, and runs the shared pipeline:
    /// (host) authorize + validate → order/dedup → <see cref="SyncApplyScope"/> → apply → UI refresh →
    /// (host) rebroadcast. PURE: every outbound effect AND every game-runtime handle goes through
    /// <see cref="ISyncSink"/>, so the router references no transport / Unity / HarmonyLib type and is
    /// unit-tested in isolation, like every other sync primitive. Composes the SAME
    /// <see cref="SequenceTracker"/>/<see cref="RequestDedup"/>/<see cref="SurfaceRegistry"/> instances
    /// the engine holds, so behavior matches the legacy per-packet path exactly.
    /// </summary>
    public sealed class SurfaceRouter
    {
        private readonly SurfaceRegistry _surfaces;
        private readonly SequenceTracker _tracker;
        private readonly RequestDedup _dedup;
        private ulong _hostSequence;   // host-assigned, monotonic per-router action sequence

        public SurfaceRouter(SurfaceRegistry surfaces, SequenceTracker tracker, RequestDedup dedup)
        {
            _surfaces = surfaces;
            _tracker = tracker;
            _dedup = dedup;
        }

        /// <summary>Decode + route one inbound envelope. Never throws (forward-compat: drop).</summary>
        public void OnInbound(ulong senderPeerId, byte[] data, ISyncSink sink)
        {
            if (!SyncProtocol.TryDecodeEnvelope(data, out var surfaceId, out var kind, out var payload)) return;
            var entry = _surfaces.Get(surfaceId);
            if (entry == null || !entry.Accepts(kind)) return;   // unknown surface or wrong kind → drop

            switch (kind)
            {
                case SyncKind.ActionRequest: HandleActionRequest(senderPeerId, entry, payload, sink); break;
                case SyncKind.ActionApply:   HandleActionApply(entry, payload, sink); break;
                // StateSnapshot / StateDelta routed in Phase 2.
                default: break;
            }
        }

        // ─── host: client → host action request ───────────────────────────
        private void HandleActionRequest(ulong senderPeerId, SurfaceRegistry.SurfaceEntry entry,
            byte[] payload, ISyncSink sink)
        {
            if (!sink.IsHost) return;
            if (_dedup.IsDuplicate(senderPeerId, HashPayload(payload))) return; // reliable-transport double-send

            var action = ReadAction(entry, payload);
            if (action == null) return;

            Guid actor = sink.ResolveActor(senderPeerId);
            var rt = sink.Runtime;
            if (actor == Guid.Empty || !PermissionGate.CheckFor(actor, action.Category) || !action.Validate(rt, actor))
            {
                sink.RejectTo(senderPeerId, entry.SurfaceId);
                return;
            }

            try { using (SyncApplyScope.Enter()) action.Apply(rt); }
            catch { /* best-effort: a faulty apply must not break the relay (SyncEngine logs at its boundary) */ }

            sink.MarkSurfaceDirty(entry.SurfaceId);              // no-op for pure actions (channel mark is Phase 2)
            if (entry.Screen.HasValue) sink.RefreshUi();

            ulong seq = ++_hostSequence;
            _tracker.Mark(seq);
            sink.RebroadcastActionApply(entry.SurfaceId, seq, payload);
        }

        // ─── client: host → all action apply ──────────────────────────────
        private void HandleActionApply(SurfaceRegistry.SurfaceEntry entry, byte[] payload, ISyncSink sink)
        {
            if (sink.IsHost) return;   // host never replays its own echo
            ulong seq; byte[] actionBytes;
            if (!TrySplitApply(payload, out seq, out actionBytes)) return;
            if (!_tracker.ShouldApply(seq)) return;
            _tracker.Mark(seq);

            var action = ReadAction(entry, actionBytes);
            if (action == null) return;
            if (action is IHostOnlyApply) return;   // client must NOT replay host-only outcome side-effects

            try { using (SyncApplyScope.Enter()) action.Apply(sink.Runtime); }
            catch { /* best-effort: a faulty replay must not break the relay */ }
            if (entry.Screen.HasValue) sink.RefreshUi();
        }

        // ─── apply wire: [seq:u64][actionBytes] ───────────────────────────
        public static byte[] EncodeApplyPayload(ulong sequence, byte[] actionBytes)
        {
            actionBytes = actionBytes ?? new byte[0];
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(sequence);
                w.Write(actionBytes);
                return ms.ToArray();
            }
        }

        private static bool TrySplitApply(byte[] payload, out ulong sequence, out byte[] actionBytes)
        {
            sequence = 0; actionBytes = null;
            try
            {
                using (var ms = new MemoryStream(payload ?? new byte[0]))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    sequence = r.ReadUInt64();
                    actionBytes = r.ReadBytes((int)(ms.Length - ms.Position));
                    return true;
                }
            }
            catch { return false; }
        }

        private static ISyncedAction ReadAction(SurfaceRegistry.SurfaceEntry entry, byte[] payload)
        {
            if (entry.Reader == null) return null;
            using (var ms = new MemoryStream(payload ?? new byte[0]))
            using (var r = new BinaryReader(ms, Encoding.UTF8))
                return entry.Reader(r);
        }

        // Stand-in nonce for dedup: the legacy path keyed (peer, client-nonce). On the envelope the
        // request carries no separate nonce field, so key on (peer, payload-hash) — identical request
        // bytes from the same peer are the reliable-transport double-send we must drop.
        private static uint HashPayload(byte[] payload)
        {
            unchecked
            {
                uint h = 2166136261u;
                if (payload != null)
                    for (int i = 0; i < payload.Length; i++) h = (h ^ payload[i]) * 16777619u;
                return h;
            }
        }
    }
}
