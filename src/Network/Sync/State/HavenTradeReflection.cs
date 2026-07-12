using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Reflection bridge for host-authoritative HAVEN resource TRADE (audit gap 1, 2026-07-12). The mod holds NO
    /// compile-time game reference, so every member resolves by name and caches. Mirrors <see cref="MarketplaceReflection"/>:
    /// the CLIENT relays a <see cref="Actions.HavenTradeIntent"/> (which haven / which resource pair / how many)
    /// and the HOST executes the trade authoritatively here.
    ///
    /// Verified against the decompile (2026-07-12):
    ///   • <c>GeoHaven.TradeResource(GeoFaction, HavenTradingEntry, int)</c> (GeoHaven.cs:715) — subtracts the
    ///     offered resource from <c>StockedResources</c>, adds the wanted one, and applies the mirror pack to
    ///     <c>faction.Wallet</c>. It does NOT validate affordability itself; the trade UI's +/- buttons cap the
    ///     amount client-side, so a co-op client computing off STALE stock could over-draw the host.
    ///   • <c>HavenTradingEntry</c> (struct, HavenTradingEntry.cs) = {HavenOffers, HavenWants (ResourceType),
    ///     HavenOfferQuantity, HavenReceiveQuantity, ResourceStock (int)}. The per-trade quantities come from the
    ///     owner faction's <c>GeoFactionDef.ResourceTradingRatios</c> (GeoFactionDef.cs:239, immutable def data),
    ///     so the host RE-DERIVES them from its own live haven — a stale/spoofed client can neither pick a wrong
    ///     rate nor bypass the host affordability gate.
    ///
    /// Host apply (<see cref="TryTrade"/>): resolve the live haven by SiteId → re-derive the ratio for the chosen
    /// pair (missing ratio = logged no-op) → gate affordability on HOST stock + HOST wallet
    /// (<see cref="Actions.HavenTradeIntent.CanExecute"/>; stale intent = logged no-op) → call the native
    /// <c>TradeResource</c>. The authoritative result mirrors back on the wallet echo (0xA0) + the ch#5 haven
    /// stock tail; the client never simulates. Every path try/caught — best-effort, never throws.
    /// </summary>
    public static class HavenTradeReflection
    {
        private static bool _probed;
        private static Type _geoHavenType;             // PhoenixPoint.Geoscape.Entities.GeoHaven
        private static Type _resourceTypeEnum;         // PhoenixPoint.Common.Core.ResourceType
        private static PropertyInfo _havenSiteProp;    // GeoHaven.Site
        private static FieldInfo _siteIdField;         // GeoSite.SiteId (int)
        private static PropertyInfo _siteOwnerProp;    // GeoSite.Owner (GeoFaction)
        private static PropertyInfo _factionDefProp;   // GeoFaction.Def (GeoFactionDef)
        private static PropertyInfo _factionWalletProp;// GeoFaction.Wallet
        private static FieldInfo _tradingRatiosField;  // GeoFactionDef.ResourceTradingRatios (List<TradingRatio>)
        private static FieldInfo _ratioOfferResField;  // TradingRatio.OfferResource (ResourceType)
        private static FieldInfo _ratioRecvResField;   // TradingRatio.RecieveResource (ResourceType)
        private static FieldInfo _ratioOfferQtyField;  // TradingRatio.OfferQuantity (int)
        private static FieldInfo _ratioRecvQtyField;   // TradingRatio.RecieveQuantity (int)
        private static FieldInfo _stockedField;        // GeoHaven.StockedResources (ResourcePack)
        private static MethodInfo _packByResourceType; // ResourcePack.ByResourceType(ResourceType) → ResourceUnit
        private static PropertyInfo _unitRoundedValueProp; // ResourceUnit.RoundedValue (int, = CeilToInt(Value))
        private static FieldInfo _unitValueField;      // ResourceUnit.Value (raw float) — floored for the funds gate
        private static MethodInfo _walletGetItem;      // Wallet.get_Item(ResourceType) → ResourceUnit (indexer)
        private static Type _entryType;                // HavenTradingEntry
        private static FieldInfo _entHavenOffers, _entHavenWants, _entOfferQty, _entRecvQty, _entResStock;
        private static MethodInfo _tradeResourceMethod;// GeoHaven.TradeResource(GeoFaction, HavenTradingEntry, int)

        private static void Ensure()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                _geoHavenType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoHaven");
                _resourceTypeEnum = AccessTools.TypeByName("PhoenixPoint.Common.Core.ResourceType");
                var geoSiteType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
                var geoFactionType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoFaction");
                var factionDefType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoFactionDef");
                var ratioType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Sites.TradingRatio");
                var packType = AccessTools.TypeByName("PhoenixPoint.Common.Core.ResourcePack");
                var unitType = AccessTools.TypeByName("PhoenixPoint.Common.Core.ResourceUnit");
                var walletType = AccessTools.TypeByName("PhoenixPoint.Common.Core.Wallet");
                _entryType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Sites.HavenTradingEntry");
                if (_geoHavenType == null || _resourceTypeEnum == null || _entryType == null) return;

                if (geoSiteType != null) _siteIdField = AccessTools.Field(geoSiteType, "SiteId");
                _havenSiteProp = AccessTools.Property(_geoHavenType, "Site");
                if (geoSiteType != null) _siteOwnerProp = AccessTools.Property(geoSiteType, "Owner");
                if (geoFactionType != null)
                {
                    _factionDefProp = AccessTools.Property(geoFactionType, "Def");
                    _factionWalletProp = AccessTools.Property(geoFactionType, "Wallet");
                }
                if (factionDefType != null) _tradingRatiosField = AccessTools.Field(factionDefType, "ResourceTradingRatios");
                if (ratioType != null)
                {
                    _ratioOfferResField = AccessTools.Field(ratioType, "OfferResource");
                    _ratioRecvResField = AccessTools.Field(ratioType, "RecieveResource");   // (game's spelling)
                    _ratioOfferQtyField = AccessTools.Field(ratioType, "OfferQuantity");
                    _ratioRecvQtyField = AccessTools.Field(ratioType, "RecieveQuantity");
                }
                _stockedField = AccessTools.Field(_geoHavenType, "StockedResources");
                if (packType != null) _packByResourceType = AccessTools.Method(packType, "ByResourceType", new[] { _resourceTypeEnum });
                if (unitType != null)
                {
                    _unitRoundedValueProp = AccessTools.Property(unitType, "RoundedValue");
                    _unitValueField = AccessTools.Field(unitType, "Value");   // raw float (floored for the funds gate)
                }
                if (walletType != null) _walletGetItem = AccessTools.Method(walletType, "get_Item", new[] { _resourceTypeEnum });

                _entHavenOffers = AccessTools.Field(_entryType, "HavenOffers");
                _entHavenWants = AccessTools.Field(_entryType, "HavenWants");
                _entOfferQty = AccessTools.Field(_entryType, "HavenOfferQuantity");
                _entRecvQty = AccessTools.Field(_entryType, "HavenReceiveQuantity");
                _entResStock = AccessTools.Field(_entryType, "ResourceStock");

                if (geoFactionType != null)
                    _tradeResourceMethod = AccessTools.Method(_geoHavenType, "TradeResource",
                        new[] { geoFactionType, _entryType, typeof(int) });
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] HavenTradeReflection.Ensure failed (haven trade sync disabled): " + ex.Message); }
        }

        /// <summary>The <c>GeoSite.SiteId</c> of a live <c>GeoHaven</c>, or -1 (unbound / not a haven).</summary>
        public static int GetHavenSiteId(object haven)
        {
            try
            {
                Ensure();
                if (haven == null || _havenSiteProp == null || _siteIdField == null) return -1;
                var site = _havenSiteProp.GetValue(haven, null);
                if (site == null) return -1;
                return _siteIdField.GetValue(site) is int i ? i : -1;
            }
            catch { return -1; }
        }

        /// <summary>CLIENT: read the clicked trade offer's resource pair (raw <c>ResourceType</c> ints) off the
        /// boxed <c>HavenTradingEntry</c>. false = unreadable (→ the buy is not relayed, never simulated).</summary>
        public static bool TryReadOfferResources(object offer, out int offerRes, out int wantRes)
        {
            offerRes = 0; wantRes = 0;
            try
            {
                Ensure();
                if (offer == null || _entHavenOffers == null || _entHavenWants == null) return false;
                offerRes = Convert.ToInt32(_entHavenOffers.GetValue(offer));
                wantRes = Convert.ToInt32(_entHavenWants.GetValue(offer));
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// HOST: execute the intent authoritatively. Resolve the live haven → re-derive the ratio for the pair →
        /// gate affordability on HOST stock + HOST wallet → native <c>TradeResource</c>. Returns true only when
        /// the trade actually applied (a missing ratio / stale intent / unbound reflection is a logged no-op).
        /// </summary>
        public static bool TryTrade(GeoRuntime rt, Actions.HavenTradeIntent intent)
        {
            try
            {
                Ensure();
                if (_tradeResourceMethod == null || _entryType == null || _resourceTypeEnum == null) return false;
                if (intent.OfferAmount <= 0) return false;

                var site = GeoSiteReflection.ResolveSiteById(rt, intent.SiteId);
                if (site == null || !(site is Component c)) return false;
                var haven = c.GetComponent(_geoHavenType);
                if (haven == null) { Debug.Log("[Multiplayer] HavenTradeReflection.TryTrade: site " + intent.SiteId + " is not a haven"); return false; }

                var faction = rt?.PhoenixFaction();
                if (faction == null || _factionWalletProp == null) return false;
                var wallet = _factionWalletProp.GetValue(faction, null);
                if (wallet == null) return false;

                object offerResEnum = Enum.ToObject(_resourceTypeEnum, intent.OfferResource);
                object wantResEnum = Enum.ToObject(_resourceTypeEnum, intent.WantResource);

                // Re-derive the authoritative ratio from the host's own faction def (immutable data) — this is
                // what makes the RATE host-authoritative (a stale/spoofed client can't pick a cheaper one).
                if (!TryFindRatio(site, intent.OfferResource, intent.WantResource, out int offerQty, out int recvQty))
                {
                    Debug.Log("[Multiplayer] HavenTradeReflection.TryTrade: no ratio " + intent.OfferResource
                              + "→" + intent.WantResource + " at site " + intent.SiteId + " (stale/invalid intent, no-op)");
                    return false;
                }

                int offerTotal = offerQty * intent.OfferAmount;   // haven gives / faction receives
                int recvTotal = recvQty * intent.OfferAmount;     // haven receives / faction pays
                int havenStock = RoundedStock(haven, offerResEnum);
                int factionFunds = FloorWallet(wallet, wantResEnum);
                if (!Actions.HavenTradeIntent.CanExecute(havenStock, offerTotal, factionFunds, recvTotal))
                {
                    Debug.Log("[Multiplayer] HavenTradeReflection.TryTrade rejected — insufficient (havenStock="
                              + havenStock + " need=" + offerTotal + " funds=" + factionFunds + " cost=" + recvTotal + ")");
                    return false;
                }

                // Build the authoritative HavenTradingEntry (boxed struct — set fields on the box) + trade.
                object entry = Activator.CreateInstance(_entryType);
                _entHavenOffers?.SetValue(entry, offerResEnum);
                _entHavenWants?.SetValue(entry, wantResEnum);
                _entOfferQty?.SetValue(entry, offerQty);
                _entRecvQty?.SetValue(entry, recvQty);
                _entResStock?.SetValue(entry, havenStock);
                _tradeResourceMethod.Invoke(haven, new object[] { faction, entry, intent.OfferAmount });
                Debug.Log("[Multiplayer] HavenTradeReflection.TryTrade applied (site=" + intent.SiteId
                          + " " + intent.OfferResource + "→" + intent.WantResource + " x" + intent.OfferAmount + ")");
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] HavenTradeReflection.TryTrade failed: " + ex.Message); return false; }
        }

        private static bool TryFindRatio(object site, int offerRes, int wantRes, out int offerQty, out int recvQty)
        {
            offerQty = 0; recvQty = 0;
            try
            {
                if (_siteOwnerProp == null || _factionDefProp == null || _tradingRatiosField == null) return false;
                var owner = _siteOwnerProp.GetValue(site, null);
                var def = owner != null ? _factionDefProp.GetValue(owner, null) : null;
                if (def == null || !(_tradingRatiosField.GetValue(def) is IEnumerable ratios)) return false;
                foreach (var r in ratios)
                {
                    if (r == null) continue;
                    if (Convert.ToInt32(_ratioOfferResField.GetValue(r)) != offerRes) continue;
                    if (Convert.ToInt32(_ratioRecvResField.GetValue(r)) != wantRes) continue;
                    offerQty = Convert.ToInt32(_ratioOfferQtyField.GetValue(r));
                    recvQty = Convert.ToInt32(_ratioRecvQtyField.GetValue(r));
                    return true;
                }
                return false;
            }
            catch { return false; }
        }

        private static int RoundedStock(object haven, object resEnum)
        {
            try
            {
                var pack = _stockedField?.GetValue(haven);
                if (pack == null || _packByResourceType == null || _unitRoundedValueProp == null) return 0;
                var unit = _packByResourceType.Invoke(pack, new[] { resEnum });
                return Convert.ToInt32(_unitRoundedValueProp.GetValue(unit, null));
            }
            catch { return 0; }
        }

        /// <summary>FLOOR of the faction's wallet balance for a resource — NOT <c>RoundedValue</c> (= CeilToInt).
        /// The affordability gate must never OVER-count funds: a sub-unit balance (e.g. 4.3) ceils to 5 and would
        /// pass a cost-5 trade the wallet can't actually cover (&lt;1 overdraw at the boundary). Floor is
        /// conservative and still ≥ native (which has NO gate). Reads the raw <c>ResourceUnit.Value</c> float;
        /// degrades to RoundedValue only if that field can't be resolved (never worse than the pre-fix behavior).</summary>
        private static int FloorWallet(object wallet, object resEnum)
        {
            try
            {
                if (_walletGetItem == null) return 0;
                var unit = _walletGetItem.Invoke(wallet, new[] { resEnum });
                if (_unitValueField != null)
                    return (int)Math.Floor(Convert.ToDouble(_unitValueField.GetValue(unit)));
                if (_unitRoundedValueProp != null)   // reflection degrade — no worse than the pre-fix ceil read
                    return Convert.ToInt32(_unitRoundedValueProp.GetValue(unit, null));
                return 0;
            }
            catch { return 0; }
        }
    }
}
