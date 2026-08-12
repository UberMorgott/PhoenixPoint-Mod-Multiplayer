using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;

namespace RailCheck
{
    /// <summary>
    /// L426 — A BATTLE-START ACTOR KEY IS DERIVED FROM THE ACTOR, NEVER FROM THE ROSTER IT SITS IN.
    ///
    /// THE REPORT (live 2026-08-11). The derived key used to be an ORDINAL over the key-less actors: the key
    /// of actor #40 was a function of how many actors that peer had COUNTED before it. The host built 118,
    /// the client 119 (13 s later, post-reveal), and from the first mismatch every later key named a
    /// different object — the host's -103 resolved to a <c>TacAIWaypoint</c> on the client. Dropped settles,
    /// "ROSTER DIVERGENCE … SMALLER battle" x51, holes in the resolved-attack stream, eight forced 124-actor
    /// resnapshots, missing loot manifests. A 68-vs-68 mission on the same build had zero errors, which is
    /// the tell: nothing was wrong with the mechanism, the ADDRESS was cardinality-dependent.
    ///
    /// This law replaces L360, which asserted the tie-breaking that the ordinal scheme needed (three
    /// identical crates at one position). That problem does not exist any more: the crates are scene-placed,
    /// so they have three distinct <c>SceneObjectId</c>s and nothing has to be broken apart.
    ///
    /// Falsify (each verified RED, then restored): make <c>ContentKey</c> return the same value for two
    /// different strings → (a); return a positive/zero key → (b); stop calling <c>ContentKeyOf</c> from
    /// <c>BuildBattleKeys</c>, or count anything there instead → (d); put a content key inside the
    /// host-assigned range → (c); delete the collision report → (e).
    /// </summary>
    internal static class L426_ABattleStartKeyIsNeverAnOrdinal
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var keyer = typeof(TacticalActorKey);
            var asm = keyer.Assembly;
            var contentKey = keyer.GetMethod("ContentKey", All);
            var build = keyer.GetMethod("BuildBattleKeys", All);
            var baseField = keyer.GetField("ContentKeyBase", All);
            if (contentKey == null || contentKey.ReturnType != typeof(int) ||
                contentKey.GetParameters().Length != 1 || build == null || baseField == null)
            {
                yield return "L426 premise-changed: TacticalActorKey.ContentKey(string)->int, ContentKeyBase or " +
                             "BuildBattleKeys no longer resolves. Content derivation is the ONLY thing standing " +
                             "between this rail and the ordinal scheme that made one peer's key name another " +
                             "peer's waypoint — without it every arm below passes while checking nothing.";
                yield break;
            }

            Func<string, int> key = s => (int)contentKey.Invoke(null, new object[] { s });
            int keyBase = (int)baseField.GetValue(null);

            // ── (a) DIFFERENT ACTORS, DIFFERENT KEYS — and the SAME actor, the same key ──────────────
            var distinct = new[]
            {
                key("scene:0f8c2b1a-1111-4b7e-9c1d-000000000001"),
                key("scene:0f8c2b1a-1111-4b7e-9c1d-000000000002"),
                key("spawn:Crabman_ActorDef@100,0,200"),
                key("spawn:Crabman_ActorDef@110,0,200"),
            }.Distinct().Count();
            if (distinct != 4)
                yield return "L426 addresses-collide: four different actor addresses minted only " + distinct +
                             " distinct key(s). Two actors then answer to one number and every command, hit and " +
                             "settle naming it reaches whichever the map hands back first.";
            if (key("scene:0f8c2b1a-1111-4b7e-9c1d-000000000001") !=
                key("scene:0f8c2b1a-1111-4b7e-9c1d-000000000001"))
                yield return "L426 key-not-deterministic: the same address minted two different keys inside one " +
                             "process. Nothing about this address can be shared and the peers disagree about " +
                             "every key-less actor from the first frame.";

            // ── (b) THE RANGE — never 0, never a real GeoTacUnitId ───────────────────────────────────
            foreach (var k in new[] { key(""), key("scene:x"), key("spawn:y@0,0,0") })
                if (k >= 0)
                {
                    yield return "L426 key-not-negative: a content key (" + k + ") is 0 or positive. 0 means " +
                                 "'no shared identity' and positive means 'a real GeoTacUnitId', so this key " +
                                 "either vanishes or names a geoscape soldier.";
                    break;
                }

            // ── (c) THE TWO SCHEMES CANNOT MEET — THE POSITIVE CONTROL ───────────────────────────────
            // Host-assigned keys count down from -1. A content key inside that range is the collision the
            // ranges exist to make impossible, and it is exactly what a "simplification" back to a running
            // number would produce.
            foreach (var k in new[] { key("scene:x"), key("spawn:y@0,0,0"), key("scene:zzzzzzzz") })
                if (k > -keyBase)
                {
                    yield return "L426 content-key-in-the-host-range: a content key (" + k + ") is inside the " +
                                 "range host-assigned spawn keys occupy (-1 downwards). A mid-battle spawn and a " +
                                 "battle-start crate then answer to one number.";
                    break;
                }

            // ── (d) THE BUILDER DERIVES, AND IT DOES NOT COUNT ───────────────────────────────────────
            var calls = Program.Callees(build, asm).ToList();
            if (!calls.Any(c => c.Name == "ContentKeyOf"))
                yield return "L426 builder-does-not-derive: BuildBattleKeys never asks ContentKeyOf for a key, so " +
                             "the rule above is proven and unused and the battle-start address is back to " +
                             "whatever the builder invents — an ordinal, in every version of this file that had " +
                             "one.";
            foreach (var s in Program.StringRefs(build))
                if (s.IndexOf("ordinal", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    s.IndexOf("never an ordinal", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    yield return "L426 ordinal-is-back: BuildBattleKeys talks about ordinals again. The count of " +
                                 "actors a peer sees may not enter any key — one extra actor shifts every key " +
                                 "after it and each one silently names a different object.";
                    break;
                }

            // ── (e) A COLLISION IS LOUD, AND IT REFUSES ──────────────────────────────────────────────
            var built = Program.StringRefs(build).ToList();
            if (!built.Any(s => s.IndexOf("SAME derived key", StringComparison.Ordinal) >= 0))
                yield return "L426 collision-silent: BuildBattleKeys carries no sentence about two actors hashing " +
                             "to one key. A hash CAN collide; what it may not do is collide quietly, because the " +
                             "second actor then overwrites the first in the resolver and both records go to one " +
                             "of them for the rest of the battle.";
            if (!calls.Any(c => c.Name == "Refuse"))
                yield return "L426 collision-not-refused: BuildBattleKeys reports a collision without REFUSING the " +
                             "key, so one of the two actors still answers for both. A number that names two " +
                             "objects must name neither.";
        }
    }
}
