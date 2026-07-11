using System.IO;
using System.Text;
using Multiplayer.Network.Sync.Actions;
using Multiplayer.Network.Sync.State;
using Xunit;

// The buy intent identifies the offer by VALUE (kind + guid + price), not index, so the host can match it
// against a list that may have shifted. Pin the Write→Read round-trip of that payload.
public class MarketplaceBuyActionCodecTests
{
    private static MarketplaceBuyAction RoundTrip(MarketplaceBuyAction a)
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
            return Assert.IsType<MarketplaceBuyAction>(MarketplaceBuyAction.Read(r));
    }

    [Fact]
    public void RoundTrips_AllFields()
    {
        var back = RoundTrip(new MarketplaceBuyAction(ObjectivesSnapshot.OfferUnit, "abc-123-guid", 750f));
        Assert.Equal(ObjectivesSnapshot.OfferUnit, back.Kind);
        Assert.Equal("abc-123-guid", back.Guid);
        Assert.Equal(750f, back.Price);
    }

    [Fact]
    public void NullGuid_EncodesEmpty()
    {
        var back = RoundTrip(new MarketplaceBuyAction(ObjectivesSnapshot.OfferItem, null, 0f));
        Assert.Equal(ObjectivesSnapshot.OfferItem, back.Kind);
        Assert.Equal("", back.Guid);
        Assert.Equal(0f, back.Price);
    }

    [Fact]
    public void ActionId_IsMarketplaceBuy()
        => Assert.Equal(Multiplayer.Network.Sync.SyncedActionIds.MarketplaceBuy,
                        new MarketplaceBuyAction(ObjectivesSnapshot.OfferResearch, "r", 1f).ActionId);
}
