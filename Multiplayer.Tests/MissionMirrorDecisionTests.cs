using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// Pure branch logic of the client mission mirror (GeoSiteReflection.ApplyMission): the contested-site
/// progress-circle fix hinges on the KeepRefresh branch — a re-apply for an ALREADY-mirrored mission must
/// refresh the existing instance (deployments tick down hourly on the host), never rebuild, never no-op.
/// </summary>
public class MissionMirrorDecisionTests
{
    private static GeoMissionRecord Haven(string defGuid = "DEF", int atk = 100, int def = 200)
        => new GeoMissionRecord(GeoMissionRecord.HavenDefense, defGuid,
            attackerFactionDefGuid: "FAC", attackerDeployment: atk, defenderDeployment: def);

    [Fact]
    public void NullRecord_NothingAttached_IsNoOp()
    {
        Assert.Equal(MissionMirrorAction.None,
            MissionMirrorDecision.Decide(hasCurrent: false, 0, null, rec: null));
    }

    [Fact]
    public void NullRecord_WithMirrorAttached_Clears()
    {
        Assert.Equal(MissionMirrorAction.Clear,
            MissionMirrorDecision.Decide(hasCurrent: true, GeoMissionRecord.HavenDefense, "DEF", rec: null));
    }

    [Fact]
    public void UnknownClass_NeverAttaches_ClearsExistingMirror()
    {
        var unknown = new GeoMissionRecord(GeoMissionRecord.Unknown, "DEF");
        Assert.Equal(MissionMirrorAction.Clear,
            MissionMirrorDecision.Decide(hasCurrent: true, GeoMissionRecord.HavenDefense, "DEF", unknown));
        Assert.Equal(MissionMirrorAction.None,
            MissionMirrorDecision.Decide(hasCurrent: false, 0, null, unknown));
    }

    [Fact]
    public void SameClassSameDef_RefreshesExistingInstance()
    {
        // THE progress-circle path: hourly host deployments re-flush the SAME mission — the client must
        // stamp the attached instance in place (the pie controller holds a reference to it).
        Assert.Equal(MissionMirrorAction.KeepRefresh,
            MissionMirrorDecision.Decide(hasCurrent: true, GeoMissionRecord.HavenDefense, "DEF",
                Haven(atk: 90, def: 180)));
    }

    [Fact]
    public void NothingAttached_Rebuilds()
    {
        Assert.Equal(MissionMirrorAction.Rebuild,
            MissionMirrorDecision.Decide(hasCurrent: false, 0, null, Haven()));
    }

    [Fact]
    public void ClassMismatch_Rebuilds()
    {
        Assert.Equal(MissionMirrorAction.Rebuild,
            MissionMirrorDecision.Decide(hasCurrent: true, GeoMissionRecord.Scavenging, "DEF", Haven()));
    }

    [Fact]
    public void DefGuidMismatch_Rebuilds()
    {
        Assert.Equal(MissionMirrorAction.Rebuild,
            MissionMirrorDecision.Decide(hasCurrent: true, GeoMissionRecord.HavenDefense, "OTHER_DEF", Haven()));
    }
}
