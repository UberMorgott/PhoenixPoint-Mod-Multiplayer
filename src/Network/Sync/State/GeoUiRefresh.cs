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
    ///   • Base layout: <c>GeoscapeModulesData.BaseLayoutModule</c> (public field, :42) →
    ///     <c>UIModuleBaseLayout : UIModuleBehavior</c> (:27); re-Init =
    ///     <c>Init(GeoPhoenixBase pxBase, GeoscapeViewContext context, bool forceBaseLayoutRebuild = true)</c>
    ///     (:281; default true rebuilds the facility grid), reading back the cached <c>PxBase</c> (public
    ///     getter, :191) + <c>_context</c> (private field, :152).
    /// </summary>
    public static class GeoUiRefresh
    {
        /// <summary>Screen ids for channel→UI mapping. Inventory→Manufacturing (incr. A); Research (incr. C); BaseLayout = facility grid.</summary>
        public enum Screen { Manufacturing, Research, BaseLayout }

        private static bool _ready;
        private static FieldInfo _viewField;          // GeoLevelController.View
        private static FieldInfo _modulesField;       // GeoscapeView.GeoscapeModules
        private static FieldInfo _manufModuleField;   // GeoscapeModulesData.ManufacturingModule
        private static FieldInfo _manufContextField;  // UIModuleManufacturing._context (private)
        private static MethodInfo _manufInit;         // UIModuleManufacturing.Init(...)
        private static FieldInfo _researchModuleField;  // GeoscapeModulesData.ResearchModule
        private static FieldInfo _researchContextField; // UIModuleResearch._context (private)
        private static MethodInfo _researchInit;        // UIModuleResearch.Init(GeoscapeViewContext)
        private static FieldInfo _baseLayoutModuleField;  // GeoscapeModulesData.BaseLayoutModule
        private static FieldInfo _baseLayoutContextField; // UIModuleBaseLayout._context (private)
        private static PropertyInfo _baseLayoutPxBaseProp; // UIModuleBaseLayout.PxBase (public getter)
        private static MethodInfo _baseLayoutInit;         // UIModuleBaseLayout.Init(GeoPhoenixBase, GeoscapeViewContext, bool=true)

        // ─── Persistent bars (do NOT rebuild from a reflective model write) ──
        private static FieldInfo _infoBarModuleField;     // GeoscapeModulesData.ResourcesModule → UIModuleInfoBar
        private static FieldInfo _infoBarContextField;    // UIModuleInfoBar._context (private GeoscapeViewContext)
        private static MethodInfo _infoBarUpdateResource; // UIModuleInfoBar.UpdateResourceInfo(GeoFaction, bool) (private)
        private static PropertyInfo _ctxViewerFactionProp; // GeoscapeViewContext.ViewerFaction (public getter → GeoFaction)
        private static FieldInfo _sectionBarModuleField;  // GeoscapeModulesData.GeoSectionBarModule → UIModuleGeoSectionBar
        private static FieldInfo _sectionBarContextField; // UIModuleGeoSectionBar._context (private GeoscapeViewContext)
        private static MethodInfo _sectionBarUpdateProgress; // UIModuleGeoSectionBar.UpdateProgressBar(UIGeoSection) (public)
        private static object _uiGeoSectionResearch;      // boxed UIGeoSection.Research enum value

        private static void Ensure(GeoRuntime rt)
        {
            if (_ready) return;
            var geoType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoLevelController");
            var viewType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.GeoscapeView");
            var modulesType = AccessTools.TypeByName("Base.UI.GeoscapeModulesData");
            var manufType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleManufacturing");
            var researchType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleResearch");
            var baseLayoutType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleBaseLayout");
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

            // Base-layout facility grid (best-effort; absence must not break Manufacturing/Research refresh).
            if (baseLayoutType != null)
            {
                _baseLayoutModuleField = AccessTools.Field(modulesType, "BaseLayoutModule");
                _baseLayoutContextField = AccessTools.Field(baseLayoutType, "_context");
                _baseLayoutPxBaseProp = AccessTools.Property(baseLayoutType, "PxBase");
                // UIModuleBaseLayout.Init(GeoPhoenixBase pxBase, GeoscapeViewContext context,
                // bool forceBaseLayoutRebuild = true) — forceBaseLayoutRebuild defaults true → rebuilds the grid.
                foreach (var m in baseLayoutType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != "Init") continue;
                    _baseLayoutInit = m;
                    break;
                }
            }

            // Persistent geoscape bars (wallet info bar + bottom section progress bar). Unlike the screen
            // modules above, these stay open across the whole geoscape but only repaint from native model
            // events (Wallet.ResourcesChanged / the hourly progress coroutine), which a reflective host-echo
            // apply does not trip — so the client shows stale money/progress until its next local action. We
            // re-drive the modules' own native repaint methods. Best-effort: absence must not break the
            // screen-module refreshes above, so this is gated independently and never affects `_ready`.
            var infoBarType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleInfoBar");
            var ctxType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.GeoscapeViewContext");
            if (infoBarType != null && ctxType != null)
            {
                _infoBarModuleField = AccessTools.Field(modulesType, "ResourcesModule");
                _infoBarContextField = AccessTools.Field(infoBarType, "_context");
                // UIModuleInfoBar.UpdateResourceInfo(GeoFaction faction, bool useAnimation) — single overload (:388).
                _infoBarUpdateResource = AccessTools.Method(infoBarType, "UpdateResourceInfo");
                _ctxViewerFactionProp = AccessTools.Property(ctxType, "ViewerFaction");
            }
            var sectionBarType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleGeoSectionBar");
            var sectionEnumType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIGeoSection");
            if (sectionBarType != null && sectionEnumType != null)
            {
                _sectionBarModuleField = AccessTools.Field(modulesType, "GeoSectionBarModule");
                _sectionBarContextField = AccessTools.Field(sectionBarType, "_context");
                // UIModuleGeoSectionBar.UpdateProgressBar(UIGeoSection geoSection) — public, reads model (:347).
                _sectionBarUpdateProgress = AccessTools.Method(sectionBarType, "UpdateProgressBar", new[] { sectionEnumType });
                try { _uiGeoSectionResearch = Enum.Parse(sectionEnumType, "Research"); }
                catch { _uiGeoSectionResearch = null; }
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
                    case Screen.BaseLayout: RefreshBaseLayout(rt); break;
                }
            }
            catch (Exception ex) { Debug.LogWarning("[Multipleer] GeoUiRefresh.Refresh best-effort failed: " + ex.Message); }
        }

        /// <summary>
        /// Single fan-out over every "needs-kick" geoscape module (those that rebuild only on Init, not from
        /// model events): Research + Manufacturing + BaseLayout. Both host and client drive this after a synced
        /// apply. Add one line here to cover a new needs-kick screen everywhere. Each Refresh no-ops if closed.
        /// </summary>
        public static void RefreshNeedsKick(GeoRuntime rt)
        {
            Refresh(rt, Screen.Research);
            Refresh(rt, Screen.Manufacturing);
            Refresh(rt, Screen.BaseLayout);
        }

        /// <summary>
        /// Re-drive the two PERSISTENT geoscape bars after a CLIENT-side synced apply: the top wallet/
        /// resource info bar (UIModuleInfoBar.UpdateResourceInfo) and the bottom section bar's Research
        /// progress segment (UIModuleGeoSectionBar.UpdateProgressBar). These stay open across the whole
        /// geoscape but only repaint from native model events — a reflective host-echo apply doesn't trip
        /// those, so they show stale money/progress until the next local action. Inbound sync is delivered
        /// on the Unity main thread (the transport drains its receive queue inside Update()), so calling
        /// these UI methods directly here is main-thread-safe. Every step is null-guarded + IsOpen-gated +
        /// try/catch → a safe no-op when no geoscape view is shown (and harmless if ever run on the host).
        /// </summary>
        public static void RefreshPersistentBars(GeoRuntime rt)
        {
            try
            {
                Ensure(rt);
                RefreshWalletBar(rt);
                RefreshResearchProgressBar(rt);
            }
            catch (Exception ex) { Debug.LogWarning("[Multipleer] GeoUiRefresh.RefreshPersistentBars best-effort failed: " + ex.Message); }
        }

        private static void RefreshWalletBar(GeoRuntime rt)
        {
            if (_infoBarModuleField == null || _infoBarContextField == null
                || _infoBarUpdateResource == null || _ctxViewerFactionProp == null) return;
            var geo = rt?.GeoLevel();
            if (geo == null) return;
            var view = _viewField.GetValue(geo);
            if (view == null) return;
            var modules = _modulesField.GetValue(view);
            if (modules == null) return;
            var module = _infoBarModuleField.GetValue(modules);
            if (module == null) return;
            if (!IsOpen(module)) return;

            var context = _infoBarContextField.GetValue(module);
            if (context == null) return; // never opened yet → nothing cached
            var faction = _ctxViewerFactionProp.GetValue(context, null);
            if (faction == null) return;

            // UIModuleInfoBar.UpdateResourceInfo(viewerFaction, useAnimation:false) — repaint resource text now.
            _infoBarUpdateResource.Invoke(module, new object[] { faction, false });
        }

        private static void RefreshResearchProgressBar(GeoRuntime rt)
        {
            if (_sectionBarModuleField == null || _sectionBarContextField == null
                || _sectionBarUpdateProgress == null || _uiGeoSectionResearch == null) return;
            var geo = rt?.GeoLevel();
            if (geo == null) return;
            var view = _viewField.GetValue(geo);
            if (view == null) return;
            var modules = _modulesField.GetValue(view);
            if (modules == null) return;
            var module = _sectionBarModuleField.GetValue(modules);
            if (module == null) return;
            if (!IsOpen(module)) return;

            // UpdateProgressBar reads _context.ViewerFaction.Research.Progress; a null _context would NPE.
            var context = _sectionBarContextField.GetValue(module);
            if (context == null) return; // never opened yet → nothing cached

            // UIModuleGeoSectionBar.UpdateProgressBar(UIGeoSection.Research) — repaint the research segment.
            _sectionBarUpdateProgress.Invoke(module, new object[] { _uiGeoSectionResearch });
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

        private static void RefreshBaseLayout(GeoRuntime rt)
        {
            if (_baseLayoutModuleField == null || _baseLayoutContextField == null
                || _baseLayoutPxBaseProp == null || _baseLayoutInit == null) return;
            var geo = rt?.GeoLevel();
            if (geo == null) return;
            var view = _viewField.GetValue(geo);
            if (view == null) return;
            var modules = _modulesField.GetValue(view);
            if (modules == null) return;
            var module = _baseLayoutModuleField.GetValue(modules);
            if (module == null) return;
            if (!IsOpen(module)) return;

            // Read back the cached owner base + context (set on the last open Init); no-op if either is null.
            var pxBase = _baseLayoutPxBaseProp.GetValue(module, null);
            if (pxBase == null) return;
            var context = _baseLayoutContextField.GetValue(module);
            if (context == null) return; // never opened yet → nothing cached to re-init with

            // Re-invoke Init(pxBase, context, <defaults for the rest>) — forceBaseLayoutRebuild defaults true,
            // so the facility grid is fully rebuilt from the model (UIModuleBaseLayout.cs:281).
            var ps = _baseLayoutInit.GetParameters();
            var args = new object[ps.Length];
            args[0] = pxBase;
            args[1] = context;
            for (int i = 2; i < ps.Length; i++)
                args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
            _baseLayoutInit.Invoke(module, args);
        }

        private static bool IsOpen(object module)
        {
            // UIModuleBehavior : MonoBehaviour → ((Component)module).gameObject.activeInHierarchy.
            var comp = module as Component;
            return comp != null && comp.gameObject != null && comp.gameObject.activeInHierarchy;
        }
    }
}
