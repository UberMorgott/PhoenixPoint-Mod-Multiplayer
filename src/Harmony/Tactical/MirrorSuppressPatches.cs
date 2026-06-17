using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multipleer.Sync.Tactical;
using UnityEngine;

namespace Multipleer.Harmony.Tactical
{
    /// <summary>
    /// Increment-1 client mirror-mode suppression (spec §6, plan T6). When this instance is a synced-session
    /// CLIENT inside a hydrated tactical mission (<see cref="TacticalDeploySync.IsClientMirroring"/>), it is
    /// a PURE MIRROR: it must not run the enemy AI loop and must not auto-advance the turn (both are driven
    /// by the host and arrive over the live outcome rails in later increments). These prefixes skip those
    /// two coroutines on the client only; the host is never suppressed.
    ///
    /// Both targets return <c>IEnumerator&lt;NextUpdate&gt;</c>. A prefix that returns false MUST set
    /// <c>__result</c> to a non-null EMPTY enumerator of the right element type, or the caller (which does
    /// <c>yield return Timing.Current.Call(theCrt())</c>) would dereference null. We build an empty
    /// <c>IEnumerator&lt;NextUpdate&gt;</c> via reflection on the game's NextUpdate type.
    ///
    /// Auto-registers via PatchAll — no bootstrap edit.
    /// </summary>
    internal static class EmptyCrt
    {
        private static System.Type _nextUpdateType;
        private static object _cachedEmpty;

        /// <summary>An empty <c>IEnumerator&lt;NextUpdate&gt;</c> (cached). Returns null if NextUpdate can't
        /// be resolved (then the prefix falls back to letting the original run — safest).</summary>
        public static object Empty()
        {
            if (_cachedEmpty != null) return _cachedEmpty;
            // NextUpdate lives in Base.Core (grounded: Base.Core/NextUpdate.cs). The crts return
            // IEnumerator<NextUpdate>, so an empty enumerator of that element type is the safe skip result.
            _nextUpdateType = _nextUpdateType ?? AccessTools.TypeByName("Base.Core.NextUpdate");
            if (_nextUpdateType == null) return null;
            // Build List<NextUpdate>().GetEnumerator() → a valid empty IEnumerator<NextUpdate>.
            var listType = typeof(List<>).MakeGenericType(_nextUpdateType);
            var list = System.Activator.CreateInstance(listType);
            var getEnum = listType.GetMethod("GetEnumerator");
            _cachedEmpty = getEnum.Invoke(list, null);
            return _cachedEmpty;
        }
    }

    /// <summary>Client: skip the enemy-AI loop (<c>TacticalFaction.AIUpdateCrt</c>). The host runs enemy AI
    /// and streams outcomes; the client never rolls.</summary>
    [HarmonyPatch]
    public static class AIUpdateCrtSuppressPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.TacticalFaction");
            if (t == null) return false;
            _target = AccessTools.Method(t, "AIUpdateCrt");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix(ref object __result)
        {
            if (!TacticalDeploySync.IsClientMirroring) return true;   // host / non-mirror → run normally
            var empty = EmptyCrt.Empty();
            if (empty == null) return true;   // couldn't build empty crt → safest to let it run
            __result = empty;
            return false;   // skip original
        }
    }

    /// <summary>Client: skip native faction auto-advance (<c>TacticalLevelController.NextTurnCrt</c>). Turn
    /// progression is driven by the host's <c>tac.turn</c> (Increment 4); the client never self-advances.</summary>
    [HarmonyPatch]
    public static class NextTurnCrtSuppressPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.TacticalLevelController");
            if (t == null) return false;
            _target = AccessTools.Method(t, "NextTurnCrt");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix(ref object __result)
        {
            if (!TacticalDeploySync.IsClientMirroring) return true;
            var empty = EmptyCrt.Empty();
            if (empty == null) return true;
            __result = empty;
            return false;
        }
    }
}
