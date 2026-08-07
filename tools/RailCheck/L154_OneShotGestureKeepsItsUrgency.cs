using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Network;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L154 — THE DEPARTURE AND EXPLORATION-START SEAMS ARE WIRED TO THE URGENT FLUSH, AND A FLUSH THAT
    /// LANDS MID-CYCLE IS STILL SERVED URGENTLY.
    ///
    /// THE REPORT (3-instance sessions, the same family as L153's: 2026-08-04 23:22 and every session
    /// since): on the geoscape an aircraft's FLIGHT and a site's exploration SPINNER trail the host by a
    /// steady ~0.25-0.5 s on every client, while gesture-driven things — windows, orders, tactical commands —
    /// are instant. Both trail by the SAME amount, which is the tell: the two quantities are closed-form
    /// functions of (start time, duration) that the client re-derives locally
    /// (<c>GeoNavComponent.NavigateRoutine</c>'s <c>Ratio01</c>, and
    /// <c>GeoActorProgressionVisualController.Progression</c> — <c>GenericApplier</c>:1059 names them the
    /// same shape), so both are anchored on when the START reached this peer, not on what it says.
    ///
    /// WHAT WAS ACTUALLY MISSING, and it is NOT a second flush. Both seams already flush: the host's own
    /// <c>GeoVehicle.StartTravel</c> and <c>GeoVehicle.StartExploringCurrentSite</c> run through
    /// <c>VehicleSync</c>'s capture prefixes, whose first line is <c>IntentRail.ShouldRunNative()</c>, whose
    /// first line is <c>DiffEngine.FlushOnHostGesture()</c> (N3's third arm). The hole was on the other side
    /// of the flag. A flush that lands MID-cycle cannot be served by that cycle if the walk already passed
    /// the root that changed — <c>BeginCycle</c> snapshots the root list and <c>_cycleNext</c> only moves
    /// forward — so it ships from the NEXT cycle; and <c>RunSlice</c> cleared <c>_cycleUrgent</c> at
    /// completion, leaving that next cycle to walk 625 roots at the ordinary <c>SliceBudgetMs</c> ≈ 0.25 s.
    /// A ONE-SHOT gesture (an aircraft departing, an exploration starting) has no second flush to re-arm the
    /// flag; the gesture the 141 ms figure came from does — an equip drag re-enters the seam 6-7 frames in a
    /// row — which is exactly why the two classes measured differently and why ten fixes aimed elsewhere.
    ///
    /// RUNTIME-ONLY, STATED PLAINLY so no reader mistakes a green L154 for a synchronised geoscape. This law
    /// does NOT assert that the flight and the spinner agree between peers, that the residual is small, or
    /// that it is bounded. It cannot: the residual is one urgent walk plus one wire crossing, both runtime
    /// quantities, and L153's <c>ClockPhaseDiag</c> is the seam that reads them. Worse, this law CANNOT see
    /// the navigation half's own anchoring defect — <c>GenericApplier.ReseedNavigation</c> hands
    /// <c>Navigation.Navigate(path)</c> no start time, so the client's leg is anchored at DELTA-ARRIVAL,
    /// permanently late by whatever the delivery cost (that method's own comment at :1244 admits it), while
    /// <c>ReseedExploration</c> passes the MIRRORED <c>start</c> and is anchored correctly. Reducing the
    /// latency shrinks the flight's trail proportionally; it does not remove it, and no static arm can say so.
    ///
    /// SCOPE, NAMED SO THE THIRD CASE IS NOT MISTAKEN FOR COVERED. The haven-attack contest — the attacker
    /// and defender bars racing each other — LOOKS like the same symptom and is NOT the same mechanism, so
    /// this law does not reach it and neither does the flush above. Those bars are not a closed-form
    /// derivation off a mirrored start at all: <c>GeoHavenDefenseMissionInstanceData</c> carries
    /// <c>AttackerDeployment</c> / <c>DefenderDeployment</c> as <c>Int32</c> leaves plus
    /// <c>WaitTimeLeft</c> (<c>docs/rail-baseline.txt</c>:264-275), i.e. ACCUMULATED values that ride the
    /// rail and therefore STEP at the walk cadence rather than running late-but-smooth. They also change
    /// from the SIM, which has no gesture seam to hang a flush on — the class <c>DiffEngine</c>:43-58 names
    /// out loud and deliberately leaves to the poll. Making that contest prompt is a different fix (either
    /// P15 it — derive the bars from a mirrored start and rate — or give the sim writer its own N3 seam),
    /// and it is stated here rather than silently folded in.
    ///
    /// THE ARMS:
    ///   (a) <c>seam-unflushed</c> — <c>VehicleSync.CaptureTravel</c> and <c>VehicleSync.CaptureExplore</c>
    ///       must each reach <c>IntentRail.ShouldRunNative</c>, and <c>ShouldRunNative</c> must reach
    ///       <c>DiffEngine.FlushOnHostGesture</c>. This is the arm that stops the fix being refactored away:
    ///       a capture seam rewritten to test <c>engine.IsHost</c> for itself would still be CORRECT and
    ///       would silently drop the host's flush, which is precisely the shape of the previous regressions.
    ///   (b) <c>flush-guard-wrong</c> — <c>FlushOnHostGesture</c> must read <c>NetworkEngine.IsHost</c>,
    ///       <c>NetworkEngine.IsActiveSession</c> and <c>SyncApplyScope.Active</c>. Guarded THERE and not at
    ///       the call sites, on purpose: dropping any one of the three makes a CLIENT flush, or makes the
    ///       host flush while applying (host repaints re-enter capture seams under the scope — those are not
    ///       gestures), and either is a new bug wearing the fix's clothes.
    ///   (c) <c>urgency-not-handed-over</c> — <c>DiffEngine.FlushNow</c> must WRITE <c>_urgentPending</c> and
    ///       <c>RunSlice</c> must READ it. This is the whole hand-off; delete either half and the one-shot
    ///       gesture goes back to being served by an ordinary-budget cycle, invisibly and with every other
    ///       arm of this law still green.
    ///   (d) POSITIVE CONTROL, EXECUTED — <c>DiffEngine.SliceBudget</c> is run over its three cases. A
    ///       vacuous sweep is possible here and is the likeliest silent regression: a refactor that collapsed
    ///       the two budgets to one value, or that stopped consulting the urgent flag, would satisfy (a)-(c)
    ///       while making them all pointless. So the urgent budget is asserted to be STRICTLY LARGER than the
    ///       ordinary one, and the drag exemption asserted to still fall back to the ordinary one.
    ///
    /// Falsify: inline a bespoke host test in either capture seam in place of <c>ShouldRunNative</c>, or drop
    /// the <c>FlushOnHostGesture</c> line from <c>ShouldRunNative</c> → (a); relax any clause of the
    /// host-gesture guard → (b); restore <c>_cycleUrgent = false</c> at cycle completion → (c); set
    /// <c>UrgentSliceBudgetMs</c> equal to <c>SliceBudgetMs</c>, or ignore either argument → (d).
    /// </summary>
    internal static class L154_OneShotGestureKeepsItsUrgency
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check()
        {
            var vehicleSync = typeof(VehicleSync);
            var captureTravel = vehicleSync.GetMethod("CaptureTravel", AllMembers);
            var captureExplore = vehicleSync.GetMethod("CaptureExplore", AllMembers);
            var shouldRunNative = typeof(IntentRail).GetMethod("ShouldRunNative", AllMembers);

            var diff = typeof(DiffEngine);
            var flushOnGesture = diff.GetMethod("FlushOnHostGesture", AllMembers);
            var flushNow = diff.GetMethod("FlushNow", AllMembers);
            var runSlice = diff.GetMethod("RunSlice", AllMembers);
            var sliceBudget = diff.GetMethod("SliceBudget", AllMembers);
            var urgentPending = diff.GetField("_urgentPending", AllMembers);

            var isHost = typeof(NetworkEngine).GetProperty("IsHost", AllMembers);
            var isActive = typeof(NetworkEngine).GetProperty("IsActiveSession", AllMembers);
            var applyActive = typeof(SyncApplyScope).GetProperty("Active", AllMembers);

            if (captureTravel == null || captureExplore == null || shouldRunNative == null ||
                flushOnGesture == null || flushNow == null || runSlice == null || sliceBudget == null ||
                urgentPending == null || isHost == null || isActive == null || applyActive == null)
            {
                yield return "L154 premise-changed: VehicleSync.{CaptureTravel,CaptureExplore} / " +
                             "IntentRail.ShouldRunNative / DiffEngine.{FlushOnHostGesture,FlushNow,RunSlice," +
                             "SliceBudget,_urgentPending} / NetworkEngine.{IsHost,IsActiveSession} / " +
                             "SyncApplyScope.Active did not all resolve. The departure/exploration-start " +
                             "flush path has moved or been removed, and this law is asserting about a shape " +
                             "it no longer has — which is how the geoscape lateness survived ten fixes and a " +
                             "green harness.";
                yield break;
            }

            // ── arm (a): both one-shot seams reach the shared host-gesture flush.
            foreach (var seam in new[] { captureTravel, captureExplore })
                if (!CallsMethod(seam, shouldRunNative))
                    yield return "L154 seam-unflushed: VehicleSync." + seam.Name + " does not call " +
                                 "IntentRail.ShouldRunNative, so the HOST's own departure/exploration start no " +
                                 "longer arms DiffEngine.FlushNow. The change then waits out the poll and the " +
                                 "client re-seeds its closed-form derivation from a start time that arrives a " +
                                 "whole walk late — the reported flight/spinner trail, exactly.";

            if (!CallsMethod(shouldRunNative, flushOnGesture))
                yield return "L154 seam-unflushed: IntentRail.ShouldRunNative does not call " +
                             "DiffEngine.FlushOnHostGesture. That one line is N3's third arm and the ONLY " +
                             "reason a host gesture ships this cycle instead of the next poll; every capture " +
                             "seam in the repo inherits it from here, so losing it is a repo-wide regression " +
                             "that no single family's tests would notice.";

            // ── arm (b): the guard is the host-gesture guard, all three clauses.
            foreach (var p in new[] { isHost, isActive, applyActive })
                if (!CallsMethod(flushOnGesture, p.GetGetMethod(true)))
                    yield return "L154 flush-guard-wrong: DiffEngine.FlushOnHostGesture does not read " +
                                 p.DeclaringType.Name + "." + p.Name + ". The three clauses are one guard: " +
                                 "without IsHost/IsActiveSession a CLIENT flushes a rail it does not drive, and " +
                                 "without !SyncApplyScope.Active the host flushes while APPLYING — host repaints " +
                                 "re-enter the capture seams under that scope and they are not gestures.";

            // ── arm (c): the mid-cycle hand-off exists on both sides.
            if (!TouchesStaticField(flushNow, urgentPending, 0x80))   // stsfld
                yield return "L154 urgency-not-handed-over: DiffEngine.FlushNow never writes _urgentPending, so " +
                             "a flush that lands mid-cycle is forgotten the moment that cycle completes. The " +
                             "next cycle — the one that actually carries the change, because the walk cursor had " +
                             "already passed the root — then walks ~625 roots at SliceBudgetMs, i.e. ~0.25 s. " +
                             "That is the measured lateness, and a ONE-SHOT gesture has no second flush to " +
                             "re-arm the flag the way an equip drag's 6-7 does.";
            if (!TouchesStaticField(runSlice, urgentPending, 0x7E))   // ldsfld
                yield return "L154 urgency-not-handed-over: DiffEngine.RunSlice never reads _urgentPending, so " +
                             "whatever FlushNow recorded about a mid-cycle arrival is written and dropped. " +
                             "Restoring a bare `_cycleUrgent = false` at cycle completion is exactly this " +
                             "failure, and it is invisible: every other arm of this law stays green.";

            // ── arm (d), EXECUTED.
            foreach (var v in PositiveControl(sliceBudget)) yield return v;
        }

        /// <summary>ARM (d), the POSITIVE CONTROL. The hand-off in (c) is worth nothing unless the two
        /// budgets are actually DIFFERENT and the urgent flag actually selects between them. Executed on the
        /// real method with fabricated inputs — no game, no session, no walk.</summary>
        private static IEnumerable<string> PositiveControl(MethodInfo sliceBudget)
        {
            double urgent = 0, urgentDragging = 0, ordinary = 0;
            string failed = null;   // C# forbids yield inside catch, so the message crosses the block as a local
            try
            {
                urgent         = (double)sliceBudget.Invoke(null, new object[] { true, false });
                urgentDragging = (double)sliceBudget.Invoke(null, new object[] { true, true });
                ordinary       = (double)sliceBudget.Invoke(null, new object[] { false, false });
            }
            catch (Exception e) { failed = (e.InnerException ?? e).Message; }

            if (failed != null)
            {
                yield return "L154 premise-changed: DiffEngine.SliceBudget could not be executed (" +
                             failed + "), so arm (d) — the only executed evidence " +
                             "this law has — asserts nothing.";
                yield break;
            }

            if (!(urgent > ordinary))
                yield return "L154 sweep-is-vacuous: SliceBudget(urgent) = " + urgent + " ms is not larger " +
                             "than SliceBudget(ordinary) = " + ordinary + " ms. With one budget the whole " +
                             "urgency apparatus — the flag, the mid-cycle hand-off, every flush arm above — " +
                             "buys nothing, and a green L154 would mean nothing. The gap IS the fix: ~625 " +
                             "roots take ~15 frames at 3 ms and ~6 at 8 ms.";

            if (urgentDragging != ordinary)
                yield return "L154 drag-floor-lost: SliceBudget(urgent, localInputInFlight) = " + urgentDragging +
                             " ms, expected the ordinary " + ordinary + " ms. The low floor was bought for " +
                             "exactly the local user's uncommitted drag; spending the urgent budget there pays " +
                             "frame time during the one interaction it exists to protect.";

            if (ordinary <= 0.0)
                yield return "L154 budget-degenerate: the ordinary budget reads " + ordinary + " ms. A " +
                             "non-positive budget makes RunSlice's ≥1-root floor the only thing bounding a " +
                             "cycle, which is a 625-frame walk — the opposite of what this law defends.";
        }

        private static bool CallsMethod(MethodBase caller, MethodBase target)
        {
            if (caller == null || target == null) return false;
            foreach (var tok in TokensAfter(caller, 0x28, 0x6F))   // call / callvirt
            {
                MethodBase c = null;
                try { c = caller.Module.ResolveMethod(tok); } catch { }
                if (c != null && c.MetadataToken == target.MetadataToken && c.Module == target.Module) return true;
            }
            return false;
        }

        /// <summary>ldsfld (0x7E) / stsfld (0x80) over one static field — the same IL-token read L153 uses,
        /// parameterised on the opcode because this law needs BOTH directions of one hand-off.</summary>
        private static bool TouchesStaticField(MethodBase caller, FieldInfo target, byte opcode)
        {
            foreach (var tok in TokensAfter(caller, opcode))
            {
                FieldInfo f = null;
                try { f = caller.Module.ResolveField(tok); } catch { }
                if (f != null && f.MetadataToken == target.MetadataToken && f.Module == target.Module) return true;
            }
            return false;
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
