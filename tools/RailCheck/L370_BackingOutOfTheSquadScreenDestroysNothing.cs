using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L370 — BACKING OUT OF THE SQUAD SCREEN DESTROYS NOTHING, ON EITHER ROLE.
    ///
    /// THE REPORT (2026-08-09, 3-peer soak, one clock). The host opened the deployment screen for S#42 —
    /// <c>[MP][deploy] squad screen opening for S#42</c>, multiplayer.log 02:42:13.537 — and backed out of it.
    /// Both clients then logged <c>[MP][site] repaint S#42 … activeMission=none</c> at 02:42:17.501 and
    /// .502, and when a client pressed START MISSION the host refused its own launch intent at 02:44:02.782:
    /// <c>the host's site has no ActiveMission AT ALL</c>. One player leaving a screen deleted the mission
    /// for the whole team.
    ///
    /// IT IS L351'S DELETION THROUGH THE SIBLING DOOR, which is the whole reason this law is separate.
    /// `0b1549c` made the mission BRIEF per-peer at <c>GeoscapeView.ModalResultCallback</c> and explicitly
    /// left "the deployment screen's own Back button" untouched, while
    /// <see cref="MissionCancelGate"/> waved the HOST through wholesale. So the identical call —
    /// <c>UIStateRosterDeployment.ToPreviousScreen</c>:256 → <c>_mission.Cancel()</c>:258 →
    /// <c>GeoMission.Cancel</c>:253-265 (<c>Site.ActiveMission = null</c>, <c>DestroySite()</c>,
    /// <c>Reward</c> wiped) — arrived one screen later with L351 still green. A law per FUNNEL, because
    /// green on one proves nothing about the other.
    ///
    /// THE OUTCOME, NOT THE CALL: the arms execute <see cref="MissionCancelGate.Runs"/> over its truth
    /// table rather than checking that a patch attribute exists.
    ///
    ///   (a) <c>host-back-deletes-the-mission</c> — a HOST backing out of the squad screen must NOT run
    ///       Cancel. This is the report.
    ///   (b) <c>client-back-deletes-the-mission</c> — the pre-existing half: a CLIENT's Cancel is refused
    ///       whatever screen it came from (a projector may not write shared structure at all, law 3).
    ///   (c) <c>host-can-never-retire-a-mission</c> — a host cancel that is NOT the Back button MUST still
    ///       run: expiry, <c>ShowMissionBriefing</c>:1891's KeepEncounter arm, <c>Complete</c>, a destroyed
    ///       site. Blocking the host wholesale satisfies (a) by breaking the campaign, and that is the
    ///       cheap wrong fix this arm exists to catch.
    ///   (d) <c>solo-changed</c> — outside a session every cancel runs; vanilla untouched.
    ///   (e) <c>apply-refused</c> — a cancel reached from a rail APPLY must run, or the mirror can never
    ///       follow the host's own legitimate retirement and the two graphs diverge for good.
    ///   (f) <c>seam-is-decorative</c> — the live prefix must consult BOTH halves
    ///       (<c>Runs</c> and <c>BackingOutOfSquadScreen</c>), and the gesture premise must still hold:
    ///       <c>ToPreviousScreen</c> must still call <c>GeoMission.Cancel</c>, and
    ///       <c>UIStateRosterDeployment.Mission</c> / <c>GeoscapeView.CurrentViewState</c> must still
    ///       resolve — they ARE how the probe recognises the gesture.
    ///   (g) POSITIVE CONTROL, EXECUTED — the same table over <see cref="FakeSeam.HostAlwaysRuns"/>, the
    ///       decision exactly as it stood before this seam, MUST come back red on (a).
    ///
    /// NOT A QUORUM (P13) and nothing waits: refusing a deletion makes no peer depend on another. The
    /// declining peer still leaves the screen — L91 arm (d) pins that premise, that ToPreviousScreen's own
    /// <c>ResetViewState</c>/<c>SwitchToPreviousState</c> + <c>FinishQueriedState</c> sit BESIDE the blocked
    /// call and never inside it — and every other peer keeps a mission it can still fly.
    ///
    /// Falsify: restore <c>isHost</c> as an unconditional pass in <c>Runs</c> → (a); drop the
    /// <c>isHost</c> term entirely → (c); drop <c>applying</c> → (e); drop <c>!inSession</c> → (d); delete
    /// the <c>BackingOutOfSquadScreen</c> call from the prefix → (f).
    /// </summary>
    internal static class L370_BackingOutOfTheSquadScreenDestroysNothing
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var seam = typeof(MissionCancelGate);
            var mod = seam.Assembly;
            var decision = seam.GetMethod("Runs", All);
            var prefix = seam.GetMethod("Prefix", All);
            var probe = seam.GetMethod("BackingOutOfSquadScreen", All);
            if (decision == null || prefix == null || probe == null)
            {
                yield return "L370 premise-changed: MissionCancelGate.Runs / Prefix / BackingOutOfSquadScreen no " +
                             "longer resolve whole. Those three ARE this law — re-point it at whatever carries " +
                             "them now; do not delete it, because without them a host backing out of the squad " +
                             "screen deletes the mission for every peer again (measured 2026-08-09, and it is " +
                             "L351's deletion arriving through the sibling caller).";
                yield break;
            }

            foreach (var red in Table(MissionCancelGate.Runs, "L370")) yield return red;

            // ── (f) the live seam asks both halves ──────────────────────────────────────────────────
            var callees = Program.Callees(prefix, mod).Where(c => c != null).Select(c => c.Name).ToList();
            foreach (var needed in new[] { "Runs", "BackingOutOfSquadScreen" })
                if (!callees.Contains(needed))
                    yield return "L370 seam-is-decorative: MissionCancelGate.Prefix no longer calls " + needed +
                                 ", so the table above is proved about a decision the live gate does not make. " +
                                 "That is how L132 stayed four days green while the gate it named refused every " +
                                 "client overwatch.";

            // ── (f) the GESTURE premise: how the probe recognises the Back button ───────────────────
            var toPrev = AccessTools.Method(typeof(UIStateRosterDeployment), "ToPreviousScreen");
            var cancel = typeof(GeoMission).GetMethod("Cancel", All, null, Type.EmptyTypes, null);
            var mission = typeof(UIStateRosterDeployment).GetProperty("Mission", All);
            var current = typeof(PhoenixPoint.Geoscape.View.GeoscapeView).GetProperty("CurrentViewState", All);
            if (toPrev == null || cancel == null || mission == null || current == null)
                yield return "L370 gesture-premise-changed: " +
                             (toPrev == null ? "UIStateRosterDeployment.ToPreviousScreen" :
                              cancel == null ? "GeoMission.Cancel()" :
                              mission == null ? "UIStateRosterDeployment.Mission" :
                                                "GeoscapeView.CurrentViewState") +
                             " no longer resolves. BackingOutOfSquadScreen recognises the Back button by asking " +
                             "the live view whether the CURRENT state is the squad screen for THIS mission — it " +
                             "works only because ToPreviousScreen:258 runs Cancel BEFORE it pops the state. Lose " +
                             "any of those and the probe silently answers false, i.e. the host deletes again.";

            // ── (g) POSITIVE CONTROL: the decision as it stood before this seam ─────────────────────
            if (!Table(FakeSeam.HostAlwaysRuns, "control").Any())
                yield return "L370 control-not-red: FakeSeam.HostAlwaysRuns waves every host cancel through — the " +
                             "pre-fix behaviour — and the truth table did not flag it. The arms above are " +
                             "decorative and would stay green over a seam that had been deleted.";
        }

        /// <summary>The truth table, run over production in the arms and over <see cref="FakeSeam"/> in the
        /// control — same code both times, which is what makes the control a control.
        /// Order: (inSession, applying, isHost, backingOutOfSquadScreen).</summary>
        private static IEnumerable<string> Table(Func<bool, bool, bool, bool, bool> runs, string id)
        {
            // (a) host + the squad screen's Back button -> must NOT run
            if (runs(true, false, true, true))
                yield return id + " host-back-deletes-the-mission: a HOST backing out of the deployment screen " +
                             "runs the game's own GeoMission.Cancel (ToPreviousScreen:258), which nulls " +
                             "Site.ActiveMission, can DestroySite() and wipes Reward — so one player leaving a " +
                             "screen deletes the mission for the whole team and every other peer's launch is then " +
                             "refused with \"the host's site has no ActiveMission AT ALL\". Measured live " +
                             "2026-08-09 (multiplayer.log 02:42:13.537 -> both clients activeMission=none at " +
                             "02:42:17.50 -> host REJECT 02:44:02.782). This is L351's deletion through the " +
                             "sibling caller, which is why L351 stays green over it.";

            if (id != "control")
            {
                // (b) client, any screen -> must NOT run
                if (runs(true, false, false, false))
                    yield return id + " client-back-deletes-the-mission: a CLIENT's Cancel runs. A projector may " +
                                 "not write shared structure at all (law 3) and the diff can never correct it — " +
                                 "the diff is host-now vs host-before, so a mission only the client deleted is " +
                                 "never mentioned again.";
                // (c) host, NOT the back button -> must run
                if (!runs(true, false, true, false))
                    yield return id + " host-can-never-retire-a-mission: a host cancel that is not the squad " +
                                 "screen's Back button is refused too. Mission expiry, ShowMissionBriefing:1891's " +
                                 "KeepEncounter arm, Complete and a destroyed site all reach GeoMission.Cancel — " +
                                 "block those and missions accumulate on the map forever. Blocking the host " +
                                 "wholesale is the cheap wrong fix that satisfies the first arm by breaking the " +
                                 "campaign.";
                // (d) solo is vanilla
                if (!runs(false, false, false, true))
                    yield return id + " solo-changed: outside a co-op session a cancel is refused. Single-player " +
                                 "must be bit-identical to vanilla here — a solo player backing out of the squad " +
                                 "screen is SUPPOSED to cancel the mission.";
                // (e) a rail apply must reach the native write
                if (!runs(true, true, false, true))
                    yield return id + " apply-refused: a Cancel reached from inside a rail apply is blocked, so a " +
                                 "client can never follow the host's own legitimate retirement of a mission and " +
                                 "the two graphs diverge permanently — the gate turning itself into the drift it " +
                                 "exists to prevent.";
            }
        }

        private static class FakeSeam
        {
            /// <summary>THE POSITIVE CONTROL: the decision exactly as it stood before this seam — the host is
            /// waved through whatever gesture it came from. The table MUST flag it on arm (a).</summary>
            internal static bool HostAlwaysRuns(bool inSession, bool applying, bool isHost, bool backingOut)
                => !inSession || applying || isHost;
        }
    }
}
