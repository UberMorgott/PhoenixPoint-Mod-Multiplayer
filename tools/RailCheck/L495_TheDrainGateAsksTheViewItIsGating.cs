using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Base.UI;
using HarmonyLib;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L495 — THE OPEN-SCREEN HOLD FAILS CLOSED: IT ASKS THE VIEW IT IS GATING, NOT A GLOBAL LOOKUP.
    ///
    /// THE REPORT (owner, 2026-08-14): "while I am working inside a screen — research, roster — geoscape
    /// EVENT windows now pop up and interrupt me. This never used to happen."
    ///
    /// MEASURED, client multiplayer.log, one clock, no inference:
    ///   23:55:30.322  `[MP][input] sets=[SetGeoRosterControls]`   — the player enters the recruits screen
    ///   23:55:45.533  `[MP][input] sets=[SetGeoRosterControls]`   — last navigation of any kind
    ///   23:55:58.631  `[MP][events] raised 'RE21' seq=7 priority=0`
    ///   23:55:58.647  `[MP][windows] queue HELD while UIStateRosterRecruits is open` — the gate WORKS
    ///   23:56:02.207  `[MP][input] sets=[SetGeoscapeEventControls]` — and 3.56 s later it opened anyway
    /// No map control set is logged between the hold and the open, so the player never left the screen;
    /// exiting a window logs one (`SetVehicleSelectedControls` at 23:56:11.960, when he finally did).
    ///
    /// EVERY OTHER ESCAPE IS RULED OUT BY THE SAME LOG. The head was priority 0, i.e. inside the review
    /// band <see cref="WindowOrder.HoldsForOpenScreen"/> holds. No `order gate failed` line was written, so
    /// the catch-all drain did not fire. The hold itself had already engaged on that screen 3.5 s earlier,
    /// which proves <c>_currentStateSwitchRequest</c> was null (a screen opened straight onto
    /// <c>_statesStack</c>, GeoWindowCoverage:44, never sets it) and that the gate was being asked. The one
    /// remaining input is the READ: <c>GenericApplier.GeoLevel()?.View?.CurrentViewState</c> returned null
    /// for one frame, and L163's arm (b) — correctly — says a null state DRAINS. One frame is enough,
    /// because <c>StateStack.SwitchToState(..., PushOnTop)</c> is permanent.
    ///
    /// SO THE PREDICATE IS UNTOUCHED AND THE READ IS FIXED. <c>ProcessQueriedStateSwitch</c> is called from
    /// <c>GeoscapeView.Update</c>:1358, so the view owning the query is alive whenever the gate runs,
    /// whatever a global level lookup says while a level swap, a curtain or a save transfer is in flight.
    /// <see cref="WindowOrder.CurrentViewStateOf"/> reads <c>GeoscapeViewSwitchQuery._view</c> (:13, set in
    /// the ctor at :22) instead, so "no current state" means what L163 says it means — an empty state
    /// stack — and not "the mod could not find the geoscape this frame". Generic by construction: the hole
    /// was under EVERY window family at once, so it is closed once, at the one read.
    ///
    /// WHY L163/L175 COULD NOT SEE IT. Both execute the pure predicate with a state type handed to them.
    /// The defect was in the ARGUMENT the live gate computes, which no arm of either law ever ran — L163
    /// even asserts (correctly, and still) that a null state drains, which is the rule the bad read
    /// weaponised. This law executes the real read over a real query.
    ///
    /// THE ARMS (all EXECUTED over the real method and the real game types):
    ///   (a) <c>gate-looks-away</c> — a query whose own view has UIStateRosterRecruits on its stack reports
    ///       that screen. RailCheck has NO live geoscape, so <c>GenericApplier.GeoLevel()</c> is null here:
    ///       this arm reproduces the incident's exact frame, and the old read returns null in it.
    ///   (b) <c>screen-interrupted</c> — end to end: a real priority-0 event request against that same read
    ///       is HELD. Arm (a) alone would pass on a read that returns the right type but is never used.
    ///   (c) <c>empty-stack-held</c>, NON-VACUITY — a query whose view has an EMPTY stack reads null and the
    ///       same request is NOT held. A read that answered "some screen" everywhere would satisfy (a)+(b)
    ///       and hold every window forever, which is strictly worse than the bug (L163 arm (b)).
    ///   (d) <c>map-held</c>, NON-VACUITY — the same query with UIStateVehicleSelected on the stack drains.
    ///
    /// Falsify (verified RED, then restored):
    ///   • restore the body to <c>GenericApplier.GeoLevel()?.View?.CurrentViewState?.GetType()</c> → (a)+(b)
    ///   • return the queued state's type, or any constant screen type → (c)+(d)
    ///   • drop the <c>_view</c> bind (leave only the global fallback) → (a)+(b)
    /// </summary>
    internal static class L495_TheDrainGateAsksTheViewItIsGating
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var read = typeof(WindowOrder).GetMethod("CurrentViewStateOf", All);
            var holdsHead = typeof(WindowOrder).GetMethod("HoldsHead", All);
            var viewStack = AccessTools.Field(typeof(GeoscapeView), "_statesStack");
            var queryView = AccessTools.Field(typeof(GeoscapeViewSwitchQuery), "_view");
            if (read == null || holdsHead == null || viewStack == null || queryView == null)
            {
                yield return "L495 premise-changed: WindowOrder.CurrentViewStateOf / WindowOrder.HoldsHead / " +
                             "GeoscapeView._statesStack / GeoscapeViewSwitchQuery._view did not resolve. The " +
                             "drain gate's read of the player's screen has been renamed, removed or moved back " +
                             "onto a global lookup, and every arm below would pass vacuously — while the failure " +
                             "is the silent kind: one frame of a null read and an event window is sitting on top " +
                             "of the screen the player is working in, with no log line saying so.";
                yield break;
            }

            var onScreen = QueryOver(viewStack, queryView, typeof(UIStateRosterRecruits));
            var onMap = QueryOver(viewStack, queryView, typeof(UIStateVehicleSelected));
            var empty = QueryOver(viewStack, queryView, null);
            var request = Request(typeof(UIStateGeoscapeEvent), 0);
            if (onScreen == null || onMap == null || empty == null || request == null)
            {
                yield return "L495 premise-changed: this law could not build a real GeoscapeViewSwitchQuery " +
                             "around a real GeoscapeView state stack, so none of its arms ran. The game's view " +
                             "or state-stack shape changed; re-ground the harness rather than deleting the law.";
                yield break;
            }

            // ── (a) the read finds the player's screen with NO live geoscape to look up ────────────────
            var seen = read.Invoke(null, new object[] { onScreen }) as Type;
            if (seen != typeof(UIStateRosterRecruits))
                yield return "L495 gate-looks-away: the drain gate reads '" + (seen == null ? "<null>" : seen.Name) +
                             "' for a query whose OWN view has UIStateRosterRecruits on its stack. That is the " +
                             "2026-08-14 frame exactly — the view is alive, the global level lookup is not — and " +
                             "a null read there is not \"no screen\", it is \"the mod could not find the " +
                             "geoscape this frame\". L163 then drains, correctly, onto a screen the player " +
                             "opened, and the push is permanent.";

            // ── (b) and the hold it feeds actually engages ─────────────────────────────────────────────
            if (!Held(holdsHead, request, read, onScreen))
                yield return "L495 screen-interrupted: a real priority-0 UIStateGeoscapeEvent request is NOT " +
                             "held while the query's own view is on UIStateRosterRecruits. This is the owner's " +
                             "report verbatim — an event window taking a screen he opened — and it is asserted " +
                             "end to end because a read that returns the right type but is not the one the gate " +
                             "consults would leave arm (a) green and the defect live.";

            // ── (c) NON-VACUITY: an empty stack still drains (L163 arm (b) stays true) ─────────────────
            if (read.Invoke(null, new object[] { empty }) as Type != null ||
                Held(holdsHead, request, read, empty))
                yield return "L495 empty-stack-held: a query whose view has an EMPTY state stack reports a " +
                             "screen, or its window is held. There is no screen to protect and nothing to " +
                             "return to, so a hold there is a queue nothing ever releases — the failure this " +
                             "fix must not become, and the one L163 arm (b) already guards on the predicate " +
                             "side. Fixing the READ must not smuggle a hold-everywhere in behind it.";

            // ── (d) NON-VACUITY: the map drains ────────────────────────────────────────────────────────
            if (read.Invoke(null, new object[] { onMap }) as Type != typeof(UIStateVehicleSelected) ||
                Held(holdsHead, request, read, onMap))
                yield return "L495 map-held: a query whose view is on UIStateVehicleSelected — the geoscape " +
                             "with an aircraft selected — does not read as that state, or its window is held. " +
                             "That is where a peer sits between windows all game long and the one place the " +
                             "design says a notification IS reviewed.";
        }

        private static bool Held(MethodInfo holdsHead, object request, MethodInfo read, object query) =>
            (bool)holdsHead.Invoke(null, new[] { request, read.Invoke(null, new[] { query }) });

        /// <summary>A REAL <c>GeoscapeViewSwitchQuery</c> whose <c>_view</c> is a REAL
        /// <c>GeoscapeView</c> holding <paramref name="stateType"/> (null = empty stack) — both are Unity
        /// objects, so they are minted uninitialized (the L175 pattern) and only the fields under test are
        /// written. Nothing here touches a live geoscape, which is the whole point: this is the frame in
        /// which <c>GenericApplier.GeoLevel()</c> answers null while the view is alive.</summary>
        private static object QueryOver(FieldInfo viewStack, FieldInfo queryView, Type stateType)
        {
            try
            {
                var stack = new StateStack<GeoscapeViewContext>(null);
                if (stateType != null)
                {
                    var inner = AccessTools.Field(typeof(StateStack<GeoscapeViewContext>), "_statesStack")
                        ?.GetValue(stack) as Stack<IState<GeoscapeViewContext>>;
                    var state = FormatterServices.GetUninitializedObject(stateType) as IState<GeoscapeViewContext>;
                    if (inner == null || state == null) return null;
                    inner.Push(state);
                }
                var view = FormatterServices.GetUninitializedObject(typeof(GeoscapeView));
                viewStack.SetValue(view, stack);
                var query = FormatterServices.GetUninitializedObject(typeof(GeoscapeViewSwitchQuery));
                queryView.SetValue(query, view);
                return query;
            }
            catch (Exception) { return null; }
        }

        private static GeoscapeViewStateSwitchRequest Request(Type stateType, int priority)
        {
            try
            {
                var state = FormatterServices.GetUninitializedObject(stateType) as IState<GeoscapeViewContext>;
                return state == null ? null : new GeoscapeViewStateSwitchRequest(state, priority);
            }
            catch (Exception) { return null; }
        }
    }
}
