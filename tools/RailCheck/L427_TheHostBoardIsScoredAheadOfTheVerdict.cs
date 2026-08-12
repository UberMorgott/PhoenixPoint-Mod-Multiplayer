using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Tactical;

namespace RailCheck
{
    /// <summary>
    /// L427 — THE HOST'S OBJECTIVE BOARD IS A CORRECTION THAT LANDS BEFORE THE VERDICT, NOT A SECOND TRUTH.
    ///
    /// THE REPORT (live, escort/convince): the host got the objective, the reward and the XP; the client got
    /// none of it, and its mission-summary numbers differed. <c>FactionObjective.State</c> is not serialized
    /// (<c>FactionObjective</c>:70) — every peer RECOUNTS it, and the turn-edge sweep is what normally makes
    /// the two counts agree. A mission that ends on the turn its objective completes has no edge left.
    ///
    /// THE FIX IS SHAPED, and each arm below is one of the shape's load-bearing halves:
    ///   • it must be AHEAD OF THE VERDICT — arriving after <c>OpEnd</c> is arriving after the client's own
    ///     <c>GameOver()</c> has already paid the XP out, which is the bug, not the fix;
    ///   • it must MIRROR THE REWARD, not only the state — six evaluator subclasses compute completion from
    ///     LIVE counters that stamping the state is exactly what stops refreshing;
    ///   • it must SUPPRESS NOTHING — a client that stops running <c>GiveExperienceForObjectives</c> or
    ///     <c>GameOverCondition.EvaluateObjectives</c> gains a second source of truth that can disagree with
    ///     the board in front of the player, and loses the <c>TacFactionState</c> assignment that gates
    ///     <c>RemoveMindControl</c>, <c>TacticalTelemetry</c> and <c>ModManager.OnTacticalEnd</c>;
    ///   • it must be DROPPED PER BATTLE — a reward mirror carried into the next mission pays out there.
    ///
    /// NOT A QUORUM and cannot become one: host→all, one shot, applied on arrival. A peer that never receives
    /// it runs its own native game-over and finishes the mission alone. Nothing waits on a human.
    ///
    /// Falsify (each verified RED, then restored): move the <c>HostBroadcast</c> call after the <c>OpEnd</c>
    /// <c>Send</c> → (b); delete the <c>GetActualExperienceReward</c> postfix → (c); patch
    /// <c>GiveExperienceForObjectives</c> → (d); drop <c>TacticalObjectiveOutcome.Reset</c> from
    /// <c>TacticalTurnSync.Reset</c> → (e); give <c>OpOutcome</c> the same number as another 0x80 op → (f).
    /// </summary>
    internal static class L427_TheHostBoardIsScoredAheadOfTheVerdict
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var outcome = typeof(TacticalObjectiveOutcome);
            var asm = outcome.Assembly;
            var turn = typeof(TacticalTurnSync);
            var broadcast = outcome.GetMethod("HostBroadcast", All);
            var apply = outcome.GetMethod("ApplyOutcome", All);
            var reset = outcome.GetMethod("Reset", All);
            var end = turn.GetMethod("HostBroadcastEnd", All);
            var send = turn.GetMethod("Send", All);
            var turnReset = turn.GetMethod("Reset", All);
            var opField = outcome.GetField("OpOutcome", All);
            if (broadcast == null || apply == null || reset == null || end == null || send == null ||
                turnReset == null || opField == null)
            {
                yield return "L427 premise-changed: TacticalObjectiveOutcome.HostBroadcast/ApplyOutcome/Reset, " +
                             "TacticalTurnSync.HostBroadcastEnd/Send/Reset or OpOutcome no longer resolves. The " +
                             "mission-end objective correction is the only thing that makes a battle ending on " +
                             "the turn its objective completes score the same on both peers — without these " +
                             "every arm below passes while checking nothing.";
                yield break;
            }

            // ── (b) AHEAD OF THE VERDICT — the whole point, and an IL-order fact ─────────────────────
            var calls = Program.Callees(end, asm).ToList();
            int iBoard = calls.FindIndex(c => c.Name == "HostBroadcast" && c.DeclaringType == outcome);
            int iSend = calls.FindIndex(c => c.Name == "Send" && c.DeclaringType == turn);
            if (iBoard < 0)
                yield return "L427 board-not-sent: HostBroadcastEnd never sends the host's objective board, so " +
                             "every peer scores the mission from its own recount and a battle that ends inside a " +
                             "turn keeps the divergence that killed the escort mission's client XP.";
            else if (iSend < 0)
                yield return "L427 verdict-not-sent: HostBroadcastEnd no longer calls Send, so OpEnd never goes " +
                             "out and no client is told the mission ended at all.";
            else if (iBoard > iSend)
                yield return "L427 board-behind-the-verdict: HostBroadcastEnd sends the objective board AFTER " +
                             "OpEnd. On one ordered stream that means the correction is applied after the client " +
                             "has already run its own GameOver and paid out its own XP — the board is then " +
                             "corrected for nothing.";

            // ── (c) THE REWARD, NOT ONLY THE STATE — at the one non-virtual funnel ───────────────────
            var patched = outcome.GetNestedTypes(All)
                .SelectMany(t => t.GetCustomAttributes(typeof(HarmonyPatch), false).OfType<HarmonyPatch>())
                .Select(a => a.info?.methodName)
                .Where(n => n != null).ToList();
            foreach (var want in new[] { "GetActualExperienceReward", "GetActualSkillPointsReward" })
                if (!patched.Contains(want))
                    yield return "L427 reward-not-mirrored: nothing in TacticalObjectiveOutcome patches " +
                                 "FactionObjective." + want + ". Six evaluator subclasses compute GetCompletion " +
                                 "from live counters that only EvaluateObjective refreshes, and the stamped state " +
                                 "is precisely what stops it running — so a partly achieved escort keeps this " +
                                 "peer's own count and pays a different reward from the host's.";

            // ── (d) NOTHING IS SUPPRESSED — the rejected full-authority design, kept rejected ────────
            var forbidden = new[] { "GiveExperienceForObjectives", "EvaluateObjectives" };
            foreach (var t in asm.GetTypes())
                foreach (var a in t.GetCustomAttributes(typeof(HarmonyPatch), false).OfType<HarmonyPatch>())
                {
                    var n = a.info?.methodName;
                    if (n == null || !forbidden.Contains(n)) continue;
                    yield return "L427 client-evaluation-suppressed: " + t.FullName + " patches " + n + ". The " +
                                 "client must keep computing its own objectives and its own XP — the host's board " +
                                 "CORRECTS that computation, it does not replace it. Suppressing it makes a second " +
                                 "truth that can disagree with the board the player is looking at, and " +
                                 "GameOverCondition also assigns the TacFactionState that gates RemoveMindControl, " +
                                 "TacticalTelemetry and every other mod's OnTacticalEnd.";
                }

            // ── (e) DROPPED PER BATTLE — a stale mirror pays out in the next mission ─────────────────
            if (!Program.Callees(turnReset, asm).Any(c => c.Name == "Reset" && c.DeclaringType == outcome))
                yield return "L427 mirror-outlives-the-battle: TacticalTurnSync.Reset does not clear the reward " +
                             "mirror. It is keyed by objective INSTANCE, so a held entry is dead weight at best " +
                             "and — if the next battle's allocator hands back the same reference — the last " +
                             "mission's reward paid out in this one.";

            // ── (f) THE OP IS ITS OWN — THE POSITIVE CONTROL ────────────────────────────────────────
            byte op = (byte)opField.GetValue(null);
            var taken = new List<byte>();
            foreach (var f in turn.GetFields(All).Where(f => f.IsLiteral && f.FieldType == typeof(byte) &&
                                                             f.Name.StartsWith("Op", StringComparison.Ordinal) &&
                                                             f.Name != "OpEndTurn" && f.Name != "OpLeaveBattle" &&
                                                             f.Name != "OpRestartMission"))
                taken.Add((byte)f.GetRawConstantValue());
            if (taken.Contains(op))
                yield return "L427 op-collides: OpOutcome (" + op + ") is already a host→all op on 0x80. Two " +
                             "meanings on one number means the client reads a turn edge, a mission end or a leave " +
                             "as an objective board and desynchronises the stream from that byte onward.";
            if (taken.Count == 0)
                yield return "L427 op-set-empty: no host→all op constants were found on TacticalTurnSync, so the " +
                             "collision check above proved nothing.";
        }
    }
}
