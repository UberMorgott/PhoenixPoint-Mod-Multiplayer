using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L26 — A BLOCKING WINDOW ON ANY PEER HOLDS THE SHARED CLOCK, AND A ONE-SHOT WINDOW IS NEVER REPLAYED
    /// BY A REPAINT. Two symptoms of one live run (2026-08-04, 1 host + 2 clients), both silent, both about
    /// the game's own queued-window mechanism:
    ///
    /// (a) THE CLOCK. An aircraft was flying, an event and then a cinematic fired, the HOST paused — and both
    /// clients kept running, so the plane flew on while every player was reading. The mirrored window pushed
    /// itself with <c>PauseGame = false</c> and trusted the host's pause to arrive over the rail; it does not
    /// arrive when the host is ALREADY paused, because <c>Timing.Paused</c>'s setter is CHANGE-GATED
    /// (Timing.cs:112) — no event, no diff, no delta — while the client's own local pause was blocked by the
    /// block-first law. Nothing anywhere expressed "somebody else is still reading", so a resume was one
    /// boolean and the first peer to press play restarted the clock for everyone.
    ///
    /// (b) THE VIDEO. The same cinematic restarted 7 times on each client. <c>OpenUiRepaint</c>'s LAST-RESORT
    /// Exit+Enter fallback ran on <c>UIStateGeoCutscene</c>, and that is not a repaint: <c>ExitState</c>:76
    /// stops the video, <c>EnterState</c>:59 <c>Setup()</c>s it again → <c>PlayCutsceneOnFinishedLoad</c>:114
    /// → <c>Play()</c>. Every queued window is a ONE-SHOT PRESENTATION with the same hazard
    /// (<c>UIStateRosterDeployment</c> was re-entered mid-deployment in the same run).
    ///
    /// WHY A LAW. Both halves are the repo's dominant shape — a correct-looking fix that quietly stops being
    /// reached, with no log line. The arbiter in particular decides a REFUSAL, and a refusal that only ever
    /// runs in a live 3-instance session is how "the play button does nothing" ships.
    ///
    /// Falsify: make a release resume (<c>Paused = false</c> in <see cref="PauseHold.Decide"/>'s release arm)
    /// → <c>arbiter-auto-resumes</c>; drop the hold-count test from the resume arm → <c>arbiter-resume-free</c>;
    /// let <c>Decide</c> mutate its input set → <c>arbiter-impure</c>; delete op 3 from
    /// <c>TimeSync.RegisterIntents</c> → <c>hold-unregistered</c>; unpatch
    /// <c>ProcessQueriedStateSwitch</c> → <c>hold-edge-missing</c>; put <c>PauseGame = false</c> back on
    /// either mirrored raiser → <c>mirror-window-unpaused</c>; delete the
    /// <c>IsCurrentQueuedWindow</c> guard in <c>OpenUiRepaint.Repaint</c> → <c>oneshot-replayable</c>.
    /// </summary>
    internal static class L26_PauseAndOneShot
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check()
        {
            // ─── (a) THE ARBITER IS EXECUTED, NOT MERELY PRESENT ───
            // Called DIRECTLY (InternalsVisibleTo("RailCheck")), not by reflection: a renamed arbiter must
            // break the build here rather than turn the gate silently green.
            const byte opHold = PauseHold.OpHold;
            const byte opPause = TimeSync.OpPause;
            Func<IEnumerable<ulong>, ulong, byte, byte, PauseHold.Decision> run = PauseHold.Decide;
            Func<PauseHold.Decision, HashSet<ulong>> holds = d => d.Holds;
            Func<PauseHold.Decision, bool?> paused = d => d.Paused;
            Func<PauseHold.Decision, string> refusal = d => d.Refusal;

            var none = new HashSet<ulong>();

            // 1. A hold pauses and is recorded.
            var d1 = run(none, 7UL, opHold, 1);
            if (!holds(d1).Contains(7UL) || paused(d1) != true)
                yield return "L26 arbiter-hold-inert: a peer announcing a blocking window neither joined the " +
                             "hold set nor paused the clock — the whole point is that a window on ANY peer " +
                             "stops the shared campaign, so this is the free-running aircraft back verbatim";

            // 2. A release removes the holder and does NOT resume.
            var d2 = run(new HashSet<ulong> { 7UL }, 7UL, opHold, 0);
            if (holds(d2).Contains(7UL))
                yield return "L26 arbiter-release-stuck: a peer that dismissed its window still holds the " +
                             "clock, so the campaign can never be resumed again by anyone";
            if (paused(d2) != null)
                yield return "L26 arbiter-auto-resumes: the last release now writes the clock. The game itself " +
                             "never resumes when a window closes (vanilla leaves the geoscape paused and the " +
                             "player presses play), so this restarts time the instant ONE peer finishes " +
                             "reading — the exact behaviour the hold set exists to remove";

            // 3. A resume is refused while another peer still holds a window.
            var d3 = run(new HashSet<ulong> { 7UL }, 9UL, opPause, 0);
            if (refusal(d3) == null || paused(d3) != null)
                yield return "L26 arbiter-resume-free: a resume was granted while a blocking window is still " +
                             "open on another peer. Time then runs for everyone while one player is mid-" +
                             "popup, which is the co-op half of this bug and is invisible from that player's " +
                             "screen";

            // 4. With nobody holding, a resume is an ordinary resume.
            var d4 = run(none, 9UL, opPause, 0);
            if (refusal(d4) != null || paused(d4) != false)
                yield return "L26 arbiter-resume-blocked: a resume with an EMPTY hold set was refused — the " +
                             "veto has stopped being about windows and now just freezes the campaign";

            // 5. A pause is always granted (co-op parity; it is the safe direction).
            var d5 = run(new HashSet<ulong> { 7UL }, 9UL, opPause, 1);
            if (paused(d5) != true || refusal(d5) != null)
                yield return "L26 arbiter-pause-refused: a peer was refused a PAUSE. Any peer may stop the " +
                             "shared clock — refusing that direction can only ever lose time nobody wanted " +
                             "to spend";

            // 6. Two holders, one leaves: still held.
            var d6 = run(new HashSet<ulong> { 7UL, 9UL }, 7UL, opHold, 0);
            if (holds(d6).Count != 1 || refusal(run(holds(d6), 9UL, opPause, 0)) == null)
                yield return "L26 arbiter-partial-release: one peer dismissing its window released the clock " +
                             "for a set that still has another holder in it — 'dismissed everywhere' " +
                             "collapsed back to 'dismissed by the last person who clicked'";

            // 7. PURITY. The applier adopts Decision.Holds; a Decide that edited the caller's set in place
            //    would apply half a decision on the refusal path, where the caller returns early.
            var input = new HashSet<ulong> { 7UL };
            run(input, 9UL, opHold, 1);
            run(input, 7UL, opHold, 0);
            if (input.Count != 1 || !input.Contains(7UL))
                yield return "L26 arbiter-impure: Decide mutated the hold set it was given, so a REFUSED " +
                             "resume now leaves the host's set edited by a decision it did not apply — and " +
                             "the pure arm above stops proving anything about what the host really holds";

            // ─── (b) THE HOLD SEAM IS WIRED ───
            // Op 3 really is in the 0xB0 family table: registration is invoked and the engine's own family
            // map read back, so this is the live dispatch table and not a re-reading of the source.
            TimeSync.RegisterIntents();
            var families = typeof(IntentRail)
                .GetField("_families", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as System.Collections.IDictionary;
            object family = families?[SurfaceIds.GeoTimeIntent];
            var ops = family?.GetType().GetField("Ops", AllMembers)?.GetValue(family) as System.Collections.IDictionary;
            if (ops == null)
                yield return "L26 hold-unregistered: the 0xB0 time family has no op table after " +
                             "RegisterIntents ran — every time intent, hold included, would be answered " +
                             "with 'unknown op' and silently dropped";
            else if (!ops.Contains(opHold))
                yield return "L26 hold-unregistered: op " + opHold + " (the window hold) is not in the 0xB0 " +
                             "op table. A client announcing its blocking window is rejected as an unknown " +
                             "op, so the host never learns anybody is reading and every resume is granted";

            // The announce seam is the game's OWN single writer of the current queued window.
            var edge = typeof(PauseHold.QueueHoldEdge);
            var patch = edge.GetCustomAttributes(false).FirstOrDefault(a => a.GetType().Name == "HarmonyPatch");
            if (patch == null || edge.GetMethod("Postfix", AllMembers) == null)
                yield return "L26 hold-edge-missing: nothing patches GeoscapeViewSwitchQuery." +
                             "ProcessQueriedStateSwitch any more, so no peer ever announces that it has a " +
                             "window up. The hold set stays empty for ever and the arbiter above decides " +
                             "nothing in a real game";

            // PREMISE: the two game members the whole seam reads.
            if (typeof(GeoscapeViewSwitchQuery).GetField("_currentStateSwitchRequest", AllMembers) == null)
                yield return "L26 premise-changed: GeoscapeViewSwitchQuery._currentStateSwitchRequest is gone. " +
                             "Both the hold edge and the one-shot guard read that field to know which window " +
                             "the game is showing — with it renamed they bind null and fail OPEN, silently";
            if (typeof(GeoscapeViewStateSwitchRequest).GetField("PauseGame") == null)
                yield return "L26 premise-changed: GeoscapeViewStateSwitchRequest.PauseGame is gone — the " +
                             "game's own 'this window blocks the player' flag was the whole reason the hold " +
                             "seam needs no per-window-kind table";

            // ─── (c) THE MIRRORED WINDOW STILL BLOCKS ITS OWN PEER ───
            foreach (var v in MirrorPausesGame(typeof(EventPopup), "RaiseMirrored")) yield return v;
            foreach (var v in MirrorPausesGame(typeof(GeoModalMirror), "RaiseMirrored")) yield return v;

            // ─── (d) A ONE-SHOT WINDOW IS NEVER RE-ENTERED ───
            var repaintBody = typeof(OpenUiRepaint).GetMethod("Repaint", AllMembers);
            if (repaintBody == null)
                yield return "L26 oneshot-unguarded: OpenUiRepaint.Repaint is gone, so nothing was checked " +
                             "about the Exit+Enter fallback — the cinematic that restarted 7 times per client " +
                             "is unguarded again";
            else if (!Reaches(repaintBody, "PauseHold", "IsCurrentQueuedWindow"))
                yield return "L26 oneshot-replayable: the repaint fallback no longer asks whether the open " +
                             "screen is the queue's current window, so it will Exit+Enter one. That is not a " +
                             "repaint of a cinematic, it is a SECOND showing of it (UIStateGeoCutscene." +
                             "ExitState:76 Stop → EnterState:59 Setup → Play), and the same line resets a " +
                             "deployment screen out from under whoever is deploying";
            // The premise that makes the guard load-bearing: the fallback is still an Exit+Enter.
            else if (!Reaches(repaintBody, "GeoscapeViewState", "Exit") ||
                     !Reaches(repaintBody, "GeoscapeViewState", "Enter"))
                yield return "L26 premise-changed: OpenUiRepaint.Repaint no longer drives GeoscapeViewState." +
                             "Exit/Enter, so the guard above is protecting against something that is gone — " +
                             "whatever replaced it needs its own answer for one-shot windows";
        }

        /// <summary>A mirrored raise must push its window with the GAME'S own PauseGame flag set, or that
        /// peer has a blocking window up and no hold to show for it. Read off the IL: the raiser sets the
        /// field, and the only two values it can be given are the ldc.i4 0/1 immediately before the stfld.</summary>
        private static IEnumerable<string> MirrorPausesGame(Type raiser, string method)
        {
            var m = raiser.GetMethod(method, AllMembers);
            var field = typeof(GeoscapeViewStateSwitchRequest).GetField("PauseGame");
            if (m == null || field == null)
            {
                yield return "L26 mirror-window-unchecked: " + raiser.Name + "." + method + " (or the game's " +
                             "PauseGame field) is gone, so nothing proved a mirrored window blocks the peer " +
                             "it is shown to";
                yield break;
            }
            if (!StoresConstantOne(m, field))
                yield return "L26 mirror-window-unpaused: " + raiser.Name + " pushes its mirrored window with " +
                             "PauseGame = false. That peer then shows a blocking window while its own clock " +
                             "runs, and it does NOT recover from the host's pause: an already-paused host " +
                             "re-writing Timing.Paused is swallowed by the change-gated setter, so no delta " +
                             "is ever emitted and the aircraft keeps flying under the popup";
        }

        // ─── IL helpers (self-contained: this file adds no member to Program) ───

        private static readonly Dictionary<short, OpCode> OpCodeByValue = BuildOpCodes();

        private static Dictionary<short, OpCode> BuildOpCodes()
        {
            var map = new Dictionary<short, OpCode>();
            foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.FieldType == typeof(OpCode)) { var op = (OpCode)f.GetValue(null); map[op.Value] = op; }
            return map;
        }

        private static int OperandSize(OperandType t, byte[] il, int pos)
        {
            switch (t)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineBrTarget:
                case OperandType.InlineField:
                case OperandType.InlineI:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.ShortInlineR: return 4;
                case OperandType.InlineI8:
                case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch: return pos + 4 > il.Length ? -1 : 4 + 4 * BitConverter.ToInt32(il, pos);
                default: return -1;
            }
        }

        /// <summary>Walk the IL once, handing each instruction to <paramref name="visit"/> as
        /// (opcode, operand token position). Stops on anything it cannot size — a truncated walk can only
        /// make an arm fail, never pass, which is the safe direction for a law.</summary>
        private static void WalkIl(MethodBase m, Action<OpCode, int, byte[]> visit)
        {
            byte[] il = null;
            try { il = m?.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null) return;
            int i = 0;
            while (i < il.Length)
            {
                short code = il[i++];
                if (code == 0xFE)
                {
                    if (i >= il.Length) break;
                    code = (short)(0xFE00 | il[i++]);
                }
                if (!OpCodeByValue.TryGetValue(code, out var op)) break;
                int size = OperandSize(op.OperandType, il, i);
                if (size < 0 || i + size > il.Length) break;
                visit(op, i, il);
                i += size;
            }
        }

        /// <summary>Does the method call <paramref name="calleeName"/> (optionally on
        /// <paramref name="ownerName"/>)? Same shape as Program's Reaches, kept local so this law file
        /// stands alone.</summary>
        private static bool Reaches(MethodBase m, string ownerName, string calleeName)
        {
            bool found = false;
            var typeArgs = m?.DeclaringType != null && m.DeclaringType.IsGenericType ? m.DeclaringType.GetGenericArguments() : null;
            var methodArgs = m != null && m.IsGenericMethodDefinition ? m.GetGenericArguments() : null;
            WalkIl(m, (op, pos, il) =>
            {
                if (found || op.OperandType != OperandType.InlineMethod) return;
                if (op != OpCodes.Call && op != OpCodes.Callvirt) return;
                MethodBase callee = null;
                try { callee = m.Module.ResolveMethod(BitConverter.ToInt32(il, pos), typeArgs, methodArgs); } catch { }
                if (callee != null && callee.Name == calleeName &&
                    (ownerName == null || callee.DeclaringType?.Name == ownerName)) found = true;
            });
            return found;
        }

        /// <summary>True when every store of <paramref name="field"/> in this method writes the constant 1.
        /// A field initialiser in an object initialiser compiles to `ldc.i4.0/1; stfld`, so the byte before
        /// the store's opcode is the value — which is exactly the claim "this window blocks its peer".</summary>
        private static bool StoresConstantOne(MethodBase m, FieldInfo field)
        {
            bool sawStore = false, allOne = true;
            short prev = -1;
            var typeArgs = m.DeclaringType != null && m.DeclaringType.IsGenericType ? m.DeclaringType.GetGenericArguments() : null;
            WalkIl(m, (op, pos, il) =>
            {
                if (op.OperandType == OperandType.InlineField && (op == OpCodes.Stfld || op == OpCodes.Stsfld))
                {
                    FieldInfo target = null;
                    try { target = m.Module.ResolveField(BitConverter.ToInt32(il, pos), typeArgs, null); } catch { }
                    if (target == field)
                    {
                        sawStore = true;
                        if (prev != OpCodes.Ldc_I4_1.Value) allOne = false;
                    }
                }
                prev = op.Value;
            });
            return sawStore && allOne;
        }
    }
}
