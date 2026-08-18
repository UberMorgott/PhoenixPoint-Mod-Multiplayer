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
        // everything. FlushIfDirty resolves the pair into _dirty exactly once, at batch end (§B.5).
        private static bool _scopedDirty;
        private static readonly System.Collections.Generic.HashSet<string> _touchedPaths =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        private static bool _hudDirty;
        /// <summary>This batch contained a mark with NO path, so no declared prefix can be matched against
        /// it and every HUD scope must advance. Consumed by <see cref="BumpScopeGenerations"/>.</summary>
        private static bool _bumpAllScopes;
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

        /// <summary>Floor between two native agenda rebuilds, seconds. The module's own
        /// <c>UpdateModuleDataCrt</c> poll refreshes existing rows once per SECOND, so four rebuilds per
        /// second is already below anything a player can see.</summary>
        private const float AgendaRebuildMinInterval = 0.25f;
        private static float _nextAgendaRebuildAt;

        private static readonly FieldInfo StatesStackField =
            AccessTools.Field(typeof(GeoscapeView), "_statesStack");

        // UIModuleFactionAgendaTracker: the module's own PUBLIC full rebuild, Init(GeoscapeViewContext):93 —
        // UpdateData() + InitialSetup():144. Bound by signature so a native change reports itself.
        private static readonly MethodInfo TrackerInit =
            AccessTools.Method(typeof(UIModuleFactionAgendaTracker), "Init",
                               new[] { typeof(GeoscapeViewContext) });
        // Init(context):93 is what fills it; before the first geoscape state has entered, InitialSetup:144
        // would NRE on `_faction.Manufacture`. Asked rather than caught: the module handle below is the
        // NATIVE one, so it resolves long before the module is live.
        private static readonly FieldInfo TrackerContext =
            AccessTools.Field(typeof(UIModuleFactionAgendaTracker), "_context");
        // The faction InitialSetup:144 reads — asked of the module rather than assumed to be ViewerFaction,
        // so the row identity below is computed over exactly the rows the module would rebuild.
        private static readonly FieldInfo TrackerFaction =
            AccessTools.Field(typeof(UIModuleFactionAgendaTracker), "_faction");
        // TEMPORARY (report 2026-08-15, host has no RESEARCH row / clients have no MANUFACTURE row).
        // The live row list, so the diag can tell "row never built" from "row built, then torn down":
        // UpdateData():199-203 DISPOSES any element whose UpdateData(element):270-307 returns true, i.e.
        // whose time-left is <= TimeUnit.Zero — and TimeUnit.Invalid IS TimeSpan.MinValue.
        private static readonly FieldInfo TrackerElements =
            AccessTools.Field(typeof(UIModuleFactionAgendaTracker), "_currentTrackedElements");
        /// <summary>Last repaint key per strip — see <see cref="RepaintNeeded"/>. A dictionary rather than a
        /// field per strip so the SECOND caller costs a line, not a mechanism.</summary>
        private static readonly System.Collections.Generic.Dictionary<string, string> _repaintKeys =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>Last agenda-strip diag line — TEMPORARY, see the MpDiag block in
        /// <see cref="RefreshAgendaTracker"/>. Diagnostic only: nothing reads it but the log.</summary>
        private static string _lastAgendaDiag;

        // UIModuleInfoBar: private no-arg UpdatePopulation():276 and the Init(context):144 field that
        // proves the module is live (it dereferences `_context.View` unguarded, so calling it before
        // Init would NRE). TFTV's TopInforBar:127 postfixes exactly this method and rewrites the
        // Anu/Nj/Syn reputation percentages from <Faction>.Diplomacy.GetDiplomacy(PhoenixFaction) —
        // a STORED field the rail writes directly, so no native event ever fires on a client.
        private static readonly MethodInfo InfoBarUpdatePopulation =
            AccessTools.Method(typeof(UIModuleInfoBar), "UpdatePopulation", Type.EmptyTypes);
        private static readonly FieldInfo InfoBarContext =
            AccessTools.Field(typeof(UIModuleInfoBar), "_context");
        // THE REST OF THE SAME BAR (L562). UpdatePopulation alone is the population meter and TFTV's
        // reputation postfix; every other number on the strip has its own paint method, each reached
        // NATIVELY only from an event the rail never fires on a mirroring peer (Init:150-160 subscribes
        // ScannerCapacityChanged/CharacterAdded/StorageChanged/ResourcesChanged/OnIncomeChanged, and
        // Update():216-247 only drains flags those handlers set). Signatures READ, not assumed:
        //   UpdateResourceInfo(GeoFaction, bool):388 — wallet + income for all seven resources
        //   SetScannersInfo():371            — scanner count/capacity
        //   UpdatePhoenixSpecificDataData():265 — soldiers, vehicles, storage, containment in one call
        // UpdateStorage():338 is a REAL member and is deliberately NOT listed: :271 already reaches it,
        // and a second invoke would paint the same label twice per batch.
        private static readonly MethodInfo InfoBarUpdateResourceInfo =
            AccessTools.Method(typeof(UIModuleInfoBar), "UpdateResourceInfo",
                               new[] { typeof(GeoFaction), typeof(bool) });
        private static readonly MethodInfo InfoBarSetScannersInfo =
            AccessTools.Method(typeof(UIModuleInfoBar), "SetScannersInfo", Type.EmptyTypes);
        private static readonly MethodInfo InfoBarPhoenixData =
            AccessTools.Method(typeof(UIModuleInfoBar), "UpdatePhoenixSpecificDataData", Type.EmptyTypes);

        /// <summary>
        /// THE ONE DELIBERATE MODULE-TIER EXCEPTION, and it is MECHANISM-FORCED — do not "clean this up"
        /// into something generic. There is no rebuild contract at the module tier to be generic OVER:
        /// <c>UIModuleBehavior</c> (Base.UI/UIModuleBehavior.cs:10-88) declares only SetStateID/OnShow/OnHide,
        /// exactly 1 of 73 modules overrides OnShow, <c>Init</c> exists on 26 of 73 with differing signatures
        /// and <c>Uninit</c> on 8 — so a reflection sweep over "the module's refresh method" would be a guess
        /// per module. This bar in particular belongs to NO view state (that is why
        /// <see cref="RefreshPersistentHud"/> exists at all) and its <c>Init</c>:142-175 performs 18
        /// unbalanced <c>+=</c> on long-lived objects with NO Uninit to match — so re-driving Init leaks one
        /// subscription set per rail batch. The read-direction paints below are the only honest repaint it
        /// has, and naming them is the price of that.
        ///
        /// Internal so L562 can execute the list rather than re-listing it: a native rename then turns the
        /// law RED instead of silently blanking half the strip.
        /// </summary>
        internal static readonly MethodInfo[] InfoBarPaints =
        {
            InfoBarUpdatePopulation, InfoBarUpdateResourceInfo, InfoBarSetScannersInfo, InfoBarPhoenixData,
        };

        /// <summary>Drive every bound paint on the ONE live bar. The argument shape is derived from the
        /// member itself (the only two-parameter one is UpdateResourceInfo(faction, useAnimation)) so the
        /// list above stays a list and never becomes a switch. <c>useAnimation: false</c> deliberately: the
        /// animated form is what the native wallet handler uses for a change THIS peer made, and a mirrored
        /// batch is not a spend the local player just performed.</summary>
        private static void PaintInfoBar(UIModuleInfoBar bar, GeoFaction faction)
        {
            foreach (var paint in InfoBarPaints)
            {
                if (paint == null) continue;
                var parameters = paint.GetParameters();
                if (parameters.Length == 0) paint.Invoke(bar, null);
                else if (faction != null) paint.Invoke(bar, new object[] { faction, false });
            }
        }

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
        /// Its native poll cannot be waited on: <c>Init</c>:100 starts <c>UpdateModuleDataCrt</c> on the GAME
        /// clock (<c>_context.Level.Timing.Start</c>), so it only repaints once the geoscape is UNPAUSED, and
        /// it refreshes EXISTING rows only. So the module's own public <c>Init(context)</c>:93 is re-driven
        /// here — the full native rebuild, one tick early.
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
            // FIRST, before any module is refreshed: advance every declared strip's generation ONCE for
            // this batch (§B.5 two-phase mark/evaluate), so the four strips below all see one consistent
            // world instead of a half-advanced one.
            BumpScopeGenerations(_touchedPaths);
            var geo = GenericApplier.GeoLevel();
            var view = geo?.View;
            var mods = view?.GeoscapeModules;
            if (mods == null) return; // no geoscape view at all (tactical / main menu) — nothing to repaint
            // ONE NAME PER STRIP (RailCost). "repaint" was a single step covering all five strips, the scroll
            // walk and the screen rebuild, so the 2026-08-18 client line `worst=repaint 35..43ms` named the
            // pass and not the work — and a strip whose rebuild costs a frame is exactly what makes a
            // travelling aircraft render in steps on a peer (its pose is closed-form per FRAME,
            // GeoNavComponent.cs:104-116, so a stalled frame is a missing frame of motion). Charging is two
            // Stopwatch reads per strip, the same price every other step here already pays.
            long t = RailCost.Now();
            RefreshAgendaTracker(mods);
            t = RailCost.Charge("repaint:agenda", t);
            RefreshInfoBar(mods);
            t = RailCost.Charge("repaint:infobar", t);
            RefreshSiteContextualMenu(geo, mods, view.CurrentViewState);
            t = RailCost.Charge("repaint:sitemenu", t);
            RefreshVehicleCrew(mods);
            t = RailCost.Charge("repaint:crew", t);
            RefreshRosterSlots(mods);
            RailCost.Charge("repaint:roster", t);
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
                if (!RepaintNeeded("roster", ScopeKey("UIModuleGeoRoster"))) return;
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
        ///   • the strip's DECLARED prefixes must have moved — <see cref="ScopeKey"/>, through the same one
        ///     gate the other strips use (<see cref="RepaintNeeded"/>).
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
                if (!RepaintNeeded("crew", ScopeKey("UIModuleVehicleSelection"))) return;
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

        /// <summary>Top-right activity strip — NATIVE FULL REBUILD, once per flushed batch it is on screen.
        /// <c>UIModuleFactionAgendaTracker.Init(context)</c>:93-104 is the module's own PUBLIC rebuild:
        /// <c>UpdateData()</c> then <c>InitialSetup()</c>:144, which is where every row is CREATED. The 1 s
        /// <c>UpdateModuleDataCrt</c> poll refreshes only rows that ALREADY exist, so a peer whose model
        /// gained a task has no row for it until something runs InitialSetup again — and nothing does: the
        /// module spans view states, so no state re-Init's it when the player comes back.
        ///
        /// INIT, NOT A HAND-PICKED REBUILD. <c>InitialSetup</c> is the seam other mods extend — TFTV appends
        /// its own agenda rows from a Postfix on it (refs/TFTV-src/TFTV/TFTVAAAgenda/AgendaPatches.cs:363-448)
        /// — so driving the native entry point picks up every row type this assembly has never heard of, with
        /// zero enumeration here. The four-source signature gate this replaces (AgendaSignature /
        /// AgendaNeedsRebuild, and L492/L516/L548 with them) is exactly what excluded them: it could only see
        /// vanilla's manufacture head, research head, vehicle actions and facility builds, so a change to any
        /// other row source compared EQUAL and the rebuild it owed was skipped for good.
        ///
        /// Re-driving Init is safe: :98 starts <c>UpdateModuleDataCrt</c> only when the old
        /// <c>_updateable</c> is null or stopped, and it is <c>Awake</c>:89-90 — not Init — that owns the
        /// button and hotkey subscriptions, so nothing double-subscribes and no coroutine accumulates.
        ///
        /// ONCE PER FLUSHED BATCH, never per delta: the only caller is <c>RefreshPersistentHud</c>, whose
        /// only callers are <c>FlushIfDirty</c> and <c>RepaintOpenGeoscapeScreen</c> (itself reached from
        /// FlushIfDirty). Every mark in between merely sets a flag — <c>MarkDirty</c>/<c>MarkHudDirty</c> —
        /// so N deltas in one batch still cost exactly ONE rebuild.</summary>
        /// <summary>THROTTLE WITH A TRAILING REBUILD. <c>InitialSetup</c>:148 EMPTIES the row container
        /// before re-creating every row, so one rebuild per flushed batch is a full teardown at rail rate —
        /// up to ~10/s on a busy client, which blinks. This is the floor: rebuild when
        /// <see cref="AgendaRebuildMinInterval"/> has elapsed, otherwise skip.
        ///
        /// NEVER LOSSY: a skip re-arms the HUD flag, so the LAST change of a burst is carried by the
        /// existing per-frame pass (<see cref="FlushIfDirty"/> calls <see cref="RefreshPersistentHud"/> for
        /// an owed HUD with nothing else dirty) the first frame past the floor. No coroutine, no Update
        /// hook, no timer object — the same <c>Time.realtimeSinceStartup</c> clock the diagnostics use, read
        /// by the caller so a law can drive this decision with no live module.</summary>
        internal static bool AgendaRebuildDue(float now)
        {
            if (now < _nextAgendaRebuildAt) { MarkHudDirty(); return false; }
            _nextAgendaRebuildAt = now + AgendaRebuildMinInterval;
            return true;
        }

        private static void RefreshAgendaTracker(GeoscapeModulesData mods)
        {
            if (TrackerInit == null || TrackerContext == null) return;
            var tracker = mods.FactionDataTracker;
            if (tracker == null)
            {
                if (_loggedFailures.Add("AgendaTrackerMissing"))
                    MpLog.LogWarning("[Multiplayer][rail] GeoscapeModulesData.FactionDataTracker is null — the " +
                                     "top-right activity strip has no handle to repaint and will only ever update " +
                                     "on its own game-clock poll (logged once)");
                return;
            }
            // Not Init'd yet: InitialSetup would NRE on _faction, and there is no context to hand back.
            var context = TrackerContext.GetValue(tracker) as GeoscapeViewContext;
            if (context == null) return;
            try
            {
                // A module whose state is not up is switched off by the game itself
                // (UIModuleBehavior.SetStateID:34) — nothing on screen, nothing to rebuild.
                bool onScreen = tracker.gameObject.activeInHierarchy;
                if (MpDiag.On)
                {
                    var trackedFaction = TrackerFaction?.GetValue(tracker) as GeoFaction;
                    string diag = "[MP][uirepaint] agenda " +
                        (NetworkEngine.Instance != null && NetworkEngine.Instance.IsHost ? "HOST" : "CLIENT") +
                        " onScreen=" + onScreen +
                        " faction=" + (trackedFaction == null ? "NULL-BINDING" : trackedFaction.GetType().Name) +
                        " research=" + (trackedFaction == null || trackedFaction.Research == null ||
                                        trackedFaction.Research.Current == null
                                            ? "-" : trackedFaction.Research.Current.ResearchID) +
                        " rows=" + AgendaRowCount(tracker) +
                        " rTL=" + AgendaResearchTimeLeft(trackedFaction) +
                        " mTL=" + AgendaManufactureTimeLeft(trackedFaction);
                    if (!string.Equals(diag, _lastAgendaDiag, StringComparison.Ordinal))
                    { _lastAgendaDiag = diag; MpLog.Log(diag); }
                }
                if (!onScreen) return;
                // Asked LAST, after every handle guard: a throttled skip owes a rebuild, and a strip that is
                // absent or off-screen owes nothing.
                if (!AgendaRebuildDue(Time.realtimeSinceStartup)) return;
                // law 8: the rebuild re-reads the model and can fire native UI events a capture seam hears.
                using (SyncApplyScope.Enter())
                    TrackerInit.Invoke(tracker, new object[] { context });
            }
            catch (Exception ex)
            {
                if (_loggedFailures.Add("PersistentHud"))
                    MpLog.LogWarning("[Multiplayer][rail] persistent-HUD refresh threw — the top-right tracker may " +
                                     "stay stale until the next screen change (logged once): " + ex);
            }
        }


        // TEMPORARY DIAGNOSTIC HELPERS (report 2026-08-15). The strip's ROWS do not live or die by
        // InitialSetup alone: UpdateData():199-203 disposes every element whose per-element
        // UpdateData(element):270-307 returns true, and that returns true exactly when the row's TIME LEFT
        // is <= TimeUnit.Zero (with no manufacture failure reason). TimeUnit.Invalid is TimeSpan.MinValue,
        // so an UNCOMPUTABLE time is <= Zero and tears the row down. Research time-left is Max when
        // production is 0 (Research.cs:694-703) but ZERO when Progress01 >= 1; manufacture time-left is
        // INVALID whenever the faction's Production income is <= 0 or the item cannot progress
        // (ItemManufacturing.cs:405-416). Bucketed, not numeric, so the change-gated line stays quiet
        // while a countdown ticks. No behaviour change: read-only, MpDiag-guarded at the call site.
        private static string TimeBucket(TimeUnit t) =>
            !t.IsValid ? "inv" : t == TimeUnit.Max ? "max" : t <= TimeUnit.Zero ? "ZERO" : "pos";

        private static string AgendaRowCount(UIModuleFactionAgendaTracker tracker)
        {
            var rows = TrackerElements?.GetValue(tracker) as System.Collections.ICollection;
            return rows == null ? "?" : rows.Count.ToString();
        }

        private static string AgendaResearchTimeLeft(GeoFaction faction)
        {
            try
            {
                var current = faction?.Research?.Current;
                return current == null ? "-" : TimeBucket(faction.Research.GetTotalTimeLeft(current));
            }
            catch (Exception) { return "throw"; }
        }

        private static string AgendaManufactureTimeLeft(GeoFaction faction)
        {
            try
            {
                var current = faction?.Manufacture?.Current;
                if (current == null) return "-";
                string bucket = TimeBucket(faction.Manufacture.GetTotalTimeLeft(current));
                // The second half of the dispose predicate: a row with a failure reason SURVIVES a
                // non-positive time and shows the reason instead, so the bucket alone does not decide.
                string reason = faction.Manufacture.CanFinishConstruction(current).ToString();
                return reason == "None" ? bucket : bucket + "/" + reason;
            }
            catch (Exception) { return "throw"; }
        }

        /// <summary>THE INFO BAR'S GATE, in L516's shape and for L516's reason: the module's LIVENESS is
        /// asked BEFORE <see cref="RepaintNeeded"/>'s memory is touched, never after. A key recorded against
        /// a bar that was not yet Init'd is a key BURNED — the first refresh after the bar comes up compares
        /// EQUAL and skips the repaint it owed. <c>&amp;&amp;</c> and not <c>&amp;</c>: short-circuit is what
        /// keeps <see cref="RepaintNeeded"/> from being evaluated at all for a dead bar.</summary>
        internal static bool InfoBarNeedsRefresh(bool barLive, string key) =>
            barLive && RepaintNeeded("infobar", key);

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
        /// THE REPLACEMENT FOR THE HAND-ROLLED SIGNATURES (§B.8). A persistent-HUD strip used to compute
        /// its own read-set as a string — AgendaSignature, InfoBarKey, CrewSignature, RosterSignature,
        /// CrewSlotKey — which is a read-set written BACKWARDS, i.e. the v1 per-widget sync this project
        /// abandoned, and which InfoBarKey itself admitted was incomplete by adding a 1-second
        /// Time.realtimeSinceStartup floor.
        ///
        /// The strip now asks the SAME question the screens ask: did anything I DECLARED change? Its key
        /// is a generation number that moves exactly when one of its declared prefixes was touched, so
        /// <see cref="RepaintNeeded"/>'s memory semantics — and therefore L492's flicker fix and L516's
        /// visibility-before-memory ordering — are preserved unchanged.
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<string, int> _scopeGeneration =
            new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>The strip's current key: stable while nothing it declared moved, different the moment
        /// something did. Pure with respect to its own memory — it reads, never writes.</summary>
        internal static string ScopeKey(string declaredName)
        {
            int generation;
            return declaredName + "#" +
                   (_scopeGeneration.TryGetValue(declaredName, out generation) ? generation : 0);
        }

        /// <summary>Advance the generation of every declared name whose prefixes this batch touched. Runs
        /// ONCE per flush, at batch end (§B.5), before any module is refreshed — two-phase mark/evaluate,
        /// so no strip sees a half-advanced world. Writes only <see cref="_scopeGeneration"/>, so
        /// enumerating the declaration table while doing it is safe.</summary>
        internal static void BumpScopeGenerations(
            System.Collections.Generic.ICollection<string> touchedPaths)
        {
            bool all = _bumpAllScopes;
            _bumpAllScopes = false;
            if (!all && (touchedPaths == null || touchedPaths.Count == 0)) return;
            foreach (var name in UiNativeRepaint.DeclaredPrefixes.Keys)
            {
                if (!all && !SurfaceRepaints(name, touchedPaths)) continue;
                int generation;
                _scopeGeneration[name] =
                    (_scopeGeneration.TryGetValue(name, out generation) ? generation : 0) + 1;
            }
        }

        /// <summary>
        /// DOES THIS SURFACE OWE A REPAINT FOR THESE TOUCHED PATHS? Pure, named and internal so RailCheck
        /// and RailSim execute the real decision with no live screen — the whole reason the property
        /// "a surface whose declared prefixes were untouched did NOT repaint" is testable at all.
        ///
        /// EVERY UNCERTAIN ANSWER IS "REPAINT": an unknown surface name, an undeclared surface (which is
        /// every surface this assembly has never heard of, INCLUDING another mod's own panels), a null path
        /// in the set, or an empty declaration all return true. Declaration is opt-in to SCOPE, never
        /// opt-in to reactivity — a forgotten surface degrades to today's behaviour, never to stale data.
        ///
        /// L541 executes all of that; the declarations themselves live in
        /// <see cref="UiNativeRepaint.DeclaredPrefixes"/>.
        /// </summary>
        internal static bool SurfaceRepaints(string surfaceName,
                                             System.Collections.Generic.ICollection<string> touchedPaths)
        {
            if (touchedPaths == null || touchedPaths.Count == 0) return false; // nothing changed at all
            if (surfaceName == null) return true;                              // no surface = repaint
            string[] prefixes;
            if (!UiNativeRepaint.DeclaredPrefixes.TryGetValue(surfaceName, out prefixes) ||
                prefixes == null || prefixes.Length == 0)
                return true;                                                   // UNDECLARED = repaint
            foreach (var path in touchedPaths)
            {
                if (path == null) return true;                                 // unknown path = repaint
                foreach (var prefix in prefixes)
                    if (prefix != null && path.StartsWith(prefix, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Top-right resource/population strip — third citizen of the same gap: it is a MODULE,
        /// not a GeoscapeViewState, so no <see cref="UiNativeRepaint.Table"/> row can reach it. Its
        /// percentages come from a stored field (PartyDiplomacy._relations → Relation._diplomacy), which
        /// the rail writes DIRECTLY, so none of the module's native subscriptions (Init:148-168) ever
        /// fire on a client and the strip freezes at its last Init until a screen re-enter.
        /// <c>UpdatePopulation</c>:276 is the module's own repaint that TFTV's TopInforBar:127 postfixes
        /// to write the Anu/Nj/Syn reputation numbers, so driving it repaints them — read-direction only,
        /// no view-state transition.</summary>
        private static void RefreshInfoBar(GeoscapeModulesData mods)
        {
            if (InfoBarUpdatePopulation == null || InfoBarContext == null) return;
            var bar = mods.ResourcesModule;
            if (bar == null)
            {
                if (_loggedFailures.Add("InfoBarMissing"))
                    MpLog.LogWarning("[Multiplayer][rail] GeoscapeModulesData.ResourcesModule is null — the " +
                                     "top-right resource/reputation strip has no handle to repaint and will only " +
                                     "update on a screen re-enter (logged once)");
                return;
            }
            // LIVENESS BEFORE MEMORY (L516's ordering, L543's arm c). `_context` is null until
            // UIModuleInfoBar.Init:144, and UpdatePopulation:276-288 dereferences `_context.View` unguarded
            // (decompiled/AssemblyCSharp/.../UIModuleInfoBar.cs:110 field, :144 assignment, :278 first read).
            var context = InfoBarContext.GetValue(bar) as GeoscapeViewContext;
            if (!InfoBarNeedsRefresh(context != null, ScopeKey("UIModuleInfoBar"))) return;
            try
            {
                // law 8: the repaint re-reads the model and can fire native UI events a capture seam hears.
                // The faction comes from the bar's OWN binding, never from the viewer: every paint below
                // dereferences `_context.ViewerFaction` itself, so asking anything else would repaint one
                // faction's numbers into another faction's strip.
                using (SyncApplyScope.Enter()) PaintInfoBar(bar, context.ViewerFaction);
            }
            catch (Exception ex)
            {
                if (_loggedFailures.Add("InfoBar"))
                    MpLog.LogWarning("[Multiplayer][rail] info-bar refresh threw — the top-right reputation " +
                                     "percentages may stay stale until the next screen change (logged once): " + ex);
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
        /// <remarks>A PATHLESS mark also bumps EVERY declared HUD scope (<see cref="_bumpAllScopes"/>).
        /// The persistent-HUD strips are gated on <see cref="ScopeKey"/> since the hand-rolled signatures
        /// died (§B.8), and a key that never moves is a strip that never repaints — so the ~63 kindless
        /// sites, which carry nothing to match a declared prefix against, must keep repainting everything
        /// here too, exactly as the old signatures did when the model under them changed.</remarks>
        public static void MarkDirty() { _dirty = true; _bumpAllScopes = true; _marksSinceFlush++; }

        /// <summary>
        /// THE RECOMPUTE'S OWN MARK — dirty, but NOT pathless (L563 arm (d)).
        ///
        /// <see cref="DerivedAggregateRefresh.ClientTick"/> used to end on the pathless
        /// <see cref="MarkDirty()"/> above, and the <c>_bumpAllScopes</c> half of it made
        /// <see cref="BumpScopeGenerations"/> advance EVERY declared scope unconditionally. Since a
        /// recompute row arms on "S#"/"F#"/"U#" — i.e. on essentially every rail batch a live geoscape
        /// produces — that turned every scope gate in the file OFF: the agenda strip's full row teardown,
        /// the info bar's TFTV postfix, the crew strip's <c>SetCrew</c> re-instantiation, the roster walk
        /// and the diplomacy pips all ran 3-4 times a second on every client, whatever had actually moved.
        /// Measured 2026-08-18 on a live client: <c>worst=repaint 35..43ms</c>, <c>frameMax=67..75ms</c> —
        /// and a stalled frame is a missing segment of aircraft flight, because the pose is closed-form per
        /// FRAME (GeoNavComponent.cs:104-116).
        ///
        /// THE MARK IS STILL UNCONDITIONAL, and only the SCOPE half is dropped. That is not a rounding of
        /// the reactivity mandate, it is where the mandate actually bites: the batch that armed the rows
        /// recorded its OWN touched paths a moment earlier, and every strip that prints a rebuilt rollup
        /// declares a prefix set that COVERS that row's inputs — asserted row by row and strip by strip in
        /// RailCheck L563 arm (b), so the two halves cannot drift apart silently. <c>_dirty</c> is kept
        /// because the open SCREEN's declaration is NOT covering in the same way: UIStateEditSoldier prints
        /// <c>_faction.GetTotalAvailableStorage()</c> (EnterState:596), a value the GeoPhoenixBase/UpdateStats
        /// row rebuilds from "S#", while the screen declares only { "U#", "V#", "F#" }.
        /// </summary>
        internal static void MarkRecomputeDirty() { _dirty = true; _marksSinceFlush++; }

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
                // No path to match a declared prefix against, so the HUD strips fall back to repainting.
                _bumpAllScopes = true;
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
        /// NOTHING IS DECIDED HERE. <see cref="FlushIfDirty"/> asks
        /// <see cref="SurfaceRepaints"/> ONCE at batch end whether the open surface's declared prefixes were
        /// touched; a surface with no declaration — every surface but the declared few, including another
        /// mod's panels — repaints on everything, exactly as before (§B.3, §B.7).
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
                // The SCREEN declined the kind; the HUD strips did not, and they have their own
                // declarations — so the path is still recorded for them. It cannot re-dirty the screen:
                // _scopedDirty stays clear here, and the screen's own SurfaceRepaints question filters
                // this path by the screen's own declared prefixes.
                _touchedPaths.Add(path);
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
        /// THE ACTING PEER'S OWN GESTURE (L549). Every other mark site in this file's caller set is an
        /// APPLY path — <see cref="GenericApplier"/>, <c>IntentRail.HandleInbound</c> — so a peer repainted
        /// its persistent HUD for everybody's change except the one it made itself. Reported on the host
        /// 2026-08-15: it started a research and the top-right agenda strip grew no research row until
        /// something re-Init'd the module, because <c>UIModuleFactionAgendaTracker</c> subscribes to no
        /// research event of its own (Awake:82-91) and its rows are built in <c>InitialSetup</c> alone.
        ///
        /// Raised from the ONE host-local gesture seam (<c>DiffEngine.FlushOnHostGesture</c>), so research,
        /// manufacture, facilities and vehicles are all covered by the same line — the client half needs
        /// nothing, because a client's own gesture is blocked and lands as the host's echo, which marks.
        ///
        /// THE HUD HALF ONLY, DELIBERATELY. That seam is entered by every capture family on every
        /// invocation — <c>VehicleSync.CaptureTravel</c> sees EVERY faction's departure on the planet
        /// before any owner test — so a pathless <see cref="MarkDirty()"/> here would bump every declared
        /// scope and re-enter the open view state at NPC-traffic rate, which is the L492/L548 teardown
        /// bought back. The strips gate themselves: the agenda strip on its ROW IDENTITY, the others on
        /// their declared scope, so an unmoved model costs one signature build.
        /// ponytail: no path is carried, so the SCOPE-gated strips (info bar, crew, roster) still wait for
        /// a rail path; upgrade path is to pass the gesture's own path once a seam knows it.
        /// </summary>
        public static void MarkLocalGesture() => MarkHudDirty();

        /// <summary>Test seam for L549: does the persistent HUD owe a refresh?</summary>
        internal static bool HudRepaintOwed => _hudDirty;

        /// <summary>THIS BATCH'S TOUCHED PATHS, read-only, for the one consumer that has to run BEFORE the
        /// flush rather than inside it: <see cref="DerivedAggregateRefresh"/> (L557). A rollup the rail
        /// staled has to be rebuilt while the batch's paths are still known — the repaint that follows one
        /// line later then paints the CORRECTED value instead of the stale one, in the same frame.
        /// Exposed as a peek and never as a handle to clear: <see cref="FlushIfDirty"/> owns the lifetime
        /// of this set, and a second owner is how a batch gets consumed twice.</summary>
        internal static System.Collections.Generic.ICollection<string> TouchedPaths => _touchedPaths;

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
            _bumpAllScopes = false;
            _deferredFrames = 0;
            _deferLogged = false;
            _marksSinceFlush = 0;
            _loggedFailures.Clear();
            _loggedFallback.Clear();
            _loggedSkips.Clear();
            // …and a dead session's strips must not vouch for the next one's: empty = repaint once each.
            _repaintKeys.Clear();
            _lastAgendaDiag = null;
            // A dead session's rebuild floor must not delay the next session's first strip paint.
            _nextAgendaRebuildAt = 0f;
            _scopeGeneration.Clear();
            // Next session's first refresh must not read a dead session's site refs as a fresh edge.
            _exploringSites.Clear();
            // …and never hold a dead scene's ScrollRects alive across a session boundary.
            _scrolls.Clear();
            _scrollAt.Clear();
            _scrollsForState = null;
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
            // §B.5 — MARK DURING THE BATCH, EVALUATE ONCE AT BATCH END. Two-phase mark/evaluate is what
            // avoids a glitch mid-batch; this is the evaluate, and it runs at exactly the point the global
            // bool is already evaluated. It resolves the scoped pair INTO _dirty, so the early-out below
            // stays the single gate it has always been.
            if (!_dirty && _scopedDirty)
            {
                _scopedDirty = false;
                var surface = GenericApplier.GeoLevel()?.View?.CurrentViewState?.GetType().Name;
                bool owes = SurfaceRepaints(surface, _touchedPaths);
                // NOT cleared here: the HUD strips read the set through BumpScopeGenerations, which runs
                // inside RefreshPersistentHud below. Clearing it before they run is what would burn the
                // batch for them — the set is dropped only after the repaint that consumed it.
                // The persistent HUD spans view states and is in no declaration, so it always hears about a
                // change — the same split MarkHudDirty already makes for a declined KIND (L60).
                if (owes) _dirty = true;
                else _hudDirty = true;
            }
            if (!_dirty)
            {
                // Every mark the open screen declined still owes the persistent HUD a refresh (L60,
                // see MarkHudDirty). Same once-per-frame coalescing as the screen repaint, and it does
                // not touch a single widget the drag/typing defer below exists to protect.
                if (_hudDirty) { _hudDirty = false; RefreshPersistentHud(); }
                _touchedPaths.Clear();
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
            _hudDirty = false; // RepaintOpenGeoscapeScreen opens with the HUD refresh itself
            RepaintOpenGeoscapeScreen();
            // AFTER the repaint, never before: RefreshPersistentHud (the first thing
            // RepaintOpenGeoscapeScreen does) reads the set to advance the strips' generations.
            _touchedPaths.Clear();
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
            long t = RailCost.Now();
            var baseline = UiNativeRepaint.StageSnapshot.Capture(current, view);
            CaptureScrolls(view, current);
            t = RailCost.Charge("repaint:scrolls", t);   // cached ScrollRect snapshot (walk only on state change)
            try { Repaint(current, view, marks); }
            finally { baseline.Restore(); RestoreScrolls(); RailCost.Charge("repaint:screen", t); }
        }

        /// <summary>WHERE THE PLAYER HAD SCROLLED TO, ACROSS THE WHOLE REPAINT. Both halves of
        /// <see cref="Repaint"/> can move it: a native rebuild re-fills a list, and the Exit+Enter fallback
        /// destroys every widget on the screen. Nothing is NAMED here — no screen, no module, no list — which
        /// is the entire point: the alternative is one "it lost my scroll" patch per panel, forever, and the
        /// next panel is always the one nobody wrote a patch for. The whole-canvas walk was measured at
        /// 29-35ms per flushed batch (worst charged step on every peer, 2026-08-17 logs), so it is CACHED:
        /// the walk runs only when the open view state changes (or after the widget-destroying Exit+Enter
        /// fallback invalidates it); every other batch just reads normalizedPosition off the cached list.
        /// A destroyed rect compares null and is skipped on both ends, so a stale entry is inert.</summary>
        private static readonly System.Collections.Generic.List<UnityEngine.UI.ScrollRect> _scrolls =
            new System.Collections.Generic.List<UnityEngine.UI.ScrollRect>();
        private static readonly System.Collections.Generic.List<Vector2> _scrollAt =
            new System.Collections.Generic.List<Vector2>();
        /// <summary>View-state TYPE NAME the cached <see cref="_scrolls"/> walk was taken under; null =
        /// re-walk. A string, not a Type: this is a cache key, not a reflection handle, and a Type field
        /// would (rightly) trip L23's null-handle sweep.</summary>
        private static string _scrollsForState;

        private static void CaptureScrolls(GeoscapeView view, GeoscapeViewState current)
        {
            _scrollAt.Clear();
            var mods = view == null ? null : view.GeoscapeModules;
            if (mods == null) { _scrolls.Clear(); _scrollsForState = null; return; }
            try
            {
                // NON-ALLOCATING OVERLOAD, and the list is reused across flushes: the array-returning form
                // allocates one array per walk over the WHOLE canvas. Same traversal, same
                // includeInactive:true, no new API — GetComponentsInChildren<T>(bool, List<T>) is Unity's
                // own overload of the same call. Every hit is recorded, dead ones included, so the two
                // lists stay INDEX-ALIGNED; RestoreScrolls already skips a null or content-less entry.
                // ponytail: rects INSTANTIATED mid-state (not merely toggled active) are invisible to the
                // cache until the next state change — their scroll may jump on a repaint. Upgrade path if
                // a real screen hits it: invalidate from that screen's rebuild seam, not per-batch walks.
                if (_scrollsForState != current.GetType().FullName)
                {
                    _scrolls.Clear();
                    mods.transform.root.GetComponentsInChildren(true, _scrolls);
                    _scrollsForState = current.GetType().FullName;
                }
                foreach (var scroll in _scrolls)
                    _scrollAt.Add(scroll == null || scroll.content == null ? Vector2.zero : scroll.normalizedPosition);
            }
            catch (Exception ex)
            {
                _scrolls.Clear();
                _scrollAt.Clear();
                _scrollsForState = null;
                if (_loggedFailures.Add("ScrollCapture"))
                    MpLog.LogWarning("[Multiplayer][rail] scroll capture threw — a repaint may jump the " +
                                     "player's list back to the top (logged once): " + ex);
            }
        }

        /// <summary>BEST EFFORT, NEVER A THROW: a repaint that already happened must not be undone by a
        /// scroll rect that did not survive it. A destroyed rect compares null (Unity's overloaded ==) and is
        /// skipped; a rebuilt one may also have a DIFFERENT content height, and a virtualised list
        /// (<c>VirtualScrollRect</c>) repools its content entirely, so the restored normalized position is an
        /// approximation there rather than a guarantee — approximately where the player was still beats the
        /// top of the list.</summary>
        private static void RestoreScrolls()
        {
            // _scrolls is the CACHE and survives the flush; only the per-flush positions are dropped.
            for (int i = 0; i < _scrolls.Count && i < _scrollAt.Count; i++)
            {
                var scroll = _scrolls[i];
                try
                {
                    if (scroll == null || scroll.content == null) continue;
                    scroll.normalizedPosition = _scrollAt[i];
                }
                catch (Exception ex)
                {
                    if (_loggedFailures.Add("ScrollRestore"))
                        MpLog.LogWarning("[Multiplayer][rail] scroll restore threw — the list under the " +
                                         "player may have jumped to the top (logged once): " + ex);
                }
            }
            _scrollAt.Clear();
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
            // §B.9: the Exit+Enter fallback destroys and rebuilds every widget on the screen — documented
            // right above to have restarted a cutscene 7 times — and for a screen carrying a live character
            // doll it IS the animation reset: EnterState re-runs the state's own DisplaySoldier, which is
            // the resetAnimation:true call this task removed from the repaint path. It stays the last resort
            // for UNDECLARED surfaces only. Reaching here for a declared one means its UiNativeRepaint.Table
            // entry is missing or declined, so say so loudly instead of silently rebuilding the doll.
            // The persistent HUD was already refreshed by the caller (same as the queued-window arm above).
            if (UiNativeRepaint.ModelAnimationSurfaces.Contains(current.GetType().Name))
            {
                if (_loggedFallback.Add(current.GetType().Name))
                    MpLog.LogWarning("[Multiplayer][uirepaint] refused the Exit+Enter fallback on " +
                                     current.GetType().Name + " — it carries model/animation state and " +
                                     "must be patched, not rebuilt. Its entry in UiNativeRepaint.Table is " +
                                     "missing or declined (logged once per screen)");
                return;
            }
            // LAST RESORT: lifecycle re-enter for screens with no native-rebuild registration yet.
            // One-time inventory line per screen type = the to-do list for the next table entry.
            if (_loggedFallback.Add(current.GetType().Name))
                MpLog.Log("[MP][uirepaint] fallback re-enter: " + current.GetType().Name);
            if (!(StatesStackField?.GetValue(view) is StateStack<GeoscapeViewContext> stack)) return;
            // Exit+Enter destroys and rebuilds every widget on the screen — the cached ScrollRect list is
            // stale after it (destroyed entries are inert nulls, rebuilt ones absent), so re-walk next batch.
            _scrollsForState = null;
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
