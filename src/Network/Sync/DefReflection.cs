using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multipleer.Network.Sync
{
    /// <summary>
    /// Shared reflection bridge to the game def repository. Every synced def is wired by its stable
    /// <c>BaseDef.Guid</c> (string) and resolved on each peer via the singleton <c>DefRepository</c>.
    ///
    /// Verified against the decompile (2026-06-15):
    ///   • <c>Base.Defs.BaseDef.Guid</c> (public string, BaseDef.cs:21).
    ///   • <c>Base.Defs.DefRepository.GetDef(string guid)</c> (DefRepository.cs:70) → BaseDef.
    ///   • repo singleton: <c>GameUtl.GameComponent&lt;DefRepository&gt;()</c> (the resolver used by mods,
    ///     e.g. TFTV DefCache). Bound here generically as <c>GameUtl.GameComponent(Type)</c>.
    /// </summary>
    public static class DefReflection
    {
        private static bool _ready;
        private static Type _baseDefType;        // Base.Defs.BaseDef
        private static Type _defRepoType;         // Base.Defs.DefRepository
        private static Type _gameUtlType;         // Base.Core.GameUtl
        private static FieldInfo _guidField;      // BaseDef.Guid
        private static MethodInfo _getDef;        // DefRepository.GetDef(string)
        private static MethodInfo _gameComponent; // GameUtl.GameComponent<DefRepository>()
        private static object _repo;              // cached DefRepository instance

        private static void Ensure()
        {
            if (_ready) return;
            _baseDefType = AccessTools.TypeByName("Base.Defs.BaseDef");
            _defRepoType = AccessTools.TypeByName("Base.Defs.DefRepository");
            _gameUtlType = AccessTools.TypeByName("Base.Core.GameUtl") ?? AccessTools.TypeByName("GameUtl");
            if (_baseDefType == null || _defRepoType == null || _gameUtlType == null) return;

            _guidField = AccessTools.Field(_baseDefType, "Guid");
            _getDef = AccessTools.Method(_defRepoType, "GetDef", new[] { typeof(string) });

            // GameUtl.GameComponent<DefRepository>() — generic static; close it over DefRepository.
            var generic = AccessTools.Method(_gameUtlType, "GameComponent", new Type[0]);
            if (generic != null && generic.IsGenericMethodDefinition)
                _gameComponent = generic.MakeGenericMethod(_defRepoType);

            _ready = _guidField != null && _getDef != null;
        }

        /// <summary>Read <c>BaseDef.Guid</c> off any def instance, or null.</summary>
        public static string GetGuid(object def)
        {
            if (def == null) return null;
            try { Ensure(); return _guidField?.GetValue(def) as string; }
            catch (Exception ex) { Debug.LogError("[Multipleer] DefReflection.GetGuid failed: " + ex.Message); return null; }
        }

        private static object Repo()
        {
            if (_repo != null) return _repo;
            try
            {
                Ensure();
                if (_gameComponent != null)
                    _repo = _gameComponent.Invoke(null, null);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] DefReflection.Repo failed: " + ex.Message); }
            return _repo;
        }

        /// <summary>Resolve a def by its <c>BaseDef.Guid</c> via the live <c>DefRepository</c>, or null.</summary>
        public static object GetDefByGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            try
            {
                Ensure();
                if (!_ready) return null;
                var repo = Repo();
                if (repo == null) return null;
                return _getDef.Invoke(repo, new object[] { guid });
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] DefReflection.GetDefByGuid failed: " + ex.Message); return null; }
        }
    }
}
