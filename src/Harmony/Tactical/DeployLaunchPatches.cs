using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Sync.Tactical;
using UnityEngine;

namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// Increment-1 deploy-sync entry point (spec §8, plan T4). Postfix on the PUBLIC
    /// <c>TacticalLevelController.OnLevelStateChanged(Level, Level.State prev, Level.State state)</c>,
    /// firing on the <c>Playing</c> transition (state value 5) — the moment the tactical scene + map are
    /// fully built and turn 0 is ready (grounded: OnLevelStateChanged → OnLevelStart → NextTurnCrt).
    ///
    ///   • HOST: capture the full battle snapshot + NetId actor table and broadcast <c>tac.deploy</c>.
    ///   • CLIENT: restore the pending host snapshot (ProcessInstanceData), rebuild the NetId dict, arm
    ///     mirror mode.
    ///
    /// One hook drives both sides; <see cref="TacticalDeploySync"/> branches on host/client internally.
    /// This patch auto-registers via <c>MultiplayerMain.PatchAll(GetExecutingAssembly())</c> — NO edit to
    /// the bootstrap. Reflection-target (TypeByName) so it binds lazily, like the existing TacticalPatches.
    /// </summary>
    [HarmonyPatch]
    public static class TacticalLevelStateChangedPatch
    {
        private static System.Type _tlcType;
        private static MethodBase _target;
        private static int _playingStateValue = 5;   // Level.State.Playing = 5 (grounded Base.Levels.Level.cs:26)

        public static bool Prepare()
        {
            _tlcType = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.TacticalLevelController");
            if (_tlcType == null) return false;
            _target = AccessTools.Method(_tlcType, "OnLevelStateChanged");
            // Resolve Level.State.Playing dynamically (defensive against enum-value drift).
            var stateType = AccessTools.TypeByName("Base.Levels.Level+State");
            if (stateType != null)
            {
                try { _playingStateValue = (int)System.Enum.Parse(stateType, "Playing"); } catch { }
            }
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // Signature: void OnLevelStateChanged(Level level, Level.State prevState, Level.State state)
        public static void Postfix(object __instance, object state)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive) return;

            int stateVal;
            try { stateVal = System.Convert.ToInt32(state); } catch { return; }
            if (stateVal != _playingStateValue) return;   // only act on the Playing transition

            try
            {
                if (engine.IsHost) TacticalDeploySync.HostOnLevelReady(__instance);
                // Legacy fresh-load path: this Playing-transition postfix only fires for a level the client
                // built natively (launch-then-hydrate) ⇒ alwaysLoaded=false (snapshot ProcessInstanceData IS
                // required). The late-deploy-into-a-live-level path calls ClientOnLevelReady(…, true) directly.
                else TacticalDeploySync.ClientOnLevelReady(__instance, alreadyLoaded: false);
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] TacticalLevelStateChangedPatch.Postfix failed: " + ex);
            }
        }
    }

    /// <summary>
    /// Prefix on <c>GeoLevelController.LaunchTacticalGame(GeoMission mission, PlayTacticalGameLevelResult)</c>
    /// (plan T4). Runs while still on the geoscape, so <c>mission.Site.SiteId</c> — the stable cross-instance
    /// identifier — is reachable; stamps it for the level-ready capture/hydrate.
    ///
    ///   • HOST: stamp the launching site id, then let the native launch run (authoritative).
    ///   • CLIENT: GATE — a spontaneous client-initiated launch is BLOCKED (return false); the client only
    ///     launches when DRIVEN by a received tac.deploy (TacticalDeploySync.ClientLaunchInProgress). The
    ///     deploy-driven launch is allowed through (and still stamps the site id).
    /// </summary>
    [HarmonyPatch]
    public static class LaunchTacticalGameGatePatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var glc = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoLevelController");
            if (glc == null) return false;
            _target = AccessTools.Method(glc, "LaunchTacticalGame");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // Signature: void LaunchTacticalGame(GeoMission mission, PlayTacticalGameLevelResult gameParams)
        public static bool Prefix(object mission)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive) return true;   // single-player → untouched

            // Always stamp the launching site id (both sides need it).
            try { TacticalDeploySync.OnTacticalLaunch(mission); }
            catch (Exception ex) { Debug.LogError($"[Multiplayer][tac] OnTacticalLaunch failed: {ex}"); }

            if (engine.IsHost) return true;   // host launches authoritatively

            // CLIENT: only the deploy-driven launch is allowed; block any spontaneous self-launch.
            if (TacticalDeploySync.ClientLaunchInProgress) return true;
            Debug.Log("[Multiplayer][tac] CLIENT spontaneous LaunchTacticalGame GATED (awaiting tac.deploy)");
            return false;
        }
    }

    /// <summary>
    /// Mission-exit cleanup: prefix on <c>TacticalLevelController.OnLevelStateChanged</c> is overkill; instead
    /// hook the tactical level teardown so mirror mode + per-mission tactical state reset when the battle
    /// ends. Targets <c>TacticalLevelController.OnLevelEnd</c> if present, else <c>StopLevel</c>; harmless
    /// no-op when neither exists (mirror also disarms on the next deploy's fresh state).
    /// </summary>
    [HarmonyPatch]
    public static class TacticalLevelEndPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var tlc = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.TacticalLevelController");
            if (tlc == null) return false;
            _target = AccessTools.Method(tlc, "OnLevelEnd")
                      ?? AccessTools.Method(tlc, "StopLevel")
                      ?? AccessTools.Method(tlc, "OnLevelStop");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static void Postfix()
        {
            try { TacticalDeploySync.OnMissionExit(); }
            catch (Exception ex) { Debug.LogError($"[Multiplayer][tac] OnMissionExit failed: {ex}"); }
        }
    }
}
