using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.View.ViewModules;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L413 — ONE PLAYER OPENING A TAB DOES NOT STOP THE CLOCK FOR ANYBODY ELSE.
    ///
    /// THE FAILURE (live 2026-08-11, host Player.log). Vanilla pauses geoscape time whenever the player
    /// leaves the map for a screen — right for one player, wrong for three. Every one of those pauses lands
    /// on <c>GeoscapeView.SetGamePauseState</c>, which this mod captures and relays UNCONDITIONALLY, so a
    /// peer clicking Manufacturing froze the aircraft another peer was steering: repeated
    /// <c>[MP][pause] peer=2 → paused=True</c> at nonces 5/8/11/14/53/56/59/62/65, each one a screen change
    /// and none of them a request to stop anyone else's game.
    ///
    /// THE RULE. A tab is a PRIVATE screen; it stops the shared clock only for the peer who DISPATCHED an
    /// aircraft that is still in the air. The game records no ordering peer on <c>GeoVehicle</c>, so the
    /// ledger is kept where the order is accepted (<c>VehicleSync.HandleTravelTo</c>) and read where the
    /// verdict is reached (<c>TimeSync.HonourTabPause</c>, host-side — the host is the only peer that knows
    /// who sent what). A blocking WINDOW is the other case and keeps pausing everyone: it is what every peer
    /// is looking at, not one peer's screen.
    ///
    /// NOT A QUORUM AND CANNOT BECOME ONE (P13). Nothing here waits on a human: this decides a pause BIT.
    /// A peer that goes AFK inside a tab does not freeze the campaign — that is the whole point — and any
    /// peer's resume still wins unconditionally (PauseHold).
    ///
    /// THREE THINGS HAVE TO STAY TRUE:
    ///   (a) the private-screen table names the tabs, does NOT name the window states (a window must go on
    ///       pausing everyone) and does NOT name the MAP states (2026-08-13 — naming them swallowed the
    ///       aircraft-arrival pause and the spacebar); the section bar's own pause-before-switch
    ///       (UIModuleGeoSectionBar.cs:112-196) is recognised by <c>TimeSync._sectionBarSwitch</c> instead;
    ///   (b) the host JUDGES a peer's tab pause instead of replaying it (<c>TimeSync.HandleIntentOp</c>
    ///       reaches <c>HonourTabPause</c>);
    ///   (c) the ledger is actually written when a client's travel order is accepted, or it is empty, every
    ///       tab pause is declined, and the dispatcher's own departure silently stops pausing.
    ///
    /// GUARDED TWICE: every member must resolve, the private-screen table must read back non-empty (an
    /// empty one passes every containment test by accident), and a POSITIVE CONTROL proves the IL walker
    /// can see a <c>HonourTabPause</c> edge that exists by construction before its silence is believed.
    ///
    /// Falsify: replay a tab pause unconditionally on the host → host-replays-a-tab-pause; drop
    /// <c>UIStateResearch</c> from the table → private-screen-table-misses-a-tab; add
    /// <c>UIStateGeoModal</c> to it → private-screen-table-swallows-a-window; delete the
    /// <c>AircraftDispatch.Note</c> call from <c>VehicleSync.HandleTravelTo</c> → dispatch-ledger-not-written.
    /// </summary>
    internal static class L413_ATabPauseFollowsTheDispatcherNotTheAudience
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        // The tabs the owner named. NOT the map states — see MustNotBeMapState.
        private static readonly Type[] MustBePrivate =
        {
            typeof(UIStateResearch), typeof(UIStateManufacturing), typeof(UIStateGeoRoster),
            typeof(UIStatePhoenixBaseLayout), typeof(UIStateDiplomacy), typeof(UIStateEditSoldier),
        };

        // A window is what every peer is looking at — it keeps stopping the world.
        private static readonly Type[] MustNotBePrivate =
        {
            typeof(UIStateGeoModal), typeof(UIStateGeoscapeEvent), typeof(UIStateGeoCutscene),
            typeof(UIStateRosterDeployment), typeof(UIStateAssetDeployment),
        };

        // ARM (a) INVERTED 2026-08-13. These MAP states were named private so the section bar's
        // pause-before-switch would be judged; naming them made every ENGINE pause raised on the map a "tab
        // pause" too, and the host declined them all. Reported live: an aircraft arriving at a site of
        // interest no longer stopped time, and the SPACEBAR stopped pausing on the host — on the map the
        // time module is bound with pauseTiming:false (UIStateNothingSelected.cs:98-99), so the key IS a
        // bare SetGamePauseState with nothing else to identify it by. The section-bar click is now scoped
        // instead (TimeSync._sectionBarSwitch), which is the thing the rule actually needed to know.
        private static readonly Type[] MustNotBeMapState =
        {
            typeof(UIStateNothingSelected), typeof(UIStateVehicleSelected), typeof(UIStateInitial),
        };

        // The six section-bar tab openers the scope patch must cover (UIModuleGeoSectionBar.cs:112-196):
        // each is SetGamePauseState(true) followed by To*State(), i.e. the one pause raised while the MAP
        // state is still current that IS a tab open.
        private static readonly string[] SectionBarOpeners =
        {
            "ActivatePhoenixBasesContent", "ActivateRosterContent", "ActivateVehicleRosterContent",
            "ActivateResearchContent", "ActivateManufacturingContent", "ActivateDiplomacyContent",
            "ActivatePhoenixpediaContent",
        };

        internal static IEnumerable<string> Check()
        {
            var mod = typeof(TimeSync).Assembly;
            var dispatch = mod.GetType("Multiplayer.Network.Sync.AircraftDispatch");
            var note = dispatch?.GetMethod("Note", All);
            var inFlight = dispatch?.GetMethod("HasAircraftInFlight", All);
            var honour = typeof(TimeSync).GetMethod("HonourTabPause", All);
            var handle = typeof(TimeSync).GetMethod("HandleIntentOp", All);
            var vehicle = mod.GetType("Multiplayer.Network.Sync.VehicleSync");
            var travel = vehicle?.GetMethod("HandleTravelTo", All);
            var table = typeof(TimeSync).GetField("PrivateScreens", All);

            if (dispatch == null || note == null || inFlight == null || honour == null || handle == null ||
                travel == null || table == null)
            {
                yield return "L413 premise-changed: one of AircraftDispatch.Note / HasAircraftInFlight / " +
                             "TimeSync.HonourTabPause / TimeSync.HandleIntentOp / TimeSync.PrivateScreens / " +
                             "VehicleSync.HandleTravelTo no longer " +
                             "resolves, so nothing checks that one player's tab leaves everybody else's " +
                             "clock alone. Re-point this law — do not delete it: what it guards is an " +
                             "aircraft freezing in mid-air because a different player opened Research.";
                yield break;
            }

            var screens = table.GetValue(null) as IEnumerable;
            var named = screens == null ? new List<Type>() : screens.Cast<Type>().ToList();
            if (named.Count == 0)
            {
                // ANTI-VACUITY: an empty table would pass every containment test below by accident.
                yield return "L413 premise-changed: TimeSync.PrivateScreens read back empty, so every " +
                             "containment verdict below would be vacuous. The table is a static readonly " +
                             "HashSet<Type> built from typeof() constants — an empty read means the field " +
                             "moved or its initializer did not run, not that the rule changed.";
                yield break;
            }

            foreach (var t in MustBePrivate)
                if (!named.Contains(t))
                    yield return "L413 private-screen-table-misses-a-tab: TimeSync.PrivateScreens does not " +
                                 "name " + t.Name + ", so a peer entering it relays a pause to every other " +
                                 "peer — the aircraft somebody else is steering stops because this player " +
                                 "opened a screen. Both shapes must be named: the section bar pauses from the " +
                                 "MAP state before it switches (UIModuleGeoSectionBar.cs:119-194), the screen " +
                                 "pauses again from its own EnterState.";

            foreach (var t in MustNotBePrivate)
                if (named.Contains(t))
                    yield return "L413 private-screen-table-swallows-a-window: TimeSync.PrivateScreens names " +
                                 t.Name + ", which is a WINDOW every peer is shown, not one peer's tab. " +
                                 "Treating it as private stops relaying its pause, and the aircraft flies on " +
                                 "while every player reads the popup — the 2026-08-04 bug, restored.";

            foreach (var t in MustNotBeMapState)
                if (named.Contains(t))
                    yield return "L413 private-screen-table-swallows-a-map-pause: TimeSync.PrivateScreens " +
                                 "names " + t.Name + ", a MAP state. Every pause raised while the player " +
                                 "stands on the map then goes through the dispatcher rule and is declined — " +
                                 "the vanilla aircraft-arrival pause (UIStateVehicleSelected:1179) and the " +
                                 "spacebar itself (bound pauseTiming:false, UIStateNothingSelected:98-99) " +
                                 "both stop working. The section-bar click has its own scope; use it.";

            // The scope that replaced the map states. Guarded on BOTH sides — the flag the rule reads and
            // the seam that sets it — because either one missing fails SILENTLY: without the flag a tab open
            // from the map relays a pause to every peer (the 2026-08-11 bug), and nobody reports a pause
            // that DID happen.
            var scopeFlag = typeof(TimeSync).GetField("_sectionBarSwitch", All);
            var scopePatch = typeof(TimeSync).GetNestedType("SectionBarSwitchScopePatch", All);
            if (scopeFlag == null || scopePatch == null ||
                scopePatch.GetMethod("Prefix", All) == null || scopePatch.GetMethod("Finalizer", All) == null)
                yield return "L413 section-bar-scope-gone: TimeSync._sectionBarSwitch / " +
                             "SectionBarSwitchScopePatch (Prefix+Finalizer) no longer resolves, so the ONE " +
                             "tab open that pauses while the map state is still current is no longer " +
                             "recognised — a peer clicking Research from the map freezes the aircraft another " +
                             "peer is steering.";
            foreach (var name in SectionBarOpeners)
                if (typeof(UIModuleGeoSectionBar).GetMethod(name, All, null, Type.EmptyTypes, null) == null)
                    yield return "L413 section-bar-opener-renamed: UIModuleGeoSectionBar." + name + "() is " +
                                 "gone, so the scope patch cannot cover it and that tab's pause reaches " +
                                 "every peer unjudged. Re-point SectionBarOpeners and the patch's " +
                                 "TargetMethods filter together.";

            // POSITIVE CONTROL: the walker must be able to see a HonourTabPause edge it is KNOWN to have —
            // the local half, TimeSync.PrivateScreenPause, calls it outright. Without this, "HandleIntentOp
            // does not reach HonourTabPause" could just be a reader that died on an opcode.
            var localHalf = typeof(TimeSync).GetMethod("PrivateScreenPause", All);
            if (localHalf == null ||
                !Program.Callees(localHalf, mod).Any(c => c.MetadataToken == honour.MetadataToken &&
                                                          c.Module == honour.Module))
            {
                yield return "L413 premise-changed: POSITIVE CONTROL failed — the IL walker cannot see the " +
                             "HonourTabPause call inside TimeSync.PrivateScreenPause, which is there by " +
                             "construction. Its silence about HandleIntentOp would therefore mean nothing. " +
                             "Fix the reader (or re-point this law) before trusting any verdict below.";
                yield break;
            }
            var handleCallees = Program.Callees(handle, mod).ToList();

            if (!handleCallees.Any(c => c.MetadataToken == honour.MetadataToken && c.Module == honour.Module))
                yield return "L413 host-replays-a-tab-pause: TimeSync.HandleIntentOp no longer routes a peer's " +
                             "tab pause through HonourTabPause, so the host stops the shared clock for " +
                             "everybody the moment any peer opens any screen. Time authority stays with the " +
                             "host precisely so the host can DECLINE this: only the peer whose own aircraft is " +
                             "still in the air is entitled to pause by walking away from the map.";

            if (!Program.Callees(travel, mod).Any(c => c.MetadataToken == note.MetadataToken &&
                                                       c.Module == note.Module))
                yield return "L413 dispatch-ledger-not-written: VehicleSync.HandleTravelTo no longer records " +
                             "the ordering peer via AircraftDispatch.Note. The ledger is the ONLY place the " +
                             "dispatcher is known — the game stores no ordering peer on GeoVehicle — so " +
                             "without this write it stays empty, HasAircraftInFlight answers false for " +
                             "everyone, and the dispatcher's own departure silently stops pausing the clock " +
                             "it should stop. Failing OPEN like that is invisible: nobody reports a pause " +
                             "that did not happen.";
        }
    }
}
