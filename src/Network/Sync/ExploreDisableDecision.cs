namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// PURE (Unity-free, unit-testable) decision for the DELIBERATE UX improvement: grey out the
    /// <c>ExploreSiteAbility</c> button while the vehicle is ALREADY exploring a site (native PP leaves it lit —
    /// <c>ExploreSiteAbility.GetDisabledStateInternal</c> never checks <c>IsExploringSite</c>). The button must be
    /// disabled on BOTH co-op sides during an active exploration of that vehicle.
    ///
    /// The two sides read DIFFERENT authoritative sources for "is this vehicle exploring", and this gate encodes
    /// that source-selection so the game-bound patch stays a thin reflection shell:
    ///   • HOST / single-player (<paramref name="onActiveClient"/> == false) → the native
    ///     <c>GeoVehicle.IsExploringSite</c> (the real, authoritative timer state).
    ///   • CLIENT (active co-op session, not host) → the mirrored 0xA7 exploring flag
    ///     (<c>GeoVehicleExploreMirror.IsExploringMirrored</c>) — robust even if the client's best-effort native
    ///     bar-spawn (which would otherwise flip <c>IsExploringSite</c>) failed.
    /// The "other side's" value is deliberately IGNORED for the current side, so a stale cross-source reading can
    /// never leak into the decision.
    /// </summary>
    public static class ExploreDisableDecision
    {
        /// <summary>True ⇒ force the explore button into a DISABLED state. On an active client the mirrored flag is
        /// authoritative; otherwise (host / single-player) the native exploring flag is. PURE.</summary>
        public static bool ShouldDisable(bool onActiveClient, bool hostNativeExploring, bool clientMirrorExploring)
            => onActiveClient ? clientMirrorExploring : hostNativeExploring;
    }
}
