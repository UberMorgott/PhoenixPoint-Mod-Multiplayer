using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Reflection-bound "is the geoscape live?" probe (bind once, cache — mirrors
    /// <c>TimeSyncManager.EnsureReflection()</c>). Verified against the decompile (2026-06-15):
    ///   Base.Core.GameUtl.CurrentLevel() -> Level (a Component).
    /// The old wallet-sync helpers (CurrentLevel/PhoenixFaction/Wallet) were deleted 2026-07-22 —
    /// zero callers after the generic value rail took over; history in git.
    /// </summary>
    public sealed class GeoRuntime
    {
        private static GeoRuntime _instance;
        public static GeoRuntime Instance => _instance ?? (_instance = new GeoRuntime());

        private Type _geoLevelType;
        private Type _gameUtlType;
        private MethodInfo _currentLevel;
        private bool _ready;

        private GeoRuntime() => EnsureReflection();

        private void EnsureReflection()
        {
            if (_ready) return;
            _geoLevelType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoLevelController");
            _gameUtlType = AccessTools.TypeByName("Base.Core.GameUtl") ?? AccessTools.TypeByName("GameUtl");
            _currentLevel = _gameUtlType != null ? AccessTools.Method(_gameUtlType, "CurrentLevel") : null;
            _ready = _geoLevelType != null && _currentLevel != null;
        }

        /// <summary>The live <c>GeoLevelController</c>, or null if not in geoscape / mid-load.</summary>
        public object GeoLevel()
        {
            EnsureReflection();
            if (!_ready) return null;
            try
            {
                var level = _currentLevel.Invoke(null, null); // GameUtl.CurrentLevel()
                if (level == null) return null;
                if (level is Component comp)
                    return comp.GetComponent(_geoLevelType); // null if current level isn't geoscape
                return null;
            }
            catch { return null; }
        }

        public bool IsGeoscapeActive => GeoLevel() != null;
    }
}
