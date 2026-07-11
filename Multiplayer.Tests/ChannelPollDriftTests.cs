using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// Pure signature tests for the host drift-poll backstops added to the unlock (#3), objectives (#7) and
/// GeoSite (#5) channels — the signal each PollHostDrift compares is FNV-1a over the channel's OWN encoder
/// (ResearchChannel idiom), so it must be stable for equal state and drift on any mirrored change. The live
/// PollHostDrift methods bind game types and are not unit-testable (codebase convention); the recruit-pool
/// (#10) and personnel (#9) polls reuse PersonnelStateFlush.Hash / RecruitPoolFlush.HashSlot, already covered
/// by PersonnelStateFlushTests. Mirrors InventorySnapshotTests' drift-signature section.
/// </summary>
public class ChannelPollDriftTests
{
    // ─── the shared drift primitive (Fnv1a over encoded bytes) ───────────────
    [Fact]
    public void Fnv1a_Null_ReturnsZero() => Assert.Equal(0UL, ResearchSnapshot.Fnv1a(null));

    [Fact]
    public void Fnv1a_Stable_ForEqualBytes()
        => Assert.Equal(ResearchSnapshot.Fnv1a(new byte[] { 1, 2, 3 }), ResearchSnapshot.Fnv1a(new byte[] { 1, 2, 3 }));

    [Fact]
    public void Fnv1a_Drifts_ForDifferentBytes()
        => Assert.NotEqual(ResearchSnapshot.Fnv1a(new byte[] { 1, 2, 3 }), ResearchSnapshot.Fnv1a(new byte[] { 1, 2, 4 }));

    // ─── unlock channel (#3) poll signal ───────────────────────────────────
    private static ulong UnlockSig(UnlockSnapshot s) => ResearchSnapshot.Fnv1a(UnlockSnapshot.Encode(s));

    [Fact]
    public void UnlockPollSig_Stable_ForEqualState()
    {
        var a = new UnlockSnapshot(); a.Facilities.Add("FAC_Lab"); a.Manufacture.Add("ITM_Laser");
        var b = new UnlockSnapshot(); b.Facilities.Add("FAC_Lab"); b.Manufacture.Add("ITM_Laser");
        Assert.Equal(UnlockSig(a), UnlockSig(b));
    }

    [Fact]
    public void UnlockPollSig_Drifts_WhenUnlockAppears()
    {
        var a = new UnlockSnapshot(); a.Facilities.Add("FAC_Lab");
        var b = new UnlockSnapshot(); b.Facilities.Add("FAC_Lab"); b.Augmentations.Add("AUG_Bionic");
        Assert.NotEqual(UnlockSig(a), UnlockSig(b));
    }

    // ─── objectives channel (#7) poll signal ────────────────────────────────
    private static ulong ObjSig(ObjectivesSnapshot s) => ResearchSnapshot.Fnv1a(ObjectivesSnapshot.Encode(s));

    private static ObjectivesSnapshot ObjWith(int varValue)
    {
        var s = new ObjectivesSnapshot();
        s.Objectives.Add(new ObjectivesSnapshot.ObjectiveRecord { Disc = ObjectivesSnapshot.DiscEvent, Payload = "SDI_07" });
        s.Variables.Add(("BC_SDI", varValue));
        return s;
    }

    [Fact]
    public void ObjectivesPollSig_Stable_ForEqualState() => Assert.Equal(ObjSig(ObjWith(5)), ObjSig(ObjWith(5)));

    [Fact]
    public void ObjectivesPollSig_Drifts_OnVariableChange()
        => Assert.NotEqual(ObjSig(ObjWith(5)), ObjSig(ObjWith(6)));   // a TFTV quest-step variable write

    // ─── GeoSite channel (#5) per-site poll signal ──────────────────────────
    // Mirrors GeoSiteChannel.SiteSig: FNV-1a of the ONE site's encoded record.
    private static ulong SiteSig(GeoSiteState s)
    {
        var one = new GeoSiteSnapshot(); one.Sites.Add(s);
        return ResearchSnapshot.Fnv1a(GeoSiteSnapshot.Encode(one));
    }

    [Fact]
    public void GeoSitePollSig_Stable_ForEqualSite()
        => Assert.Equal(SiteSig(new GeoSiteState(42, "PX", 10, 1, "KEY_BASE", "ENC")),
                        SiteSig(new GeoSiteState(42, "PX", 10, 1, "KEY_BASE", "ENC")));

    [Fact]
    public void GeoSitePollSig_Drifts_OnOwnerChange()
        => Assert.NotEqual(SiteSig(new GeoSiteState(42, "PX", 10, 1, "KEY_BASE", "ENC")),
                           SiteSig(new GeoSiteState(42, "AN", 10, 1, "KEY_BASE", "ENC")));

    [Fact]
    public void GeoSitePollSig_Drifts_OnNameChange_BeyondCrcOwnerStateSubset()
        // Same Owner + State (all the Inc5 CRC probe covers) but a different SiteName — the poll catches what
        // the CRC probe cannot, proving the full-DTO signature earns its cost.
        => Assert.NotEqual(SiteSig(new GeoSiteState(42, "PX", 10, 1, "KEY_BASE_A", "ENC")),
                           SiteSig(new GeoSiteState(42, "PX", 10, 1, "KEY_BASE_B", "ENC")));
}
