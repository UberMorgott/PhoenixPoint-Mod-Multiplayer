using System.Reflection;
using HarmonyLib;
using Multiplayer.Sync.Tactical;
using UnityEngine;

namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// MISSION-CONCLUSION host chokepoint (spec TS4, surface <c>tac.missionend</c> 0x95). A single HOST postfix on
    /// <c>TacticalLevelController.GameOver()</c> — the one method that fires <c>GameWrappingUpEvent</c> then
    /// <c>GameOverEvent</c> and (lazily) builds <c>GetMissionResult()</c>. It hands the level to
    /// <see cref="TacticalMissionEndSync.HostOnGameOver"/>, which broadcasts the wrappingup + gameover conclusion.
    /// The client is never patched (it closes purely from the inbound 0x95 surface by riding the native
    /// <c>IsGameOver</c> flow). All gating (host + active session + not client-mirroring + deploy-captured) lives in
    /// the sync layer, so a single-player / pre-deploy / client-side GameOver is a clean no-op. Auto-registers via
    /// <c>MultiplayerMain.PatchAll(GetExecutingAssembly())</c>; reflection-target lazily like the sibling tactical
    /// patches.
    /// </summary>
    [HarmonyPatch]
    public static class TacticalLevelControllerGameOverPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.TacticalLevelController");
            if (t == null) return false;
            // public void GameOver()  (TacticalLevelController.cs:825)
            _target = AccessTools.Method(t, "GameOver");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // Postfix (never suppresses the native game-over). __instance = the TacticalLevelController that ended.
        public static void Postfix(object __instance)
        {
            try { TacticalMissionEndSync.HostOnGameOver(__instance); }
            catch (System.Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] TacticalLevelControllerGameOverPatch.Postfix failed: " + ex);
            }
        }
    }
}
