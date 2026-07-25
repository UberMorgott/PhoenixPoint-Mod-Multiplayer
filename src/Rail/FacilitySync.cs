using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Base.Core;
using Base.Defs;
using HarmonyLib;
using Multiplayer.Network.MessageLayer;
using PhoenixPoint.Common.Entities;
using PhoenixPoint.Geoscape.Entities.PhoenixBases;
using PhoenixPoint.Geoscape.Entities.Sites;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View.ViewControllers.PhoenixBase;
using PhoenixPoint.Geoscape.View.ViewModules;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Base-construction intent family (surface 0xB1, law 1): build / demolish-scrap / repair ride the
    /// ONE generic <see cref="IntentRail"/>; the host executes the SAME native funnels the UI uses —
    /// GeoPhoenixBase.ConstructFacility (GeoPhoenixBase.cs:229, wallet take + place + under-construction),
    /// RemoveFacility (:277, scrap refund path = the UI demolish/cancel gesture) and RepairFacility
    /// (:261) — and re-derives every cost from its OWN state. The payload carries only ids: siteId,
    /// facility def guid + grid position (build), FacilityId (demolish/repair). No client-supplied costs.
    ///
    /// Mirroring is the rail's job, not this file's: facility VALUES (state/_nextUpdateTime/
    /// _prevUpdateTime — construction progress is time-derived, GeoPhoenixFacility.cs:199-206) ride the
    /// 0xAC value rail keyed by FacilityId (IdentityResolver.IdProbes); the new-facility ENTITY itself
    /// arrives via the universal structural create/destroy applier. Repaint: UIStatePhoenixBaseLayout
    /// already sits in the UiNativeRepaint table (SetLeftSideInfo + SetupBaseLayout).
    ///
    /// The BUILD capture sits on UIModuleBaseLayout.ConstructFacility (the gesture method), NOT on
    /// GeoPhoenixBase.ConstructFacility: blocking the model method would null its return value and the
    /// native caller dereferences it unguarded (slot.InitFacility → facility.ViewElementDef,
    /// PhoenixFacilityController.cs:222 = NRE). It is also that model method's only caller (templates
    /// and tutorial go through Layout.PlaceFacility directly). Demolish/repair capture the model funnel
    /// (void returns, safe to block).
    ///
    /// Same-slot race: the host serializes intents; the loser fails CanPlaceFacility (an
    /// under-construction facility's tiles are already non-buildable, GeoPhoenixBaseLayout.cs:802-812)
    /// or CanBuildFacility (wallet re-check) and is Rejected → law-7 scoped re-emit of S#&lt;siteId&gt;
    /// (values + dict censuses) reconverges the gesturing client.
    /// </summary>
    public static class FacilitySync
    {
        private const byte OpBuild = 1;
        private const byte OpDemolish = 2;  // UI demolish/cancel-construction (scrap:true refund path)
        private const byte OpRepair = 3;

        // Blocked native body's own position derivation (UIModuleBaseLayout.ConstructFacility:459).
        private static readonly MethodInfo CoordByIdx =
            AccessTools.Method(typeof(UIModuleBaseLayout), "GetCoordinateByTileIndex");

        internal static void RegisterIntents()
        {
            var ops = new Dictionary<byte, IntentRail.OpHandler>
            {
                [OpBuild] = HandleIntent,
                [OpDemolish] = HandleIntent,
                [OpRepair] = HandleIntent,
            };
            IntentRail.Register(SurfaceIds.GeoBaseIntent, "base", ops);
        }

        private static GeoLevelController GeoLevel()
        {
            var level = GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<GeoLevelController>();
        }

        /// <summary>TRUE = let the native method run (host, solo, or inside an apply).</summary>
        private static bool ShouldRunNative()
        {
            DiffEngine.FlushOnHostGesture();
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return true;
            return SyncApplyScope.Active; // law 8: applying a delta never echoes an intent
        }

        // ─── CLIENT: capture seams (law 4a) ────────────────────────────────

        /// <summary>Build gesture (see class doc for why this level and not the model funnel).
        /// The menu is closed like the blocked native tail would have; the facility itself appears
        /// when the host's delta lands and the base screen repaints.</summary>
        [HarmonyPatch(typeof(UIModuleBaseLayout), "ConstructFacility")]
        internal static class BuildCapturePatch
        {
            private static bool Prefix(UIModuleBaseLayout __instance, PhoenixFacilityController slot, PhoenixFacilityDef facilityDef)
            {
                if (ShouldRunNative()) return true;
                try
                {
                    var px = __instance.PxBase;
                    if (px == null || px.Site == null || facilityDef == null || slot == null || CoordByIdx == null)
                        return false; // law 3: never write locally — nothing sendable either
                    int siteId = px.Site.SiteId;
                    var pos = (Vector2Int)CoordByIdx.Invoke(__instance, new object[] { slot.transform.GetSiblingIndex() });
                    IntentRail.Send(SurfaceIds.GeoBaseIntent, OpBuild,
                        "build " + facilityDef.name + " @" + pos + " site=" + siteId, w =>
                        { w.Write(siteId); w.Write(facilityDef.Guid); w.Write(pos.x); w.Write(pos.y); });
                    __instance.HideBuildMenu();
                }
                catch (Exception ex) { Debug.LogError("[MP][base] build capture failed: " + ex); }
                return false;
            }
        }

        /// <summary>Demolish/cancel gesture — model funnel. scrap==true is the ONLY user path
        /// (UIModuleBaseLayout.DemolishFacility:773); scrap==false is host-only sim cleanup
        /// (post-base-defense, GeoPhoenixBase.cs:760-762) → blocked silently on a client, the host's
        /// outcome arrives as structural destroy + values.</summary>
        [HarmonyPatch(typeof(GeoPhoenixBase), nameof(GeoPhoenixBase.RemoveFacility))]
        internal static class DemolishCapturePatch
        {
            private static bool Prefix(GeoPhoenixBase __instance, GeoPhoenixFacility facility, bool scrap)
            {
                if (ShouldRunNative()) return true;
                try
                {
                    if (scrap && facility != null && __instance.Site != null)
                        IntentRail.Send(SurfaceIds.GeoBaseIntent, OpDemolish,
                            "demolish " + facility.Def.name + " fid=" + facility.FacilityId, w =>
                            { w.Write(__instance.Site.SiteId); w.Write(facility.FacilityId); });
                }
                catch (Exception ex) { Debug.LogError("[MP][base] demolish capture failed: " + ex); }
                return false;
            }
        }

        /// <summary>Repair gesture — model funnel (only the base-screen repair button reaches it).</summary>
        [HarmonyPatch(typeof(GeoPhoenixBase), nameof(GeoPhoenixBase.RepairFacility))]
        internal static class RepairCapturePatch
        {
            private static bool Prefix(GeoPhoenixBase __instance, GeoPhoenixFacility facility)
            {
                if (ShouldRunNative()) return true;
                try
                {
                    if (facility != null && __instance.Site != null)
                        IntentRail.Send(SurfaceIds.GeoBaseIntent, OpRepair,
                            "repair " + facility.Def.name + " fid=" + facility.FacilityId, w =>
                            { w.Write(__instance.Site.SiteId); w.Write(facility.FacilityId); });
                }
                catch (Exception ex) { Debug.LogError("[MP][base] repair capture failed: " + ex); }
                return false;
            }
        }

        // ─── HOST: apply through the SAME native funnels (dedup/decode/reject = IntentRail) ─────

        private static void HandleIntent(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            int siteId = r.ReadInt32();
            var geo = GeoLevel();
            var site = geo?.Map?.AllSites?.FirstOrDefault(s => s != null && s.SiteId == siteId);
            var px = site == null ? null : site.GetComponent<GeoPhoenixBase>();
            if (px == null || px.Layout == null)
            { IntentRail.Reject(SurfaceIds.GeoBaseIntent, senderPeerId, "no phoenix base for site " + siteId); return; }

            if (op == OpBuild)
            {
                string defGuid = r.ReadString();
                var pos = new Vector2Int(r.ReadInt32(), r.ReadInt32());
                var def = GameUtl.GameComponent<DefRepository>()?.GetDef(defGuid) as PhoenixFacilityDef;
                if (def == null)
                { IntentRail.Reject(SurfaceIds.GeoBaseIntent, senderPeerId, "unknown facility def " + defGuid, "S#" + siteId); return; }
                // Pre-validate BOTH gates: ConstructFacility takes the wallet BEFORE PlaceFacility can
                // throw on an occupied slot (GeoPhoenixBase.cs:231→237) — validating up front keeps a
                // rejected intent side-effect-free. This is also the same-slot race arbiter: the second
                // build of a slot fails CanPlaceFacility here and only ever loses its click.
                if (!px.CanBuildFacility(def) || !px.Layout.CanPlaceFacility(def, pos))
                { IntentRail.Reject(SurfaceIds.GeoBaseIntent, senderPeerId, "build refused (funds/limit/slot) " + def.name + " @" + pos, "S#" + siteId); return; }
                px.ConstructFacility(def, pos);
                Debug.Log("[MP][base] HOST built " + def.name + " @" + pos + " site=" + siteId +
                          " nonce=" + nonce + " peer=" + senderPeerId);
                return;
            }

            uint facilityId = r.ReadUInt32();
            var fac = px.Layout.Facilities.FirstOrDefault(f => f != null && f.FacilityId == facilityId);
            if (fac == null)
            { IntentRail.Reject(SurfaceIds.GeoBaseIntent, senderPeerId, "no facility id=" + facilityId + " site=" + siteId, "S#" + siteId); return; }
            if (op == OpDemolish)
            {
                if (fac.CannotDemolish)
                { IntentRail.Reject(SurfaceIds.GeoBaseIntent, senderPeerId, "cannot demolish " + fac.Def.name, "S#" + siteId); return; }
                px.RemoveFacility(fac, scrap: true);
            }
            else // OpRepair (op set is table-gated upstream)
            {
                if (!px.CanRepairFacility(fac))
                { IntentRail.Reject(SurfaceIds.GeoBaseIntent, senderPeerId, "repair refused " + fac.Def.name, "S#" + siteId); return; }
                px.RepairFacility(fac);
            }
            Debug.Log("[MP][base] HOST op=" + op + " " + fac.Def.name + " fid=" + facilityId +
                      " site=" + siteId + " nonce=" + nonce + " peer=" + senderPeerId);
        }
    }
}
