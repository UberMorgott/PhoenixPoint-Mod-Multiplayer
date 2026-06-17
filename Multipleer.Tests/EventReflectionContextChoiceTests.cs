using Multipleer.Network.Sync;
using Xunit;

// Pure decision: which GeoscapeEventContext shape does BuildEvent use? A site that did NOT resolve on the
// client (siteless event siteId<0, OR a site absent from this client's map) must use the faction-only
// SITELESS context (native renders title-only) — NEVER StartingBase ("Точка Феникс"). A resolved site uses
// the site context.
public class EventReflectionContextChoiceTests
{
    [Fact]
    public void SitelessEvent_NoSiteId_UsesSitelessContext()
    {
        Assert.True(EventReflection.UsesSitelessContext(resolvedSite: false, siteId: -1));
    }

    [Fact]
    public void SiteEvent_SiteAbsentOnClient_UsesSitelessContext()
    {
        // siteId >= 0 but the site did NOT resolve on this (sim-frozen) client → still siteless, NOT StartingBase.
        Assert.True(EventReflection.UsesSitelessContext(resolvedSite: false, siteId: 1337));
    }

    [Fact]
    public void SiteEvent_SitePresent_UsesSiteContext()
    {
        Assert.False(EventReflection.UsesSitelessContext(resolvedSite: true, siteId: 1337));
    }
}
