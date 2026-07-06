using System.Reflection;
using HarmonyLib;
using Multiplayer.Sync.Tactical;
using UnityEngine;

namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// Mid-battle actor LIFECYCLE host chokepoints (spec TS1). Two HOST postfixes on
    /// <c>TacticalLevelController</c>; the client is never patched (it materializes/removes purely from the
    /// inbound 0x92/0x93 surfaces via <see cref="TacticalActorLifecycleSync"/>). Both delegate all gating to the
    /// sync layer (host + active session + deploy-captured), so a stray fire off-host / off-session is a clean
    /// no-op. Auto-register via <c>MultiplayerMain.PatchAll(GetExecutingAssembly())</c>; reflection-target lazily
    /// like the sibling tactical patches.
    /// </summary>
    [HarmonyPatch]
    public static class TacticalLevelControllerActorEnteredPlayPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.TacticalLevelController");
            if (t == null) return false;
            // public void ActorEnteredPlay(TacticalActorBase actor)  (TacticalLevelController.cs:798)
            _target = AccessTools.Method(t, "ActorEnteredPlay");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // Postfix (never suppresses the native handler). __0 = the entering TacticalActorBase.
        public static void Postfix(object __0)
        {
            try { TacticalActorLifecycleSync.HostOnActorEnteredPlay(__0); }
            catch (System.Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] ActorEnteredPlayPatch.Postfix failed: " + ex);
            }
        }
    }

    /// <summary>
    /// HOST postfix on <c>TacticalLevelController.ActorDied(DeathReport)</c> (TacticalLevelController.cs:793):
    /// registry cleanup for a damage-death so a re-minted netId can't collide the dead one (and the despawn sweep
    /// never re-broadcasts a death). The death VISUAL already mirrors via tac.damage (0x88) — this is NOT a second
    /// death path.
    /// </summary>
    [HarmonyPatch]
    public static class TacticalLevelControllerActorDiedPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.TacticalLevelController");
            if (t == null) return false;
            // public void ActorDied(DeathReport deathReport)
            _target = AccessTools.Method(t, "ActorDied");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // Postfix. __0 = the DeathReport.
        public static void Postfix(object __0)
        {
            try { TacticalActorLifecycleSync.HostOnActorDied(__0); }
            catch (System.Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] ActorDiedPatch.Postfix failed: " + ex);
            }
        }
    }
}
