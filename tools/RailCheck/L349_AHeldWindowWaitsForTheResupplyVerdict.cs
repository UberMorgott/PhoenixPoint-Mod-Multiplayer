using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L349 — WHILE A RETURNING PEER'S RESUPPLY VERDICT IS STILL OUT, NO HELD WINDOW IS CARRIED.
    ///
    /// THE OUTCOME: the one gate both window paths route through refuses while the verdict is pending, and
    /// the pending flag is the real countdown rather than a constant that satisfies a scan.
    ///
    /// THE DEFECT. A returning client enters <c>UIStateInitial</c> before the host's post-mission writes
    /// land, so its resupply screen can only be queued a beat later (<c>ReplenishSync.ClientArrivalTick</c>
    /// re-asks the game's own <c>GetMissingItems()</c> over a bounded window). The held-window drain starts
    /// replaying about a SECOND before that beat. Rank 20 (<c>ReplenishSync.Rank</c>, applied by
    /// <c>QueueRankPatch</c>) cannot rescue the order once a window is CURRENT:
    /// <c>GeoscapeViewSwitchQuery.ProcessQueriedStateSwitch</c>:58-63 dequeues only while
    /// <c>_currentStateSwitchRequest == null</c>, so a mirrored event that got in first owns the screen and
    /// the resupply screen queues behind it. Keeping the order therefore has to happen BEFORE the queue.
    ///
    /// THIS IS NOT A QUORUM AND THE LAW ASSERTS THE DIFFERENCE, because the mod's hardest rule forbids one.
    /// <c>_recheckFrames</c> is a monotone LOCAL countdown with a 180-frame (3 s) ceiling; it reads no other peer,
    /// waits on no human action, and HOLDS rather than drops — the drain retries every tick, so the window
    /// is served the moment the verdict is in. Arm (a) is what keeps it honest: a flag that could stay true
    /// forever would fail it, because the flag it executes is the countdown itself.
    ///
    /// ARMS
    ///   (a) <c>verdict-is-not-the-countdown</c> — EXECUTED. <c>ResupplyVerdictPending</c> must be TRUE with
    ///       frames left and FALSE at zero. Both directions: <c>=&gt; false</c> passes arm (b) while gating
    ///       nothing, and <c>=&gt; true</c> would hold every window of the session forever.
    ///   (b) <c>held-window-outruns-the-verdict</c> — <c>EventPopup.CanCarryWindow</c> must reach the flag,
    ///       and <c>DrainHeldRaises</c> + <c>RaiseMirrored</c> must both reach <c>CanCarryWindow</c>. The
    ///       second half is what makes ONE gate cover both callers; a check inlined into the drain alone
    ///       leaves the live-raise path racing exactly as before.
    ///   (c) <c>control-not-red</c> — POSITIVE CONTROL. A seam whose gate asks nothing must raise (b).
    ///
    /// STRUCTURAL for arm (b), and the reason is not laziness: <c>CanCarryWindow</c> takes a live
    /// <c>GeoLevelController</c> and RailCheck cannot construct one. Handed null it answers false whatever
    /// the verdict says, so executing it could not tell a refusal caused by the verdict from one caused by
    /// the missing view. The half that CAN be executed is executed — that is arm (a).
    ///
    /// Falsify (RUN 2026-08-09, both verified RED against a real build and restored):
    ///   • VERIFIED RED — delete <c>if (ReplenishSync.ResupplyVerdictPending) return false;</c> from
    ///     <c>CanCarryWindow</c> → <c>held-window-outruns-the-verdict</c>
    ///   • VERIFIED RED — <c>ResupplyVerdictPending =&gt; false</c> → <c>verdict-is-not-the-countdown</c>
    ///   • not run — rename the property, the gate or either caller → <c>premise-changed</c>
    /// </summary>
    internal static class L349_AHeldWindowWaitsForTheResupplyVerdict
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var popup = typeof(EventPopup);
            var gate = popup.GetMethod("CanCarryWindow", AllMembers);
            var drain = popup.GetMethod("DrainHeldRaises", AllMembers);
            var raise = popup.GetMethod("RaiseMirrored", AllMembers);
            var pending = typeof(ReplenishSync).GetProperty("ResupplyVerdictPending", AllMembers)?.GetGetMethod(true);
            var frames = typeof(ReplenishSync).GetField("_recheckFrames", AllMembers);

            if (gate == null || drain == null || raise == null || pending == null || frames == null)
            {
                yield return "L349 premise-changed: one of EventPopup.CanCarryWindow / DrainHeldRaises / " +
                             "RaiseMirrored (the gate and the two paths it gates), " +
                             "ReplenishSync.ResupplyVerdictPending or ReplenishSync._recheckFrames no longer " +
                             "resolves. Both arms below are asserting about a shape the build no longer has.";
                yield break;
            }

            // ── arm (a): EXECUTED. Pure — the property reads one int field and nothing else, so it answers
            // outside a Unity runtime. Restored afterwards; the harness shares this static with nothing.
            object prior = null;
            string threw = null;
            bool onWhilePending = false, offWhenDone = false;
            try
            {
                prior = frames.GetValue(null);
                frames.SetValue(null, 1);
                onWhilePending = (bool)pending.Invoke(null, null);
                frames.SetValue(null, 0);
                offWhenDone = !(bool)pending.Invoke(null, null);
            }
            catch (Exception e) { threw = (e.InnerException ?? e).GetType().Name; }
            finally { try { if (prior != null) frames.SetValue(null, prior); } catch { } }

            if (threw != null)
                yield return "L349 verdict-is-not-the-countdown: reading ReplenishSync.ResupplyVerdictPending " +
                             "threw " + threw + " outside a Unity runtime. It must stay a pure read of " +
                             "_recheckFrames — EventPopup.CanCarryWindow asks it on every drain tick, and " +
                             "anything heavier there is paid once per frame per held window.";
            else if (!onWhilePending || !offWhenDone)
                yield return "L349 verdict-is-not-the-countdown: with _recheckFrames=1 the property said " +
                             onWhilePending + " and with 0 it said " + (!offWhenDone) + " — it must be the " +
                             "countdown itself, true while frames remain and false at zero. A constant false " +
                             "passes arm (b) while gating nothing (the window race returns, silently); a " +
                             "constant true holds every mirrored window of the session forever, which is the " +
                             "unbounded wait the no-quorum rule forbids. The bound is the countdown.";

            // ── arm (b), over the real seam.
            foreach (var v in Scan(popup, "EventPopup")) yield return v;

            // ── arm (c): the must-reach walk above must be able to say NO.
            var control = Scan(typeof(FakeSeam), "FakeSeam").ToList();
            if (!control.Any(c => c.Contains("held-window-outruns-the-verdict")))
                yield return "L349 control-not-red: FakeSeam's gate asks about nothing but the view — the " +
                             "shape that shipped before 2026-08-09 — and the scan did not flag it. A " +
                             "must-reach arm whose IL walk resolves no call edges reports every gate as " +
                             "present, so the green above would mean nothing.";
        }

        private static IEnumerable<string> Scan(Type seam, string label)
        {
            var gate = seam.GetMethod("CanCarryWindow", AllMembers);
            var drain = seam.GetMethod("DrainHeldRaises", AllMembers);
            var raise = seam.GetMethod("RaiseMirrored", AllMembers);
            if (gate == null || drain == null || raise == null)
            {
                yield return "L349 held-window-outruns-the-verdict: " + label + " has no " +
                             "CanCarryWindow/DrainHeldRaises/RaiseMirrored trio at all.";
                yield break;
            }

            bool gated = Reaches(gate, "get_ResupplyVerdictPending");
            bool drainUses = Reaches(drain, "CanCarryWindow");
            bool raiseUses = Reaches(raise, "CanCarryWindow");
            if (!gated || !drainUses || !raiseUses)
                yield return "L349 held-window-outruns-the-verdict: " + label + ".CanCarryWindow reaches the " +
                             "resupply verdict=" + gated + ", DrainHeldRaises routes through the gate=" +
                             drainUses + ", RaiseMirrored routes through it=" + raiseUses + ". A returning " +
                             "peer's resupply screen is queued a beat AFTER it reaches the geoscape, and rank " +
                             "20 cannot preempt a window that is already current " +
                             "(ProcessQueriedStateSwitch:58-63 dequeues only while _currentStateSwitchRequest " +
                             "is null) — so a mirrored event released in that beat owns the screen and the " +
                             "resupply screen never appears. One gate, both paths: a check inlined into the " +
                             "drain alone leaves the live-raise path racing exactly as before.";
        }

        /// <summary>ARM (c). Never called — it exists only to be walked. The gate as it stood before
        /// 2026-08-09: readiness of the view and nothing about what is still being decided.</summary>
        private sealed class FakeSeam
        {
            internal static bool CanCarryWindow(object geo) => geo != null;
            internal static void DrainHeldRaises() { if (CanCarryWindow(null)) RaiseMirrored(); }
            internal static void RaiseMirrored() { if (!CanCarryWindow(null)) return; }
        }

        // ─── IL helpers (same primitives as L153/L158/L182/L250/L253/L344/L348; Program.cs is not partial) ──

        private static bool Reaches(MethodBase caller, string calleeName)
            => CalleesOf(caller).Any(c => c.Name == calleeName);

        private static IEnumerable<MethodBase> CalleesOf(MethodBase caller)
        {
            foreach (var tok in TokensAfter(caller, 0x28, 0x6F))   // call / callvirt
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
