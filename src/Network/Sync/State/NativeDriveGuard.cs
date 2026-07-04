namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// PURE (Unity-free) missing-member tagging for the HOST native-drive reflection gate shared by
    /// <c>EventReflection.TryHostNativeResolve</c> and <c>EventReflection.TryHostNativeAdvanceSingleChoice</c>.
    ///
    /// Why it exists (in-game false-negative, run 2026-07-03 00:27): the combined
    /// "guard=not-ready/missing-member" log could not say WHICH reflection lookup failed, so the real
    /// culprit — <c>GeoscapeModulesData</c> looked up under the WRONG namespace
    /// (<c>PhoenixPoint.Geoscape.View</c> instead of the actual <c>Base.UI</c>, decompile
    /// Base.UI/GeoscapeModulesData.cs:7) → <c>_gmSiteEncModuleField</c> forever null → the host NEVER
    /// drove the native prompt→result advance a client requested — hid behind the blanket tag for a full
    /// in-game repro. One distinct greppable tag per failure cause; <c>null</c> = every member resolved →
    /// the native drive may proceed.
    /// </summary>
    public static class NativeDriveGuard
    {
        /// <summary>
        /// First failing member's tag, in fixed precedence order (not-ready first, then the module chain
        /// outermost→innermost), or <c>null</c> when the reflection gate passes. Pure — unit-testable.
        /// </summary>
        public static string MissingMemberTag(
            bool ready,
            bool hasViewField,
            bool hasModulesField,
            bool hasSiteEncModuleField,
            bool hasGeoEventField,
            bool hasOnChoiceSelected)
        {
            if (!ready) return "not-ready(core-event-lookups)";
            if (!hasViewField) return "missing-GeoLevelController.View";
            if (!hasModulesField) return "missing-GeoscapeView.GeoscapeModules";
            if (!hasSiteEncModuleField) return "missing-GeoscapeModulesData.SiteEncountersModule";
            if (!hasGeoEventField) return "missing-UIModuleSiteEncounters._geoEvent";
            if (!hasOnChoiceSelected) return "missing-UIModuleSiteEncounters.OnChoiceSelected";
            return null;
        }
    }
}
