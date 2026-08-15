using System;

namespace Multiplayer.Network
{
    /// <summary>
    /// THE REVEAL IS ONE INSTANT, NOT ONE MESSAGE.
    ///
    /// The load barrier itself was never the defect: the host waits for EVERY live roster slot's
    /// LoadComplete before it broadcasts RevealAll. What followed it was — the host called
    /// <c>PerformDeferredLift</c> in the same frame it broadcast, so it lifted at t0 while each client
    /// lifted at t0 + wire hop + one of its own (post-load, expensive) frames, and the input unlock,
    /// which is keyed on the lift, trailed further still.
    ///
    /// MEASURED 2026-08-15 20:43 across three machines:
    ///   lift        host 52.897   s3 53.317 (+420 ms)   s2 53.544 (+647 ms)
    ///   input live  host 52.994   s3 54.325 (+1331 ms)  s2 54.546 (+1552 ms)
    /// The host could act 1.33–1.55 s before its clients, and the client order tracked their load
    /// order — the "whoever loads first gets in first" report, exactly.
    ///
    /// The fix is a DEADLINE, not a handshake. The host picks an instant on ITS OWN clock, ships it,
    /// and every peer — the host included — lifts when that instant arrives, mapped onto the local
    /// clock by <c>PingTable.TryHostNowMs</c> (the same host-clock mapping the tactical execute-epoch
    /// already runs on). This is NOT a quorum: nothing here waits for a peer to ACT, the instant
    /// elapses by itself, a silent or departed peer cannot postpone it by one millisecond, and a peer
    /// whose deadline is already in the past when the packet lands lifts on its very next frame.
    /// </summary>
    public static class RevealSchedule
    {
        /// <summary>Floor. Below one comfortable frame the host is back to lifting on its own
        /// broadcast frame, which is the whole defect.</summary>
        public const int MinLeadMs = 300;

        /// <summary>Ceiling. The lead is dead black screen for everyone, so a pathological ping must
        /// not buy unbounded extra darkness. 2.5 s is above the worst spread ever measured (1.55 s).</summary>
        public const int MaxLeadMs = 2500;

        /// <summary>What the wire hop does NOT explain. The measured client lag was far larger than
        /// any hop: a client is spending its first frames after a multi-second blocking level load,
        /// so the deadline has to clear one of those frames as well as the packet.</summary>
        public const int FrameMarginMs = 400;

        /// <summary>How far ahead of now the host puts the common reveal instant, derived from the
        /// MEASURED link — the worst live peer RTT — never from a guessed constant. Full RTT rather
        /// than RTT/2 on purpose: the one-way hop is only part of what the measurement above showed,
        /// and the RTT is the only number of the right magnitude the session actually samples.
        /// ponytail: one number for every peer, not a per-peer deadline. A per-peer instant would let
        /// a fast link lift first again, which is the bug.</summary>
        public static int LeadMs(int maxRttMs)
        {
            int lead = (maxRttMs < 0 ? 0 : maxRttMs) + FrameMarginMs;
            return Math.Max(MinLeadMs, Math.Min(MaxLeadMs, lead));
        }

        /// <summary>Has the common instant arrived on this peer? Both operands are on the HOST clock.
        /// Deliberately <c>&gt;=</c>: a peer whose deadline already passed while the packet was in
        /// flight lifts immediately rather than being stranded — late, never early, never blocked.</summary>
        public static bool Due(long hostNowMs, long dueHostMs) => hostNowMs >= dueHostMs;
    }
}
