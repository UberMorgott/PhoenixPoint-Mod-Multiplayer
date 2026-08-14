using System;
using System.Reflection;
using Base.Core;
using Base.UI;
using HarmonyLib;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.Abilities;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewModules;
using PhoenixPoint.Geoscape.View.ViewStates;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Law 11, UNIVERSAL path. After a remote mirror batch lands on the CLIENT, repaint the OPEN
    /// geoscape screen. DEFAULT = the transition-free repaint primitive: <see cref="UiNativeRepaint"/>
    /// dispatches to the screen's own native read-direction refresh methods (model → live widgets, no
    /// lifecycle transition — scroll, selection and open sub-menus survive). LAST RESORT for screens
    /// not yet in that table = re-drive the native full-rebuild (Exit → Enter on the SAME view-state
    /// instance), flagged once per screen type in the log so the remaining unwired screens are visible.
    ///
    /// GROUNDED (decompile): GeoscapeViewState.Exit(stack) removes the input handler + runs ExitState();
    /// Enter(stack) re-adds it (AddUnique = no double-sub) + runs EnterState() = the screen's only
    /// generic rebuild path (there is no native Refresh/Invalidate common to all screens). Same instance
    /// preserves the ctor fields (_site/_base/_vehicle…); Context/_stateStack were set in Push and
    /// survive. Enter/Exit take the StateStack&lt;GeoscapeViewContext&gt; — the GeoscapeView._statesStack
    /// private field. But Exit is a TRANSITION, not a repaint — it tears down real resources and resets
    /// in-progress UI, which is why the table is the default and this is the fallback.
    ///
    /// DEBOUNCE: one mirror tick can arrive as several chunked GeoRail packets processed in ONE frame
    /// (NetworkEngine.Update drains all inbound via Transport.Update BEFORE Sync.Tick). Re-entering a
    /// screen is not free, so the batch boundary only sets a dirty flag; SyncEngine.Tick flushes exactly
    /// ONE re-enter per frame. The HOST marks dirty too (post-intent reseeds in EquipSync/PersonnelSync)
    /// — every repaint, both sides, rides the same defer + coalescing.
    /// </summary>
    public static class OpenUiRepaint
    {
        private static bool _dirty;
        private static bool _hudDirty;
        private static int _deferredFrames;
        private static bool _deferLogged;
        private static int _marksSinceFlush;
        private static float _nextDiagAt;
        private static readonly System.Collections.Generic.HashSet<string> _loggedFailures =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        private static readonly System.Collections.Generic.HashSet<string> _loggedFallback =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        private static readonly System.Collections.Generic.HashSet<string> _loggedSkips =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        /// <summary>Defer ceiling in frames (~5 s at 60 fps). See <see cref="FlushIfDirty"/>.</summary>
        private const int MaxDeferFrames = 300;

        private static readonly FieldInfo StatesStackField =
            AccessTools.Field(typeof(GeoscapeView), "_statesStack");

        // UIModuleFactionAgendaTracker: private no-arg UpdateData() (:179 — the body UpdateModuleDataCrt:125
        // calls) and the flag that makes it do a FULL rebuild (:186-190 -> InitialSetup:144).
        private static readonly MethodInfo TrackerUpdateData =
            AccessTools.Method(typeof(UIModuleFactionAgendaTracker), "UpdateData", Type.EmptyTypes);
        private static readonly FieldInfo TrackerNeedsRefresh =
            AccessTools.Field(typeof(UIModuleFactionAgendaTracker), "_needsRefresh");
        // Init(context):93 is what fills it; before the first geoscape state has entered, InitialSetup:144
        // would NRE on `_faction.Manufacture`. Asked rather than caught: the module handle below is the
        // NATIVE one, so it resolves long before the module is live.
        private static readonly FieldInfo TrackerContext =
            AccessTools.Field(typeof(UIModuleFactionAgendaTracker), "_context");

        /// <summary>The open state's OWN contextual-ability derivation, cached per state type. Deliberately
        /// resolved by NAME on whatever state is open instead of naming screens: the two states that drive
        /// the site menu build DIFFERENT lists — UIStateNothingSelected.GetContextualAbilities:682 is the
        /// site's abilities, UIStateVehicleSelected's:1481 prepends the selected vehicle's — so
        /// re-implementing either here would be a guess about which screen is up AND a second copy of a
        /// derivation the game already owns.</summary>
        private static readonly System.Collections.Generic.Dictionary<Type, MethodInfo> ContextualAbilitiesOf =
            new System.Collections.Generic.Dictionary<Type, MethodInfo>();

        private static MethodInfo ContextualAbilities(Type state)
        {
            if (!ContextualAbilitiesOf.TryGetValue(state, out var m))
                ContextualAbilitiesOf[state] = m =
                    AccessTools.Method(state, "GetContextualAbilities", new[] { typeof(GeoSite) });
            return m;
        }

        /// <summary>
        /// The half of law 11 that belongs to NO view state: the geoscape MODULES. <see cref="UiNativeRepaint
        /// .Table"/> is keyed by view-state TYPE, so a module that spans states — or outlives one — has no
        /// row that can reach it, no matter how many rows the table grows. That is one gap, not one bug per
        /// widget, and this method is where it is closed once: every such module is re-derived here, through
        /// the native <c>GeoscapeView.GeoscapeModules</c> handle, from the ONE universal repaint. Two live
        /// today — the top-right activity strip and the open site menu — and a third is another block here,
        /// never another syncer.
        ///
        /// The top-right
        /// agenda tracker (<c>UIModuleFactionAgendaTracker</c>) paints "Research: …", the current
        /// manufacturing item, every aircraft ACTION in progress (exploration included,
        /// <c>InitialSetup</c>:162-168 via <c>VehicleActionsViewService.GetCurrentActionTime</c>) and every
        /// facility being built — all rail-mirrored — yet it is not a <c>GeoscapeViewState</c>, so no entry
        /// in <see cref="UiNativeRepaint.Table"/> can ever reach it and <see cref="Repaint"/> alone left it
        /// stale on every peer that did not perform the gesture.
        ///
        /// Its native refresh cannot be borrowed as-is: <c>Init</c>:100 starts <c>UpdateModuleDataCrt</c> on
        /// the GAME clock (<c>_context.Level.Timing.Start</c>), so setting <c>_needsRefresh</c> and waiting
        /// — what this used to do, from ResearchSync — only repaints once the geoscape is UNPAUSED, and on
        /// the host nothing set the flag at all. So drive the module's own <c>UpdateData()</c> directly,
        /// with the flag on: that is precisely what the native poll does, one tick early.
        ///
        /// Called from the ONE universal repaint, so every mark — a client's rail batch, the host applying a
        /// remote intent (IntentRail.HandleInbound), a reject reconverge — refreshes it, for every kind,
        /// with no per-subsystem nudge anywhere.
        /// </summary>
        internal static void RefreshPersistentHud()
        {
            // THE NATIVE HANDLE, never UnityEngine.Object.FindObjectOfType. That is what this method used to
            // resolve the tracker with, and it is the R3 silent swallow: FindObjectOfType skips every
            // INACTIVE GameObject, and a geoscape module is switched off by the game itself —
            // UIModuleBehavior.SetStateID:34 calls `gameObject.SetActive(false)` for the state it is not
            // part of. So on any peer whose screen had the module off when the delta landed, the lookup
            // returned null and this whole refresh returned with NO log line at all; only walking into a
            // state that re-Inits the module (UIStateNothingSelected.EnterState:104) brought the strip back —
            // the reported "enter Research and come back and it appears". GeoscapeModulesData holds the
            // module by reference whether it is active or not, so the handle can never go quiet on us.
            var geo = GenericApplier.GeoLevel();
            var view = geo?.View;
            var mods = view?.GeoscapeModules;
            if (mods == null) return; // no geoscape view at all (tactical / main menu) — nothing to repaint
            RefreshAgendaTracker(mods);
            RefreshSiteContextualMenu(geo, mods, view.CurrentViewState);
        }

        /// <summary>Top-right activity strip. The flag makes UpdateData() take its InitialSetup branch
        /// (UIModuleFactionAgendaTracker.cs:186-190 → :144), i.e. the module's own full rebuild from
        /// Research.Current / Manufacture.Current / every vehicle action / every facility under
        /// construction — exactly what its game-clock poll does, one tick early.</summary>
        private static void RefreshAgendaTracker(GeoscapeModulesData mods)
        {
            if (TrackerUpdateData == null || TrackerNeedsRefresh == null || TrackerContext == null) return;
            var tracker = mods.FactionDataTracker;
            if (tracker == null)
            {
                if (_loggedFailures.Add("AgendaTrackerMissing"))
                    MpLog.LogWarning("[Multiplayer][rail] GeoscapeModulesData.FactionDataTracker is null — the " +
                                     "top-right activity strip has no handle to repaint and will only ever update " +
                                     "on its own game-clock poll (logged once)");
                return;
            }
            if (TrackerContext.GetValue(tracker) == null) return; // not Init'd yet — InitialSetup would NRE
            try
            {
                // law 8: the rebuild re-reads the model and can fire native UI events a capture seam hears.
                using (SyncApplyScope.Enter())
                {
                    TrackerNeedsRefresh.SetValue(tracker, true);
                    TrackerUpdateData.Invoke(tracker, null);
                }
            }
            catch (Exception ex)
            {
                if (_loggedFailures.Add("PersistentHud"))
                    MpLog.LogWarning("[Multiplayer][rail] persistent-HUD refresh threw — the top-right tracker may " +
                                     "stay stale until the next screen change (logged once): " + ex);
            }
        }

        /// <summary>
        /// The OPEN site menu — the popup carrying "Explore (Xh)", "Move", "Attack" over a clicked site.
        /// Second citizen of the same gap the tracker was the first of: <see cref="UiNativeRepaint.Table"/>
        /// is keyed by VIEW STATE, and this module belongs to no single one (both
        /// UIStateNothingSelected:60 and UIStateVehicleSelected:87 drive it), so no table entry can ever
        /// reach it. Most of its content IS derived — SetMenuItems:76-83 asks each ability
        /// `View.VisibleInContextMenu(target)` and drops the ones that answer no — so re-deriving is the
        /// right primitive for everything whose availability the model can express.
        ///
        /// RE-DERIVING IS NOT ENOUGH FOR AN OFFER THAT WAS TAKEN, and that is why R1 came back: the menu is
        /// a TRANSIENT OFFER, and on the peer that takes it the game does not re-derive anything — it
        /// CLOSES the menu, unconditionally, at UIStateVehicleSelected.OnContextualItemSelected:431 (and
        /// UIStateNothingSelected's twin). A mirror never walks that click path, so the offer stays on
        /// screen. For exploration the derivation cannot cover for it even in principle:
        /// ExploreSiteAbility.GetDisabledStateInternal:18-29 and GetTargetDisabledStateInternal:31-55 read
        /// crew, visibility, ExplorationTime, GetInspected and CurrentSite — and NEVER
        /// GeoVehicle.IsExploringSite:236, whose only readers in the whole assembly are the ability's own
        /// no-op guard (ExploreSiteAbility:12) and VehicleActionsViewService:179/:192. So "Explore" derives
        /// as visible AND activatable for the entire duration of the exploration, on every peer, exactly as
        /// it does in vanilla — and SetMenuItems would not have hidden it even if CanActivate had gone
        /// false, because a non-activatable ability is GREYED (SetInteractable:94), never dropped.
        /// Measured, not assumed (2026-08-05 00:17 session): the watcher's re-seed line
        /// "exploration re-seed V#1@8be7e872… → started" IS in its log, i.e. the state mirror lands and the
        /// spinner runs — the derived list simply does not change.
        ///
        /// So the offer is closed, on the EDGE — the batch in which this peer first learns the site is
        /// under exploration. State-based would be wrong here and not merely inelegant: rail batches arrive
        /// continuously, so "hide whenever the site is being explored" would slam the menu shut on the next
        /// batch every time a player deliberately opened it at an exploring site, which vanilla allows.
        /// The edge is sampled from the model on EVERY refresh, before every bail below, so a site whose
        /// exploration started while its menu was closed is already in the memo and never reads as a fresh
        /// edge later.
        ///
        /// NEVER OPENS ONE: guarded on the module's own <c>IsContextualMenuVisible</c>, so a peer that has
        /// no menu up keeps having none (the same rule that keeps repaints from re-raising popups —
        /// GenericApplier.RaiseArrivedForUi's doc). Read-direction only: SetMenuItems reads abilities and
        /// writes widgets, and HideContextualMenu:138-144 only SetActive(false)s three GameObjects — no
        /// view-state transition, so the popped-state caution does not apply.
        ///
        /// ponytail: exploration is the only order named here, because it is the only one whose in-progress
        /// state the native derivation is blind to (a departure moves CurrentSite/Travelling, which the
        /// derivation DOES read). Upgrade path if a second such order appears: widen
        /// <see cref="ExploringSiteRefs"/> to "sites with an order in flight", not another branch here.
        /// </summary>
        private static void RefreshSiteContextualMenu(GeoLevelController geo, GeoscapeModulesData mods,
                                                      GeoscapeViewState current)
        {
            var menu = mods.SiteContextualMenuModule;
            // UNCONDITIONAL, before every bail: the memo must track every site, not only the selected one.
            bool offerTaken = ExplorationJustStarted(_exploringSites, ExploringSiteRefs(geo),
                menu == null ? null : IdentityResolver.RootRef(menu.SelectedSite));
            if (menu == null || current == null) return;
            if (!menu.IsContextualMenuVisible || menu.SelectedSite == null) return;
            if (offerTaken)
            {
                // Not silent, and not once-per-site-per-session either: this is a per-exploration event, so
                // a line per close is a line per order — and "the menu closed on its own" is otherwise
                // indistinguishable in a log from "the player clicked something".
                MpLog.Log("[MP][uirepaint] site menu CLOSED for " + IdentityResolver.RootRef(menu.SelectedSite) +
                          " — its exploration has started, so the offer this menu made is already taken " +
                          "(the game closes it the same way on the peer that clicked)");
                menu.HideContextualMenu();
                return;
            }
            var derive = ContextualAbilities(current.GetType());
            if (derive == null) return; // this screen owns no site menu of its own — nothing to re-derive
            try
            {
                var abilities = derive.Invoke(current, new object[] { menu.SelectedSite })
                                as System.Collections.Generic.List<GeoAbility>;
                if (abilities == null) return;
                // Put the menu back where it already is: SetMenuItems:54 writes
                // `position + (CenterXOffset, CenterYOffset)` into the container, so feeding the current
                // position minus those offsets reproduces the exact same placement without needing the
                // camera — and the menu must not jump under a player who is aiming at a button.
                var anchor = menu.MenuButtonsContainer.GetComponent<RectTransform>().position
                             - new Vector3(menu.CenterXOffset, menu.CenterYOffset, 0f);
                // law 8: re-deriving runs native ability views, which a capture seam could hear.
                using (SyncApplyScope.Enter()) menu.SetMenuItems(menu.SelectedSite, abilities, anchor);
            }
            catch (Exception ex)
            {
                if (_loggedFailures.Add("SiteContextualMenu"))
                    MpLog.LogWarning("[Multiplayer][rail] site contextual-menu re-derive threw — its buttons may stay " +
                                     "stale until the player clicks the site again (logged once): " + ex);
            }
        }

        /// <summary>Sites this peer already knew were under exploration at the previous HUD refresh.
        /// Site REFS, not GeoSite handles: the same key GenericApplier's exploration seam logs with, and
        /// the only shape RailCheck L102 can drive without a live scene.</summary>
        private static readonly System.Collections.Generic.HashSet<string> _exploringSites =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        private static readonly System.Collections.Generic.HashSet<string> _exploringNow =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        /// <summary>Every site the player faction is CURRENTLY exploring, asked of the model — the same
        /// <c>GeoVehicle.IsExploringSite</c>:236 the mirror's own re-seed drives
        /// (GenericApplier.ReseedExploration), so host and client answer off one predicate.</summary>
        private static System.Collections.Generic.ICollection<string> ExploringSiteRefs(GeoLevelController geo)
        {
            _exploringNow.Clear();
            var faction = geo == null ? null : geo.PhoenixFaction;
            if (faction == null) return _exploringNow;
            foreach (var v in faction.Vehicles)
            {
                if (v == null || !v.IsExploringSite || v.CurrentSite == null) continue;
                var siteRef = IdentityResolver.RootRef(v.CurrentSite);
                if (siteRef != null) _exploringNow.Add(siteRef);
            }
            return _exploringNow;
        }

        /// <summary>THE EDGE, kept pure so RailCheck L102 can execute it case by case: did exploration at
        /// <paramref name="siteRef"/> START since the last call? <paramref name="seen"/> is re-based to
        /// <paramref name="now"/> on EVERY call — including the ones that ask about no site
        /// (<paramref name="siteRef"/> null, i.e. no menu open) — which is what makes a second call about
        /// the same still-exploring site answer false, so a player may open that site's menu and keep it.
        /// </summary>
        internal static bool ExplorationJustStarted(System.Collections.Generic.HashSet<string> seen,
                                                    System.Collections.Generic.ICollection<string> now,
                                                    string siteRef)
        {
            bool started = siteRef != null && now.Contains(siteRef) && !seen.Contains(siteRef);
            seen.Clear();
            foreach (var s in now) seen.Add(s);
            return started;
        }

        /// <summary>Close of a remote mirror-apply batch (client) or a host-side post-intent reseed.
        /// Coalesced to one re-enter per frame by <see cref="FlushIfDirty"/> — cheaper than re-entering
        /// per chunk on a multi-packet resend.</summary>
        public static void MarkDirty() { _dirty = true; _marksSinceFlush++; }

        /// <summary>
        /// The same mark, carrying WHICH KIND changed — so the OPEN screen can decline a change that
        /// cannot affect it. A screen must not be marked dirty by a kind it provably never paints: a
        /// soldier's inventory/perk sheet has no business rebuilding because a GeoSite timer ticked, and
        /// that blanket dirtying (15 churning kinds × every rail batch × the heaviest entry in
        /// UiNativeRepaint.Table) is what dropped that screen to 4-5 fps.
        ///
        /// The relevance itself is DECLARED, per screen, in <see cref="UiNativeRepaint.IgnoredKinds"/> and
        /// machine-checked by RailCheck L38 — never decided here. Asked at MARK time rather than at flush
        /// time because <see cref="UiEventMap.Fire"/> already holds the level, so the open-screen lookup is
        /// free, and nothing has to accumulate kinds across a frame boundary (a mark raised by a native
        /// event DURING a repaint then cannot be swallowed — <c>_dirty</c> was already cleared).
        ///
        /// CONSERVATIVE BY CONSTRUCTION: an undeclared kind, an undeclared screen, an exact-type miss on a
        /// subclass, or no open screen at all → marks, exactly like <see cref="MarkDirty()"/>.
        /// </summary>
        public static void MarkDirty(Type kind, GeoLevelController geo)
        {
            var screen = geo?.View?.CurrentViewState;
            if (screen != null && kind != null &&
                UiNativeRepaint.IgnoredKinds.TryGetValue(screen.GetType(), out var ignored) &&
                ignored.Contains(kind))
            {
                // Law 1 (silent swallow): a refusal to repaint is never invisible. Once per kind per screen
                // and NOT behind MpDiag — this runs at rail-batch rate, so a per-mark line would BE the
                // freeze it exists to report, while a diag-gated one would leave the default build silent
                // about work it declined to do.
                if (_loggedSkips.Add(screen.GetType().Name + ":" + kind.Name))
                    MpLog.Log("[MP][uirepaint] SKIP " + kind.Name + " on " + screen.GetType().Name +
                              " — kind declared irrelevant to this screen (logged once per kind per screen)");
                MarkHudDirty();
                return;
            }
            MarkDirty();
        }

        /// <summary>Material deployment edits use the same coalesced, drag-safe repaint boundary as a
        /// mirrored model batch, but name the callback-free roster primitive explicitly so a queued
        /// window can never fall through to destructive Exit/Enter.</summary>
        internal static void MarkPreparationDirty(GeoLevelController geo) =>
            MarkDirty(typeof(UIStateRosterDeployment), geo);

        /// <summary>The SCREEN declined this kind, so the persistent HUD still has to hear about it.
        /// <see cref="UiNativeRepaint.IgnoredKinds"/> is a claim about one VIEW STATE's own reads, and L38
        /// proves it against that state's <c>EnterState</c> — but the skip it drives used to suppress the
        /// whole of <see cref="RepaintOpenGeoscapeScreen"/>, and the first thing that does is
        /// <see cref="RefreshPersistentHud"/>, which belongs to no view state and was never in that audit.
        /// The tracker's rebuild reads exactly the aircraft actions UIStateEditSoldier declares irrelevant
        /// TO ITSELF (InitialSetup:162-168 → VehicleActionsViewService.GetCurrentActionTime), so a vehicle
        /// leaving on a mission while a peer sits on the soldier screen could drop its tracker row with
        /// nothing red. Masked in practice — GeoPhoenixFaction/GeoCharacter churn in the same batches and
        /// are not declared — which is exactly why it needed finding statically rather than in play.
        ///
        /// A named method, not an inline flag write: RailCheck L60 walks callees, and a stfld is invisible
        /// to a callee walk — an inline write would leave half this split unprovable.</summary>
        private static void MarkHudDirty() { _hudDirty = true; }

        /// <summary>
        /// A DESTROYED SOLDIER MUST NOT STAY UNDER SOMEBODY'S CURSOR. Reported 2026-08-08: a client
        /// dismissed a soldier, every peer's model converged (host `intent APPLIED op=fire`, both clients
        /// `structural destroy 'U#4' applied`) — and the acting client's edit screen kept painting the dead
        /// unit, then threw 220 times before the game was closed. A repaint alone cannot fix that, because
        /// the screen's binding is a FIELD, not a read: <c>UIStateEditSoldier._currentCharacter</c> is
        /// snapshotted from the actor cycle (:159/:336) and <c>UpdateState</c>:470 hands it to
        /// <c>UpdateSoldierEquipment</c> → <c>UpdatePreferredLoadout</c>:551 →
        /// <c>GeoPhoenixFaction</c>:1233 → <c>PostmissionReplenishManager</c>:131-133, which throws
        /// `… who is not listed!` for a character the (mirrored) preferred-loadout table no longer knows.
        /// The throw lands INSIDE <c>UpdateState</c>, so every later line — including the rebuild this
        /// screen repaints through — is unreachable FOREVER: the screen cannot repaint itself out of it.
        ///
        /// AND THE NATIVE TAIL CANNOT SAVE IT ON A CLIENT. Vanilla's own dismissal rebinds
        /// (<c>UIStateEditSoldier.OnDismissSoldierDialogCallback</c>:443-451) by rotating through
        /// <c>_characters</c> — the list captured in the constructor (:83). On a client the model kill is
        /// blocked (<c>PersonnelSync.DismissCapturePatch</c> returns false, the host owns the kill), so that
        /// list still holds the dismissed unit and every list built after it does too. Which is why this
        /// runs off the STRUCTURAL DESTROY — the one event that means "gone on every peer" — and not off
        /// the gesture: it is equally right for the peer that did not press anything.
        ///
        /// NATIVE DOORS ONLY, the same two the game's own dismissal uses:
        /// <c>GeoscapeView.ToEditUnitState(GeoCharacter, IEnumerable&lt;GeoCharacter&gt;, StateStackAction)</c>
        /// (GeoscapeView.cs:506; it routes vehicles/mutogs to their own screens by TemplateDef, so this stays
        /// generic over the three edit screens) and <c>GeoscapeView.ResetViewState()</c> (:413) when nothing
        /// survives. The surviving roster is passed EXPLICITLY rather than letting the overload default to
        /// <c>GetFactionCharacters(null)</c>: <c>DestroyTacUnit</c> only removes the unit from
        /// <c>_tacUnits</c> (GeoLevelController.cs:1560-1563), the faction's own character list is a
        /// separately-mirrored container, so the default would be free to list the corpse straight back.
        /// </summary>
        public static void ReleaseScreenBoundTo(GeoLevelController geo, GeoCharacter destroyed)
        {
            var view = geo?.View;
            var screen = view?.CurrentViewState;
            if (destroyed == null || screen == null) return;
            // One field name, three screens (UIStateEditSoldier:43, UIStateEditVehicle:38,
            // UIStateViewVehicle:31 — all `_currentCharacter` + `_characters`). A screen that keeps no
            // character binding simply resolves neither and is left alone.
            var boundField = AccessTools.Field(screen.GetType(), "_currentCharacter");
            var rosterField = AccessTools.Field(screen.GetType(), "_characters");
            if (boundField == null || rosterField == null) return;
            var roster = rosterField.GetValue(screen) as System.Collections.Generic.List<GeoCharacter>;
            GeoCharacter next;
            if (!ReleaseBinding(roster, destroyed, boundField.GetValue(screen) as GeoCharacter, out next))
                return;
            MpLog.Log("[MP][uirepaint] released " + screen.GetType().Name + " from destroyed U#" +
                      (int)destroyed.Id + " → " + (next == null ? "no unit left, back to the geoscape"
                                                                : "U#" + (int)next.Id));
            // law 8: a state transition fires native UI events an intent-capture seam listens to.
            using (SyncApplyScope.Enter())
            {
                if (next == null) view.ResetViewState();
                else view.ToEditUnitState(next, roster, StateStackAction.ReplaceTop);
            }
        }

        /// <summary>THE EDGE, kept pure so RailCheck L326 can execute it: prune <paramref name="destroyed"/>
        /// out of the screen's own roster and answer whether the screen itself has to be rebound, and to
        /// what. False = this screen was showing somebody else, so no transition — but the roster is pruned
        /// ANYWAY, which is what stops a unit already dead on every peer from being cycled back onto the
        /// screen later. True + null = nothing survived → the caller resets to the geoscape. The successor
        /// is the game's own choice: <c>IndexOf(current).IncRotate(Count)</c>
        /// (UIStateEditSoldier.cs:443-444) is exactly the element that slides into the removed slot, or the
        /// first one when the removed slot was last. Reference identity throughout (L113).</summary>
        internal static bool ReleaseBinding(System.Collections.Generic.IList<GeoCharacter> roster,
                                            GeoCharacter destroyed, GeoCharacter bound, out GeoCharacter next)
        {
            next = null;
            int at = -1;
            if (roster != null)
                for (int i = roster.Count - 1; i >= 0; i--)
                    if (ReferenceEquals(roster[i], destroyed)) { at = i; roster.RemoveAt(i); }
            if (!ReferenceEquals(bound, destroyed)) return false;
            if (roster == null || roster.Count == 0) return true; // next stays null → ResetViewState
            next = roster[at < 0 || at >= roster.Count ? 0 : at];
            return true;
        }

        /// <summary>Session teardown: drop the pending repaint so the NEXT session's first Tick does not
        /// inherit a dirty flag from the dead one, and re-arm the one-shot diagnostics.</summary>
        public static void Reset()
        {
            _dirty = false;
            _hudDirty = false;
            _deferredFrames = 0;
            _deferLogged = false;
            _marksSinceFlush = 0;
            _loggedFailures.Clear();
            _loggedFallback.Clear();
            _loggedSkips.Clear();
            // Next session's first refresh must not read a dead session's site refs as a fresh edge.
            _exploringSites.Clear();
        }

        /// <summary>
        /// Driven once per frame from SyncEngine.Tick, both sides.
        ///
        /// N2 — a repaint DEFERS to uncommitted local input. Exit+Enter destroys and rebuilds every
        /// widget on the screen, so running it while the user is mid-drag yanks the item out of their
        /// hand. The defer happens BEFORE <c>_dirty</c> is cleared, so a deferred repaint is retried on
        /// the next frame rather than dropped — which is the difference between "later" and "never", and
        /// the reason this is not an opt-out. No screen is ever named here: the question asked is about
        /// INPUT state, not about which screen is open.
        /// </summary>
        public static void FlushIfDirty()
        {
            // Task 12 terminal teardown is callback-free and retryable independently of its already-durable
            // tombstone. A transient carrier exception therefore heals on the next ordinary sync tick.
            DurableSourceRevalidationEngine.RetryTerminalTeardown(DurableInboxSession.ActiveStore);
            if (!_dirty)
            {
                // Every mark the open screen declined still owes the persistent HUD a refresh (L60,
                // see MarkHudDirty). Same once-per-frame coalescing as the screen repaint, and it does
                // not touch a single widget the drag/typing defer below exists to protect.
                if (_hudDirty) { _hudDirty = false; RefreshPersistentHud(); }
                return;
            }
            if (LocalInputInFlight())
            {
                // ponytail: bounded defer, ceiling = MaxDeferFrames. A leaked gesture flag or a wedged
                // drag can delay a repaint that long but can NEVER starve it forever — an unbounded
                // version of exactly this wedge (a4f3b2b's drag claim) cost a whole test cycle. Upgrade
                // path if this ever fires legitimately: make the stuck flag frame-scoped at its source,
                // do not raise the cap.
                if (++_deferredFrames < MaxDeferFrames) return; // _dirty stays set: retried next frame
                if (!_deferLogged)
                {
                    _deferLogged = true;
                    MpLog.LogWarning("[Multiplayer][rail] open-UI repaint forced after " + MaxDeferFrames +
                                     " deferred frames — a drag or gesture flag is stuck (please report)");
                }
            }
            _deferredFrames = 0;
            _dirty = false;
            _hudDirty = false; // RepaintOpenGeoscapeScreen opens with the HUD refresh itself
            RepaintOpenGeoscapeScreen();
        }

        /// <summary>The local user has UNCOMMITTED input in flight. Asked of input state, not of screens:
        /// a screen with no drag icon simply answers false, so this needs no per-screen table.
        /// Internal because DiffEngine.RunSlice asks the same question before spending the URGENT walk
        /// budget — same reason, same answer: work that costs frame time waits out a live gesture.</summary>
        internal static bool LocalInputInFlight()
        {
            // Typing guard: Exit+Enter rebuilds every widget, wiping an active text entry mid-word
            // (soldier rename = UnityEngine.UI.InputField, UIModuleActorCycle.SoldierNameEditField:71;
            // the game's UI ships NO TMP input fields — TMPro appears only in I2.Loc label targets).
            var selected = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
            if (selected != null)
            {
                var field = selected.GetComponent<UnityEngine.UI.InputField>();
                if (field != null && field.isFocused) return true;
            }
            var mods = GenericApplier.GeoLevel()?.View?.GeoscapeModules;
            if (mods == null) return false;
            // ponytail: EquipSync's gesture flag is deliberately NOT consulted here — it is consumed by the
            // very next SetItems flush (same frame on an open equip screen), so it can never be "in flight"
            // long enough for a repaint to interleave, and a leaked one would defer repaints for nothing.
            // The drag half below already covers the case that yanks a held item.
            var soldierDrag = mods.SoldierEquipModule == null ? null : mods.SoldierEquipModule.ItemDragIcon;
            if (soldierDrag != null && soldierDrag.IsBeingDragged()) return true;
            var vehicleDrag = mods.VehicleEquipModule == null ? null : mods.VehicleEquipModule.ItemDragIcon;
            return vehicleDrag != null && vehicleDrag.IsBeingDragged();
        }

        /// <summary>Repaint the open geoscape screen: native rebuild table first (the primitive),
        /// Exit+Enter re-enter only for screens the table doesn't know.
        /// Guarded per-state so an un-audited screen misbehaving can't crash the apply loop.
        /// Private: every caller goes through <see cref="MarkDirty"/> so no repaint can bypass the
        /// drag/typing defer + per-frame coalescing in <see cref="FlushIfDirty"/>.</summary>
        private static void RepaintOpenGeoscapeScreen()
        {
            int marks = _marksSinceFlush;
            _marksSinceFlush = 0;
            // FIRST, and outside the open-screen question entirely: the persistent HUD belongs to no view
            // state, so it must not be skipped by the `current == null` bail below.
            RefreshPersistentHud();
            // ALSO outside the open-screen question: a window that is QUEUED is not `current`, so nothing
            // below would ever look at it — and an unservable one served later opens EMPTY (four measured
            // cases, 2026-08-08/09). Dropped here, once per flush, through the game's own pending list.
            try { DeploymentWindowClose.DropUnservableQueued(); }
            catch (Exception ex) { MpLog.LogError("[MP][deploy] queued-window sweep threw: " + ex); }
            var view = GenericApplier.GeoLevel()?.View;
            var current = view?.CurrentViewState;
            if (current == null) return;
            // A reseed (table entry OR fallback re-enter, both below) recomputes the screen's own
            // UI-SESSION BASELINE from the model, which eats the local user's undo floor while they
            // are mid-edit — a delta arriving from ANY peer would otherwise cancel the local
            // "revert what I just staged" gesture. The set of baseline fields is DECLARED per screen
            // in UiNativeRepaint.StageBaselines; this checkpoint is the only place that saves and
            // restores them, and it names no screen.
            var baseline = UiNativeRepaint.StageSnapshot.Capture(current, view);
            try { Repaint(current, view, marks); }
            finally { baseline.Restore(); }
        }

        private static void Repaint(GeoscapeViewState current, GeoscapeView view, int marks)
        {
            try
            {
                // law 8: a native refresh can fire UI events an intent-capture seam listens to.
                using (SyncApplyScope.Enter())
                {
                    if (UiNativeRepaint.TryRepaint(current, view))
                    {
                        if (MpDiag.On && Time.realtimeSinceStartup >= _nextDiagAt)
                        {
                            _nextDiagAt = Time.realtimeSinceStartup + 1f;
                            MpLog.Log("[MP][uirepaint] native rebuild " + current.GetType().Name + " marks=" + marks);
                        }
                        return;
                    }
                }
            }
            catch (Exception rebuildEx)
            {
                // A throwing native rebuild = PARTIAL repaint on a mirrored model — keep the screen and
                // keep using the table on later batches (law 11 outranks log tidiness). Never demote to
                // Exit+Enter: that transition is exactly what the table exists to avoid.
                if (_loggedFailures.Add(current.GetType().Name + ":NativeRebuild"))
                    MpLog.LogWarning("[Multiplayer][rail] native rebuild for " + current.GetType().Name +
                                     " threw — screen kept (logged once per screen): " + rebuildEx);
                return;
            }
            // A QUEUED WINDOW IS NOT A SCREEN — it is never Exit+Enter'd. The fallback below is a lifecycle
            // TRANSITION, and everything the game's switch-query puts up is a ONE-SHOT PRESENTATION whose
            // EnterState re-fires the very thing it exists to show: UIStateGeoCutscene.ExitState:76 stops
            // the video and EnterState:59 Setup()s it again (→ VideoPlaybackClipFinishedLoading →
            // PlayCutsceneOnFinishedLoad:114 → Play()), so ONE cinematic restarted 7 times on both clients
            // during one rail-batch storm (live 2026-08-04, multiplayer-2.log 21:20:36-21:20:54); the same
            // line re-entered UIStateRosterDeployment mid-deployment. A window that CAN be repainted
            // declares a UiNativeRepaint.Table entry and returns above (UIStateGeoModal, UIStateGeoscapeEvent)
            // — reaching HERE while the queue is showing you is itself the proof that there is nothing to
            // repaint. The persistent HUD was already refreshed by the caller, so law 11 keeps its half.
            // Never silent: once per screen type, un-gated, like every other declined repaint.
            if (PauseHold.IsCurrentQueuedWindow(view, current))
            {
                if (_loggedSkips.Add("queued:" + current.GetType().Name))
                    MpLog.Log("[MP][uirepaint] SKIP re-enter of queued window " + current.GetType().Name +
                              " — a one-shot presentation has no repaint, and Exit+Enter would replay it " +
                              "(logged once per screen)");
                return;
            }
            // LAST RESORT: lifecycle re-enter for screens with no native-rebuild registration yet.
            // One-time inventory line per screen type = the to-do list for the next table entry.
            if (_loggedFallback.Add(current.GetType().Name))
                MpLog.Log("[MP][uirepaint] fallback re-enter: " + current.GetType().Name);
            if (!(StatesStackField?.GetValue(view) is StateStack<GeoscapeViewContext> stack)) return;
            try
            {
                // law 8: a re-enter that fires native events must not echo an intent back to the host.
                using (SyncApplyScope.Enter())
                {
                    try { current.Exit(stack); }
                    catch (Exception exitEx)
                    {
                        // GeoscapeViewState.Exit removes the input handler BEFORE ExitState (decompile
                        // GeoscapeViewState.cs:98) — bailing out after a thrown ExitState would leave the
                        // screen DEAF. Always fall through to Enter: AddUnique re-subscribes idempotently.
                        if (_loggedFailures.Add(current.GetType().Name + ":Exit"))
                            MpLog.LogWarning("[Multiplayer][rail] open-UI Exit for " + current.GetType().Name +
                                             " threw — attempting Enter anyway (logged once per screen): " + exitEx);
                    }
                    current.Enter(stack);
                }
                // The ONE place a repaint actually executes — throttled diag so a re-enter storm is
                // visible in the log without flooding it (family switch: MULTIPLAYER_DIAG, see MpDiag).
                if (MpDiag.On && Time.realtimeSinceStartup >= _nextDiagAt)
                {
                    _nextDiagAt = Time.realtimeSinceStartup + 1f;
                    MpLog.Log("[MP][uirepaint] re-entered " + current.GetType().Name + " marks=" + marks);
                }
            }
            catch (Exception ex)
            {
                // NON-DESTRUCTIVE: stay on the screen the user is looking at. A throw inside EnterState is a
                // PARTIAL repaint, not a lost screen — and a partial repaint beats ejecting the player.
                //
                // The previous behaviour rolled the stack forward to UIStateNothingSelected, which reads to
                // the user as "the game kicked me out of the roster", once per rail batch. Grounded reason it
                // is not needed: GeoscapeViewState.Enter (decompile GeoscapeViewState.cs:88-94) re-registers
                // the input handler at :91 BEFORE calling EnterState(), and a geoscape state sets its own
                // MainUILayer/input state in the opening statements of EnterState (UIStateEditSoldier.cs:99-100)
                // — so a throw further in leaves the screen live and closable. The observed failure throws at
                // the LAST statement of UIStateEditSoldier.EnterState (:177 SelectCharacterProgression), i.e.
                // everything except the progression panel had already been rebuilt.
                //
                // We keep repainting this screen on later batches ON PURPOSE: the repaint mostly works, and
                // law 11 (reactivity) outranks log tidiness. Once per state TYPE the full exception is logged,
                // then it goes quiet — a per-frame stack dump was its own kind of freeze.
                if (_loggedFailures.Add(current.GetType().Name))
                    MpLog.LogWarning("[Multiplayer][rail] open-UI re-enter for " + current.GetType().Name +
                                     " threw — screen kept, that panel may be partially painted until the " +
                                     "underlying subtree is complete (logged once per screen): " + ex);
            }
        }
    }
}
