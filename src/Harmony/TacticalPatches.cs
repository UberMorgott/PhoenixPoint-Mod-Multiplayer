using System;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Network.MessageLayer;
using UnityEngine;

namespace Multipleer.Harmony
{
    [HarmonyPatch]
    public static class ActivateAbilityPatch
    {
        private static Type _targetType;
        private static MethodBase _targetMethod;

        public static bool Prepare()
        {
            _targetType = AccessTools.TypeByName("PhoenixPoint.Tactical.View.TacticalViewState");
            if (_targetType == null) return false;

            _targetMethod = AccessTools.Method(_targetType, "ActivateAbility",
                new[] {
                    AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.TacticalAbility"),
                    typeof(object)
                });
            return _targetMethod != null;
        }

        public static MethodBase TargetMethod() => _targetMethod;

        public static bool Prefix(object __instance, object ability, object target)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive) return true;
            if (engine.IsHost) return true;

            try
            {
                if (ability == null) return true;

                var abilityType = ability.GetType();
                var actorBaseProp = abilityType.GetProperty("TacticalActorBase");
                var actorBase = actorBaseProp?.GetValue(ability, null);
                if (actorBase == null) return true;

                var geoUnitProp = actorBase.GetType().GetProperty("GeoUnitId");
                var geoUnit = geoUnitProp?.GetValue(actorBase, null);
                var geoIdField = geoUnit?.GetType().GetField("_id");
                var actorGeoId = geoIdField != null ? (int)geoIdField.GetValue(geoUnit) : -1;

                var defProp = abilityType.GetProperty("Def");
                var def = defProp?.GetValue(ability, null);
                var guidField = def?.GetType().GetProperty("Guid");
                var abilityDefId = guidField?.GetValue(def, null)?.ToString() ?? "";

                var action = new TacticalActionMessage
                {
                    ActionId = Guid.NewGuid(),
                    ActionType = ResolveActionType(ability),
                    ActorGeoId = actorGeoId,
                    AbilityDefId = abilityDefId,
                    TargetData = SerializeTarget(target),
                    Timestamp = DateTime.UtcNow.Ticks
                };

                engine.SendTacticalAction(action);
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Multipleer] ActivateAbility Prefix failed: {ex.Message}");
                return true;
            }
        }

        private static TacticalActionType ResolveActionType(object ability)
        {
            var name = ability.GetType().Name;
            if (name.Contains("Shoot")) return TacticalActionType.Shoot;
            if (name.Contains("Move")) return TacticalActionType.Move;
            if (name.Contains("Reload")) return TacticalActionType.Reload;
            if (name.Contains("Overwatch")) return TacticalActionType.Overwatch;
            return TacticalActionType.UseAbility;
        }

        private static byte[] SerializeTarget(object target)
        {
            if (target == null) return Array.Empty<byte>();
            try
            {
                var targetType = target.GetType();
                var actorField = targetType.GetField("Actor");
                var actorProp = actorField == null ? targetType.GetProperty("Actor") : null;
                var posField = targetType.GetField("PositionToApply");
                var posProp = posField == null ? targetType.GetProperty("PositionToApply") : null;

                object targetActor = null;
                if (actorField != null) targetActor = actorField.GetValue(target);
                else if (actorProp != null) targetActor = actorProp.GetValue(target, null);

                object targetPosObj = Vector3.zero;
                if (posField != null) targetPosObj = posField.GetValue(target);
                else if (posProp != null) targetPosObj = posProp.GetValue(target, null);

                var targetPos = targetPosObj is Vector3 v ? v : Vector3.zero;

                int targetGeoId = -1;
                if (targetActor != null)
                {
                    var geoProp = targetActor.GetType().GetProperty("GeoUnitId");
                    var geoUnit = geoProp?.GetValue(targetActor, null);
                    var idField = geoUnit?.GetType().GetField("_id");
                    if (idField != null) targetGeoId = (int)idField.GetValue(geoUnit);
                }

                return Serialization.NetworkSerializer.SerializeTargetData(
                    targetGeoId, targetPos.x, targetPos.y, targetPos.z, 0, null);
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }
    }

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
