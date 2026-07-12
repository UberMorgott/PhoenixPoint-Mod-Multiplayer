using System;
using System.IO;

namespace Multiplayer.Network.Sync.Actions
{
    /// <summary>
    /// PURE wire payload of a haven resource-trade intent (audit gap 1, 2026-07-12): the client's chosen trade
    /// at a haven, relayed to the host which executes <c>GeoHaven.TradeResource</c> authoritatively. Carries only
    /// the SEMANTIC choice — WHICH haven, WHICH resource pair, HOW MANY times — never the per-trade quantities:
    /// those come from the owner faction's immutable <c>ResourceTradingRatios</c> def data, which the HOST
    /// re-derives from its own live haven (so a stale/spoofed client can neither pick a wrong rate nor over-draw
    /// the host's stock/wallet). Resource types carry the RAW <c>ResourceType</c> enum value (a [Flags] enum up
    /// to 0x800 — needs i32, not a byte). Pure + BCL-only so the encode/decode round-trip is directly unit-testable
    /// (mirrors <see cref="PersonnelActionWire"/>); the Unity-side <c>HavenTradeAction</c> delegates its wire here.
    /// </summary>
    public readonly struct HavenTradeIntent : IEquatable<HavenTradeIntent>
    {
        public readonly int SiteId;         // GeoHaven.Site.SiteId (host resolves the live haven by this)
        public readonly int OfferResource;  // HavenTradingEntry.HavenOffers raw ResourceType (haven gives → faction receives)
        public readonly int WantResource;   // HavenTradingEntry.HavenWants  raw ResourceType (faction pays → haven receives)
        public readonly int OfferAmount;    // UIModuleTrade.NumberOrTrades (how many ratio-trades)

        public HavenTradeIntent(int siteId, int offerResource, int wantResource, int offerAmount)
        {
            SiteId = siteId;
            OfferResource = offerResource;
            WantResource = wantResource;
            OfferAmount = offerAmount;
        }

        public void Write(BinaryWriter w)
        {
            w.Write(SiteId);
            w.Write(OfferResource);
            w.Write(WantResource);
            w.Write(OfferAmount);
        }

        public static HavenTradeIntent Read(BinaryReader r)
            => new HavenTradeIntent(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());

        /// <summary>Host-side affordability gate (both sides read as rounded ints — the same values the trade UI
        /// shows): the haven must hold at least what it gives, and the faction must hold at least what it pays.
        /// Pure so the reject-on-insufficient boundary is unit-testable without a live game.</summary>
        public static bool CanExecute(int havenStock, int offerTotal, int factionFunds, int receiveTotal)
            => havenStock >= offerTotal && factionFunds >= receiveTotal;

        public bool Equals(HavenTradeIntent other)
            => SiteId == other.SiteId && OfferResource == other.OfferResource
               && WantResource == other.WantResource && OfferAmount == other.OfferAmount;

        public override bool Equals(object obj) => obj is HavenTradeIntent o && Equals(o);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = SiteId;
                h = (h * 397) ^ OfferResource;
                h = (h * 397) ^ WantResource;
                h = (h * 397) ^ OfferAmount;
                return h;
            }
        }

        public override string ToString()
            => $"HavenTrade(site={SiteId} offer={OfferResource} want={WantResource} x{OfferAmount})";
    }
}
