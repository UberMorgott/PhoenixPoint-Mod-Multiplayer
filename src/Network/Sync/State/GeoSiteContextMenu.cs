using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// HOST-side reflection bridge that HIDES the geoscape site contextual menu the same way the native explore
    /// click does. See <see cref="GeoSiteContextMenuDecision"/> for the RCA: the native click-path
    /// (<c>UIStateVehicleSelected.OnContextualItemSelected</c> :431) calls <c>HideContextualMenu()</c> after
    /// <c>Activate</c>, but the host's PROGRAMMATIC apply of a client-relayed explore
    /// (<c>VehicleTravelReflection.StartExploringCurrentSite</c>) skips it, leaving a stale-active "Explore" button
    /// on the still-open menu. We reproduce that single skipped native step.
    ///
    /// Verified against the decompile (2026-07-05):
    ///   • <c>GeoLevelController.View</c> (public field) → <c>GeoscapeView</c>;
    ///     <c>GeoscapeView.GeoscapeModules</c> (public field) → <c>Base.UI.GeoscapeModulesData</c>.
    ///   • <c>GeoscapeModulesData.SiteContextualMenuModule</c> (public field, :82) →
    ///     <c>UIModuleSiteContextualMenu</c>.
    ///   • <c>UIModuleSiteContextualMenu.IsContextualMenuVisible</c> (public bool, :36),
    ///     <c>SelectedSite</c> (public GeoSite, :38), <c>HideContextualMenu()</c> (public, :138).
    ///   • <c>GeoSite.SiteId</c> (public int, :45, default -1).
    /// All reflection is null-safe / best-effort — a missing member no-ops (never throws into game code).
    /// </summary>
    public static class GeoSiteContextMenu
    {
        private static bool _ready;
        private static FieldInfo _viewField;          // GeoLevelController.View
        private static FieldInfo _modulesField;       // GeoscapeView.GeoscapeModules
        private static FieldInfo _ctxMenuModuleField; // GeoscapeModulesData.SiteContextualMenuModule
        private static FieldInfo _visibleField;       // UIModuleSiteContextualMenu.IsContextualMenuVisible (bool)
        private static FieldInfo _selectedSiteField;  // UIModuleSiteContextualMenu.SelectedSite (GeoSite)
        private static MethodInfo _hideMethod;        // UIModuleSiteContextualMenu.HideContextualMenu()
        private static FieldInfo _siteIdField;        // GeoSite.SiteId (int)

        private static void Ensure(GeoRuntime rt)
        {
            if (_ready) return;
            var geoType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoLevelController");
            var viewType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.GeoscapeView");
            var modulesType = AccessTools.TypeByName("Base.UI.GeoscapeModulesData");
            var ctxMenuType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleSiteContextualMenu");
            var siteType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
            if (geoType == null || viewType == null || modulesType == null || ctxMenuType == null || siteType == null) return;

            _viewField = AccessTools.Field(geoType, "View");
            _modulesField = AccessTools.Field(viewType, "GeoscapeModules");
            _ctxMenuModuleField = AccessTools.Field(modulesType, "SiteContextualMenuModule");
            _visibleField = AccessTools.Field(ctxMenuType, "IsContextualMenuVisible");
            _selectedSiteField = AccessTools.Field(ctxMenuType, "SelectedSite");
            _hideMethod = AccessTools.Method(ctxMenuType, "HideContextualMenu");
            _siteIdField = AccessTools.Field(siteType, "SiteId");

            _ready = _viewField != null && _modulesField != null && _ctxMenuModuleField != null
                     && _visibleField != null && _selectedSiteField != null && _hideMethod != null
                     && _siteIdField != null;
        }

        /// <summary>HOST: after applying a relayed explore for the site with <paramref name="exploredSiteId"/>, hide
        /// the site contextual menu IF it is currently open on that same site — the exact
        /// <c>HideContextualMenu()</c> the native click-path runs after <c>Activate</c>. No-op on any miss.</summary>
        public static void HideIfShowingSite(GeoRuntime rt, int exploredSiteId)
        {
            try
            {
                Ensure(rt);
                if (!_ready) return;
                var geo = rt?.GeoLevel();
                if (geo == null) return;
                var view = _viewField.GetValue(geo);
                if (view == null) return;
                var modules = _modulesField.GetValue(view);
                if (modules == null) return;
                var module = _ctxMenuModuleField.GetValue(modules);
                if (module == null) return;

                bool visible;
                try { visible = (bool)_visibleField.GetValue(module); } catch { visible = false; }

                int selectedSiteId = -1;
                var site = _selectedSiteField.GetValue(module);
                if (site != null)
                {
                    try { selectedSiteId = Convert.ToInt32(_siteIdField.GetValue(site)); } catch { selectedSiteId = -1; }
                }

                if (GeoSiteContextMenuDecision.ShouldHide(visible, selectedSiteId, exploredSiteId))
                {
                    _hideMethod.Invoke(module, null);
                    Debug.Log("[Multiplayer][geo] host explore: hid stale site contextual menu for site " + exploredSiteId
                        + " (native HideContextualMenu, matches click-path)");
                }
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer] GeoSiteContextMenu.HideIfShowingSite best-effort failed: " + ex.Message); }
        }
    }
}
