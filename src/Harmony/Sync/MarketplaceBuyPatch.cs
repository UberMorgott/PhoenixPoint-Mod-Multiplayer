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
    /// Relay interceptor for AB DLC5 Kaos "The Marketplace" purchases:
    /// <c>UIModuleTheMarketplace.OnChoiceSelected(GeoEventChoice)</c> (UIModuleTheMarketplace.cs:210) — the click
    /// handler that natively deducts the wallet, applies the reward (<c>CompleteMarketplaceEvent</c>) and removes
    /// the offer. On a co-op CLIENT the sim is frozen and the client is display-only (host-authoritative canon),
    /// so a local OnChoiceSelected would double-apply the reward + diverge. So: CLIENT → relay the buy intent
    /// (<see cref="MarketplaceBuyAction"/>, identified by kind+guid+price) + SKIP native entirely; the host applies
    /// the authoritative purchase and the converged state (wallet echo + Inventory/Research/GeoVehicle channels +
    /// the #7 marketplace-offer list) mirrors back — the offer disappears from the still-open window when
    /// <c>MarketplaceReflection.ApplyOffers</c> repaints it. HOST / single-player → untouched (native runs; its
    /// offer-list change propagates via the #7 drift poll, wallet/reward via their own channels).
    ///
    /// Game types are NEVER hard-referenced: the target resolves via AccessTools; Prepare() returns false (Harmony
    /// skips the patch) when the type/method is absent (no DLC5 build). Mirrors <c>ExploreSitePatch</c>.
    /// </summary>
    [HarmonyPatch]
    public static class MarketplaceBuyPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var moduleT = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleTheMarketplace");
            var choiceT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.GeoEventChoice");
            if (moduleT == null || choiceT == null) return false;
            _target = AccessTools.Method(moduleT, "OnChoiceSelected", new[] { choiceT });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __instance = UIModuleTheMarketplace; choice = the clicked GeoEventChoice. Return false = skip native buy.
        public static bool Prefix(object choice)
        {
            if (SyncApplyScope.IsApplying) return true;   // engine-driven apply → run native
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return true;   // single-player / no session → native
            if (engine.IsHost) return true;               // host authoritative → native buy; result mirrors on channels

            // CLIENT (display-only). Never simulate the buy locally, whatever happens below → always skip native.
            try
            {
                if (!PermissionGate.Check(ActionCategory.GeoAbility))
                {
                    PermissionGate.Notify(ActionCategory.GeoAbility);
                    return false;
                }
                if (MarketplaceReflection.TryReadOffer(GeoRuntime.Instance, choice, out var kind, out var guid, out var price))
                    engine.Sync.SendActionRequest(new MarketplaceBuyAction(kind, guid, price));
                else
                    Debug.LogWarning("[Multiplayer] MarketplaceBuyPatch: could not read clicked offer → buy not relayed (no local sim)");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] MarketplaceBuyPatch failed: " + ex.Message); }
            return false;   // client: block the local (frozen) buy; host executes + result mirrors back
        }
    }
}
