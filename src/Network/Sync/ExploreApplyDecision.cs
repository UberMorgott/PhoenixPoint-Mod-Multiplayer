namespace Multiplayer.Network.Sync
{
    /// <summary>Which path the host uses to apply a relayed EXPLORE-SITE order.</summary>
    public enum ExploreApplyPath
    {
        /// <summary>Run the native <c>ExploreSiteAbility.Activate(target)</c> — the SAME entrypoint a local host
        /// click uses (validation + cost + <c>ActivateInternal</c> → <c>StartExploringCurrentSite</c>).</summary>
        NativeActivate,
        /// <summary>Fall back to a direct <c>GeoVehicle.StartExploringCurrentSite()</c> call — used when the native
        /// ability / target can't be resolved or <c>CanActivate</c> is false (defensive: never drop a valid host
        /// order; the outcome still mirrors via 0xA7 + the geoscape-event echoes).</summary>
        FallbackDirect,
    }

    /// <summary>
    /// PURE (Unity-free, unit-testable) guard that decides how the HOST applies a client-relayed explore order.
    /// The client already validated the intent and the host is authoritative, so the decision is: use the native
    /// ability-activation entrypoint when it is resolvable AND would succeed (double-activation guard — the native
    /// <c>ExploreSiteAbility.ActivateInternal</c> already no-ops while <c>IsExploringSite</c>, but we still gate on
    /// <c>CanActivate</c> so we never trip its <c>Debug.LogError</c> disabled path); otherwise fall back to the
    /// raw direct call (today's proven behavior). The reflection that resolves the ability/target/CanActivate lives
    /// in <see cref="VehicleTravelReflection"/>; this class is only the boundary logic so it can be tested without
    /// any game reference.
    /// </summary>
    public static class ExploreApplyDecision
    {
        /// <summary>Decide the apply path from the three resolved facts. NativeActivate ONLY when the native ability
        /// and its default target both resolved AND the ability reports it can activate that target; anything else
        /// (unresolved ability/target, or CanActivate false — already inspected / invalid / mid-explore) →
        /// FallbackDirect. PURE.</summary>
        public static ExploreApplyPath Decide(bool abilityResolved, bool targetResolved, bool canActivate)
            => (abilityResolved && targetResolved && canActivate)
                ? ExploreApplyPath.NativeActivate
                : ExploreApplyPath.FallbackDirect;
    }
}
