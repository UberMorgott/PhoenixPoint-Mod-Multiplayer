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
    public void ActionId_IsMissionStartRequest()
        => Assert.Equal(Multiplayer.Network.Sync.SyncedActionIds.MissionStartRequest,
                        new MissionStartRequestAction(11, 7).ActionId);

    [Fact]
    public void Category_IsDialogs_UngatedLikeEventAnswers()
        => Assert.Equal(Multiplayer.Network.Sync.ActionCategory.Dialogs,
                        new MissionStartRequestAction(11, 7).Category);
}
