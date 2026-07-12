using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.Actions;
using Multiplayer.Network.Sync.State;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// Relay interceptor for HAVEN resource trades: <c>GeoHaven.TradeResource(GeoFaction, HavenTradingEntry, int)</c>
    /// (GeoHaven.cs:715) — the MODEL chokepoint both trade UI paths funnel through (<c>UIStateTrade.ConfirmTrade</c>
    /// AND <c>HavenInteractionController</c>), so intercepting here catches every caller (root-cause, not per-UI).
    /// It natively mutates the haven's <c>StockedResources</c> + applies the mirror pack to <c>faction.Wallet</c>.
    /// On a co-op CLIENT the sim is frozen and the client is display-only (host-authoritative canon), so a local
    /// TradeResource desyncs (host unaware → wallet echo 0xA0 rolls it back). So: CLIENT → relay the trade intent
    /// (<see cref="HavenTradeAction"/>, keyed by siteId + resource pair + amount) + SKIP native; the host applies
    /// the authoritative trade and the converged state (wallet echo + ch#5 haven stock tail) mirrors back.
    /// HOST / single-player → untouched (native runs); the postfix marks the haven's ch#5 site dirty so the host's
    /// OWN trades (and its applied client intents) flush the fresh stock to clients instantly (canon: known paths
    /// converge instantly; the ch#5 poll is only the backstop).
    ///
    /// Game types are NEVER hard-referenced: the target resolves via AccessTools; Prepare() returns false (Harmony
    /// skips the patch) when the type/method is absent. Mirrors <c>MarketplaceBuyPatch</c>.
    /// </summary>
    [HarmonyPatch]
    public static class HavenTradePatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var havenT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoHaven");
            var factionT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoFaction");
            var entryT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Sites.HavenTradingEntry");
            if (havenT == null || factionT == null || entryT == null) return false;
            _target = AccessTools.Method(havenT, "TradeResource", new[] { factionT, entryT, typeof(int) });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __instance = GeoHaven; offer = the boxed HavenTradingEntry; offerAmount = NumberOrTrades. Return false = skip native.
        public static bool Prefix(object __instance, object offer, int offerAmount)
        {
            if (SyncApplyScope.IsApplying) return true;   // engine-driven apply → run native (host authoritative execute)
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return true;   // single-player / no session → native
            if (engine.IsHost) return true;               // host authoritative → native trade; result mirrors on channels

            // CLIENT (display-only). Never simulate the trade locally, whatever happens below → always skip native.
            try
            {
                if (!PermissionGate.Check(ActionCategory.GeoAbility))
                {
                    PermissionGate.Notify(ActionCategory.GeoAbility);
                    return false;
                }
                int siteId = HavenTradeReflection.GetHavenSiteId(__instance);
                if (siteId >= 0 && HavenTradeReflection.TryReadOfferResources(offer, out int offerRes, out int wantRes))
                    engine.Sync.SendActionRequest(new HavenTradeAction(new HavenTradeIntent(siteId, offerRes, wantRes, offerAmount)));
                else
                    Debug.LogWarning("[Multiplayer] HavenTradePatch: could not read trade offer → trade not relayed (no local sim)");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] HavenTradePatch failed: " + ex.Message); }
            return false;   // client: block the local (frozen) trade; host executes + result mirrors back
        }

        // Runs after a native TradeResource on the HOST (its own trade, or an applied client intent) — mark the
        // haven's ch#5 site dirty so the fresh StockedResources flush to clients immediately. No-op on the client
        // (its native was skipped by the prefix; the postfix still fires, so guard on IsHost) and in solo.
        public static void Postfix(object __instance)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
                int siteId = HavenTradeReflection.GetHavenSiteId(__instance);
                if (siteId >= 0) GeoSiteChannel.MarkSiteDirtyExternal(siteId);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] HavenTradePatch.Postfix failed: " + ex.Message); }
        }
    }
}
