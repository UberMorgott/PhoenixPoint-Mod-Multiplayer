using System;
using System.Linq;
using Base.Core;
using HarmonyLib;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.Abilities;
using PhoenixPoint.Geoscape.Entities.Missions;
using PhoenixPoint.Geoscape.Events;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// A DECLINED MISSION OFFER IS NOT A DEAD ONE — THE 2026-08-11 REPORT.
    ///
    /// Every peer may press CANCEL and come back later; in the stock game clicking the site again re-offers
    /// the mission. Measured in the 00:45 three-peer run, that re-entry was dead on a client and lopsided on
    /// the host, for TWO separate reasons and one stale read:
    ///
    ///  (1) THE GESTURE WAS SWALLOWED. Both native re-entry doors end in a LOCAL EVENT RAISE:
    ///      <c>TriggerEncounterAbility.ActivateInternal</c>:45 calls
    ///      <c>GeoscapeEventSystem.TriggerGeoscapeEvent</c> directly, and
    ///      <c>GeoscapeView.ShowMissionBriefing</c>:1889-1893 (the <c>KeepEncounterID</c> arm the mission
    ///      entry takes once a mission exists) calls <c>GeoCustomMission.Cancel()</c> and then the same
    ///      trigger. On a client BOTH halves are gated — <c>MissionCancelGate</c> (law 4b, shared campaign
    ///      state) and <c>ClientSimGate.GeoscapeEventRaiseGate</c> (law 3, the client mints no record) — so
    ///      the click did nothing at all and said so only in the log:
    ///      <c>PP-Instance2 7455 "[MP][events] client-local raise of 'PROG_SY0_MISS' BLOCKED"</c>.
    ///      Fixed here by RELAYING the gesture: the host runs the game's own re-offer and every peer gets it
    ///      as the ordinary 0xB6 raise, so all peers land in the SAME stage.
    ///
    ///  (2) THE ENTRY WAS NOT EVEN THERE. Both menu rows are gated on state a per-peer decline never
    ///      touches: <c>LaunchMissionAbility</c>:34 needs <c>site.ActiveMission</c>, and
    ///      <c>TriggerEncounterAbility</c>:34 needs <c>GeoSite.HasActiveEncounter</c>, which is
    ///      <c>!IsEventTriggered</c> = the record must read <c>Reset</c>
    ///      (<c>GeoscapeEventRecord.IsFree</c>). The game's OWN re-offer switch is
    ///      <c>GeoscapeEvent.CompleteEvent</c>:103-106, which calls <c>EnableGeoscapeEvent</c> for a
    ///      <c>ReEneableEvent</c> decline — and our per-peer <c>cancel-local</c> arm
    ///      (<c>EventPopup.EventChoiceClientLock</c>) deliberately runs no <c>CompleteEvent</c> at all, so
    ///      that switch never flipped on any peer and the row stayed hidden until somebody ELSE accepted and
    ///      minted an <c>ActiveMission</c> (<c>PP-Instance2 6048 "[MP][site] repaint S#200 …
    ///      activeMission=StorySYN0_CustomMissionTypeDef"</c>). Nothing cached it; the state was genuinely
    ///      gone. <see cref="AfterDecline"/> flips the game's own switch instead.
    ///
    /// NO QUORUM (P13). A decline re-enables the offer immediately and unconditionally — it never counts
    /// peers, never waits for one, and the re-offer click needs nobody's agreement. The LAUNCH is untouched
    /// and still host-authoritative exactly once (<c>MissionSync.Validate</c> / L405).
    ///
    /// REACTIVITY. The re-enable rides the ordinary <c>ES</c> record delta, and
    /// <c>OpenUiRepaint.RefreshSiteContextualMenu</c> (OpenUiRepaint.cs:246) re-derives the OPEN site
    /// context menu through the game's own <c>SetMenuItems</c> after every rail batch — so the row appears
    /// on an already-open menu with no click, no re-entry and no flying away.
    /// </summary>
    internal static class MissionReoffer
    {
        /// <summary>Re-enable this peer's declined offer for EVERYONE, through the game's own switch. The
        /// record is a rail-covered leaf (docs/rail-baseline.txt:463-470), so a client cannot write it — it
        /// asks the host, and the host's <c>Reset</c> comes back as the ordinary delta.</summary>
        internal static void AfterDecline(string eventId, GeoSite site)
        {
            if (string.IsNullOrEmpty(eventId)) return;
            string siteRef = IdentityResolver.RootRef(site) ?? "";
            if (IntentRail.ShouldRunNative())
            {
                var es = GenericApplier.GeoLevel()?.EventSystem;
                var rec = es?.GetEventRecord(eventId);
                string refusal = EventSync.ReofferRefusal(rec == null ? (GeoscapeEventRecordState?)null : rec.State);
                EventPopup.BriefProbe(eventId, "reoffer-enabled", refusal == null
                    ? "this peer declined and the offer was RE-ENABLED for every peer on the spot " +
                      "(GeoscapeEventSystem.EnableGeoscapeEvent, the same call CompleteEvent:105 makes for a " +
                      "ReEneableEvent decline) — the site's own menu row is back with no fly-away and nothing " +
                      "waits on another player"
                    : "this peer declined but the offer was NOT re-enabled — " + refusal);
                if (refusal == null) es.EnableGeoscapeEvent(eventId);
                return;
            }
            EventPopup.BriefProbe(eventId, "reoffer-requested", "this peer declined; the record is host-owned, " +
                "so the re-enable is relayed and comes back as an ES delta. Nothing here waits on it and " +
                "nothing waits on another player");
            IntentRail.Send(SurfaceIds.GeoEventIntent, EventSync.OpReoffer,
                "re-enable offer '" + eventId + "'",
                w => { w.Write(siteRef); w.Write(eventId); w.Write(EventSync.ReofferEnable); });
        }

        /// <summary>The client half of the two native re-entry doors. Returns true when the gesture was
        /// relayed and the native body must be skipped — on a client that body can only be swallowed twice
        /// over (see the class header), which is exactly what the player saw as "nothing happens".</summary>
        internal static bool RelayGesture(GeoSite site, string what)
        {
            if (site == null) return false;
            string siteRef = IdentityResolver.RootRef(site);

            // THE DOOR IS THE WAY BACK IN, NOT A SECOND OFFER — the 2026-08-13 report's second half, and the
            // ONE seam both native re-entry doors route through, on BOTH roles. A re-offer is not a re-open:
            // it ends in GeoCustomMission.Cancel + TriggerGeoscapeEvent, which RESETS the shared record and
            // broadcasts a fresh START/CANCEL dialog to every peer. Measured: peer 2 started the mission
            // (record=Completed), re-offered it from the site row, the record went back to Reset, peer 3 was
            // handed that stale dialog and its click came back as "another player already answered this
            // event". While this peer's own DEPLOYMENT PREP button serves this very site, the gesture is
            // ANSWERED BY THAT BUTTON: nothing is cancelled, nothing is re-raised, and no peer is offered a
            // choice that is already taken. Blocks nobody (P13) — the refusal lasts exactly as long as the
            // door does, and the door takes itself down when the drop launches, is cancelled or flies off.
            if (DeployPrep.ReofferIsRedundant(DeployPrep.LiveSiteRef(), siteRef))
            {
                EventPopup.BriefProbe(siteRef, "reoffer-has-a-door", what + " — a deployment prep for THIS " +
                    "site is already announced, so the DEPLOYMENT PREP button on the geoscape is the " +
                    "entrance. The re-offer is skipped: running it would cancel the live mission and re-raise " +
                    "a start/cancel dialog on peers whose start has already been taken.");
                return true;
            }

            if (IntentRail.ShouldRunNative()) return false;
            if (string.IsNullOrEmpty(siteRef))
            {
                MpLog.LogWarning("[MP][mission] re-offer gesture at an unaddressable site was dropped — the host " +
                                 "cannot be told which site to re-offer; the player can still reach the mission " +
                                 "from the aircraft once another peer opens it");
                return false;
            }
            string eventId = site.EncounterID ?? "";
            EventPopup.BriefProbe(siteRef, "reoffer-relayed", what + " on a client — both native halves are " +
                "gated here (MissionCancelGate + GeoscapeEventRaiseGate), so the gesture is relayed and the " +
                "HOST runs the game's own re-offer; every peer then lands in the same stage off one 0xB6 raise");
            IntentRail.Send(SurfaceIds.GeoEventIntent, EventSync.OpReoffer, "re-offer at " + siteRef,
                w => { w.Write(siteRef); w.Write(eventId); w.Write(EventSync.ReofferNow); });
            return true;
        }

        /// <summary>HOST: run the re-offer the relaying peer's own click would have run, choosing the door
        /// the GAME chooses — a live <c>KeepEncounterID</c> mission goes through
        /// <c>ShowMissionBriefing</c> (which cancels it and re-triggers, exactly as
        /// <c>LaunchMissionAbilityView</c>:16 does), otherwise the site's own encounter is triggered
        /// (<c>TriggerEncounterAbility</c>:45). Never a hand-rolled window: both calls broadcast through the
        /// raise patch every peer already listens on.</summary>
        internal static string HostReoffer(GeoLevelController geo, GeoSite site)
        {
            if (geo == null || site == null) return "the site does not resolve on the host";
            var view = geo.View;
            if (view == null) return "the host has no geoscape view to raise a window from";
            var vehicle = site.Vehicles == null ? null
                : site.Vehicles.FirstOrDefault(v => v.Owner == geo.ViewerFaction);   // GeoscapeView.cs:1699
            var custom = site.ActiveMission as GeoCustomMission;
            if (custom != null && !string.IsNullOrEmpty(custom.KeepEncounterID))
            { view.ShowMissionBriefing(site.ActiveMission, vehicle); return null; }
            if (site.HasActiveEncounter)
            {
                geo.EventSystem.TriggerGeoscapeEvent(site.EncounterID,
                    new GeoscapeEventContext(site, geo.ViewerFaction, vehicle));
                return null;
            }
            return "the host's site offers nothing to re-open: no KeepEncounter mission and no free encounter " +
                   "record (GeoSite.HasActiveEncounter is false)";
        }

    }

    /// <summary>Door one: the site's own encounter row. Client relays; host/solo run native.</summary>
    [HarmonyPatch(typeof(TriggerEncounterAbility), "ActivateInternal", new[] { typeof(GeoAbilityTarget) })]
    internal static class EncounterReofferInput
    {
        private static bool Prefix(GeoAbilityTarget target) =>
            !MissionReoffer.RelayGesture(target.Actor as GeoSite, "TriggerEncounterAbility:45");
    }

    /// <summary>Door two: the aircraft's mission row once a <c>KeepEncounterID</c> mission exists
    /// (<c>LaunchMissionAbilityView</c>:16). Only the KeepEncounter arm is relayed — the ordinary brief-modal
    /// arm at :1897-1904 is a per-peer window <c>GeoModalMirror</c> already owns.</summary>
    [HarmonyPatch(typeof(GeoscapeView), nameof(GeoscapeView.ShowMissionBriefing))]
    internal static class BriefReofferInput
    {
        /// <summary>FIRST, ahead of <c>NativeRaiserToken.MissionBriefCapture</c>. This door decides whether
        /// the brief happens here at all; the capture only records a brief that does. Relaying before the
        /// capture reflects into the native <c>GetMissionBriefModal</c> also keeps the wire ahead of the work
        /// that a cancelled call throws away.</summary>
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(GeoMission mission)
        {
            var custom = mission as GeoCustomMission;
            if (custom == null || string.IsNullOrEmpty(custom.KeepEncounterID)) return true;
            return !MissionReoffer.RelayGesture(mission.Site, "GeoscapeView.ShowMissionBriefing:1891");
        }
    }
}
