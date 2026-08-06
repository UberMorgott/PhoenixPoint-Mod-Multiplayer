using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Tactical;
using UnityEngine;

namespace RailCheck
{
    /// <summary>
    /// L105 — A STALE STAT SNAPSHOT MAY NEVER WIN OVER A FRESHER VALUE.
    ///
    /// THE MEASUREMENT. Host log 2026-08-05, one axe kill, one actor: <c>HOST settle Soldier_4 ap=2,6 wp=8
    /// seq=231</c> @00:38:04.036 → <c>HOST damage Ironbar hp-196 seq=104</c> @00:38:04.953 → <c>HOST settle
    /// Soldier_4 ap=2,6 wp=10 seq=233</c> @00:38:05.545. The settle for an action is captured at
    /// <c>TacticalAbility.ClearPlayingAction</c>, which fires A FULL SECOND BEFORE that action's own damage
    /// resolves — so the WP it carries is the number from before the kill. The +2 between the two settles is
    /// the game's own kill bonus: <c>TacticalActor.OnAnotherActorDeath</c>:1849-1865 adds the victim's
    /// <c>WillPointWorth</c> to its KILLER and subtracts it from every faction-mate of the victim,
    /// synchronously inside <c>Health.Set(0)</c> → <c>OnHealthChange</c>:616-622 → <c>Die</c>, on EVERY peer
    /// separately.
    ///
    /// The host never applies its own settles, so it shows one clean +2. A client holds settle #231 behind
    /// its mirrored ability (<c>TacticalCommandSync.ClientTick</c>), applies the death off 0x84 meanwhile
    /// (+2), drains the held PRE-kill snapshot (-2), and only then receives #233 (+2). That is the reported
    /// +2/-2/+2 verbatim. It is an ORDERING defect and it is not specific to will points: the same race
    /// exists for every stat a replicated native event moves while a snapshot of it is in flight — which is
    /// why the rule is stated over the SNAPSHOT, not over the stat.
    ///
    /// THE RULE. Every stat snapshot that crosses the wire is stamped, when it ARRIVES, with
    /// <c>TacticalDamageSync.StatEpoch</c> — a counter bumped once per native stat event this peer replays
    /// (a death, the only host fact whose replay rewrites stats the record itself does not name). A snapshot
    /// draining against a higher counter is older than the board and must not write stats. Nothing is lost by
    /// dropping it: this peer replayed the SAME native grant the host did, so the fresher numbers are already
    /// here.
    ///
    /// WHAT THE ARMS ASSERT, AND THE ONE THING THEY CANNOT. (a)-(c) are EXECUTED and assert the OUTCOME —
    /// that a snapshot stamped before a native stat event actually loses, that one stamped after it actually
    /// WINS (a guard that refuses everything would trade this flicker for a dead AP authority, law L98), and
    /// that the stamp is taken by the real arrival path rather than by the test. (d) asserts the counter is
    /// actually driven, without which every other arm is vacuously green. (e) is the seam between the two:
    /// <c>ApplySettle</c> must consult the predicate, and it cannot be EXECUTED headless because it needs a
    /// live <c>TacticalActor</c> with a <c>CharacterStats</c> — so it is read out of the applier's IL and
    /// that is stated rather than dressed up.
    /// ponytail: (e) scans the raw IL bytes for the callee's metadata token instead of decoding opcodes — a
    /// 4-byte token match can in principle collide with an operand's bytes, i.e. it can only ever be too
    /// LENIENT, never too strict. Swap in a full opcode walk (L98 has one) if a false green is ever seen.
    /// </summary>
    internal static class L105_StatOrdering
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var dmg = typeof(TacticalDamageSync);
            var stamp = dmg.GetProperty("StatEpoch", All);
            var bump = dmg.GetMethod("NoteNativeStatEvent", All);
            var stale = dmg.GetMethod("StatsAreStale", All);
            if (stamp == null || bump == null || stale == null)
            {
                yield return "L105 ordering-stamp-gone: TacticalDamageSync no longer exposes StatEpoch / " +
                             "NoteNativeStatEvent / StatsAreStale, so nothing stamps a stat snapshot with when " +
                             "it arrived and a settle captured before a kill can silently rewind that kill's " +
                             "will points on every mirror.";
                yield break;
            }

            // ── (a)+(b) the predicate's own outcome ────────────────────────
            int before = (int)stamp.GetValue(null, null);
            if ((bool)stale.Invoke(null, new object[] { before }))
                yield return "L105 fresh-snapshot-refused: a snapshot stamped with the CURRENT StatEpoch is " +
                             "reported stale. Then every settle would lose its stat write, which does not fix " +
                             "the flicker — it deletes the host's AP authority (law L98) instead.";
            bump.Invoke(null, null);
            if (!(bool)stale.Invoke(null, new object[] { before }))
                yield return "L105 stale-snapshot-wins: after a native stat event was replayed, a snapshot " +
                             "stamped BEFORE it is still reported fresh. That snapshot holds pre-kill will " +
                             "points and applying it rewinds a kill bonus every peer already granted itself — " +
                             "the +2/-2/+2 flicker measured 2026-08-05.";

            // ── (c) the ARRIVAL path is what stamps, not the test ──────────
            var cmd = typeof(TacticalCommandSync);
            var queue = cmd.GetMethod("QueueSettle", All);
            var pendingField = cmd.GetField("_pending", BindingFlags.NonPublic | BindingFlags.Static);
            if (queue == null || pendingField == null)
            {
                yield return "L105 settle-queue-gone: TacticalCommandSync.QueueSettle / _pending no longer " +
                             "exist — the arrival side of a settle cannot be exercised, so nothing here can " +
                             "prove a held settle carries the epoch it arrived at.";
            }
            else
            {
                var pending = (System.Collections.IDictionary)pendingField.GetValue(null);
                var saved = new List<System.Collections.DictionaryEntry>();
                foreach (System.Collections.DictionaryEntry e in pending) saved.Add(e);
                pending.Clear();

                string violation = null;
                try
                {
                    const int key = 4243;
                    int atArrival = (int)stamp.GetValue(null, null);
                    // Trailing args = the settle's carried sets (statuses, ability traits — L131/L137); this
                    // arm is about the arrival STAMP, so both are empty.
                    queue.Invoke(null, new object[] { key, new Vector3(1f, 0f, 1f), 9f, 5f, false,
                                                      new List<string>(), new List<string>() });
                    var entry = pending[key];
                    var epoch = entry == null ? null : entry.GetType().GetField("Epoch", All);
                    if (epoch == null)
                        violation = "L105 settle-unstamped: the pending settle carries no Epoch, so when it " +
                                    "finally drains there is no way to tell whether the board moved under it. " +
                                    "A settle held behind a mirrored ability routinely outlives the death it " +
                                    "was captured before.";
                    else if ((int)epoch.GetValue(entry) != atArrival)
                        violation = "L105 settle-stamped-late: the pending settle's Epoch is not the one that " +
                                    "was current when it arrived. The stamp has to be the ARRIVAL instant — " +
                                    "stamping at drain time makes every settle eternally fresh and restores " +
                                    "the bug exactly.";
                    else
                    {
                        bump.Invoke(null, null);
                        if (!(bool)stale.Invoke(null, new object[] { (int)epoch.GetValue(entry) }))
                            violation = "L105 held-settle-survives-a-death: a settle queued before a replayed " +
                                        "native stat event is not reported stale when it drains, so its " +
                                        "pre-kill numbers overwrite the post-kill ones this peer already has.";
                    }
                }
                finally
                {
                    pending.Clear();
                    foreach (var e in saved) pending[e.Key] = e.Value;
                }
                if (violation != null) yield return violation;
            }

            // ── (d) the counter must actually be driven ───────────────────
            foreach (var name in new[] { "ApplyDamage", "ApplyResnap" })
            {
                var applier = dmg.GetMethod(name, All);
                if (applier == null)
                    yield return "L105 applier-gone: TacticalDamageSync." + name + " no longer exists — the " +
                                 "point where this peer replays a host death, and therefore the only thing " +
                                 "that can move the epoch, is unreachable.";
                else if (!References(applier, bump))
                    yield return "L105 epoch-never-bumped: TacticalDamageSync." + name + " replays the host's " +
                                 "deaths and never calls NoteNativeStatEvent. The counter then never moves, " +
                                 "every snapshot is eternally fresh, and every other arm of this law is " +
                                 "vacuously green while the flicker is back.";
            }

            // ── (e) the applier must consult the predicate ─────────────────
            var apply = cmd.GetMethod("ApplySettle", All);
            if (apply == null)
                yield return "L105 settle-applier-gone: TacticalCommandSync.ApplySettle no longer exists.";
            else if (!References(apply, stale))
                yield return "L105 applier-ignores-staleness: ApplySettle writes ActionPoints/WillPoints " +
                             "without consulting TacticalDamageSync.StatsAreStale, so the stamp is carried " +
                             "all the way to the write and then thrown away.";
        }

        /// <summary>Does <paramref name="m"/>'s IL mention <paramref name="callee"/>? A raw 4-byte scan for
        /// the metadata token — see the ponytail note on the class: lenient by construction, never strict.
        /// Used only where the real outcome needs a live actor and cannot be executed headless.</summary>
        private static bool References(MethodBase m, MethodBase callee)
        {
            byte[] il = null;
            try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null || callee == null) return false;
            int token = callee.MetadataToken;
            for (int i = 0; i + 4 <= il.Length; i++)
                if (BitConverter.ToInt32(il, i) == token) return true;
            return false;
        }
    }
}
