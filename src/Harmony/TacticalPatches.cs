using System;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Network.MessageLayer;
using UnityEngine;

namespace Multipleer.Harmony
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
            try { return Multipleer.Sync.Tactical.TacticalTurnSync.ClientRelayEndTurn(); }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer] RequestEndTurnPatch relay failed: " + ex.Message);
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

        public static bool Prefix()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive) return true;
            if (engine.IsHost) return true;
            // Feature C: when the client is deliberately REPLAYING this coroutine to play the attack ANIMATION
            // (projectile-free, camera-silent), let it run — otherwise this prefix would suppress our own replay.
            if (Multipleer.Sync.Tactical.TacticalFireAnimSync.ReplayActive) return true;
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
