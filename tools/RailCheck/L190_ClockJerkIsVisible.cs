using System;
using System.Collections.Generic;
using System.Reflection;
using Base.Core;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L190 — A RATE CHANGE IS NOT DRIFT, AND A CLOCK JERK IS VISIBLE ON THE PEER IT JERKS.
    ///
    /// THE REPORT (host log, co-op session 2026-08-07). Two ERRORs, the only ones this class produced in
    /// sixteen minutes, at 23:10:09.971 and 23:10:50.724: <c>"5 DRIFT re-latches in 10 s (threshold 4) — the
    /// drift prediction does not match the host clock, so every client's level clock is being jerked to a
    /// fresh anchor on almost every walk cycle"</c>. Both were FALSE, and the client — the peer the sentence
    /// accuses of being jerked — carried no <c>TimeAnchor</c> line whatsoever, so nothing in the corpus could
    /// confirm or deny the accusation. Two failures, one law.
    ///
    /// ARM A — the alarm was counting the user's own speed clicks. <c>TimeAnchor.Drifted</c> predicts the
    /// host clock by extrapolating the WHOLE interval since the last latch at the rate it reads NOW, which is
    /// sound only while that rate has not changed. On the single walk cycle a pause, resume or speed click
    /// lands, the rate is one the clock never ran at and the predicate is guaranteed over threshold in both
    /// directions: a RESUME after a P-second pause reads an error of <c>rate x P</c> against a
    /// <c>rate x 0.5</c> tolerance (true for any pause past half a second), and a PAUSE reads rate 0, which
    /// collapses the tolerance to its 5 GAME-second floor while the error is the entire running stretch since
    /// the latch. That floor is nothing at geoscape speed: the same log clocks 8 game-hours between mist hour
    /// 254 and 262 over realtime 411.057-421.059, i.e. ~2880 game-s per real-s, so 5 game seconds is 1.7 ms
    /// of wall clock. So EVERY rate change answered DRIFT. The host log matches one-for-one: the second alarm's window (realtime
    /// 401.5-411.5) holds exactly five <c>[MP][pause]</c> transitions — 402.264, 402.740, 408.488, 409.409,
    /// 409.817 — a vehicle-order storm, while the other ~15 minutes of geoscape are silent. Counting
    /// gestures, not a model disagreeing with its clock.
    ///
    /// The REGRESSION this repairs is a4a368a, which introduced the <c>driftLatch</c> flag for precisely this
    /// exclusion — <c>ChurnCheck</c>'s own words, "a pause/speed latch is a user gesture" — and did not get
    /// it, because the flag was computed by a rate-blind predicate hoisted above the very Paused/Scale
    /// comparison that would have told it. L73 arm E was landed by that same commit and stayed GREEN through
    /// the whole breakage: it asserts an INEQUALITY (the threshold sits below the latch ceiling), which says
    /// nothing about what gets counted. This law asserts the OUTCOME instead, and the guard now lives inside
    /// <c>Drifted</c> rather than in its caller so the predicate cannot be asked a question it has no basis
    /// to answer.
    ///
    /// ARM B — the peer that suffers logged nothing. <c>ApplyIfTouched</c> is where a client's level clock is
    /// actually moved (through <c>Rebase</c>), and it wrote no line, so ten fix attempts at the geoscape lag
    /// were aimed at a quantity no log ever carried (the sentence <c>TimeAnchor</c>:76-99 and
    /// <c>ClockPhaseDiag</c> both open with). A jerk must state its own size on the peer it jerks. This is
    /// deliberately an assertion about a LOG and not about a number: the offset is a runtime quantity and
    /// cannot be checked statically — the same boundary L153 draws for the phase probe.
    ///
    /// ARM C — the mandatory guard, and it is not ceremony. Arm B is worth nothing if <c>ApplyIfTouched</c>
    /// has stopped being the seam that moves the clock: a log line in a method that no longer calls
    /// <c>Rebase</c> is a green light on an empty road.
    ///
    /// NON-VACUITY: every subject must resolve or the law says so and asserts nothing else. Falsify: delete
    /// the <c>_hostDto.Paused</c>/<c>_hostDto.Scale</c> guard from <c>Drifted</c> (or hoist the predicate back
    /// above the comparison, which is the same thing) → <c>drift-rate-blind</c>; delete the
    /// <c>[Multiplayer][rail] TimeAnchor: anchor apply moved...</c> line → <c>jerk-silent</c>; make
    /// <c>ApplyIfTouched</c> stop calling <c>Rebase</c> → <c>premise-changed</c>.
    /// </summary>
    internal static class L190_ClockJerkIsVisible
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check()
        {
            var anchor = typeof(TimeAnchor);
            var drifted = anchor.GetMethod("Drifted", AllMembers);
            var apply = anchor.GetMethod("ApplyIfTouched", AllMembers);
            var rebase = anchor.GetMethod("Rebase", AllMembers);
            var pausedF = typeof(TimingInstanceData).GetField("Paused");
            var scaleF = typeof(TimingInstanceData).GetField("Scale");
            if (drifted == null || apply == null || rebase == null || pausedF == null || scaleF == null)
            {
                yield return "L190 unresolved: TimeAnchor.Drifted / ApplyIfTouched / Rebase or " +
                             "TimingInstanceData.Paused / Scale did not all resolve — neither the drift " +
                             "predicate's rate guard nor the client's jerk report is checked by anything";
                yield break;
            }

            // ── arm A: the drift predicate must consult the LATCHED rate before answering.
            if (!ReadsField(drifted, pausedF) || !ReadsField(drifted, scaleF))
                yield return "L190 drift-rate-blind: TimeAnchor.Drifted answers without comparing the anchor's own " +
                             "latched Paused/Scale against the live clock. Its prediction extrapolates the whole " +
                             "interval since the latch at the rate read NOW, so on the one cycle a pause, resume or " +
                             "speed click lands it is measuring the clock against a rate the clock never ran at — " +
                             "guaranteed over threshold both ways (a resume after a P-second pause reads rate x P " +
                             "against a rate x 0.5 tolerance; a pause reads rate 0, collapsing the tolerance to its " +
                             "5 game-second floor against the whole running stretch). Every rate change then reports " +
                             "as DRIFT and the churn alarm confesses the user's own speed clicks: host log " +
                             "2026-08-07 23:10:09.971 and 23:10:50.724, five 'DRIFT re-latches' that are one-for-one " +
                             "the five [MP][pause] transitions of a vehicle-order storm. a4a368a added the driftLatch " +
                             "flag to exclude exactly this and missed it here; L73's inequality stayed green throughout";

            // ── arm B: the peer whose clock is moved has to say so, with the size.
            if (!LogsSomething(apply))
                yield return "L190 jerk-silent: TimeAnchor.ApplyIfTouched moves this peer's level clock and writes no " +
                             "log line. That is the blindness the whole clock-phase investigation is stuck behind — " +
                             "the host can print 'every client's level clock is being jerked' while every client log " +
                             "carries not one TimeAnchor line, so a player reporting geoscape stutter produces zero " +
                             "evidence on the peer that stuttered (2026-08-07, both alarms; and all three peer logs " +
                             "of 2026-08-04 23:22 before it). The offset itself is a runtime quantity and cannot be " +
                             "asserted here — L153 draws the same boundary — but the LINE can be, and without it the " +
                             "next session is as undecidable as the last ten";

            // ── arm C: ...and that method must still be the one that moves it.
            if (!CallsMethod(apply, rebase))
                yield return "L190 premise-changed: TimeAnchor.ApplyIfTouched no longer calls Rebase, so it is no " +
                             "longer the seam where a client's level clock is jerked and arm B is guarding an empty " +
                             "road — find where the anchor is applied now and move this law's subject there before " +
                             "trusting its green";
        }

        /// <summary>Does this method's IL load the given INSTANCE field? Same naive linear token scan L100
        /// uses (a byte inside an operand could in principle be mistaken for an opcode), which is sound
        /// enough here because a false positive would have to resolve to this exact field token.</summary>
        private static bool ReadsField(MethodBase m, FieldInfo target)
        {
            foreach (var tok in TokensAfter(m, 0x7B, 0x7C))   // ldfld / ldflda
            {
                FieldInfo f = null;
                try { f = m.Module.ResolveField(tok); } catch { }
                if (f != null && f.MetadataToken == target.MetadataToken && f.Module == target.Module) return true;
            }
            return false;
        }

        private static bool CallsMethod(MethodBase caller, MethodBase target)
        {
            foreach (var tok in TokensAfter(caller, 0x28, 0x6F))   // call / callvirt
            {
                MethodBase c = null;
                try { c = caller.Module.ResolveMethod(tok); } catch { }
                if (c != null && c.MetadataToken == target.MetadataToken && c.Module == target.Module) return true;
            }
            return false;
        }

        /// <summary>Any UnityEngine.Debug log call at all. Asked of the CLASS rather than of one method name
        /// so that raising the line's severity — Log to LogWarning once a measurement finally calibrates what
        /// size of jump is pathological — is not a law failure.</summary>
        private static bool LogsSomething(MethodBase m)
        {
            foreach (var tok in TokensAfter(m, 0x28, 0x6F))
            {
                MethodBase c = null;
                try { c = m.Module.ResolveMethod(tok); } catch { }
                if (c != null && c.DeclaringType == typeof(Multiplayer.MpLog) &&
                    c.Name.StartsWith("Log", StringComparison.Ordinal)) return true;
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
