using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace RailCheck
{
    /// <summary>
    /// L311 — TWO DESTRUCTIBLES STANDING IN THE SAME PLACE STILL HAVE TWO ADDRESSES.
    ///
    /// THE REPORT (2026-08-08, both clients of one 3-instance battle, identical numbers). The battle indexed
    /// 1269 destructibles and said out loud: <c>358 destructible(s) stand within a tenth of a unit of another,
    /// so the position address cannot tell them apart. One of each pair is unreachable by that address</c>, and
    /// alongside it <c>9 destructible guid collision(s)</c>. Those two counts intersect: a guid collision costs
    /// an object its FIRST address (a collided guid is dropped on every peer, by design, because
    /// <c>SceneObjectIdsComponent.MergeWith</c>:29-34 replaces it with a fresh RANDOM one), and losing the cell
    /// to whichever object the walk reached first cost it the SECOND. An object with neither took the host's
    /// damage nowhere — the wall is rubble there and solid here, and nothing said so.
    ///
    /// THE 18 THAT WERE "NEVER INDEXED" WERE NEVER A THIRD HOLE. 1269 walked, 1251 guid-indexed, 9 collisions ×
    /// 2 objects each = the 18. The index log now states that arithmetic in its own line so the next sweep does
    /// not chase a gap that is already explained.
    ///
    /// WHY PRECISION WAS THE WRONG LEVER. A destructible's <c>transform</c> is its PREFAB PIVOT, and the game
    /// never locates one by it: <c>Destructable.Init</c>:647 reads <c>MeshRenderer.bounds</c>, :721 keeps
    /// <c>_gridOrigin = bounds.min</c>, :712-716 places one receiver GameObject per tile from
    /// <c>GridToWorld(bounds.min…)</c>. A kit-built wall set with one shared pivot puts every panel of a
    /// building at a BIT-IDENTICAL world point, so no number of decimal places separates them. Nor could the
    /// bounds replace the pivot as the address: <c>CheckAndDisableSelf</c>:254-256 destroys the MeshRenderer
    /// and Collider of a fully broken object, so a bounds-derived address evaporates precisely when the object
    /// is still present and still being addressed.
    ///
    /// SO THE DISCRIMINATOR IS THE WALK — the thing this arc already stakes everything on.
    /// <c>TacticalDestruction.AddressTags</c> mints <c>cell#i/n</c>: the rounded cell, the object's index among
    /// the n found there by <c>GetComponentsInChildrenStable</c> (<c>UnityUtil</c>:69-74, strict depth-first,
    /// own components then children in sibling order), and n itself. The hierarchy cannot shorten mid-mission —
    /// <c>CheckAndDisableSelf</c>:261 only <c>SetActive(false)</c>, <c>Breakable.DisableSelf</c>:374 only clears
    /// <c>enabled</c>, and an inactive child is still walked — so a tag assigned at battle start stays true.
    ///
    /// THIS LAW ASSERTS THE OUTCOME, NOT THE ARITHMETIC. Arm (a) is the whole point: n co-located objects get n
    /// DIFFERENT addresses. Arm (b) is the property a bare ordinal never had — the occupant count rides in the
    /// key, so a peer that found a different number in that cell matches NOTHING rather than confidently naming
    /// the wrong wall. Arm (c) keeps one cell's addresses out of another's. Arm (d) is the seam: the capture
    /// side must ship the tag the INDEX minted, because the index is the only thing that knows n — an address
    /// derived freshly at capture time is exactly the un-checkable one this replaces.
    ///
    /// Falsify (each verified RED, then restored): drop the <c>#i</c> from AddressTags → (a) ×3; drop the
    /// <c>/n</c> → (b) ×2; use ':' in place of '#' so a cell is a prefix of its own tags → (c); make
    /// OnEnvironmentDamage call <c>PosCell</c> again instead of the minted tag → (d) ×2.
    /// </summary>
    internal static class L311_ACoLocatedDestructibleStillHasItsOwnAddress
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var dest = typeof(Multiplayer.Tactical.TacticalCommandSync).Assembly
                .GetType("Multiplayer.Tactical.TacticalDestruction");
            var tags = dest?.GetMethod("AddressTags", All);
            var index = dest?.GetMethod("Index", All);
            var tagOf = dest?.GetMethod("TagOf", All);
            var posCell = dest?.GetMethod("PosCell", All);
            var capture = dest?.GetMethod("OnEnvironmentDamage", All);

            if (tags == null || index == null || tagOf == null || posCell == null || capture == null ||
                tags.GetParameters().Length != 2 || tags.ReturnType != typeof(string[]))
            {
                yield return "L311 premise-changed: TacticalDestruction.AddressTags(string,int)->string[], Index, " +
                             "TagOf, PosCell or OnEnvironmentDamage no longer resolves. The destructible address " +
                             "has been reshaped and every arm below would pass vacuously while a host's damage to " +
                             "one of two co-located walls lands on neither peer.";
                yield break;
            }

            Func<string, int, string[]> mint = (cell, n) => (string[])tags.Invoke(null, new object[] { cell, n });

            // ── (a) THE OUTCOME: co-located objects are TOLD APART ────────────────────
            foreach (var n in new[] { 2, 3, 7 })
            {
                var minted = mint("104,15,-88", n);
                if (minted.Length != n || minted.Distinct(StringComparer.Ordinal).Count() != n)
                    yield return "L311 co-located-share-one-address: " + n + " destructibles in one cell were given " +
                                 minted.Distinct(StringComparer.Ordinal).Count() + " distinct address(es), not " + n +
                                 ". That is the 2026-08-08 report verbatim — 358 of 1269 objects sharing a cell with " +
                                 "another, one of each pair unreachable — and for anything whose guid also collided " +
                                 "it means the host's damage to it is dropped on this peer with nothing to resolve.";
            }

            // ── (b) SELF-CHECKING: the occupant COUNT is part of the key ──────────────
            foreach (var pair in new[] { new[] { 2, 3 }, new[] { 1, 2 } })
            {
                var mine = mint("104,15,-88", pair[0]);
                var theirs = mint("104,15,-88", pair[1]);
                if (mine.Intersect(theirs, StringComparer.Ordinal).Any())
                    yield return "L311 address-not-self-checking: a cell holding " + pair[0] + " destructible(s) and " +
                                 "the same cell holding " + pair[1] + " produce addresses that MATCH. A peer whose " +
                                 "map really did differ would then resolve the host's address to whatever object " +
                                 "happens to sit at that index — the confident-wrong-wall failure the position " +
                                 "address exists to avoid. It must resolve nothing and say so instead.";
            }

            // ── (c) ONE CELL'S ADDRESSES ARE NOT ANOTHER'S ───────────────────────────
            if (mint("1,2,3", 2).Intersect(mint("1,2,30", 2), StringComparer.Ordinal).Any() ||
                mint("1,2,3", 2).Intersect(mint("1,2,3", 2).Select(t => t + "0"), StringComparer.Ordinal).Any())
                yield return "L311 cells-bleed-into-each-other: two DIFFERENT cells produced a shared address, so a " +
                             "separator that is not part of any coordinate has gone missing. Damage aimed at one " +
                             "wall would land on a different wall a tenth of a unit away.";

            // ── (d) THE SEAM: capture ships the tag the INDEX minted ─────────────────
            var mod = dest.Assembly;
            if (!Program.Callees(index, mod).Any(c => Same(c, tags)))
                yield return "L311 index-mints-nothing: TacticalDestruction.Index no longer calls AddressTags, so " +
                             "arms (a)-(c) prove a pure function nothing uses and the index is back to letting the " +
                             "first object the walk reached keep the whole cell.";
            var direct = Program.Callees(capture, mod).ToList();
            if (!direct.Any(c => Same(c, tagOf)) || direct.Any(c => Same(c, posCell)))
                yield return "L311 capture-derives-its-own-address: OnEnvironmentDamage no longer reads the tag the " +
                             "index assigned (or computes a bare cell of its own again). Only the index knows how " +
                             "many objects share that cell, so an address minted at capture time carries no count — " +
                             "it cannot be checked by the receiving peer, and arm (b) is void for every record on " +
                             "the wire.";
        }

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;
    }
}
