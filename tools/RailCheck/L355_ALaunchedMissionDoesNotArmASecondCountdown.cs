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
    /// L355 — A MISSION THAT ALREADY LAUNCHED DOES NOT ARM A SECOND COUNTDOWN, AND THE BUTTON THAT ASKS FOR
    /// ONE IS DEAD WHILE THE FIRST RUNS.
    ///
    /// THE CRASH, twice in one session (host 2026-08-08 22:22:45 and 22:52:14), reconstructed frame by frame
    /// from the 22:52 one: countdown armed at <c>3569,652</c> on the host's own press; nonces <b>213-219</b>
    /// each hit <c>_pending != null</c> and were refused SILENTLY while <c>MissionSync</c> printed "HOST intent
    /// APPLIED op=launch" for every one of them; at <c>3574,697</c> the count reached zero and the native
    /// launch ran; at <c>3574,806</c> — <b>109 ms later</b> — nonce <b>220</b> arrived, found <c>_pending</c>
    /// empty, and armed A SECOND COUNTDOWN ON AN ALREADY-LAUNCHED MISSION; at <c>3579,979</c> that one reached
    /// zero and <c>GeoMission.Launch_Patch2</c> threw. Same profile at <c>1803,385→1814,413</c>, nonces
    /// 150/151, across two sites.
    ///
    /// WHY NOTHING CAUGHT IT, and why each of those is an arm below rather than a comment:
    ///   • <c>HostTick</c>'s <c>!mission.IsRunnable</c> guard is VACUOUS — <c>GeoMission.cs:147
    ///     IsRunnable =&gt; true</c> with ZERO overrides in the shipped assembly.
    ///   • <see cref="MissionSync.Validate"/> cannot see it either: <c>GeoMission.Launch</c>:226-245 never
    ///     touches <c>Site.ActiveMission</c> (only <c>Cancel</c>:253 does), so <c>MissionExists</c> and
    ///     <c>MissionRunnable</c> stay true after a launch.
    ///   • <c>IntentDedup</c> is CORRECT to pass them: nine distinct nonces are nine distinct clicks. This law
    ///     asserts nothing about it and nothing here may be "fixed" there.
    ///
    /// ARMS
    ///   (a) <c>relaunch-arms-again</c> — <see cref="DeployCountdown.ArmsFor"/> EXECUTED over its truth table;
    ///       the falsifying row is <c>alreadyLaunched = true</c>, which IS nonce 220.
    ///   (b) <c>own-reissue-refused</c> — the host's own re-issue (<c>committed</c>) and every non-host /
    ///       solo call must still run native, or the countdown can never release the launch it holds.
    ///   (c) <c>second-press-stacks</c> — a press while one is already counting must not arm another.
    ///   (d) <c>button-live-during-countdown</c> — <see cref="DeployCountdown.ButtonLive"/> is false while the
    ///       count runs whatever the native verdict says, and the postfix that applies it is attached to
    ///       <c>UIStateRosterDeployment.CheckForDeployment</c> — the ONE writer of the button's
    ///       interactability (:377), and a POSTFIX so it also runs when TFTV's own Prefix returns false.
    ///   (e) <c>vacuous-premise</c> — <c>GeoMission.IsRunnable</c> still has no overrides. The moment one
    ///       appears, <c>HostTick</c>'s guard stops being vacuous and this law's ground has changed.
    ///   (f) <c>latch-outlives-the-boundary</c> — <c>ResetForReloadBoundary</c> must clear it, or one
    ///       launched mission poisons the same site for the rest of the session.
    ///   (g) POSITIVE CONTROL, EXECUTED — the same table over <see cref="FakeSeam.AlwaysArms"/> MUST be red.
    ///
    /// Falsify: drop the <c>alreadyLaunched</c> term from <c>ArmsFor</c> → (a); drop <c>pendingBusy</c> → (c);
    /// <c>ButtonLive =&gt; nativeVerdict</c> → (d).
    /// </summary>
    internal static class L355_ALaunchedMissionDoesNotArmASecondCountdown
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var cd = typeof(DeployCountdown);
            var mod = cd.Assembly;
            var arms = cd.GetMethod("ArmsFor", All);
            var live = cd.GetMethod("ButtonLive", All);
            var gate = cd.GetMethod("Gate", All);
            var reset = cd.GetMethod("ResetForReloadBoundary", All);
            var lockPostfix = typeof(DeployButtonCountdownLock).GetMethod("Postfix", All);
            if (arms == null || live == null || gate == null || reset == null || lockPostfix == null)
            {
                yield return "L355 premise-changed: DeployCountdown.ArmsFor / ButtonLive / Gate / " +
                             "ResetForReloadBoundary or DeployButtonCountdownLock.Postfix no longer resolve " +
                             "whole. Those are the two halves that keep a launched mission from arming a second " +
                             "countdown — re-point this law; do not delete it, the failure is a thrown " +
                             "GeoMission.Launch that fired twice in one session.";
                yield break;
            }

            foreach (var red in Table(DeployCountdown.ArmsFor, "L355")) yield return red;

            // ── (d) the button, and the seam that applies the verdict ───────────────────────────────
            if (DeployCountdown.ButtonLive(true, true))
                yield return "L355 button-live-during-countdown: START MISSION stays pressable while the " +
                             "countdown runs. That is the click factory the reported session ran on — nine " +
                             "launch intents against one mission, seven of them swallowed and the ninth landing " +
                             "109 ms after the launch had already happened.";
            if (!DeployCountdown.ButtonLive(true, false))
                yield return "L355 button-dead-always: the button is refused with no countdown running, so " +
                             "nobody can ever deploy. Greying it unconditionally passes the arm above by " +
                             "removing the feature.";
            if (DeployCountdown.ButtonLive(false, false))
                yield return "L355 button-overrides-the-game: this lock GRANTS interactability the game's own " +
                             "verdict (CheckForDeployment:377 flag && flag3 && flag2) withheld — an empty or " +
                             "over-capacity squad would become launchable.";
            if (!Program.Callees(lockPostfix, mod).Any(c => c != null && c.Name == "ButtonLive"))
                yield return "L355 lock-is-decorative: DeployButtonCountdownLock.Postfix no longer calls " +
                             "ButtonLive, so the arms above are proved about a verdict nothing applies.";
            if (typeof(DeployButtonCountdownLock).GetMethod("Prefix", All) != null)
                yield return "L355 lock-is-a-prefix: DeployButtonCountdownLock declares a Prefix. It must be a " +
                             "POSTFIX — the native write at UIStateRosterDeployment.cs:377 has to land first, " +
                             "and a postfix is also what still runs when TFTV's own Prefix on " +
                             "CheckForDeployment returns false (AircraftReworkMissionDeployment.cs:136-179).";

            // ── (e) the vacuous premise this whole family rests on ──────────────────────────────────
            var overrides = AccessTools.GetTypesFromAssembly(typeof(GeoMission).Assembly)
                                       .Where(t => t != null && t != typeof(GeoMission) &&
                                                   typeof(GeoMission).IsAssignableFrom(t))
                                       .Where(t => t.GetProperty("IsRunnable", BindingFlags.Public |
                                                                               BindingFlags.Instance |
                                                                               BindingFlags.DeclaredOnly) != null)
                                       .Select(t => t.Name).ToList();
            if (overrides.Count > 0)
                yield return "L355 vacuous-premise: GeoMission.IsRunnable now has override(s) — " +
                             string.Join(", ", overrides) + ". HostTick's `!mission.IsRunnable` guard was a " +
                             "constant true when this law was written, which is WHY the already-launched latch " +
                             "had to exist; with a real override that guard may now carry some of the weight, " +
                             "and both it and MissionSync.Validate's MissionRunnable fact need re-reading " +
                             "before anything here is simplified away.";

            // ── (f) the latch is cleared at the boundary ────────────────────────────────────────────
            if (!Program.Callees(reset, mod).Any(c => c != null && c.Name == "ClearPending") ||
                !WritesLaunched(reset))
                yield return "L355 latch-outlives-the-boundary: ResetForReloadBoundary does not clear the " +
                             "already-launched latch. Mod state must be EMPTY at every reload boundary " +
                             "(IdentityResolver.cs:205-206), and a latch left set refuses every later launch of " +
                             "that same mission object for the rest of the session — silently, because the " +
                             "refusal is host-local.";

            // ── (g) POSITIVE CONTROL ────────────────────────────────────────────────────────────────
            if (!Table(FakeSeam.AlwaysArms, "control").Any())
                yield return "L355 control-not-red: FakeSeam.AlwaysArms arms for everything — the pre-fix " +
                             "behaviour of the 109 ms window — and the table did not flag it.";
        }

        private static bool WritesLaunched(MethodBase m)
        {
            byte[] il;
            try { il = m?.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null) return false;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x80) continue;                        // stsfld
                try
                {
                    if (m.Module.ResolveField(BitConverter.ToInt32(il, i + 1)).Name == "_launched") return true;
                }
                catch { }
            }
            return false;
        }

        /// <summary>The truth table, run over production in the arms and over <see cref="FakeSeam"/> in the
        /// control. Signature: (inSession, isHost, committed, pendingBusy, alreadyLaunched).</summary>
        private static IEnumerable<string> Table(Func<bool, bool, bool, bool, bool, bool> arms, string id)
        {
            // (a) THE ROW THAT IS NONCE 220: host, nothing pending, mission already launched -> NO arm
            if (arms(true, true, false, false, true))
                yield return id + " relaunch-arms-again: a launch intent for a mission that ALREADY LAUNCHED " +
                             "arms a second countdown. That is nonce 220, 109 ms after the first countdown " +
                             "released its launch: _pending is empty again by then (ClearPending runs before " +
                             "the native call), IsRunnable is a constant true, and Site.ActiveMission is " +
                             "untouched by Launch — so NOTHING else in this repo can refuse it, and five " +
                             "seconds later GeoMission.Launch throws. Twice in one session.";

            if (id != "control")
            {
                // (b) the release, and everybody who is not the host
                if (arms(true, true, true, false, false))
                    yield return id + " own-reissue-armed: the host's OWN re-issue (committed) arms yet another " +
                                 "countdown instead of running native. The launch the count has been holding " +
                                 "would then never happen at all.";
                if (arms(false, true, false, false, false) || arms(true, false, false, false, false))
                    yield return id + " solo-or-client-arms: a solo game or a CLIENT arms the host's countdown. " +
                                 "The countdown belongs to the host — the only peer that can start the battle — " +
                                 "and vanilla must be untouched outside a session.";
                // (c) a second press while one runs
                if (arms(true, true, false, true, false))
                    yield return id + " second-press-stacks: a press while a countdown is already running arms " +
                                 "a SECOND one. Both would reach zero and both would launch.";
                // the ordinary case must still arm, or the feature is gone
                if (!arms(true, true, false, false, false))
                    yield return id + " never-arms: the ordinary host press does not arm a countdown at all, so " +
                                 "the five-second drop — and every peer's chance to cancel it — is gone.";
            }
        }

        private static class FakeSeam
        {
            /// <summary>THE POSITIVE CONTROL: the decision as it stood in the crashing build.</summary>
            internal static bool AlwaysArms(bool inSession, bool isHost, bool committed, bool pendingBusy,
                                            bool alreadyLaunched) => true;
        }
    }
}
