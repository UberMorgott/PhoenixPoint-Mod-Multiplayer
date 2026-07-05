using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.State;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// Host-authoritative RESOURCE-HARVEST FLOAT mirror (Batch-2 P6 of the 2026-07-05 unified popup-mirror
    /// spec). ONE chokepoint: <c>GeoSite.ShowResourceHarvested(ResourcePack)</c> (GeoSite.cs:931) — the native
    /// harvest rail funnels there (<c>GeoscapeView.PxFaction_OnResourcesHarvested</c>, GeoscapeView.cs:1956-1959)
    /// and it renders exactly one tuple: the FIRST ResourceUnit's Type + RoundedValue. Mirrors the
    /// <see cref="ReportModalMirror"/> pattern exactly:
    ///   • HOST Postfix → read {siteId, firstResourceType, firstValue} off the live call and broadcast
    ///     (<c>SyncEngine.BroadcastHarvestFloat</c>, 0x67 envelope surface 0xA8, host-monotonic occId);
    ///     pure observe — the host's own float is untouched.
    ///   • CLIENT Prefix → suppress a LOCAL (non-engine) call so a not-fully-frozen client sim can never
    ///     double-float; the engine replay runs under <c>SyncApplyScope</c> and passes.
    /// DISPLAY-ONLY contract (spec §4 one-writer): this rail never credits resources — the balance rides the
    /// silent wallet 0xA0 snapshot; this only makes the gain VISIBLE at the harvesting site on the client.
    /// Gated on <see cref="ReportMirrorGate"/> (the popup-mirror program's master switch — Batch-1 precedent:
    /// whitelist growth rides the same gate; OFF → byte-for-byte native both sides). Best-effort try/catch;
    /// on any failure native runs (fail-open). Reflective target (Prepare false → skipped on rename).
    /// </summary>
    [HarmonyPatch]
    public static class HarvestFloatMirrorPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var siteT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
            var packT = AccessTools.TypeByName("PhoenixPoint.Common.Core.ResourcePack");
            if (siteT == null || packT == null) return false;
            // EXACT param match (harmony-accesstools-exact-param-match): (ResourcePack).
            _target = AccessTools.Method(siteT, "ShowResourceHarvested", new[] { packT });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // CLIENT: suppress the local float (the mirror replays the host's; engine replay passes via IsApplying).
        public static bool Prefix()
        {
            try
            {
                if (!ReportMirrorGate.Enabled) return true;
                if (SyncApplyScope.IsApplying) return true;   // engine-driven replay → never block
                var engine = NetworkEngine.Instance;
                if (engine != null && engine.IsActiveSession && !engine.IsHost) return false;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] HarvestFloatMirrorPatch.Prefix failed: " + ex.Message); }
            return true;                                       // host (and any failure / gate-off): native runs
        }

        // HOST: broadcast the float tuple AFTER the native render (pure observe). __0 = the ResourcePack.
        public static void Postfix(object __instance, object __0)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
                if (!ReportMirrorGate.Enabled) return;
                if (SyncApplyScope.IsApplying) return;        // never re-broadcast an engine replay
                int siteId = GeoSiteReflection.GetSiteId(__instance);
                if (siteId < 0) return;
                if (!GeoSiteReflection.ReadFirstResource(__0, out var resourceType, out var value)) return;
                engine.Sync?.BroadcastHarvestFloat(siteId, resourceType, value);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] HarvestFloatMirrorPatch.Postfix failed: " + ex.Message); }
        }
    }
}
