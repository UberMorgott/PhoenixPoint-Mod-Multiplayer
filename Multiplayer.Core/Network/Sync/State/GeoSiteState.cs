using System;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// One site's mirrored IDENTITY: the fields the geoscape-event card / native art collection reads off
    /// <c>Context.Site</c> (Owner / Type / State / EncounterID, plus the site name loc-key for token text).
    /// A pure value type with structural equality so the codec round-trip is directly assertable.
    ///
    /// <c>SiteType</c> and <c>State</c> carry the RAW enum integer value (NOT an ordinal): the game enums are
    /// sparse — <c>GeoSiteType</c> { None=0, PhoenixBase=10, … Marketplace=110 } and <c>GeoSiteState</c>
    /// { None=0, Functioning=1, Destroyed=2, Abandoned=4 } — both fit in a byte, and the client converts back
    /// via <c>Enum.ToObject(enumType, byteValue)</c>. <c>OwnerFactionDefGuid</c> is the owning
    /// <c>GeoFaction.Def.Guid</c> (resolved back to a live faction on the client). <c>SiteName</c> is the
    /// <c>LocalizedTextBind.LocalizationKey</c> the card's token replacement reads.
    ///
    /// The per-faction EXPLORED-STATE FAMILY (all read for <c>ViewerFaction</c>, the shared Phoenix faction of a
    /// co-op campaign — <c>GeoSiteFactionData</c> { Visible, Inspected, Visited }, GeoSiteFactionData.cs:12-19):
    ///   • <c>Inspected</c> — <c>GetInspected</c> (GeoSite.cs:398). A site EXPLORATION completion sets it
    ///     (<c>GeoFaction.OnVehicleSiteExplored</c> → <c>SetInspected(faction, true)</c>, GeoFaction.cs:1922);
    ///     the un-inspected map icon is the "?" marker (GeoSiteVisualsController.cs:239).
    ///   • <c>Visible</c> — <c>GetVisible</c> (GeoSite.cs:387). Exploration also REVEALS sites around the POI
    ///     (<c>UpdateVehicleSite</c> → <c>RevealAroundSite</c> → <c>SetVisible(faction, true)</c>,
    ///     GeoFaction.cs:1908 / GeoSite.cs:896-910); an invisible site renders NO marker at all
    ///     (GeoSiteVisualsController.cs:195), so without carrying it the newly revealed POIs never appear
    ///     on the sim-frozen client.
    ///   • <c>Visited</c> — <c>GetVisited</c> (GeoSite.cs:370), set on first visit/exploration
    ///     (<c>UpdateVehicleSite</c> → <c>SetVisited(faction, true)</c>, GeoFaction.cs:1907); feeds the haven
    ///     visited icon (GeoSiteVisualsController.cs:327) + FindPhoenixBase objectives.
    /// All three are per-faction display state the sim-frozen client never derives — the host reads them off the
    /// live site, the client mirrors them. Optional trailing fields (default false) so DTO callers stay stable.
    /// </summary>
    public readonly struct GeoSiteState : IEquatable<GeoSiteState>
    {
        public readonly int SiteId;
        public readonly string OwnerFactionDefGuid;
        public readonly byte SiteType;     // raw GeoSiteType enum value
        public readonly byte State;        // raw GeoSiteState enum value
        public readonly string SiteName;   // LocalizedTextBind.LocalizationKey
        public readonly string EncounterID;
        public readonly bool Inspected;    // GetInspected(ViewerFaction) — per-faction site reveal (exploration outcome)
        public readonly bool Visible;      // GetVisible(ViewerFaction) — site shown on the map at all (RevealAroundSite outcome)
        public readonly bool Visited;      // GetVisited(ViewerFaction) — first-visit flag (haven visited icon / objectives)
        public readonly GeoMissionRecord Mission; // site.ActiveMission mirror (P1); null = TOMBSTONE (no active mission)
        public readonly GeoHavenTail Haven;       // WA-2 haven tail (extras block); null = not carried (never a clear)
        public readonly GeoAlienBaseTail AlienBase;     // WA-2 alien-base tail; null = not carried
        public readonly GeoExcavationTail Excavation;   // WA-2 excavation tail; null = not carried
        public readonly GeoAttackTail Attack;           // pre-attack schedule tail (gap 6b); null = not carried
        public readonly GeoWeatherTail Weather;         // weather tail (gap 6f, bit6); null = host weather is Clear (client resets)
        public readonly GeoExpiringTimerTail ExpiringTimer; // expiring-timer tail (bit7); null = host timer is Zero (client clears)
        public readonly GeoFacilityTail Facility;       // W1 facility working-state tail (separate facility section); null = not carried (non-base / older payload)

        public GeoSiteState(int siteId, string ownerFactionDefGuid, byte siteType, byte state, string siteName, string encounterID,
                            bool inspected = false, bool visible = false, bool visited = false,
                            GeoMissionRecord mission = null, GeoHavenTail haven = null,
                            GeoAlienBaseTail alienBase = null, GeoExcavationTail excavation = null,
                            GeoAttackTail attack = null, GeoWeatherTail weather = null,
                            GeoExpiringTimerTail expiringTimer = null, GeoFacilityTail facility = null)
        {
            SiteId = siteId;
            // Normalize null → "" so equality + the wire are stable (the codec also coalesces, this keeps
            // an in-memory DTO comparable to its decoded twin).
            OwnerFactionDefGuid = ownerFactionDefGuid ?? "";
            SiteType = siteType;
            State = state;
            SiteName = siteName ?? "";
            EncounterID = encounterID ?? "";
            Inspected = inspected;
            Visible = visible;
            Visited = visited;
            Mission = mission;
            Haven = haven;
            AlienBase = alienBase;
            Excavation = excavation;
            Attack = attack;
            Weather = weather;
            ExpiringTimer = expiringTimer;
            Facility = facility;
        }

        public bool Equals(GeoSiteState other)
            => SiteId == other.SiteId
               && OwnerFactionDefGuid == other.OwnerFactionDefGuid
               && SiteType == other.SiteType
               && State == other.State
               && SiteName == other.SiteName
               && EncounterID == other.EncounterID
               && Inspected == other.Inspected
               && Visible == other.Visible
               && Visited == other.Visited
               && (Mission == null ? other.Mission == null : Mission.Equals(other.Mission))
               && (Haven == null ? other.Haven == null : Haven.Equals(other.Haven))
               && (AlienBase == null ? other.AlienBase == null : AlienBase.Equals(other.AlienBase))
               && (Excavation == null ? other.Excavation == null : Excavation.Equals(other.Excavation))
               && (Attack == null ? other.Attack == null : Attack.Equals(other.Attack))
               && (Weather == null ? other.Weather == null : Weather.Equals(other.Weather))
               && (ExpiringTimer == null ? other.ExpiringTimer == null : ExpiringTimer.Equals(other.ExpiringTimer))
               && (Facility == null ? other.Facility == null : Facility.Equals(other.Facility));

        public override bool Equals(object obj) => obj is GeoSiteState o && Equals(o);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = SiteId;
                h = (h * 397) ^ (OwnerFactionDefGuid?.GetHashCode() ?? 0);
                h = (h * 397) ^ SiteType;
                h = (h * 397) ^ State;
                h = (h * 397) ^ (SiteName?.GetHashCode() ?? 0);
                h = (h * 397) ^ (EncounterID?.GetHashCode() ?? 0);
                h = (h * 397) ^ (Inspected ? 1 : 0);
                h = (h * 397) ^ (Visible ? 2 : 0);
                h = (h * 397) ^ (Visited ? 4 : 0);
                h = (h * 397) ^ (Mission?.GetHashCode() ?? 0);
                h = (h * 397) ^ (Haven?.GetHashCode() ?? 0);
                h = (h * 397) ^ (AlienBase?.GetHashCode() ?? 0);
                h = (h * 397) ^ (Excavation?.GetHashCode() ?? 0);
                h = (h * 397) ^ (Attack?.GetHashCode() ?? 0);
                h = (h * 397) ^ (Weather?.GetHashCode() ?? 0);
                h = (h * 397) ^ (ExpiringTimer?.GetHashCode() ?? 0);
                h = (h * 397) ^ (Facility?.GetHashCode() ?? 0);
                return h;
            }
        }

        public override string ToString()
            => $"Site({SiteId} owner={OwnerFactionDefGuid} type={SiteType} state={State} name={SiteName} enc={EncounterID} insp={Inspected} vis={Visible} visited={Visited} mission={(Mission == null ? "none" : Mission.ToString())} haven={(Haven == null ? "none" : Haven.ToString())} alienBase={(AlienBase == null ? "none" : AlienBase.ToString())} excav={(Excavation == null ? "none" : Excavation.ToString())} attack={(Attack == null ? "none" : Attack.ToString())} weather={(Weather == null ? "none" : Weather.ToString())} expTimer={(ExpiringTimer == null ? "none" : ExpiringTimer.ToString())} facility={(Facility == null ? "none" : Facility.ToString())})";
    }
}
