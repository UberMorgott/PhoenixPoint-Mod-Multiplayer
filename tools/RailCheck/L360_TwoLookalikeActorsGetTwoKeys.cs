using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;

namespace RailCheck
{
    /// <summary>
    /// L360 — TWO ACTORS THAT LOOK IDENTICAL AT BATTLE START STILL GET TWO KEYS, AND A MISCOUNT IS LOUD.
    ///
    /// THE REPORT (2026-08-08): three <c>Deploy_Crate_ZoneBounds</c> at one position with one name. That is a
    /// FULL tie in <c>CanonicalOrder</c> — position and name are everything it compares — so which crate took
    /// which ordinal came down to <c>Map.GetActors</c> enumeration order, and <c>List.Sort</c> is not stable,
    /// so it need not agree even between two peers that see the same board. Probably identical in practice
    /// (both peers restored from one save blob) and NOT PROVEN, which is the part that matters: the scheme had
    /// no way to notice it was wrong.
    ///
    /// THE ANSWER IS <c>TacticalDestruction.AddressTags</c>:161's, borrowed rather than reinvented: number the
    /// tied members #i/n and put the COUNT inside the address. A peer that found two where the sender found
    /// three then mints a DIFFERENT key, so every record naming it is refused out loud instead of resolving to
    /// a lookalike. The running ordinal is still consumed for a tied actor, so no untied actor's key moves.
    ///
    /// The key is an int and cannot spell "#i/n", so the tie key is a HASH of exactly those fields. FNV-1a and
    /// not <c>string.GetHashCode</c>: the framework's string hash is an implementation detail that may be
    /// randomised per process, and a key two peers must agree on cannot be one of those.
    ///
    /// Falsify (each verified RED, then restored): drop <c>n</c> from <c>TieKeyOf</c>'s hash → (b)
    /// count-not-in-the-key; drop <c>index</c> → (a) tie-members-collide; stop calling <c>TieKey</c> from
    /// <c>BuildBattleKeys</c> → (d); delete the collision report → (e).
    /// </summary>
    internal static class L360_TwoLookalikeActorsGetTwoKeys
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var keyer = typeof(TacticalActorKey);
            var asm = keyer.Assembly;
            var tieKeyOf = keyer.GetMethod("TieKeyOf", All);
            var build = keyer.GetMethod("BuildBattleKeys", All);
            if (tieKeyOf == null || tieKeyOf.ReturnType != typeof(int) || tieKeyOf.GetParameters().Length != 6 ||
                build == null)
            {
                yield return "L360 premise-changed: TacticalActorKey.TieKeyOf(string,int,int,int,int,int)->int or " +
                             "BuildBattleKeys no longer resolves. The tie rule is the ONLY thing that stops two " +
                             "indistinguishable battle-start actors depending on enumeration order — without it " +
                             "every arm below passes while two peers may be pointing one key at two different " +
                             "objects and nothing anywhere notices.";
                yield break;
            }

            Func<string, int, int, int, int, int, int> key =
                (nm, x, y, z, i, n) => (int)tieKeyOf.Invoke(null, new object[] { nm, x, y, z, i, n });

            // ── (a) THE MEMBERS OF ONE TIE RESOLVE APART ─────────────────────────────
            var run = new[] { key("Crate", 10, 0, 20, 0, 3), key("Crate", 10, 0, 20, 1, 3), key("Crate", 10, 0, 20, 2, 3) };
            if (run.Distinct().Count() != 3)
                yield return "L360 tie-members-collide: three actors tied on every comparable field were minted " +
                             "only " + run.Distinct().Count() + " distinct key(s). Two of them then answer to one " +
                             "number and every command, hit and settle naming it reaches whichever the map hands " +
                             "back first — which is the enumeration-order dependency this law exists to remove.";

            // ── (b) THE COUNT IS IN THE KEY — THE POSITIVE CONTROL ───────────────────
            // This is the whole mechanism: a peer that counted the run differently must NOT land on the sender's
            // number. A rule that ignores n passes arm (a) and fails here, which is exactly the discrimination
            // arm (a) alone cannot make.
            if (key("Crate", 10, 0, 20, 0, 3) == key("Crate", 10, 0, 20, 0, 2))
                yield return "L360 count-not-in-the-key: member #0 of a run of THREE and member #0 of a run of TWO " +
                             "mint the same key. A peer that found a different number of lookalikes then silently " +
                             "answers with one of its own instead of refusing — which is worse than a miss, because " +
                             "nothing ever says the two boards disagree.";

            // ── (c) THE KEY IS A VALUE TWO PEERS MAY COMPARE ─────────────────────────
            if (key("Crate", 10, 0, 20, 1, 3) != key("Crate", 10, 0, 20, 1, 3))
                yield return "L360 key-not-deterministic: the same six inputs minted two different keys inside one " +
                             "process. Nothing about this address can be shared, and the peers disagree about every " +
                             "tied actor from the first frame.";
            if (key("Crate", 10, 0, 20, 0, 3) == key("Crate", 11, 0, 20, 0, 3) ||
                key("Crate", 10, 0, 20, 0, 3) == key("Barrel", 10, 0, 20, 0, 3))
                yield return "L360 groups-collide: two DIFFERENT tie groups — different position, or different name " +
                             "— mint the same key. The tie key must separate groups as well as members, or the " +
                             "collision it was added to prevent simply moves one level up.";
            foreach (var k in run)
                if (k > -1000000)
                {
                    yield return "L360 tie-key-in-the-ordinal-range: a tie key (" + k + ") is inside the range the " +
                                 "running battle-start ordinals occupy (-1 downwards). The two schemes then collide " +
                                 "and a crate answers to a soldier's key.";
                    break;
                }

            // ── (d) THE BUILDER ACTUALLY USES IT ─────────────────────────────────────
            var calls = Program.Callees(build, asm).ToList();
            if (!calls.Any(c => c.Name == "TieKey"))
                yield return "L360 builder-ignores-the-tie: BuildBattleKeys never mints a tie key, so the rule above " +
                             "is proven and unused and every tied actor is back on its enumeration ordinal.";
            if (!calls.Any(c => c.Name == "ReportIndistinguishableRun"))
                yield return "L360 tie-unreported: BuildBattleKeys no longer says when it found a run of " +
                             "indistinguishable actors. The key handles the disagreement; the line is what tells " +
                             "anyone the board contained one at all.";

            // ── (e) A COLLISION IS LOUD ──────────────────────────────────────────────
            if (!Program.StringRefs(build).Any(s => s.IndexOf("SAME derived key", StringComparison.Ordinal) >= 0))
                yield return "L360 collision-silent: BuildBattleKeys carries no sentence about two actors being " +
                             "minted one key. A hash CAN collide; what it may not do is collide quietly, because " +
                             "the second actor then overwrites the first in the resolver and both records go to one " +
                             "of them for the rest of the battle.";
        }
    }
}
