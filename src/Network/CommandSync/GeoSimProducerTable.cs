using System.Collections.Generic;

namespace Multipleer.Network.CommandSync
{
    // Auditable, pure (Unity-free) declarative list of the CLOSED 13-producer geoscape-sim set the
    // CLIENT must suppress so it runs ZERO stochastic/clock-driven authoritative simulation (Inc1 A).
    // Every entry is a `private NextUpdate <Method>(Timing)` callback started via Timing.Start;
    // ClientGeoSimSuppressPatch resolves each row (disambiguated by the single Base.Core.Timing param)
    // and prefixes it to return NextUpdate.Never on the client. Best-effort by design — a missed/renamed
    // producer is bounded local jitter, self-healed by the host save-blob snapshot on join.
    //
    // WHITELIST (must NOT appear here — render/cosmetic/log, kept alive on client):
    //   GeoNavComponent.NavigateRoutine, MistRendererSystem.FrameUpdate, GeoscapeLog.ProcessQueuedEvents.
    //
    // Decompile-verified decl lines (E:\DEV\PhoenixPoint\decompiled\AssemblyCSharp\...\src) — RE-PINNED 2026-06-17:
    //   GeoLevelController.LevelHourlyUpdateCrt:777 (sched :761)
    //   VehicleFactionController.RescheduleDestinationCrt:185 (sched :123/:180/:235)
    //   GeoScavengingSite.RefreshEnemyAtSiteCrt:99 (sched :96/:135)
    //   GeoAlienBase.ExpandAlienBase:291 (sched :154/:479) [overload trap: static ExpandAlienBase(IConsole):580 -> pin Timing param]
    //   GeoScanner.Expand:72 (sched :135)
    //   SiteSurroundingsScanner.ExpandSiteScanner:273 (sched :213)
    //   GeoAncientSiteProbe.CompleteScanCrt:47 (sched :77)
    //   MistRepeller.ExpansMistRepeller:104 (sched :99)
    //   GeoBehemothActor.SubmergeCrt:578 (sched :300/:575)
    //   GeoBehemothActor.EmergeCrt:659 (sched :706)
    //   GeoVehicle.SiteExplorationCompleted:463 (sched :440 via ExploreCurrentSite:437)
    //   GeoHarvestingSite.ResourceHarvestedCompleted:118 (sched :112/:166)
    //   MistRendererSystem.UpdateMist:384 (sched :201)
    public sealed class GeoSimProducer
    {
        public string DeclaringTypeName; // AccessTools.TypeByName key (full namespaced name)
        public string MethodName;        // the NextUpdate(Timing) callback on that type
    }

    public static class GeoSimProducerTable
    {
        public static readonly IReadOnlyList<GeoSimProducer> Producers = new[]
        {
            new GeoSimProducer { DeclaringTypeName = "PhoenixPoint.Geoscape.Levels.GeoLevelController",          MethodName = "LevelHourlyUpdateCrt" },
            new GeoSimProducer { DeclaringTypeName = "PhoenixPoint.Geoscape.Entities.VehicleFactionController",  MethodName = "RescheduleDestinationCrt" },
            new GeoSimProducer { DeclaringTypeName = "PhoenixPoint.Geoscape.Entities.Sites.GeoScavengingSite",  MethodName = "RefreshEnemyAtSiteCrt" },
            new GeoSimProducer { DeclaringTypeName = "PhoenixPoint.Geoscape.Entities.GeoAlienBase",             MethodName = "ExpandAlienBase" },
            new GeoSimProducer { DeclaringTypeName = "PhoenixPoint.Geoscape.Entities.GeoScanner",               MethodName = "Expand" },
            new GeoSimProducer { DeclaringTypeName = "PhoenixPoint.Geoscape.Entities.SiteSurroundingsScanner",  MethodName = "ExpandSiteScanner" },
            new GeoSimProducer { DeclaringTypeName = "PhoenixPoint.Geoscape.Entities.GeoAncientSiteProbe",      MethodName = "CompleteScanCrt" },
            new GeoSimProducer { DeclaringTypeName = "PhoenixPoint.Geoscape.Entities.MistRepeller",             MethodName = "ExpansMistRepeller" },
            new GeoSimProducer { DeclaringTypeName = "PhoenixPoint.Geoscape.Entities.GeoBehemothActor",         MethodName = "SubmergeCrt" },
            new GeoSimProducer { DeclaringTypeName = "PhoenixPoint.Geoscape.Entities.GeoBehemothActor",         MethodName = "EmergeCrt" },
            new GeoSimProducer { DeclaringTypeName = "PhoenixPoint.Geoscape.Entities.GeoVehicle",               MethodName = "SiteExplorationCompleted" },
            new GeoSimProducer { DeclaringTypeName = "PhoenixPoint.Geoscape.Entities.Sites.GeoHarvestingSite",  MethodName = "ResourceHarvestedCompleted" },
            new GeoSimProducer { DeclaringTypeName = "PhoenixPoint.Geoscape.MistRendererSystem",                MethodName = "UpdateMist" }
        };
    }
}
