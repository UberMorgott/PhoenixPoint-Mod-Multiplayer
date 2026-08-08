using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;
using PhoenixPoint.Tactical.Entities.Abilities;

namespace RailCheck
{
    /// <summary>
    /// L321 — THE ENGINE'S ONE-FRAME HANDOFF IS NOT A STALL, AND AN ORDER THAT LEAVES THE CHANNEL IS NAMED
    /// ON THE FRAME IT HAPPENS.
    ///
    /// THE REPORT (2026-08-08, `8bc2f99`). The mirror watchdog fired on 8 of 8 mirrors in one battle and
    /// named <c>IdleAbility</c> as the blocker every time, which sent an RCA chasing the NORMAL path.
    /// Landing behind the idle loop is how the engine starts every order, on the acting peer exactly as on a
    /// mirroring one: <c>ShootAbility.Activate</c>:167 takes <c>EnqueueAction</c> on every player-turn click,
    /// <c>EnqueueAction</c>:947-951 appends behind the resting soldier's never-ending
    /// <c>IdleAction</c>:276-292, and the same call then runs <c>TacticalActor.OnAbilityEnqueued</c>:1361-1365
    /// = <c>IdleAbility.RequestStop()</c>, so the loop ends on its next resumption and
    /// <c>ActionComponent.CompletedAction</c>:112-119 starts ours. ONE FRAME. Reading
    /// <c>ExecutingAbilities</c> alone right after <c>Activate</c> cannot tell that apart from a stall.
    ///
    /// TWO OPPOSITE FAILURES, AND THIS LAW HOLDS BOTH ENDS. Over-reporting is the one that was measured: an
    /// ability whose <c>Activate</c> does its work synchronously takes NO action at all, so it could never
    /// start, so it reached the ten-second ceiling and was announced as "never played" when it had been.
    /// Under-reporting is the one that costs a battle: an order really can leave the channel without ever
    /// executing — <c>ActionComponent.PlayActionAfterCurrent</c>:80-91 cancels every entry past the FIRST
    /// whenever a later solo enqueue arrives on the same actor, so a second order on that soldier deletes
    /// the one already in line. A watchdog tuned to stop crying wolf by watching less would lose exactly
    /// that case, silently.
    ///
    /// SO THE DECISION IS EXECUTED, NOT INSPECTED (arm (a)). <c>MirrorIsWaitingInLine(executing, enqueued)</c>
    /// is a pure two-axis reading of the engine's own state — <c>TacticalAbility.IsExecuting</c>:257 and
    /// <c>IsEnqueued</c>:269 — and all four corners are asserted. Only "not running AND still holding a
    /// pending action" is a wait. Arm (f) is the positive control: the pre-fix shape (<c>!executing</c>,
    /// which is what reading <c>ExecutingAbilities</c> alone amounts to) is run through the same corner
    /// table and MUST be caught.
    ///
    /// AND THE ARMING PATH SAYS NOTHING (arms (b)-(c)). The old code announced the queueing at
    /// <c>Activate</c>-time — that IS the false alarm, and its absence is the outcome, so
    /// <c>ApplyActivate</c> must reach no <c>Debug.LogError</c>/<c>LogWarning</c> after the activation call.
    /// It must, in the same stretch, read <c>IsEnqueued</c> and consult the decision, or arm (a) proves a
    /// pure function nothing uses (L137's lesson, and the reason the function was extracted at all).
    ///
    /// AND A DROP IS NAMED WHEN IT HAPPENS, NOT TEN SECONDS LATER (arm (d)). <c>TickQueuedMirrors</c> must
    /// read <c>IsEnqueued</c> — the engine's own signal that the action was cleared, which
    /// <c>PlayingAction.SetState</c>:55-63 also runs on CANCEL — and must carry TWO separate reports, the
    /// drop and the ceiling, with the drop verdict reached first. Folding them together is how "it is still
    /// waiting its turn" survives for ten seconds over an order that was already gone.
    ///
    /// Falsify (each verified RED, then restored): <c>!executing &amp;&amp; enqueued</c> → <c>!executing</c>
    /// (the defect, verbatim) → (a) took-no-action-watched; → <c>enqueued</c> → (a) playing-watched;
    /// put the old "MIRROR QUEUED, not played" LogError back after <c>Activate</c> → (b); stop passing
    /// <c>IsEnqueued</c> into the decision → (c); delete the drop branch from <c>TickQueuedMirrors</c> →
    /// (d) drop-unnamed; move the drop test below the ceiling counter → (d) drop-waits-for-the-ceiling.
    /// </summary>
    internal static class L321_AnEnqueuedMirrorIsNotAStallAndADroppedOneIsNamed
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var sync = typeof(TacticalCommandSync);
            var decide = sync.GetMethod("MirrorIsWaitingInLine", All);
            var apply = sync.GetMethod("ApplyActivate", All);
            var tick = sync.GetMethod("TickQueuedMirrors", All);

            if (decide == null || apply == null || tick == null || decide.ReturnType != typeof(bool) ||
                decide.GetParameters().Length != 2)
            {
                yield return "L321 premise-changed: TacticalCommandSync.MirrorIsWaitingInLine(bool,bool)->bool, " +
                             "ApplyActivate or TickQueuedMirrors no longer resolves. The mirror watchdog has " +
                             "been reshaped and every arm below would pass vacuously while it either cries " +
                             "wolf on the engine's normal handoff or loses a dropped order in silence.";
                yield break;
            }

            // ── (a) THE DECISION, ON ALL FOUR CORNERS ────────────────────────────────
            Func<bool, bool, bool> real = (e, q) => (bool)decide.Invoke(null, new object[] { e, q });
            foreach (var v in Corners(real, "L321")) yield return v;

            // ── (b)-(c) THE ARMING PATH: silent, and fed by the engine's own state ───
            var seq = Program.CalleeSequence(apply);
            int at = seq.FindIndex(c => c.Name == "Activate");
            if (at < 0)
                yield return "L321 activation-gone: ApplyActivate no longer calls Activate at all, so there is " +
                             "no mirror to watch and the whole watchdog is measuring nothing.";
            else
            {
                var after = seq.Skip(at + 1).ToList();
                var said = after.FirstOrDefault(c => c.DeclaringType == typeof(UnityEngine.Debug) &&
                                                    (c.Name == "LogError" || c.Name == "LogWarning"));
                if (said != null)
                    yield return "L321 handoff-reported-as-a-stall: ApplyActivate reaches Debug." + said.Name +
                                 " AFTER the activation. An order landing behind IdleAbility is how the engine " +
                                 "starts every one of them and it clears on the next frame, so a line said here " +
                                 "fires on every mirror in the battle — the 8-of-8 false alarm this law exists " +
                                 "to keep buried. Whatever is worth saying is the SWEEP's to say, a frame later, " +
                                 "when it is known to be true.";
                if (!after.Any(c => c.Name == "get_IsEnqueued"))
                    yield return "L321 decision-unfed: ApplyActivate no longer reads TacticalAbility.IsEnqueued " +
                                 "after activating. Without it the two axes collapse back to ExecutingAbilities " +
                                 "alone, which cannot tell the one-frame handoff from a stall — and arm (a) " +
                                 "then proves a pure function nothing consults.";
                if (!after.Any(c => c.MetadataToken == decide.MetadataToken && c.Module == decide.Module))
                    yield return "L321 decision-unreached: ApplyActivate no longer calls MirrorIsWaitingInLine, " +
                                 "so the corner table in arm (a) is asserted over a function the arming path " +
                                 "does not use and every mirror is watched (or not) by some other rule nothing " +
                                 "here checks.";
            }

            // ── (d) THE SWEEP: a drop is its own verdict, reached before the ceiling ──
            var sweep = Program.CalleeSequence(tick);
            int enq = sweep.FindIndex(c => c.Name == "get_IsEnqueued");
            var reports = sweep.Select((c, i) => new { c, i })
                               .Where(x => x.c.DeclaringType == typeof(UnityEngine.Debug) && x.c.Name == "LogError")
                               .Select(x => x.i).ToList();
            if (enq < 0)
                yield return "L321 drop-unnamed: TickQueuedMirrors never reads TacticalAbility.IsEnqueued, which " +
                             "is the ONLY signal that a queued order left the channel — IsEnqueued:269 goes false " +
                             "the moment ClearPlayingAction runs for that action, and PlayingAction.SetState:55-63 " +
                             "runs it on CANCEL too. Without it, ActionComponent.PlayActionAfterCurrent:80-91 " +
                             "eating the record when a second order arrives on the same soldier is invisible: " +
                             "this peer never plays an action every other peer did, and nothing says so.";
            else if (reports.Count < 2)
                yield return "L321 one-verdict-for-two-failures: TickQueuedMirrors carries " + reports.Count +
                             " report(s), not two. A record that LEFT the line and one that is still in it after " +
                             "ten seconds are opposite failures with opposite repairs; collapsing them means the " +
                             "dropped order is either announced as still waiting or not announced at all.";
            else if (reports[0] < enq)
                yield return "L321 drop-waits-for-the-ceiling: the first report in TickQueuedMirrors is reached " +
                             "before IsEnqueued is consulted, so an order that has already been cancelled is " +
                             "still described as waiting its turn until the 600-frame ceiling expires. The whole " +
                             "point of reading the engine's state is that the loss is named on the frame it " +
                             "happens.";

            // ── (f) POSITIVE CONTROL ─────────────────────────────────────────────────
            if (!Corners((e, q) => !e, "CONTROL").Any())
                yield return "L321 control-not-red: the pre-fix decision (`!executing` — what reading " +
                             "ExecutingAbilities alone amounts to) passed the corner table, so arm (a) cannot " +
                             "tell the fixed rule from the one that produced 8 false alarms in a battle.";
        }

        /// <summary>The whole truth table, both failure directions in one place. Only "not running AND still
        /// holding a pending action" is a wait.</summary>
        private static List<string> Corners(Func<bool, bool, bool> decide, string tag)
        {
            var found = new List<string>();
            if (!decide(false, true))
                found.Add(tag + " genuine-wait-unwatched: an ability that is not executing and still holds an " +
                          "enqueued action was not watched. That is the real queue — the record that later " +
                          "names either the drop or the ten-second stall — and without it a mirrored order " +
                          "this peer never plays leaves no trace at all.");
            if (decide(false, false))
                found.Add(tag + " took-no-action-watched: an ability that is neither executing nor enqueued was " +
                          "put on the watch list. It took no action AT ALL (a def whose Activate does its work " +
                          "synchronously), so there is nothing to wait for, it can never start, and it reaches " +
                          "the ceiling every time — announced as 'never played' when it had in fact played. " +
                          "That is the over-report half of the 2026-08-08 false alarm.");
            if (decide(true, false) || decide(true, true))
                found.Add(tag + " playing-watched: an ability the engine reports as EXECUTING was put on the " +
                          "queued-mirror watch list. It is running; a watchdog that counts frames against a " +
                          "playing order announces a stall over an animation that is doing exactly what it " +
                          "should.");
            return found;
        }
    }
}
