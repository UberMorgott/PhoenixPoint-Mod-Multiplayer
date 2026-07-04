using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using PhoenixPoint.Common.Levels.Params;
using UnityEngine;

namespace Multiplayer.Harmony
{
    /// <summary>
    /// Barrier safety net for the session-start load.
    ///
    /// Both client and host load paths converge on PhoenixGame.FinishLevel(ILevelParams)
    /// (PhoenixGame.cs:263) when the argument is a LoadLevelGameResult — that is the single
    /// "enter the loaded level" seam. During a coop session-start the SaveTransferCoordinator drives
    /// the entry explicitly on BEGIN, so any FinishLevel(LoadLevelGameResult) that arrives while the
    /// barrier is still pending must be HELD (return false) to stop a peer entering early.
    ///
    /// The coordinator's own EnterLevel() sets _begun before it calls FinishLevel, so the gate lets
    /// that release call through.
    /// </summary>
    [HarmonyPatch]
    public static class FinishLevelBarrierPatch
    {
        private static Type _targetType;
        private static MethodBase _targetMethod;

        public static bool Prepare()
        {
            _targetType = AccessTools.TypeByName("PhoenixPoint.Common.Game.PhoenixGame");
            if (_targetType == null) return false;
            _targetMethod = AccessTools.Method(_targetType, "FinishLevel");
            return _targetMethod != null;
        }

        public static MethodBase TargetMethod() => _targetMethod;

        // result is the ILevelParams arg of FinishLevel(ILevelParams result = null).
        public static bool Prefix(object result)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActive) return true;

                var coord = engine.SaveTransfer;
                if (coord == null || !coord.IsBarrierPending) return true;

                // Only gate the level-entry call; let quit/lobby/other results pass through.
                if (!(result is LoadLevelGameResult)) return true;

                // Barrier still closed → hold this entry until BEGIN releases it.
                Debug.Log("[Multiplayer] Holding FinishLevel until session BEGIN (barrier).");
                return false;
            }
            catch (Exception e)
            {
                Debug.LogError("[Multiplayer] FinishLevelBarrierPatch failed: " + e.Message);
                return true;
            }
        }
    }
}
