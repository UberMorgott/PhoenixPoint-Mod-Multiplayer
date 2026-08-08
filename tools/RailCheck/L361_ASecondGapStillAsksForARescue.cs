using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;

namespace RailCheck
{
    /// <summary>
    /// L361 — A SECOND HOLE IN THE RESOLVED-ATTACK STREAM STILL PRODUCES A RESNAPSHOT REQUEST.
    ///
    /// 0x84 carries discrete events with no re-emit, so a hole is a permanent difference in somebody's health
    /// and the resnapshot is the only repair there is. <c>_resnapRequested</c> exists so one hole does not
    /// become a storm of requests — and it was cleared in exactly ONE place: a resnapshot actually arriving.
    ///
    /// SO IT WAS A LATCH. A request that never reached the host, or that the host REJECTED
    /// (<c>HandleResnapRequest</c> refuses when it is not in a battle), left the flag set for the rest of the
    /// mission. Every later gap then logged its error, asked for nothing, and recovered from nothing — the
    /// recovery path reporting loudly while being permanently disarmed, which reads in a log exactly like a
    /// recovery path that is working.
    ///
    /// The re-arm is counted in TICKS, not seconds, on purpose: <c>ClientTick</c> is the rail's own standing
    /// pump and <c>Time</c> is a native ECall this harness cannot make — so the rule stays executable here,
    /// which is the difference between this law and a comment.
    ///
    /// Falsify (each verified RED, then restored): delete the re-arm branch from <c>ClientTick</c> → (b)
    /// latched-forever; re-arm on the FIRST tick (drop the wait) → (a) no-debounce-left; stop clearing the
    /// counter when a resnapshot lands → (c).
    /// </summary>
    internal static class L361_ASecondGapStillAsksForARescue
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var sync = typeof(TacticalDamageSync);
            var tick = sync.GetMethod("ClientTick", All);
            var requested = sync.GetField("_resnapRequested", All);
            var pending = sync.GetField("_resnapPending", All);
            var waited = sync.GetField("_resnapWaited", All);
            var ticks = sync.GetField("ResnapWaitTicks", All);
            var reset = sync.GetMethod("Reset", All);
            if (tick == null || requested == null || pending == null || waited == null || ticks == null || reset == null)
            {
                yield return "L361 premise-changed: TacticalDamageSync.ClientTick / _resnapRequested / _resnapPending " +
                             "/ _resnapWaited / ResnapWaitTicks / Reset no longer all resolve. Together they ARE the " +
                             "only recovery a discrete-event surface has; if the shape moved, re-point this law " +
                             "rather than let it pass on a rail that cannot heal itself.";
                yield break;
            }
            int ceiling = (int)ticks.GetRawConstantValue();
            if (ceiling <= 1)
            {
                yield return "L361 no-debounce-left: ResnapWaitTicks is " + ceiling + ", so the request re-arms " +
                             "immediately and the flag stops being a debounce at all. One dropped packet then " +
                             "becomes a resnapshot request per frame — a whole-battlefield broadcast per frame — " +
                             "which is the storm the latch was introduced to prevent.";
                yield break;
            }

            string threw = null;
            bool armedAfterOne = false, armedAtCeiling = false, counterCleared = false;
            try
            {
                reset.Invoke(null, null);
                requested.SetValue(null, true);        // a request went out; nothing has answered it
                pending.SetValue(null, false);         // and it is no longer waiting to be SENT
                waited.SetValue(null, 0);

                tick.Invoke(null, new object[] { null });
                armedAfterOne = (bool)requested.GetValue(null);

                for (int i = 1; i <= ceiling && (bool)requested.GetValue(null); i++)
                    tick.Invoke(null, new object[] { null });
                armedAtCeiling = (bool)requested.GetValue(null);
                counterCleared = (int)waited.GetValue(null) == 0;
            }
            catch (Exception ex) { threw = (ex.InnerException ?? ex).GetType().Name + ": " + (ex.InnerException ?? ex).Message; }
            finally { try { reset.Invoke(null, null); } catch { } }

            if (threw != null)
            {
                yield return "L361 tick-throws: pumping the client tick with an outstanding request threw (" + threw +
                             "). That pump runs every frame of every mission; a throw in it takes the whole rail's " +
                             "standing emitters down with it.";
                yield break;
            }
            // ── (a) IT IS STILL A DEBOUNCE ───────────────────────────────────────────
            if (!armedAfterOne)
                yield return "L361 no-debounce-left: the outstanding request was abandoned on the very first tick. " +
                             "The flag then holds nothing back and each lost record asks the host to broadcast the " +
                             "whole battlefield again.";
            // ── (b) AND IT IS NOT A LATCH ────────────────────────────────────────────
            if (armedAtCeiling)
                yield return "L361 latched-forever: after " + ceiling + " ticks with no resnapshot arriving, " +
                             "_resnapRequested is STILL set. The next gap — and every gap after it in that battle — " +
                             "logs an error and asks for nothing. The report says the recovery fired; nothing did.";
            // ── (c) AND IT REARMS CLEANLY ────────────────────────────────────────────
            else if (!counterCleared)
                yield return "L361 counter-not-cleared: the wait counter was left non-zero after the re-arm, so the " +
                             "NEXT request expires early — a live request racing an answer that is still in flight, " +
                             "which is the storm again by a slower route.";

            // ── (d) THE ARRIVAL PATH STILL DISARMS IT ────────────────────────────────
            var resnapped = sync.GetMethod("ApplyResnap", All);
            if (resnapped == null || !Program.FieldRefs(resnapped).Any(f => f.Name == "_resnapRequested"))
                yield return "L361 arrival-does-not-disarm: ApplyResnap no longer clears _resnapRequested. The " +
                             "timeout above then becomes the ONLY way back, so every recovered battle spends a full " +
                             "ceiling of ticks unable to ask again.";
        }
    }
}
