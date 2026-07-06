using Multiplayer.Network.MessageLayer;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// The ONE gate + wire builders for the geoscape action-relay → envelope CUTOVER
    /// (spec 2026-07-02-multiplayer-action-relay-envelope-cutover-design). <see cref="UseEnvelope"/> selects, for
    /// EVERY geoscape action message, whether it rides the LEGACY raw ActionRequest/ActionApply/ActionReject
    /// packets (0x60/0x61/0x62) or the unified 0x67 <see cref="SyncProtocol"/> envelope on the three geoscape
    /// action surfaces <see cref="SurfaceIds.GeoIntent"/> (0xA2) / <see cref="SurfaceIds.GeoOutcome"/> (0xA3) /
    /// <see cref="SurfaceIds.GeoReject"/> (0xA4). The inner action bytes are byte-for-byte identical on both rails
    /// (the outer packet header is the only difference), so <c>UseEnvelope == false</c> is byte-identical to the
    /// pre-cutover wire — an un-flipped peer is unaffected and the legacy relay is a clean rollback.
    ///
    /// ATOMIC FLIP: every sender site (<c>SyncEngine.SendActionRequest</c> / <c>BroadcastHostAction</c> / the
    /// <c>OnActionRequest</c> broadcast + reject tails) and every inbound guard (per-peer <see cref="IntentDedup"/>
    /// on 0xA2, <see cref="SurfaceSeq"/> on 0xA3) reads this ONE flag, so flipping it switches sender + receiver +
    /// guard together. Both peers run the SAME Multiplayer.dll (there is no in-lobby build negotiation), so the
    /// flip lands on both simultaneously via the recompile — there is never a mixed rail inside one build (the
    /// double-apply hazard the spec §2 rules out). Kept OFF until the in-game 2-instance pass verifies the
    /// enveloped path; then flipped in a one-line follow-up.
    ///
    /// PURE (no Unity/Harmony) → the flip + the byte-identical-when-off guarantee are unit-tested. The builders
    /// take the flag as an explicit parameter (not the field) so BOTH states are deterministically exercised;
    /// the live call sites pass <see cref="UseEnvelope"/>.
    /// </summary>
    public static class GeoActionRelay
    {
        /// <summary>
        /// The ONE cutover gate. <c>false</c> = legacy raw 0x60/0x61/0x62 relay (default, un-flipped); <c>true</c>
        /// = enveloped GeoIntent/GeoOutcome/GeoReject on the 0x67 rail. <c>static readonly</c> (NOT <c>const</c>) so
        /// BOTH branches compile — no dead-code elimination — and the JIT drops the unused one; the flip is a
        /// ONE-line field change that every sender + guard site picks up together (see class remarks).
        /// </summary>
        public static readonly bool UseEnvelope = false;

        /// <summary>
        /// Client→host action REQUEST wire. Envelope: <see cref="SurfaceIds.GeoIntent"/> (0xA2) carrying the SAME
        /// <see cref="SyncProtocol.EncodeActionRequest"/> bytes; legacy: the raw <see cref="PacketType.ActionRequest"/>
        /// (0x60) packet with those exact bytes. Sent via <c>SendToHost</c> either way.
        /// </summary>
        public static NetworkMessage BuildIntent(bool useEnvelope, ushort actionId, uint nonce, byte[] payload)
        {
            var inner = SyncProtocol.EncodeActionRequest(actionId, nonce, payload);
            return useEnvelope
                ? new NetworkMessage(PacketType.SyncEnvelope,
                    SyncProtocol.EncodeEnvelope(SurfaceIds.GeoIntent, SyncKind.ActionRequest, inner))
                : new NetworkMessage(PacketType.ActionRequest, inner);
        }

        /// <summary>
        /// Host→all authoritative APPLY wire. Envelope: <see cref="SurfaceIds.GeoOutcome"/> (0xA3) carrying the SAME
        /// <see cref="SyncProtocol.EncodeActionApply"/> bytes; legacy: the raw <see cref="PacketType.ActionApply"/>
        /// (0x61). Broadcast to ALL either way. The <paramref name="seq"/> is authored by the caller — from
        /// <c>SurfaceSeq.Next(GeoOutcome)</c> when enveloped, or the legacy host action counter when not — and
        /// stored losslessly in the u64 apply field.
        /// </summary>
        public static NetworkMessage BuildOutcome(bool useEnvelope, ushort actionId, ulong seq, byte[] payload)
        {
            var inner = SyncProtocol.EncodeActionApply(actionId, seq, payload);
            return useEnvelope
                ? new NetworkMessage(PacketType.SyncEnvelope,
                    SyncProtocol.EncodeEnvelope(SurfaceIds.GeoOutcome, SyncKind.ActionApply, inner))
                : new NetworkMessage(PacketType.ActionApply, inner);
        }

        /// <summary>
        /// Host→originator REJECT wire. Envelope: <see cref="SurfaceIds.GeoReject"/> (0xA4) carrying the SAME
        /// <see cref="SyncProtocol.EncodeActionReject"/> bytes; legacy: the raw <see cref="PacketType.ActionReject"/>
        /// (0x62). Rides <c>SendToClient(originator)</c> either way — the envelope rail is direction-agnostic; the
        /// unicast target is set at the <see cref="NetworkMessage"/> layer, not in the envelope. Reject is
        /// nonce-correlated and idempotent, so it needs no seq.
        /// </summary>
        public static NetworkMessage BuildReject(bool useEnvelope, uint nonce, byte reasonCode, string reason)
        {
            var inner = SyncProtocol.EncodeActionReject(nonce, reasonCode, reason);
            return useEnvelope
                ? new NetworkMessage(PacketType.SyncEnvelope,
                    SyncProtocol.EncodeEnvelope(SurfaceIds.GeoReject, SyncKind.ActionApply, inner))
                : new NetworkMessage(PacketType.ActionReject, inner);
        }
    }
}
