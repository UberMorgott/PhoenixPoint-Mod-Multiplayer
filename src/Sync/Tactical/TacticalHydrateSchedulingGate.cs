namespace Multipleer.Sync.Tactical
{
    /// <summary>
    /// Pure, Unity-free decision for HOW the CLIENT must drive its deploy hydrate.
    ///
    /// Root cause this guards (RCA 2026-06-18, round-6 2-instance log decode): once the deploy ARRIVAL
    /// gate began correctly hydrating the already-live client tactical level (no relaunch), the hydrate
    /// itself threw:
    ///   <c>DeserializeGraph failed: InvalidOperationException: Timing.Current should be called from
    ///   inside a running IUpdateable — at Base.Core.Timing.get_Current → Serializer+&lt;Read&gt;.MoveNext
    ///   → TacticalDeploySync.Pump → DeserializeGraph</c>.
    /// The native <c>Serializer.Read</c> coroutine reads <c>Timing.Current</c>, which throws unless the
    /// calling thread is inside a running <c>IUpdateable</c> update tick (Timing.cs:37-47:
    /// <c>CurrentUpdateable == null ⇒ throw</c>). The HOST serialize works only because it is pumped
    /// inside its deferred-capture coroutine (started via <c>Timing.Start</c>); the CLIENT hydrate was
    /// driven INLINE from the network inbound callback (OnDeployReceived → ClientOnLevelReady →
    /// DeserializeGraph → Pump), outside any IUpdateable ⇒ <c>Timing.Current</c> threw and the mirror
    /// never armed.
    ///
    /// FIX (symmetric with the proven host path): run the whole client hydrate body inside a coroutine
    /// started on the level's <c>Timing</c>, so the serializer pump executes inside a running
    /// IUpdateable. This gate is the pure seam deciding that:
    ///   • A live Timing is resolvable → DEFER the hydrate onto it (the correct path).
    ///   • No Timing could be resolved → fall back to an INLINE hydrate (best-effort; may still throw on
    ///     the serializer, but better than silently doing nothing — mirrors the host's
    ///     <c>StartDeferredCapture</c> immediate fallback).
    /// The host never reaches this gate.
    /// </summary>
    public static class TacticalHydrateSchedulingGate
    {
        public enum Decision
        {
            /// <summary>A Timing is available → start the hydrate as a coroutine on it (serializer pump
            /// then runs inside a running IUpdateable, so Timing.Current resolves).</summary>
            DeferOnTiming,
            /// <summary>No Timing resolvable → hydrate inline as a best-effort fallback.</summary>
            HydrateInline,
        }

        /// <summary>
        /// Decide how the client should drive the hydrate.
        /// </summary>
        /// <param name="hasTiming">
        /// True if a <c>Base.Core.Timing</c> could be resolved (from the live tactical level or
        /// <c>Timing.Current</c>) on which to start the hydrate coroutine.
        /// </param>
        public static Decision Decide(bool hasTiming)
            => hasTiming ? Decision.DeferOnTiming : Decision.HydrateInline;
    }
}
