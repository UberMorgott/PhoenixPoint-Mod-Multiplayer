using System.IO;
using Multiplayer.Network.Sync.Actions;
using Xunit;

/// <summary>
/// Haven resource-trade intent (audit gap 1, 2026-07-12): the pure wire payload the client relays to the host
/// (which haven / which resource pair / how many trades) + the host-side affordability gate. Covers the
/// encode/decode round-trip and the reject-on-insufficient boundary (the host re-validates against ITS stock/wallet).
/// </summary>
public class HavenTradeIntentTests
{
    private static HavenTradeIntent RoundTrip(HavenTradeIntent i)
    {
        using (var ms = new MemoryStream())
        {
            i.Write(new BinaryWriter(ms));
            ms.Position = 0;
            return HavenTradeIntent.Read(new BinaryReader(ms));
        }
    }

    [Theory]
    [InlineData(7, 2, 1, 1)]                 // site 7, Materials→Supplies, x1
    [InlineData(0, 1, 4, 25)]               // Supplies→Tech, x25
    [InlineData(1234, 0x400, 0x800, 3)]    // Orichalcum→ProteanMutane (high [Flags] values need i32, not byte)
    [InlineData(int.MaxValue, 4, 2, int.MaxValue)]
    public void Intent_RoundTrips(int siteId, int offerRes, int wantRes, int amount)
    {
        var intent = new HavenTradeIntent(siteId, offerRes, wantRes, amount);
        var rt = RoundTrip(intent);

        Assert.Equal(intent, rt);
        Assert.Equal(siteId, rt.SiteId);
        Assert.Equal(offerRes, rt.OfferResource);
        Assert.Equal(wantRes, rt.WantResource);
        Assert.Equal(amount, rt.OfferAmount);
    }

    [Fact]
    public void Intent_ParticipatesInEquality()
    {
        var baseline = new HavenTradeIntent(7, 2, 1, 1);
        Assert.NotEqual(baseline, new HavenTradeIntent(8, 2, 1, 1));
        Assert.NotEqual(baseline, new HavenTradeIntent(7, 4, 1, 1));
        Assert.NotEqual(baseline, new HavenTradeIntent(7, 2, 4, 1));
        Assert.NotEqual(baseline, new HavenTradeIntent(7, 2, 1, 2));
        Assert.Equal(baseline, new HavenTradeIntent(7, 2, 1, 1));
    }

    [Theory]
    // havenStock, offerTotal, factionFunds, receiveTotal → expected
    [InlineData(100, 100, 50, 50, true)]     // exact-enough on both sides passes
    [InlineData(100, 40, 200, 120, true)]    // plenty
    [InlineData(30, 40, 200, 120, false)]    // haven short (stale client thought haven had more) → reject
    [InlineData(100, 40, 100, 120, false)]   // faction short (host wallet lower than client thought) → reject
    [InlineData(0, 1, 100, 1, false)]        // empty shelf → reject
    public void CanExecute_GatesOnBothSides(int stock, int offerTotal, int funds, int recvTotal, bool expected)
    {
        Assert.Equal(expected, HavenTradeIntent.CanExecute(stock, offerTotal, funds, recvTotal));
    }
}
