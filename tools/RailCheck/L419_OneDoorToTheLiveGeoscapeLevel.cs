using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Base.Core;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Levels;

namespace RailCheck
{
    /// <summary>
    /// L419 — ONE DOOR TO THE LIVE GEOSCAPE LEVEL, AND IT ASKS UNITY'S QUESTION.
    ///
    /// THE FAILURE (2c4af73). Twenty-two files each carried their own private <c>GeoLevel()</c> accessor over
    /// <c>GameUtl.CurrentLevel()</c>. Nineteen wrote <c>level == null ? null : level.GetComponent&lt;…&gt;()</c>;
    /// three — MarketplaceSync, ReplenishSync, TradeSync — had drifted to
    /// <c>CurrentLevel()?.GetComponent&lt;…&gt;()</c>, which is NOT the same question.
    /// <c>level == null</c> runs <c>UnityEngine.Object.op_Equality</c>, which answers "the native half is
    /// gone" for a DESTROYED Level; <c>?.</c> is a plain managed reference test, sees the husk as alive and
    /// calls <c>GetComponent</c> on a dead object. That is precisely the hazard
    /// <see cref="L113_UnityIdentityEquality"/> exists for, arriving through a copy nobody re-read.
    ///
    /// WHY A LAW AND NOT THE DELETION ALONE. The deletion is done; what a law adds is that the copies cannot
    /// come BACK. Twenty-two identical private helpers is not a thing anyone writes on purpose — it is what
    /// twenty-two authors each doing the obvious local thing produces, and the twenty-third will do it again.
    /// Three of twenty-two had already drifted before anyone looked.
    ///
    /// THE ARMS:
    ///   (a) <c>the-door-is-gone</c> — premise: <c>GenericApplier.GeoLevel</c> resolves as an internal static
    ///       returning <c>GeoLevelController</c>, and its IL really does ask <c>CurrentLevel</c> and
    ///       <c>GetComponent</c>. Without it every arm below is about a method that no longer exists.
    ///   (b) <c>the-door-asks-a-managed-question</c> — that one accessor's IL must call Unity's
    ///       <c>op_Equality</c>/<c>op_Inequality</c>. This is the drift itself, at the one place it now lives.
    ///   (c) POSITIVE CONTROL, EXECUTED — arm (b) is satisfied by a call the IL DOES contain, but its
    ///       falsifying shape is an absence, so the same predicate is run over a sentinel in this file written
    ///       with <c>?.</c> exactly as the three drifted copies were. It must come back as NOT asking Unity,
    ///       or (b) is a test that passes on both answers.
    ///   (d) <c>the-lookup-is-copied-again</c> — any OTHER method in the mod named <c>GeoLevel</c> must REACH
    ///       <c>GenericApplier.GeoLevel</c> and must not perform the lookup itself. Three survive on purpose
    ///       (EventPopup, GeoModalMirror, MissionOutcomeMirror) — they are try/catch wrappers, which is theirs
    ///       and not duplication — and this arm is what keeps them wrappers.
    ///
    /// WHAT THIS LAW DELIBERATELY DOES NOT CLAIM. "No method anywhere performs <c>CurrentLevel</c> →
    /// <c>GetComponent&lt;GeoLevelController&gt;</c>" is FALSE today and shipping it would mean an allowlist of
    /// ~20 inline sites — a debt list wearing a law's clothes, which is the shape this repo keeps in
    /// <c>vacuity-exempt.txt</c> and does not want more of. The narrow claim is the true one: the accessor is
    /// one, and the one asks Unity's question.
    ///
    /// Falsify: rewrite <c>GenericApplier.GeoLevel</c> with <c>?.</c> → (b); give any of the three wrappers
    /// its own <c>CurrentLevel().GetComponent&lt;…&gt;()</c> back → (d).
    /// </summary>
    internal static class L419_OneDoorToTheLiveGeoscapeLevel
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var mod = typeof(GenericApplier).Assembly;
            var door = typeof(GenericApplier).GetMethod("GeoLevel", All, null, Type.EmptyTypes, null);

            if (door == null || !door.IsStatic || door.ReturnType != typeof(GeoLevelController) ||
                !DoesLookup(door))
            {
                yield return "L419 the-door-is-gone: GenericApplier.GeoLevel() no longer resolves as a static " +
                             "returning GeoLevelController whose body asks GameUtl.CurrentLevel for a " +
                             "GeoLevelController. Twenty-two private copies of that lookup were folded into it " +
                             "and three of them had drifted to a managed null test on a Unity object; with the " +
                             "single door gone, both arms below are about nothing and the copies are free to " +
                             "come back one file at a time.";
                yield break;
            }

            // ── (c) POSITIVE CONTROL, before (b) is believed ────────────────────────
            var drifted = typeof(L419_OneDoorToTheLiveGeoscapeLevel).GetMethod("SentinelManagedNullTest", All);
            if (drifted == null || AsksUnity(drifted))
            {
                yield return "L419 premise-changed: POSITIVE CONTROL failed — the IL predicate reports this " +
                             "law's own SentinelManagedNullTest, which is written with `?.` by construction, as " +
                             "asking Unity's op_Equality. It therefore cannot tell the safe accessor from the " +
                             "drifted one, and arm (b) below would pass on both.";
                yield break;
            }

            // ── (b) THE ONE DOOR ASKS THE NATIVE HALF ───────────────────────────────
            if (!AsksUnity(door))
                yield return "L419 the-door-asks-a-managed-question: GenericApplier.GeoLevel no longer runs " +
                             "UnityEngine.Object's op_Equality on the Level it got back — it uses `?.`, a plain " +
                             "managed reference test. A destroyed Level's managed husk is not null, so this " +
                             "returns a live-looking GeoLevelController for a level whose native half is gone " +
                             "and every caller reaches into it. That is L113's hazard, and it is now behind ONE " +
                             "door instead of twenty-two, which is the only reason it is cheap to keep right.";

            // ── (d) NOBODY RE-IMPLEMENTS IT ─────────────────────────────────────────
            foreach (var t in mod.GetTypes())
            {
                if (t == typeof(GenericApplier)) continue;
                MethodInfo copy;
                try { copy = t.GetMethod("GeoLevel", All | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null); }
                catch { continue; }
                // Only the STRONGLY-TYPED family 2c4af73 consolidated. GeoRuntime.GeoLevel() returns object
                // and reaches the level entirely through AccessTools — it exists precisely so the early-load
                // path never names GeoLevelController, so it cannot call a method whose signature does.
                if (copy == null || copy.ReturnType != typeof(GeoLevelController)) continue;
                bool delegates = Program.Callees(copy, mod).Any(c => Same(c, door));
                if (delegates && !DoesLookup(copy)) continue;
                yield return "L419 the-lookup-is-copied-again: " + t.Name + ".GeoLevel() " +
                             (delegates ? "re-implements the CurrentLevel→GetComponent lookup beside its call to "
                                        : "does not go through ") +
                             "GenericApplier.GeoLevel. This method name had twenty-two bodies and three of them " +
                             "had silently drifted to `?.` on a Unity object — the copies are indistinguishable " +
                             "in review and only differ on a destroyed level, which is exactly the state nobody " +
                             "tests. A local wrapper is fine (EventPopup, GeoModalMirror and MissionOutcomeMirror " +
                             "each add their own try/catch); re-asking the question is not.";
            }
        }

        /// <summary>Does this body perform the lookup itself — <c>GameUtl.CurrentLevel</c> AND a
        /// <c>GetComponent</c> instantiated at <c>GeoLevelController</c>?</summary>
        private static bool DoesLookup(MethodBase m)
        {
            var seq = Program.CalleeSequence(m);
            return seq.Any(c => c != null && c.Name == "CurrentLevel" && c.DeclaringType == typeof(GameUtl)) &&
                   seq.Any(IsGeoGetComponent);
        }

        private static bool IsGeoGetComponent(MethodBase c)
        {
            if (c == null || c.Name != "GetComponent" || !c.IsGenericMethod) return false;
            try { return c.GetGenericArguments().Any(a => a == typeof(GeoLevelController)); }
            catch { return false; }
        }

        /// <summary>Does this body run Unity's own liveness comparison, rather than a managed reference
        /// test? <c>level == null</c> emits a call to <c>UnityEngine.Object.op_Equality</c>; <c>?.</c> emits a
        /// branch and no call at all.</summary>
        private static bool AsksUnity(MethodBase m) =>
            Program.CalleeSequence(m).Any(c => c != null && c.DeclaringType == typeof(UnityEngine.Object) &&
                                               (c.Name == "op_Equality" || c.Name == "op_Inequality"));

        /// <summary>ARM (c). The drifted shape, verbatim as MarketplaceSync/ReplenishSync/TradeSync carried it.
        /// Never called — it exists so arm (b)'s predicate has a known WRONG body to answer about.</summary>
        private static GeoLevelController SentinelManagedNullTest()
        {
            return GameUtl.CurrentLevel()?.GetComponent<GeoLevelController>();
        }

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;
    }
}
