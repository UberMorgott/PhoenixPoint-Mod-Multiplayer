using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
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
        /// <summary>Screen ids for channel→UI mapping. Inventory→Manufacturing (incr. A); Research (incr. C);
        /// BaseLayout = facility grid; RosterEquip = soldier-equipment/inventory/progression edit screen
        /// (UIStateEditSoldier); RosterOverview = soldier roster list (UIStateGeoRoster); Containment =
        /// alien-containment (UIStateRosterAliens); Recruits = recruit-hire (UIStateRosterRecruits);
        /// VehicleEquip = ground-vehicle equipment edit screen (UIStateEditVehicle).</summary>
        public enum Screen { Manufacturing, Research, BaseLayout, RosterEquip, RosterOverview, Containment, Recruits, VehicleEquip }

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
                    case Screen.RosterEquip: RefreshRosterEquip(rt); break;
                    case Screen.RosterOverview: RefreshRosterOverview(rt); break;
                    case Screen.Containment: RefreshContainment(rt); break;
                    case Screen.Recruits: RefreshRecruits(rt); break;
                    case Screen.VehicleEquip: RefreshVehicleEquip(rt); break;
                }
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer] GeoUiRefresh.Refresh best-effort failed: " + ex.Message); }
        }

        /// <summary>
        /// Single fan-out over every CHEAP "needs-kick" geoscape screen (those that rebuild only on Init, not
        /// from model events): Research + Manufacturing + BaseLayout + RosterEquip (soldier equip/storage/
        /// progression) + VehicleEquip (ground-vehicle equip/storage) + RosterOverview (soldier roster list).
        /// Both host and client drive this after a synced
        /// apply. Add one line here to cover a new needs-kick screen everywhere. Each Refresh no-ops if closed.
        /// NOTE: the two POOL screens (Containment / Recruits) are deliberately NOT here — their only native
        /// refresh idiom is a full state re-enter (too heavy to run on every action/channel apply); they are
        /// driven targeted on their #10 carrier channel + the host's Recruitment-category apply instead.
        /// </summary>
        public static void RefreshNeedsKick(GeoRuntime rt)
        {
            Refresh(rt, Screen.Research);
            Refresh(rt, Screen.Manufacturing);
            Refresh(rt, Screen.BaseLayout);
            Refresh(rt, Screen.RosterEquip);
            Refresh(rt, Screen.VehicleEquip);
            Refresh(rt, Screen.RosterOverview);
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
                // FORCE the wallet bar: a mirrored wallet delta (event-choice reward, income tick) must repaint the
                // resource bar the instant it lands, even while a geoscape EVENT MODAL is open. The event state
                // does MainUILayer.SetActiveState("GeoscapeEvent") (UIStateGeoscapeEvent.cs:50), so the info bar
                // is behind the event layer and its IsOpen (activeInHierarchy) gate would otherwise SKIP the
                // repaint until the modal closes — the "reward looks ungranted while the modal is up" lag (W1.2,
                // reactivity mandate: repaint on message arrival, never deferred).
                RefreshWalletBar(rt, force: true);
                RefreshResearchProgressBar(rt);
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer] GeoUiRefresh.RefreshPersistentBars best-effort failed: " + ex.Message); }
        }

        /// <summary>Repaint the persistent top resource bar (UIModuleInfoBar.UpdateResourceInfo) from the current
        /// model. <paramref name="force"/> = repaint even when the module is not <c>activeInHierarchy</c> (behind
        /// an open event modal): setting the label text while hidden persists it so the bar is already correct the
        /// instant the modal closes — never deferred. Self-Ensured + self-guarded → safe to call directly from a
        /// channel apply; a no-op when no geoscape view / info bar is live.</summary>
        public static void RefreshWalletBar(GeoRuntime rt, bool force = false)
        {
            try
            {
                Ensure(rt);
                if (_infoBarModuleField == null || _infoBarContextField == null
                    || _infoBarUpdateResource == null || _ctxViewerFactionProp == null
                    || _viewField == null || _modulesField == null) return;
                var geo = rt?.GeoLevel();
                if (geo == null) return;
                var view = _viewField.GetValue(geo);
                if (view == null) return;
                var modules = _modulesField.GetValue(view);
                if (modules == null) return;
                var module = _infoBarModuleField.GetValue(modules);
                if (module == null) return;
                if (!force && !IsOpen(module)) return;   // gated by default; forced past an open-modal layer swap

                var context = _infoBarContextField.GetValue(module);
                if (context == null) return; // never opened yet → nothing cached (guard holds even when forced)
                var faction = _ctxViewerFactionProp.GetValue(context, null);
                if (faction == null) return;

                // UIModuleInfoBar.UpdateResourceInfo(viewerFaction, useAnimation:false) — pure display re-read
                // (wallet + ResourceIncome + working-facility tally); no model mutation, no events raised.
                _infoBarUpdateResource.Invoke(module, new object[] { faction, false });
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer] GeoUiRefresh.RefreshWalletBar best-effort failed: " + ex.Message); }
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

        // ─── Roster family screens (UIStateEditSoldier / UIStateGeoRoster / RosterAliens / RosterRecruits) ───
        // These are EDITOR-BUFFER / read-on-open screens that subscribe to NO model change-event, so a mirrored
        // personnel/pool write (#9 blob ApplySoldierState, host relayed SetItems/progression, #10 pool) is
        // invisible until the player switches soldier / reopens. We re-drive each screen's OWN native read path
        // so an open screen repaints from the freshly-stamped model. Decompile-verified 2026-07-07:
        //   • GeoscapeView.CurrentViewState (getter, GeoscapeView.cs:193); GeoscapeModulesData fields
        //     SoldierEquipModule (:110) / CharacterProgressionModule (:38) / ActorCycleModule (:114).
        //   • EditSoldier equip: UIModuleSoldierEquip.UpdateData(inv,ready,armour,storage,slots) (:626) — as
        //     DisplaySoldier (:582); echo-safe (UIInventoryList.SetItems (:118) never fires OnSlotItemChanged /
        //     GeoCharacter.SetItems → cannot re-enter SetItemsEditRelayPatch). Storage: state RefreshStorage()
        //     (:587) after _refreshStorage=true forces a model re-read of the faction/site ItemStorage (#1).
        //   • EditSoldier progression: UIModuleCharacterProgression.SetCharacterProgression(GeoFaction,char)
        //     (:463) re-reads model; it RESETS the edit buffer, so SKIP when the viewer has an unconfirmed local
        //     allocation — IsCharacterChanged() (:358, pure) or _boughtAbilitySlot!=null (:241). Never-silent.
        //   • EditSoldier header: UIModuleActorCycle.RefreshSoldierInfo() (:514) — cheap name/class/level/
        //     corruption + button costs, no animation reset. The 3D armour mesh (DisplaySoldier, resets pose)
        //     is intentionally NOT re-driven (visible glitch every peer edit > slightly-stale mesh).
        //   • Roster list: UIStateGeoRoster.OnActorStatChanged (:364) re-Inits the portrait/HP-bar list +
        //     unit-stats; the reflective HP write never fires Health.StatChangeEvent so it never runs. Args ignored.
        //   • Pool screens have NO in-place refresh — the game's own idiom is a full state re-enter:
        //     GeoscapeView.ToAlienContainmentState(bool)/ToPhoenixRecruitsState(bool) (:608/:639), used verbatim
        //     after kill/harvest (UIStateRosterAliens.cs:260/280/301). Gated so it only fires while that screen
        //     is current (never opens it). SoldierEquipModule is SHARED with UIStateEditVehicle, so every path
        //     gates on the exact CurrentViewState type first. Best-effort; any miss no-ops.
        private static bool _rosterFamilyEnsured;
        private static PropertyInfo _currentViewStateProp;   // GeoscapeView.CurrentViewState (getter)
        private static Type _editSoldierType;                // UIStateEditSoldier
        private static FieldInfo _editSoldierCharField;      // UIStateEditSoldier._currentCharacter (private GeoCharacter)
        private static FieldInfo _editSoldierRefreshStorageField; // UIStateEditSoldier._refreshStorage
        private static MethodInfo _editSoldierRefreshStorage;    // UIStateEditSoldier.RefreshStorage()
        private static FieldInfo _soldierEquipModuleField;   // GeoscapeModulesData.SoldierEquipModule
        private static MethodInfo _soldierEquipUpdateData;   // UIModuleSoldierEquip.UpdateData(inv,ready,armour,storage,slots)
        private static PropertyInfo _charInventoryItemsProp; // GeoCharacter.InventoryItems
        private static PropertyInfo _charEquipmentItemsProp; // GeoCharacter.EquipmentItems
        private static PropertyInfo _charArmourItemsProp;    // GeoCharacter.ArmourItems
        private static FieldInfo _progModuleField;           // GeoscapeModulesData.CharacterProgressionModule
        private static MethodInfo _isCharacterChanged;       // UIModuleCharacterProgression.IsCharacterChanged()
        private static FieldInfo _boughtAbilitySlotField;    // UIModuleCharacterProgression._boughtAbilitySlot
        private static MethodInfo _setCharacterProgression;  // UIModuleCharacterProgression.SetCharacterProgression(GeoFaction,char)
        private static MethodInfo _refreshStatPanel;         // UIModuleCharacterProgression.RefreshStatPanel() (repaints panel from buffer; NO buffer reset)
        private static FieldInfo _progHasPandoranField;      // UIModuleCharacterProgression._hasPandoranProgression (mutagen path → no SP pool)
        private static FieldInfo _progPhoenixFactionField;   // UIModuleCharacterProgression._phoenixFaction (GeoPhoenixFaction, cached on open)
        private static FieldInfo _progStartingFactionPointsField; // UIModuleCharacterProgression._startingFactionPoints (int)
        private static FieldInfo _progCurrentFactionPointsField;  // UIModuleCharacterProgression._currentFactionPoints (int)
        private static FieldInfo _phoenixFactionSkillpointsField; // GeoPhoenixFaction.Skillpoints (int, shared faction-SP pool)
        private static PropertyInfo _viewerFactionProp;      // GeoLevelController.ViewerFaction
        private static FieldInfo _actorCycleModuleField;     // GeoscapeModulesData.ActorCycleModule
        private static MethodInfo _refreshSoldierInfo;       // UIModuleActorCycle.RefreshSoldierInfo()
        private static Type _rosterOverviewType;             // UIStateGeoRoster
        private static MethodInfo _onActorStatChanged;       // UIStateGeoRoster.OnActorStatChanged(BaseStat,StatChangeType,float,float)
        private static object _statChangeTypeZero;           // boxed StatChangeType default (the handler ignores its args)
        private static Type _rosterAliensType;               // UIStateRosterAliens
        private static MethodInfo _toAlienContainment;       // GeoscapeView.ToAlienContainmentState(bool)
        private static Type _rosterRecruitsType;             // UIStateRosterRecruits
        private static MethodInfo _toPhoenixRecruits;        // GeoscapeView.ToPhoenixRecruitsState(bool)
        // Ground-vehicle equip screen (UIStateEditVehicle) — shares SoldierEquipModule but drives the VEHICLE
        // slot layout (weapon/hull/engine) via UpdateVehicleData, so it needs its own re-drive path.
        private static Type _editVehicleType;                // UIStateEditVehicle
        private static FieldInfo _editVehicleCharField;      // UIStateEditVehicle._currentCharacter
        private static FieldInfo _editVehicleRefreshNeededField; // UIStateEditVehicle._uiRefreshNeeded
        private static FieldInfo _editVehicleRefreshStorageField; // UIStateEditVehicle._refreshStorage
        private static MethodInfo _editVehicleRefreshStorage;    // UIStateEditVehicle.RefreshStorage()
        private static MethodInfo _soldierEquipUpdateVehicleData;// UIModuleSoldierEquip.UpdateVehicleData(inv,storage,weapon,hull,engine,slots)
        private static Type _iCommonItemType;                // PhoenixPoint.Common.Entities.ICommonItem (list element)
        private static Type _iVehicleEquipmentType;          // Code.PhoenixPoint.Tactical.Entities.Equipments.IVehicleEquipment
        private static MethodInfo _getEquipmentType;         // IVehicleEquipment.GetEquipmentType() → GroundVehicleEquipmentType
        private static object _gveWeapon, _gveHull, _gveEngine;  // boxed GroundVehicleEquipmentType values
        private static Type _geoItemType2;                   // PhoenixPoint.Geoscape.Entities.GeoItem
        private static PropertyInfo _geoItemDefProp2;        // GeoItem.ItemDef
        private static PropertyInfo _charStatsProp;          // GeoCharacter.CharacterStats
        private static MethodInfo _getInventorySlots;        // CharacterStats.GetInventorySlots()
        // One-shot per-apply stamp for the progression panel (mirrors PersonnelChannel.LastStateApplyUnitIds):
        // armed by the caller that knows WHAT the apply changed (client #9 / host stat-SP intent), consumed by
        // the next RefreshRosterEquip. Unarmed calls keep the legacy conservative repaint-when-idle behavior.
        private static bool _progressionStampArmed;
        private static IReadOnlyList<long> _progressionStampIds;
        private static bool _progressionStampFactionSp;

        private static void EnsureRosterFamily()
        {
            if (_rosterFamilyEnsured) return;
            _rosterFamilyEnsured = true;
            try
            {
                var viewType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.GeoscapeView");
                var modulesType = AccessTools.TypeByName("Base.UI.GeoscapeModulesData");
                var soldierEquipType = AccessTools.TypeByName("PhoenixPoint.Common.View.ViewModules.UIModuleSoldierEquip");
                _editSoldierType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewStates.UIStateEditSoldier");
                var charType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoCharacter");
                if (viewType == null || modulesType == null || soldierEquipType == null || _editSoldierType == null || charType == null) return;
                _currentViewStateProp = AccessTools.Property(viewType, "CurrentViewState");
                _editSoldierCharField = AccessTools.Field(_editSoldierType, "_currentCharacter");
                _editSoldierRefreshStorageField = AccessTools.Field(_editSoldierType, "_refreshStorage");
                _editSoldierRefreshStorage = AccessTools.Method(_editSoldierType, "RefreshStorage");
                _soldierEquipModuleField = AccessTools.Field(modulesType, "SoldierEquipModule");
                _soldierEquipUpdateData = AccessTools.Method(soldierEquipType, "UpdateData");
                _charInventoryItemsProp = AccessTools.Property(charType, "InventoryItems");
                _charEquipmentItemsProp = AccessTools.Property(charType, "EquipmentItems");
                _charArmourItemsProp = AccessTools.Property(charType, "ArmourItems");

                // progression panel (best-effort; absence must not break the equip re-drive)
                var progType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleCharacterProgression");
                if (progType != null)
                {
                    _progModuleField = AccessTools.Field(modulesType, "CharacterProgressionModule");
                    _isCharacterChanged = AccessTools.Method(progType, "IsCharacterChanged");
                    _boughtAbilitySlotField = AccessTools.Field(progType, "_boughtAbilitySlot");
                    _setCharacterProgression = AccessTools.Method(progType, "SetCharacterProgression");
                    // PartialRepaint plumbing: refresh the shared faction-SP pool label from the model without
                    // resetting the stat/ability edit buffer. RefreshStatPanel repaints from _current* (buffer),
                    // so the label reads from _currentFactionPoints — we reconcile that baseline to the live
                    // shared pool first (see PartialRepaintProgression). All best-effort; a miss degrades to
                    // "keep the buffer, refresh nothing".
                    _refreshStatPanel = AccessTools.Method(progType, "RefreshStatPanel");
                    _progHasPandoranField = AccessTools.Field(progType, "_hasPandoranProgression");
                    _progPhoenixFactionField = AccessTools.Field(progType, "_phoenixFaction");
                    _progStartingFactionPointsField = AccessTools.Field(progType, "_startingFactionPoints");
                    _progCurrentFactionPointsField = AccessTools.Field(progType, "_currentFactionPoints");
                    var phoenixFactionType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.Factions.GeoPhoenixFaction");
                    if (phoenixFactionType != null) _phoenixFactionSkillpointsField = AccessTools.Field(phoenixFactionType, "Skillpoints");
                }
                var geoType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoLevelController");
                if (geoType != null) _viewerFactionProp = AccessTools.Property(geoType, "ViewerFaction");

                // soldier header (best-effort)
                var actorCycleType = AccessTools.TypeByName("PhoenixPoint.Common.View.ViewModules.UIModuleActorCycle");
                if (actorCycleType != null)
                {
                    _actorCycleModuleField = AccessTools.Field(modulesType, "ActorCycleModule");
                    _refreshSoldierInfo = AccessTools.Method(actorCycleType, "RefreshSoldierInfo");
                }

                // roster list (best-effort)
                _rosterOverviewType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewStates.UIStateGeoRoster");
                if (_rosterOverviewType != null) _onActorStatChanged = AccessTools.Method(_rosterOverviewType, "OnActorStatChanged");
                var statChangeType = AccessTools.TypeByName("Base.Entities.Statuses.StatChangeType");
                if (statChangeType != null) { try { _statChangeTypeZero = Enum.ToObject(statChangeType, 0); } catch { _statChangeTypeZero = null; } }

                // pool screens (best-effort)
                _rosterAliensType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewStates.UIStateRosterAliens");
                _toAlienContainment = AccessTools.Method(viewType, "ToAlienContainmentState", new[] { typeof(bool) });
                _rosterRecruitsType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewStates.UIStateRosterRecruits");
                _toPhoenixRecruits = AccessTools.Method(viewType, "ToPhoenixRecruitsState", new[] { typeof(bool) });

                // ground-vehicle equip screen (best-effort; absence must not break the soldier re-drives)
                _editVehicleType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewStates.UIStateEditVehicle");
                if (_editVehicleType != null)
                {
                    _editVehicleCharField = AccessTools.Field(_editVehicleType, "_currentCharacter");
                    _editVehicleRefreshNeededField = AccessTools.Field(_editVehicleType, "_uiRefreshNeeded");
                    _editVehicleRefreshStorageField = AccessTools.Field(_editVehicleType, "_refreshStorage");
                    _editVehicleRefreshStorage = AccessTools.Method(_editVehicleType, "RefreshStorage");
                    // UIModuleSoldierEquip.UpdateVehicleData(inv, storage, weapon, hull, engine, slots) — 6-arg vehicle layout.
                    _soldierEquipUpdateVehicleData = AccessTools.Method(soldierEquipType, "UpdateVehicleData");
                    _iCommonItemType = AccessTools.TypeByName("PhoenixPoint.Common.Entities.ICommonItem");
                    _iVehicleEquipmentType = AccessTools.TypeByName("Code.PhoenixPoint.Tactical.Entities.Equipments.IVehicleEquipment");
                    if (_iVehicleEquipmentType != null) _getEquipmentType = AccessTools.Method(_iVehicleEquipmentType, "GetEquipmentType");
                    var gveEnum = AccessTools.TypeByName("PhoenixPoint.Common.Entities.Equipments.GroundVehicleEquipmentType");
                    if (gveEnum != null)
                    {
                        try { _gveWeapon = Enum.Parse(gveEnum, "Weapon"); _gveHull = Enum.Parse(gveEnum, "Hull"); _gveEngine = Enum.Parse(gveEnum, "Engine"); }
                        catch { _gveWeapon = _gveHull = _gveEngine = null; }
                    }
                    _geoItemType2 = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoItem");
                    if (_geoItemType2 != null) _geoItemDefProp2 = AccessTools.Property(_geoItemType2, "ItemDef");
                    _charStatsProp = AccessTools.Property(charType, "CharacterStats");
                    var charStatsType = AccessTools.TypeByName("PhoenixPoint.Common.Entities.CharacterStats");
                    if (charStatsType != null) _getInventorySlots = AccessTools.Method(charStatsType, "GetInventorySlots");
                }
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer] GeoUiRefresh.EnsureRosterFamily failed: " + ex.Message); }
        }

        /// <summary>Resolve the active geoscape view state (null when no geoscape view / not resolved).</summary>
        private static object CurrentViewState(GeoRuntime rt)
        {
            if (_currentViewStateProp == null || _viewField == null) return null;
            var geo = rt?.GeoLevel();
            if (geo == null) return null;
            var view = _viewField.GetValue(geo);
            return view == null ? null : _currentViewStateProp.GetValue(view, null);
        }

        /// <summary>The currently-edited soldier GeoCharacter when the soldier-equip screen is the active view
        /// state, else null. Public for the v2 gesture/repaint driver (EquipMirrorRepaint).</summary>
        public static object ActiveEditSoldierCharacter(GeoRuntime rt)
        {
            try
            {
                EnsureRosterFamily();
                if (_viewField == null || _currentViewStateProp == null || _editSoldierType == null
                    || _editSoldierCharField == null) return null;
                var geo = rt?.GeoLevel();
                if (geo == null) return null;
                var view = _viewField.GetValue(geo);
                if (view == null) return null;
                var state = _currentViewStateProp.GetValue(view, null);
                if (state == null || !_editSoldierType.IsInstanceOfType(state)) return null;
                return _editSoldierCharField.GetValue(state);
            }
            catch { return null; }
        }

        /// <summary>v2 soldier-equip mirror repaint (the old (A) block, UNCONDITIONAL): re-drive the open equip
        /// doll + shared-storage lists from the freshly-stamped model via the cheapest native read paths
        /// (UIModuleSoldierEquip.UpdateData as DisplaySoldier, then RefreshStorage for #1). NO drag/_uiRefreshNeeded
        /// guard here — the caller (EquipMirrorRepaint) already gated on EditSession.ShouldDeferRepaint (drag in
        /// hand) and is CLIENT-only, so the client's equip screen is a pure mirror. Echo-safe: UpdateData/SetItems
        /// (UI) never fire GeoCharacter.SetItems, so this cannot re-enter the gesture relay. No-op when the
        /// equip screen is closed / not current. Never throws.</summary>
        public static void RepaintEquipAndStorage(GeoRuntime rt)
        {
            try
            {
                EnsureRosterFamily();
                if (_editSoldierType == null || _editSoldierCharField == null || _soldierEquipModuleField == null
                    || _soldierEquipUpdateData == null || _charInventoryItemsProp == null || _charEquipmentItemsProp == null
                    || _charArmourItemsProp == null || _modulesField == null || _viewField == null || _currentViewStateProp == null) return;
                var geo = rt?.GeoLevel();
                if (geo == null) return;
                var view = _viewField.GetValue(geo);
                if (view == null) return;
                var state = _currentViewStateProp.GetValue(view, null);
                if (state == null || !_editSoldierType.IsInstanceOfType(state)) return;   // not the soldier-equip screen
                var modules = _modulesField.GetValue(view);
                if (modules == null) return;
                var character = _editSoldierCharField.GetValue(state);
                var module = _soldierEquipModuleField.GetValue(modules);
                if (module == null || !IsOpen(module) || character == null) return;

                object inv = _charInventoryItemsProp.GetValue(character, null);
                object ready = _charEquipmentItemsProp.GetValue(character, null);
                object armour = _charArmourItemsProp.GetValue(character, null);
                _soldierEquipUpdateData.Invoke(module, new object[] { inv, ready, armour, null, 0 });
                // shared-stores panel: force a model re-read of faction/site ItemStorage (#1). RefreshStorage
                // reuses the UI's current items unless _refreshStorage is set, so set it first. Read-only.
                if (_editSoldierRefreshStorage != null && _editSoldierRefreshStorageField != null)
                {
                    _editSoldierRefreshStorageField.SetValue(state, true);
                    _editSoldierRefreshStorage.Invoke(state, null);
                }
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer] GeoUiRefresh.RepaintEquipAndStorage best-effort failed: " + ex.Message); }
        }

        // Roster-equip fan-out (from RefreshNeedsKick, BOTH peers): progression + header ONLY. The equip + storage
        // repaint is driven separately: on the CLIENT by EquipMirrorRepaint on #9/#1 applies; on the HOST by
        // the explicit RepaintEquipAndStorage call in OnActionRequest after a client equip/augment intent — so
        // the host UI lists match the model before the native per-frame UpdateSoldierEquipment flush.
        private static void RefreshRosterEquip(GeoRuntime rt)
        {
            EnsureRosterFamily();
            // Consume the one-shot progression stamp FIRST (even on early exit) so a stale stamp can never
            // mis-gate a later unrelated fan-out call. Armed only by a stat/SP-relevant apply (client #9 /
            // host stat intent); every exit below that drops an armed stamp logs why (stat-sync RCA 2026-07-10:
            // the silent-success/silent-exit ambiguity made the staleness verdict impossible from logs).
            bool stampArmed = _progressionStampArmed;
            var stampIds = _progressionStampIds;
            bool stampSp = _progressionStampFactionSp;
            _progressionStampArmed = false; _progressionStampIds = null; _progressionStampFactionSp = false;

            if (_editSoldierType == null || _editSoldierCharField == null || _modulesField == null || _viewField == null)
                { ProgressionStampDiag(stampArmed, "roster-family reflection unresolved"); return; }
            var geo = rt?.GeoLevel();
            if (geo == null) { ProgressionStampDiag(stampArmed, "no geoscape level"); return; }
            var view = _viewField.GetValue(geo);
            if (view == null) { ProgressionStampDiag(stampArmed, "no geoscape view"); return; }
            // Gate: active view state MUST be the soldier-equip screen.
            var state = _currentViewStateProp?.GetValue(view, null);
            if (state == null || !_editSoldierType.IsInstanceOfType(state))
                { ProgressionStampDiag(stampArmed, "view state is " + (state == null ? "null" : state.GetType().Name) + ", not EditSoldier"); return; }
            var modules = _modulesField.GetValue(view);
            if (modules == null) { ProgressionStampDiag(stampArmed, "no modules data"); return; }
            var character = _editSoldierCharField.GetValue(state);

            // (B) progression panel (stats / SP / abilities) — same #9 blob. A FULL re-drive
            // (SetCharacterProgression) re-reads the model and RESETS the edit buffer, so
            // ProgressionRepaintDecision gates it (reactivity mandate — never defer forever):
            //   Repaint (no pending edit) → full re-drive; ConflictRepaint (open soldier changed remotely
            //   mid-edit) → full re-drive, remote wins, local clicks discarded; PartialRepaint (a DIFFERENT
            //   soldier's change moved the shared faction-SP pool) → refresh the shared pool label only, keep
            //   the buffer; Skip → nothing on the panel changed. Never-silent for armed stamps.
            if (_progModuleField != null && _isCharacterChanged != null && _setCharacterProgression != null
                && _viewerFactionProp != null && character != null)
            {
                var progModule = _progModuleField.GetValue(modules);
                if (progModule == null || !IsOpen(progModule))
                    ProgressionStampDiag(stampArmed, progModule == null ? "progression module null" : "progression module not activeInHierarchy");
                else
                {
                    bool pendingStat = _isCharacterChanged.Invoke(progModule, null) is bool pc && pc;
                    bool pendingAbility = _boughtAbilitySlotField != null && _boughtAbilitySlotField.GetValue(progModule) != null;
                    long viewedUnitId = PersonnelReflection.ReadUnitId(character);
                    var outcome = ProgressionRepaintDecision.Decide(pendingStat || pendingAbility,
                        viewedUnitId, stampArmed ? stampIds : null, stampArmed && stampSp);
                    if (outcome == ProgressionRepaintDecision.Outcome.PartialRepaint)
                    {
                        PartialRepaintProgression(progModule);   // shared faction-SP pool label, buffer preserved
                        if (stampArmed)
                            Debug.Log("[Multiplayer] GeoUiRefresh: progression PARTIAL repaint (shared faction-SP pool) — open unit="
                                      + viewedUnitId + " not in stamped=[" + JoinIds(stampIds) + "], local edit buffer preserved");
                    }
                    else if (outcome == ProgressionRepaintDecision.Outcome.Repaint
                             || outcome == ProgressionRepaintDecision.Outcome.ConflictRepaint)
                    {
                        object viewerFaction = _viewerFactionProp.GetValue(geo, null);
                        if (viewerFaction == null) ProgressionStampDiag(stampArmed, "ViewerFaction null");
                        else
                        {
                            _setCharacterProgression.Invoke(progModule, new[] { viewerFaction, character });
                            if (outcome == ProgressionRepaintDecision.Outcome.ConflictRepaint)
                                Debug.Log("[Multiplayer] GeoUiRefresh: progression CONFLICT repaint — remote changed OPEN unit="
                                          + viewedUnitId + " (stamped=[" + JoinIds(stampIds) + "]); local uncommitted allocation discarded, remote wins");
                            else if (stampArmed)
                                Debug.Log("[Multiplayer] GeoUiRefresh: progression re-drive unit=" + viewedUnitId
                                          + " stamped=[" + JoinIds(stampIds) + "] factionSp=" + stampSp);
                        }
                    }
                    else if (stampArmed)   // Skip: apply touched neither the open unit nor the shared pool
                        Debug.Log("[Multiplayer] GeoUiRefresh: progression repaint SKIPPED — apply touched neither open unit="
                                  + viewedUnitId + " nor the shared SP pool (stamped=[" + JoinIds(stampIds) + "])");
                }
            }
            else ProgressionStampDiag(stampArmed, "progression reflection unresolved or no current character");

            // (C) soldier header (name/class/level/corruption + button costs) — cheap, no animation reset, no
            // editable buffer to clobber. The 3D armour mesh is a documented skip (DisplaySoldier resets pose).
            if (_actorCycleModuleField != null && _refreshSoldierInfo != null && character != null)
            {
                var actorCycle = _actorCycleModuleField.GetValue(modules);
                if (actorCycle != null && IsOpen(actorCycle))
                    try { _refreshSoldierInfo.Invoke(actorCycle, null); }
                    catch (Exception ex) { Debug.LogWarning("[Multiplayer] GeoUiRefresh RefreshSoldierInfo skipped: " + ex.Message); }
            }
        }

        /// <summary>Arm the one-shot progression-repaint stamp for the NEXT RefreshRosterEquip call: the unit
        /// ids the apply actually stamped (null = unknown → conservative) + whether the shared faction-SP pool
        /// changed (its total is displayed on the panel for every soldier). Callers: SyncEngine.OnStateSync on
        /// a #9 apply (client) and SyncEngine.OnActionRequest after a stat/SP intent apply (host).</summary>
        public static void SetProgressionStamp(IReadOnlyList<long> stampedUnitIds, bool factionSpChanged)
        {
            _progressionStampArmed = true;
            _progressionStampIds = stampedUnitIds;
            _progressionStampFactionSp = factionSpChanged;
        }

        /// <summary>PartialRepaint: a remote apply moved the SHARED faction-SP pool but not the open soldier, so
        /// refresh the pool label WITHOUT resetting the stat/ability edit buffer. RefreshStatPanel writes the
        /// label from <c>_currentFactionPoints</c> (buffer), so first reconcile that baseline to the live pool —
        /// shift BOTH <c>_starting/_currentFactionPoints</c> by the model delta so the user's pending spend count
        /// (current − starting) survives — then re-drive the module's own RefreshStatPanel. The per-soldier
        /// stat/SP buffer is never touched. Skipped for mutoid (mutagen-cost) progression and a non-Phoenix
        /// faction (no pooled SP). ponytail: buffer-delta reconcile, not a live label-element write — upgrade only
        /// if sub-frame pool accuracy mid-allocation ever matters. Best-effort; never throws.</summary>
        private static void PartialRepaintProgression(object progModule)
        {
            try
            {
                bool pandoran = _progHasPandoranField?.GetValue(progModule) is bool p && p;
                object pf = _progPhoenixFactionField?.GetValue(progModule);
                if (!pandoran && pf != null && _phoenixFactionSkillpointsField != null
                    && _progStartingFactionPointsField != null && _progCurrentFactionPointsField != null)
                {
                    int live = Convert.ToInt32(_phoenixFactionSkillpointsField.GetValue(pf));
                    int start = Convert.ToInt32(_progStartingFactionPointsField.GetValue(progModule));
                    int delta = live - start;
                    if (delta != 0)
                    {
                        _progStartingFactionPointsField.SetValue(progModule, live);
                        int cur = Convert.ToInt32(_progCurrentFactionPointsField.GetValue(progModule));
                        _progCurrentFactionPointsField.SetValue(progModule, cur + delta);
                    }
                }
                _refreshStatPanel?.Invoke(progModule, null);
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer] GeoUiRefresh.PartialRepaintProgression best-effort failed: " + ex.Message); }
        }

        /// <summary>Never-silent stamp accounting: a stat/SP-relevant apply armed the stamp but the repaint was
        /// not reached — say why (one line per armed stamp; stat applies are rare, user-triggered).</summary>
        private static void ProgressionStampDiag(bool stampArmed, string why)
        {
            if (stampArmed) Debug.Log("[Multiplayer] GeoUiRefresh: progression stamp dropped — " + why);
        }

        private static string JoinIds(IReadOnlyList<long> ids)
        {
            if (ids == null) return "?";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < ids.Count; i++) { if (i > 0) sb.Append(','); sb.Append(ids[i]); }
            return sb.ToString();
        }

        private static void RefreshRosterOverview(GeoRuntime rt)
        {
            EnsureRosterFamily();
            if (_rosterOverviewType == null || _onActorStatChanged == null || _statChangeTypeZero == null) return;
            var state = CurrentViewState(rt);
            if (state == null || !_rosterOverviewType.IsInstanceOfType(state)) return;
            // Re-invoke the state's own stat-change handler: it re-Inits the roster portrait/HP-bar list +
            // unit-stats from the model. The reflective #9 blob write never fires Health/Corruption
            // StatChangeEvent, so this never runs on a mirror apply. The handler ignores its four args.
            _onActorStatChanged.Invoke(state, new object[] { null, _statChangeTypeZero, 0f, 0f });
        }

        private static void RefreshContainment(GeoRuntime rt)
        {
            EnsureRosterFamily();
            if (_rosterAliensType == null || _toAlienContainment == null || _viewField == null) return;
            var geo = rt?.GeoLevel();
            if (geo == null) return;
            var view = _viewField.GetValue(geo);
            if (view == null) return;
            var state = _currentViewStateProp?.GetValue(view, null);
            if (state == null || !_rosterAliensType.IsInstanceOfType(state)) return;
            // No in-place refresh exists; re-enter the state (the game's own idiom after a kill/harvest) so it
            // re-reads the captured pool. Gated → only ever fires while containment IS current (never opens it).
            _toAlienContainment.Invoke(view, new object[] { true });
        }

        private static void RefreshRecruits(GeoRuntime rt)
        {
            EnsureRosterFamily();
            if (_rosterRecruitsType == null || _toPhoenixRecruits == null || _viewField == null) return;
            var geo = rt?.GeoLevel();
            if (geo == null) return;
            var view = _viewField.GetValue(geo);
            if (view == null) return;
            var state = _currentViewStateProp?.GetValue(view, null);
            if (state == null || !_rosterRecruitsType.IsInstanceOfType(state)) return;
            // Re-enter the recruits state (its own refresh idiom) so it re-reads the haven/naked recruit pool.
            _toPhoenixRecruits.Invoke(view, new object[] { true });
        }

        // Ground-vehicle equip screen (UIStateEditVehicle). Reached from the vehicle roster / aircraft bay; it
        // reuses the SHARED SoldierEquipModule but drives the VEHICLE slot layout (weapon/hull/engine) via
        // UpdateVehicleData, and its own item edits commit through GeoCharacter.SetItems (a vehicle IS a
        // GeoCharacter) — so the client edit already rides the existing SetItemsEditRelayPatch and mirrors back
        // on the #9 blob. What was missing was the REPAINT: RefreshRosterEquip gates to UIStateEditSoldier only,
        // so a mirrored vehicle-equip write left the open UIStateEditVehicle stale. We re-drive the panel's OWN
        // vehicle read path (UpdateVehicleData) exactly as UIStateEditVehicle.DisplaySoldier (:346) — model→UI,
        // never calls SetItems, so it cannot re-enter the edit relay (echo-free). Decompile-verified 2026-07-07.
        private static void RefreshVehicleEquip(GeoRuntime rt)
        {
            EnsureRosterFamily();
            if (_editVehicleType == null || _editVehicleCharField == null || _soldierEquipModuleField == null
                || _soldierEquipUpdateVehicleData == null || _charInventoryItemsProp == null
                || _charEquipmentItemsProp == null || _charArmourItemsProp == null || _iCommonItemType == null
                || _iVehicleEquipmentType == null || _getEquipmentType == null || _geoItemType2 == null
                || _geoItemDefProp2 == null || _gveWeapon == null || _gveHull == null || _gveEngine == null
                || _modulesField == null || _viewField == null || _currentViewStateProp == null) return;
            var geo = rt?.GeoLevel();
            if (geo == null) return;
            var view = _viewField.GetValue(geo);
            if (view == null) return;
            // Gate: active view state MUST be the vehicle-equip screen (SoldierEquipModule is shared with
            // UIStateEditSoldier — driving vehicle slot lists onto an open soldier screen would corrupt it).
            var state = _currentViewStateProp.GetValue(view, null);
            if (state == null || !_editVehicleType.IsInstanceOfType(state)) return;
            var modules = _modulesField.GetValue(view);
            if (modules == null) return;
            var module = _soldierEquipModuleField.GetValue(modules);
            if (module == null || !IsOpen(module)) return;
            var character = _editVehicleCharField.GetValue(state);
            if (character == null) return;

            // SKIP only for the HOST's own unflushed local equipment drag (_uiRefreshNeeded, set until the next
            // UpdateState flush) — re-reading the model would clobber the host's in-progress drag. A client is a
            // pure mirror (its vehicle SetItems is suppressed + relayed), so it must ALWAYS repaint the
            // authoritative #9/#1 apply — same host-only gate as the soldier-equip path above.
            if (IsHostEditingGuardActive() && _editVehicleRefreshNeededField?.GetValue(state) is bool re && re)
            {
                Debug.Log("[Multiplayer] GeoUiRefresh: EditVehicle equip re-drive SKIPPED — host has an unflushed local equipment edit");
                return;
            }

            object inv = _charInventoryItemsProp.GetValue(character, null);
            object weapon = FilterVehicleEquip(character, _gveWeapon);
            object hull = FilterVehicleEquip(character, _gveHull);
            object engine = FilterVehicleEquip(character, _gveEngine);
            int slots = 0;
            if (_charStatsProp != null && _getInventorySlots != null)
            {
                var stats = _charStatsProp.GetValue(character, null);
                if (stats != null) { try { slots = Convert.ToInt32(_getInventorySlots.Invoke(stats, null)); } catch { slots = 0; } }
            }
            // UpdateVehicleData(inventory, storage:null, weapon, hull, engine, inventorySlots) — storage stays
            // null (a separate re-read below owns the shared-stores panel), mirroring DisplaySoldier.
            _soldierEquipUpdateVehicleData.Invoke(module, new object[] { inv, null, weapon, hull, engine, slots });

            // shared-stores panel: force a model re-read of the faction/site ItemStorage (#1). RefreshStorage
            // reuses the UI's current items unless _refreshStorage is set, so set it first. Read-only, echo-safe.
            if (_editVehicleRefreshStorage != null && _editVehicleRefreshStorageField != null)
            {
                _editVehicleRefreshStorageField.SetValue(state, true);
                _editVehicleRefreshStorage.Invoke(state, null);
            }
        }

        /// <summary>Build a <c>List&lt;ICommonItem&gt;</c> of the character's equipment/armour GeoItems whose
        /// ItemDef is an <c>IVehicleEquipment</c> of the given <c>GroundVehicleEquipmentType</c> — the exact
        /// GetVehicleEquipment filter UIStateEditVehicle uses (:337-342). Typed as ICommonItem so the arg binds
        /// to UpdateVehicleData's <c>IEnumerable&lt;ICommonItem&gt;</c> slot param.</summary>
        private static object FilterVehicleEquip(object character, object targetTypeBoxed)
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(_iCommonItemType));
            AddMatchingVehicleEquip(_charEquipmentItemsProp.GetValue(character, null), targetTypeBoxed, list);
            AddMatchingVehicleEquip(_charArmourItemsProp.GetValue(character, null), targetTypeBoxed, list);
            return list;
        }

        private static void AddMatchingVehicleEquip(object items, object targetTypeBoxed, IList outList)
        {
            if (!(items is IEnumerable en)) return;
            foreach (var it in en)
            {
                if (it == null || !_geoItemType2.IsInstanceOfType(it)) continue;
                object def = _geoItemDefProp2.GetValue(it, null);
                if (def == null || !_iVehicleEquipmentType.IsInstanceOfType(def)) continue;
                object t;
                try { t = _getEquipmentType.Invoke(def, null); } catch { continue; }
                if (Equals(t, targetTypeBoxed)) outList.Add(it);
            }
        }

        // ─── WA-3: aircraft HP bar (vehicle-selected panel) ──────────────────────────────────────────
        // UIModuleVehicleSelection repaints its HP bar ONLY from GeoVehicle.OnMaintenanceChanged (its handler
        // sets the private bool _refreshVehicleBars; Update() consumes it → RefreshVehicleBars() reads
        // Stats.HitPoints — UIModuleVehicleSelection.cs:412-433). The client's silent HP value write
        // deliberately skips that event, so we set the SAME native deferred-repaint flag the handler sets —
        // the module's own Update() then repaints from the freshly-stamped model. Best-effort, never throws.
        private static FieldInfo _vehicleSelModuleField;   // GeoscapeModulesData.VehicleSelectionModule
        private static FieldInfo _vehicleSelRefreshField;  // UIModuleVehicleSelection._refreshVehicleBars (private bool)
        private static bool _vehicleSelEnsured;

        private static void EnsureVehicleSelection()
        {
            if (_vehicleSelEnsured) return;
            _vehicleSelEnsured = true;
            try
            {
                var moduleT = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleVehicleSelection");
                if (moduleT == null) return;
                var modulesT = AccessTools.TypeByName("Base.UI.GeoscapeModulesData");
                if (modulesT == null) return;
                _vehicleSelModuleField = AccessTools.Field(modulesT, "VehicleSelectionModule");
                _vehicleSelRefreshField = AccessTools.Field(moduleT, "_refreshVehicleBars");
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer] GeoUiRefresh.EnsureVehicleSelection failed: " + ex.Message); }
        }

        /// <summary>Arm the vehicle-selection module's native deferred HP-bar repaint after a WA-3 aircraft
        /// HP/repair value write. Safe no-op when the module is closed/missing (a closed module's Update()
        /// simply consumes the flag on its next open and repaints from the then-current model).</summary>
        public static void RefreshVehicleBars(GeoRuntime rt)
        {
            try
            {
                Ensure(rt);
                EnsureVehicleSelection();
                if (_vehicleSelModuleField == null || _vehicleSelRefreshField == null
                    || _viewField == null || _modulesField == null) return;
                var geo = rt?.GeoLevel();
                if (geo == null) return;
                var view = _viewField.GetValue(geo);
                if (view == null) return;
                var modules = _modulesField.GetValue(view);
                if (modules == null) return;
                var module = _vehicleSelModuleField.GetValue(modules);
                if (module == null) return;
                _vehicleSelRefreshField.SetValue(module, true);
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer] GeoUiRefresh.RefreshVehicleBars best-effort failed: " + ex.Message); }
        }

        // ─── Augmentation screens (UIStateMutate / UIStateBionics) ────────────────────────────────────
        // Both modules cache CharacterOriginalItems on open/character-change and never re-read from the
        // model until a new character is selected or the screen is re-opened. A remote augment apply (#9
        // blob) stamps the model but the cached baseline stays stale → the bodypart sections, augment
        // count, mutagen wallet, and 3D mesh show pre-apply state. We update the cached baseline from the
        // freshly-stamped model and re-drive the module's own RequestViewRefresh (public:
        // InitCharacterInfo/InitCurrentMutagens + OnRefreshRequested → DisplaySoldier). Decompile-verified
        // 2026-07-08: UIModuleMutate.cs:68/70/72/76/88/123/345/355; UIModuleBionics.cs:67/69/71/74/92/136/393.
        private static bool _augmentEnsured;
        private static Type _mutateStateType;                  // UIStateMutate
        private static Type _bionicsStateType;                 // UIStateBionics
        private static FieldInfo _mutateModuleField;           // GeoscapeModulesData.MutateModule
        private static FieldInfo _bionicsModuleField;          // GeoscapeModulesData.BionicsModule
        private static PropertyInfo _mutateCurrentCharProp;    // UIModuleMutate.CurrentCharacter (public)
        private static PropertyInfo _bionicsCurrentCharProp;   // UIModuleBionics.CurrentCharacter (public)
        private static PropertyInfo _mutateOrigItemsProp;      // UIModuleMutate.CharacterOriginalItems (public List<GeoItem>)
        private static PropertyInfo _bionicsOrigItemsProp;     // UIModuleBionics.CharacterOriginalItems (public List<GeoItem>)
        private static PropertyInfo _mutateCurrentItemsProp;   // UIModuleMutate.CharacterCurrentItems (public List<GeoItem>)
        private static PropertyInfo _bionicsCurrentItemsProp;  // UIModuleBionics.CharacterCurrentItems (public List<GeoItem>)
        private static MethodInfo _mutateRequestRefresh;       // UIModuleMutate.RequestViewRefresh()
        private static MethodInfo _bionicsRequestRefresh;      // UIModuleBionics.RequestViewRefresh()
        private static FieldInfo _mutateSectionsField;         // UIModuleMutate._augmentSections (Dictionary<AddonSlotDef, UIModuleMutationSection>)
        private static FieldInfo _bionicsSectionsField;        // UIModuleBionics._augmentSections (same shape)
        private static MethodInfo _sectionClearSelection;      // UIModuleMutationSection.ClearMutationSelection() (public)
        private static MethodInfo _sectionUnselectAll;         // UIModuleMutationSection.UnselectAllMutations() (public)

        private static void EnsureAugmentation()
        {
            if (_augmentEnsured) return;
            _augmentEnsured = true;
            try
            {
                var modulesType = AccessTools.TypeByName("Base.UI.GeoscapeModulesData");
                _mutateStateType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewStates.UIStateMutate");
                _bionicsStateType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewStates.UIStateBionics");
                var mutateModType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleMutate");
                var bionicsModType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleBionics");
                if (modulesType == null || mutateModType == null || bionicsModType == null) return;
                _mutateModuleField = AccessTools.Field(modulesType, "MutateModule");
                _bionicsModuleField = AccessTools.Field(modulesType, "BionicsModule");
                _mutateCurrentCharProp = AccessTools.Property(mutateModType, "CurrentCharacter");
                _bionicsCurrentCharProp = AccessTools.Property(bionicsModType, "CurrentCharacter");
                _mutateOrigItemsProp = AccessTools.Property(mutateModType, "CharacterOriginalItems");
                _bionicsOrigItemsProp = AccessTools.Property(bionicsModType, "CharacterOriginalItems");
                _mutateCurrentItemsProp = AccessTools.Property(mutateModType, "CharacterCurrentItems");
                _bionicsCurrentItemsProp = AccessTools.Property(bionicsModType, "CharacterCurrentItems");
                _mutateRequestRefresh = AccessTools.Method(mutateModType, "RequestViewRefresh");
                _bionicsRequestRefresh = AccessTools.Method(bionicsModType, "RequestViewRefresh");
                _mutateSectionsField = AccessTools.Field(mutateModType, "_augmentSections");
                _bionicsSectionsField = AccessTools.Field(bionicsModType, "_augmentSections");
                var sectionType = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleMutationSection");
                if (sectionType != null)
                {
                    _sectionClearSelection = AccessTools.Method(sectionType, "ClearMutationSelection");
                    _sectionUnselectAll = AccessTools.Method(sectionType, "UnselectAllMutations");
                }
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer] GeoUiRefresh.EnsureAugmentation failed: " + ex.Message); }
        }

        /// <summary>Repaint the open augmentation screen (mutation / bionics) after a remote model apply.
        /// The modules cache <c>CharacterOriginalItems</c> on open / character-change and never re-read
        /// from the model, so a remote augment apply (channel #9 blob) leaves the UI stale. We update the
        /// cached baseline from the freshly-stamped model, clear any pending preview selection, and re-drive
        /// the module's own <c>RequestViewRefresh</c> (InitCharacterInfo + InitCurrentMutagens + DisplaySoldier).
        /// <paramref name="stampedUnitIds"/> = the unit ids the apply actually stamped (null = unknown →
        /// unconditional): the repaint fires ONLY when the screen's character is among them — on unrelated
        /// applies it would eat the user's uncommitted LOCAL preview and, because the live ArmourItems still
        /// hold the transient preview item, bake that preview into the baseline (phantom never-purchased
        /// augment — preview regression RCA 2026-07-09). Safe no-op when neither augmentation screen is open.
        /// Never throws.</summary>
        public static void RepaintAugmentation(GeoRuntime rt, IReadOnlyList<long> stampedUnitIds = null)
        {
            try
            {
                Ensure(rt);
                EnsureAugmentation();
                EnsureRosterFamily(); // _currentViewStateProp, _viewField, _modulesField, _charArmourItemsProp
                if (_viewField == null || _currentViewStateProp == null || _modulesField == null) return;
                var geo = rt?.GeoLevel();
                if (geo == null) return;
                var view = _viewField.GetValue(geo);
                if (view == null) return;
                var state = _currentViewStateProp.GetValue(view, null);
                if (state == null) return;
                var modules = _modulesField.GetValue(view);
                if (modules == null) return;

                if (_mutateStateType != null && _mutateStateType.IsInstanceOfType(state))
                {
                    RepaintAugmentModule(modules, _mutateModuleField, _mutateCurrentCharProp,
                        _mutateOrigItemsProp, _mutateCurrentItemsProp, _mutateRequestRefresh,
                        _mutateSectionsField, stampedUnitIds);
                }
                else if (_bionicsStateType != null && _bionicsStateType.IsInstanceOfType(state))
                {
                    RepaintAugmentModule(modules, _bionicsModuleField, _bionicsCurrentCharProp,
                        _bionicsOrigItemsProp, _bionicsCurrentItemsProp, _bionicsRequestRefresh,
                        _bionicsSectionsField, stampedUnitIds);
                }
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer] GeoUiRefresh.RepaintAugmentation best-effort failed: " + ex.Message); }
        }

        /// <summary>Shared helper: update a single augmentation module's cached baseline + re-drive its
        /// native refresh. Called for whichever module (Mutate or Bionics) is the active view state.</summary>
        private static void RepaintAugmentModule(object modules, FieldInfo moduleField,
            PropertyInfo currentCharProp, PropertyInfo origItemsProp, PropertyInfo currentItemsProp,
            MethodInfo requestRefresh, FieldInfo sectionsField, IReadOnlyList<long> stampedUnitIds)
        {
            if (moduleField == null || currentCharProp == null || origItemsProp == null
                || currentItemsProp == null || requestRefresh == null || _charArmourItemsProp == null) return;
            var module = moduleField.GetValue(modules);
            if (module == null || !IsOpen(module)) return;
            var character = currentCharProp.GetValue(module, null);
            if (character == null) return;
            // Genuine-remote-apply gate: only an apply that stamped THIS character can make the screen
            // stale; anything else must leave the user's local preview transaction untouched.
            if (!AugmentRepaintDecision.ShouldRepaint(PersonnelReflection.ReadUnitId(character), stampedUnitIds))
                return;

            // Re-read the model's current ArmourItems (post-apply) into the cached CharacterOriginalItems
            // baseline so InitCharacterInfo (inside RequestViewRefresh) sees the fresh state, and any
            // RevertUnconfirmedChanges later reverts to this new truth — not the pre-apply snapshot.
            var armourItems = _charArmourItemsProp.GetValue(character, null);
            var origItems = origItemsProp.GetValue(module, null) as IList;
            var currItems = currentItemsProp.GetValue(module, null) as IList;
            if (origItems != null && armourItems is IEnumerable armourEnum)
            {
                origItems.Clear();
                foreach (var item in armourEnum) origItems.Add(item);
            }
            // Unselect every body-part section through the NATIVE toggle-off (ClearMutationSelection →
            // SelectMutation(same slot) → _selectedMutationSlot = null + SelectedButton cleared + ClearAugment,
            // which re-stamps the just-reset baseline — a no-op write). Leaving _selectedMutationSlot armed
            // while the visible selection was wiped inverted the click parity: the user's next click on the
            // same option TOGGLED OFF ("first click does nothing until Escape"). UnselectAllMutations after
            // is the hard-null backstop for the locked-section edge SelectMutation refuses to clear.
            if (sectionsField?.GetValue(module) is IDictionary sections)
            {
                foreach (var section in sections.Values)
                {
                    try
                    {
                        _sectionClearSelection?.Invoke(section, null);
                        _sectionUnselectAll?.Invoke(section, null);
                    }
                    catch (Exception ex) { Debug.LogWarning("[Multiplayer] GeoUiRefresh augment section unselect failed: " + ex.Message); }
                }
            }
            // Clear any pending preview selection (a remote apply supersedes a local uncommitted pick).
            currItems?.Clear();

            // Re-drive the module's own refresh: InitCharacterInfo (body-part sections) + InitCurrentMutagens
            // (mutagen wallet, Mutate only) + OnRefreshRequested → DisplaySoldier (3D mesh update).
            requestRefresh.Invoke(module, null);
        }

        private static bool IsOpen(object module)
        {
            // UIModuleBehavior : MonoBehaviour → ((Component)module).gameObject.activeInHierarchy.
            var comp = module as Component;
            return comp != null && comp.gameObject != null && comp.gameObject.activeInHierarchy;
        }

        /// <summary>The equip/vehicle "skip-if-locally-editing" guards apply ONLY to the HOST: the host is the
        /// authority and a mid-drag re-read would clobber the host player's own uncommitted equipment drag. A
        /// CLIENT's geoscape equip edit is suppressed + relayed as an intent (never a local model write), so its
        /// open equip screen is a pure display mirror that must ALWAYS repaint from the authoritative #9/#1 apply —
        /// never gated by _uiRefreshNeeded (also set by screen-open/refresh, not just a real drag). No active MP
        /// session (single-player) → the mirror re-drive path never reaches these guards, so the value is moot.</summary>
        private static bool IsHostEditingGuardActive()
        {
            var eng = NetworkEngine.Instance;
            return eng != null && eng.IsActiveSession && eng.IsHost;
        }

        // (The v2 equip repaint defer/drain now lives in the pure EditSession + EquipMirrorRepaint driver — the
        // old per-Tick reflection+log probe machinery (IsEquipItemDragActive / IsEquipEditWindowActive /
        // FlushPendingEquipRepaint / ClearPendingEquipRepaint) that re-armed a flag every frame is DELETED.)
    }
}
