using Multipleer.Network.Sync.State;
using Xunit;

/// <summary>
/// Pure missing-member tagging behind the host native-drive reflection gate
/// (<c>EventReflection.TryHostNativeResolve</c> / <c>TryHostNativeAdvanceSingleChoice</c>). The in-game
/// 2026-07-03 false-negative (host never drove a client-requested prompt→result advance) hid behind the
/// combined "guard=not-ready/missing-member" tag; these tests pin one DISTINCT tag per failure cause and
/// that a fully-resolved lookup set passes the gate (null) — the modal-showing readiness itself is a
/// separate runtime check (module/_geoEvent state), not part of this member gate.
/// </summary>
public class NativeDriveGuardTests
{
    private static string Tag(
        bool ready = true,
        bool hasViewField = true,
        bool hasModulesField = true,
        bool hasSiteEncModuleField = true,
        bool hasGeoEventField = true,
        bool hasOnChoiceSelected = true)
        => NativeDriveGuard.MissingMemberTag(
            ready, hasViewField, hasModulesField, hasSiteEncModuleField, hasGeoEventField, hasOnChoiceSelected);

    [Fact]
    public void AllMembersResolved_GatePasses()
    {
        Assert.Null(Tag());
    }

    [Fact]
    public void NotReady_TagsCoreEventLookups()
    {
        Assert.Equal("not-ready(core-event-lookups)", Tag(ready: false));
    }

    [Fact]
    public void MissingViewField_TagsGeoLevelControllerView()
    {
        Assert.Equal("missing-GeoLevelController.View", Tag(hasViewField: false));
    }

    [Fact]
    public void MissingModulesField_TagsGeoscapeViewGeoscapeModules()
    {
        Assert.Equal("missing-GeoscapeView.GeoscapeModules", Tag(hasModulesField: false));
    }

    [Fact]
    public void MissingSiteEncModuleField_TagsGeoscapeModulesDataSiteEncountersModule()
    {
        // THE 2026-07-03 root cause: GeoscapeModulesData lives in namespace Base.UI, the lookup used
        // PhoenixPoint.Geoscape.View → TypeByName null → this member never resolved. Distinct tag = one
        // log line pins it without an in-game repro-click marathon.
        Assert.Equal("missing-GeoscapeModulesData.SiteEncountersModule", Tag(hasSiteEncModuleField: false));
    }

    [Fact]
    public void MissingGeoEventField_TagsUIModuleSiteEncountersGeoEvent()
    {
        Assert.Equal("missing-UIModuleSiteEncounters._geoEvent", Tag(hasGeoEventField: false));
    }

    [Fact]
    public void MissingOnChoiceSelected_TagsUIModuleSiteEncountersOnChoiceSelected()
    {
        Assert.Equal("missing-UIModuleSiteEncounters.OnChoiceSelected", Tag(hasOnChoiceSelected: false));
    }

    [Fact]
    public void NotReady_WinsOverAnyMissingMember()
    {
        // Fixed precedence: not-ready first (the core lookups gate everything else), then the module
        // chain outermost→innermost so the FIRST unreachable link is the one named.
        Assert.Equal("not-ready(core-event-lookups)", Tag(ready: false, hasSiteEncModuleField: false));
        Assert.Equal("missing-GeoscapeView.GeoscapeModules", Tag(hasModulesField: false, hasOnChoiceSelected: false));
    }
}
