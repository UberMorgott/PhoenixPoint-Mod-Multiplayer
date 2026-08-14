namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// THE ONE PREDICATE EVERY CLIENT-SIDE REFUSAL GATE ASKS. Pure, allocation-free, Unity-free —
    /// RailCheck (L506) invokes it directly outside the engine.
    ///
    /// WHY IT IS NOT <c>IsActiveSession</c>. <c>NetworkEngine.IsActiveSession</c> (NetworkEngine.cs:58)
    /// adds a third term — <c>IsHost || Session?.HostPeerId.HasValue</c>. That term is UNKNOWN for a real
    /// stretch of a client's life: mid-load, across a mission boundary, and above all right after the save
    /// transfer, where <c>Session.HostPeerId</c> is momentarily unset. Every research-cascade RCA of
    /// 2026-08-14 bottoms out there — a refusal gate that consults it is OPEN exactly in the window where
    /// the client is most dangerous. A peer whose engine is live and is not the host IS a client, session
    /// predicate or not, so the term is deliberately DROPPED.
    ///
    /// WHY THE TWO TERMS IT DOES KEEP ARE THE RIGHT ONES:
    ///   • <c>IsActive</c> — set in <c>Initialize()</c> (:175/:212) BEFORE any connect and cleared only in
    ///     <c>Shutdown()</c> (:246) / <c>TearDown()</c> (:286). It is what keeps SOLO open. <c>Shutdown</c>
    ///     leaves <c>Instance</c> NON-NULL (only <c>TearDown</c>:294 nulls it), so a peer that backs out of
    ///     the lobby after a join attempt and then starts a single-player campaign still has an engine with
    ///     <c>IsActive=false, IsHost=false</c>. Without this term such a peer reads as a CLIENT and every
    ///     refusal gate holds in SINGLE PLAYER — that is a real freeze, not a hypothetical.
    ///   • <c>IsHost</c> — set in <c>StartHost</c>:317 BEFORE the bind is known to have succeeded, so a host
    ///     whose transport failed is never demoted to "client" and never has its own sim gated away.
    ///
    /// FAIL OPEN ONLY WHERE OPENING IS FREE. This predicate serves REFUSAL gates, where a wrong refusal
    /// costs one stale value the next delta re-states. It must NOT be used for CAPTURE seams
    /// (<c>IntentRail.ShouldRunNative</c>): a capture that fires with no host link swallows the player's
    /// input and native never ran — nothing restores it — so capture keeps the <c>IsActiveSession</c>
    /// term and stays fail-OPEN. L506 arm (e) holds that split in place.
    /// </summary>
    internal static class ClientAuthority
    {
        /// <summary>True iff this peer is a LIVE non-host peer. Engine gone or torn down → false → native
        /// runs, which is what solo and post-teardown need.</summary>
        internal static bool IsClient()
        {
            var e = NetworkEngine.Instance;
            return e != null && e.IsActive && !e.IsHost;
        }

        /// <summary>DIAGNOSTIC ONLY — never a gate term. True when the session predicate a gate USED to be
        /// written against reads false, i.e. this call sits inside the unknown window. Lives here, not in
        /// the gates, so <c>IsActiveSession</c> has exactly one reader on the refusal side and L506's IL
        /// sweep can keep the gates themselves free of engine reads.</summary>
        internal static bool InUnknownWindow()
        {
            var e = NetworkEngine.Instance;
            return e == null || !e.IsActiveSession;
        }
    }
}
