using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Base.Core;
using Base.Entities.Statuses;
using Base.UI;
using HarmonyLib;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Common.Entities.Addons;
using PhoenixPoint.Common.Entities.Characters;
using PhoenixPoint.Common.Utils;
using PhoenixPoint.Common.View.ViewModules;
using PhoenixPoint.Geoscape.View.ViewControllers.Modal;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.Research;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.DataObjects;
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
    ///   • RELEVANCE: every arm marks through <c>MarkDirty(kind, geo)</c>, so the OPEN screen may
    ///     decline a kind it provably cannot paint (<see cref="UiNativeRepaint.IgnoredKinds"/>).
    ///     Undeclared kind or undeclared screen still marks — the exclusion is opt-in, never implied.
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
                            OpenUiRepaint.MarkDirty(entity.GetType(), geo);
                            break;
                        case Research _:
                        case ResearchElement _:
                            // Presentation latch first (started log / completed modal from the mirrored
                            // transition — the 0xAA channel's former MsgStart/MsgComplete job), then the
                            // proven repaint path.
                            if (!researchDone)
                            {
                                researchDone = true;
                                ResearchSync.PresentFromMirror(geo);
                                ResearchSync.RepaintResearchUi();
                            }
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
                            OpenUiRepaint.MarkDirty(entity.GetType(), geo);
                            break;
                        case GeoCharacter gc:
                            // Item lists (_armourItems…) also land raw — natively SetItems runs
                            // AddAbilitiesFromItems(updateStats: false) (GeoCharacter.cs:846) and THEN
                            // UpdateStats(recalculateBodparts: true) (:876-879); mirror both, else
                            // PassiveModifiersFromItems stays stale after armour/augment deltas.
                            if (AddAbilitiesFromItemsMethod != null)
                                using (SyncApplyScope.Enter())
                                    AddAbilitiesFromItemsMethod.Invoke(gc, new object[] { false });
                            RefreshDerivedStats(gc);
                            OpenUiRepaint.MarkDirty(entity.GetType(), geo);
                            break;
                        case PhoenixPoint.Geoscape.Entities.PhoenixBases.GeoPhoenixFacility fac:
                            // Facility leaves (_isPowered, state, update times) land RAW, so the native
                            // notify chain never ran: SetPowered (GeoPhoenixFacility.cs:317) fires
                            // OnPowerStateChanged → GeoPhoenixBase.Facility_PowerStateChanged (:860-863)
                            // → UpdateStats (:393). PhoenixBaseStats CACHES PowerConsumption/PowerOutput
                            // (PhoenixBaseStats.cs:18-26 — RemainingPower/Underpowered are derived from
                            // them and refreshed ONLY by that call), so without this the client's base
                            // screen keeps painting pre-delta power even once the value arrives. Same
                            // per-kind shape as CharacterProgression/GeoCharacter: native derive, then
                            // the universal repaint re-reads it.
                            using (SyncApplyScope.Enter())
                                if (fac.PxBase != null) fac.PxBase.UpdateStats();
                            OpenUiRepaint.MarkDirty(entity.GetType(), geo);
                            break;
                        case PhoenixPoint.Geoscape.Events.GeoscapeEventSystem _:
                            // A record delta NEVER raises a window (windows are live 0xB6 raises) — but it
                            // IS the answer/dismiss signal: the repaint refreshes an open dialog's stale
                            // Record ref and closes a picker somebody else already answered
                            // (EventPopup.RepaintDialog, the UiNativeRepaint entry for UIStateGeoscapeEvent).
                            // Site encounter labels etc. ride the same universal repaint like any other kind.
                            OpenUiRepaint.MarkDirty(entity.GetType(), geo);
                            break;
                        case CharacterIdentity ci:
                            // THE REPAINT WAS FIRING ALL ALONG AND PAINTED NOTHING (live 2026-08-06:
                            // "no per-kind mapping for CharacterIdentity — universal open-screen repaint").
                            // Identity is the one kind whose whole presentation sits behind TWO caches the
                            // game only ever invalidates from its OWN write path, so a mirrored write is
                            // invisible until the visit is torn down and rebuilt — the "leave the screen and
                            // come back" the user measured. Both are re-seeded here, before the flush.
                            ReseedIdentityDisplay(OwnerOf(ci, geo), geo);
                            OpenUiRepaint.MarkDirty(entity.GetType(), geo);
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
                            OpenUiRepaint.MarkDirty(entity.GetType(), geo);
                            break;
                        default:
                            // Law 11 universal cover, guaranteed HERE: any kind without a per-kind
                            // native event still repaints the open geoscape screen through the
                            // generic seam, regardless of what the caller does after Fire(). The ONE
                            // exception is declared, not implicit: a kind listed against the open
                            // screen in UiNativeRepaint.IgnoredKinds cannot change anything that
                            // screen paints, and skipping it is logged once per kind per screen.
                            OpenUiRepaint.MarkDirty(entity.GetType(), geo);
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
        private static readonly MethodInfo AddAbilitiesFromItemsMethod =
            AccessTools.Method(typeof(GeoCharacter), "AddAbilitiesFromItems"); // private (bool updateStats = true), GeoCharacter.cs:617
        private static readonly FieldInfo LevelTacUnitsField =
            AccessTools.Field(typeof(GeoLevelController), "_tacUnits");

        /// <summary>Owner of a touched progression: CharacterProgression carries no back-ref, so scan the
        /// level's U# root registry (same _tacUnits dict IdentityResolver roots from) for the character
        /// whose Progression IS this instance.</summary>
        internal static GeoCharacter OwnerOf(CharacterProgression cp, GeoLevelController geo)
        {
            if (geo == null || !(LevelTacUnitsField?.GetValue(geo) is IDictionary units)) return null;
            foreach (var u in units.Values)
                if (u is GeoCharacter c && ReferenceEquals(c.Progression, cp)) return c;
            return null;
        }

        /// <summary>Owner of a touched identity — same scan, same registry, same reason as
        /// <see cref="OwnerOf(CharacterProgression, GeoLevelController)"/>: <c>CharacterIdentity</c> carries
        /// no back-ref to its character either.</summary>
        internal static GeoCharacter OwnerOf(CharacterIdentity identity, GeoLevelController geo)
        {
            if (geo == null || identity == null || !(LevelTacUnitsField?.GetValue(geo) is IDictionary units)) return null;
            foreach (var u in units.Values)
                if (u is GeoCharacter c && ReferenceEquals(c.Identity, identity)) return c;
            return null;
        }

        private static readonly FieldInfo ActorCycleUnitsField =
            AccessTools.Field(typeof(UIModuleActorCycle), "_units");        // List<UnitDisplayData>, :126
        private static readonly FieldInfo ActorCycleBuilderField =
            AccessTools.Field(typeof(UIModuleActorCycle), "_charBuilder");  // AddonsCharacterBuilder, :130

        /// <summary>
        /// THE TWO CACHES BETWEEN A MIRRORED IDENTITY AND THE PIXELS, invalidated the way the game itself
        /// invalidates them. Diagnosis 2026-08-06, in-game: a rename and a recolour landed on the rail, the
        /// universal repaint ran, and the open screen kept the old name and the old armour until the player
        /// left to the geoscape and came back. The state was never the problem — the two reads were.
        ///
        ///   (1) THE NAME IS A SNAPSHOT, NOT A READ. Every name label on these screens reads
        ///       <c>UIModuleActorCycle.CurrentUnit.Name</c> (<c>RefreshSoldierInfo</c>:514), and
        ///       <c>UnitDisplayData.Name</c> is assigned ONCE, in the constructor, from
        ///       <c>character.GetName()</c> (UnitDisplayData.cs:41) — a per-visit copy that no refresh path
        ///       re-reads. The GAME KNOWS: its own rename handler hand-patches the copy
        ///       (<c>RenameCharacter</c>:743 <c>CurrentUnit.Name = currentCharacter.GetName()</c>) precisely
        ///       because nothing else would. So do the same, for every cached unit — a batch can rename more
        ///       than one, and re-reading a name costs nothing. (The sibling fields are NOT snapshots:
        ///       <c>GameTags</c> is the live list reference and the item lists are deferred LINQ, so the rest
        ///       of the identity is already current.)
        ///
        ///   (2) THE MODEL REBUILD IS SKIPPED FOR AN IDENTITY-ONLY CHANGE. <c>DisplaySoldier</c> — the funnel
        ///       every one of these screens repaints through — pushes the fresh <c>GameTags</c> into the
        ///       addons manager with autorefresh OFF (:613), then EARLY-RETURNS at :636-644 whenever the
        ///       addon set (armour + weapon) is unchanged, without ever reaching
        ///       <c>CommonCharacterUtils.RebuildCharacter</c>. A recolour, a new face, a haircut change no
        ///       armour, so they take that early return every time, and re-enabling autorefresh afterwards
        ///       (:642) does not replay a change made while it was off (<c>AddonsManager
        ///       .SetAutorefreshOnTagsChanged</c>:197 only flips the flag). Removing a HELMET does change the
        ///       set, which is exactly why the user saw the helmet start working while the recolour never did.
        ///       The fix is a cache INVALIDATION, not a rebuild of our own: empty <c>AddonsCharacterBuilder
        ///       .Addons</c> — the very list that comparison reads — and the game's own next
        ///       <c>DisplaySoldier</c> takes the rebuild branch and refills it (<c>RebuildCharacter</c> starts
        ///       with <c>addons.Clear()</c>, CommonCharacterUtils.cs:56-58). Nothing is torn off the model by
        ///       clearing it: the list is the REQUEST for the next rebuild, never a description of what is
        ///       currently worn.
        ///
        /// WHICH ALSO COVERS BOTH HELMET TOGGLES WITHOUT NAMING EITHER. The vanilla tick
        /// (<c>UIStateSoldierCustomization</c>:24/:44/:51) and TFTV's edit-screen icon (its prefix on this
        /// same <c>UIModuleActorCycle.DisplaySoldier(GeoCharacter,bool,bool,bool)</c> overload,
        /// TFTVUI/Personnel/ShowWithoutHelmet.cs:319-321) are both just the <c>showHelmet</c> ARGUMENT to the
        /// funnel we are un-blocking — neither is replicated state and neither should be, since which helmet
        /// a player looks at is that player's own view. So the reactive property that must hold is that a
        /// mirrored identity change repaints UNDERNEATH whichever toggle this peer is holding, and it does,
        /// with zero TFTV types named here and therefore no <c>TftvLateBinder</c> arm to bind late and
        /// nothing to degrade when TFTV is absent.
        ///
        /// Scoped like every other raiser (law 8): <c>RefreshTags</c> and <c>RefreshSoldierInfo</c> fire
        /// native UI events that an intent-capture seam listens to.
        /// </summary>
        private static void ReseedIdentityDisplay(GeoCharacter owner, GeoLevelController geo)
        {
            var cycle = geo?.View?.GeoscapeModules?.ActorCycleModule;
            if (cycle == null) return;
            using (SyncApplyScope.Enter())
            {
                // GeoCharacter.cs:568 — Identity → GameTags. Idempotent (ApplyGameTags MERGES), and needed
                // here because the mirrored write lands on the identity leaves, never on the tag list.
                if (owner != null) owner.RefreshTags();

                if (ActorCycleUnitsField?.GetValue(cycle) is IEnumerable cached)
                    foreach (var u in cached)
                        if (u is UnitDisplayData d && d.BaseObject is GeoCharacter c) d.Name = c.GetName();

                if (ActorCycleBuilderField?.GetValue(cycle) is AddonsCharacterBuilder builder)
                    builder.Addons?.Clear();

                // The label itself: no repaint path calls this (UIStateEditSoldier.DisplaySoldier:580-585
                // rebuilds the equip lists and the doll, never the header), and it now reads a fresh copy.
                if (cycle.CurrentUnit != null) cycle.RefreshSoldierInfo();
            }
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
        // UIStateVehicleRoster.OnVehicleCycled (:214-227) = this screen's OWN model-change reaction: the
        // handler the native selection path raises (UIModuleVehicleCycle.SelectVehicle:197 →
        // OnVehicleChanged, subscribed at UIStateVehicleRoster.cs:77). READ-direction throughout — :222
        // re-selects the slot from the LIVE module (so the highlight follows the model, not a stale
        // _initialVehicle), :223-226 rebuild weapons/modules/storage widgets from
        // GetBaseObject<GeoVehicle>().Weapons/Modules + ViewerFaction.AircraftItemStorage.Items.
        // Only `newVehicle` is read, and only for the null branch.
        // Its write-direction siblings are deliberately NOT called: UpdateVehicleEquipments:244-276
        // (ReplaceEquipments) and UpdateAircraftStorage:278-290 (RemoveItems/AddItems on the live storage)
        // push the UI lists BACK into the model — which is exactly what ExitState:128-131 does and why the
        // Exit+Enter fallback reverted every delta this screen had just been sent.
        private static readonly MethodInfo VrCycled = AccessTools.Method(typeof(UIStateVehicleRoster), "OnVehicleCycled",
            new[] { typeof(VehicleDisplayData), typeof(VehicleDisplayData), typeof(bool) });
        // UIStateNothingSelected.OnFactionObjectivesChanged (:519-525): objectives module + section-bar
        // diplomacy count. Site markers live on GeoscapeView, outside any view state — nothing else on
        // this screen reads the model.
        private static readonly MethodInfo NsObjectives = AccessTools.Method(typeof(UIStateNothingSelected), "OnFactionObjectivesChanged");
        // UIStateUnitCustomization<T>.RefreshSelectedCharacter (:136-140) — the customization screens' own
        // model-change reseed, resolved per closed form because the declaring base is generic. See the two
        // Table entries below for why the Exit+Enter fallback cannot be used on these two screens.
        private static readonly MethodInfo ScRefreshSelected =
            AccessTools.Method(typeof(UIStateSoldierCustomization), "RefreshSelectedCharacter");
        private static readonly MethodInfo VcRefreshSelected =
            AccessTools.Method(typeof(UIStateVehicleCustomization), "RefreshSelectedCharacter");
        // UIModuleBaseLayout.SetLeftSideInfo (:605, also its own PxBase.Stats.OnStatsUpdated handler)
        // + SetupBaseLayout (:561, re-inits every slot from PxBase.Layout). Deliberately NOT
        // UIModuleBaseLayout.Init — Init closes an open build menu / facility info (:291-292).
        private static readonly MethodInfo BlLeftInfo = AccessTools.Method(typeof(UIModuleBaseLayout), "SetLeftSideInfo");
        private static readonly MethodInfo BlLayout = AccessTools.Method(typeof(UIModuleBaseLayout), "SetupBaseLayout");
        private static readonly FieldInfo BlSlots = AccessTools.Field(typeof(UIModuleBaseLayout), "_slots");

        /// <summary>Internal, not private: the RailCheck L21 belt reads the KEYS — a screen whose ExitState
        /// writes back into the live model must be declared here, or the Exit+Enter fallback undoes deltas.</summary>
        internal static readonly Dictionary<Type, Func<GeoscapeViewState, GeoscapeView, bool>> Table =
            new Dictionary<Type, Func<GeoscapeViewState, GeoscapeView, bool>>
            {
                // First two table citizens = the former special-case branches (9b35194 manufacturing
                // opt-out, research per-kind path); their rebuild knowledge stays in the family files.
                [typeof(UIStateManufacturing)] = (s, v) => { ManufactureSync.RepaintManufacturingUi(); return true; },
                // Event dialog: refresh the stale Record ref + re-drive the module's own SetChoices, which
                // re-reads per-button affordability AND re-applies the first-answer-wins freeze. The
                // fallback re-enter is doubly forbidden here — its ExitState answers a still-Triggered
                // event with Choices.Last() (UIStateGeoscapeEvent.cs:61-65). RailCheck L21 asserts this key.
                [typeof(UIStateGeoscapeEvent)] = (s, v) => EventPopup.RepaintDialog(s, v),
                // Modal dialog: still never Exit+Enter'd — a re-enter would run UIStateGeoModal.ExitState:116,
                // which (a) invokes the HOST's own DialogCallback with ModalResult.Close on a window nobody
                // closed (ResearchCompleteModalHandler, a mission Cancel arm…) and (b) fires
                // GeoscapeView.ModalClosed, the very signal a future host→all hide would read as "the host
                // dismissed it". Almost every modal renders a frozen snapshot of something that ALREADY
                // HAPPENED and genuinely has nothing to re-read. The ability-purchase confirmation is the one
                // that is not: it is an OFFER, still unpaid, standing over a SHARED purse — see
                // CloseUnaffordableBuyConfirm.
                [typeof(UIStateGeoModal)] = (s, v) => { CloseUnaffordableBuyConfirm(s, v); return true; },
                // Kaos marketplace: also a queued window, so the fallback Exit+Enter skips it (and must —
                // re-entering re-posts the shop's opening narration). Its rows come off 0xBF and its prices
                // are paid from the SHARED wallet, so a spend anywhere else must re-gate this screen's buttons.
                [typeof(UIStateMarketplaceGeoscapeEvent)] = (s, v) => { MarketplaceSync.RepaintOpenMarketplace(); return true; },
                // Third queued-window citizen, and for it a Table entry is the ONLY repaint that can ever
                // exist: PauseHold.IsCurrentQueuedWindow makes the fallback re-enter skip this state
                // (GeoWindowCoverage.cs:174 declares it queued), and skip it it must — EnterState:29
                // RequestGamePause()s and rebinds OkButton. Unlike the cutscene next to it, though, this
                // screen's whole content is model-derived, so "nothing to repaint" is false here.
                // What goes stale is _missingItems: UIStateReplenish.cs:31 snapshots GetMissingItems() into
                // the module at Enter, and the screen's own model-change handler (OnResourcesChanged:53-59)
                // only re-reads COSTS — so a peer replenishing a soldier leaves every other peer's list
                // naming gear that is already back on the roster. Init is the module's own read-direction
                // reseed of exactly that (UIModuleReplenish.cs:118-130: re-reads Manufacture.Queue +
                // missingItems, rebuilds the rows) and writes NOTHING into the model — the write-direction
                // audit UIStateVehicleRoster needed finds nothing to avoid here, because this screen stages
                // nothing: a click goes straight into the mirrored manufacture queue (SingleItemReplenish
                // :205-208 → AddToQueue), so there is no local edit a repaint could eat.
                // ponytail: Init also drops the hover back to the first row (OnCurrentItemsChanged:176-193).
                // Native does the same on every wallet change (OnResourcesChanged:55 ResetCurrentlySelected),
                // so it is not new behaviour; if it ever bites, split the missing-items re-read out of Init.
                [typeof(UIStateReplenish)] = (s, v) =>
                {
                    var m = v.GeoscapeModules.ReplenishModule;
                    var ctx = StateContext(s);
                    if (m == null || ctx == null ||
                        !(Viewer() is PhoenixPoint.Geoscape.Levels.Factions.GeoPhoenixFaction px)) return false;
                    m.Init(ctx, px.GetMissingItems());
                    return true;
                },
                [typeof(UIStateResearch)] = (s, v) => { ResearchSync.RepaintResearchUi(); return true; },
                [typeof(UIStateEditSoldier)] = (s, v) =>
                {
                    if (EsSelectProgression == null) return false; // resolve-all-first: decline BEFORE the reseed mutates anything
                    if (!ReseedEquipScreen(s, v.GeoscapeModules, EsRefreshFlag, EsGetData, EsDisplay, EsRefreshStorage)) return false;
                    // Progression panel: ALWAYS the full native reseed from the mirrored model — the
                    // client-posture law (IntentRail.ShouldRunNative doc) repaints from the model, own
                    // echo included. The reseed also recomputes the visit's undo baseline; the
                    // StageBaselines checkpoint below puts it back (see OpenUiRepaint).
                    return Call(EsSelectProgression, s, v.GeoscapeModules.ActorCycleModule.CurrentCharacter);
                },
                [typeof(UIStateEditVehicle)] = (s, v) =>
                {
                    var mods = v.GeoscapeModules;
                    if (!ReseedEquipScreen(s, mods, EvRefreshFlag, EvGetData, EvDisplay, EvRefreshStorage)) return false;
                    // progression panel: EnterState calls the module directly (UIStateEditVehicle.cs:162);
                    // always the full native reseed — same client-posture law as the edit-soldier entry
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
                [typeof(UIStateVehicleRoster)] = (s, v) =>
                {
                    var cycle = v.GeoscapeModules.VehicleCycleModule;
                    if (cycle?.CurrentVehicle == null) return false; // empty vehicle bay → fallback re-enter
                    // THE DOCKED GATE READS A SNAPSHOT, NOT THE MIRROR. VehicleDisplayData.CurrentSite is
                    // copied ONCE, in the UIStateVehicleRoster ctor (:52 → VehicleDisplayData.cs:36), and no
                    // delta ever touches that copy — so UIModuleVehicleCycle.InPhoenixBase:135-149, the very
                    // predicate :226 hands to UIModuleVehicleEquip.UpdateData as `inPhoenixBase` (which is
                    // what sets EnableEventHandlers on the weapon/module/storage lists, :271-280), kept
                    // answering "docked" after a mirrored take-off. The slot lists stayed live for an
                    // aircraft the host knew was at a mission site, and the loadout the player then dragged
                    // was refused (VehicleSync.IsDocked). GeoVehicle.CurrentSite IS covered rail state
                    // (rail-baseline.txt:712) — the mirror was right all along, only the copy was stale.
                    // Re-copy it here and the native rebuild below re-derives the gate from live state, on
                    // the ALREADY-OPEN screen; the location label (RefreshVehicleInfo:256 → GetSiteLabel)
                    // reads the same field and stops lying for free.
                    var liveVehicle = cycle.CurrentVehicle.GetBaseObject<GeoVehicle>();
                    if (liveVehicle != null) cycle.CurrentVehicle.CurrentSite = liveVehicle.CurrentSite;
                    if (!Call(VrCycled, s, null, cycle.CurrentVehicle, false)) return false;
                    cycle.UpdateAircraftInfoController();                                // header/info panel (:330)
                    v.GeoscapeModules.VehicleRoster?.UpdateSelectedVehicleEquipments();  // roster slot (:112)
                    return true;
                },
                [typeof(UIStateNothingSelected)] = (s, v) => Call(NsObjectives, s, Viewer()),
                // THE HALF L149 STATES AND NOTHING ENFORCED — a mirrored identity change must repaint
                // UNDERNEATH whichever helmet toggle THIS peer is holding. Both customization screens were
                // missing from this table, so they took the Exit+Enter fallback, and
                // `UIStateSoldierCustomization.EnterState`:24 slams `HideHelmetToggle.isOn = true` on every
                // entry (the screen's default is helmet HIDDEN, :48 passes `!isOn` as `showHelmet`). So every
                // mirrored batch that marked this screen dirty threw the viewer's helmet choice away and
                // re-hid the helmet, while a recolour — which the re-enter DOES re-read — kept working. That
                // is exactly the reported divergence: colours sync, the helmet toggle "does nothing". The
                // state landed on the rail all along; the REPAINT destroyed the view argument.
                // `RefreshSelectedCharacter` is the game's own reseed for these screens and the only one that
                // preserves it: `OnNewCharacter` re-Inits every selector from the LIVE identity and its tail
                // raises `OnCustomizationChanged` (UIModuleSoldierCustomization.cs:135,
                // UIModuleVehicleCustomization.cs:31), which `UIStateUnitCustomization.EnterState`:60 bound to
                // `RefreshUnitDisplay` — whose overrides (soldier :44-48, vehicle :24-28) re-read the live
                // toggle and drive `DisplaySoldier`/`DisplayVehicle`. The identity reseed has already emptied
                // `AddonsCharacterBuilder.Addons`, so the :636-644 addon-set early-out cannot swallow an
                // identity-only change on the way through.
                // TFTV's edit-screen icon rides along for free, for the same reason it always did: its prefix
                // sits on the GAME's `DisplaySoldier`, which this path calls — no TFTV type is named here, so
                // no late bind and nothing to degrade when TFTV is absent (L149 arm (c)).
                [typeof(UIStateSoldierCustomization)] = (s, v) => RepaintCustomization(s, v, ScRefreshSelected),
                [typeof(UIStateVehicleCustomization)] = (s, v) => RepaintCustomization(s, v, VcRefreshSelected),
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

        /// <summary>Kinds carrying no character/item content — pure geoscape WORLD simulation (map sites,
        /// aircraft, havens, alien bases, generated missions, run statistics, the geoscape log). Named once
        /// and shared, because "world layer" is a property of the KIND, not of any one screen. Every entry
        /// is a type the classifier covers (docs/rail-baseline.txt) and every one appeared in the client
        /// log as a permanently-churning unmapped kind.</summary>
        private static readonly Type[] WorldLayerKinds =
        {
            typeof(GeoSite), typeof(GeoVehicle), typeof(GeoHaven), typeof(GeoAlienBase),
            typeof(GeoAlienBaseMission), typeof(GeoScavengingMission), typeof(GeoHavenDefenseMission),
            typeof(GeoHavenDefenseMissionInstanceData), typeof(AlienRaidManager),
            typeof(GeoscapeStats), typeof(GeoscapeLog),
        };

        /// <summary>
        /// RELEVANCE, declared in the EXCLUSION direction: per screen, the entity kinds whose deltas
        /// provably cannot change anything that screen paints. Read by
        /// <see cref="OpenUiRepaint.MarkDirty(Type, GeoLevelController)"/> — a marked kind listed here does
        /// not dirty that screen. The defect it closes: the blanket default arm in <see cref="UiEventMap"/>
        /// let 15 permanently-churning world kinds mark the open screen dirty on EVERY rail batch, and on
        /// UIStateEditSoldier that is the heaviest entry in <see cref="Table"/> (faction-storage re-read +
        /// equip-list rebuild + a rebuild of the whole perk tree) → 4-5 fps.
        ///
        /// EXCLUSION and not inclusion, ON PURPOSE: an UNDECLARED kind — or any kind on an undeclared
        /// screen — still repaints, exactly as before. So a missing entry costs a wasted repaint
        /// (recoverable, and visible as frame rate) and can NEVER cost a stale screen, which is the
        /// silent-swallow class this repo fights. Inclusion would carry that risk the other way round.
        ///
        /// A row is a CLAIM, and RailCheck L38 proves it three ways: the screen must be in
        /// <see cref="Table"/> (a screen repainted by the Exit+Enter fallback has un-audited reads), the
        /// kind must be a type the classifier actually covers (a typo silences nothing), and — the arm that
        /// keeps this from becoming a stale-UI bug — the screen's own native <c>EnterState</c> must not
        /// reach a non-accessor method of that kind. Which is exactly why GeoPhoenixFaction, GeoCharacter
        /// and StatusStat are ABSENT here despite churning just as hard: UIStateEditSoldier.EnterState
        /// reaches `_faction.GetTotalAvailableStorage()` (:596) and `CurrentCharacter.HasLostHandStatus()` /
        /// `.GetEquippedItemHealthMap()` (:495-497).
        ///
        /// Only the kind-carrying marks in <see cref="UiEventMap.Fire"/> consult this. The structural
        /// appliers, intent rejects and post-intent reseeds mark UNCONDITIONALLY, so a site or aircraft
        /// APPEARING still repaints every screen — the ignore list is about a kind's leaf deltas, never
        /// about its identity coming or going.
        ///
        /// UIStateEditVehicle is deliberately NOT declared: same reseed shape, but its whole subject IS a
        /// GeoVehicle and its read path was not audited. One line to add once it is.
        ///
        /// UIStateBionics / UIStateMutate are deliberately NOT declared either, and that is a MEASURED
        /// decision, not a pending audit (2026-07-31): L38 ACCEPTS both rows — neither EnterState reaches a
        /// non-accessor method of any <see cref="WorldLayerKinds"/> entry — but a row here only pays for
        /// itself when it skips an expensive rebuild, and there is none to skip. Their <see cref="Table"/>
        /// entry is <c>RepaintAugmentScreen</c>, which is a guard-and-return: armed selection → return, or
        /// live armour still equal to snapshot/trial → return. What a row would save is a StageSnapshot
        /// miss plus one list compare at rail-batch rate — orders of magnitude under this repo's smallest
        /// measured main-thread cost — while adding two claims that must stay true across game updates and
        /// that gate the screen's own model-moved-under-me correctness arm. UIStateEditSoldier is declared
        /// because its rebuild is faction storage + both equip lists + the whole perk tree (4-5 fps).
        /// </summary>
        internal static readonly Dictionary<Type, HashSet<Type>> IgnoredKinds =
            new Dictionary<Type, HashSet<Type>>
            {
                [typeof(UIStateEditSoldier)] = new HashSet<Type>(WorldLayerKinds),
            };

        /// <summary>
        /// AN UNPAID OFFER OVER A SHARED PURSE IS NOT A FROZEN SNAPSHOT. Reported 2026-08-06: two peers
        /// opened the ability-purchase confirmation on the SAME soldier for DIFFERENT skills with one
        /// skill's worth of points between them, both pressed confirm, both skills were learned and the
        /// balance went negative. <see cref="PersonnelSync.HostSpendGate"/> is the correctness half — the
        /// host now asks <see cref="PersonnelSync.CanAfford"/> at the COMMIT, so the loser is simply refused
        /// and the balance cannot go below zero whoever wins the race. This is the REACTIVITY half
        /// (postulate 1): the loser should not still be standing in front of a live-looking Confirm button
        /// for points that are gone.
        ///
        /// THE WINDOW IS CLOSED, NOT GREYED, and the reason is that closing is the only version made of
        /// native parts. <c>ConfirmBuyAbilityDataBind</c> holds references to the cost text, the icon, the
        /// name and the description — and to NO BUTTON (the Confirm button is wired to
        /// <c>UIModal.Confirm()</c> in the prefab), and <c>UIModal</c> exposes none either, so greying it
        /// would mean hunting a button down the modal's transform tree and guessing which one it is: a
        /// hand-rolled widget lookup that FAILS SILENTLY when it misses, which is the exact bug class this
        /// repo fights. Closing is one call to the game's OWN modal dismissal —
        /// <c>GeoscapeView.FinishQueriedState</c>:2164, which is literally what <c>UIStateGeoModal.OnCancel</c>
        /// :151-155 does when the player presses cancel — and its <c>ExitState</c>:116-119 then hands the
        /// still-unhandled callback <c>ModalResult.Close</c>, i.e. <c>UIStateEditSoldier.ConfirmationHandler</c>
        /// :719-722 runs <c>ClearBoughtAbility()</c> and the offer is properly released.
        ///
        /// AND THE GREYING THE PLAYER ASKED FOR IS WHAT THEY LAND ON. The screen underneath is
        /// UIStateEditSoldier, whose Table entry above reseeds the whole progression panel from the model,
        /// and the ability tree greys an unaffordable slot NATIVELY (<c>AbilityTrackContainerElement
        /// .SetAbilitySlot</c>:230-248 → <c>IsAbilityBuyable</c> → <c>SetSkillState(isBuyable:false)</c>, and
        /// <c>AbilityTrackSkillEntryElement.OnPointerClick</c>:153-158 will not even fire for a slot that is
        /// not buyable). So the window drops away and the skill behind it is already grey — with no widget
        /// of ours anywhere in it.
        ///
        /// NOT A QUORUM and nothing waits: this peer reads its own mirrored model and decides alone.
        /// Deliberately narrow — only <c>CharacterProgressionConfirmCharacter</c>, only when the offer has
        /// become unaffordable. Every other modal keeps the old no-op.
        /// </summary>
        private static void CloseUnaffordableBuyConfirm(GeoscapeViewState s, GeoscapeView v)
        {
            if (!(s is UIStateGeoModal modal) || v == null) return;
            if (modal.ModalType != ModalType.CharacterProgressionConfirmCharacter) return;
            if (!(modal.ModalData is ConfirmBuyAbilityDataBind.Data data) || data.AbilitySlot == null) return;
            // The offer's owner without a registry scan: this modal is only ever opened by UIStateEditSoldier
            // for the soldier the actor cycle is showing (UIStateEditSoldier.cs:693-707 builds Data from
            // _currentCharacter, which is that module's CurrentCharacter). Identity-checked, and if the two
            // ever disagree we leave the offer alone rather than price the wrong soldier's purse.
            var owner = v.GeoscapeModules?.ActorCycleModule?.CurrentCharacter;
            if (owner == null || owner.Progression == null ||
                !ReferenceEquals(owner.Progression, data.Progression)) return;
            int cost = PersonnelSync.AbilityCost(owner, data.Progression, data.AbilitySlot, data.Ability);
            if (PersonnelSync.CanAfford(owner, cost)) return;
            Debug.Log("[MP][personnel] buy-confirm CLOSED U#" + (int)owner.Id + " — " +
                      (data.Ability == null ? "the offer" : data.Ability.name) + " costs " + cost +
                      " and only " + (owner.Progression.SkillPoints + PersonnelSync.SharedPool(owner)) +
                      " is left; another peer spent the points while this window was open");
            v.FinishQueriedState();   // GeoscapeView.cs:2164 — the game's own modal dismissal
        }

        /// <summary>True = the open screen has a native rebuild and it ran (caller skips the re-enter).
        /// Called inside OpenUiRepaint's SyncApplyScope + try/catch — a throwing rebuild is logged there
        /// and the screen kept, never demoted to Exit+Enter.</summary>
        internal static bool TryRepaint(GeoscapeViewState current, GeoscapeView view)
        {
            return Table.TryGetValue(current.GetType(), out var rebuild) && rebuild(current, view);
        }

        // ─── UI-SESSION BASELINE — declarative, keyed like the Table above ──────────────────────
        // A screen that stages an edit also keeps a per-VISIT BASELINE: the value the visit started
        // at, which the screen's own native undo/revert gate compares against. That baseline is UI
        // state, not model state — but every reseed recomputes it FROM the model
        // (UIModuleCharacterProgression.RefreshStats:516-518), so repainting the peer that is
        // mid-edit raises its undo floor to "already spent": the native minus gate
        // (ChangeCharacterStat:907 `currentStatValue - 1 >= startingStatValue`) and its highlight
        // (:799-808) go false forever and the spend can never be taken back. The spend itself is
        // fine — only the peer's ability to undo it dies, and only on the peer that clicked.
        // Declared here, applied generically: OpenUiRepaint saves before / restores after the
        // reseed, PersonnelSync re-aligns at the native stage→model flush. No screen is named in
        // either mechanism.
        // NOT in this table: list-shaped snapshots (UIModuleBionics/UIModuleMutate
        // CharacterOriginalItems) — a numeric clamp is meaningless for them, and
        // RepaintAugmentScreen already preserves those by declining to reseed.

        /// <summary>One staged value: the visit baseline field + the live stage field it is compared
        /// against. Declared as a pair because both consumers need both halves.</summary>
        internal sealed class StagePair
        {
            internal FieldInfo Baseline;
            internal FieldInfo Stage;
            /// <summary>Which side of the model value this baseline must stay on. A STAT baseline is a
            /// FLOOR — the visit only spends it UP, so baseline ≤ model. A POOL baseline is a CEILING —
            /// the visit only spends it DOWN, so baseline ≥ model. Same rule, opposite direction.</summary>
            internal bool Ceiling;
        }

        /// <summary>Baseline owner TYPE → its staged values. Internal for the RailCheck bind law.</summary>
        internal static readonly Dictionary<Type, StagePair[]> StageBaselines = new Dictionary<Type, StagePair[]>
        {
            [typeof(UIModuleCharacterProgression)] = Pairs(typeof(UIModuleCharacterProgression),
                floors: new[]
                {
                    "_startingStrengthStat", "_currentStrengthStat",
                    "_startingWillStat",     "_currentWillStat",
                    "_startingSpeedStat",    "_currentSpeedStat",
                },
                // The SHARED SP pool's baseline is not an undo floor — it is what the native refund
                // SPLIT reads: ChangeCharacterStat:915-931 pays a decrement back into the FACTION pool
                // up to `_startingFactionPoints` and only the remainder into the soldier's own pool. A
                // repaint that reseeds it to the live model makes that read say "the shared pool was
                // never touched", so the whole refund lands personal — on every peer, with nothing red.
                // OUT on purpose: `_startingMutagens` (mutoids have ONE pool, no split to lose) and
                // `_startingSkillPoints` (no native decision reads it).
                ceilings: new[] { "_startingFactionPoints", "_currentFactionPoints" }),
        };

        /// <summary>Screen → the object its baseline lives on (the module, not the view state).</summary>
        private static readonly Dictionary<Type, Func<GeoscapeView, object>> BaselineOwners =
            new Dictionary<Type, Func<GeoscapeView, object>>
            {
                [typeof(UIStateEditSoldier)] = v => v.GeoscapeModules.CharacterProgressionModule,
            };

        /// <summary>Bind at declaration time: a renamed/retyped field is a LOUD dead entry, never a
        /// silent one (the int type is also what lets the snapshot skip defensive casts).</summary>
        private static StagePair[] Pairs(Type owner, string[] floors, string[] ceilings)
        {
            var pairs = new List<StagePair>((floors.Length + ceilings.Length) / 2);
            Bind(owner, floors, ceiling: false, pairs);
            Bind(owner, ceilings, ceiling: true, pairs);
            return pairs.ToArray();
        }

        private static void Bind(Type owner, string[] names, bool ceiling, List<StagePair> pairs)
        {
            for (int i = 0; i + 1 < names.Length; i += 2)
            {
                var b = AccessTools.Field(owner, names[i]);
                var s = AccessTools.Field(owner, names[i + 1]);
                if (b != null && s != null && b.FieldType == typeof(int) && s.FieldType == typeof(int))
                    pairs.Add(new StagePair { Baseline = b, Stage = s, Ceiling = ceiling });
                else
                    Debug.LogError("[Multiplayer][rail] stage-baseline bind FAILED on " + owner.Name + "." +
                                   names[i] + " — that screen's undo floor is unprotected against repaints");
            }
        }

        /// <summary>The one non-mechanical rule of the restore. NEVER put the floor above what the
        /// reseed just read from the model: <c>saved &lt;= fresh</c> is the normal case (this visit's
        /// own spends) and keeps the undo window open, while <c>saved &gt; fresh</c> means the points
        /// are gone — a foreign refund, a respec or a host reject — and re-claiming them would let
        /// this peer refund what it no longer owns. A CEILING baseline (a spent-down pool) is the same
        /// rule mirrored: never put it BELOW the live value, or native's own cap
        /// (ChangeCharacterStat:928-931 writes `_currentFactionPoints = _startingFactionPoints`) would
        /// push that lower number back into the pool and destroy points instead of mis-splitting them.</summary>
        internal static int ClampBaseline(int saved, int fresh, bool ceiling = false) =>
            ceiling ? (saved > fresh ? saved : fresh) : (saved < fresh ? saved : fresh);

        /// <summary>Baseline of the open screen, captured BEFORE a repaint and restored after. A screen
        /// with no declaration captures nothing and restores nothing.</summary>
        internal struct StageSnapshot
        {
            private object _owner;
            private StagePair[] _pairs;
            private int[] _saved;

            internal static StageSnapshot Capture(GeoscapeViewState screen, GeoscapeView view)
            {
                var snap = default(StageSnapshot);
                if (screen == null || view == null) return snap;
                if (!BaselineOwners.TryGetValue(screen.GetType(), out var ownerOf)) return snap;
                var owner = ownerOf(view);
                if (owner == null || !StageBaselines.TryGetValue(owner.GetType(), out var pairs)) return snap;
                var saved = new int[pairs.Length];
                for (int i = 0; i < pairs.Length; i++) saved[i] = (int)pairs[i].Baseline.GetValue(owner);
                snap._owner = owner;
                snap._pairs = pairs;
                snap._saved = saved;
                return snap;
            }

            /// <summary>After the reseed the baseline field holds the FRESH model value (that is what
            /// the native refresh writes into it) — which makes it both the value to clamp against and
            /// the correct answer when nothing was staged.</summary>
            internal void Restore()
            {
                if (_owner == null) return;
                for (int i = 0; i < _pairs.Length; i++)
                {
                    var f = _pairs[i].Baseline;
                    f.SetValue(_owner, ClampBaseline(_saved[i], (int)f.GetValue(_owner), _pairs[i].Ceiling));
                }
            }
        }

        /// <summary>The native stage→model flush is about to run: make its baseline the live stage so
        /// the flush's DELTA half is a no-op. For peers where the model already carries every staged
        /// click (PersonnelSync applies each gesture on its own) and only the flush's ABSOLUTE half
        /// must stay native — that half is what a native ability purchase pays with.</summary>
        internal static void AlignStageBaseline(object owner)
        {
            if (owner == null || !StageBaselines.TryGetValue(owner.GetType(), out var pairs)) return;
            foreach (var p in pairs) p.Baseline.SetValue(owner, p.Stage.GetValue(owner));
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
            // MID-INTERACTION. An armed body-part selection is UNCOMMITTED LOCAL INPUT, and the rebind
            // below takes it: OnNewCharacter ends by running UnselectAllMutations() + ClearMutationSelection()
            // on every section (UIModuleBionics.cs:136-154), which nulls _selectedMutationSlot. SelectMutation
            // (UIModuleMutationSection.cs:173-259) is a TOGGLE keyed on exactly that field and it also owns
            // the confirm button's visibility (:250 MutateButton.SetActive(... && _selectedMutationSlot != null
            // && ...)), so nulling it behind the user's back INVERTS the next click and makes the AUGMENT
            // button blink away and back on every rail batch. Decline instead: the screen stays as the user
            // left it and converges the moment they commit or clear the selection.
            // Deliberately NOT OpenUiRepaint.LocalInputInFlight: that is a BOUNDED global defer
            // (MaxDeferFrames, "do not raise the cap") for input a repaint would physically yank out of the
            // user's hand. A body-part selection can legitimately sit armed for minutes while the player
            // reads the cost, and parking it there would stall every OTHER screen's repaint too.
            if (SelectionArmed(module)) return true;
            var live = cur.ArmourItems;
            if (SameItems(live, snapshot) || SameItems(live, trial)) return true; // snapshot/trial still valid
            // The model moved under the screen (a foreign augment on this soldier, or a reject re-emit).
            // ADOPT it as the visit baseline BEFORE the native rebind — the order is the whole fix:
            //   • OnNewCharacter OPENS with RevertUnconfirmedChanges() = SetItems(CharacterOriginalItems),
            //     which stamps the STALE baseline back over the armour the rail just mirrored in. The host's
            //     value did not change, so no later delta re-ships it and that peer stays diverged until the
            //     law-7 CRC backstop heals it.
            //   • It then re-snapshots ArmourItems into the baseline and runs InitCharacterInfo(), so a
            //     preview still sitting in that list is BAKED IN as a real augment. InitCharacterInfo counts
            //     it, hits MAX_AUGMENTATIONS (2) and sets AugumentSlotState.AugumentationLimitReached →
            //     ResetContainer(...) → the slot is locked with nothing committed. That is the "armour can
            //     no longer be selected or cleared after clicking helmets and legs" report.
            // Adopting first makes the revert a no-op against the live set and the recount truthful.
            snapshot.Clear();
            snapshot.AddRange(live);
            if (module is UIModuleBionics b2) b2.OnNewCharacter(cur);
            else ((UIModuleMutate)module).OnNewCharacter(cur);
            mods.ActorCycleModule?.DisplaySoldier(cur, resetAnimation: false, addWeapon: false);
            return true;
        }

        // ─── Augment-screen staging state — bound from the decompile, never guessed ──────────────
        // UIModuleBionics.cs:51 / UIModuleMutate.cs:54  private Dictionary<AddonSlotDef, UIModuleMutationSection> _augmentSections
        // UIModuleMutationSection.cs:78                 private UIModuleMutationsSlot _selectedMutationSlot
        private static readonly FieldInfo BionicsSections = RequireField(typeof(UIModuleBionics), "_augmentSections");
        private static readonly FieldInfo MutateSections = RequireField(typeof(UIModuleMutate), "_augmentSections");
        internal static readonly FieldInfo SectionSelectedSlot = RequireField(typeof(UIModuleMutationSection), "_selectedMutationSlot");

        /// <summary>Bind loudly: a renamed member after a game update must be a red line in the log, not a
        /// repaint that silently goes back to clobbering the user's selection. Same contract as
        /// <see cref="Bind"/>.</summary>
        private static FieldInfo RequireField(Type owner, string name)
        {
            var f = AccessTools.Field(owner, name);
            if (f == null)
                Debug.LogError("[Multiplayer][rail] augment-screen bind FAILED on " + owner.Name + "." + name +
                               " — a repaint can no longer see an armed body-part selection and will clobber it");
            return f;
        }

        /// <summary>True when this augment module has an ARMED body-part selection anywhere, i.e. the local
        /// user is mid-interaction. Internal so RailCheck can assert the guard is the one the repaint
        /// consults.</summary>
        internal static bool SelectionArmed(object module)
        {
            var sectionsField = module is UIModuleBionics ? BionicsSections
                              : module is UIModuleMutate ? MutateSections : null;
            if (sectionsField == null || SectionSelectedSlot == null) return false;
            if (!(sectionsField.GetValue(module) is IDictionary sections)) return false;
            foreach (var section in sections.Values)
            {
                if (section == null) continue;
                // Unity's overloaded != also answers "destroyed", which a plain null check would miss.
                var slot = SectionSelectedSlot.GetValue(section) as UnityEngine.Object;
                if (slot != null) return true;
            }
            return false;
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

        /// <summary>The context the OPEN state was pushed with — <c>GeoscapeViewState.Context</c>:20, set
        /// once in <c>Push</c>:84 and the very value every EnterState hands its modules. Reflected because
        /// it is protected; the alternative (reading a module's own private <c>_context</c> back out) says
        /// the same thing one indirection later and only works for modules that already ran Init.</summary>
        private static readonly MethodInfo StateContextGetter =
            AccessTools.PropertyGetter(typeof(GeoscapeViewState), "Context");

        private static GeoscapeViewContext StateContext(GeoscapeViewState s) =>
            StateContextGetter == null || s == null ? null : StateContextGetter.Invoke(s, null) as GeoscapeViewContext;

        private static GeoFaction Viewer()
        {
            var level = GameUtl.CurrentLevel();
            var geo = level == null ? null : level.GetComponent<GeoLevelController>();
            return geo == null ? null : geo.ViewerFaction;
        }

        /// <summary>Both closed forms of <c>UIStateUnitCustomization&lt;T&gt;</c> repaint through the SAME
        /// inherited reseed, so the guard lives here once rather than in each entry: an empty cycle would
        /// hand <c>OnNewCharacter</c> a null character (UIModuleSoldierCustomization.cs:77 dereferences it),
        /// and declining hands that case to the fallback re-enter, which rebuilds the roster from scratch.</summary>
        private static bool RepaintCustomization(GeoscapeViewState s, GeoscapeView v, MethodInfo refreshSelected)
        {
            var cycle = v.GeoscapeModules.ActorCycleModule;
            if (cycle == null || cycle.CurrentCharacter == null) return false;
            return Call(refreshSelected, s);
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
