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
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.PhoenixBases;
using PhoenixPoint.Geoscape.Entities.PhoenixBases.FacilityComponents;
using PhoenixPoint.Geoscape.Entities.Sites;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View.ViewControllers.PhoenixBase;
using PhoenixPoint.Geoscape.View.ViewModules;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Base intent family (surface 0xB1, law 1): build / demolish-scrap / repair / power / rename ride the
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
        private const byte OpTogglePower = 4;
        private const byte OpRename = 5;      // the base's NAME — a site value, not a facility one
        private const byte OpAssignSlot = 6;  // a facility's worker/user SLOT — see AssignSlotCapturePatch

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
                [OpTogglePower] = HandleIntent,
                [OpRename] = HandleIntent,
                [OpAssignSlot] = HandleIntent,
            };
            IntentRail.Register(SurfaceIds.GeoBaseIntent, "base", ops);
        }

        // ─── CLIENT: capture seams (law 4a; gate = IntentRail.ShouldRunNative, the shared law) ─────

        /// <summary>Build gesture (see class doc for why this level and not the model funnel).
        /// The menu is closed like the blocked native tail would have; the facility itself appears
        /// when the host's delta lands and the base screen repaints.</summary>
        [HarmonyPatch(typeof(UIModuleBaseLayout), "ConstructFacility")]
        internal static class BuildCapturePatch
        {
            private static bool Prefix(UIModuleBaseLayout __instance, PhoenixFacilityController slot, PhoenixFacilityDef facilityDef)
            {
                if (IntentRail.ShouldRunNative()) return true;
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
                catch (Exception ex) { MpLog.LogError("[MP][base] build capture failed: " + ex); }
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
                if (IntentRail.ShouldRunNative()) return true;
                try
                {
                    if (scrap && facility != null && __instance.Site != null)
                        IntentRail.Send(SurfaceIds.GeoBaseIntent, OpDemolish,
                            "demolish " + facility.Def.name + " fid=" + facility.FacilityId, w =>
                            { w.Write(__instance.Site.SiteId); w.Write(facility.FacilityId); });
                }
                catch (Exception ex) { MpLog.LogError("[MP][base] demolish capture failed: " + ex); }
                return false;
            }
        }

        /// <summary>Repair gesture — model funnel (only the base-screen repair button reaches it).</summary>
        [HarmonyPatch(typeof(GeoPhoenixBase), nameof(GeoPhoenixBase.RepairFacility))]
        internal static class RepairCapturePatch
        {
            private static bool Prefix(GeoPhoenixBase __instance, GeoPhoenixFacility facility)
            {
                if (IntentRail.ShouldRunNative()) return true;
                try
                {
                    if (facility != null && __instance.Site != null)
                        IntentRail.Send(SurfaceIds.GeoBaseIntent, OpRepair,
                            "repair " + facility.Def.name + " fid=" + facility.FacilityId, w =>
                            { w.Write(__instance.Site.SiteId); w.Write(facility.FacilityId); });
                }
                catch (Exception ex) { MpLog.LogError("[MP][base] repair capture failed: " + ex); }
                return false;
            }
        }

        /// <summary>Power on/off gesture. Captured at the UI funnel (UIModuleBaseLayout.TogglePower:856),
        /// which is where the native validation lives (PowerCost==0 / RemainingPower) — the model funnel
        /// GeoPhoenixFacility.SetPowered is the class-wide BLOCK instead (ClientSimGate.FacilityPowerGate),
        /// because every non-gesture power write (RoutePower, state-change auto-plug) also lands there and
        /// must stay host-only. The caller TogglePowerOnSlot:850-854 still refreshes the slot widget from
        /// the un-mutated model; the host's delta repaints it for real.</summary>
        [HarmonyPatch(typeof(UIModuleBaseLayout), "TogglePower")]
        internal static class TogglePowerCapturePatch
        {
            private static bool Prefix(UIModuleBaseLayout __instance, GeoPhoenixFacility facility)
            {
                if (IntentRail.ShouldRunNative()) return true;
                try
                {
                    var px = __instance == null ? null : __instance.PxBase;
                    if (facility != null && px != null && px.Site != null)
                        IntentRail.Send(SurfaceIds.GeoBaseIntent, OpTogglePower,
                            "power " + facility.Def.name + " fid=" + facility.FacilityId, w =>
                            { w.Write(px.Site.SiteId); w.Write(facility.FacilityId); });
                }
                catch (Exception ex) { MpLog.LogError("[MP][base] power capture failed: " + ex); }
                return false;
            }
        }

        /// <summary>
        /// RENAME gesture — model funnel (GeoPhoenixBase.RenameBase:991-993, a void that writes
        /// Site.SiteName and nothing else; its ONLY caller is UIModuleBaseLayout.OnBaseRenamed:695, so
        /// blocking it strands no native tail).
        ///
        /// THE OTHER HALF OF 21edc3b. That commit put the site's name on the value rail (the
        /// GeoSite.Name -> SiteName alias in RailMeta), so a HOST rename now reaches every peer — and it
        /// said in as many words that a CLIENT's own rename was "a local write nobody captures, so the
        /// host's next delta reverts it". This is the capture. Nothing else was missing: the intent
        /// family, the dedup, the reject and the mirror all existed; the rename simply had no seam.
        ///
        /// The blocked native tail (UIModuleBaseLayout.cs:696-697) re-reads BaseName.text and the tab
        /// button from the UN-mutated model, so the client's screen keeps the OLD name for the
        /// round-trip — which is the block-first posture, not a glitch: the new name appears when the
        /// host's delta lands and the universal re-enter arm repaints
        /// (UiEventMap.cs:529 UIStatePhoenixBaseLayout -> UIModuleBaseLayout.SetLeftSideInfo:605 ->
        /// BaseName.text = PxBase.Site.Name:607).
        /// </summary>
        [HarmonyPatch(typeof(GeoPhoenixBase), nameof(GeoPhoenixBase.RenameBase))]
        internal static class RenameCapturePatch
        {
            private static bool Prefix(GeoPhoenixBase __instance, string name)
            {
                if (IntentRail.ShouldRunNative()) return true;
                try
                {
                    if (__instance?.Site == null || string.IsNullOrEmpty(name))
                        return false; // law 3: never write locally — and nothing sendable either
                    IntentRail.Send(SurfaceIds.GeoBaseIntent, OpRename,
                        "rename site=" + __instance.Site.SiteId + " -> " + name, w =>
                        { w.Write(__instance.Site.SiteId); w.Write(name); });
                }
                catch (Exception ex) { MpLog.LogError("[MP][base] rename capture failed: " + ex); }
                return false;
            }
        }

        /// <summary>
        /// SOLDIER-SLOT gesture — the assign/unassign half of a base, which had NO seam at all: nothing in
        /// src/ named <c>UseSoldiersFacilityComponent</c>, so a client dragging a worker into a lab mutated
        /// its own model silently and every peer (the host included) kept the old crew. That is economy
        /// divergence, not a stale panel: the slot arrays feed <c>ResourceGeneratorFacilityComponent</c>
        /// .UpdateOutput, the heal/XP rates and the base's whole production rollup.
        ///
        /// ONE PREFIX, THE MODEL FUNNEL. <c>AssignSoldierSlot</c> (UseSoldiersFacilityComponent.cs:207) is
        /// the SINGLE writer of both slot arrays — every other route is a wrapper over it:
        /// <c>AssignSoldierToFreeSlot</c> :185, <c>UnassignSoldierFromUsedSlot</c> :200,
        /// <c>UnassignSoldierSlot</c> :243, <c>UnassignAllSoldiers</c> :354/:361 and
        /// <c>UnassignSoldier</c> :372/:380 (itself the <c>OnSoldierRemoved</c> handler :235). So the UI
        /// gesture (UIModulePXBaseFacilityRoster.cs:138/142, UIStatePhoenixFacilityRosterAssignment.cs:134/
        /// 137/143), the facility-driven release (HealFacilityComponent.cs:81,
        /// ExperienceFacilityComponent.cs:66) and the base-driven one (GeoPhoenixBase.cs:812/869/907) all
        /// arrive here already reduced to (slot, character, type) — no per-caller guard, no re-derivation.
        /// The <c>source</c> rides along so the host replays the gesture the client actually made.
        ///
        /// Void method, so blocking strands no dereference. The one caller that reads a RESULT is
        /// <c>AssignSoldierToFreeSlot</c>, which returns true for "a free slot existed" — its UI caller
        /// (:137/:143) only stages <c>slot.Selected</c>, a widget flag the repaint re-seeds. Block-first
        /// posture: the model stays as the rail left it until the host's own AssignSoldierSlot lands.
        ///
        /// Repaint: the slot arrays now ride the rail as a LeafList of "U#" refs under
        /// <c>_components#&lt;componentDefGuid&gt;</c> (the ComponentDef probe, 64e9ef0), and
        /// <c>UseSoldiersFacilityComponent</c> has no per-kind arm in <c>UiEventMap</c>, so it takes the
        /// universal open-screen repaint (UiEventMap.cs:205-213) — the open roster screen is Exit+Enter'd
        /// and re-reads the slots. The HOST's own screen is repainted by the intent dispatch
        /// (IntentRail.cs:236).
        /// </summary>
        [HarmonyPatch(typeof(UseSoldiersFacilityComponent), nameof(UseSoldiersFacilityComponent.AssignSoldierSlot))]
        internal static class AssignSlotCapturePatch
        {
            private static bool Prefix(UseSoldiersFacilityComponent __instance, int slot, GeoCharacter character,
                                       UseSoldiersFacilityComponent.SlotType type,
                                       UseSoldiersFacilityComponent.SlotChangeSource source)
            {
                if (IntentRail.ShouldRunNative()) return true;
                try
                {
                    var fac = __instance.Facility;
                    var site = fac?.PxBase == null ? null : fac.PxBase.Site;
                    var guid = __instance.ComponentDef == null ? null : __instance.ComponentDef.Guid;
                    if (site == null || guid == null)
                        return false; // law 3: never write locally — and nothing addressable to send
                    int charId = character == null ? -1 : (int)character.Id;
                    IntentRail.Send(SurfaceIds.GeoBaseIntent, OpAssignSlot,
                        "slot " + type + "[" + slot + "] fid=" + fac.FacilityId + " -> " +
                        (charId < 0 ? "(empty)" : "U#" + charId), w =>
                        {
                            w.Write(site.SiteId);
                            w.Write(fac.FacilityId);
                            w.Write(guid);
                            w.Write(slot);
                            w.Write((byte)type);
                            w.Write((byte)source);
                            w.Write(charId);
                        });
                }
                catch (Exception ex) { MpLog.LogError("[MP][base] slot capture failed: " + ex); }
                return false;
            }
        }

        // ─── HOST: apply through the SAME native funnels (dedup/decode/reject = IntentRail) ─────

        private static void HandleIntent(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            int siteId = r.ReadInt32();
            var geo = GenericApplier.GeoLevel();
            var site = geo?.Map?.AllSites?.FirstOrDefault(s => s != null && s.SiteId == siteId);
            var px = site == null ? null : site.GetComponent<GeoPhoenixBase>();
            if (px == null || px.Layout == null)
            { IntentRail.Reject(SurfaceIds.GeoBaseIntent, senderPeerId, "no phoenix base for site " + siteId); return; }

            if (op == OpBuild)
            {
                string defGuid = WireString.ReadKey(r);
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
                MpLog.Log("[MP][base] HOST built " + def.name + " @" + pos + " site=" + siteId +
                          " nonce=" + nonce + " peer=" + senderPeerId);
                return;
            }

            if (op == OpRename)
            {
                // The same native funnel the host's own popup uses; the new name reaches every peer on the
                // 0xAC value rail as GeoSite.SiteName (RailMeta alias, 21edc3b). No cost, no validation
                // beyond non-empty: the game's own BaseNameValidator (UIModuleBaseLayout.cs:686-689)
                // accepts every character, so the only refusable input is nothing at all.
                string newName = WireString.ReadText(r);
                if (string.IsNullOrWhiteSpace(newName))
                { IntentRail.Reject(SurfaceIds.GeoBaseIntent, senderPeerId, "empty base name", "S#" + siteId); return; }
                px.RenameBase(newName);
                MpLog.Log("[MP][base] HOST renamed site=" + siteId + " -> '" + newName +
                          "' nonce=" + nonce + " peer=" + senderPeerId);
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
            else if (op == OpTogglePower)
            {
                // The native gesture body (UIModuleBaseLayout.cs:856-878), replayed from HOST state — the
                // client sends only "toggle this facility", never a target value or a power number.
                if (fac.Def.PowerCost == 0)
                { IntentRail.Reject(SurfaceIds.GeoBaseIntent, senderPeerId, "no power switch on " + fac.Def.name, "S#" + siteId); return; }
                if (fac.IsPowered) fac.SetPowered(powered: false);
                else if (fac.Def.PowerCost <= px.Stats.RemainingPower) fac.SetPowered(powered: true);
                else
                { IntentRail.Reject(SurfaceIds.GeoBaseIntent, senderPeerId, "not enough power for " + fac.Def.name, "S#" + siteId); return; }
            }
            else if (op == OpAssignSlot)
            {
                // Client input is UNTRUSTED: every field is re-validated against HOST state before the
                // native funnel runs, and nothing derived is taken from the wire (no output, no rate, no
                // capacity — those are recomputed by the game from the slots it ends up with).
                string compGuid = WireString.ReadKey(r);
                int slot = r.ReadInt32();
                byte typeByte = r.ReadByte(), sourceByte = r.ReadByte();
                int charId = r.ReadInt32();
                var comp = fac.Components?.OfType<UseSoldiersFacilityComponent>()
                              .FirstOrDefault(c => c.ComponentDef != null && c.ComponentDef.Guid == compGuid);
                if (comp == null)
                { IntentRail.Reject(SurfaceIds.GeoBaseIntent, senderPeerId, "no soldier component " + compGuid + " on fid=" + facilityId, "S#" + siteId); return; }
                // SlotType.None makes GetSoldierSlots throw (UseSoldiersFacilityComponent.cs:263), and an
                // undefined enum byte would too — refuse both here rather than through the throw funnel.
                var type = (UseSoldiersFacilityComponent.SlotType)typeByte;
                if (type != UseSoldiersFacilityComponent.SlotType.User &&
                    type != UseSoldiersFacilityComponent.SlotType.Worker)
                { IntentRail.Reject(SurfaceIds.GeoBaseIntent, senderPeerId, "bad slot type " + typeByte, "S#" + siteId); return; }
                if (slot < 0 || slot >= comp.GetSoldierSlotsCount(type))
                { IntentRail.Reject(SurfaceIds.GeoBaseIntent, senderPeerId, "slot " + slot + " out of range for " + type, "S#" + siteId); return; }
                GeoCharacter who = null;
                if (charId >= 0)
                {
                    who = geo.Factions.SelectMany(f => f.Characters).FirstOrDefault(c => c != null && (int)c.Id == charId);
                    // The soldier must be one of the BASE OWNER's — the wire carries a bare id, so without
                    // this a client could staff its own labs with another faction's roster.
                    if (who == null || who.Faction != site.Owner)
                    { IntentRail.Reject(SurfaceIds.GeoBaseIntent, senderPeerId, "no own soldier U#" + charId + " for site " + siteId, "S#" + siteId); return; }
                }
                var source = Enum.IsDefined(typeof(UseSoldiersFacilityComponent.SlotChangeSource), sourceByte)
                    ? (UseSoldiersFacilityComponent.SlotChangeSource)sourceByte
                    : UseSoldiersFacilityComponent.SlotChangeSource.External;
                comp.AssignSoldierSlot(slot, who, type, source);
            }
            else // OpRepair (op set is table-gated upstream)
            {
                if (!px.CanRepairFacility(fac))
                { IntentRail.Reject(SurfaceIds.GeoBaseIntent, senderPeerId, "repair refused " + fac.Def.name, "S#" + siteId); return; }
                px.RepairFacility(fac);
            }
            MpLog.Log("[MP][base] HOST op=" + op + " " + fac.Def.name + " fid=" + facilityId +
                      " site=" + siteId + " nonce=" + nonce + " peer=" + senderPeerId);
        }
    }
}
