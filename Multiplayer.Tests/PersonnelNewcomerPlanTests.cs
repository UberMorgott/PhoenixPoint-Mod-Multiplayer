using System.Collections.Generic;
using Multiplayer.Network.Sync.State;
using Xunit;

// PS1/PS2 hire-gap placement core (pure): decide WHERE a brand-new (never-seen) soldier is materialized —
// the site whose mirrored roster lists its GeoUnitId. Ids already live on the client are not newcomers;
// the None sentinel (0) is never placed; first roster wins. The game glue (PersonnelReflection blob decode
// + value-only _tacUnits add) is game-bound + in-game-gated, mirroring how the sibling cores are tested.
public class PersonnelNewcomerPlanTests
{
    private static PersonnelSiteRoster Site(int id, params long[] units) => new PersonnelSiteRoster(id, units);

    [Fact]
    public void HiredSoldier_NotLive_IsPlacedAtItsSite()
    {
        var sites = new[] { Site(166, 10, 13) };   // 10 is an existing crew member, 13 is the hire
        var live = new HashSet<long> { 10 };

        var plan = PersonnelNewcomerPlan.ResolvePlacements(sites, live);

        Assert.Single(plan);
        Assert.Equal(166, plan[13]);
    }

    [Fact]
    public void AllListedIdsAlreadyLive_EmptyPlan()
    {
        var sites = new[] { Site(166, 10, 11), Site(170, 12) };
        var live = new HashSet<long> { 10, 11, 12 };

        Assert.Empty(PersonnelNewcomerPlan.ResolvePlacements(sites, live));
    }

    [Fact]
    public void NoneSentinelZero_NeverPlaced()
    {
        var sites = new[] { Site(166, 0, 13) };

        var plan = PersonnelNewcomerPlan.ResolvePlacements(sites, new HashSet<long>());

        Assert.False(plan.ContainsKey(0));
        Assert.Equal(166, plan[13]);
    }

    [Fact]
    public void FirstRosterWins_WhenIdListedTwice()
    {
        var sites = new[] { Site(166, 13), Site(170, 13) };

        var plan = PersonnelNewcomerPlan.ResolvePlacements(sites, new HashSet<long>());

        Assert.Equal(166, plan[13]);
    }

    [Fact]
    public void NullSitesOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(PersonnelNewcomerPlan.ResolvePlacements(null, new HashSet<long>()));
        Assert.Empty(PersonnelNewcomerPlan.ResolvePlacements(new PersonnelSiteRoster[0], new HashSet<long>()));
    }
}
