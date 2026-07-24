using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Base.Core;
using Base.Entities.Statuses;
using Base.UI;
using HarmonyLib;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Common.Entities.Characters;
using PhoenixPoint.Common.View.ViewModules;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.Research;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewControllers.PhoenixBase;
using PhoenixPoint.Geoscape.View.ViewControllers.AugmentationScreen;
using PhoenixPoint.Geoscape.View.ViewModules;
using PhoenixPoint.Geoscape.View.ViewStates;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Law 11 reactivity table: per entity KIND, the native event/nudge that repaints already-open UI
    /// the instant a rail delta lands. This is presentation-only knowledge (which native event a view
    /// listens to), NOT sync logic — the rail itself stays subsystem-blind.
    ///   • Wallet → raise its own <c>ResourcesChanged</c> (GeoscapeView relays to FactionResourcesChanged
    ///     → info bar / manufacturing / replenish screens repaint natively).
    ///   • Research / ResearchElement → the proven ResearchSync repaint path (open research screen
    ///     SetupQueue rebuild, else agenda-tracker nudge).
    ///   • Timing → no-op: Paused/Scale rode the native property setters during apply, which already
    ///     fire OnPausedEvent/EffectiveScaleChangedEvent; the clock readout polls Timing.Now per frame.
    ///   • ItemStorage → raise its own <c>StorageChanged</c> (GeoItemDict apply wrote _storageItems
    ///     directly, bypassing AddItem/RemoveItem = no native notify) → free-space info bar +
    ///     inventory views (which subscribe to it) repaint. Manufacturing + equip screens are
    ///     PULL-model (no StorageChanged subscription) → also mark the open screen dirty; the flush
    ///     re-enters it, or for UIStateManufacturing routes to its dedicated per-panel rebuild.
    ///     Covers GeoFaction + GeoSite.
    ///   • Unknown kind → logged ONCE — the to-do list for the next event-map entry.
    /// </summary>
    public static class UiEventMap
    {
        private static readonly HashSet<string> _loggedUnknown = new HashSet<string>(StringComparer.Ordinal);

        public static void Fire(HashSet<object> touched, GeoLevelController geo)
        {
            if (touched == null || touched.Count == 0) return;
            bool researchDone = false;
            foreach (var entity in touched)
            {
                try
                {
                    switch (entity)
                    {
                        case Wallet w: // touched is a HashSet — each wallet fires at most once
                            RaiseResourcesChanged(w);
                            // FactionResourcesChanged reaches only its three native subscribers
                            // (decompile: UIStateManufacturing.cs:58, UIStateReplenish.cs:34,
                            // UIModuleInfoBar.cs:148); every other open screen reads the wallet
                            // PULL-model (equip-screen quick-produce affordability, base build
                            // menu, research cost gating) → also the universal repaint seam.
                            // Safe from inside SyncApplyScope: MarkDirty only sets a flag, the
                            // flush stays in SyncEngine.Tick with its own scope + defer/coalescing.
                            OpenUiRepaint.MarkDirty();
                            break;
                        case Research _:
                        case ResearchElement _:
                            if (!researchDone) { researchDone = true; ResearchSync.RepaintResearchUi(); }
                            break;
                        case Timing _:
                        case TimingInstanceData _: // the TimeAnchor scratch DTO
                            // No-op, GROUNDED (not assumed): Paused/Scale rode the native property setters
                            // during apply, which already fire OnPausedEvent/EffectiveScaleChangedEvent; and
                            // the clock readout genuinely POLLS — UIModuleTimeControl.Update():139 reads
                            // _timing.Now.DateTime every frame and repaints only when it changed (:143-149).
                            // So a latched anchor needs no event of its own to become visible.
                            break;
                        case CharacterProgression cp:
                            // Derived-stat recompute (stat-spend RCA): the rail wrote _baseStats /
                            // SkillPoints RAW, so CharacterProgression.StatModifiedCallback never fired
                            // and the owner's private GeoCharacter.UpdateStats (GeoCharacter.cs:1185, the
                            // native recompute the callback drives) never ran — every open screen keeps
                            // painting stale CharacterStats. Same per-kind shape as Wallet/ItemStorage:
                            // run the native derive, then the universal repaint re-reads it.
                            RefreshDerivedStats(OwnerOf(cp, geo));
                            OpenUiRepaint.MarkDirty();
                            break;
                        case GeoCharacter gc:
                            // Item lists (_armourItems…) also land raw — natively SetItems ends in
                            // UpdateStats(recalculateBodparts: true) (GeoCharacter.cs:876-879); mirror that.
                            RefreshDerivedStats(gc);
                            OpenUiRepaint.MarkDirty();
                            break;
                        case ItemStorage storage:
                            RaiseStorageChanged(storage);
                            // Manufacturing + equip screens are PULL-model: NO StorageChanged subscription
                            // anywhere in them (decompile: only GeoPhoenixFaction.cs:308 + UIModuleInfoBar
                            // .cs:158 subscribe; UIStateEditSoldier re-reads storage only via EnterState →
                            // RefreshStorage, _refreshStorage set at :101) — so the open screen must also
                            // go through the universal repaint seam; the flush dispatches to the screen's
                            // native rebuild via the UiNativeRepaint table below (manufacturing's entry
                            // also re-snapshots the scrap-mode storage copies).
                            OpenUiRepaint.MarkDirty();
                            break;
                        default:
                            // Law 11 universal cover, guaranteed HERE: any kind without a per-kind
                            // native event still repaints the open geoscape screen through the
                            // generic seam — no unmapped type is ever silently repaint-less,
                            // regardless of what the caller does after Fire().
                            OpenUiRepaint.MarkDirty();
                            if (_loggedUnknown.Add(entity.GetType().Name))
                                Debug.Log("[Multiplayer][rail] UiEventMap: no per-kind mapping for " + entity.GetType().Name + " — universal open-screen repaint (logged once)");
                            break;
                    }
                }
                catch (Exception ex) { Debug.LogWarning("[Multiplayer][rail] UiEventMap fire failed for " + entity.GetType().Name + ": " + ex.Message); }
            }
        }

        private static readonly MethodInfo UpdateStatsMethod =
            AccessTools.Method(typeof(GeoCharacter), "UpdateStats"); // private (bool recalculateBodparts)
        private static readonly FieldInfo LevelTacUnitsField =
            AccessTools.Field(typeof(GeoLevelController), "_tacUnits");

        /// <summary>Owner of a touched progression: CharacterProgression carries no back-ref, so scan the
        /// level's U# root registry (same _tacUnits dict IdentityResolver roots from) for the character
        /// whose Progression IS this instance.</summary>
        private static GeoCharacter OwnerOf(CharacterProgression cp, GeoLevelController geo)
        {
            if (geo == null || !(LevelTacUnitsField?.GetValue(geo) is IDictionary units)) return null;
            foreach (var u in units.Values)
                if (u is GeoCharacter c && ReferenceEquals(c.Progression, cp)) return c;
            return null;
        }

        /// <summary>Native derived recompute, scoped like the other raisers (law 8: UpdateStats fires the
        /// Stat callbacks a seam could hear). Idempotent — same inputs, same CharacterStats.</summary>
        private static void RefreshDerivedStats(GeoCharacter character)
        {
            if (character == null || UpdateStatsMethod == null) return;
            using (SyncApplyScope.Enter())
                UpdateStatsMethod.Invoke(character, new object[] { true });
        }

        private static readonly System.Reflection.FieldInfo ResourcesChangedField =
            AccessTools.Field(typeof(Wallet), "ResourcesChanged");

        /// <summary>Raise the wallet's own native event (empty diff pack — subscribers re-read totals).
        /// Guarded by SyncApplyScope like <see cref="RaiseStorageChanged"/> — a subscriber (TFTV) reacting
        /// by spending/moving resources must not echo an intent back to the host (law 8).</summary>
        private static void RaiseResourcesChanged(Wallet wallet)
        {
            var del = ResourcesChangedField?.GetValue(wallet) as Delegate;
            if (del == null) return;
            using (SyncApplyScope.Enter())
                del.DynamicInvoke(wallet, new ResourcePack(), OperationReason.None);
        }

        /// <summary>Raise the storage's own native change notification (public Action field — pure notify,
        /// no AddItem/RemoveItem gameplay, no ammo unload) so every subscribed view repaints: free-space
        /// info bar re-reads GetStorageUsed, inventory/manufacturing lists rebuild. Guarded by
        /// SyncApplyScope so a re-entrant subscriber can't echo an intent back to the host (law 8).</summary>
        private static void RaiseStorageChanged(ItemStorage storage)
        {
            using (SyncApplyScope.Enter())
                storage.StorageChanged?.Invoke();
        }
    }

    /// <summary>
    /// Law 11 repaint PRIMITIVE: per screen type, the game's OWN read-direction refresh methods —
    /// re-read the model into the LIVE widgets, no lifecycle transition. This is the DEFAULT path of
    /// OpenUiRepaint; Exit+Enter survives only as the flagged fallback for screens not yet in this
    /// table. Every method here is grounded in the decompile as what the game itself calls when its
    /// model changes while the screen is open (file:line in each entry) — NEVER guessed.
    /// An entry returns false to decline (missing module/selection after a game update or an empty
    /// screen) — the flush then falls back to the re-enter for that one repaint.
    /// </summary>
    public static class UiNativeRepaint
    {
        // --- UIStateEditSoldier / UIStateEditVehicle: same reseed shape (UIStateEditSoldier.cs
        // CharacterChangedHandler:358-365 minus the UI→model write-back of the previous character):
        // _refreshStorage=true so RefreshStorage re-reads StorageItems() instead of the stale UI list
        // (:587-597), OnDataChanged = wallet/tags panel, DisplaySoldier = equip lists + doll from the
        // model (:580-585), then the progression panel.
        private static readonly FieldInfo EsRefreshFlag = AccessTools.Field(typeof(UIStateEditSoldier), "_refreshStorage");
        private static readonly MethodInfo EsGetData = AccessTools.Method(typeof(UIStateEditSoldier), "GetSoldierEquipModuleData");
        private static readonly MethodInfo EsDisplay = AccessTools.Method(typeof(UIStateEditSoldier), "DisplaySoldier", new[] { typeof(GeoCharacter) });
        private static readonly MethodInfo EsRefreshStorage = AccessTools.Method(typeof(UIStateEditSoldier), "RefreshStorage");
        private static readonly MethodInfo EsSelectProgression = AccessTools.Method(typeof(UIStateEditSoldier), "SelectCharacterProgression", new[] { typeof(GeoCharacter) });
        private static readonly FieldInfo EvRefreshFlag = AccessTools.Field(typeof(UIStateEditVehicle), "_refreshStorage");
        private static readonly MethodInfo EvGetData = AccessTools.Method(typeof(UIStateEditVehicle), "GetSoldierEquipModuleData");
        private static readonly MethodInfo EvDisplay = AccessTools.Method(typeof(UIStateEditVehicle), "DisplaySoldier", new[] { typeof(GeoCharacter) });
        private static readonly MethodInfo EvRefreshStorage = AccessTools.Method(typeof(UIStateEditVehicle), "RefreshStorage");
        // UIStateGeoRoster.OnActorStatChanged (:364-368) = the game's own open-screen model-change
        // reaction: re-Init the roster module from the containers + refresh the unit-stats panel.
        // It ignores all four arguments.
        private static readonly MethodInfo GrStatChanged = AccessTools.Method(typeof(UIStateGeoRoster), "OnActorStatChanged");
        // UIStateVehicleSelected: the per-concern refreshes its own event handlers call —
        // UpdateVehiclesTabs (OnVehichleChanged:1294), UpdateVehicleActions (idempotent, clears via
        // UnsubscribeVehicleActions:1427-1438), UpdateReachableSitesMarkers (:903),
        // OnFactionObjectivesChanged (:1008, self-gates on viewer faction).
        private static readonly PropertyInfo VsSelected = AccessTools.Property(typeof(UIStateVehicleSelected), "SelectedVehicle");
        private static readonly MethodInfo VsTabs = AccessTools.Method(typeof(UIStateVehicleSelected), "UpdateVehiclesTabs");
        private static readonly MethodInfo VsActions = AccessTools.Method(typeof(UIStateVehicleSelected), "UpdateVehicleActions");
        private static readonly MethodInfo VsReachable = AccessTools.Method(typeof(UIStateVehicleSelected), "UpdateReachableSitesMarkers");
        private static readonly MethodInfo VsObjectives = AccessTools.Method(typeof(UIStateVehicleSelected), "OnFactionObjectivesChanged");
        // UIStateNothingSelected.OnFactionObjectivesChanged (:519-525): objectives module + section-bar
        // diplomacy count. Site markers live on GeoscapeView, outside any view state — nothing else on
        // this screen reads the model.
        private static readonly MethodInfo NsObjectives = AccessTools.Method(typeof(UIStateNothingSelected), "OnFactionObjectivesChanged");
        // UIModuleBaseLayout.SetLeftSideInfo (:605, also its own PxBase.Stats.OnStatsUpdated handler)
        // + SetupBaseLayout (:561, re-inits every slot from PxBase.Layout). Deliberately NOT
        // UIModuleBaseLayout.Init — Init closes an open build menu / facility info (:291-292).
        private static readonly MethodInfo BlLeftInfo = AccessTools.Method(typeof(UIModuleBaseLayout), "SetLeftSideInfo");
        private static readonly MethodInfo BlLayout = AccessTools.Method(typeof(UIModuleBaseLayout), "SetupBaseLayout");
        private static readonly FieldInfo BlSlots = AccessTools.Field(typeof(UIModuleBaseLayout), "_slots");

        private static readonly Dictionary<Type, Func<GeoscapeViewState, GeoscapeView, bool>> Table =
            new Dictionary<Type, Func<GeoscapeViewState, GeoscapeView, bool>>
            {
                // First two table citizens = the former special-case branches (9b35194 manufacturing
                // opt-out, research per-kind path); their rebuild knowledge stays in the family files.
                [typeof(UIStateManufacturing)] = (s, v) => { ManufactureSync.RepaintManufacturingUi(); return true; },
                [typeof(UIStateResearch)] = (s, v) => { ResearchSync.RepaintResearchUi(); return true; },
                [typeof(UIStateEditSoldier)] = (s, v) =>
                    EsSelectProgression != null // resolve-all-first: decline BEFORE the reseed mutates anything
                    && ReseedEquipScreen(s, v.GeoscapeModules, EsRefreshFlag, EsGetData, EsDisplay, EsRefreshStorage)
                    && Call(EsSelectProgression, s, v.GeoscapeModules.ActorCycleModule.CurrentCharacter),
                [typeof(UIStateEditVehicle)] = (s, v) =>
                {
                    var mods = v.GeoscapeModules;
                    if (!ReseedEquipScreen(s, mods, EvRefreshFlag, EvGetData, EvDisplay, EvRefreshStorage)) return false;
                    // progression panel: EnterState calls the module directly (UIStateEditVehicle.cs:162)
                    mods.CharacterProgressionModule.SetCharacterProgression(Viewer(), mods.ActorCycleModule.CurrentCharacter);
                    return true;
                },
                [typeof(UIStateGeoRoster)] = (s, v) =>
                {
                    var cur = v.GeoscapeModules.ActorCycleModule == null ? null : v.GeoscapeModules.ActorCycleModule.CurrentCharacter;
                    if (cur == null) return false; // empty roster → fallback re-enter re-reads the containers
                    if (!Call(GrStatChanged, s, null, default(StatChangeType), 0f, 0f)) return false;
                    // module re-Init dropped the highlight — re-select the character being viewed
                    v.GeoscapeModules.GeneralPersonelRosterModule?.SetSelectSlot(cur, scrollToSoldier: true);
                    return true;
                },
                [typeof(UIStateVehicleSelected)] = (s, v) =>
                {
                    // resolve-all-first: a null MethodInfo mid-&&-chain would run the earlier refreshes,
                    // then decline → the fallback re-enter stacked on a partial repaint. Decline up front.
                    if (VsSelected == null || VsTabs == null || VsActions == null || VsReachable == null || VsObjectives == null) return false;
                    if (VsSelected.GetValue(s, null) == null) return false;
                    return Call(VsTabs, s) && Call(VsActions, s) && Call(VsReachable, s) && Call(VsObjectives, s, Viewer());
                },
                [typeof(UIStateNothingSelected)] = (s, v) => Call(NsObjectives, s, Viewer()),
                [typeof(UIStateBionics)] = (s, v) => RepaintAugmentScreen(v.GeoscapeModules.BionicsModule, v.GeoscapeModules),
                [typeof(UIStateMutate)] = (s, v) => RepaintAugmentScreen(v.GeoscapeModules.MutateModule, v.GeoscapeModules),
                [typeof(UIStatePhoenixBaseLayout)] = (s, v) =>
                {
                    var m = v.GeoscapeModules.BaseLayoutModule;
                    if (m == null || m.PxBase == null || BlLeftInfo == null || BlLayout == null) return false;
                    // SetupBaseLayout re-Instantiates a prefab per facility, but AttachFacilityPrefab only
                    // ASSIGNS (PhoenixFacilityController.cs:349-352) — the old prefab is destroyed only by
                    // DetachPrefab, which natively runs only from Uninit (UIModuleBaseLayout.cs:557; the
                    // game never re-calls SetupBaseLayout while open). Mirror Uninit's slot loop first, or
                    // every repaint stacks one duplicate prefab per facility.
                    if (!(BlSlots?.GetValue(m) is PhoenixFacilityController[] slots)) return false;
                    foreach (var slot in slots) { slot.VisualsContainer.SetActive(true); slot.DetachPrefab(); }
                    return Call(BlLeftInfo, m) && Call(BlLayout, m);
                },
            };

        /// <summary>True = the open screen has a native rebuild and it ran (caller skips the re-enter).
        /// Called inside OpenUiRepaint's SyncApplyScope + try/catch — a throwing rebuild is logged there
        /// and the screen kept, never demoted to Exit+Enter.</summary>
        internal static bool TryRepaint(GeoscapeViewState current, GeoscapeView view)
        {
            return Table.TryGetValue(current.GetType(), out var rebuild) && rebuild(current, view);
        }

        /// <summary>UIStateBionics / UIStateMutate. These screens stage an unconfirmed trial IN the model
        /// (OnAugmentClicked → SetItems, reverted on exit), so the generic Exit+Enter fallback is exactly
        /// wrong here: it clears the player's mutation selection on every batch and re-snapshots
        /// CharacterOriginalItems around whatever was staged. Native read-direction pieces instead:
        ///   • the model moved UNDER the screen (armour ≠ snapshot AND ≠ trial — a foreign augment on this
        ///     soldier, or a reject re-emit): run the screen's own rebind (CharacterChangedHandler shape,
        ///     UIStateBionics.cs:118-122) — OnNewCharacter re-snapshots CharacterOriginalItems from the
        ///     model (its internal revert-SetItems stays suppressed by SetItemsApplyGate — this runs under
        ///     the repaint's SyncApplyScope) — plus the OnAugmentClicked doll refresh
        ///     (UIModuleBionics.cs:199, DisplaySoldier(resetAnimation:false, addWeapon:false)).
        ///   • otherwise snapshot, trial and selection are all still valid — keep them untouched.
        ///     Wallet/storage bars repaint through their own native events. ponytail: UIModuleMutate's
        ///     mutagen label (private InitCurrentMutagens) can stale here until the next selection
        ///     change/reopen — wire its reflection call if that ever shows in play.</summary>
        private static bool RepaintAugmentScreen(object module, GeoscapeModulesData mods)
        {
            GeoCharacter cur;
            List<GeoItem> snapshot, trial;
            switch (module)
            {
                case UIModuleBionics b: cur = b.CurrentCharacter; snapshot = b.CharacterOriginalItems; trial = b.CharacterCurrentItems; break;
                case UIModuleMutate m: cur = m.CurrentCharacter; snapshot = m.CharacterOriginalItems; trial = m.CharacterCurrentItems; break;
                default: return false;
            }
            if (cur == null || snapshot == null || trial == null) return false; // fallback re-enter re-seeds from scratch
            var live = cur.ArmourItems;
            if (!SameItems(live, snapshot) && !SameItems(live, trial))
            {
                if (module is UIModuleBionics b2) b2.OnNewCharacter(cur);
                else ((UIModuleMutate)module).OnNewCharacter(cur);
                mods.ActorCycleModule?.DisplaySoldier(cur, resetAnimation: false, addWeapon: false);
            }
            return true;
        }

        /// <summary>GeoItem.Equals is VALUE equality (def+count+charges, GeoItem.cs:124) — survives the
        /// applier's live-instance reuse; both module lists preserve model order by construction.</summary>
        private static bool SameItems(IReadOnlyList<GeoItem> live, List<GeoItem> list)
        {
            if (live == null || list == null || live.Count != list.Count) return false;
            for (int i = 0; i < live.Count; i++)
                if (!Equals(live[i], list[i])) return false;
            return true;
        }

        private static bool ReseedEquipScreen(GeoscapeViewState state, GeoscapeModulesData mods,
            FieldInfo refreshFlag, MethodInfo getData, MethodInfo display, MethodInfo refreshStorage)
        {
            var cur = mods.ActorCycleModule == null ? null : mods.ActorCycleModule.CurrentCharacter;
            // resolve-all-first: every MethodInfo checked BEFORE the first invocation, so a renamed
            // member declines cleanly instead of running half the reseed and then falling back.
            if (cur == null || refreshFlag == null || getData == null || display == null || refreshStorage == null) return false;
            refreshFlag.SetValue(state, true); // next RefreshStorage re-reads the model, not the stale UI list
            mods.SoldierEquipModule.OnDataChanged((UIModuleSoldierEquipData)getData.Invoke(state, null));
            // reseeds the CURRENT character = selection preserved by construction
            return Call(display, state, cur) && Call(refreshStorage, state);
        }

        private static GeoFaction Viewer()
        {
            var level = GameUtl.CurrentLevel();
            var geo = level == null ? null : level.GetComponent<GeoLevelController>();
            return geo == null ? null : geo.ViewerFaction;
        }

        /// <summary>Reflection guard: a game update renaming a member turns the entry into a decline
        /// (→ fallback re-enter), not an NRE.</summary>
        private static bool Call(MethodInfo m, object target, params object[] args)
        {
            if (m == null) return false;
            m.Invoke(target, args);
            return true;
        }
    }
}
