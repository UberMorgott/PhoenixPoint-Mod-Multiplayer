using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multipleer.Network.Sync
{
    /// <summary>
    /// Centralized reflection surface for live geoscape state + bound action methods.
    /// All game types are resolved by name (the mod has NO compile-time game references).
    /// Mirrors <c>TimeSyncManager.EnsureReflection()/GetGeoLevel()</c>: bind once, cache.
    /// Action <c>Apply</c>/<c>Validate</c> code and interceptors share this one binding surface.
    /// Verified against the decompile (2026-06-15):
    ///   Base.Core.GameUtl.CurrentLevel() -> Level (a Component);
    ///   PhoenixPoint.Geoscape.Levels.GeoLevelController.PhoenixFaction { get; } -> GeoPhoenixFaction;
    ///   GeoFaction.Wallet { get; } -> Wallet.
    /// </summary>
    public sealed class GeoRuntime
    {
        private static GeoRuntime _instance;
        public static GeoRuntime Instance => _instance ?? (_instance = new GeoRuntime());

        private Type _geoLevelType;
        private Type _gameUtlType;
        private MethodInfo _currentLevel;
        private PropertyInfo _phoenixFactionProp;
        private bool _ready;

        private GeoRuntime() => EnsureReflection();

        private void EnsureReflection()
        {
            if (_ready) return;
            _geoLevelType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoLevelController");
            _gameUtlType = AccessTools.TypeByName("Base.Core.GameUtl") ?? AccessTools.TypeByName("GameUtl");
            _currentLevel = _gameUtlType != null ? AccessTools.Method(_gameUtlType, "CurrentLevel") : null;
            if (_geoLevelType != null)
                _phoenixFactionProp = AccessTools.Property(_geoLevelType, "PhoenixFaction");
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

        /// <summary>The player faction (<c>GeoPhoenixFaction</c>), or null.</summary>
        public object PhoenixFaction()
        {
            var geo = GeoLevel();
            if (geo == null) return null;
            try { return _phoenixFactionProp?.GetValue(geo, null); }
            catch { return null; }
        }

        /// <summary>The player faction <c>Wallet</c>, or null.</summary>
        public object Wallet()
        {
            var fac = PhoenixFaction();
            if (fac == null) return null;
            try { return AccessTools.Property(fac.GetType(), "Wallet")?.GetValue(fac, null); }
            catch { return null; }
        }

        public bool IsGeoscapeActive => GeoLevel() != null;
    }
}
