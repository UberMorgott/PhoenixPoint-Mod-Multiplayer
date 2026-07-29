using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Multiplayer.Network.MessageLayer;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// THE generic client-intent engine (law 1 "Intent" primitive, one implementation). Every intent
    /// family (research 0xAB / manufacture 0xAE / personnel 0xAF / time 0xB0 / base 0xB1 / equip 0xB3)
    /// registers its op table here at SyncEngine construction; the framework owns the plumbing that was
    /// previously copy-pasted per family with drift:
    ///   • Envelope: [nonce:u32][op:u8][family body] riding SyncKind.ActionRequest on the family's OWN
    ///     surface id — the surface byte IS the family discriminator (SurfaceRouter already routes on
    ///     it), so no inner family id and no single shared surface. The reserved 0xA2-0xA4 action-relay
    ///     ids stay tombstones: GeoOutcome/GeoReject never materialized because outcomes ride the
    ///     normal rail diff / order channels and rejects ride scoped re-emit (below).
    ///   • ONE client nonce allocator. The host dedup key is (peer, surface, nonce), so a shared
    ///     monotonic counter can never collide across surfaces — and two senders on one surface (the
    ///     old EquipSync-borrows-ManufactureSync.NextNonce trap) cannot desynchronize counters anymore.
    ///     The counter persists across session resets (rca-3 symmetric-persistence contract).
    ///   • ONE host-side IntentDedup — the idempotence guard (law 7): the reliable channel can
    ///     double-send, and a non-idempotent native op (a stat spend) must not double-apply. Nonce ring
    ///     keyed (peer, surface, nonce), capacity-bounded; intents are user-gesture rate and a
    ///     transport dupe arrives adjacent to its original, so the shared 512 window holds. Per-op
    ///     state checks on top of it live in the validators where the op itself demands one
    ///     (def-at-index guard, "already learned", storage-count) — domain logic, not transport.
    ///   • The host dispatch: host-only gate, [nonce][op] decode, dedup, table lookup; unknown op and
    ///     handler throw funnel into the SAME reject path instead of four hand-rolled variants.
    ///   • The REJECT discipline (law 7 convergence, generalized from EquipSync): a reject is NEVER
    ///     log-only — <see cref="Reject"/> logs AND reconverges the gesturing client via scoped
    ///     <see cref="DiffEngine.ForceReemit"/> of the touched subtrees (callers pass path prefixes)
    ///     plus the family's registered reconverge action (order-channel forced resend for queues that
    ///     do not ride the value rail). Host state did not change on a reject, so the diff rail emits
    ///     nothing on its own — only a forced re-emit converges a client whose mirror or open screen
    ///     ran ahead. Families whose convergence is continuous (time: TimeAnchor.EnforceDrift) register
    ///     no reconverge — the standing corrector is the mechanism.
    /// Families keep: Harmony capture seams (with their session-active/host gates), per-op
    /// validators/appliers (domain logic, host-state-only per law 3, applied via NATIVE methods), and
    /// their host→all order channels. On success host state changes ride the normal rail diff; families
    /// needing same-frame delivery push their order channel / rely on change-driven FlushNow as before.
    /// </summary>
    public static class IntentRail
    {
        /// <summary>One registered op: decode the family body from the reader, validate against HOST
        /// state only (law 3), apply via NATIVE game methods. Refusals call <see cref="Reject"/>
        /// (a throw is caught by the dispatch and rejected uniformly); deliberate no-ops stay silent.</summary>
        public delegate void OpHandler(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r);

        private sealed class Family
        {
            public string Tag;
            public Dictionary<byte, OpHandler> Ops;
            public Action Reconverge; // family-wide reject reconvergence (e.g. forced order-channel resend); null = none
        }

        private static readonly Dictionary<byte, Family> _families = new Dictionary<byte, Family>();
        private static readonly IntentDedup Intents = new IntentDedup();
        private static uint _nextNonce;

        /// <summary>Idempotent (SyncEngine is constructed per engine start on both peers — re-registering
        /// a surface just replaces its table with an identical one).</summary>
        public static void Register(byte surfaceId, string tag, Dictionary<byte, OpHandler> ops, Action reconverge = null)
            => _families[surfaceId] = new Family { Tag = tag, Ops = ops, Reconverge = reconverge };

        /// <summary>Full session teardown: drop the dedup window. The nonce counter deliberately
        /// persists (matching the per-family behavior it replaced) — the host prunes a rejoining
        /// peer's window via <see cref="ResetIntentDedupForPeer"/> instead.</summary>
        public static void Reset() => Intents.Reset();

        /// <summary>Rejoin (rca-3 audit b): the returning peer's fresh engine restarts its nonce at 1.</summary>
        public static void ResetIntentDedupForPeer(ulong peerId) => Intents.ResetPeer(peerId);

        // ─── THE CLIENT-POSTURE LAW: BLOCK-FIRST ───────────────────────────

        /// <summary>
        /// THE CLIENT-POSTURE LAW — BLOCK-FIRST, the ONE posture every intent family follows.
        /// On a client, outside <see cref="SyncApplyScope"/>, a native STATE mutation is BLOCKED at its
        /// model funnel and converted into an intent; the HOST executes the same native method; the
        /// result reaches every peer through the delta rail; the client NEVER writes the model
        /// optimistically. PRESENTATION-ONLY work (widget staging, slot animation, popups — anything
        /// that lives on a view/view-model, not the game model) may proceed: the mirrored echo reseeds
        /// it. A family is either block-first or fully native (solo/host) — never "result-ship", and
        /// never co-managing a native view-model's lifecycle across the wire (the 2026-07-25/26
        /// personnel floor/commit/ledger loop was exactly that, 11 RCAs deep).
        /// TRUE = run the native code: host, solo, or inside a delta apply (law 8 — an apply that fires
        /// native events must never re-enter capture). FALSE = client: block, send the intent (or
        /// nothing, when the gesture is a no-op). Every capture seam calls THIS one (2026-07-26
        /// collapse); a family may AND extra native-passthrough arms on top (foreign-faction /
        /// non-viewer targets stay native and un-synced) but never re-states the base decision.
        /// </summary>
        public static bool ShouldRunNative()
        {
            DiffEngine.FlushOnHostGesture(); // host gestures ship this frame (N3 third arm), no-op elsewhere
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return true;
            return SyncApplyScope.Active;
        }

        // ─── CLIENT: the one emit ──────────────────────────────────────────

        /// <summary>Send one intent to the host: [nonce][op] + whatever <paramref name="writeBody"/>
        /// appends. Callers gate on their own capture decision (ShouldRunNative) — this helper only
        /// guards the engine's existence, like every emit it replaced.</summary>
        public static void Send(byte surfaceId, byte op, string what, Action<BinaryWriter> writeBody = null)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null) return;
            try
            {
                byte[] inner;
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms, Encoding.UTF8))
                {
                    w.Write(++_nextNonce);
                    w.Write(op);
                    writeBody?.Invoke(w);
                    inner = ms.ToArray();
                }
                var env = SyncProtocol.EncodeEnvelope(surfaceId, SyncKind.ActionRequest, inner);
                engine.SendToHost(new NetworkMessage(PacketType.SyncEnvelope, env));
                Debug.Log("[MP][intent] CLIENT " + Tag(surfaceId) + " " + what + " nonce=" + _nextNonce);
            }
            catch (Exception ex)
            {
                // A block-first gesture whose intent never reached the host: host state is unchanged, so
                // no delta will ever repaint what the client staged — reconverge like the reject path
                // does (repaint the open screen from the un-mutated local model). Never silent.
                Debug.LogError("[MP][intent] " + Tag(surfaceId) + " send failed (" + what + ") — reconverging local UI: " + ex);
                OpenUiRepaint.MarkDirty();
            }
        }

        // ─── HOST: the one dispatch ────────────────────────────────────────

        /// <summary>Returns true when the surface is a registered intent surface (consumed). Armed as
        /// part of SurfaceRouter.GeoscapeInbound.</summary>
        public static bool HandleInbound(NetworkEngine engine, ulong senderPeerId, byte surfaceId, byte[] payload)
        {
            if (!_families.TryGetValue(surfaceId, out var family)) return false;
            if (engine == null) return true;
            if (!engine.IsHost)
            {
                // The only host→client traffic on an intent surface is the Reject nudge (below): the
                // reconverge re-emit usually arrives byte-equal → applies as Unchanged → no touched → no
                // repaint, so the gesturing client's STAGED widgets stay stale. Its model is already
                // correct (law 3: the native op never ran locally) — repainting from it un-stages the
                // open screen. Payload is deliberately empty; nothing to decode.
                Debug.Log("[MP][intent] CLIENT " + Tag(surfaceId) + " reject nudge — repainting open UI");
                OpenUiRepaint.MarkDirty();
                return true;
            }
            try
            {
                using (var ms = new MemoryStream(payload))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint nonce = r.ReadUInt32();
                    byte op = r.ReadByte();
                    if (!Intents.IsNew(senderPeerId, surfaceId, nonce)) return true; // reliable double-send
                    if (!family.Ops.TryGetValue(op, out var handler))
                    { Reject(surfaceId, senderPeerId, "unknown op " + op); return true; }
                    handler(engine, senderPeerId, nonce, op, r);
                    // N3 second arm (the seam DiffEngine.ArmChangeDrivenFlush's doc already promises):
                    // a dispatched intent just changed host state via a native op — ship it THIS frame
                    // (Transport.Update dispatches before Sync.Tick) instead of waiting out the 0.5 s
                    // poll + ~250 ms sliced cycle, which held EVERY confirming delta ~0.25-0.75 s while
                    // the acting client stared at its own click. Rejects converge via ForceReemit (which
                    // flushes itself); a silent no-op op costs one wasted walk at user-gesture rate.
                    DiffEngine.FlushNow();
                    // The HOST'S OWN open screen (law 11, missing half): FlushNow repaints the CLIENTS
                    // (their appliers MarkDirty), and the host's native op fires whatever native UI
                    // events exist — but where the game never needed one (UIModuleBaseLayout.
                    // OnFacilityAdded:746 is an EMPTY body that is not even subscribed; facility remove
                    // has no handler at all — single-player builds only ever change the screen through
                    // its own build menu), the host applying a REMOTE intent repainted nothing and its
                    // screen stayed stale until re-enter (2026-07-26 retest, symptoms 1+2). ONE mark at
                    // the ONE dispatch covers every family; the flush's native rebuild runs under
                    // SyncApplyScope (OpenUiRepaint.cs), so host capture seams cannot echo (law 8).
                    OpenUiRepaint.MarkDirty();
                }
            }
            catch (Exception ex)
            {
                // Native validation throws double as the reject path (families with a scoped subtree to
                // re-emit catch closer in, where they still know the entity id).
                Reject(surfaceId, senderPeerId, "(throw) " + ex.Message);
            }
            return true;
        }

        /// <summary>Uniform reject: log + re-emit of the host truth + the family's registered reconverge.
        /// Re-emit is UNCONDITIONAL — <paramref name="reemitPrefixes"/> only NARROWS it. A reject changes
        /// no host state, so the diff has nothing of its own to ship: without a forced re-emit the client
        /// keeps whatever its optimistic local gesture staged, forever (PersonnelSync's hireNaked reject
        /// passed no prefix and the client's debited resources never came back). Callers that know the
        /// touched subtree pass its path prefixes and pay a scoped re-emit; a caller that knows no scope —
        /// or passes only conditionally-known ids that came out null — falls back to the full covered
        /// graph, because "nothing" is a permanent divergence and a reject is user-gesture rate.</summary>
        public static void Reject(byte surfaceId, ulong peer, string why, params string[] reemitPrefixes)
        {
            Debug.LogWarning("[MP][intent] HOST " + Tag(surfaceId) + " REJECT peer=" + peer + " — " + why);
            int scoped = 0;
            if (reemitPrefixes != null)
                foreach (var p in reemitPrefixes)
                    if (!string.IsNullOrEmpty(p)) { DiffEngine.ForceReemit(p); scoped++; }
            if (scoped == 0)
            {
                DiffEngine.RequestFullResend();
                DiffEngine.FlushNow(); // converge on the next frame, not on the ≤0.5 s poll
            }
            if (_families.TryGetValue(surfaceId, out var f)) f.Reconverge?.Invoke();
            // The re-emit reconverges the client's MODEL, but a reject means host state did not change —
            // re-emitted values arrive byte-equal, apply as Unchanged and repaint nothing, leaving the
            // gesturing client's staged UI stale until reopen. Nudge that ONE client: an empty envelope
            // back on the family's own surface, handled as MarkDirty in HandleInbound's client branch.
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine != null && engine.IsHost && peer != 0)
                    engine.SendToClient(peer, new NetworkMessage(PacketType.SyncEnvelope,
                        SyncProtocol.EncodeEnvelope(surfaceId, SyncKind.ActionRequest, null)));
            }
            catch (Exception ex) { Debug.LogError("[MP][intent] reject nudge send failed: " + ex.Message); }
        }

        private static string Tag(byte surfaceId)
            => _families.TryGetValue(surfaceId, out var f) ? f.Tag : "0x" + surfaceId.ToString("X2");
    }
}
