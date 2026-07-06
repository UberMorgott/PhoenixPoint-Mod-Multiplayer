namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// PURE (Unity-free, unit-testable) decision for the host "stale Explore button" fix: after the host applies a
    /// CLIENT-relayed explore order, should it HIDE the open site contextual menu?
    ///
    /// RCA: the native host click-path (<c>UIStateVehicleSelected.OnContextualItemSelected</c>,
    /// UIStateVehicleSelected.cs:407) runs <c>ability.Activate(target)</c> then
    /// <c>_contextualMenuModule.HideContextualMenu()</c> (:431) — the POI context menu closes on click. A
    /// client-relayed explore is applied on the host PROGRAMMATICALLY
    /// (<c>VehicleTravelReflection.StartExploringCurrentSite</c> → <c>ExploreSiteAbility.Activate</c>), which
    /// reproduces the Activate but SKIPS <c>HideContextualMenu()</c>. The context menu is built once by
    /// <c>UIModuleSiteContextualMenu.SetMenuItems</c> (item interactability = <c>View.CanActivate</c>,
    /// UIModuleSiteContextualMenu.cs:76/97) and never re-evaluates while open, so the still-open menu keeps a
    /// visibly-active "Explore" (Разведать) button on the host. We hide it — exactly like native — but ONLY when the
    /// open menu is for the site whose exploration just started, so a menu open on a DIFFERENT site is never disturbed.
    /// This is the native hide, NOT the declined "live-refresh the menu while open".
    /// </summary>
    public static class GeoSiteContextMenuDecision
    {
        /// <summary>True ⇒ hide the site contextual menu: it is visible AND its selected site is the (valid) site
        /// whose exploration just started. PURE.</summary>
        public static bool ShouldHide(bool menuVisible, int selectedSiteId, int exploredSiteId)
            => menuVisible && exploredSiteId >= 0 && selectedSiteId == exploredSiteId;
    }
}
