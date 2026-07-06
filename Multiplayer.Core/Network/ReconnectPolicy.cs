namespace Multiplayer.Network
{
    /// <summary>
    /// Pure, Unity-free decision helpers for the Phase 8 reconnection / host-failover flow. Single
    /// authority means an UNEXPECTED host drop breaks the session for everyone (an intentional leave
    /// is already handled elsewhere); before any reconnect UI or save-transfer re-seed can react, the
    /// client must decide: is this drop a host-loss?, has the heartbeat actually timed out?, how long
    /// to wait before the next attempt?, and when to give up?
    ///
    /// Every input is passed in — NO <c>DateTime.Now</c>, NO randomness — so the whole policy is
    /// deterministic and unit-testable here without a game install. The clocks/timers and the actual
    /// "freeze clients / re-establish from save" firing are the 🔴 game-facing half and live in the
    /// mod project. Timeout defaults are grounded on the live heartbeat constants in
    /// <c>src/Network/SessionManager.cs</c> so the policy and the wire agree.
    /// </summary>
    public static class ReconnectPolicy
    {
        /// <summary>
        /// Heartbeat silence, in ms, after which a peer is presumed lost. Mirrors
        /// <c>SessionManager.HeartbeatTimeoutMs = 20000</c> (the live drop threshold, checked there as
        /// <c>now - lastHeartbeat &gt; HeartbeatTimeoutMs</c>). Kept identical so a reconnect decision
        /// made from this policy lines up with the transport's own timeout.
        /// </summary>
        public const long DefaultTimeoutMs = 20000;

        /// <summary>
        /// Base delay, in ms, for the first reconnection attempt (attempt 0). A 1s floor mirrors the
        /// order of the 5s heartbeat interval — quick enough to retry promptly, slow enough not to
        /// hammer a host that is still coming back.
        /// </summary>
        public const long DefaultBaseMs = 1000;

        /// <summary>
        /// Upper bound, in ms, on any single backoff delay so the wait never grows unbounded. 30s
        /// keeps the tail responsive to a host that recovers late.
        /// </summary>
        public const long DefaultCapMs = 30000;

        /// <summary>
        /// True iff the heartbeat is timed out: <paramref name="nowMs"/> minus
        /// <paramref name="lastHeartbeatMs"/> is STRICTLY greater than <paramref name="timeoutMs"/>.
        /// Strictly-greater (not &gt;=) matches <c>SessionManager</c>'s own live check exactly, so a
        /// gap EQUAL to the timeout is NOT yet a loss — the boundary tick is still alive.
        /// </summary>
        public static bool IsHeartbeatTimedOut(long lastHeartbeatMs, long nowMs, long timeoutMs)
        {
            return nowMs - lastHeartbeatMs > timeoutMs;
        }

        /// <summary>
        /// True iff a transport drop should be treated as a genuine host-loss (worth a reconnect)
        /// rather than the local side leaving on purpose. Thin, documented alias over
        /// <see cref="SessionLifecycle.ShouldNotifyHostLeft"/> — that predicate already owns the
        /// "unexpected host drop vs voluntary leave" decision (a client closing its only peer looks
        /// byte-identical to a host crash; only the local teardown intent tells them apart). The
        /// alias gives the reconnection code ONE named entry point without re-deriving the rule.
        /// </summary>
        public static bool IsHostLoss(bool localDisconnectIntentional)
        {
            return SessionLifecycle.ShouldNotifyHostLeft(localDisconnectIntentional);
        }

        /// <summary>
        /// Deterministic exponential backoff for the Nth reconnection attempt:
        /// <c>baseMs * 2^attempt</c>, clamped to <paramref name="capMs"/>. A negative
        /// <paramref name="attempt"/> is clamped to 0 (first-attempt delay). The doubling is done by
        /// comparing against the cap BEFORE each shift, so a large <paramref name="attempt"/>
        /// saturates to the cap instead of overflowing to a negative/wrapped value — the returned
        /// delay is always in the range <c>[min(baseMs, capMs), capMs]</c> for non-negative inputs.
        /// </summary>
        public static long BackoffDelayMs(int attempt, long baseMs, long capMs)
        {
            if (attempt < 0) attempt = 0;

            long delay = baseMs;
            for (int i = 0; i < attempt; i++)
            {
                // Cap-check before doubling: once we would meet/exceed the cap, saturate. This is the
                // overflow guard — we never let `delay` grow past the cap, so `delay * 2` can't wrap.
                if (delay >= capMs)
                    return capMs;
                delay *= 2;
            }

            return delay > capMs ? capMs : delay;
        }

        /// <summary>
        /// True iff reconnection should stop: <paramref name="attempt"/> (0-based count of attempts
        /// already made) has reached or passed <paramref name="maxAttempts"/>. At the boundary
        /// (<c>attempt == maxAttempts</c>) we give up — the Nth attempt was the last allowed.
        /// </summary>
        public static bool ShouldGiveUp(int attempt, int maxAttempts)
        {
            return attempt >= maxAttempts;
        }
    }
}
