using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;

namespace RailCheck
{
    /// <summary>
    /// L336 — THE READY BUTTON'S REACHABILITY IS MEASURED WHEN A POINTER COULD ACTUALLY REACH IT.
    ///
    /// WHAT HAPPENED (2026-08-08). <c>TacticalReadyRowFollower.ProbeReachable</c> filed
    /// "ready button is BURIED ... under vignette" as an ERROR in four battles out of four, on every peer —
    /// and in the same logs the host then CLICKED that button seventeen times. Both facts are true, because
    /// the probe fired while the ink loading curtain was still on the screen:
    ///
    ///   BURIED 17:57:27.042 -> curtain lift RELEASED 17:57:45.666   (18.6 s later)
    ///   BURIED 18:27:24.567 -> curtain lift RELEASED 18:27:38.327   (13.8 s)
    ///   BURIED 18:46:32.310 -> curtain lift RELEASED 18:46:53.382   (21.1 s)
    ///   BURIED 18:57:35.227 -> curtain lift RELEASED 18:57:50.083   (14.9 s)
    ///
    /// <c>vignette</c> and <c>InkBG</c> at sorting order 100 are that curtain
    /// (<c>SceneFadeController.Curtain</c>, a <c>UseInkUI</c>), and this mod itself PARKS its lift on the
    /// all-players reveal (<c>CurtainLiftGatePatch.Gated</c>) — so a co-op battle spends its first 15-20
    /// seconds under a full-screen overlay by our own design. The EventSystem answering "the curtain" there
    /// is the raycaster being right, not the button being wrong.
    ///
    /// THE GATE THAT FAILED was <c>Drawn()</c> alone, and the comment above it already named the exact
    /// hazard it was meant to cover ("the battle opens behind the loading curtain ... a full-screen vignette
    /// is on top of everything"). It does not cover it: the clone's graphics BATCH behind the curtain — live
    /// depth 261-267 in the same footprint lines — so Drawn() is already true down there.
    ///
    /// WHY THIS COSTS MORE THAN A STRAY LINE. The probe is the only instrument that can say the ready button
    /// is dead; L220 arm (c) exists solely to keep it able to say NO out loud. An instrument that cries NO
    /// every single battle on a working button is worse than none — the next real burial arrives as the
    /// fifth identical ERROR in a log full of them. That is this repo's own lesson from the opposite
    /// direction (L220's own docs: a diagnostic that only speaks when the answer is good).
    ///
    /// NOT A PLACEMENT LAW. The clone sitting 30 world units below <c>EndTurnContainerModule</c> is the
    /// widget's SPECIFICATION, not a defect — the ask was a second button UNDER a container that is exactly
    /// one button tall, and <c>ReportFootprint</c> says so in its own verdict text. Containment is therefore
    /// deliberately NOT asserted here; asserting it would freeze a bug report into the harness.
    ///
    /// THE ARMS — (a) and (b) are EXECUTED on <see cref="TacticalReadyRowFollower.ProbeNow"/>, the whole
    /// decision, extracted pure precisely so this law runs it rather than scanning for a call:
    ///   (a) <c>ready-probe-under-the-curtain</c> — the arm that would have been RED through all four false
    ///       reports. With the curtain down the probe does not fire, whatever else is true: drawn, not
    ///       drawn, one frame in or ten thousand.
    ///   (b) <c>ready-probe-never-fires</c> — the opposite failure, and the one a lazy fix would ship: the
    ///       gate must NOT be a mute button. Curtain up + drawn must fire on the spot, and curtain up +
    ///       never drawn must still fire once the frame bound is spent, because "the canvas never drew this
    ///       clone" is a real finding this diagnostic exists to file.
    ///   (c) <c>ready-probe-ungated</c> — the wiring half, honestly labelled as a CALL assertion: the method
    ///       that fires the raycast must be gated by the pure function above, or (a) is arithmetic nobody
    ///       runs. It also names <c>IsCurtainLifted</c>: the answer has to come from the game's own flag
    ///       (<c>LevelSwitchCurtainController</c>:113), not from a timer or a guess at how long a curtain
    ///       lasts — the reveal barrier it is parked on has no deadline, so neither can this.
    ///   (d) POSITIVE CONTROL, EXECUTED — <see cref="FakeSeam"/> supplies the pre-fix gate (ignores the
    ///       curtain) and a mute one (never fires). (a) and (b) must both go red on them.
    ///
    /// Falsify: drop the <c>curtainLifted</c> term from <c>ProbeNow</c> -> (a); make it return false -> (b);
    /// remove the <c>ProbeNow</c> call from <c>TacticalReadyRowFollower.Apply</c> -> (c).
    /// </summary>
    internal static class L336_TheReadyProbeMeasuresALiftedCurtain
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        /// <summary>The shape of the gate, so the real one and a deliberately broken one run the same cases.</summary>
        private delegate bool Gate(bool curtainLifted, bool drawn, int waited);

        internal static IEnumerable<string> Check()
        {
            var probe = typeof(TacticalReadyRowFollower).GetMethod("ProbeNow", AllMembers);
            var apply = typeof(TacticalReadyRowFollower).GetMethod("Apply", AllMembers);
            var lifted = typeof(Base.Utils.LevelSwitchCurtainController)
                .GetProperty("IsCurtainLifted", BindingFlags.Public | BindingFlags.Instance);

            if (probe == null || apply == null || lifted == null)
            {
                yield return "L336 premise-changed: one of TacticalReadyRowFollower.ProbeNow, " +
                             "TacticalReadyRowFollower.Apply or " +
                             "Base.Utils.LevelSwitchCurtainController.IsCurtainLifted no longer resolves. The " +
                             "seam this law is written over has moved, so every arm below would be asserting " +
                             "about a shape this build does not have.";
                yield break;
            }

            foreach (var v in ScanGate(TacticalReadyRowFollower.ProbeNow,
                                       "TacticalReadyRowFollower.ProbeNow")) yield return v;
            foreach (var v in ScanWiring(typeof(TacticalReadyRowFollower), "TacticalReadyRowFollower"))
                yield return v;

            // ── arm (d): the cases must be able to SEE each failure.
            var control = ScanGate(FakeSeam.CurtainBlind, "FakeSeam.CurtainBlind")
                .Concat(ScanGate(FakeSeam.NeverFires, "FakeSeam.NeverFires"))
                .Concat(ScanWiring(typeof(FakeSeam), "FakeSeam"))
                .ToList();
            foreach (var want in new[] { "ready-probe-under-the-curtain", "ready-probe-never-fires",
                                         "ready-probe-ungated" })
                if (!control.Any(c => c.Contains(want)))
                    yield return "L336 control-not-red: FakeSeam commits " + want + " and the cases did not " +
                                 "flag it. The arm cannot tell a gated probe from an ungated one, so its green " +
                                 "above is arithmetic that proves nothing — which is exactly how L220 stood " +
                                 "over this same diagnostic while it filed four false ERRORs.";
        }

        /// <summary>ARMS (a) and (b), EXECUTED. Each case is a state the live logs actually contained, or the
        /// state the fix must not break.</summary>
        private static IEnumerable<string> ScanGate(Gate gate, string label)
        {
            // ── (a) curtain DOWN: never, on any combination of the other two.
            var underCurtain = new[]
            {
                // why, drawn, waited
                new Case("the live false report — our graphics batched at depth 261-267 BEHIND the ink " +
                         "curtain, ten seconds into the battle", true, 600),
                new Case("the same, on the first frame", true, 0),
                new Case("not drawn yet, frame bound spent, curtain still down", false, 100000),
                new Case("not drawn yet, curtain still down", false, 0),
            };
            foreach (var c in underCurtain)
                if (gate(false, c.Drawn, c.Waited))
                    yield return "L336 ready-probe-under-the-curtain: " + label + " fires the reachability " +
                                 "probe with the loading curtain still down (" + c.Why + "). Under a " +
                                 "full-screen overlay the EventSystem's honest answer is the overlay, so the " +
                                 "measurement is of the curtain and the ERROR it files names a burial that " +
                                 "does not exist — four battles out of four, on a button the host went on to " +
                                 "click seventeen times. This mod PARKS the lift on the all-players reveal " +
                                 "itself, so those first 15-20 seconds are our own design, not a slow machine.";

            // ── (b) curtain UP: the probe must still be able to fire, or the fix is a mute button.
            var mustFire = new[]
            {
                new Case("curtain lifted and the clone drawn — the state the whole diagnostic is FOR", true, 0),
                new Case("curtain lifted, canvas never drew the clone, frame bound spent — a genuinely dead " +
                         "button, which is a real finding and must still land", false, 100000),
            };
            foreach (var c in mustFire)
                if (!gate(true, c.Drawn, c.Waited))
                    yield return "L336 ready-probe-never-fires: " + label + " refuses to measure with the " +
                                 "curtain lifted (" + c.Why + "). Silencing the probe is not the fix — it is " +
                                 "the same defect wearing the other sign, and it removes the only instrument " +
                                 "in this repo that can say the ready button takes no click (L220 arm (c) " +
                                 "exists to keep that NO audible).";
        }

        private sealed class Case
        {
            internal Case(string why, bool drawn, int waited) { Why = why; Drawn = drawn; Waited = waited; }
            internal readonly string Why;
            internal readonly bool Drawn;
            internal readonly int Waited;
        }

        /// <summary>ARM (c), and it asserts a CALL — said out loud, because a pure gate nobody consults is
        /// decoration. The method that writes the one-shot latch must ask it, and the type must read the
        /// game's own curtain flag rather than time the curtain itself.</summary>
        private static IEnumerable<string> ScanWiring(Type seam, string label)
        {
            bool gated = false, asksTheGame = false;
            foreach (var m in AllMethodsOf(seam))
            {
                if (Reaches(m, null, "ProbeNow")) gated = true;
                if (Reaches(m, "LevelSwitchCurtainController", "get_IsCurtainLifted")) asksTheGame = true;
            }

            if (!gated)
                yield return "L336 ready-probe-ungated: " + label + " never calls ProbeNow, so the gate that " +
                             "keeps the reachability measurement out of the curtain era is arithmetic nobody " +
                             "runs. The probe then fires on the old Drawn()-alone condition, which is TRUE " +
                             "behind the curtain (the clone batches at depth 261-267 down there) — the exact " +
                             "shape that filed BURIED in four battles out of four.";
            else if (!asksTheGame)
                yield return "L336 ready-probe-ungated: " + label + " gates on ProbeNow but never reads " +
                             "LevelSwitchCurtainController.IsCurtainLifted, so 'is the curtain gone' is being " +
                             "answered by something other than the game — a frame count, a timer, a guess. " +
                             "The lift this waits on is parked on the all-players reveal, and that barrier has " +
                             "NO deadline by design (law L84), so any budget here just re-files the same false " +
                             "alarm a few seconds later.";
        }

        /// <summary>ARM (d). Never instantiated, never registered — walked and executed only. Two broken
        /// gates and a type that consults neither.</summary>
        private sealed class FakeSeam
        {
            /// <summary>(a): the gate as it stood through all four false reports — the curtain is not a term.</summary>
            internal static bool CurtainBlind(bool curtainLifted, bool drawn, int waited)
                => drawn || waited >= 600;

            /// <summary>(b): the lazy "fix" — no more false alarms, and no more measurement either.</summary>
            internal static bool NeverFires(bool curtainLifted, bool drawn, int waited) => false;

            /// <summary>(c): fires the one-shot without asking anything.</summary>
            internal static void Measure(ref bool measured) { measured = true; }
        }

        // ─── IL helpers (same primitives as L220; Program.cs is not partial) ─────────────────────

        private static IEnumerable<MethodBase> AllMethodsOf(Type t)
            => t.GetMethods(AllMembers).Cast<MethodBase>().Concat(t.GetConstructors(AllMembers));

        private static bool Reaches(MethodBase caller, string declaringType, string calleeName)
            => CalleesOf(caller).Any(c => c.Name == calleeName &&
                                          (declaringType == null || c.DeclaringType?.Name == declaringType));

        private static IEnumerable<MethodBase> CalleesOf(MethodBase caller)
        {
            foreach (var tok in TokensAfter(caller, 0x28, 0x6F, 0x73))
            {
                MethodBase c = null;
                try { c = caller.Module.ResolveMethod(tok); } catch { }
                if (c != null) yield return c;
            }
        }

        private static IEnumerable<int> TokensAfter(MethodBase m, params byte[] opcodes)
        {
            byte[] il;
            try { il = m?.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null) yield break;
            for (int i = 0; i + 4 < il.Length; i++)
                if (Array.IndexOf(opcodes, il[i]) >= 0)
                    yield return BitConverter.ToInt32(il, i + 1);
        }
    }
}
