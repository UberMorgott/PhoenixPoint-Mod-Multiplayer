using System;
using System.Reflection;
using Base.Core;
using Base.UI;
using HarmonyLib;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.Abilities;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.Levels.Factions;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewControllers.Roster;
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
        // §B.4: the global bool above STAYS and serves the ~63 kindless sites and all structural
        // create/destroy. The scoped pair is BESIDE it, never instead of it — a kindless mark repaints
        // everything. Nothing NARROWS on these yet; Task 4 is what reads the paths.
        private static bool _scopedDirty;
        private static readonly System.Collections.Generic.HashSet<string> _touchedPaths =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
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
        // The faction InitialSetup:144 reads — asked of the module rather than assumed to be ViewerFaction,
        // so the signature below is computed over exactly the rows the module would rebuild.
        private static readonly FieldInfo TrackerFaction =
            AccessTools.Field(typeof(UIModuleFactionAgendaTracker), "_faction");
        /// <summary>Last repaint key per strip — see <see cref="RepaintNeeded"/>. A dictionary rather than a
        /// field per strip so the SECOND caller costs a line, not a mechanism.</summary>
        private static readonly System.Collections.Generic.Dictionary<string, string> _repaintKeys =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);

        // UIModuleInfoBar: private no-arg UpdatePopulation():276 and the Init(context):144 field that
        // proves the module is live (it dereferences `_context.View` unguarded, so calling it before
        // Init would NRE). TFTV's TopInforBar:127 postfixes exactly this method and rewrites the
        // Anu/Nj/Syn reputation percentages from <Faction>.Diplomacy.GetDiplomacy(PhoenixFaction) —
        // a STORED field the rail writes directly, so no native event ever fires on a client.
        private static readonly MethodInfo InfoBarUpdatePopulation =
            AccessTools.Method(typeof(UIModuleInfoBar), "UpdatePopulation", Type.EmptyTypes);
        private static readonly FieldInfo InfoBarContext =
            AccessTools.Field(typeof(UIModuleInfoBar), "_context");

        // UIModuleVehicleSelection: private SetCrew(IEnumerable<GeoCharacter>, int):401, which forwards to
        // AircraftCrewController.SetCrew:56 — the ONE place that ever switches
        // UnitOnBoardElementController.LevelUpIndicator (:28) on, at AircraftCrewController.cs:127-139 off
        // LevelProgression.HasNewLevel. Bound BY SIGNATURE like the two strips above, so a native overload
        // change reports itself instead of silently resolving to something else.
        private static readonly MethodInfo VehicleSelectionSetCrew =
            AccessTools.Method(typeof(UIModuleVehicleSelection), "SetCrew",
                new[] { typeof(System.Collections.Generic.IEnumerable<GeoCharacter>), typeof(int) });
        // The vehicle the module is CURRENTLY drawing (SetVehicleInfo:294). Asked of the module rather than
        // derived from the view state: the strip is a module, it outlives the state that opened it, and
        // repainting anything other than what it is showing would be a guess.
        private static readonly FieldInfo VehicleSelectionCurrent =
            AccessTools.Field(typeof(UIModuleVehicleSelection), "_currentVehicle");

        // UIModuleGeoRoster: the SECOND surface that paints the same green cross. GeoRosterItem:345 switches
        // LevelUpNotification on off `Character.Progression.LevelProgression.HasNewLevel`, inside the slot's
        // private UpdateLocations():315 — reached from its own PUBLIC no-arg UpdateCharacterData():239, which
        // is the whole per-slot repaint (name, class icon, corruption fill, level text, bars, notifications)
        // and creates NO GameObject, so unlike SetCrew it cannot flicker. Bound BY SIGNATURE like the strip
        // above, so a renamed native member reports itself instead of repainting nothing.
        private static readonly MethodInfo RosterItemUpdate =
            AccessTools.Method(typeof(GeoRosterItem), "UpdateCharacterData", Type.EmptyTypes);

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
            RefreshInfoBar(geo, mods);
            RefreshSiteContextualMenu(geo, mods, view.CurrentViewState);
            RefreshVehicleCrew(mods);
            RefreshRosterSlots(mods);
        }

        /// <summary>
        /// THE ROSTER LIST — the SECOND place the green level-up cross is painted, and the fifth citizen of
        /// the same gap. <c>GeoRosterItem</c>:345 reads the very flag <see cref="RefreshVehicleCrew"/> chases
        /// (<c>Character.Progression.LevelProgression.HasNewLevel</c>) and it is a DIFFERENT surface with a
        /// different owner: <c>UIModuleGeoRoster</c>, held by <c>GeoscapeModulesData</c>:30, so
        /// <see cref="UiNativeRepaint.Table"/> has no key that reaches it either and it had ZERO repaint
        /// references in src/. The roster SCREEN's own re-enter re-Inits the slots — but only while that
        /// screen is the CURRENT state; behind a queued window, or while the module is up as part of another
        /// state, nothing re-ran them.
        ///
        /// NATIVE PAINT, PER SLOT: the game's own public <c>UpdateCharacterData()</c>, which is exactly what
        /// <c>GeoRosterItem.Init</c>:211 calls. It rewrites text and toggles notification GameObjects — it
        /// instantiates nothing and re-subscribes nothing — so it is far cheaper than <c>SetCrew</c>. The
        /// signature gate is kept anyway: same one bound as every other strip, and it keeps this off the
        /// ~10 Hz flush path entirely when nothing the list draws has moved.
        /// </summary>
        private static void RefreshRosterSlots(GeoscapeModulesData mods)
        {
            if (RosterItemUpdate == null) return;
            var roster = mods.GeoRosterModule;
            if (roster == null)
            {
                if (_loggedFailures.Add("GeoRosterMissing"))
                    MpLog.LogWarning("[Multiplayer][rail] GeoscapeModulesData.GeoRosterModule is null — the " +
                                     "roster list has no handle to repaint, so a level-up cross or a renamed " +
                                     "soldier will only appear on a screen re-enter (logged once)");
                return;
            }
            try
            {
                // A module whose state is not up is switched off by the game itself
                // (UIModuleBehavior.SetStateID:34) — nothing open, nothing to repaint.
                if (!roster.gameObject.activeInHierarchy) return;
                var slots = roster.Slots;
                if (slots == null || slots.Count == 0) return;
                if (!RepaintNeeded("roster", RosterSignature(slots))) return;
                // law 8: the native paint can fire UI events a capture seam hears.
                using (SyncApplyScope.Enter())
                    foreach (var slot in slots)
                        if (slot != null && slot.Character != null && slot.gameObject.activeInHierarchy)
                            RosterItemUpdate.Invoke(slot, null);
            }
            catch (Exception ex)
            {
                if (_loggedFailures.Add("RosterSlots"))
                    MpLog.LogWarning("[Multiplayer][rail] roster-list repaint threw — the level-up cross and " +
                                     "soldier data may stay stale until a screen re-enter (logged once): " + ex);
            }
        }

        /// <summary>EVERY ROSTER SLOT AS THE LIST DRAWS IT, through the same <see cref="CrewSlotKey"/> the crew
        /// strip uses — one key shape for both surfaces of the same flag. Null forces the repaint, so an
        /// unreadable model costs a redundant paint and never a stale cross.</summary>
        private static string RosterSignature(System.Collections.Generic.IList<GeoRosterItem> slots)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (var slot in slots)
                {
                    var c = slot == null ? null : slot.Character;
                    sb.Append(c == null ? "-" : CrewSlotKey(c.DisplayName, c.OccupingSpace, c.LevelProgression,
                        c.CharacterStats == null ? -1 : c.CharacterStats.Corruption.IntValue)).Append(',');
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                if (_loggedFailures.Add("RosterSignature"))
                    MpLog.LogWarning("[Multiplayer][rail] roster-list signature threw — the list falls back to " +
                                     "repainting on every flush (logged once): " + ex);
                return null;
            }
        }

        /// <summary>
        /// THE AIRCRAFT CREW STRIP — fourth citizen of the same gap, and the one the green level-up cross
        /// lives on.
        ///
        /// THE REPORT (owner, 2026-08-15): a soldier levels up on one peer and the green cross appears on
        /// that peer's crew strip alone; the ally sees it only after leaving the geoscape and coming back.
        ///
        /// IT WAS NEVER A REPLICATION DEFECT. <c>LevelProgression.HasNewLevel</c> is a plain stored public
        /// bool (LevelProgression.cs:20, written at :90), it is a covered rail leaf
        /// (docs/rail-baseline.txt: `LevelProgression [direct] covered=3/3`) and <c>LevelProgression</c> is
        /// an audited kind that reaches <see cref="UiEventMap.Fire"/> and marks dirty. The state LANDS on
        /// every peer. Nothing REPAINTS it: the indicator is painted in exactly one place
        /// (<c>AircraftCrewController.SetCrew</c>:127-139, off <c>UIModuleVehicleSelection.SetCrew</c>:401),
        /// the universal repaint re-enters the open VIEW STATE, and the crew strip is a MODULE — no
        /// <see cref="UiNativeRepaint.Table"/> row can reach it, and <c>UIModuleVehicleSelection</c> had
        /// ZERO references anywhere in src/. Will and Health only survived because they are
        /// <c>BaseStat</c>s: RailTypes.EchoStatChange re-raises <c>StatChangeEvent</c>, which reaches
        /// <c>RefreshCrewBars</c> — sliders only, never the indicator.
        ///
        /// SO THE MODULE'S OWN NATIVE PAINT IS RE-RUN, nothing is hand-drawn, and only when the strip is
        /// actually up and something it draws actually changed — <c>SetCrew</c> RECREATES its elements
        /// (UIUtil.EnsureActiveComponentsInContainer + a full re-subscribe), so running it on every rail
        /// flush would be the L492 flicker again at ~10 Hz. Two bounds, both cheap:
        ///   • the crew panel must be ACTIVE IN HIERARCHY — a module whose state is not up is switched off
        ///     by the game itself (UIModuleBehavior.SetStateID:34), and <c>SetCrew</c> would re-activate
        ///     the panel, so this also stops the repaint from revealing a strip the game hid;
        ///   • <see cref="CrewSignature"/> must have changed, through the same one gate the other strips
        ///     use (<see cref="RepaintNeeded"/>).
        /// </summary>
        private static void RefreshVehicleCrew(GeoscapeModulesData mods)
        {
            if (VehicleSelectionSetCrew == null || VehicleSelectionCurrent == null) return;
            var module = mods.VehicleSelectionModule;
            if (module == null)
            {
                if (_loggedFailures.Add("VehicleSelectionMissing"))
                    MpLog.LogWarning("[Multiplayer][rail] GeoscapeModulesData.VehicleSelectionModule is null — " +
                                     "the aircraft crew strip has no handle to repaint, so a level-up cross or a " +
                                     "crew change will only appear on a screen re-enter (logged once)");
                return;
            }
            try
            {
                var crew = module.CrewController;
                if (crew == null || !crew.gameObject.activeInHierarchy) return; // strip not up: nothing open to repaint
                var vehicle = VehicleSelectionCurrent.GetValue(module) as GeoVehicle;
                // Not ours = the module drew DisableCrew():407, and SetCrew would switch the panel back on.
                if (vehicle == null || !vehicle.IsOwnedByViewer) return;
                if (!RepaintNeeded("crew", CrewSignature(vehicle))) return;
                // law 8: the native paint re-subscribes stat events and can fire UI events a capture seam hears.
                using (SyncApplyScope.Enter())
                    VehicleSelectionSetCrew.Invoke(module, new object[] { vehicle.Units, vehicle.MaxCharacterSpace });
            }
            catch (Exception ex)
            {
                if (_loggedFailures.Add("VehicleCrew"))
                    MpLog.LogWarning("[Multiplayer][rail] aircraft crew-strip repaint threw — the level-up cross " +
                                     "and crew changes may stay stale until a screen re-enter (logged once): " + ex);
            }
        }

        /// <summary>EVERYTHING <c>AircraftCrewController.SetCrew</c> PAINTS AND THE STAT ECHO DOES NOT, AS ONE
        /// STRING — read off :100-152 slot by slot: the crew SET and its order, each character's display name,
        /// occupied space, level, corruption, and <c>LevelProgression.HasNewLevel</c> (the green cross itself),
        /// plus the vehicle's slot count. Health/stamina/corruption BARS are deliberately absent: they are
        /// <c>BaseStat</c>s and repaint natively through RailTypes.EchoStatChange → RefreshCrewBars, so
        /// including them would rebuild the whole strip on every point of damage. Returns null when the model
        /// cannot be read; null forces the repaint, so an unreadable model costs at worst a flicker and never
        /// a stale cross (REACTIVITY is a hard mandate).</summary>
        private static string CrewSignature(GeoVehicle vehicle)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(vehicle.MaxCharacterSpace).Append('|');
                foreach (var c in vehicle.Units)
                    sb.Append(c == null ? "-" : CrewSlotKey(c.DisplayName, c.OccupingSpace, c.LevelProgression,
                        c.CharacterStats == null ? -1 : c.CharacterStats.Corruption.IntValue)).Append(',');
                return sb.ToString();
            }
            catch (Exception ex)
            {
                if (_loggedFailures.Add("CrewSignature"))
                    MpLog.LogWarning("[Multiplayer][rail] crew-strip signature threw — the strip falls back to " +
                                     "repainting on every flush (logged once): " + ex);
                return null;
            }
        }

        /// <summary>ONE CREW SLOT AS THE STRIP DRAWS IT. Separated from <see cref="CrewSignature"/> so RailCheck
        /// L512 can execute the level-up half without a live <c>GeoVehicle</c> (a Unity object whose
        /// <c>MaxCharacterSpace</c> dereferences its def). <c>HasNewLevel</c> — the green cross — is read
        /// BEFORE <c>Level</c> and <c>Level</c> is asked only when the def is there, because the level is
        /// DERIVED (<c>Def.GetLevel(Experience)</c>) and an unresolvable def must not cost the cross its
        /// key.</summary>
        internal static string CrewSlotKey(string name, int space,
                                           PhoenixPoint.Common.Entities.Characters.LevelProgression level,
                                           int corruption) =>
            (name ?? "?") + "@" + space + "@" +
            (level == null ? "-" : (level.HasNewLevel ? "!" : "") + (level.Def == null ? "?" : level.Level.ToString())) +
            "@" + corruption;

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
                // THE STRIP MUST BE ON SCREEN BEFORE IT IS ASKED ANYTHING (report 2026-08-15: the top-right
                // label stopped following RESEARCH on clients). A module whose state is not up is switched
                // off by the game itself (UIModuleBehavior.SetStateID:34) — and unlike UIStateNothingSelected
                // this module SPANS states, so nothing re-Inits it when the state comes back; that is the
                // whole reason this method exists. RepaintNeeded REMEMBERS every key it is handed, so asking
                // an OFF strip made it vouch for a signature nobody ever drew: the research head changed
                // while the player was inside UIStateResearch or behind the research-complete modal, the key
                // was recorded, and the next flush — the first one with the strip back on screen — compared
                // EQUAL and skipped the rebuild. The row then sat stale until something ELSE in the signature
                // moved, which is exactly "manufacturing updates, research does not".
                // The two strips added the same day guard this way already (RefreshVehicleCrew:304,
                // RefreshRosterSlots:212); the agenda strip was the one that did not. The info bar is not in
                // this class of defect: InfoBarKey carries a one-second floor, so its key can never stay
                // equal for longer than the module's own native poll.
                // THE FLICKER (client, reported 2026-08-14). InitialSetup DESTROYS and re-creates every row,
                // and this ran on EVERY flush — measured at `marks=10`, i.e. ~10 rail batches a second, so the
                // strip visibly blinked. A rebuild is only owed when the SET OF ROWS changed; the per-element
                // time text is refreshed by the plain UpdateData below (and by the module's own 1 Hz poll),
                // which touches no GameObject lifecycle.
                bool onScreen = tracker.gameObject.activeInHierarchy;
                bool rebuild = AgendaNeedsRebuild(onScreen,
                    AgendaSignature(TrackerFaction?.GetValue(tracker) as GeoFaction));
                if (!onScreen) return; // switched off: nothing to paint, and nothing was remembered either
                // law 8: the rebuild re-reads the model and can fire native UI events a capture seam hears.
                using (SyncApplyScope.Enter())
                {
                    if (rebuild) TrackerNeedsRefresh.SetValue(tracker, true);
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

        /// <summary>Does the strip owe a full rebuild? True on the first call of a session, on any change to
        /// <see cref="AgendaSignature"/>, and whenever the signature could not be read (null = rebuild, the
        /// safe direction). Its own memory, so L492 can drive it without a live tracker.
        ///
        /// <paramref name="stripOnScreen"/> IS A GATE ON THE QUESTION, NOT ON THE ANSWER (L516, report
        /// 2026-08-15). <see cref="RepaintNeeded"/> REMEMBERS every key it is handed, so a strip that is
        /// switched off must not be asked at all — answering "no rebuild" for it would be harmless, but
        /// RECORDING its signature makes the off strip vouch for rows nobody drew, and the first flush after
        /// it comes back compares EQUAL and skips the rebuild it owes. Short-circuit, deliberately: the
        /// whole point is that <see cref="RepaintNeeded"/> is never reached.</summary>
        internal static bool AgendaNeedsRebuild(bool stripOnScreen, string signature) =>
            stripOnScreen && RepaintNeeded("agenda", signature);

        /// <summary>
        /// THE ONE REPAINT-IF-CHANGED GATE, for every persistent-HUD strip. A rail flush lands ~10 times a
        /// second on a live geoscape, so driving a module's repaint from every flush is 10 Hz of whatever
        /// that repaint costs — a full row teardown for the agenda strip (the 2026-08-14 flicker, L492), and
        /// every postfix any other mod has hung on it for the info bar (TFTV's TopInforBar:127 does string
        /// Transform.Find lookups, a LINQ walk of the alien bases and a sprite load).
        ///
        /// True on the first call for a strip, on any change of its key, and whenever the key could not be
        /// read (null = repaint, the safe direction — REACTIVITY is a hard mandate, so an unreadable model
        /// may cost a repaint and may never cost a stale strip). Its own memory, so a law can drive it with
        /// no live module.
        /// </summary>
        internal static bool RepaintNeeded(string strip, string key)
        {
            string previous;
            bool needed = key == null || !_repaintKeys.TryGetValue(strip, out previous) ||
                          !string.Equals(key, previous, StringComparison.Ordinal);
            _repaintKeys[strip] = key;
            return needed;
        }

        /// <summary>
        /// EVERYTHING THE STRIP DRAWS, AS ONE STRING — the rebuild key for <see cref="RefreshAgendaTracker"/>.
        /// Read straight off <c>InitialSetup</c>:144-174, row source by row source, so nothing displayed can
        /// change without changing this (REACTIVITY is a hard mandate here, not an optimisation):
        ///   • manufacture head — <c>Manufacture.Current.ManufacturableItem.RelatedItemDef</c> (the row label
        ///     is that def's display name);
        ///   • research head — <c>Research.Current.ResearchID</c>;
        ///   • every vehicle the strip lists — the row exists iff <c>GetCurrentActionTime &gt; 0</c>, which
        ///     VehicleActionsViewService:177-188 defines as exploring-or-travelling, and its label/icon are
        ///     the vehicle NAME plus those same two flags;
        ///   • every facility row — repairing, or building with a non-zero construction time, keyed by def
        ///     and state.
        /// NOT included, deliberately: the per-row TIME LEFT. It is the one thing a rebuild does NOT paint —
        /// <c>UpdateData(element)</c>:271 does, on every call, rebuild or not.
        /// Returns null when the model cannot be read; null forces the rebuild, so an unreadable model can
        /// only ever cost a flicker, never a stale strip.
        /// </summary>
        private static string AgendaSignature(GeoFaction faction)
        {
            if (faction == null) return null;
            try
            {
                var sb = new System.Text.StringBuilder();
                var head = faction.Manufacture?.Current?.ManufacturableItem?.RelatedItemDef;
                sb.Append(head == null ? "-" : head.name).Append('|')
                  .Append(faction.Research?.Current?.ResearchID ?? "-").Append('|');
                foreach (var v in faction.Vehicles)
                {
                    if (v == null || (!v.Travelling && !v.IsExploringSite)) continue;
                    sb.Append(v.Name).Append(v.Travelling ? "@t," : "@x,");
                }
                sb.Append('|');
                if (faction is GeoPhoenixFaction phoenix)
                    foreach (var b in phoenix.Bases)
                    {
                        if (b?.Layout == null) continue;
                        foreach (var f in b.Layout.Facilities)
                        {
                            if (f == null) continue;
                            if (f.IsRepairing) sb.Append(f.Def == null ? "?" : f.Def.name).Append("@r,");
                            else if (f.IsBuilding && f.ConstructionTime != TimeUnit.Zero)
                                sb.Append(f.Def == null ? "?" : f.Def.name).Append("@b,");
                        }
                    }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                if (_loggedFailures.Add("AgendaSignature"))
                    MpLog.LogWarning("[Multiplayer][rail] agenda-strip signature threw — the top-right strip " +
                                     "falls back to rebuilding on every flush (logged once): " + ex);
                return null;
            }
        }

        /// <summary>Top-right resource/population strip — third citizen of the same gap: it is a MODULE,
        /// not a GeoscapeViewState, so no <see cref="UiNativeRepaint.Table"/> row can reach it. Its
        /// percentages come from a stored field (PartyDiplomacy._relations → Relation._diplomacy), which
        /// the rail writes DIRECTLY, so none of the module's native subscriptions (Init:148-168) ever
        /// fire on a client and the strip freezes at its last Init until a screen re-enter.
        /// <c>UpdatePopulation</c>:276 is the module's own repaint that TFTV's TopInforBar:127 postfixes
        /// to write the Anu/Nj/Syn reputation numbers, so driving it repaints them — read-direction only,
        /// no view-state transition.</summary>
        private static void RefreshInfoBar(GeoLevelController geo, GeoscapeModulesData mods)
        {
            if (InfoBarUpdatePopulation == null || InfoBarContext == null) return;
            if (!RepaintNeeded("infobar", InfoBarKey(geo))) return;
            var bar = mods.ResourcesModule;
            if (bar == null)
            {
                if (_loggedFailures.Add("InfoBarMissing"))
                    MpLog.LogWarning("[Multiplayer][rail] GeoscapeModulesData.ResourcesModule is null — the " +
                                     "top-right resource/reputation strip has no handle to repaint and will only " +
                                     "update on a screen re-enter (logged once)");
                return;
            }
            if (InfoBarContext.GetValue(bar) == null) return; // not Init'd yet — UpdatePopulation would NRE
            try
            {
                // law 8: the repaint re-reads the model and can fire native UI events a capture seam hears.
                using (SyncApplyScope.Enter()) InfoBarUpdatePopulation.Invoke(bar, null);
            }
            catch (Exception ex)
            {
                if (_loggedFailures.Add("InfoBar"))
                    MpLog.LogWarning("[Multiplayer][rail] info-bar refresh threw — the top-right reputation " +
                                     "percentages may stay stale until the next screen change (logged once): " + ex);
            }
        }

        /// <summary>
        /// EVERYTHING THE INFO BAR DRAWS, AS ONE STRING — the repaint key for <see cref="RefreshInfoBar"/>,
        /// through the same <see cref="RepaintNeeded"/> gate the agenda strip uses.
        ///
        /// THE NATIVE HALF IS EXACT. <c>UIModuleInfoBar.UpdatePopulation</c>:276-288 reads three values and
        /// nothing else — <c>WorldPopulation</c>, <c>GameOverWorldPopulation</c>,
        /// <c>StartingWorldPopulation</c> off the view — so those three ARE the native draw.
        ///
        /// THE POSTFIX HALF IS COVERED BY MODEL, NOT BY MOD. Everything TFTV's <c>TopInforBar</c>:127 paints
        /// on top is read from first-party state, so the key reads that state generically and names no mod,
        /// no def and no type of theirs: the three diplomacy values (:176/:180/:183
        /// <c>&lt;Faction&gt;.Diplomacy.GetDiplomacy(PhoenixFaction)</c> — the STORED field the rail writes
        /// directly, which is why this repaint exists at all) and the alien base list by type def name
        /// (:146-161, the nest/lair/citadel counts behind the ODI meter).
        ///
        /// AND A ONE-SECOND FLOOR, WHICH IS THE HONEST PART. A key over a panel a THIRD-PARTY postfix draws
        /// can never be complete — TFTV also reads the three "Discovered" diplomacy GameTags (:200/:211/:219,
        /// and <c>GameTagsProviderList</c> exposes no count to key on), an event-system variable, a void-omen
        /// check and an event record's chosen answer, and chasing those would mean naming their defs here,
        /// i.e. exactly the bespoke per-panel patch this consolidation exists to stop. The floor bounds that
        /// gap absolutely
        /// instead of pretending it away: the strip repaints at least once a second no matter what, which is
        /// the module's own native poll rate, so nothing this key does not model can ever be staler than it
        /// is in vanilla — while the 10 Hz flush rate stops driving a sprite load and a Transform.Find walk.
        /// Returns null when the model cannot be read; null repaints.
        /// ponytail: a coarse clock term, not a modelled input. Drop it the day the strip's remaining
        /// inputs are all first-party and cheap to read.
        /// </summary>
        private static string InfoBarKey(GeoLevelController geo)
        {
            var view = geo == null ? null : geo.View;
            if (view == null) return null;
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(view.WorldPopulation).Append('/').Append(view.GameOverWorldPopulation)
                  .Append('/').Append(view.StartingWorldPopulation).Append('|');
                var phoenix = geo.PhoenixFaction;
                foreach (var f in geo.Factions)
                {
                    if (f == null || f.Diplomacy == null || ReferenceEquals(f, phoenix)) continue;
                    sb.Append(f.Diplomacy.GetDiplomacy(phoenix)).Append(',');
                }
                sb.Append('|');
                if (geo.AlienFaction != null)
                    foreach (var b in geo.AlienFaction.Bases)
                        sb.Append(b == null || b.AlienBaseTypeDef == null ? "?" : b.AlienBaseTypeDef.name).Append(',');
                return sb.Append('|').Append((int)Time.realtimeSinceStartup).ToString();
            }
            catch (Exception ex)
            {
                if (_loggedFailures.Add("InfoBarKey"))
                    MpLog.LogWarning("[Multiplayer][rail] info-bar repaint key threw — the strip falls back to " +
                                     "repainting on every flush (logged once): " + ex);
                return null;
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

        /// <summary>
        /// THE SAME MARK, CARRYING THE EXACT RAIL PATH THAT CHANGED. The rail has always known the leaf —
        /// <see cref="GenericApplier"/>'s ApplyEntry holds (kindId, path, fieldIdx, subKey, value) — and used
        /// to throw the path away one line before it was needed. This is where it stops being thrown away
        /// (§B.1).
        ///
        /// It accumulates into a SET OF TOUCHED PATHS BESIDE the global bool and NEVER instead of it. A null
        /// path (structural create/destroy, a preparation edit, a durable choice) falls straight through to
        /// the unconditional arm, which is exactly what the ~63 kindless call sites already do (§B.4).
        ///
        /// NOTHING IS NARROWED HERE. <see cref="FlushIfDirty"/> treats a scoped mark exactly like a global
        /// one — it repaints everything — so this task lands the CARRIER with the DECISION unchanged. Task 4
        /// is what makes a declared surface consult the paths.
        ///
        /// The declared-irrelevance gate is repeated rather than delegated: the two-argument overload owes
        /// L60 a DIRECT call to MarkHudDirty on its declined branch, and a shared helper would hide it from
        /// a callee walk.
        ///
        /// CONSERVATIVE BY CONSTRUCTION, in the same direction as the overload above: an unknown kind, a
        /// null path or no open screen at all marks.
        /// </summary>
        public static void MarkDirty(Type kind, GeoLevelController geo, string path)
        {
            if (path == null) { MarkDirty(kind, geo); return; }   // no path = today's behaviour, exactly
            var screen = geo?.View?.CurrentViewState;
            if (screen != null && kind != null &&
                UiNativeRepaint.IgnoredKinds.TryGetValue(screen.GetType(), out var ignored) &&
                ignored.Contains(kind))
            {
                if (_loggedSkips.Add(screen.GetType().Name + ":" + kind.Name))
                    MpLog.Log("[MP][uirepaint] SKIP " + kind.Name + " on " + screen.GetType().Name +
                              " — kind declared irrelevant to this screen (logged once per kind per screen)");
                MarkHudDirty();
                return;
            }
            _touchedPaths.Add(path);
            _scopedDirty = true;
            _marksSinceFlush++;
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
            _scopedDirty = false;
            _touchedPaths.Clear();
            _hudDirty = false;
            _deferredFrames = 0;
            _deferLogged = false;
            _marksSinceFlush = 0;
            _loggedFailures.Clear();
            _loggedFallback.Clear();
            _loggedSkips.Clear();
            // …and a dead session's strips must not vouch for the next one's: empty = repaint once each.
            _repaintKeys.Clear();
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
            // A SCOPED mark is a mark. Until Task 4 gives a surface a declaration to be measured
            // against, _scopedDirty is read here and nowhere else, and it means exactly what _dirty means.
            if (!_dirty && !_scopedDirty)
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
            _scopedDirty = false;
            _touchedPaths.Clear();
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
