using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Network;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L153 — THE HOST↔CLIENT CLOCK PHASE ERROR HAS A SEAM THAT CAN READ IT, AND THAT SEAM WRITES NOTHING.
    ///
    /// THE REPORT (3 instances on one machine, 2026-08-04 23:22, and every session since): on the geoscape
    /// the client's DERIVED quantities — an aircraft's position, a site's exploration fill — trail the host
    /// by a visible, steady amount, while the events and windows that ride the order channel arrive at once.
    /// Ten attempts have been made at it and the reason they all missed is written in the code they were
    /// aimed at: <c>TimeAnchor</c>:76-99. The error is INVISIBLE. <c>TimeAnchor.EnforceDrift</c> compares the
    /// client's clock against the client's OWN anchor derivation, never against the host's clock, so both
    /// peers agree with the anchor while disagreeing with each other — and all three peer logs of that
    /// session contain not one <c>TimeAnchor</c> line while the bias was present throughout. A quantity
    /// nobody has ever read has been fixed ten times.
    ///
    /// WHAT THIS LAW IS FOR, then: not the bias — the SEAM. The phase error is a RUNTIME quantity that only
    /// exists when two peers are connected and running a geoscape, and no static check can see it, exactly
    /// as <c>L100</c>'s header says of the rail's real latency. What CAN be pinned is the static belt that
    /// makes the runtime number obtainable at all, and that belt is what got deleted, refactored and
    /// rationalised away across those ten attempts. The corpus had no law here: L100 pins the CADENCE and
    /// the compensation TERM, L73 pins the churn inequality, and neither says anything about the client's
    /// derived time being at the right ABSOLUTE value — which is why this bug survived a green harness.
    ///
    /// RUNTIME-ONLY, STATED PLAINLY so no reader mistakes a green L153 for a correct clock. This law does
    /// NOT assert that the clocks agree, that the bias is small, that it is bounded, or that it converges.
    /// It asserts that if you set <c>MULTIPLAYER_DIAG</c> and run two peers, a number comes out. Reading it
    /// is still a human's job, and <c>ClockPhaseDiag</c>'s own header lists what that number cannot see
    /// (chiefly: it includes the probe's own one-way flight and cannot subtract it).
    ///
    /// THE ARMS:
    ///   (a) <c>seam-unwired</c> — <c>ClockPhaseDiag.HostTick</c> must be reached from <c>SyncEngine.Tick</c>
    ///       and <c>ClockPhaseDiag.HandleInbound</c> from <c>SyncEngine</c>'s inbound chain. A diagnostic
    ///       nothing calls is the same as no diagnostic, and it fails silently — this repo's dominant bug
    ///       class, and the one that produced the report above.
    ///   (b) <c>diagnostic-not-gated</c> — <c>HostTick</c> must read <c>MpDiag.On</c>. Off by default is not
    ///       a preference: an ungated per-second broadcast is traffic inside the very measurement it takes,
    ///       and MpDiag's own header records what an always-on trace family already cost this repo.
    ///   (c) <c>diagnostic-writes-the-clock</c> — no method of <c>ClockPhaseDiag</c> may call a
    ///       <c>Timing</c> setter, <c>Timing.ProcessInstanceData</c>, or <c>TimeAnchor.Rebase</c>. A
    ///       measurement that perturbs the clock is an eleventh failed fix wearing a diagnostic's clothes.
    ///   (d) <c>probe-id-collides</c> — the 0xC0 id must be unique across <c>SurfaceIds</c>. It sits outside
    ///       the geoscape band because that band is fully allocated; a collision would route a diagnostic
    ///       into a live family's handler.
    ///   (e) POSITIVE CONTROL, EXECUTED — the printed line must actually TELL THE THREE SHAPES APART. This
    ///       is the arm that stops the law from being a wiring check: <c>PhaseLine</c> is run over fabricated
    ///       readings for a constant REAL offset at two different speeds (dReal must read the SAME), for an
    ///       offset that scales with speed (dReal must read DIFFERENT), and for a paused clock (no division
    ///       by zero). A vacuous sweep is possible here — a formatter that printed zeros, or dropped the
    ///       rate, would pass every other arm — so a non-zero offset is asserted never to print as zero.
    ///
    /// Falsify: delete the <c>ClockPhaseDiag.HostTick</c> call from <c>SyncEngine.Tick</c> or drop it from
    /// the inbound chain → (a); remove the <c>MpDiag.On</c> guard → (b); make the probe re-anchor the clock
    /// it measures → (c); mint 0xC0 for a second family → (d); price <c>dReal</c> at a constant instead of
    /// the live rate, or print <c>dGame</c> only → (e).
    /// </summary>
    internal static class L153_ClockPhaseIsMeasurable
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check()
        {
            var diag = typeof(ClockPhaseDiag);
            var hostTick = diag.GetMethod("HostTick", AllMembers);
            var inbound = diag.GetMethod("HandleInbound", AllMembers);
            var phaseLine = diag.GetMethod("PhaseLine", AllMembers);
            var engineTick = typeof(SyncEngine).GetMethod("Tick", AllMembers);
            var onGetter = typeof(MpDiag).GetProperty("On", AllMembers)?.GetGetMethod(true);
            var probeId = typeof(SurfaceIds).GetField("ClockPhaseProbe", AllMembers);

            if (hostTick == null || inbound == null || phaseLine == null || engineTick == null ||
                onGetter == null || probeId == null)
            {
                yield return "L153 premise-changed: ClockPhaseDiag.{HostTick,HandleInbound,PhaseLine} / " +
                             "SyncEngine.Tick / MpDiag.On / SurfaceIds.ClockPhaseProbe did not all resolve. The " +
                             "clock-phase seam has moved or been removed, and this law is asserting about a shape " +
                             "it no longer has — which is exactly how the phase error went unmeasured for ten " +
                             "fix attempts.";
                yield break;
            }

            // ── arm (a): the seam is reached from the one tick and the one inbound chain.
            if (!CallsMethod(engineTick, hostTick))
                yield return "L153 seam-unwired: SyncEngine.Tick does not call ClockPhaseDiag.HostTick, so the " +
                             "host never ships its clock reading and the client has nothing to compare against. " +
                             "The phase error goes back to being invisible — EnforceDrift checks the client " +
                             "against its own derivation, so no other line in any log names this quantity.";
            if (!TypeCallsMethod(typeof(SyncEngine), inbound))
                yield return "L153 seam-unwired: nothing in SyncEngine reaches ClockPhaseDiag.HandleInbound, so " +
                             "surface 0xC0 is unclaimed on the client: the probe falls through the whole inbound " +
                             "chain and is dropped by SurfaceRouter as a foreign envelope, silently.";

            // ── arm (b): off by default, and gated where the cost is, not at the log line.
            if (!CallsMethod(hostTick, onGetter))
                yield return "L153 diagnostic-not-gated: ClockPhaseDiag.HostTick does not read MpDiag.On. A " +
                             "per-second broadcast every session pays for is traffic inside the measurement it " +
                             "is taking, and MpDiag's own header records what an always-on trace family already " +
                             "cost here (23642 lines in one live run).";

            // ── arm (c): the measurement may not move what it measures.
            foreach (var v in WritesNothing(diag)) yield return v;

            // ── arm (d): the id is the diagnostic's alone.
            byte id = Convert.ToByte(probeId.GetRawConstantValue());
            foreach (var f in typeof(SurfaceIds).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.Name == probeId.Name || f.FieldType != typeof(byte) || !f.IsLiteral) continue;
                if (Convert.ToByte(f.GetRawConstantValue()) == id)
                    yield return "L153 probe-id-collides: SurfaceIds.ClockPhaseProbe (0x" + id.ToString("X2") +
                                 ") is also SurfaceIds." + f.Name + ". The geoscape band 0xA0-0xBF is fully " +
                                 "allocated, which is why the probe took a band of its own; a collision routes a " +
                                 "diagnostic payload into a live family's handler.";
            }

            // ── arm (e), EXECUTED.
            foreach (var v in PositiveControl(phaseLine)) yield return v;
        }

        /// <summary>ARM (c). Sweeps every method ClockPhaseDiag declares and refuses any call that could
        /// change the clock: a <c>Timing</c> property setter, <c>ProcessInstanceData</c> (the game's own
        /// save-load seam the anchor writes through), or <c>TimeAnchor.Rebase</c>. Getters are untouched —
        /// reading <c>Now</c> and <c>EffectiveScale</c> is the whole job.</summary>
        private static IEnumerable<string> WritesNothing(Type diag)
        {
            foreach (var m in diag.GetMethods(AllMembers))
            {
                foreach (var callee in CalleesOf(m))
                {
                    bool onTiming = callee.DeclaringType != null && callee.DeclaringType.FullName == "Base.Core.Timing";
                    bool isWrite = onTiming && (callee.Name.StartsWith("set_", StringComparison.Ordinal) ||
                                                callee.Name == "ProcessInstanceData");
                    bool isRebase = callee.DeclaringType == typeof(TimeAnchor) && callee.Name == "Rebase";
                    if (isWrite || isRebase)
                        yield return "L153 diagnostic-writes-the-clock: ClockPhaseDiag." + m.Name + " calls " +
                                     callee.DeclaringType.Name + "." + callee.Name + ". This surface exists to " +
                                     "MEASURE the phase error; a probe that re-anchors, re-rates or re-bases the " +
                                     "clock is an eleventh compensation layer, and the reading it prints is then a " +
                                     "reading of its own writes.";
                }
            }
        }

        /// <summary>ARM (e), the POSITIVE CONTROL. The three candidate explanations must be legible in the
        /// one printed line, or the whole surface is a wiring exercise. Fabricated readings only — no game,
        /// no session, no clock.</summary>
        private static IEnumerable<string> PositiveControl(MethodInfo phaseLine)
        {
            string flat1 = null, flat6 = null, scaled = null, paused = null;
            string failed = null;   // C# forbids yield inside catch, so the message crosses the block as a local
            try
            {
                // Shape (a): a CONSTANT 0.45 s REAL offset, seen at speed 1 and at speed 6. In game seconds
                // it is 0.45 and 2.70 — different numbers, the SAME dReal. That equality is what identifies
                // the shape, so it is the thing asserted.
                flat1 = (string)phaseLine.Invoke(null, new object[] { 1000.0, 999.550, 1.0, 5.0, 5.0 });
                flat6 = (string)phaseLine.Invoke(null, new object[] { 1000.0, 997.300, 6.0, 5.0, 5.0 });
                // Shape (b): the same 2.70 game seconds behind, but at speed 1 — a REAL offset six times
                // larger. It must not read like (a).
                scaled = (string)phaseLine.Invoke(null, new object[] { 1000.0, 997.300, 1.0, 5.0, 5.0 });
                // A paused clock has no rate to divide by.
                paused = (string)phaseLine.Invoke(null, new object[] { 1000.0, 1000.000, 0.0, 5.0, 5.0 });
            }
            catch (Exception e) { failed = (e.InnerException ?? e).Message; }

            if (failed != null)
            {
                yield return "L153 premise-changed: ClockPhaseDiag.PhaseLine could not be executed (" +
                             failed + "), so arm (e) — the only executed evidence " +
                             "this law has — asserts nothing.";
                yield break;
            }

            foreach (var line in new[] { flat1, flat6, scaled, paused })
                if (line == null || !line.StartsWith("[MP][clockphase]", StringComparison.Ordinal) ||
                    line.IndexOf("sinceLatch=", StringComparison.Ordinal) < 0)
                    yield return "L153 line-not-greppable: a sample line is '" + line + "'. The prefix is the " +
                                 "whole interface of this diagnostic — one stable token to grep across three peer " +
                                 "logs — and sinceLatch is what separates an error that ACCUMULATES between " +
                                 "latches from one that is merely constant.";

            if (flat1.IndexOf("dGame=-0.450", StringComparison.Ordinal) < 0 ||
                flat1.IndexOf("dReal=-0.450", StringComparison.Ordinal) < 0)
                yield return "L153 phase-arithmetic-wrong: 0.45 game seconds behind at rate 1 printed as '" +
                             flat1 + "'; expected dGame=-0.450 and dReal=-0.450. Sign convention: negative is " +
                             "CLIENT BEHIND, which is the reported symptom.";

            if (flat6.IndexOf("dGame=-2.700", StringComparison.Ordinal) < 0 ||
                flat6.IndexOf("dReal=-0.450", StringComparison.Ordinal) < 0)
                yield return "L153 constant-offset-not-identifiable: the SAME 0.45 s real-time offset seen at " +
                             "speed 6 printed as '" + flat6 + "'; expected dGame=-2.700 with dReal=-0.450. A " +
                             "constant real offset is recognised by dReal staying FLAT while dGame tracks the " +
                             "speed — divide by the wrong thing and shape (a) becomes indistinguishable from " +
                             "shape (b), which is the distinction this whole surface was built to make.";

            if (scaled.IndexOf("dReal=-2.700", StringComparison.Ordinal) < 0)
                yield return "L153 scaling-offset-not-identifiable: 2.70 game seconds behind at rate 1 printed " +
                             "as '" + scaled + "'; expected dReal=-2.700. This is shape (b) — an offset that " +
                             "grows with the speed setting — and it is told from shape (a) only by dReal " +
                             "differing between two samples at different speeds.";

            if (paused.IndexOf("dReal=paused", StringComparison.Ordinal) < 0)
                yield return "L153 paused-rate-divided: a rate-0 sample printed as '" + paused + "'; expected " +
                             "dReal=paused. There is no real-time equivalent of a game-second offset on a clock " +
                             "that is not advancing, and an Infinity/NaN in the column the developer greps is a " +
                             "second bug on top of the one being chased.";

            // Non-vacuity: a formatter that printed zeros would satisfy a laxer reading of every arm above.
            if (flat1.IndexOf("dGame=0.000", StringComparison.Ordinal) >= 0 ||
                flat1.IndexOf("scale=", StringComparison.Ordinal) < 0)
                yield return "L153 sweep-is-vacuous: a real, non-zero offset printed as zero, or the line " +
                             "carries no rate at all ('" + flat1 + "'). Either way the log cannot distinguish " +
                             "any of the three shapes and a green L153 would mean nothing.";
        }

        private static bool TypeCallsMethod(Type owner, MethodBase target)
        {
            foreach (var m in owner.GetMethods(AllMembers))
                if (CallsMethod(m, target)) return true;
            foreach (var c in owner.GetConstructors(AllMembers))
                if (CallsMethod(c, target)) return true;
            // Lambdas (the inbound chain is one) compile into nested display classes, so the call is not in
            // the constructor's own IL at all.
            foreach (var n in owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                if (TypeCallsMethod(n, target)) return true;
            return false;
        }

        private static bool CallsMethod(MethodBase caller, MethodBase target)
        {
            foreach (var callee in CalleesOf(caller))
                if (callee.MetadataToken == target.MetadataToken && callee.Module == target.Module) return true;
            return false;
        }

        private static IEnumerable<MethodBase> CalleesOf(MethodBase caller)
        {
            foreach (var tok in TokensAfter(caller, 0x28, 0x6F))   // call / callvirt
            {
                MethodBase c = null;
                try { c = caller.Module.ResolveMethod(tok); } catch { }
                if (c != null) yield return c;
            }
        }

        private static bool ReadsStaticField(MethodBase caller, FieldInfo target)
        {
            foreach (var tok in TokensAfter(caller, 0x7E))         // ldsfld
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
