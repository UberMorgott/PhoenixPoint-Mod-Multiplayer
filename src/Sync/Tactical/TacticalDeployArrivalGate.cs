namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// Pure, Unity-free decision for HOW a CLIENT applies a just-received <c>tac.deploy</c>.
    ///
    /// Root cause this guards (RCA 2026-06-18, round-5 2-instance log decode): the client enters its
    /// own tactical level through the EXISTING co-op load barrier (curtain Loaded→Playing →
    /// OnReachedPlaying → SendLoadComplete → OnRevealAll), NOT through a deploy-driven
    /// <c>GeoLevelController.LaunchTacticalGame</c>. So by the time the host's chunked snapshot
    /// (≈659 KB / 14 chunks) finishes reassembling on the client, the client has ALREADY left the
    /// geoscape and is sitting in a live tactical level. Two consequences were observed:
    ///
    ///   1. The Playing-transition hydrate hook (<c>ClientOnLevelReady</c>) fired ~1 s BEFORE the deploy
    ///      arrived, hit its <c>_pendingClientDeploy == null</c> guard, and silently returned.
    ///   2. When the deploy finally arrived, <c>OnDeployReceived</c> ran <c>ClientLaunchMission</c>, which
    ///      assumes the client is still in the geoscape — its first call <c>GeoLevelController()</c>
    ///      returned null ("ClientLaunchMission: no GeoLevelController"), so the client never hydrated,
    ///      <c>MirrorArmed</c> stayed false, and every subsequent <c>tac.move</c> failed with
    ///      "no actor for netId" (the actor registry was never built).
    ///
    /// FIX: branch on whether a live tactical level already exists when the deploy arrives.
    ///   • A live client TLC is present  → the client is already in its tactical level (the real co-op
    ///     flow): hydrate that existing level immediately (ProcessInstanceData + arm mirror).
    ///   • No live tactical level         → the legacy geoscape path: drive the deploy-driven launch,
    ///     hydrate on the subsequent Playing transition.
    /// The host is never a client and never reaches this gate.
    /// </summary>
    public static class TacticalDeployArrivalGate
    {
        public enum Decision
        {
            /// <summary>Client already in a live tactical level → hydrate it now (the real co-op flow).</summary>
            HydrateExisting,
            /// <summary>Client still in the geoscape → drive the deploy-driven launch, hydrate on Playing.</summary>
            LaunchThenHydrate,
        }

        /// <summary>
        /// Decide how the client should apply a received deploy.
        /// </summary>
        /// <param name="hasLiveTacticalLevel">
        /// True if <c>GameUtl.CurrentLevel()</c> is already a <c>TacticalLevelController</c> on this client
        /// (it entered its tactical level via the co-op load barrier before the deploy reassembled).
        /// </param>
        public static Decision Decide(bool hasLiveTacticalLevel)
            => hasLiveTacticalLevel ? Decision.HydrateExisting : Decision.LaunchThenHydrate;
    }
}
