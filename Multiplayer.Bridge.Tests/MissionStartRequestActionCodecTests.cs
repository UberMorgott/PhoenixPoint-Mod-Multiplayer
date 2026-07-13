using System.IO;
using System.Text;
using Multiplayer.Network.Sync.Actions;
using Xunit;

// The client "begin mission" relay identifies the pending brief by native ModalType + the mission site's
// stable GeoSite.SiteId (the identity the 0x69 report mirror / tac.deploy already ship). Pin the
// Write→Read round-trip of that payload + the wire id/category contract.
public class MissionStartRequestActionCodecTests
{
    private static MissionStartRequestAction RoundTrip(MissionStartRequestAction a)
    {
        byte[] bytes;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            a.Write(w);
            w.Flush();
            bytes = ms.ToArray();
        }
        using (var ms = new MemoryStream(bytes))
        using (var r = new BinaryReader(ms, Encoding.UTF8))
            return Assert.IsType<MissionStartRequestAction>(MissionStartRequestAction.Read(r));
    }

    [Fact]
    public void RoundTrips_AllFields()
    {
        var back = RoundTrip(new MissionStartRequestAction(15, 116));   // GeoAmbushBrief @ site 116
        Assert.Equal(15, back.ModalType);
        Assert.Equal(116, back.SiteId);
    }

    [Fact]
    public void UnreadableSite_RoundTripsMinusOne()
    {
        // Client could not read the mirror's mission site → -1 (host then matches on modalType alone).
        var back = RoundTrip(new MissionStartRequestAction(4, -1));     // GeoScavengeBrief, degraded site read
        Assert.Equal(4, back.ModalType);
        Assert.Equal(-1, back.SiteId);
    }

    [Fact]
    public void SquadTail_RoundTrips()
    {
        // Client-side squad pick: the picked GeoUnitIds ride a tail after modalType+siteId.
        var back = RoundTrip(new MissionStartRequestAction(11, 42, new long[] { 7, 300, 12345 }));
        Assert.Equal(11, back.ModalType);
        Assert.Equal(42, back.SiteId);
        Assert.Equal(new long[] { 7, 300, 12345 }, back.UnitIds);
    }

    [Fact]
    public void TwoArgCtor_HasEmptySquad()
        => Assert.Empty(new MissionStartRequestAction(15, 116).UnitIds);

    [Fact]
    public void LegacyPayload_NoTail_ReadsEmptySquad()
    {
        // An OLD writer stops after siteId — the tolerant Read must yield an empty squad (host then
        // falls back to its native deployment window), not throw on the missing tail.
        byte[] bytes;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write((byte)15);
            w.Write(116);
            w.Flush();
            bytes = ms.ToArray();
        }
        using (var ms = new MemoryStream(bytes))
        using (var r = new BinaryReader(ms, Encoding.UTF8))
        {
            var back = Assert.IsType<MissionStartRequestAction>(MissionStartRequestAction.Read(r));
            Assert.Equal(15, back.ModalType);
            Assert.Equal(116, back.SiteId);
            Assert.Empty(back.UnitIds);
        }
    }

    [Fact]
    public void InsaneSquadCount_ReadsEmptySquad()
    {
        // Corrupt/hostile count above the sanity cap reads as "no squad" (host window fallback), never
        // a giant allocation.
        byte[] bytes;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write((byte)15);
            w.Write(116);
            w.Write(9999);   // count far above any native MaxPlayerUnits
            w.Flush();
            bytes = ms.ToArray();
        }
        using (var ms = new MemoryStream(bytes))
        using (var r = new BinaryReader(ms, Encoding.UTF8))
            Assert.Empty(Assert.IsType<MissionStartRequestAction>(MissionStartRequestAction.Read(r)).UnitIds);
    }

    [Fact]
    public void ActionId_IsMissionStartRequest()
        => Assert.Equal(Multiplayer.Network.Sync.SyncedActionIds.MissionStartRequest,
                        new MissionStartRequestAction(11, 7).ActionId);

    [Fact]
    public void Category_IsDialogs_UngatedLikeEventAnswers()
        => Assert.Equal(Multiplayer.Network.Sync.ActionCategory.Dialogs,
                        new MissionStartRequestAction(11, 7).Category);
}
