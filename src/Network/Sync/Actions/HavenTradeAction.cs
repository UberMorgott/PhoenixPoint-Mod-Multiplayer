using System;
using System.IO;
using Multiplayer.Network.Sync.State;

namespace Multiplayer.Network.Sync.Actions
{
    /// <summary>
    /// Client-edit intent: TRADE one resource pair at a haven N times (<c>GeoHaven.TradeResource</c>, reached
    /// from <c>UIStateTrade.ConfirmTrade</c> / <c>HavenInteractionController</c>). On a co-op CLIENT the sim is
    /// frozen and the client is display-only, so a local TradeResource mutates the client's haven stock + wallet
    /// with the host unaware — the wallet echo (0xA0) then rolls it back within ~1 in-game hour (flicker + temp
    /// desync). So: CLIENT relays this intent + suppresses the local trade; the HOST re-derives the ratio from its
    /// own faction def, validates affordability on HOST stock/wallet, and executes <c>TradeResource</c>
    /// authoritatively (<see cref="HavenTradeReflection.TryTrade"/>). The authoritative result mirrors back on the
    /// wallet echo + the ch#5 haven stock tail; the client never simulates. Category GeoAbility (geoscape economy,
    /// like <see cref="MarketplaceBuyAction"/>). Runs INSIDE SyncApplyScope — the native TradeResource its Apply
    /// drives is seen by <c>HavenTradePatch</c> as engine-driven (IsApplying) so it runs, no interceptor loop.
    /// </summary>
    public sealed class HavenTradeAction : ISyncedAction, IHostOnlyApply
    {
        private readonly HavenTradeIntent _intent;

        public HavenTradeAction(HavenTradeIntent intent) { _intent = intent; }

        public HavenTradeIntent Intent => _intent;

        public ushort ActionId => SyncedActionIds.HavenTrade;
        public ActionCategory Category => ActionCategory.GeoAbility;

        public void Write(BinaryWriter w) => _intent.Write(w);

        public static ISyncedAction Read(BinaryReader r) => new HavenTradeAction(HavenTradeIntent.Read(r));

        public bool Validate(GeoRuntime rt, Guid actor)
            => rt != null && rt.IsGeoscapeActive && _intent.OfferAmount > 0 && _intent.SiteId >= 0;

        public void Apply(GeoRuntime rt)
        {
            if (!HavenTradeReflection.TryTrade(rt, _intent)) return;   // stale/invalid intent → logged no-op
            // Trade applied on the host. This runs INSIDE SyncApplyScope; the native TradeResource fires
            // ResourcesChanged for the wallet echo, but force the wallet mark in case of a stale binding
            // (MarketplaceBuyAction idiom). The ch#5 haven STOCK change is marked by HavenTradePatch's postfix
            // (which fires on the host for the TradeResource TryTrade just invoked).
            NetworkEngine.Instance?.Sync?.MarkWalletDirty();
        }
    }
}
