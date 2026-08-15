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
    /// THE FIRST FIX WAS A DEADLINE, AND THE DEADLINE WAS NOT ENOUGH. The host picked an instant on its
    /// own clock (<see cref="LeadMs"/>, lead derived from the worst measured RTT), shipped it, and every
    /// peer including itself lifted when that instant arrived, mapped onto the local clock by
    /// <c>PingTable.TryHostNowMs</c>. Re-measured 2026-08-15 21:54 with that shipped: RTT sampled ~0, so
    /// the lead was the 400 ms floor, so the clients read the instant as <c>inMs=-258</c> — ALREADY
    /// OVERDUE ON ARRIVAL — and then each still owed one 2038 ms post-load frame. Host on screen 10.646,
    /// clients 11.862/11.854. A 1.21 s head start, the same defect, with the deadline behaving exactly as
    /// specified: the real cost is DELIVERY (~660 ms) plus the RECEIVER'S OWN first frame (~950 ms), and
    /// an RTT sample of zero can see neither.
    ///
    /// SO THE INSTANT KEEPS ONLY THE HALF IT CAN ENFORCE. It is a FLOOR — nobody lifts before it, however
    /// ready they are — and the RELEASE is <see cref="MayLift"/>: each peer holds until IT has observed
    /// every live peer's own first post-load frame. Still not a quorum (P13): "ready" is a frame, not a
    /// human action; an AFK player's machine renders it anyway; a departed or paused peer leaves the live
    /// set instead of extending it; and a peer that goes silent without leaving is given up on after
    /// <see cref="ReadyGiveUpMs"/> measured locally.
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
        /// flight lifts immediately rather than being stranded — late, never early, never blocked.
        ///
        /// THIS IS NO LONGER A RELEASE, IT IS A FLOOR. See <see cref="MayLift"/> for why.</summary>
        public static bool Due(long hostNowMs, long dueHostMs) => hostNowMs >= dueHostMs;

        /// <summary>How long a peer waits for a slot that neither reports ready nor leaves, measured on
        /// its OWN clock from its OWN arm — so the peer that went quiet cannot withhold the very timer
        /// that releases everyone from it. 15 s is far above any first post-load frame ever measured
        /// (2038 ms) and far below the transport's own patience, so in practice a real drop is seen as a
        /// DEPARTURE (which shrinks the set instantly) and this branch only ever catches a peer whose
        /// process died with its socket still open.</summary>
        public const int ReadyGiveUpMs = 15000;

        /// <summary>MAY THIS PEER LIFT ITS OWN CURTAIN? The one release predicate, run by every peer on
        /// every frame against what IT has observed.
        ///
        /// WHY THE DEADLINE ALONE WAS NOT ENOUGH (live capture 2026-08-15 21:54, three machines,
        /// postdating the deadline fix): the host scheduled dueHostMs=55546 with a 400 ms lead because
        /// the measured RTT was ~0; the clients read that same instant as <c>inMs=-258</c> — already
        /// overdue on arrival — and then still owed one 2038 ms post-load frame each. Host on screen at
        /// 10.646, clients at 11.862/11.854: a 1.21 s head start, with the deadline working exactly as
        /// designed. An instant on the host's clock is a guess about delivery and about somebody else's
        /// frame budget, and both guesses were wrong by an order of magnitude.
        ///
        /// So the deadline keeps only the half it can actually enforce — it is a FLOOR, never a ceiling:
        ///   • <paramref name="hostNowMs"/> &lt; <paramref name="dueHostMs"/> → nobody lifts, however
        ///     ready everyone is. A fast peer can never re-acquire a head start.
        ///   • the floor passing releases NOTHING on its own; <paramref name="allReady"/> does.
        /// <paramref name="allReady"/> is this peer's OWN observation of the live roster's ready-set, and
        /// "ready" means one thing only: that peer's load finished and one of that peer's own frames
        /// rendered past it. NOT A QUORUM (P13): no human action is an input, an AFK player's machine
        /// renders that frame anyway, a departed or paused peer is not in the live set at all, and
        /// <paramref name="msSinceArmedMs"/> gives up on a peer that has gone silent without leaving.</summary>
        public static bool MayLift(bool allReady, long hostNowMs, long dueHostMs, long msSinceArmedMs)
            => hostNowMs >= dueHostMs && (allReady || msSinceArmedMs >= ReadyGiveUpMs);
    }
}
