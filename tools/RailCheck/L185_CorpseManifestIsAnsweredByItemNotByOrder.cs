using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;

namespace RailCheck
{
    /// <summary>
    /// L185 — A CORPSE'S CONTENTS ARE ANSWERED BY THE ITEM, NEVER BY ITS PLACE IN THE QUEUE.
    ///
    /// THE BUG THIS ENCODES, from both peers' logs of 2026-08-07 23:17:44 (host) / 23:17:51 (client).
    /// <c>DieAbility.ShouldDestroyItem</c>:187-200 draws from <c>SharedData.Random</c>, a per-peer
    /// <c>new System.Random()</c>, so the host rolls once and every other peer answers from the host's
    /// manifest. The manifest was a QUEUE and <c>TryDeclared</c> scanned it FORWARD to the matching def guid,
    /// discarding everything it passed over — on the stated premise that "the mirror consumes them in ITS
    /// order, which is the same order, because it is the same method over the same inventory".
    ///
    /// THAT PREMISE IS FALSE, and the two logs say so in the same breath. The host pre-rolls at
    /// <c>TacticalActorBase.Die</c> and enumerated [clip, clip, rifle] (host Player.log frame 47909: two
    /// "Will destroy item … AmmoClip" at 43 % and 40 % against a 75 % chance, then the rifle kept). The
    /// client's own <c>DieAbility.DropItems</c> — which runs LATER in the death, inside the die coroutine,
    /// after <c>StopNavigation</c> and after the equipped weapon has moved — asked [rifle, clip, clip].
    /// Reaching the rifle threw both clip answers away, emptied the queue, and the client KEPT two items the
    /// host had destroyed. It printed one line ("a corpse's contents arrived with no host manifest") and
    /// carried on; twelve minutes later the inventory rail reported an item def this peer held and the host
    /// did not. <c>GetDroppableItems</c>:202-224 is inventory-then-addons over
    /// <c>GetComponentsInChildren</c> plus a <c>Distinct()</c> — nothing in it promises an order, and the two
    /// peers do not even read it at the same instant of the same death.
    ///
    /// SO THE ADDRESS IS THE ITEM'S DEF AND ONLY THE ITEM'S DEF. The manifest is a multiset keyed by def guid,
    /// holding as many answers per def as the host rolled, consumed one per question in whatever order the
    /// questions arrive. That keeps the property the queue was built for — <c>DropItems</c>:139-185
    /// legitimately asks about a SUBSET (body parts are skipped in the mount branch, and nothing is asked at
    /// all when the def destroys everything), so an unasked answer simply goes unused — and drops the one it
    /// never had.
    ///
    /// WHAT THIS LAW ASSERTS IS THE OUTCOME. It EXECUTES the real manifest and asserts that the answers a peer
    /// gets are the ones the host declared, for every asking order, which is the only thing that makes the two
    /// corpses hold the same items. It does not assert that any method is called.
    ///
    /// Falsify: restore the forward-scanning queue in <c>LootMirror.TryDeclared</c> (dequeue until the guid
    /// matches) → <c>L185 answers-are-positional</c> AND <c>L185 an-unasked-answer-eats-the-next-one</c>;
    /// make the unmatched case return a value instead of false → <c>L185 unnamed-item-is-guessed-at</c>;
    /// stop consuming → <c>L185 one-corpse-answers-for-the-next</c>.
    /// </summary>
    internal static class L185_CorpseManifestIsAnsweredByItemNotByOrder
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        private const int Corpse = -4242;

        internal static IEnumerable<string> Check()
        {
            var loot = typeof(LootMirror);
            var declare = loot.GetMethod("Declare", All);
            var tryDeclared = loot.GetMethod("TryDeclared", All);
            var reset = loot.GetMethod("Reset", All);
            if (declare == null || tryDeclared == null || reset == null)
            {
                yield return "L185 premise-changed: LootMirror.{Declare,TryDeclared,Reset} no longer resolves. The " +
                             "corpse manifest is the ONLY thing standing between a mirrored death and a local draw " +
                             "from SharedData.Random — the same stream the spawn tables use — so re-read this law " +
                             "before assuming two peers still loot the same body.";
                yield break;
            }

            // ── (a) THE OUTCOME: the same answers, whatever the asking order ──
            // The exact 2026-08-07 shape, and then its mirror image. The host rolled clip/clip/rifle; both
            // peers must end up destroying the two clips and keeping the rifle no matter who asks first.
            foreach (var v in Answers("the host's own order",
                                      new[] { E("clip", true), E("clip", true), E("rifle", false) },
                                      new[] { "clip", "clip", "rifle" },
                                      new[] { true, true, false })) yield return v;
            foreach (var v in Answers("the order the client actually asked in",
                                      new[] { E("clip", true), E("clip", true), E("rifle", false) },
                                      new[] { "rifle", "clip", "clip" },
                                      new[] { false, true, true })) yield return v;
            foreach (var v in Answers("interleaved",
                                      new[] { E("a", true), E("b", false), E("a", false) },
                                      new[] { "b", "a", "a" },
                                      new[] { false, true, false })) yield return v;

            // ── (b) AN ANSWER THE MIRROR NEVER ASKS FOR MUST NOT EAT THE NEXT ──
            // DropItems skips body parts in the mount branch, so a declared answer routinely goes unasked.
            // Under the queue that skip consumed everything before it; here it consumes nothing.
            {
                reset.Invoke(null, null);
                declare.Invoke(null, new object[] { Corpse, new List<KeyValuePair<string, bool>>
                                                    { E("skipped", false), E("wanted", true), E("also", true) } });
                bool gotWanted, gotAlso;
                bool haveWanted = Ask(tryDeclared, "wanted", out gotWanted);
                bool haveAlso = Ask(tryDeclared, "also", out gotAlso);
                if (!haveWanted || !gotWanted || !haveAlso || !gotAlso)
                    yield return "L185 an-unasked-answer-eats-the-next-one: after skipping the first entry the " +
                                 "manifest answered 'wanted'=" + (haveWanted ? gotWanted.ToString() : "<nothing>") +
                                 " and 'also'=" + (haveAlso ? gotAlso.ToString() : "<nothing>") + ", where the host " +
                                 "declared both as destroy. That is the 2026-08-07 divergence exactly: the peer " +
                                 "keeps items the host destroyed, and the corpse is a superset for the rest of the " +
                                 "mission.";
                reset.Invoke(null, null);
            }

            // ── (c) CONSUMED, and never borrowed from another item ────────────
            {
                reset.Invoke(null, null);
                declare.Invoke(null, new object[] { Corpse, new List<KeyValuePair<string, bool>> { E("only", true) } });
                bool v1, v2, v3;
                bool first = Ask(tryDeclared, "only", out v1);
                bool again = Ask(tryDeclared, "only", out v2);
                if (!first || !v1)
                    yield return "L185 answers-are-positional: a single declared answer did not come back at all, " +
                                 "so the manifest is being read by position rather than by item.";
                if (again)
                    yield return "L185 one-corpse-answers-for-the-next: an answer already consumed is handed out a " +
                                 "second time, so the NEXT corpse inherits this one's roll.";
                reset.Invoke(null, null);
                declare.Invoke(null, new object[] { Corpse, new List<KeyValuePair<string, bool>> { E("named", true) } });
                if (Ask(tryDeclared, "not-named-by-the-host", out v3) || v3)
                    yield return "L185 unnamed-item-is-guessed-at: an item the host's manifest does not name is " +
                                 "answered anyway. The fallback has to be a LOUD keep — borrowing another item's " +
                                 "roll destroys something the host still has.";
                // …and that loud keep must not have thrown the rest of the manifest away with it.
                bool survivor;
                if (!Ask(tryDeclared, "named", out survivor) || !survivor)
                    yield return "L185 one-unknown-item-voids-the-corpse: asking about an item the host never named " +
                                 "left the answers it DID declare unreachable. One unexpected item then loses the " +
                                 "whole corpse.";
                reset.Invoke(null, null);
            }

            // ── (d) AND A CORPSE NOBODY DECLARED INVENTS NOTHING ──────────────
            {
                reset.Invoke(null, null);
                bool v;
                if (Ask(tryDeclared, "anything", out v))
                    yield return "L185 undeclared-corpse-is-answered: the manifest answers for a death nobody " +
                                 "declared. A peer would then destroy an item the host kept, on an answer that came " +
                                 "from nowhere.";
                reset.Invoke(null, null);
            }
        }

        private static KeyValuePair<string, bool> E(string guid, bool destroy) =>
            new KeyValuePair<string, bool>(guid, destroy);

        private static bool Ask(MethodInfo tryDeclared, string guid, out bool value)
        {
            var probe = new object[] { Corpse, guid, false };
            bool found = (bool)tryDeclared.Invoke(null, probe);
            value = (bool)probe[2];
            return found;
        }

        /// <summary>Declare the host's manifest, then ask in the given order, and assert every answer is the
        /// host's. The asking order is the whole point: it is the one thing the two peers do not share.</summary>
        private static IEnumerable<string> Answers(string what, KeyValuePair<string, bool>[] declared,
                                                   string[] askOrder, bool[] expected)
        {
            var loot = typeof(LootMirror);
            loot.GetMethod("Reset", All).Invoke(null, null);
            loot.GetMethod("Declare", All).Invoke(null, new object[] { Corpse, declared.ToList() });
            var tryDeclared = loot.GetMethod("TryDeclared", All);

            var got = new List<string>();
            bool bad = false;
            for (int i = 0; i < askOrder.Length; i++)
            {
                bool value;
                bool found = Ask(tryDeclared, askOrder[i], out value);
                got.Add(askOrder[i] + "=" + (found ? value.ToString() : "<nothing>"));
                if (!found || value != expected[i]) bad = true;
            }
            loot.GetMethod("Reset", All).Invoke(null, null);

            if (bad)
                yield return "L185 answers-are-positional (" + what + "): the host declared [" +
                             string.Join(", ", declared.Select(d => d.Key + "=" + d.Value).ToArray()) +
                             "] and asking in that peer's own order produced [" + string.Join(", ", got.ToArray()) +
                             "]. The two peers enumerate the same droppable set at different instants of the same " +
                             "death and NOT in the same order, so an answer found by position is an answer for a " +
                             "different item — the corpse then holds what the host destroyed, or loses what the " +
                             "host kept.";
        }
    }
}
