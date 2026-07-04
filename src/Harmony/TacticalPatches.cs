using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using UnityEngine;

namespace Multiplayer.Harmony
{
    /// <summary>
    /// END-TURN relay (Inc 4), repointed onto the live tactical rail. On a mirroring CLIENT this sends
    /// <c>tac.intent.endturn</c> to the host and lets the native <c>RequestEndTurn</c> set the LOCAL
    /// <c>_endTurnRequested</c> flag so the client's own PlayTurnCrt player loop exits (input stops) while it
    /// waits for the host's next <c>tac.turn</c>. Host / single-player run unchanged (host's end-turn drives
    /// NextTurnCrt → TacMission.OnNewTurn postfix broadcasts tac.turn). The old dead
    /// <c>PacketType.EndTurnRequest</c> send (never received) is gone.
    /// </summary>
    [HarmonyPatch]
    public static class RequestEndTurnPatch
    {
        private static Type _targetType;
        private static MethodBase _targetMethod;

        public static bool Prepare()
        {
            _targetType = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.TacticalFaction");
            _targetMethod = AccessTools.Method(_targetType, "RequestEndTurn");
            return _targetMethod != null;
        }

        public static MethodBase TargetMethod() => _targetMethod;

        // Returns true so the native RequestEndTurn always runs (sets _endTurnRequested locally). On a
        // mirroring client it ALSO relays the intent to the host first.
        public static bool Prefix()
        {
            try { return Multiplayer.Sync.Tactical.TacticalTurnSync.ClientRelayEndTurn(); }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] RequestEndTurnPatch relay failed: " + ex.Message);
                return true;
            }
        }
    }

    [HarmonyPatch]
    public static class FireWeaponPatch
    {
        private static Type _targetType;
        private static MethodBase _targetMethod;

        public static bool Prepare()
        {
            _targetType = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.TacticalLevelController");
            _targetMethod = AccessTools.Method(_targetType, "FireWeaponAtTargetCrt");
            return _targetMethod != null;
        }

        public static MethodBase TargetMethod() => _targetMethod;

        // Harmony injects the original args by NAME: FireWeaponAtTargetCrt(Weapon weapon,
        // TacticalAbilityTarget abilityTarget, ShootAbility shootAbility). We only need the ability + its target;
        // typed as object so this layer needs no engine-type references (HostBroadcastFireStart reflects them).
        public static bool Prefix(object shootAbility, object abilityTarget)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive) return true;
            if (engine.IsHost)
            {
                // RE-TIMING FIX (sequential double-play): broadcast tac.fire.start HERE — the moment the host's
                // authoritative shot animation ACTUALLY begins. This coroutine is invoked from ShootAbility.Shoot
                // AFTER a long-range shot's EnqueueAction(soloAfterCurrent)+camera-blend defer, so the client's
                // animation replay now starts CONCURRENTLY with the host's visible shot (and damage lands
                // together), instead of replaying early at the Activate/enqueue prefix. ONE call per shoot action
                // (the whole burst loops inside this single coroutine); covers host-OWN shots, host-executed
                // client intents, AND overwatch/return-fire reactions. Host-gated so the client never re-broadcasts;
                // the per-attack-type gate inside also rejects the client's own Synced replay. Fail-open.
                Multiplayer.Sync.Tactical.TacticalFireAnimSync.HostBroadcastFireStart(shootAbility, abilityTarget);
                return true;
            }
            // Feature C: when the client is deliberately REPLAYING this coroutine to play the attack ANIMATION
            // (projectile-free, camera-silent), let it run — otherwise this prefix would suppress our own replay.
            if (Multiplayer.Sync.Tactical.TacticalFireAnimSync.ReplayActive) return true;
            return false;
        }
    }

    [HarmonyPatch]
    public static class InventoryActionPatch
    {
        private static Type _targetType;
        private static MethodBase _targetMethod;

        public static bool Prepare()
        {
            _targetType = AccessTools.TypeByName("PhoenixPoint.Tactical.View.ViewStates.UIStateInventory");
            _targetMethod = AccessTools.Method(_targetType, "AttemptMoveItems");
            return _targetMethod != null;
        }

        public static MethodBase TargetMethod() => _targetMethod;

        public static bool Prefix()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive) return true;
            if (engine.IsHost) return true;

            var msg = new NetworkMessage(PacketType.TacticalActionRequest);
            engine.SendToHost(msg);
            return false;
        }
    }
}
