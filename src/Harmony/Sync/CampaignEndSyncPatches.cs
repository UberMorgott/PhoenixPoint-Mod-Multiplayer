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
    /// Campaign-END sync (feat-campaign-end) on the SINGLE native geoscape endgame chokepoint —
    /// <c>GeoLevelController.TriggerGameOver(GeoFaction victoriousFaction)</c> (GeoLevelController.cs:1068).
    /// EVERY campaign ending funnels there (decompile-verified 2026-07-07): defeat by bases lost
    /// (GameOverCheck :1064) / world-population collapse (ChangeWorldPopulation :1109), and the event-driven
    /// win/lose endings — vanilla palace victory AND TFTV custom endings alike ride
    /// <c>GeoEventChoiceOutcome.GameOverVictoryFaction</c> → <c>GeoFactionReward.Apply</c> (:121) →
    /// TriggerGameOver. Mirrors the ReportModalMirror chokepoint discipline:
    ///   • HOST Postfix → if THIS call flipped the native one-shot latch (<c>_gameOverTriggered</c>, read in
    ///     the Prefix as <c>__state</c>), broadcast the campaign-end notice on the EXISTING 0x69 report rail
    ///     (NEW <see cref="ReportModalVariant.CampaignEnd"/> variant — WA-3 InterceptionNotice precedent, no
    ///     new packet family) carrying {victory|defeat, victor faction Def.Guid}. The broadcast happens
    ///     SYNCHRONOUSLY here — long before the host's own teardown (its outro cutscene + GameOver screen +
    ///     the user's menu click all precede <c>FinishLevelAndGoToLobby</c>), so the reliable notice always
    ///     leaves the socket before the transport goes down (notice-before-teardown, with a natural grace).
    ///   • CLIENT Prefix → suppress the LOCAL trigger (a pure-mirror client must never end the campaign off
    ///     its own mirrored state — e.g. the bases-lost check re-firing on mirrored site writes); the ending
    ///     arrives solely via the host's notice, replayed under <see cref="SyncApplyScope"/> (which passes).
    /// Both directions gate through the PURE <see cref="CampaignEndFlow"/> decisions (rail kill-switch =
    /// the existing <c>ReportMirrorGate</c>; no new flag). All best-effort try/catch; on any failure native
    /// runs (fail-open) — the host must never lose ITS ending to a sync bug. Host transparency: the Prefix
    /// never blocks on the host and the Postfix only reads; the host's window is byte-identical either way.
    /// TFTV needs no extra guard here: it never patches TriggerGameOver (its palace/custom endings end the
    /// TACTICAL via controller.GameOver — already TS4 — then use the native geoscape win-event machinery).
    /// </summary>
    [HarmonyPatch]
    public static class CampaignEndChokepointPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var geoT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoLevelController");
            var factionT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoFaction");
            if (geoT == null || factionT == null) return false;
            // EXACT param match (harmony-accesstools-exact-param-match): (GeoFaction).
            _target = AccessTools.Method(geoT, "TriggerGameOver", new[] { factionT });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __state = the native one-shot latch BEFORE this call (true → the call is a native no-op and the
        // Postfix must not broadcast). CLIENT: skip the local trigger unless it is our own engine replay.
        public static bool Prefix(object __instance, ref bool __state)
        {
            __state = true;   // fail-closed default: an unreadable latch never double-broadcasts
            try
            {
                __state = CampaignEndReflection.ReadGameOverTriggered(__instance);
                var engine = NetworkEngine.Instance;
                if (engine != null && CampaignEndFlow.ClientShouldSuppressNativeTrigger(
                        engine.IsHost, engine.IsActiveSession, ReportMirrorGate.Enabled, SyncApplyScope.IsApplying))
                {
                    Debug.Log("[Multiplayer] CLIENT local TriggerGameOver suppressed (campaign end is host-authoritative"
                              + " — awaiting the 0x69 campaign-end notice)");
                    return false;
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] CampaignEndChokepointPatch.Prefix failed: " + ex.Message); }
            return true;   // host (and any failure / gate-off / replay): native runs
        }

        // __0 = victoriousFaction (GeoFaction, boxed). HOST: broadcast the one-shot campaign-end notice.
        public static void Postfix(object __instance, object __0, bool __state)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null) return;
                if (!CampaignEndFlow.HostShouldBroadcast(engine.IsHost, engine.IsActiveSession,
                        ReportMirrorGate.Enabled, wasAlreadyTriggered: __state,
                        isApplying: SyncApplyScope.IsApplying)) return;
                // Belt: only broadcast when the native latch REALLY flipped under this call (a Prefix
                // read-miss reported __state=true and already returned above; this guards the inverse —
                // a __state=false call that native still refused would otherwise ship a phantom ending).
                if (!CampaignEndReflection.ReadGameOverTriggered(__instance)) return;

                CampaignEndReflection.ReadHostEnd(__instance, __0, out var victory, out var victorGuid);
                var payload = CampaignEndFlow.BuildPayload(victory, victorGuid);
                Debug.Log("[Multiplayer] HOST campaign END → broadcast on 0x69 (victory=" + victory
                          + " victorGuid=" + victorGuid + ") — clients replay the native outro");
                engine.Sync?.BroadcastReportModal(payload);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] CampaignEndChokepointPatch.Postfix failed: " + ex.Message); }
        }
    }
}
