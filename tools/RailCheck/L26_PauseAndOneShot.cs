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
    /// L26 — A BLOCKING WINDOW PAUSES ONCE, NOBODY VETOES A RESUME, AND A ONE-SHOT WINDOW IS NEVER REPLAYED
    /// BY A REPAINT. Symptoms of one live run (2026-08-04, 1 host + 2 clients), all silent, all about the
    /// game's own queued-window mechanism:
    ///
    /// (a) THE CLOCK, WHICH FAILED TWICE IN OPPOSITE DIRECTIONS. First (21:20): an aircraft was flying, an
    /// event and then a cinematic fired, the HOST paused — and both clients kept running, so the plane flew
    /// on while every player was reading. The mirrored window pushed itself with <c>PauseGame = false</c>
    /// and trusted the host's pause to arrive over the rail; it does not arrive when the host is ALREADY
    /// paused, because <c>Timing.Paused</c>'s setter is CHANGE-GATED (Timing.cs:112) — no event, no diff,
    /// no delta — while the client's own local pause was blocked by the block-first law.
    ///
    /// Second (22:09), caused BY that fix: a hold set in which any peer's open window VETOED everyone's
    /// resume until it was dismissed everywhere. A player who had closed his own popups could then no
    /// longer fly — the play button did nothing while somebody else sat in a cutscene. That breaks the
    /// project's first-class rule: AT ANY MOMENT ANY PLAYER MUST BE ABLE TO PLAY EVERYTHING, and with 49 of
    /// 50 players AFK the one active player still plays a whole game (the host being the AFK one included).
    /// So the hold set and its veto are DELETED and the mod keeps no window state at all. What pauses is
    /// THE GAME'S OWN ONE-SHOT: <c>ProcessQueriedStateSwitch</c>:58-73 → <c>RequestGamePause</c>:1269 →
    /// <c>RequestPauseCrt</c>:1293 → <c>SetGamePauseState(true)</c>, on the very funnel <see cref="TimeSync"/>
    /// already captures — so the peer whose window opened pauses itself and relays it, once. Pause is a
    /// COURTESY EDGE; resume is UNCONDITIONAL, from any peer, first-to-act-wins.
    ///
    /// (b) THE VIDEO. The same cinematic restarted 7 times on each client. <c>OpenUiRepaint</c>'s LAST-RESORT
    /// Exit+Enter fallback ran on <c>UIStateGeoCutscene</c>, and that is not a repaint: <c>ExitState</c>:76
    /// stops the video, <c>EnterState</c>:59 <c>Setup()</c>s it again → <c>PlayCutsceneOnFinishedLoad</c>:114
    /// → <c>Play()</c>. Every queued window is a ONE-SHOT PRESENTATION with the same hazard
    /// (<c>UIStateRosterDeployment</c> was re-entered mid-deployment in the same run).
    ///
    /// WHY A LAW. Every half is the repo's dominant shape — a correct-looking fix that quietly stops being
    /// reached, with no log line. The veto especially: it reads like a safety feature on the way back in,
    /// and it only ever shows itself in a live 3-instance session, as "the play button does nothing".
    ///
    /// Falsify: re-introduce a resume veto anywhere in the mod → <c>resume-vetoed</c>; give
    /// <c>PauseHold</c> a hold set again → <c>hold-state-returned</c>; re-register op 3 on 0xB0 →
    /// <c>hold-op-returned</c>; drop the <c>SetGamePauseState</c> call from <c>TimeSync.HandleIntentOp</c>
    /// → <c>host-pause-not-applied</c>; make <c>PausesLocally(true)</c> false → <c>client-pause-blocked</c>,
    /// <c>PausesLocally(false)</c> true → <c>client-resumes-locally</c>; remove <c>RequestGamePause</c> from
    /// the game's queue seam → <c>oneshot-pause-gone</c>; put <c>PauseGame = false</c> back on either
    /// mirrored raiser → <c>mirror-window-unpaused</c>; delete the <c>IsCurrentQueuedWindow</c> guard in
    /// <c>OpenUiRepaint.Repaint</c> → <c>oneshot-replayable</c>.
    /// </summary>
    internal static class L26_PauseAndOneShot
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check()
        {
            // ─── (a) NOBODY VETOES A RESUME, AND NOTHING REMEMBERS WHOSE WINDOW IS OPEN ───
            // Banned by NAME, across the whole mod: the veto shipped once and had to be torn out the same
            // hour, and the next attempt will not be called PauseHold.VetoResume unless it is the same idea.
            Type[] modTypes;
            try { modTypes = typeof(PauseHold).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { modTypes = ex.Types.Where(t => t != null).ToArray(); }
            foreach (var t in modTypes)
            {
                if (!t.GetMethods(AllMembers).Any(m => m.Name == "VetoResume")) continue;
                yield return "L26 resume-vetoed: " + t.Name + " can refuse a resume. No peer may ever be " +
                             "stopped from restarting the shared clock — a player who has dismissed his own " +
                             "windows must be able to fly WHILE somebody else is still reading, and with 49 " +
                             "of 50 players AFK the last one still plays a whole game. Pause is a courtesy " +
                             "edge; resume is unconditional and first-to-act-wins";
                break;
            }
            // The hold SET was the veto's memory. A static collection back on this type is the same idea
            // wearing a different method name.
            foreach (var f in typeof(PauseHold).GetFields(AllMembers))
            {
                if (f.FieldType == typeof(string) || !typeof(System.Collections.IEnumerable).IsAssignableFrom(f.FieldType)) continue;
                yield return "L26 hold-state-returned: PauseHold keeps a collection again (" + f.Name + "). " +
                             "The mod holds NO window state: a blocking window is a one-shot pause issued by " +
                             "the game itself, and whose window is still up is nobody's business afterwards";
                break;
            }

            // ─── (b) THE 0xB0 FAMILY CARRIES PAUSE AND SPEED, AND NOTHING ELSE ───
            // The live dispatch table, read back off the engine after registration — not a re-reading of
            // the source. Op 3 (the hold) must stay absent: a peer announcing "I am reading" is precisely
            // the fact the host must not have, because having it is what tempted the veto.
            TimeSync.RegisterIntents();
            var families = typeof(IntentRail)
                .GetField("_families", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as System.Collections.IDictionary;
            object family = families?[SurfaceIds.GeoTimeIntent];
            var ops = family?.GetType().GetField("Ops", AllMembers)?.GetValue(family) as System.Collections.IDictionary;
            if (ops == null)
                yield return "L26 time-family-unregistered: the 0xB0 time family has no op table after " +
                             "RegisterIntents ran — every time intent would be answered with 'unknown op' " +
                             "and silently dropped, so no client could pause or resume anything";
            else
            {
                if (!ops.Contains(TimeSync.OpPause) || !ops.Contains(TimeSync.OpSpeed))
                    yield return "L26 time-family-unregistered: op " + TimeSync.OpPause + " (pause/resume) or " +
                                 "op " + TimeSync.OpSpeed + " (speed) is missing from the 0xB0 op table — a " +
                                 "client's clock gesture is dropped as an unknown op, silently";
                if (ops.Contains((byte)3))
                    yield return "L26 hold-op-returned: op 3 is registered on 0xB0 again. That op existed only " +
                                 "to tell the host somebody had a window open, which is the input to the resume " +
                                 "veto that froze the campaign around one player's popup";
            }

            // THE HOST APPLIES A PAUSE *AND A RESUME*, through the game's own funnel. Read off the IL: the
            // applier needs a live GeoLevelController, so it cannot be executed here — but it can be proved
            // to still write the clock rather than deciding whether to.
            var applier = typeof(TimeSync).GetMethod("HandleIntentOp", AllMembers);
            if (applier == null || !Reaches(applier, "GeoscapeView", "SetGamePauseState"))
                yield return "L26 host-pause-not-applied: TimeSync.HandleIntentOp no longer calls " +
                             "GeoscapeView.SetGamePauseState. Whatever it does instead, a client's pause or " +
                             "resume no longer reaches the game's own funnel — which is also where the " +
                             "TimeLimit guard (GeoscapeView.cs:1259) and the events that flush the delta live";

            // BLOCK-FIRST IN BOTH DIRECTIONS. The host force-reemits T+TA even for a swallowed no-op.
            var locally = typeof(TimeSync).GetMethod("PausesLocally", AllMembers);
            if (locally == null)
                yield return "L26 client-pause-blocked: TimeSync.PausesLocally is gone, so nothing decides " +
                             "whether a client's own pause runs — and if it does not, the aircraft flies on " +
                             "under that player's popup exactly as it did on 2026-08-04";
            else
            {
                if ((bool)locally.Invoke(null, new object[] { true }))
                    yield return "L26 client-pause-predicts: a client's PAUSE still mutates locally before the " +
                                 "host record, so the initiator and watchers have different rates in flight and " +
                                 "one shared TimeAnchor compensation cannot be correct for both";
                if ((bool)locally.Invoke(null, new object[] { false }))
                    yield return "L26 client-resumes-locally: a client's RESUME now runs locally. That is the " +
                                 "one direction that is not self-healing: the client's campaign runs ahead of " +
                                 "the host's. The intent alone is enough — nothing refuses it any more";
                if (applier != null && (!Reaches(applier, "DiffEngine", "ForceReemit") ||
                                        !Reaches(applier, "DiffEngine", "FlushNow") ||
                                        !Reaches(applier, "TimeAnchor", "RefreshForAuthoritativeReply")))
                    yield return "L26 no-op-pause-has-no-echo: HandleIntentOp must force-reemit and flush the " +
                                 "authoritative T+TA roots from a freshly relatched anchor when the native " +
                                 "setter swallows an equal value";
                if (applier != null && !HasOrderedAuthoritativeReply(applier))
                    yield return "L26 authoritative-reply-out-of-order: HandleIntentOp must call " +
                                 "RefreshForAuthoritativeReply, ForceReemit(\"T\"), ForceReemit(\"TA\"), " +
                                 "then FlushNow, in exactly that order. Missing, renamed, or swapped roots " +
                                 "can publish an aged or incomplete authoritative clock reply.";
            }

            // ─── (c) THE GAME'S OWN ONE-SHOT PAUSE, THE PREMISE THE MOD'S SEAM WAS DELETED FOR ───
            var queueSeam = typeof(GeoscapeViewSwitchQuery).GetMethod("ProcessQueriedStateSwitch", AllMembers);
            if (queueSeam == null || !Reaches(queueSeam, "GeoscapeView", "RequestGamePause"))
                yield return "L26 oneshot-pause-gone: GeoscapeViewSwitchQuery.ProcessQueriedStateSwitch no " +
                             "longer asks the view to pause when it dequeues a PauseGame window. That call IS " +
                             "our pause mechanism — the mod deleted its own window seam because the game " +
                             "already pauses the peer whose popup opened. Without it nobody pauses for an " +
                             "event or a cutscene, silently, and the aircraft flies while everyone reads";
            var crt = typeof(GeoscapeView).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(t => t.Name.Contains("RequestPauseCrt"));
            // GetMethods, not GetMethod: an iterator's MoveNext can also be an explicit interface impl, and
            // an AmbiguousMatchException here would take the whole gate down instead of failing an arm.
            var moveNext = crt?.GetMethods(AllMembers).FirstOrDefault(m => m.Name.EndsWith("MoveNext"));
            if (moveNext == null || !Reaches(moveNext, "GeoscapeView", "SetGamePauseState"))
                yield return "L26 oneshot-pause-gone: GeoscapeView.RequestPauseCrt no longer reaches " +
                             "SetGamePauseState (GeoscapeView.cs:1290-1295). The queued window's pause then " +
                             "never lands on the funnel TimeSync captures, so it pauses nobody but the peer " +
                             "that raised it — if that";

            // PREMISE: the two game members the mod still reads.
            if (typeof(GeoscapeViewSwitchQuery).GetField("_currentStateSwitchRequest", AllMembers) == null)
                yield return "L26 premise-changed: GeoscapeViewSwitchQuery._currentStateSwitchRequest is gone. " +
                             "PauseHold.IsCurrentQueuedWindow reads that field to know which window the game " +
                             "is showing — with it renamed it binds null and fails OPEN, silently, and the " +
                             "one-shot guard below stops guarding";
            if (typeof(GeoscapeViewStateSwitchRequest).GetField("PauseGame") == null)
                yield return "L26 premise-changed: GeoscapeViewStateSwitchRequest.PauseGame is gone — the " +
                             "game's own 'this window blocks the player' flag is what the mirrored raisers " +
                             "set and what the queue seam pauses on, for every window kind with no table";

            // ─── (d) THE MIRRORED WINDOW STILL PAUSES ITS OWN PEER ───
            foreach (var v in MirrorPausesGame(typeof(EventPopup), "RaiseMirrored")) yield return v;
            foreach (var v in MirrorPausesGame(typeof(GeoModalMirror), "RaiseMirrored")) yield return v;

            // ─── (e) A ONE-SHOT WINDOW IS NEVER RE-ENTERED ───
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

        /// <summary>A mirrored raise must push its window with the GAME'S own PauseGame flag set — that flag
        /// is the ONLY thing that makes the queue pause the peer it is shown to. Read off the IL: the raiser sets the
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
                             "PauseGame = false, so ProcessQueriedStateSwitch never calls RequestGamePause for " +
                             "it. That peer shows a blocking window while its own clock runs, and it does NOT " +
                             "recover from the host's pause: an already-paused host re-writing Timing.Paused is " +
                             "swallowed by the change-gated setter, so no delta is ever emitted and the " +
                             "aircraft keeps flying under the popup";
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

        /// <summary>Proves the no-op reply protocol from the actual HandleIntentOp callsites, including
        /// each ForceReemit string argument. Merely counting calls is insufficient: swapping T/TA or
        /// changing either literal still compiles and silently changes the wire ordering.</summary>
        private static bool HasOrderedAuthoritativeReply(MethodBase m)
        {
            var events = new List<string>();
            string pendingLiteral = null;
            var typeArgs = m?.DeclaringType != null && m.DeclaringType.IsGenericType
                ? m.DeclaringType.GetGenericArguments() : null;
            var methodArgs = m != null && m.IsGenericMethodDefinition ? m.GetGenericArguments() : null;

            WalkIl(m, (op, pos, il) =>
            {
                if (op == OpCodes.Ldstr)
                {
                    try { pendingLiteral = m.Module.ResolveString(BitConverter.ToInt32(il, pos)); }
                    catch { pendingLiteral = null; }
                    return;
                }

                if ((op == OpCodes.Call || op == OpCodes.Callvirt) && op.OperandType == OperandType.InlineMethod)
                {
                    MethodBase callee = null;
                    try { callee = m.Module.ResolveMethod(BitConverter.ToInt32(il, pos), typeArgs, methodArgs); }
                    catch { }
                    if (callee?.DeclaringType == typeof(TimeAnchor) && callee.Name == "RefreshForAuthoritativeReply")
                        events.Add("refresh");
                    else if (callee?.DeclaringType == typeof(DiffEngine) && callee.Name == "ForceReemit")
                        events.Add("force:" + (pendingLiteral ?? "<nonliteral>"));
                    else if (callee?.DeclaringType == typeof(DiffEngine) && callee.Name == "FlushNow")
                        events.Add("flush");
                }

                // The string must be the argument loaded at this callsite, not an unrelated earlier
                // literal. Nop is harmless sequence-point padding; any other instruction breaks the link.
                if (op != OpCodes.Nop) pendingLiteral = null;
            });

            var forces = events.Where(e => e.StartsWith("force:", StringComparison.Ordinal)).ToList();
            if (!forces.SequenceEqual(new[] { "force:T", "force:TA" })) return false;
            int refresh = events.IndexOf("refresh");
            int t = events.IndexOf("force:T");
            int ta = events.IndexOf("force:TA");
            int flush = events.IndexOf("flush");
            return refresh >= 0 && refresh < t && t < ta && ta < flush;
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
