using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L475 — A RESEARCH COMPLETION IS NEVER SILENTLY LOST.
    ///
    /// RE-EXPRESSED 2026-08-15 (§A.11), NOT RELAXED. This law used to assert the opposite-looking
    /// property: that the campaign-opening grant list is SWALLOWED until this peer's first geoscape map
    /// surface (a sticky <c>ResearchSync._everOnMapSurface</c> flag), because on 2026-08-14 a client that
    /// sat in the intro cutscene while the host granted six researches got six back-to-back "research
    /// completed" modals on the first frame of the map. The swallow was a family carving itself out of
    /// the ordering system. Under the window journal the presentation ORDER and the MOMENT of
    /// presentation belong to the host-minted position plus this peer's own cursor, so the backlog is
    /// quiet without anything being dropped — and the latch itself is no longer allowed to drop.
    ///
    /// So the SUBJECT did not move and the invariant got STRICTLY STRONGER: it was "a completion is lost
    /// only in this one named case", it is now "a completion is never lost, in any case". The law was
    /// strengthened to match. It was not deleted and no arm was removed to make a diff pass — the arms
    /// that watched the flag now watch for the flag's RETURN.
    ///
    /// NOT A QUORUM: nothing here reads another peer, a roster, or an acknowledgement.
    ///
    /// THE ARMS (all executed against the shipped latch, never read off its IL alone):
    ///   (a) <c>swallow-survives</c> — the <c>_everOnMapSurface</c> field must not exist. A reflective
    ///       check, because the swallow is a FIELD plus one early return: deleting the return and keeping
    ///       the field would leave the next regression one line away.
    ///   (b) <c>completion-dropped-off-surface</c> — a completion arriving with NO map surface must be
    ///       APPENDED. This is the arm that replaces the old swallow, in the opposite direction.
    ///   (c) <c>completion-dropped-on-surface</c> — and one arriving WITH a map surface must be appended
    ///       too, or the law would pass against a latch that accepts nothing at all.
    ///   (d) <c>gate-unwired</c> — IL: <c>PresentFromMirror</c> must still reach the deferral list through
    ///       <c>LatchCompletion</c>. One door in, so no second path can grow its own drop rule.
    ///
    /// Falsify: re-add <c>_everOnMapSurface</c> and its early return → (a)+(b); make
    /// <c>LatchCompletion</c> return false → (b)+(c); enqueue straight into <c>_deferredCompleted</c>
    /// from <c>PresentFromMirror</c> → (d).
    /// </summary>
    internal static class L475_TheOpeningGrantListIsSilentUntilTheFirstMapSurface
    {
        internal static IEnumerable<string> Check()
        {
            // THE INVARIANT IS STRICTLY STRONGER UNDER THE JOURNAL. It used to be "a completion latched
            // before this peer's first map surface is swallowed on purpose, and only then". It is now
            // "a completion is NEVER lost" — the swallow is gone (§A.11) and every completion is appended.
            var sync = typeof(ResearchSync);
            const BindingFlags Any = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public |
                                     BindingFlags.DeclaredOnly;
            if (sync.GetField("_everOnMapSurface", Any) != null)
                yield return "L475 swallow-survives: ResearchSync._everOnMapSurface still exists. A " +
                             "completion latched before this peer's first map surface used to be dropped " +
                             "silently; under the journal it is APPENDED like every other family, and " +
                             "presentation is decided by the cursor plus the open-screen hold. No family " +
                             "bypasses the journal (§A.11).";

            var latch = sync.GetMethod("LatchCompletion", Any);
            var present = sync.GetMethod("PresentFromMirror", Any);
            if (latch == null || present == null)
            {
                yield return "L475 premise-changed: ResearchSync.LatchCompletion / .PresentFromMirror did " +
                             "not resolve, so the one door into the deferral list has moved and this law " +
                             "is blind. Re-point it before believing any verdict above.";
                yield break;
            }

            // EXECUTED, both directions, so the law cannot be satisfied by a method that always drops.
            ResearchSync.ResetForReloadBoundary();
            if (!ResearchSync.LatchCompletion("PX_TestResearch_A", onMapSurfaceNow: false) ||
                ResearchSync.DeferredCompletionCount != 1)
                yield return "L475 completion-dropped-off-surface: a completion arriving with NO map " +
                             "surface was refused (deferred=" + ResearchSync.DeferredCompletionCount +
                             "). This is the arm that replaces the old swallow: it must be appended, not " +
                             "dropped — the invariant 'a completion is never silently lost' is stronger " +
                             "under the journal, so the law got stronger too.";
            if (!ResearchSync.LatchCompletion("PX_TestResearch_B", onMapSurfaceNow: true) ||
                ResearchSync.DeferredCompletionCount != 2)
                yield return "L475 completion-dropped-on-surface: a completion arriving WITH a map surface " +
                             "was refused (deferred=" + ResearchSync.DeferredCompletionCount + "). Without " +
                             "this direction the law would pass against a latch that accepts nothing at all.";
            ResearchSync.ResetForReloadBoundary(); // leave no test rows in the real deferral list

            // (d): the one door must still be the presenter's door, not merely present in the file.
            if (!References(present, latch))
                yield return "L475 gate-unwired: PresentFromMirror does not call LatchCompletion — so " +
                             "something else is putting rows into the deferral list, past the one door, " +
                             "and that second path is free to grow its own silent drop rule. The whole " +
                             "point of the re-expression is that there is exactly one entrance and it " +
                             "loses nothing.";
        }

        /// <summary>Does <paramref name="m"/>'s IL mention <paramref name="callee"/>? Raw 4-byte metadata
        /// token scan (same shape as L107).</summary>
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
