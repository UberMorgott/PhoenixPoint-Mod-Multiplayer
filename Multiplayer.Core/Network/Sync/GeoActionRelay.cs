using Multiplayer.Network.MessageLayer;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// The three wire builders for the geoscape action-relay, riding the unified 0x67 <see cref="SyncProtocol"/>
    /// envelope UNCONDITIONALLY (spec 2026-07-02-multiplayer-action-relay-envelope-cutover-design; the legacy raw
    /// ActionRequest/ActionApply/ActionReject packets 0x60/0x61/0x62 and their <c>UseEnvelope</c> gate were
    /// deleted at cutover). Every geoscape action message rides one of the three geoscape action surfaces
    /// <see cref="SurfaceIds.GeoIntent"/> (0xA2) / <see cref="SurfaceIds.GeoOutcome"/> (0xA3) /
    /// <see cref="SurfaceIds.GeoReject"/> (0xA4) on the 0x67 rail; the inner action bytes are the SAME
    /// EncodeAction* bytes the retired legacy packets carried, so only the outer packet header changed. Both peers
    /// run the SAME Multiplayer.dll (there is no in-lobby build negotiation), so there is exactly ONE rail — never
    /// a mixed rail inside one build.
    ///
    /// PURE (no Unity/Harmony) → the builders + their envelope round-trip are unit-tested (GeoActionRelayTests).
    /// </summary>
    public static class GeoActionRelay
    {
        /// <summary>
        /// Client→host action REQUEST wire: <see cref="SurfaceIds.GeoIntent"/> (0xA2) carrying the
        /// <see cref="SyncProtocol.EncodeActionRequest"/> bytes. Sent via <c>SendToHost</c>.
        /// </summary>
        public static NetworkMessage BuildIntent(ushort actionId, uint nonce, byte[] payload)
            => new NetworkMessage(PacketType.SyncEnvelope,
                SyncProtocol.EncodeEnvelope(SurfaceIds.GeoIntent, SyncKind.ActionRequest,
                    SyncProtocol.EncodeActionRequest(actionId, nonce, payload)));

        /// <summary>
        /// Host→all authoritative APPLY wire: <see cref="SurfaceIds.GeoOutcome"/> (0xA3) carrying the
        /// <see cref="SyncProtocol.EncodeActionApply"/> bytes. Broadcast to ALL. The <paramref name="seq"/> is
        /// authored by the caller from <c>SurfaceSeq.Next(GeoOutcome)</c> and stored losslessly in the u64 apply field.
        /// </summary>
        public static NetworkMessage BuildOutcome(ushort actionId, ulong seq, byte[] payload)
            => new NetworkMessage(PacketType.SyncEnvelope,
                SyncProtocol.EncodeEnvelope(SurfaceIds.GeoOutcome, SyncKind.ActionApply,
                    SyncProtocol.EncodeActionApply(actionId, seq, payload)));

        /// <summary>
        /// Host→originator REJECT wire: <see cref="SurfaceIds.GeoReject"/> (0xA4) carrying the
        /// <see cref="SyncProtocol.EncodeActionReject"/> bytes. Rides <c>SendToClient(originator)</c> — the unicast
        /// target is set at the <see cref="NetworkMessage"/> layer, not in the envelope. Reject is nonce-correlated
        /// and idempotent, so it needs no seq.
        /// </summary>
        public static NetworkMessage BuildReject(uint nonce, byte reasonCode, string reason)
            => new NetworkMessage(PacketType.SyncEnvelope,
                SyncProtocol.EncodeEnvelope(SurfaceIds.GeoReject, SyncKind.ActionApply,
                    SyncProtocol.EncodeActionReject(nonce, reasonCode, reason)));
    }
}
