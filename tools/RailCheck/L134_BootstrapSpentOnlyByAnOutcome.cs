using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Multiplayer.Network;

namespace RailCheck
{
    /// <summary>
    /// L134 — THE NEW-CAMPAIGN BOOTSTRAP'S ONE SHOT IS SPENT BY AN OUTCOME, NEVER BY AN EVALUATION.
    ///
    /// THE REPORT (three instances, 2026-08-06): the host presses NEW CAMPAIGN, the campaign is created, and
    /// then nothing — forever. Clients sit on the HomeScreen with the "host is creating a new campaign"
    /// notice and no gate, no timeout and no failure notice; the host's own curtain never lifts either.
    /// One line in the host log: "New-campaign bootstrap: autosave capture failed".
    ///
    /// THE SEAM WAS WRONG AND THE LATCH FORGAVE NOTHING. The bootstrap fired at the curtain's
    /// Loaded→Playing edge, and that edge is the moment <c>GeoLevelController.OnLevelStart</c> merely STARTS
    /// <c>LevelCrt</c> (GeoLevelController.cs:377-379 → :464) — not one line of the geoscape's init has run,
    /// so the game's own <c>AutosaveGame()</c> dies inside <c>GeoAlienFaction.RecordExtendedInstanceData</c>
    /// (its <c>AlienRaidManager</c> is built later, :651-653 → GeoFaction.cs:418 → GeoAlienFaction.cs:246).
    /// <c>NewCampaignBootstrap.TryFire</c> then set <c>Armed = false</c> BEFORE the guard result and long
    /// before the attempt concluded, so the failing autosave's <c>yield break</c> left a bootstrap that was
    /// already spent: no retry, no error, no notice, no transfer.
    ///
    /// WHY THE EXISTING LAW STAYED GREEN THROUGH ALL OF IT. L124 arm M asserts that the bootstrap coroutine
    /// calls <c>LaunchTransfer</c> exactly once — a fact about the CALL GRAPH, which was true the whole
    /// time: the call was right there, three <c>yield break</c>s below the exception. This law asserts the
    /// OUTCOME instead: a shot that was spent must have produced either a launch or a stated failure.
    ///
    /// ARMS A–E are EXECUTED against the real latch. A and B are the falsifiers: restore the old
    /// <c>Armed = false; return isHost &amp;&amp; …;</c> body and A (<c>evaluation-spends-the-shot</c>) and B
    /// (<c>attempt-spends-the-shot</c>) both go red. VERIFIED RED 2026-08-06 by doing exactly that.
    ///
    /// ARMS F–J are structural, and here is the honest limit. Driving the bootstrap coroutine needs a live
    /// geoscape, a real <c>PhoenixSaveManager</c> and the game's <c>Timing</c>, none of which exist in a
    /// console host, so "every started attempt reaches an outcome" cannot be EXECUTED. It is asserted as the
    /// strongest feasible shape instead: the coroutine body holds EXACTLY ONE
    /// <c>ConcludeNewCampaignBootstrap</c> and it is written with no early exit above it (single-exit by
    /// construction — see the comment on the coroutine); nothing else in the coordinator may consume the
    /// latch; the conclusion itself must both log an ERROR and speak on the clients' system-chat rail; and a
    /// bounded wall-clock watchdog is wired into <c>Update</c> so a geoscape that never reports ready still
    /// releases everyone. An early exit ADDED inside that coroutine ahead of the conclude would still be
    /// invisible to this law — the watchdog, not the harness, is what makes that case non-fatal.
    ///
    /// NOT A REGRESSION, ON PURPOSE: an arm that survives an evaluation also survives a stale arm across an
    /// unrelated load. It is consumed at the very next geoscape the host reaches, exactly as before — only
    /// at that geoscape's READY seam instead of its Playing edge — and the back-out postfix
    /// (<c>OnSettingsBackClicked</c>) still disarms it outright.
    /// </summary>
    internal static class L134_BootstrapSpentOnlyByAnOutcome
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            // ─── ARMS A–E: EXECUTED — what spends the single shot ─────────────
            var latch = new NewCampaignBootstrap();
            latch.Arm();

            // (A) the guard is CLOSED (a transfer is already in flight). The evaluation must refuse WITHOUT
            //     consuming the arm — this is the old code's exact defect, one line earlier than the bug.
            bool closed = latch.TryFire(isHost: true, isActiveSession: true, geoscapeActive: true,
                                        transferActive: true);
            if (closed || !latch.Armed)
                yield return "L134 evaluation-spends-the-shot: TryFire with a CLOSED guard returned " + closed +
                             " and left Armed=" + latch.Armed + " (want False/True). An evaluation that " +
                             "consumes the latch is the whole bug: the host creates the campaign, one guard " +
                             "says 'not now', and the bootstrap is gone with no transfer and nobody told.";

            // (E) a non-geoscape playable frame is not an answer either way.
            if (latch.TryFire(true, true, geoscapeActive: false, transferActive: false) || !latch.Armed)
                yield return "L134 evaluation-spends-the-shot: a NON-geoscape evaluation consumed the arm. " +
                             "The bootstrap waits for a geoscape; any other playable frame must leave it " +
                             "exactly as it found it.";

            // (B) the guard is OPEN: the attempt STARTS. The shot is still not spent — only an outcome
            //     spends it, because an attempt can fail and a failure has to be able to say so.
            bool started = latch.TryFire(true, true, geoscapeActive: true, transferActive: false);
            if (!started)
                yield return "L134 bootstrap-never-fires: TryFire refused an OPEN guard (host, live session, " +
                             "geoscape, no transfer). Nothing else starts the co-op campaign transfer, so a " +
                             "new campaign would simply never reach the clients.";
            else if (!latch.Armed || !latch.Firing)
                yield return "L134 attempt-spends-the-shot: starting the attempt left Armed=" + latch.Armed +
                             ", Firing=" + latch.Firing + " (want True/True). The latch must stay armed until " +
                             "ConcludeNewCampaignBootstrap reports what happened; spending it at the START is " +
                             "how a failed autosave became a permanent silent stall for every peer.";

            // (C) …and while it is in flight, a second ready seam must not launch a second transfer on top.
            if (latch.TryFire(true, true, geoscapeActive: true, transferActive: false))
                yield return "L134 attempt-starts-twice: TryFire started a SECOND attempt while one was already " +
                             "in flight. Two autosave+transfer runs mean two entries into the one barrier — " +
                             "L122's 'one entry, one load' broken from the other end.";

            // (D) the outcome — and only the outcome — spends it, once.
            latch.Conclude();
            if (latch.Armed || latch.Firing)
                yield return "L134 outcome-does-not-spend: Conclude left Armed=" + latch.Armed + ", Firing=" +
                             latch.Firing + " (want False/False). The shot has to end somewhere or a concluded " +
                             "bootstrap re-fires on the next geoscape the host loads.";
            if (latch.TryFire(true, true, true, false))
                yield return "L134 outcome-does-not-spend: a concluded latch still answers TryFire. That is a " +
                             "transfer launched off a campaign nobody asked to share.";

            // (J) EXECUTED — the liveness bound, walked over its own clock. Postulate 2 (no blockers) needs
            //     the wait for the ready seam to END, and to end only for a bootstrap still waiting on it.
            const long t0 = 1000;
            int bound = NewCampaignBootstrap.ReadyTimeoutMs;
            var clock = new NewCampaignBootstrap();
            clock.Arm();
            if (bound <= 0)
                yield return "L134 no-liveness-backstop: ReadyTimeoutMs is " + bound + ". The wait for the " +
                             "geoscape-ready seam needs a BOUND, not a hope that the seam comes.";
            if (clock.ReadyWaitExpired(long.MaxValue))
                yield return "L134 watchdog-fires-unasked: an armed bootstrap that never entered a geoscape " +
                             "expires anyway. The host is still in the menus deciding; giving up there tells " +
                             "the clients a campaign failed that was never started.";
            clock.NoteGeoscapeEntered(t0);
            if (clock.ReadyWaitExpired(t0 + bound - 1))
                yield return "L134 watchdog-fires-unasked: the ready wait expired BEFORE its own bound. Faction " +
                             "init is unbounded work that mods extend; cutting it short aborts campaigns that " +
                             "were about to be ready.";
            if (!clock.ReadyWaitExpired(t0 + bound))
                yield return "L134 no-liveness-backstop: the ready wait never expires. A geoscape that dies " +
                             "mid-init (a mod throwing inside LevelCrt) then leaves every peer waiting forever " +
                             "with nothing counting — the exact permanent stall this whole law exists after.";
            clock.Conclude();
            if (clock.ReadyWaitExpired(long.MaxValue))
                yield return "L134 watchdog-fires-unasked: a CONCLUDED bootstrap still expires. The transfer is " +
                             "already in flight by then, so the watchdog would announce a failure over the top " +
                             "of a load that is working.";
            if (string.IsNullOrEmpty(SaveTransferCoordinator.NewCampaignFailedNotice) ||
                SaveTransferCoordinator.NewCampaignFailedNotice ==
                SaveTransferCoordinator.NewCampaignCreatingNotice)
                yield return "L134 failure-is-silent: the clients' failure notice is empty or identical to the " +
                             "'host is creating a campaign' one. A client that is told nothing new waits on a " +
                             "campaign that is not coming.";

            // ─── ARM F: the attempt starts at READINESS, not at a frame edge ──
            var coordType = typeof(SaveTransferCoordinator);
            var playable = coordType.GetMethod("OnNewCampaignPlayableFrame", All);
            var ready = coordType.GetMethod("OnGeoscapeReady", All);
            if (playable == null || ready == null)
            {
                yield return "L134 premise-changed: SaveTransferCoordinator.{OnNewCampaignPlayableFrame," +
                             "OnGeoscapeReady} no longer both resolve. The seam this law is about has moved — " +
                             "re-read it before assuming a fresh campaign still reaches the clients.";
                yield break;
            }
            if (Calls(playable, "TryFire"))
                yield return "L134 fires-at-the-wrong-seam: OnNewCampaignPlayableFrame evaluates the latch " +
                             "again. Level.State.Playing is the frame on which LevelCrt is merely STARTED " +
                             "(GeoLevelController.cs:377-379 → :464): no faction sub-manager exists yet, so " +
                             "AutosaveGame() throws in GeoAlienFaction.RecordExtendedInstanceData and the save " +
                             "that was supposed to become the co-op start is never written.";
            if (!Calls(playable, "NoteGeoscapeEntered"))
                yield return "L134 no-liveness-backstop: OnNewCampaignPlayableFrame no longer arms the ready " +
                             "deadline. The playable frame is the ONLY place that knows a geoscape was entered " +
                             "with a bootstrap pending, so without it a geoscape that never reports ready leaves " +
                             "every peer waiting with nothing counting.";
            if (!Calls(ready, "TryFire"))
                yield return "L134 bootstrap-never-fires: OnGeoscapeReady does not evaluate the latch — the " +
                             "geoscape-ready seam is wired to nothing and no campaign is ever transferred.";

            var seam = typeof(Multiplayer.Harmony.GeoscapeReadyPatch).GetMethod("Postfix", All);
            if (seam == null || !Calls(seam, "OnGeoscapeReady"))
                yield return "L134 fires-at-the-wrong-seam: GeoscapeReadyPatch.Postfix does not call " +
                             "OnGeoscapeReady. ModManager.OnGeoscapeStart (GeoLevelController.cs:757) is the " +
                             "game's OWN 'the geoscape is fully initialised' callback and the last line of " +
                             "LevelCrt's init block; anything else is frame-guessing that another mod's slower " +
                             "init breaks.";
            var attr = typeof(Multiplayer.Harmony.GeoscapeReadyPatch)
                           .GetCustomAttributes(typeof(HarmonyLib.HarmonyPatch), false)
                           .Cast<HarmonyLib.HarmonyPatch>().FirstOrDefault();
            if (attr == null || attr.info.declaringType != typeof(PhoenixPoint.Modding.ModManager) ||
                attr.info.methodName != "OnGeoscapeStart")
                yield return "L134 fires-at-the-wrong-seam: GeoscapeReadyPatch no longer targets " +
                             "ModManager.OnGeoscapeStart. That method is where the readiness claim comes from; " +
                             "pointed anywhere else the law above is asserting a promise nothing keeps.";

            // ─── ARMS G–I: the outcome is REACHED, and it is not silent ───────
            var conclude = coordType.GetMethod("ConcludeNewCampaignBootstrap", All);
            var pump = coordType.GetMethod("PumpNewCampaignWatchdog", All);
            var update = coordType.GetMethod("Update", All);
            var crt = StateMachineOf(coordType, "NewCampaignAutosaveAndTransferCrt");
            if (conclude == null || pump == null || update == null || crt == null)
            {
                yield return "L134 premise-changed: one of SaveTransferCoordinator.{ConcludeNewCampaignBootstrap," +
                             "PumpNewCampaignWatchdog,Update,NewCampaignAutosaveAndTransferCrt} no longer " +
                             "resolves. The outcome path this law asserts is not there to be checked.";
                yield break;
            }

            // (G) EXACTLY ONE conclusion in the coroutine — the single exit — and nothing else may consume
            //     the latch behind its back. Disarm stays legal only where the host explicitly backs out.
            int inCrt = CallCount(crt, "ConcludeNewCampaignBootstrap");
            if (inCrt != 1)
                yield return "L134 consumed-without-an-outcome: the bootstrap coroutine reaches " +
                             "ConcludeNewCampaignBootstrap " + inCrt + " times instead of exactly once. One is " +
                             "the single-exit shape the fix depends on: every former `yield break` — the failed " +
                             "autosave, the stale meta, the refused launch — now falls through to it. Zero is " +
                             "the original bug (spent, silent, forever); more than one is two outcomes for one " +
                             "shot.";
            var consumers = CallSiteCounts(coordType, "Conclude")
                                .Where(kv => !kv.Key.EndsWith(".ConcludeNewCampaignBootstrap", StringComparison.Ordinal))
                                .ToList();
            if (consumers.Count > 0)
                yield return "L134 consumed-without-an-outcome: NewCampaignBootstrap.Conclude is called from " +
                             string.Join(", ", consumers.Select(kv => kv.Key).OrderBy(s => s, StringComparer.Ordinal)) +
                             " as well as ConcludeNewCampaignBootstrap. One consumption point is what makes " +
                             "'spent' and 'somebody was told' the same event; a second one is a spend nobody " +
                             "reports.";

            // (H) an outcome that says nothing is the swallow this repo keeps re-buying.
            foreach (var required in new[] { "Conclude", "LogError", "SystemChat" })
                if (!Calls(conclude, required))
                    yield return "L134 failure-is-silent: ConcludeNewCampaignBootstrap does not call " + required +
                                 ". A failed bootstrap must spend the latch, name the real cause as an ERROR, and " +
                                 "tell the clients on the SAME system-chat rail that told them the campaign was " +
                                 "being created — otherwise they wait in a lobby for a campaign that is not coming.";

            // (I) the liveness belt is actually pumped.
            if (!Calls(update, "PumpNewCampaignWatchdog"))
                yield return "L134 no-liveness-backstop: Update no longer pumps PumpNewCampaignWatchdog. The " +
                             "deadline is then armed and never read, and a geoscape that dies mid-init (a mod " +
                             "throwing inside LevelCrt) strands every peer again — silently.";
            if (!Calls(pump, "ReadyWaitExpired"))
                yield return "L134 no-liveness-backstop: the watchdog no longer asks ReadyWaitExpired. That " +
                             "predicate is the executed one above; a pump that decides by any other rule is " +
                             "not the thing arms (J) proved.";
            if (!Calls(pump, "ConcludeNewCampaignBootstrap"))
                yield return "L134 no-liveness-backstop: the watchdog does not conclude the bootstrap when the " +
                             "deadline passes. A timeout that only logs is the same permanent wait with a " +
                             "friendlier log line.";
        }

        // ─── IL helpers — self-contained per law file (this repo's idiom) ────────────────

        /// <summary>Method name → how many times it calls <paramref name="calleeName"/>.</summary>
        private static Dictionary<string, int> CallSiteCounts(Type owner, string calleeName)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var m in owner.GetMethods(All)
                                   .Concat(owner.GetNestedTypes(All).SelectMany(t => t.GetMethods(All))))
            {
                int n = CallCount(m, calleeName);
                if (n > 0) map[m.DeclaringType.Name + "." + m.Name] = n;
            }
            return map;
        }

        /// <summary>The compiler-generated MoveNext of an iterator method, which is where its body actually
        /// lives (the declared method only builds the state machine).</summary>
        private static MethodBase StateMachineOf(Type owner, string iteratorName) =>
            owner.GetNestedTypes(All)
                 .Where(t => t.Name.Contains(iteratorName))
                 .SelectMany(t => t.GetMethods(All))
                 .FirstOrDefault(m => m.Name == "MoveNext");

        private static bool Calls(MethodBase m, string calleeName) => CallCount(m, calleeName) > 0;

        private static int CallCount(MethodBase m, string calleeName)
        {
            var typeArgs = m != null && m.DeclaringType != null && m.DeclaringType.IsGenericType
                ? m.DeclaringType.GetGenericArguments() : null;
            var methodArgs = m != null && m.IsGenericMethodDefinition ? m.GetGenericArguments() : null;
            int n = 0;
            foreach (var step in Walk(m))
            {
                if (step.Value.Op.OperandType != OperandType.InlineMethod ||
                    (step.Value.Op != OpCodes.Call && step.Value.Op != OpCodes.Callvirt &&
                     step.Value.Op != OpCodes.Newobj)) continue;
                MethodBase callee = null;
                try { callee = m.Module.ResolveMethod(BitConverter.ToInt32(step.Key, step.Value.Pos),
                                                      typeArgs, methodArgs); } catch { }
                if (callee != null && callee.Name == calleeName) n++;
            }
            return n;
        }

        /// <summary>Names a field (read OR write) or an auto-property getter called
        /// <paramref name="memberName"/> anywhere in the body.</summary>
        private static bool Reads(MethodBase m, string memberName)
        {
            var typeArgs = m != null && m.DeclaringType != null && m.DeclaringType.IsGenericType
                ? m.DeclaringType.GetGenericArguments() : null;
            var methodArgs = m != null && m.IsGenericMethodDefinition ? m.GetGenericArguments() : null;
            foreach (var step in Walk(m))
            {
                if (step.Value.Op.OperandType == OperandType.InlineMethod)
                {
                    MethodBase callee = null;
                    try { callee = m.Module.ResolveMethod(BitConverter.ToInt32(step.Key, step.Value.Pos),
                                                          typeArgs, methodArgs); } catch { }
                    if (callee != null && callee.Name == "get_" + memberName) return true;
                }
                else if (step.Value.Op.OperandType == OperandType.InlineField)
                {
                    FieldInfo f = null;
                    try { f = m.Module.ResolveField(BitConverter.ToInt32(step.Key, step.Value.Pos),
                                                    typeArgs, methodArgs); } catch { }
                    if (f != null && (f.Name == memberName ||
                                      f.Name == "<" + memberName + ">k__BackingField")) return true;
                }
            }
            return false;
        }

        private struct Step { public OpCode Op; public int Pos; }

        private static IEnumerable<KeyValuePair<byte[], Step>> Walk(MethodBase m)
        {
            byte[] il = null;
            try { il = m == null ? null : m.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null) yield break;
            int i = 0;
            while (i < il.Length)
            {
                short code = il[i++];
                if (code == 0xFE)
                {
                    if (i >= il.Length) yield break;
                    code = (short)(0xFE00 | il[i++]);
                }
                OpCode op;
                if (!OpCodeByValue.TryGetValue(code, out op)) yield break;
                int size = OperandSize(op.OperandType, il, i);
                if (size < 0 || i + size > il.Length) yield break;
                yield return new KeyValuePair<byte[], Step>(il, new Step { Op = op, Pos = i });
                i += size;
            }
        }

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
    }
}
