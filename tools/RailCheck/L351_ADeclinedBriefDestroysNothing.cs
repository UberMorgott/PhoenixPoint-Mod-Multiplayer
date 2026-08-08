using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Utils;
using PhoenixPoint.Geoscape.View;

namespace RailCheck
{
    /// <summary>
    /// L351 — A DECLINED MISSION BRIEF DESTROYS NOTHING AND BLOCKS NOBODY.
    ///
    /// THE REPORT (2026-08-08, live 3-peer session). One player pressed CANCEL on a deployment brief and the
    /// mission was gone for the whole team. The other peers kept the window; their Confirm was then refused
    /// with <c>the host's site has no ActiveMission AT ALL</c>. The route is one line of the game's own:
    /// <c>GeoscapeView.ModalResultCallback</c>:825-826 answers a brief's Cancel with
    /// <c>geoMission.Cancel()</c>, and <c>GeoMission.Cancel</c>:253-265 writes <c>Site.ActiveMission = null</c>,
    /// may <c>Site.DestroySite()</c> and replaces <c>Reward</c>.
    ///
    /// <see cref="MissionCancelGate"/> already blocked that AT THE MODEL — on a CLIENT only (:1059 lets the
    /// host run native), and the destructive call ran on the HOST. So the assertion here is about the
    /// ANSWER, on both roles: declining is a statement about one player's availability, never a deletion of
    /// shared campaign state.
    ///
    /// THE OUTCOME, NOT THE CALL. The arms execute <see cref="PerPeerModalAnswer.Runs"/> over its whole truth
    /// table instead of checking that a patch attribute exists — the shape this repo keeps paying for (L81,
    /// L96, L132, L186 were all green over live breakage).
    ///
    ///   (a) <c>decline-deletes-the-mission</c> — a non-Confirm answer to this class in a session must NOT
    ///       run. This is the whole law and the row the report is.
    ///   (b) <c>confirm-refused</c> — Confirm MUST run. It is the gesture that starts the mission for
    ///       everybody (LaunchMission:1043); refuse it and no peer can ever deploy again. A "fix" that
    ///       blocks the callback wholesale satisfies (a) and removes the feature.
    ///   (c) <c>solo-changed</c> — outside a session every answer runs, vanilla untouched.
    ///   (d) <c>every-modal-frozen</c> — a modal OUTSIDE the class keeps its Cancel arm (an ability
    ///       confirmation, a research-complete, the event picker). The line sits at the mission brief, not at
    ///       every window with a No button.
    ///   (e) <c>class-has-no-mission</c> — <see cref="GeoWindowCoverage.IsPerPeerAnswerClass"/> requires a
    ///       GeoMission. <c>InterceptionBrief</c>/<c>HavenInfiltrateBrief</c> carry none and must drop out.
    ///   (f) <c>seam-is-decorative</c> — the live prefix must actually consult both halves
    ///       (<c>IsPerPeerAnswer</c> and <c>Runs</c>), and the method it patches must still exist with the
    ///       signature Harmony binds by name.
    ///   (g) POSITIVE CONTROL, EXECUTED — the same table over <see cref="FakeSeam.AlwaysRuns"/> (the decision
    ///       as it stood before this seam) MUST come back red on (a).
    ///
    /// Falsify: make <c>Runs</c> return true → (a); return <c>!inSession || !perPeerClass</c> → (b);
    /// <c>inSession &amp;&amp; …</c> → (c); drop the <c>perPeerClass</c> term → (d); drop <c>hasMission</c>
    /// from <c>IsPerPeerAnswerClass</c> → (e); delete the <c>IsPerPeerAnswer</c> call from the prefix → (f).
    /// </summary>
    internal static class L351_ADeclinedBriefDestroysNothing
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var seam = typeof(PerPeerModalAnswer);
            var mod = seam.Assembly;
            var decision = seam.GetMethod("Runs", All);
            var prefix = seam.GetMethod("Prefix", All);
            var klass = typeof(GeoWindowCoverage).GetMethod("IsPerPeerAnswerClass", All);
            if (decision == null || prefix == null || klass == null)
            {
                yield return "L351 premise-changed: PerPeerModalAnswer.Runs / Prefix or " +
                             "GeoWindowCoverage.IsPerPeerAnswerClass no longer resolve whole. Every arm below " +
                             "reads that trio; losing one silently un-asserts the only thing standing between one " +
                             "player pressing CANCEL on a brief and GeoMission.Cancel:253 deleting the mission for " +
                             "the whole team (2026-08-08).";
                yield break;
            }

            foreach (var red in Table(PerPeerModalAnswer.Runs, "L351")) yield return red;

            // ── (e) the class itself needs a mission ─────────────────────────────────────────────────
            if (GeoWindowCoverage.IsPerPeerAnswerClass(false, true, false) ||
                GeoWindowCoverage.IsPerPeerAnswerClass(false, false, true))
                yield return "L351 class-has-no-mission: IsPerPeerAnswerClass puts a window with NO GeoMission " +
                             "into the per-peer class. InterceptionBrief and HavenInfiltrateBrief reach " +
                             "ModalResultCallback with modalData that is not a mission, and freezing their answers " +
                             "would leave an interception dialog that does nothing at all when dismissed.";
            if (!GeoWindowCoverage.IsPerPeerAnswerClass(true, true, false) ||
                !GeoWindowCoverage.IsPerPeerAnswerClass(true, false, true))
                yield return "L351 class-is-empty: a mission BRIEF or its OUTCOME sibling is no longer in the " +
                             "per-peer class, so the whole seam is inert and one peer's Cancel deletes the shared " +
                             "mission exactly as it did on 2026-08-08.";

            // ── (f) the live seam asks the question, and still binds ─────────────────────────────────
            var callees = Program.Callees(prefix, mod).Where(c => c != null).Select(c => c.Name).ToList();
            foreach (var needed in new[] { "IsPerPeerAnswer", "Runs" })
                if (!callees.Contains(needed))
                    yield return "L351 seam-is-decorative: PerPeerModalAnswer.Prefix no longer calls " + needed +
                                 ", so the arms above are proved about a decision the live seam does not make. " +
                                 "That is exactly how L132 stayed four days green while the gate it named refused " +
                                 "every client overwatch.";

            var target = AccessTools.Method(typeof(GeoscapeView), "ModalResultCallback",
                                            new[] { typeof(ModalType), typeof(ModalResult), typeof(object) });
            if (target == null)
                yield return "L351 patch-cannot-bind: GeoscapeView.ModalResultCallback(ModalType, ModalResult, " +
                             "object) does not resolve, so the [HarmonyPatch] on it binds NOTHING — PatchAll turns " +
                             "that into one swallowed warning (L23) and every peer's Cancel runs the native " +
                             "GeoMission.Cancel again.";

            // ── (g) POSITIVE CONTROL: the decision as it stood before the seam ───────────────────────
            if (!Table(FakeSeam.AlwaysRuns, "control").Any())
                yield return "L351 control-not-red: FakeSeam.AlwaysRuns lets every answer run — the pre-fix " +
                             "behaviour — and the truth table did not flag it. The arms above are decorative and " +
                             "would stay green over a seam that had been deleted.";
        }

        /// <summary>The whole truth table, run over production in the arms and over <see cref="FakeSeam"/> in
        /// the control — same code both times, which is what makes the control a control.</summary>
        private static IEnumerable<string> Table(Func<bool, bool, bool, bool> runs, string id)
        {
            // (a) in a session, this class, NOT a Confirm -> must not run
            if (runs(true, true, false))
                yield return id + " decline-deletes-the-mission: a peer answering a mission brief with anything " +
                             "other than Confirm runs the game's own ModalResultCallback arm, which is " +
                             "geoMission.Cancel() (GeoscapeView.cs:825-826). GeoMission.Cancel:253-265 nulls " +
                             "Site.ActiveMission, can DestroySite() and wipes Reward — so ONE player saying \"not " +
                             "now\" deletes the mission for every peer, and the ones still holding the window get " +
                             "\"the host's site has no ActiveMission AT ALL\" when they press Confirm. Measured " +
                             "live 2026-08-08. Note this row is reached on the HOST too: MissionCancelGate:1059 " +
                             "waves the host through, and the host is where the destructive call ran.";

            if (id != "control")
            {
                // (b) Confirm always runs
                if (!runs(true, true, true))
                    yield return id + " confirm-refused: Confirm no longer reaches the game's own callback, so " +
                                 "LaunchMission:1043 never runs and NO peer can start a mission from a brief. " +
                                 "Blocking the callback wholesale passes the decline arm by removing the feature.";
                // (c) solo is vanilla
                if (!runs(false, true, false))
                    yield return id + " solo-changed: outside a co-op session an answer is refused. Single-player " +
                                 "must be bit-identical to vanilla here — a solo player declining a brief is " +
                                 "SUPPOSED to cancel the mission.";
                // (d) the rest of the modal family keeps its Cancel
                if (!runs(true, false, false))
                    yield return id + " every-modal-frozen: a modal OUTSIDE the mission brief/outcome class is " +
                                 "refused as well. ModalResultCallback also drives ability confirmations, the " +
                                 "research-complete navigation and the interception dialog; freezing those makes " +
                                 "every No button in the geoscape do nothing.";
            }
        }

        private static class FakeSeam
        {
            /// <summary>THE POSITIVE CONTROL: the decision as it effectively stood before this seam existed —
            /// every answer runs the game's own callback. The table MUST flag it.</summary>
            internal static bool AlwaysRuns(bool inSession, bool perPeerClass, bool isConfirm) => true;
        }
    }
}
