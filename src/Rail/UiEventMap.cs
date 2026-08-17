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
    ///   • Any other kind → the universal repaint, logged ONCE. NOT a to-do list, and reading it as one
    ///     is a standing invitation to over-engineer: the default arm marks EXACTLY what the per-kind arms
    ///     mark, so a kind is fully reactive with no arm of its own. An arm earns its place only when a
    ///     NATIVE cache or derived value sits between the mirrored leaves and the pixels and the game
    ///     invalidates it from its own write path alone (Wallet, ItemStorage, CharacterProgression,
    ///     GeoCharacter, GeoPhoenixFacility, CharacterIdentity — every one of them a measured stale screen).
    ///     Audited 2026-08-13 against a live 3-peer session: all 22 logged kinds — GeoSite, GeoVehicle,
    ///     GeoHaven, GeoAlienBase, GeoscapeStats, GeoFaction, GeoPhoenixFaction, GeoAlienFaction,
    ///     GeoPhoenixBase, GeoSquad, GeoCustomMission, GeoScavengingSite, FactionDiplomacy, ItemManufacturing,
    ///     LevelProgression, PostmissionReplenishManager, PhoenixStatistics, StatusStat, UnityDateTime,
    ///     MapPlotInstanceData, MistState, DeployCountdownState — repaint through the universal seam, so ZERO
    ///     arms were added. Two carry their own non-UI surface and would gain nothing from one either:
    ///     MistState is loaded by the native renderer (MistSync.Apply → MistRendererSystem.ProcessInstanceData)
    ///     and DeployCountdownState is POLLED per frame by the overlay (DeployCountdown.DisplaySecondsLeft),
    ///     while its START-button gate rides UIStateRosterDeployment's own Table entry.
    ///   • RELEVANCE: every arm marks through <c>MarkDirty(kind, geo)</c>, so the OPEN screen may
    ///     decline a kind it provably cannot paint (<see cref="UiNativeRepaint.IgnoredKinds"/>).
    ///     Undeclared kind or undeclared screen still marks — the exclusion is opt-in, never implied.
    /// </summary>
    public static class UiEventMap
    {
        private static readonly HashSet<string> _loggedUnknown = new HashSet<string>(StringComparer.Ordinal);

        public static void Fire(HashSet<GenericApplier.TouchedLeaf> touched, GeoLevelController geo)
        {
            if (touched == null || touched.Count == 0) return;
            bool researchDone = false;
            bool geoLogDone = false;
            foreach (var leaf in touched)
            {
                // §B.1: the exact rail path that changed, carried to the mark instead of discarded.
                // null (structural create/destroy, a preparation edit, a durable choice) routes to the
                // unconditional arm — exactly what the ~63 kindless call sites already do.
                var entity = leaf.Entity;
                var path = leaf.Path;
                if (entity == null) continue;
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
                            OpenUiRepaint.MarkDirty(entity.GetType(), geo, path);
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
                                // The RAISE, from inside the apply and nowhere earlier — L49's ordering arm:
                                // a research-complete window drawn before the 0xAC deltas that unlock its
                                // follow-ups shows an empty "new researches" group. Queued HERE and nowhere
                                // else in the normal case, which is what keys the window to the message
                                // that carried it (RailOrdinal.Current != 0) — the game's own drain gate
                                // then holds it if this peer is inside a screen (L496/L511).
                                ResearchSync.PumpDeferredCompletions(geo);
                                ResearchSync.RepaintResearchUi();
                            }
                            break;
                        case GeoscapeLog _:
                            // THE SECOND PRESENTATION CHANNEL (L550). The log's keyless _entries list is
                            // written straight into the field by the applier, so GeoscapeLog.AddEntry never
                            // runs here and OnNewEntry — the ONLY thing that reaches
                            // UIModuleStatusBarMessages.ShowEventMessage (GeoscapeView.cs:292/1576) —
                            // never fires on a peer that did not simulate the event. Re-raise it from the
                            // mirrored list, behind the cursor seeded before this batch. The widget is a
                            // QUEUE (ShowEventMessage:74 appends, the module's own Update():174 drains it
                            // next frame), so this repaints an ALREADY-OPEN geoscape with no re-entry.
                            if (!geoLogDone)
                            {
                                geoLogDone = true;
                                GeoLogNotice.PresentFromMirror(geo);
                            }
                            // The mirrored LOG SCREEN (UIStateGeoscapeLog) is a separate, pull-model
                            // surface: keep the universal mark the default arm used to give this kind.
                            OpenUiRepaint.MarkDirty(entity.GetType(), geo, path);
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
                            OpenUiRepaint.MarkDirty(entity.GetType(), geo, path);
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
                            OpenUiRepaint.MarkDirty(entity.GetType(), geo, path);
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
                            OpenUiRepaint.MarkDirty(entity.GetType(), geo, path);
                            break;
                        case PhoenixPoint.Geoscape.Events.GeoscapeEventSystem _:
                            // A record delta NEVER raises a window (windows are live 0xB6 raises) — but it
                            // IS the answer/dismiss signal: the repaint refreshes an open dialog's stale
                            // Record ref and closes a picker somebody else already answered
                            // (EventPopup.RepaintDialog, the UiNativeRepaint entry for UIStateGeoscapeEvent).
                            // Site encounter labels etc. ride the same universal repaint like any other kind.
                            OpenUiRepaint.MarkDirty(entity.GetType(), geo, path);
                            break;
                        case CharacterIdentity ci:
                            // THE REPAINT WAS FIRING ALL ALONG AND PAINTED NOTHING (live 2026-08-06:
                            // "no per-kind mapping for CharacterIdentity — universal open-screen repaint").
                            // Identity is the one kind whose whole presentation sits behind TWO caches the
                            // game only ever invalidates from its OWN write path, so a mirrored write is
                            // invisible until the visit is torn down and rebuilt — the "leave the screen and
                            // come back" the user measured. Both are re-seeded here, before the flush.
                            ReseedIdentityDisplay(OwnerOf(ci, geo), geo);
                            OpenUiRepaint.MarkDirty(entity.GetType(), geo, path);
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
                            OpenUiRepaint.MarkDirty(entity.GetType(), geo, path);
                            break;
                        default:
                            // Law 11 universal cover, guaranteed HERE: any kind without a per-kind
                            // native event still repaints the open geoscape screen through the
                            // generic seam, regardless of what the caller does after Fire(). The ONE
                            // exception is declared, not implicit: a kind listed against the open
                            // screen in UiNativeRepaint.IgnoredKinds cannot change anything that
                            // screen paints, and skipping it is logged once per kind per screen.
                            OpenUiRepaint.MarkDirty(entity.GetType(), geo, path);
                            if (_loggedUnknown.Add(entity.GetType().Name))
                                MpLog.Log("[Multiplayer][rail] UiEventMap: " + entity.GetType().Name + " rides the universal open-screen repaint (logged once)");
                            break;
                    }
                }
                catch (Exception ex) { MpLog.LogWarning("[Multiplayer][rail] UiEventMap fire failed for " + entity.GetType().Name + ": " + ex.Message); }
            }
        }

        internal static void FirePreparationEdit(HashSet<object> touched, GeoLevelController geo,
            OccurrenceId occurrence, ulong revision)
        {
            touched = touched ?? new HashSet<object>();
            foreach (var subject in occurrence.SubjectIds)
            {
                var resolved = IdentityResolver.Resolve(geo, subject, null);
                if (resolved != null) touched.Add(resolved);
                var site = resolved as GeoSite;
                var mission = site?.ActiveMission;
                if (mission == null) continue;
                touched.Add(mission);
                if (DeploymentRosterRefresh.TryCount(mission, null, out var sources, out var soldiers))
                {
                    foreach (var source in sources) if (source != null) touched.Add(source);
                    foreach (var soldier in soldiers) if (soldier != null) touched.Add(soldier);
                }
            }
            var leaves = new HashSet<GenericApplier.TouchedLeaf>();
            // A preparation edit names SUBJECTS, not rail paths: every leaf goes in with a null path so
            // Fire routes it to the unconditional repaint arm, exactly as it did before §B.1.
            foreach (var subject in touched) leaves.Add(new GenericApplier.TouchedLeaf(subject, null, null));
            Fire(leaves, geo);
            // The durable ledger update above refreshes queued/suspended copies before they can be
            // displayed.  The open native preparation screen is callback-free and must participate in
            // the same coalesced read-direction repaint as the touched campaign entities.
            OpenUiRepaint.MarkPreparationDirty(geo);
        }

        /// <summary>READ-direction durable-choice nudge. The ledger/canonical transaction is committed
        /// before this is called; routing through the real event-system kind reaches OpenUiRepaint and its
        /// native UIStateGeoscapeEvent entry without closing or re-opening another player's dialog.</summary>
        internal static void FireDurableChoice(GeoLevelController geo)
        {
            if (geo?.EventSystem == null) return;
            Fire(new HashSet<GenericApplier.TouchedLeaf> { new GenericApplier.TouchedLeaf(geo.EventSystem, null, null) }, geo);
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
        /// <remarks>§B.9 — CLEARING <c>Addons</c> FORCES THE REBUILD BRANCH (UIModuleActorCycle.cs:635-654:
        /// an empty Addons list can never equal the character's armour set, so the next DisplaySoldier
        /// takes <c>CharacterLoadingIndicator.SetActive(true)</c> + <c>CommonCharacterUtils.RebuildCharacter</c>).
        /// That is correct for an identity that REALLY changed and gratuitous destruction for anything else,
        /// so it must not be reachable from an unrelated write. IT IS NOT, and the gate is already in place
        /// one seam up rather than duplicated here: <c>GenericApplier</c>:1345 only puts a leaf in
        /// <c>touched</c> when <c>LeafChanged(before, after)</c> — so <see cref="Fire"/>'s CharacterIdentity
        /// arm, the only caller, runs on a real value difference. The remaining <c>touched.Add</c> sites are
        /// structural (:463/:666/:682) or a census PRUNE that already found extras (:2235), all of which are
        /// real changes too. No second cache of the identity key is kept here on purpose: it would be a
        /// duplicate of that gate and would drift from it.</remarks>
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
        // (:587-597), OnDataChanged = wallet/tags panel, the doll+equip-list paint below, then the
        // progression panel.
        //
        // §B.9 PATCH, DON'T REBUILD. The state's own private DisplaySoldier is deliberately NOT bound
        // any more (it was EsDisplay/EvDisplay). UIStateEditSoldier.DisplaySoldier:580-585 ends in
        // _actorCycleModule.DisplaySoldier(character, resetAnimation: true) and
        // UIStateEditVehicle.DisplaySoldier:344-348 in DisplayVehicle(character, resetAnimation: true)
        // — and `true` is the ONLY thing that reaches CommonCharacterUtils.ResetCharacterAnimation =
        // Animator.Play(0, -1, 0f) (CommonCharacterUtils.cs:66-73, called from UIModuleActorCycle
        // .cs:640 / :695). A repaint driven by ANOTHER peer's delta therefore restarted the soldier's
        // idle animation from frame 0 on a screen its owner was merely looking at.
        // PaintSoldierDoll/PaintVehicleDoll below reproduce those three lines verbatim with
        // resetAnimation: false, exactly the shape RepaintAugmentScreen already uses. Nothing else is
        // lost: the rebuild branch (UIModuleActorCycle.cs:653-654, CharacterLoadingIndicator +
        // CommonCharacterUtils.RebuildCharacter) is keyed on the ADDON SET differing, not on
        // resetAnimation, so a mirrored armour change still rebuilds the doll and an unrelated delta
        // no longer does.
        private static readonly FieldInfo EsRefreshFlag = AccessTools.Field(typeof(UIStateEditSoldier), "_refreshStorage");
        private static readonly MethodInfo EsGetData = AccessTools.Method(typeof(UIStateEditSoldier), "GetSoldierEquipModuleData");
        // UIStateEditSoldier.cs:50 / UIStateEditVehicle.cs:45 — the flag the state's own DisplaySoldier
        // raises (:583 / :347) so the next UpdateState (:459-486 / :360) re-derives the weight/endurance
        // readouts. Set it here, or the doll repaints and the carry-weight label keeps the old number.
        private static readonly FieldInfo EsUiRefreshNeeded = AccessTools.Field(typeof(UIStateEditSoldier), "_uiRefreshNeeded");
        private static readonly MethodInfo EsRefreshStorage = AccessTools.Method(typeof(UIStateEditSoldier), "RefreshStorage");
        private static readonly MethodInfo EsSelectProgression = AccessTools.Method(typeof(UIStateEditSoldier), "SelectCharacterProgression", new[] { typeof(GeoCharacter) });
        private static readonly FieldInfo EvRefreshFlag = AccessTools.Field(typeof(UIStateEditVehicle), "_refreshStorage");
        private static readonly MethodInfo EvGetData = AccessTools.Method(typeof(UIStateEditVehicle), "GetSoldierEquipModuleData");
        private static readonly FieldInfo EvUiRefreshNeeded = AccessTools.Field(typeof(UIStateEditVehicle), "_uiRefreshNeeded");
        // UIStateEditVehicle.cs:335-341 — the private filter the state's DisplaySoldier feeds
        // UpdateVehicleData with. Reflected rather than re-implemented: it IS the game's own definition
        // of which equipment lands in which slot list.
        private static readonly MethodInfo EvVehicleEquipment = AccessTools.Method(typeof(UIStateEditVehicle), "GetVehicleEquipment",
            new[] { typeof(GeoCharacter), typeof(PhoenixPoint.Common.Entities.Equipments.GroundVehicleEquipmentType) });
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
        /// <summary>
        /// THE TWO MODULES BOTH GEOSCAPE STATES OWN, AND NEITHER ARM PAINTED. UIStateNothingSelected
        /// (:103 SetDiplomacyDisplay, :131 _pxBaseManagementModule.Init) and UIStateVehicleSelected
        /// (:200, :231) drive them from their own EnterState — and both Table arms below return true,
        /// which SUPPRESSES the Exit+Enter fallback that was their only repaint. So on every peer that
        /// did not perform the gesture the diplomacy pip strip and the base-list personnel/aircraft
        /// counters froze until the player left the screen and came back. This is the same gap
        /// <see cref="OpenUiRepaint.RefreshPersistentHud"/> closes for modules that span states; these two
        /// belong to these two states, so they are closed HERE, once, for both arms.
        ///
        /// DELETING THE ARMS WAS THE OTHER OPTION AND IT IS UNSAFE. Neither EnterState is a repaint:
        /// UIStateNothingSelected:76-82 SWITCHES to UIStateVehicleSelected whenever the viewer owns an
        /// aircraft (and :134-139 switches the other way), and both call
        /// <c>GameUtl.GetFreeCursorController().ForceCursorToCenterScreen()</c> (:87 / :144) — i.e. a
        /// fallback re-enter would yank the player's cursor to the middle of the screen at rail rate.
        /// UIStateVehicleSelected.ExitState:244-248 also writes back into the model
        /// (<c>SelectedVehicle.Range.RangeIndicatorVisible = false</c>, <c>Animator.SetBool</c>).
        ///
        /// NEITHER PAINT IS A REBUILD. The base list is refreshed row by row through the row's OWN public
        /// <c>RefreshVisuals()</c> (SiteManagementRow.cs:56-64 → ShowSiteStats: two text assignments), which
        /// is exactly the branch <c>UIModuleSiteManagement.FillList</c>:182-188 takes when nothing structural
        /// moved — FillList itself is NOT driven, because its first statement is <c>SelectRow(null)</c>
        /// (:158), i.e. it would drop the row the player has selected once per rail batch, and Init is worse
        /// still (:98-102 HideModule()s the panel). ponytail: a base APPEARING therefore still needs the
        /// native <c>_changed</c> path; upgrade only if that is ever reported, it is a structural create.
        /// The diplomacy strip has no per-pip refresh — <c>SetDiplomacyDisplay</c> destroys and re-instantiates
        /// the pips (:28, :74) — so it is gated on its own declared scope through the SAME
        /// <see cref="OpenUiRepaint.ScopeKey"/>/<see cref="OpenUiRepaint.RepaintNeeded"/> pair the
        /// persistent-HUD strips use, and costs nothing on a batch that touched no faction.
        /// </summary>
        private static void RepaintSharedGeoscapeModules(GeoscapeViewState s, GeoscapeView v)
        {
            var mods = v == null ? null : v.GeoscapeModules;
            if (mods == null) return;
            var context = StateContext(s);
            var diplomacy = mods.DiplomacyDisplayModule;
            if (context != null && diplomacy != null && diplomacy.gameObject.activeInHierarchy &&
                OpenUiRepaint.RepaintNeeded("diplomacy", OpenUiRepaint.ScopeKey("UIModuleDiplomacyDisplay")))
                diplomacy.SetDiplomacyDisplay(context);
            var bases = mods.BaseManagementModule;
            if (bases == null || bases.BasesContainer == null || !bases.IsModuleExtended) return;
            foreach (var row in bases.BasesContainer.GetComponentsInChildren<SiteManagementRow>())
                if (row != null && row.Site != null) row.RefreshVisuals();
        }

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
                // The mission-brief arm joined it 2026-08-09: a brief is the squad screen's own front door
                // (ModalResultCallback:825 -> LaunchMission:1043 -> ToDeploymentState), so a brief for a
                // mission this peer has no container left to deploy from must go the same way the screen does.
                [typeof(UIStateGeoModal)] = (s, v) =>
                {
                    CloseUnaffordableBuyConfirm(s, v);
                    DeploymentWindowClose.CloseDepartedBrief(s);
                    return true;
                },
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
                // Fourth queued-window citizen, and like the replenish screen above a Table entry is the ONLY
                // repaint it can ever have: it holds the current queued slot (GeoscapeView.cs:592-600,
                // priority int.MaxValue, held until FinishCurrentStateSwitch:116), so
                // PauseHold.IsCurrentQueuedWindow makes the fallback Exit+Enter SKIP it — and skip it it must,
                // because a re-enter re-seeds every soldier's pose. What goes stale is the SET the screen is
                // backed by: _characterContainers (UIStateRosterDeployment.cs:26) comes from
                // GeoMission.GetDeploymentSources, so an aircraft flying off takes its soldiers with it and
                // the open screen must lose them — and lose ITSELF when the last container goes.
                [typeof(UIStateRosterDeployment)] = (s, v) => DeploymentWindowClose.RepaintDeploymentScreen(s, v),
                [typeof(UIStateResearch)] = (s, v) => { ResearchSync.RepaintResearchUi(); return true; },
                // TFTV BaseRework force-unlocks the vanilla Recruits tab and rebuilds it as ASSIGNMENTS, so
                // this screen had no entry at all and fell back to Exit+Enter — a full teardown that happens
                // to rebuild the panel through TFTV's own EnterState postfix. Its own RefreshPanel() is the
                // read-direction rebuild P11 asks for. The TFTV lookup stays inside the helper, reached by
                // name, so nothing on the rail side acquires a load-order dependency (L149 arm (c)); the
                // helper answers FALSE when TFTV is absent and the fallback runs exactly as before.
                [typeof(UIStateRosterRecruits)] = (s, v) => AssignSync.RepaintAssignmentsPanel(),
                [typeof(UIStateEditSoldier)] = (s, v) =>
                {
                    if (EsSelectProgression == null) return false; // resolve-all-first: decline BEFORE the reseed mutates anything
                    if (!ReseedEquipScreen(s, v.GeoscapeModules, EsRefreshFlag, EsGetData, PaintSoldierDoll, EsRefreshStorage)) return false;
                    // Progression panel: ALWAYS the full native reseed from the mirrored model — the
                    // client-posture law (IntentRail.ShouldRunNative doc) repaints from the model, own
                    // echo included. The reseed also recomputes the visit's undo baseline; the
                    // StageBaselines checkpoint below puts it back (see OpenUiRepaint).
                    return Call(EsSelectProgression, s, v.GeoscapeModules.ActorCycleModule.CurrentCharacter);
                },
                [typeof(UIStateEditVehicle)] = (s, v) =>
                {
                    var mods = v.GeoscapeModules;
                    if (!ReseedEquipScreen(s, mods, EvRefreshFlag, EvGetData, PaintVehicleDoll, EvRefreshStorage)) return false;
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
                    // THE TAB BAR IS NOT PART OF THE ROSTER REBUILD. This arm returns true, which
                    // suppresses the universal fallback re-enter — so EnterState:151 never runs again and
                    // the tabs keep whatever availability they were painted with when the screen opened.
                    // Every one of them is derived state a mirrored delta moves: Recruits reads
                    // RecruitmentFunctionalityUnlocked, Aliens AlienContainmentUnlocked (both now live
                    // through the faction GameTags rail row), Memorial Level.DeadSoldiers.Count, Aliens'
                    // interactable CapturedUnits.Count. Native re-derives all of them from the model in
                    // ONE call and writes nothing back (UIStateGeoRoster.cs:151 ->
                    // UIModuleGeoRosterTabs.CheckAvailableTabs:74-90), which is exactly what a repaint may
                    // do. ShowFilterButtons (:401) is deliberately NOT called: it toggles on the view's
                    // deployment mode, which no rail delta moves.
                    var rosterCtx = StateContext(s);
                    if (rosterCtx?.ViewerFaction is PhoenixPoint.Geoscape.Levels.Factions.GeoPhoenixFaction)
                        v.GeoscapeModules.GeoRosterTabsModule?.CheckAvailableTabs(rosterCtx);
                    return true;
                },
                [typeof(UIStateVehicleSelected)] = (s, v) =>
                {
                    // resolve-all-first: a null MethodInfo mid-&&-chain would run the earlier refreshes,
                    // then decline → the fallback re-enter stacked on a partial repaint. Decline up front.
                    if (VsSelected == null || VsTabs == null || VsActions == null || VsReachable == null || VsObjectives == null) return false;
                    if (VsSelected.GetValue(s, null) == null) return false;
                    RepaintSharedGeoscapeModules(s, v);
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
                [typeof(UIStateNothingSelected)] = (s, v) =>
                {
                    RepaintSharedGeoscapeModules(s, v);
                    return Call(NsObjectives, s, Viewer());
                },
                // NO ENTRY FOR THE CUSTOMIZATION SCREENS, ON PURPOSE (reverted 2026-08-08, was d51f24d).
                // `UIStateUnitCustomization<T>` writes nothing back in `ExitState` (:67-79 is pure unbind),
                // so the fallback Exit+Enter is safe here — and it is also the only one of the two paths that
                // rebuilds the actor cycle the screen's whole doll hangs off. The entry it replaced routed
                // every rail batch through `RefreshSelectedCharacter` → `CustomizationModule.OnNewCharacter`,
                // which is a seam foreign mods postfix to re-seed their own per-screen widgets from campaign
                // state (TFTV does) — i.e. it handed a once-per-visit reseed a once-per-BATCH trigger. Its
                // stated reason was wrong anyway: it read `EnterState`:24 out of the vanilla decompile alone,
                // and a mod that owns the toggle re-derives it after that line on the same entry.
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
                [typeof(UIStateEditSoldier)] = EveryWorldKindExcept(typeof(GeoVehicle)),
            };

        /// <summary>
        /// WHAT EACH SURFACE READS, AS STATIC RAIL PATH PREFIXES. Keyed by the view state's NAME, not its
        /// Type — the name form is what lets a surface belonging to a mod this assembly cannot reference
        /// (TFTV's own panels) be declared at all, and it is the same form <c>WindowOrder.MapStates</c>
        /// already uses for the same reason (src/Rail/WindowOrder.cs:243-248).
        ///
        /// STATIC, NEVER INFERRED AT RUNTIME. Automatic read tracking only sees synchronous reads, so an
        /// inferred declaration silently misses a dependency (MobX missed-dependency class, §2.5). The
        /// declaration is written here, once, by a human who read the screen.
        ///
        /// NO DECLARATION ⇒ THE SURFACE REPAINTS ON EVERYTHING (<see cref="OpenUiRepaint.SurfaceRepaints"/>,
        /// asserted by L541). Declaration is in the OPT-IN-TO-SCOPE direction ONLY; there is no way to
        /// declare "I read nothing". A forgotten surface degrades to today's behaviour, never to stale data.
        ///
        /// PREFIX DEPTH IS THE WHOLE TUNING KNOB (Firebase, §2.5). A prefix that stops at a ROOT repaints on
        /// everything under that root — a no-op declaration for that root, not a bug.
        ///
        /// THE ONE REAL TRAP (§B.3). The rail has TWO collection shapes and they declare differently:
        ///   • FieldClass.EntityCollection (KEYED) — each element has its own <c>…#&lt;stableKey&gt;</c>
        ///     subtree (DiffEngine.cs:1306-1311). A prefix is safe and meaningful at ANY depth, and
        ///     inserting an element renames nothing because the key comes from the element's own identity.
        ///   • FieldClass.EntityList (KEYLESS) — the WHOLE list is ONE canonical value blob AT THE FIELD
        ///     PATH (DiffEngine.cs:1229-1232). There are no per-element paths at all, so a prefix DEEPER
        ///     than the field name can never match and would silently subscribe to nothing. CHECK THE
        ///     FIELD'S FieldClass BEFORE WRITING A DEEP PREFIX.
        ///
        /// ORDER IS CARRIED AT THE FIELD PATH: a pure reorder ships an explicit ORDER vector as a
        /// SubKey == "" entry ON THE FIELD, so a surface that renders ORDER must declare the FIELD path.
        ///
        /// NO SEGMENT IS EVER AN INDEX (§B.3, Q5): DiffEngine.cs:1231 forbids element indices in the path
        /// and aborts the field with a loud Incident rather than falling back to one (:1299-1302). So a
        /// prefix subscription is safe on every path the rail emits.
        /// </summary>
        internal static readonly System.Collections.Generic.Dictionary<string, string[]> DeclaredPrefixes =
            new System.Collections.Generic.Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                // ONE surface to start, and the one the defect was reported on. The roots below are its
                // AUDITED reads and nothing else — the same audit <see cref="IgnoredKinds"/> above records:
                //   "U#" GeoCharacter — the soldier it paints (perks, progression, statuses, equipment).
                //   "V#" GeoVehicle   — the SQUAD ROSTER this screen cycles through rides inside
                //                       GeoVehicle.SerializationData. Dropping it is a MEASURED regression:
                //                       2026-08-08 a dismissal on one peer left the soldier standing on
                //                       another peer's open edit screen. It is why GeoVehicle is the one
                //                       kind excluded from the ignore row above, and it must be here too.
                //   "F#" GeoFaction   — EnterState:596 reads _faction.GetTotalAvailableStorage().
                // Scoped away: "T"/"TA" the clock, "S#" site + haven ticks, "ES"/"MG"/"MK"/"ST"/"GL"/"M#".
                // ponytail: "F#" stays at the ROOT because a faction key embeds the faction def GUID
                // ("F#" + Def.Guid, IdentityResolver.cs:146), so no STATIC deeper prefix can match one —
                // which means GeoFaction.Manufacture ticks still repaint this screen. Upgrade path if that
                // is the churn worth killing: a wildcard segment in the matcher, declared as
                // "F#*.ItemStorage". Not built speculatively — the roots above are already the safe answer.
                { "UIStateEditSoldier", new[] { "U#", "V#", "F#" } },

                // THE RESEARCH SCREEN. Its Table arm is ResearchSync.RepaintResearchUi ->
                // RebuildOpenResearchScreen, which is a CLOSED read set: the arm answers true for every
                // open UIStateResearch (ResearchSync.cs:403-424, the catch arm included), so the Exit+Enter
                // fallback never runs for this screen and the three module calls below are ALL of it.
                //   "F#" GeoFaction — every row the three calls paint:
                //     SetupQueue      -> _faction.Research.Current / .Last / .InQueueCount / .ResearchQueue
                //                        (UIModuleResearch.cs:336-366) and each row's Progress01/ResearchCost,
                //                        which are ResearchElement instance data under GeoFaction.Research.
                //     ShowAvailable   -> _faction.Research.Visible (:306), each row's requirement COUNTER
                //                        (ResearchListItem.cs:110-121 -> ResearchRequirement._progress, stored
                //                        under the same Research subtree) and CanResearch()'s wallet gate.
                //     SetupAllied     -> _faction.Allies[].Research.Current (:368-378) — other factions are
                //                        F# roots too, so one prefix carries the allied strip as well.
                //     RefreshFilters  -> _faction.Research.Visible/.Completed and _alienFaction.Research
                //                        .Completed (:280-285).
                //     The "new research" dots read GeoPhoenixFaction.NewEntityKnowledge (:240-243).
                //   "S#" GeoSite — THE ETA TEXT, and it is the half a faction-only declaration would lose.
                //     Every queue row prints Research.GetTotalTimeLeft (ResearchQueueItem.cs:121-124), whose
                //     rate is Faction.ResourceIncome.GetTotalResouce(Research) (Research.cs:649-651). A peer
                //     never receives ResourceIncome as rail state: it is REBUILT locally by the
                //     DerivedAggregateRefresh row GeoFaction/UpdateProduction, whose declared inputs are
                //     { "S#", "F#" } — i.e. a lab finished on another peer moves a SITE path and nothing
                //     under "F#" at all. L563 asserts exactly this pairing so the two cannot drift apart.
                // Scoped away, and the point of the row: "U#" (a soldier's gear, statuses and progression
                // are nowhere on this screen), "V#", "T"/"TA", "ES", "MG", "MK", "ST", "GL", "M#*".
                // TFTV touches this screen and does NOT widen the set: its only patches on UIModuleResearch
                // are Awake/Init/AddToQueue/SetupQueue postfixes that hang DRAG HANDLERS on the existing
                // queue items (TFTVDragandDropFunctionality.cs:43-137) — no new model readout, no new root.
                { "UIStateResearch", new[] { "F#", "S#" } },

                // UIStateNothingSelected and UIStateVehicleSelected are deliberately NOT declared, and this
                // is a REFUSAL rather than a pending audit (2026-08-18). Their arms end in
                // OnFactionObjectivesChanged -> UIModuleGeoObjectives.RefreshObjectives, and the objective
                // ROWS are the readout that cannot be attributed to a root set: GeoFaction.Objectives is
                // EXCLUDED from the rail entirely (docs/rail-baseline.txt:550/584, "blob husk on
                // GeoFactionObjective"), so the list is local simulation whose content comes from whatever
                // each subclass reads — vanilla alone spans sites, vehicles, faction diplomacy, research,
                // the event system AND GeoLevel.Tutorial (TutorialFactionObjective.cs:34-37, a "GL" read),
                // and TFTV adds its own objective rows in ten files plus paints into these two states
                // directly (TFTVAAAgenda/AgendaPatches.cs, TFTVUI/Geoscape/AircraftOrder.cs,
                // TFTVUI/Geoscape/TFTVHavenRecruitsUI/*, TFTVBaseRework/BaseActivation.cs). A prefix set
                // that misses one of those rows is a SILENTLY FROZEN screen, which is worse than the wasted
                // rebuild it would remove — so these two keep the undeclared default (L541: repaint on
                // everything) until somebody can enumerate the objective family end to end.

                // The persistent-HUD strips. They belong to no view state (that is the premise of
                // RefreshPersistentHud), so they are declared by MODULE name and keyed through
                // OpenUiRepaint.ScopeKey. Each set is the strip's OWN reads, converted from the signature
                // builder that §B.8 deleted — the conversion, row source by row source:
                //   UIModuleFactionAgendaTracker (was AgendaSignature, InitialSetup:144-174):
                //     Manufacture.Current + Research.Current live on GeoFaction -> "F#"; the aircraft
                //     action rows are GeoVehicle -> "V#"; the facility rows hang off GeoPhoenixBase, which
                //     IS a GeoSite (PhoenixPoint.Geoscape.Entities.Sites.GeoPhoenixBase) -> "S#".
                //   UIModuleInfoBar (was InfoBarKey, UpdatePopulation:276-288 + TFTV TopInforBar:127):
                //     the three diplomacy values are <Faction>.Diplomacy, a FactionDiplomacy that DESCENDS
                //     from GeoFaction (docs/rail-baseline.txt:527/561/594) -> "F#"; world population and
                //     the alien-base list are sites/havens -> "S#".
                //   UIModuleVehicleSelection (was CrewSignature): the vehicle -> "V#", its crew -> "U#".
                //   UIModuleGeoRoster (was RosterSignature): the slots' characters -> "U#".
                // "F#" is on BOTH top-right strips deliberately, and it is the half the plan's draft rows
                // omitted: dropping it would leave the research head and the reputation percentages — the
                // two defects these repaints were written for — stale on every peer.
                { "UIModuleFactionAgendaTracker", new[] { "F#", "S#", "V#" } },
                { "UIModuleInfoBar",              new[] { "F#", "S#" } },
                { "UIModuleVehicleSelection",     new[] { "V#", "U#" } },
                { "UIModuleGeoRoster",            new[] { "U#" } },
                // The diplomacy pip strip: SetDiplomacyDisplay reads <Faction>.Diplomacy through
                // MainDiplomacyRelationDisplayRowElement.DiplomacyRelation, the same stored FactionDiplomacy
                // the info bar's percentages come off (docs/rail-baseline.txt:527/561/594) -> "F#". Declared
                // here rather than left undeclared BECAUSE the paint destroys and re-instantiates every pip
                // (UIModuleDiplomacyDisplay.cs:28 + :74): undeclared would mean that teardown on every rail
                // batch, which is the L492 flicker class. It is driven from the two geoscape Table arms
                // (RepaintSharedGeoscapeModules), not from RefreshPersistentHud.
                { "UIModuleDiplomacyDisplay",     new[] { "F#" } },
            };

        /// <summary>Surfaces whose repaint MUST be a patch and never a rebuild (§B.9). Declared by NAME
        /// for the same reason <see cref="DeclaredPrefixes"/> is. Each one carries a live character doll
        /// with animation state, so the Exit+Enter fallback IS the animation reset for it, and each one
        /// therefore has a <see cref="Table"/> entry that paints with <c>resetAnimation: false</c>:
        /// <c>PaintSoldierDoll</c>, <c>PaintVehicleDoll</c> and <c>RepaintAugmentScreen</c>.
        /// The augmentation screens are TWO real states, <c>UIStateBionics</c> and <c>UIStateMutate</c>
        /// (decompile PhoenixPoint.Geoscape.View.ViewStates/UIStateBionics.cs:1 and UIStateMutate.cs:1) —
        /// there is no single "UIStateAugmentationScreen" type.</summary>
        internal static readonly System.Collections.Generic.HashSet<string> ModelAnimationSurfaces =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
            { "UIStateEditSoldier", "UIStateEditVehicle", "UIStateBionics", "UIStateMutate" };

        /// <summary>The exclusion row above, minus the kinds the screen DOES paint. Named rather than
        /// hand-listed so the exception carries its reason: <c>GeoVehicle</c> is where the squad lives.
        /// A soldier's membership of an aircraft rides inside <c>GeoVehicle.SerializationData</c>, so the
        /// roster this screen cycles through IS a GeoVehicle delta — declaring that kind irrelevant made
        /// the one screen that MUST react to a squad-roster change the one screen that could not. Measured
        /// 2026-08-08: a dismissal on one peer left the soldier standing on another peer's open edit
        /// screen, with `[MP][uirepaint] SKIP GeoVehicle on UIStateEditSoldier` as the only trace. The rest
        /// of <see cref="WorldLayerKinds"/> stays ignored — a site timer or a haven still has no business
        /// rebuilding the perk tree.</summary>
        private static HashSet<Type> EveryWorldKindExcept(params Type[] painted)
        {
            var set = new HashSet<Type>(WorldLayerKinds);
            foreach (var kind in painted) set.Remove(kind);
            return set;
        }

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
            MpLog.Log("[MP][personnel] buy-confirm CLOSED U#" + (int)owner.Id + " — " +
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
                    MpLog.LogError("[Multiplayer][rail] stage-baseline bind FAILED on " + owner.Name + "." +
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
                MpLog.LogError("[Multiplayer][rail] augment-screen bind FAILED on " + owner.Name + "." + name +
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
            FieldInfo refreshFlag, MethodInfo getData, Func<GeoscapeViewState, GeoscapeModulesData, GeoCharacter, bool> paintDoll,
            MethodInfo refreshStorage)
        {
            var cur = mods.ActorCycleModule == null ? null : mods.ActorCycleModule.CurrentCharacter;
            // resolve-all-first: every MethodInfo checked BEFORE the first invocation, so a renamed
            // member declines cleanly instead of running half the reseed and then falling back.
            if (cur == null || refreshFlag == null || getData == null || paintDoll == null || refreshStorage == null) return false;
            refreshFlag.SetValue(state, true); // next RefreshStorage re-reads the model, not the stale UI list
            mods.SoldierEquipModule.OnDataChanged((UIModuleSoldierEquipData)getData.Invoke(state, null));
            // reseeds the CURRENT character = selection preserved by construction
            return paintDoll(state, mods, cur) && Call(refreshStorage, state);
        }

        /// <summary>§B.9 — <c>UIStateEditSoldier.DisplaySoldier</c>:580-585 with <c>resetAnimation: false</c>.
        /// Line for line the same work: the equip lists off the model, the state's own refresh flag so
        /// UpdateState:459-486 re-derives the weight readouts, and the doll. Only the animation argument
        /// differs, and it is the whole defect — <c>true</c> is the sole path to
        /// <c>CommonCharacterUtils.ResetCharacterAnimation</c> (<c>UIModuleActorCycle.cs:640</c>).
        /// NOTHING IS LOST BY NOT RESETTING: the addon rebuild that actually re-dresses the model is
        /// keyed on the addon SET differing (<c>UIModuleActorCycle.cs:635-654</c>), never on this flag,
        /// so a mirrored armour/weapon change still takes the rebuild branch. <c>addWeapon</c>/
        /// <c>showHelmet</c> are left at their defaults exactly as the state passes them, which keeps
        /// TFTV's helmet-toggle prefix on this same overload in charge of what this peer looks at.</summary>
        private static bool PaintSoldierDoll(GeoscapeViewState state, GeoscapeModulesData mods, GeoCharacter c)
        {
            if (EsUiRefreshNeeded == null || mods.SoldierEquipModule == null || mods.ActorCycleModule == null) return false;
            mods.SoldierEquipModule.UpdateData(c.InventoryItems, c.EquipmentItems, c.ArmourItems, null,
                                               c.CharacterStats.GetInventorySlots());
            EsUiRefreshNeeded.SetValue(state, true);
            mods.ActorCycleModule.DisplaySoldier(c, resetAnimation: false);
            return true;
        }

        /// <summary>§B.9 — <c>UIStateEditVehicle.DisplaySoldier</c>:344-348 with <c>resetAnimation: false</c>.
        /// Same argument as <see cref="PaintSoldierDoll"/>; the vehicle reaches the reset through
        /// <c>UIModuleActorCycle.DisplayVehicle</c>:695 instead, and its rebuild branch (:704) is keyed on
        /// the addon set the same way.</summary>
        private static bool PaintVehicleDoll(GeoscapeViewState state, GeoscapeModulesData mods, GeoCharacter c)
        {
            if (EvUiRefreshNeeded == null || EvVehicleEquipment == null ||
                mods.SoldierEquipModule == null || mods.ActorCycleModule == null) return false;
            mods.SoldierEquipModule.UpdateVehicleData(c.InventoryItems, null,
                VehicleEquipment(state, c, PhoenixPoint.Common.Entities.Equipments.GroundVehicleEquipmentType.Weapon),
                VehicleEquipment(state, c, PhoenixPoint.Common.Entities.Equipments.GroundVehicleEquipmentType.Hull),
                VehicleEquipment(state, c, PhoenixPoint.Common.Entities.Equipments.GroundVehicleEquipmentType.Engine),
                c.CharacterStats.GetInventorySlots());
            EvUiRefreshNeeded.SetValue(state, true);
            mods.ActorCycleModule.DisplayVehicle(c, resetAnimation: false);
            return true;
        }

        private static IEnumerable<PhoenixPoint.Common.Entities.ICommonItem> VehicleEquipment(GeoscapeViewState state, GeoCharacter c,
            PhoenixPoint.Common.Entities.Equipments.GroundVehicleEquipmentType type) =>
            EvVehicleEquipment.Invoke(state, new object[] { c, type }) as IEnumerable<PhoenixPoint.Common.Entities.ICommonItem>;

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
