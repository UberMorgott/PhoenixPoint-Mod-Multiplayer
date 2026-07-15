using System;
using System.Collections.Generic;
using System.Reflection;
using Base.Core;
using Base.Serialization;
using HarmonyLib;
using Multiplayer.Network;
using PhoenixPoint.Common.Levels.Params;
using PhoenixPoint.Common.Saves;
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

    /// <summary>
    /// CLIENT belt for the post-mission geoscape return. Native
    /// PhoenixSaveManager.LoadCurrentGeoscape (cs:380-398) opens with
    /// _currentGeoscapeSection.ContentObjects.First() — a null section throws ArgumentNullException
    /// INSIDE the master game coroutine (MenuCrt→GeoscapeGameCrt→ProcessTacticalGameResult), killing it:
    /// the client is then stranded in a dead tactical scene forever (RCA 2026-07-15 mission-end).
    /// The section is normally filled at co-op tactical entry (SaveTransferCoordinator
    /// PrepareEntryFromBlobCrt step 1c); this prefix is the safety net if that ever misses: loud log +
    /// skip with an empty coroutine. The skip leaves _nextSceneBinding null, so GeoscapeGameCrt's next
    /// RunGameLevel throws under its own CatchException handler → MenuCrt(ErrorLoadingLevel) → main
    /// menu — degraded, but recoverable (rejoin), instead of a frozen client. Host/SP untouched.
    /// </summary>
    [HarmonyPatch]
    public static class ClientGeoscapeReturnGuardPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            _target = AccessTools.Method(typeof(PhoenixSaveManager), "LoadCurrentGeoscape");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix(PhoenixSaveManager __instance, ref IEnumerator<NextUpdate> __result)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActive || engine.IsHost) return true;

                var section = AccessTools.Field(typeof(PhoenixSaveManager), "_currentGeoscapeSection")
                    ?.GetValue(__instance) as SavegameContentSection;
                if (section != null && section.ContentObjects != null) return true;

                Debug.LogError("[Multiplayer] CLIENT LoadCurrentGeoscape: geoscape return snapshot is EMPTY " +
                               "(_currentGeoscapeSection not filled at tactical entry) — aborting the native load " +
                               "instead of killing the game coroutine; the game will fall back to the main menu.");
                __result = EmptyCrt();
                return false;
            }
            catch (Exception e)
            {
                Debug.LogError("[Multiplayer] ClientGeoscapeReturnGuardPatch failed (native load runs): " + e.Message);
                return true;
            }
        }

        private static IEnumerator<NextUpdate> EmptyCrt() { yield break; }
    }
}
