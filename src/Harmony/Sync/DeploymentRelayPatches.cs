using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.State;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// CLIENT-INITIATED mission launch — squad pick on the INITIATOR (2026-07-13). Old flow: a client CONFIRM
    /// on a mirrored brief relayed immediately and the native deployment window (UIStateRosterDeployment)
    /// opened on the HOST — the wrong player picked the squad. New flow:
    ///   1. Client CONFIRM → <c>ClientDeployRelay.TryBeginLocalSquadPick</c> (called from
    ///      BlockingModalClientLock.TryRelayBeginMission) opens the NATIVE deployment window on the CLIENT
    ///      (<c>GeoModalDisplay.TryClientOpenDeployment</c>) and arms this relay; the mirrored brief pops
    ///      natively (mirror tag cleared → lock releases).
    ///   2. Client clicks DEPLOY → <see cref="DeploySquadRelayPatch"/> captures the picked squad as GeoUnitIds
    ///      (<c>PersonnelReflection.ReadUnitId</c> — the personnel channel's shared identity), sends
    ///      <c>MissionStartRequestAction(modalType, siteId, unitIds)</c>, closes the window, and SKIPS the
    ///      native local <c>_mission.Launch</c> (a client never launches).
    ///   3. Host apply resolves the ids and arms <see cref="MissionLaunchSquadOverride"/>; the native
    ///      FinishDialog(Confirm) → <c>GeoscapeView.LaunchMission</c> then hits
    ///      <see cref="LaunchMissionSquadOverridePatch"/>, which launches with the CLIENT-picked squad directly
    ///      (the same <c>new GeoSquad + mission.Launch</c> shape as the native skip-deployment branch,
    ///      GeoscapeView.cs:1043-1047) — NO deployment window on the host.
    ///   4. Client CANCEL in the window → <see cref="MissionCancelRelayGuardPatch"/> swallows the mirror-side
    ///      <c>GeoMission.Cancel()</c> sim mutation (host stays authoritative; its brief remains open) while the
    ///      native view restore (ResetViewState + FinishQueriedState) runs.
    /// HOST-initiated launches are untouched: nothing arms on a native host confirm, so the host keeps its
    /// native window. Loadout edits inside the client window ride the existing personnel-edit rail. Every
    /// patch fails OPEN on unreadable state (reflective targets, Prepare false → skipped).
    /// </summary>
    internal static class ClientDeployRelay
    {
        private static bool _armed;
        private static byte _modalType;
        private static int _siteId;

        public static bool Armed => _armed;
        public static byte ModalType => _modalType;
        public static int SiteId => _siteId;

        /// <summary>CLIENT: open the native deployment window over the mirror and arm the relay. False →
        /// caller degrades to the legacy immediate relay (deployment window on the host).</summary>
        public static bool TryBeginLocalSquadPick(byte modalType, int siteId, object mission)
        {
            if (!GeoModalDisplay.TryClientOpenDeployment(GeoRuntime.Instance, mission)) return false;
            _armed = true;
            _modalType = modalType;
            _siteId = siteId;
            return true;
        }

        public static void Disarm() => _armed = false;
    }

    /// <summary>CLIENT: intercept the deployment window's DEPLOY (<c>UIStateRosterDeployment.DeploySquad</c>,
    /// the single chokepoint both the direct click and the tired-soldiers confirmation route through) while
    /// the squad-pick relay is armed: relay the picked GeoUnitIds to the host instead of launching locally.</summary>
    [HarmonyPatch]
    public static class DeploySquadRelayPatch
    {
        private static MethodBase _target;
        private static FieldInfo _selectedDeploymentField;   // UIStateRosterDeployment._selectedDeployment (List<GeoCharacter>)

        public static bool Prepare()
        {
            var stateT = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewStates.UIStateRosterDeployment");
            if (stateT == null) return false;
            _target = AccessTools.Method(stateT, "DeploySquad", Type.EmptyTypes);
            _selectedDeploymentField = AccessTools.Field(stateT, "_selectedDeployment");
            return _target != null && _selectedDeploymentField != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix(object __instance)
        {
            if (!ClientDeployRelay.Armed) return true;   // host / native flows untouched
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || engine.IsHost || !engine.IsActiveSession)
                {
                    ClientDeployRelay.Disarm();   // stale arm outside a client session → native runs
                    return true;
                }

                // _selectedDeployment is kept in lock-step with the enrolled deployment items
                // (UIStateRosterDeployment.OnEnrollmentChanged) — the same set native DeploySquad launches.
                var ids = new List<long>();
                if (_selectedDeploymentField.GetValue(__instance) is IEnumerable selected)
                    foreach (var ch in selected)
                    {
                        long id = PersonnelReflection.ReadUnitId(ch);
                        if (id != 0) ids.Add(id);
                    }

                // Unresolvable picks (all ids 0) degrade to the legacy empty-tail relay → the HOST gets the
                // native deployment window (same fallback as a failed window open — never a dead click).
                Debug.Log("[Multiplayer] CLIENT squad-pick DEPLOY → MissionStartRequest modalType=" + ClientDeployRelay.ModalType +
                          " siteId=" + ClientDeployRelay.SiteId + " units=" + ids.Count);
                engine.Sync?.SendActionRequest(new Multiplayer.Network.Sync.Actions.MissionStartRequestAction(
                    ClientDeployRelay.ModalType, ClientDeployRelay.SiteId, ids.ToArray()));

                ClientDeployRelay.Disarm();
                GeoModalDisplay.CloseDeployment(GeoRuntime.Instance);
                return false;   // NEVER run the native local _mission.Launch on a client
            }
            catch (Exception ex)
            {
                // Fail CLOSED for the launch (a client must never launch locally); window stays up, Cancel works.
                Debug.LogError("[Multiplayer] DeploySquadRelayPatch failed: " + ex.Message);
                return false;
            }
        }
    }

    /// <summary>CLIENT: while the squad-pick relay is armed, swallow the window's Cancel-path mirror mutation
    /// (<c>UIStateRosterDeployment.ToPreviousScreen → _mission.Cancel()</c>, :256-268) — the host still has its
    /// brief open and stays sole authority over the mission's fate. The rest of ToPreviousScreen (ResetViewState
    /// + FinishQueriedState view restore) runs natively. Mandatory missions never reach Cancel (no back button),
    /// so the one Cancel override with extra side effects (GeoPhoenixBaseDefenseMission) is unreachable here.</summary>
    [HarmonyPatch]
    public static class MissionCancelRelayGuardPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var missionT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoMission");
            if (missionT == null) return false;
            _target = AccessTools.Method(missionT, "Cancel", Type.EmptyTypes);
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix(object __instance)
        {
            if (!ClientDeployRelay.Armed) return true;
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || engine.IsHost || !engine.IsActiveSession) return true;
                if (SyncApplyScope.IsApplying) return true;   // engine-driven replay is never the local cancel click
                // Only the armed mission's cancel is swallowed; unreadable site ids (-1) still match (we are
                // inside the armed pick window — the only live Cancel source is its ToPreviousScreen).
                int siteId = ReportModalReflection.GetMissionSiteId(__instance);
                if (siteId >= 0 && ClientDeployRelay.SiteId >= 0 && siteId != ClientDeployRelay.SiteId) return true;
                ClientDeployRelay.Disarm();
                Debug.Log("[Multiplayer] CLIENT squad-pick CANCEL → mirror GeoMission.Cancel swallowed (host brief stays open)");
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] MissionCancelRelayGuardPatch failed: " + ex.Message);
                return true;   // fail OPEN: worst case the mirror cancels locally (pre-feature behavior)
            }
        }
    }

    /// <summary>HOST: one-shot live-mission handle for a relayed EVENT mission-start choice (2026-07-16
    /// SP-parity deploy fix). The model-only answer resolve leaves NO open brief — SyncEngine arms this with
    /// the <c>ChoiceReward.ApplyResult.StartMission</c> instance + unicasts EventMissionDeploy to the answering
    /// client; the client's DEPLOY returns via the id-100 sentinel (<c>EventMissionDeploySentinel</c>) and
    /// <c>MissionStartRequestAction.Apply</c> consumes this handle to launch THAT mission natively. Single
    /// slot: a newer event overwrites (only one relayed mission-start can be in flight per host player);
    /// session reset clears (SyncEngine) so a dead geoscape's live mission never leaks.</summary>
    internal static class EventMissionLaunchPending
    {
        private static int _siteId = -1;
        private static object _mission;

        public static void Arm(int siteId, object mission) { _siteId = siteId; _mission = mission; }
        public static void Clear() { _siteId = -1; _mission = null; }

        /// <summary>One-shot: the handle is cleared on a successful match. A mismatched/absent handle returns
        /// false and leaves state untouched (stale id-100 after a newer event re-armed → logged no-op).</summary>
        public static bool TryConsume(int siteId, out object mission)
        {
            mission = _mission;
            if (mission == null || siteId != _siteId) { mission = null; return false; }
            Clear();
            return true;
        }
    }

    /// <summary>HOST: one-shot squad override for the next <c>GeoscapeView.LaunchMission</c> — armed by
    /// <c>MissionStartRequestAction.Apply</c> with the RESOLVED GeoCharacter instances, consumed synchronously
    /// by <see cref="LaunchMissionSquadOverridePatch"/> in the same FinishDialog(Confirm) call stack.</summary>
    internal static class MissionLaunchSquadOverride
    {
        private static List<object> _characters;

        public static void Arm(List<object> characters) => _characters = characters;
        public static void Disarm() => _characters = null;

        public static bool TryConsume(out List<object> characters)
        {
            characters = _characters;
            _characters = null;
            return characters != null && characters.Count > 0;
        }
    }

    /// <summary>HOST: when a client-picked squad is armed, launch with it directly — the native
    /// skip-deployment shape (<c>new GeoSquad(units); mission.Launch(squad)</c>, GeoscapeView.cs:1043-1047) —
    /// and SKIP the native body, so no deployment window opens on the host.</summary>
    [HarmonyPatch]
    public static class LaunchMissionSquadOverridePatch
    {
        private static MethodBase _target;
        private static ConstructorInfo _squadCtor;   // GeoSquad() (parameterless; Units is a readonly list field)
        private static FieldInfo _squadUnitsField;   // GeoSquad.Units (List<GeoCharacter>)
        private static MethodInfo _missionLaunch;    // GeoMission.Launch(GeoSquad = null)

        public static bool Prepare()
        {
            var viewT = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.GeoscapeView");
            var missionT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoMission");
            var containerT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.IGeoCharacterContainer");
            var squadT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSquad");
            if (viewT == null || missionT == null || containerT == null || squadT == null) return false;
            // EXACT param match (harmony-accesstools-exact-param-match): LaunchMission(GeoMission, IGeoCharacterContainer).
            _target = AccessTools.Method(viewT, "LaunchMission", new[] { missionT, containerT });
            _squadCtor = AccessTools.Constructor(squadT, Type.EmptyTypes);
            _squadUnitsField = AccessTools.Field(squadT, "Units");
            _missionLaunch = AccessTools.Method(missionT, "Launch", new[] { squadT });
            return _target != null && _squadCtor != null && _squadUnitsField != null && _missionLaunch != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __0 = the GeoMission being launched (the host's validated open brief — Apply armed us only after
        // TryHostConfirmBlocking matched modalType + siteId against it).
        public static bool Prefix(object __0)
        {
            if (!MissionLaunchSquadOverride.TryConsume(out var characters)) return true;   // host-initiated → native window
            try
            {
                object squad = _squadCtor.Invoke(null);
                var units = (IList)_squadUnitsField.GetValue(squad);
                foreach (var ch in characters) units.Add(ch);
                Debug.Log("[Multiplayer] HOST LaunchMission override → launching with client-picked squad (" +
                          units.Count + " unit(s)), host deployment window skipped");
                _missionLaunch.Invoke(__0, new[] { squad });
                return false;
            }
            catch (Exception ex)
            {
                // Fail OPEN: native LaunchMission runs → deployment window on the host (pre-feature behavior).
                Debug.LogError("[Multiplayer] LaunchMissionSquadOverridePatch failed: " + ex.Message);
                return true;
            }
        }
    }
}
