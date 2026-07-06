namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// PURE decision data: the <c>GeoMap</c> aggregate events the channel-#5 dirty subscription binds
    /// (<c>GeoSiteReflection.Subscribe</c> iterates this list; unit tests pin it — the reflection binding
    /// itself needs live game types). Every event carries the changed SITE CARRIER as arg 0 (the GeoSite,
    /// or the GeoHaven for the WA-2 haven family — unwrapped via <c>GeoSiteReflection.GetOwningSiteId</c>).
    /// </summary>
    public static class GeoSiteDirtyEvents
    {
        // SiteAdded/SiteRemoved bound for symmetry; SiteFirstTimeVisited covers the Visited-only flip;
        // SiteMissionStarted/Ended/Cancelled drive the P1 ActiveMission mirror (GeoMap.cs:263-277).
        // WA-2 HAVEN family (GeoMap.cs:279-283): HavenPopulationChanged (void(GeoHaven,int,int)),
        // HavenPopulationZoneAttrition (void(GeoHaven, GeoHavenZone) — DIRTY TRIGGER only, per-zone health
        // not carried) and HavenInfestationStateChanged (Action<GeoHaven>) drive the haven tail.
        // WA-2 commit 2: SiteAddonsChanged (GeoMap.cs:257, void(GeoSite, GeoSiteAddonDef, bool)) +
        // SiteAlienBaseTypeChanged (GeoMap.cs:287, void(GeoSite, GeoAlienBaseTypeDef, GeoAlienBaseTypeDef))
        // drive the alien-base tail. Both carry the GeoSite as arg 0.
        public static readonly string[] GeoMapEventNames =
        {
            "SiteOwnerChanged", "SiteStateChanged", "SiteVisibilityChanged",
            "SiteInspectedChanged", "SiteFirstTimeVisited", "SiteAdded", "SiteRemoved",
            "SiteMissionStarted", "SiteMissionEnded", "SiteMissionCancelled",
            "HavenPopulationChanged", "HavenPopulationZoneAttrition", "HavenInfestationStateChanged",
            "SiteAddonsChanged", "SiteAlienBaseTypeChanged",
        };

        // WA-2 excavation dirty triggers (gap 3c): GeoPhoenixFaction.OnExcavationStarted/OnExcavationCompleted
        // (GeoPhoenixFaction.cs:280-282). Both are ExcavationCompletedHanlder(GeoPhoenixFaction faction,
        // SiteExcavationState excavation) — the SITE CARRIER is arg 1 (unwrapped via SiteExcavationState.Site).
        public static readonly string[] PhoenixFactionEventNames =
        {
            "OnExcavationStarted", "OnExcavationCompleted",
        };

        // Attack-schedule dirty trigger (audit gap 6b): GeoFaction.SiteAttackScheduled
        // (FactionSiteAttackHandler(GeoFaction, SiteAttackSchedule), GeoFaction.cs:319; raised by
        // ScheduleAttackOnSite :1932 and AttackAncientSite :532). Subscribed on EVERY faction (any faction —
        // alien or human — can arm a pre-attack countdown); the SITE CARRIER is arg 1 (unwrapped via
        // SiteAttackSchedule.Site). The attack FIRING needs no own trigger: mission creation dirties the
        // site via SiteMissionStarted and the re-snapshot then carries the now-disarmed (empty) tail.
        public static readonly string[] GeoFactionEventNames =
        {
            "SiteAttackScheduled",
        };
    }
}
