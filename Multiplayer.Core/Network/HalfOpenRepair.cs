namespace Multiplayer.Network
{
    /// <summary>What the CLIENT half-open detector should do this tick.</summary>
    public enum HalfOpenAction { None, Repair, Fail }

    /// <summary>
    /// Pure decision for the CLIENT half-open Steam-P2P recovery (extracted so it is unit-testable
    /// without Unity/Steam). The half-open SIGNATURE is: host traffic is still ARRIVING (inbound
    /// liveness fresh) while the host has stopped ACKing our heartbeats (outbound/ack clock stale) —
    /// our client→host channel is dead even though the transport still reports Connected. On that
    /// signature we attempt ONE session-reset + re-JOIN (<see cref="HalfOpenAction.Repair"/>) before
    /// declaring the link dead (<see cref="HalfOpenAction.Fail"/>). A full drop (inbound ALSO stale)
    /// is NOT half-open — it is the host-leave path, so this returns <see cref="HalfOpenAction.None"/>.
    /// SessionManager owns the effects; this only classifies.
    /// </summary>
    public static class HalfOpenRepair
    {
        /// <param name="repairAttempted">true once the one-shot repair already ran this connection.</param>
        public static HalfOpenAction Decide(long now, long lastInboundMs, long lastAckMs,
                                            long timeoutMs, bool repairAttempted)
        {
            bool inboundAlive = now - lastInboundMs <= timeoutMs;   // host packets still arriving
            bool outboundDead = now - lastAckMs > timeoutMs;        // host stopped acking ours
            if (!(inboundAlive && outboundDead)) return HalfOpenAction.None;
            return repairAttempted ? HalfOpenAction.Fail : HalfOpenAction.Repair;
        }
    }
}
