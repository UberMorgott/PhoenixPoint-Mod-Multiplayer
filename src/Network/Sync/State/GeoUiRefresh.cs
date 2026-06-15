using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multipleer.Network.Sync.State
{
    /// <summary>
    /// Best-effort: after a client state-channel apply, force the matching geoscape UI module to
    /// rebuild IF it is currently open. The modules do not live-update from model events (they rebuild
    /// only on <c>Init</c>/open), so a reflective state write is invisible until re-enter unless we
    /// re-drive the module. Correctness comes from the echo itself; this only fixes visible staleness,
    /// so every path is wrapped in try/catch and no-ops on any failure.
    ///
    /// Verified against the decompile (2026-06-16):
    ///   • <c>GeoLevelController.View</c> (public field, GeoLevelController.cs:101) → <c>GeoscapeView</c>.
    ///   • <c>GeoscapeView.GeoscapeModules</c> (public field, GeoscapeView.cs:61) → <c>GeoscapeModulesData</c>.
    ///   • <c>GeoscapeModulesData.ManufacturingModule</c> (public field, :54) → <c>UIModuleManufacturing</c>
    ///     (a <c>UIModuleBehavior : MonoBehaviour</c>).
    ///   • refresh = re-invoke <c>UIModuleManufacturing.Init(GeoscapeViewContext ctx, …)</c> (:337); the
    ///     module caches its <c>_context</c> (private field, :196) after the first Init, so we read that
    ///     back and pass it — no need to reconstruct the context. Remaining Init params are optional;
    ///     pass each parameter's compiled default (so we replicate the open state's filter/mode defaults).
    ///   • "is open" = the module GameObject is active in hierarchy (UIModuleBehavior toggles its
    ///     GameObject active per UI state, UIModuleBehavior.cs:34-38).
    /// </summary>
    public static class GeoUiRefresh
    {
        /// <summary>Screen ids for channel→UI mapping. Inventory→Manufacturing (incr. A); Research (incr. C).</summary>
        public enum Screen { Manufacturing, Research }

        private static bool _ready;
        private static FieldInfo _viewField;          // GeoLevelController.View
        private static FieldInfo _modulesField;       // GeoscapeView.GeoscapeModules
        private static FieldInfo _manufModuleField;   // GeoscapeModulesData.ManufacturingModule
        private static FieldInfo _manufContextField;  // UIModuleManufacturing._context (private)
        private static MethodInfo _manufInit;         // UIModuleManufacturing.Init(...)
        private static FieldInfo _researchModuleField;  // GeoscapeModulesData.ResearchModule
        private static FieldInfo _researchContextField; // UIModuleResearch._context (private)
        private static MethodInfo _researchInit;        // UIModuleResearch.Init(GeoscapeViewContext)

        private static void Ensure(GeoRuntime rt)
        {
            if (_ready) return;
            var geoType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoLevelController");
            var viewType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.GeoscapeView");
            var modulesType = AccessTools.TypeByName("Base.UI.GeoscapeModulesData");
            var manufType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleManufacturing");
            var researchType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleResearch");
            if (geoType == null || viewType == null || modulesType == null || manufType == null) return;

            _viewField = AccessTools.Field(geoType, "View");
            _modulesField = AccessTools.Field(viewType, "GeoscapeModules");
            _manufModuleField = AccessTools.Field(modulesType, "ManufacturingModule");
            _manufContextField = AccessTools.Field(manufType, "_context");
            // Init has 5 params (1 required + 4 optional); resolve by name + first param.
            foreach (var m in manufType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "Init") continue;
                _manufInit = m;
                break;
            }

            // Research module (best-effort; absence must not break the Manufacturing refresh).
            if (researchType != null)
            {
                _researchModuleField = AccessTools.Field(modulesType, "ResearchModule");
                _researchContextField = AccessTools.Field(researchType, "_context");
                // UIModuleResearch.Init(GeoscapeViewContext context) — single required param (:172).
                foreach (var m in researchType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != "Init") continue;
                    _researchInit = m;
                    break;
                }
            }

            _ready = _viewField != null && _modulesField != null && _manufModuleField != null
                     && _manufContextField != null && _manufInit != null;
        }

        /// <summary>Refresh the given screen if open. Never throws.</summary>
        public static void Refresh(GeoRuntime rt, Screen screen)
        {
            try
            {
                Ensure(rt);
                if (!_ready) return;
                switch (screen)
                {
                    case Screen.Manufacturing: RefreshManufacturing(rt); break;
                    case Screen.Research: RefreshResearch(rt); break;
                }
            }
            catch (Exception ex) { Debug.LogWarning("[Multipleer] GeoUiRefresh.Refresh best-effort failed: " + ex.Message); }
        }

        private static void RefreshManufacturing(GeoRuntime rt)
        {
            var geo = rt?.GeoLevel();
            if (geo == null) return;
            var view = _viewField.GetValue(geo);
            if (view == null) return;
            var modules = _modulesField.GetValue(view);
            if (modules == null) return;
            var module = _manufModuleField.GetValue(modules);
            if (module == null) return;
            if (!IsOpen(module)) return;

            var context = _manufContextField.GetValue(module);
            if (context == null) return; // never opened yet → nothing cached to re-init with

            // Re-invoke Init(context, <defaults for the rest>) to rebuild item list + queue from model.
            var ps = _manufInit.GetParameters();
            var args = new object[ps.Length];
            args[0] = context;
            for (int i = 1; i < ps.Length; i++)
                args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
            _manufInit.Invoke(module, args);
        }

        private static void RefreshResearch(GeoRuntime rt)
        {
            if (_researchModuleField == null || _researchContextField == null || _researchInit == null) return;
            var geo = rt?.GeoLevel();
            if (geo == null) return;
            var view = _viewField.GetValue(geo);
            if (view == null) return;
            var modules = _modulesField.GetValue(view);
            if (modules == null) return;
            var module = _researchModuleField.GetValue(modules);
            if (module == null) return;
            if (!IsOpen(module)) return;

            var context = _researchContextField.GetValue(module);
            if (context == null) return; // never opened yet → nothing cached to re-init with

            // Re-invoke Init(context) to rebuild ShowAvailable + SetupQueue from the model (UIModuleResearch.cs:172).
            var ps = _researchInit.GetParameters();
            var args = new object[ps.Length];
            args[0] = context;
            for (int i = 1; i < ps.Length; i++)
                args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
            _researchInit.Invoke(module, args);
        }

        private static bool IsOpen(object module)
        {
            // UIModuleBehavior : MonoBehaviour → ((Component)module).gameObject.activeInHierarchy.
            var comp = module as Component;
            return comp != null && comp.gameObject != null && comp.gameObject.activeInHierarchy;
        }
    }
}
