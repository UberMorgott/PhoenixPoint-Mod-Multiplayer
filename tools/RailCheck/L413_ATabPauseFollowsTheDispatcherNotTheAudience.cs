using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
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
    ///   (a) the private-screen table names the tabs AND the map states the section bar pauses from
    ///       (UIModuleGeoSectionBar.cs:119-194 pauses BEFORE it switches), and does NOT name the window
    ///       states — a window must go on pausing everyone;
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

        // The tabs the owner named, plus the map states the section bar pauses from before it switches.
        private static readonly Type[] MustBePrivate =
        {
            typeof(UIStateNothingSelected), typeof(UIStateVehicleSelected),
            typeof(UIStateResearch), typeof(UIStateManufacturing), typeof(UIStateGeoRoster),
            typeof(UIStatePhoenixBaseLayout), typeof(UIStateDiplomacy), typeof(UIStateEditSoldier),
        };

        // A window is what every peer is looking at — it keeps stopping the world.
        private static readonly Type[] MustNotBePrivate =
        {
            typeof(UIStateGeoModal), typeof(UIStateGeoscapeEvent), typeof(UIStateGeoCutscene),
            typeof(UIStateRosterDeployment), typeof(UIStateAssetDeployment),
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
