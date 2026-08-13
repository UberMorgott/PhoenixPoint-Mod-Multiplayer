using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L428 — A CRC DIVERGENCE THAT CANNOT NAME ITS FIELD IS A REPORT NOBODY CAN ACT ON.
    ///
    /// THE REPORT (live 2026-08-13). <c>CRC backstop: root 'S#95' DIVERGED on peer 2 (host B1600030 !=
    /// client BD0D5BBC at quiescent seq 230)</c>, then the same hashes for peer 1 — both clients identical,
    /// the host the odd one out, on a root that had been quiescent for two minutes. The host force-re-emitted
    /// the subtree (115 entries / 9936 B) and both clients repainted, so the divergence was REAL and it was
    /// healed. And nothing in three log files can say WHAT was wrong: a report carries ONE hash for a whole
    /// subtree, the wire carries no per-field image of the client's mirror, and the walk that made the hash
    /// is gone by the time the line is printed. The defect therefore survives its own detection — which is
    /// how the same root can diverge again next session with nobody any wiser.
    ///
    /// THE ANSWER ALREADY LANDS ON THE CLIENT. <c>DiffEngine.HandleCrcReport</c> answers a divergence with
    /// <c>ForceReemit(rootKey)</c>, i.e. every entry of that subtree, ~150 ms after the client's own report
    /// went out. <c>GenericApplier.ApplyEntry</c> already asks <c>Unchanged</c> of every arriving entry, so
    /// the entry that does NOT match local bytes under the root we just reported IS the divergence. Naming it
    /// costs one string compare and no wire byte.
    ///
    /// WHAT IS ASSERTED:
    ///   (a) premise — the report/answer seam still exists (ClientCrcTick, HandleCrcReport→ForceReemit).
    ///   (b) the client RECORDS what it reported (<c>_crcReportedRoot</c> written by ClientCrcTick), or the
    ///       apply side has nothing to correlate against.
    ///   (c) the apply funnel CONSULTS it — ApplyEntry reaches NameTheDivergedField, which reads the record
    ///       and reports through the ordinary LogMissOnce funnel.
    ///   (d) the root test is WHOLE-SEGMENT: "S#9" must not claim "S#95"'s fields. A StartsWith here would
    ///       name fields of a root that was never reported, i.e. manufacture its own root cause — the exact
    ///       failure this repo has paid for before (DiffEngine.PrefixMatchOne carries the same rule).
    ///
    /// Falsify (each verified RED, then restored): make PathUnderRoot use StartsWith → <c>root-test-loose</c>;
    /// drop the NameTheDivergedField call from ApplyEntry → <c>answer-unread</c>; stop writing
    /// <c>_crcReportedRoot</c> in ClientCrcTick → <c>report-unrecorded</c>.
    /// </summary>
    internal static class L428_ADivergenceReportNamesItsField
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var applier = typeof(GenericApplier);
            var mod = applier.Assembly;
            var crcTick = applier.GetMethod("ClientCrcTick", All);
            var applyEntry = applier.GetMethod("ApplyEntry", All);
            var name = applier.GetMethod("NameTheDivergedField", All);
            var under = applier.GetMethod("PathUnderRoot", All);
            var reported = applier.GetField("_crcReportedRoot", All);
            var logMiss = applier.GetMethod("LogMissOnce", All);
            var handle = typeof(DiffEngine).GetMethod("HandleCrcReport", All);
            var force = typeof(DiffEngine).GetMethod("ForceReemit", All);

            if (crcTick == null || applyEntry == null || name == null || under == null || reported == null ||
                logMiss == null || handle == null || force == null ||
                under.ReturnType != typeof(bool) || under.GetParameters().Length != 2)
            {
                yield return "L428 premise-changed: one of GenericApplier.{ClientCrcTick,ApplyEntry," +
                             "NameTheDivergedField,PathUnderRoot,_crcReportedRoot,LogMissOnce} or " +
                             "DiffEngine.{HandleCrcReport,ForceReemit} no longer resolves. The naming seam " +
                             "correlates OUR report with the host's forced re-emit; if either half has moved, " +
                             "every arm below passes while checking nothing.";
                yield break;
            }

            // ── (a) the host still ANSWERS a divergence with the re-emit this seam listens for ──────────
            if (!Program.Callees(handle, mod).Any(c => c.MetadataToken == force.MetadataToken && c.Module == force.Module))
                yield return "L428 answer-not-sent: DiffEngine.HandleCrcReport no longer calls ForceReemit, so a " +
                             "diverged root is never re-stated and the client sees no entries to compare. The " +
                             "divergence would be neither healed nor nameable.";

            // ── (b) the client records WHAT it asked about ──────────────────────────────────────────────
            if (!Program.ReadsField(crcTick, reported))
                yield return "L428 report-unrecorded: GenericApplier.ClientCrcTick does not touch " +
                             "_crcReportedRoot. Without the root this peer just hashed, the arriving answer is " +
                             "indistinguishable from any other delta and the field stays unnamed.";

            // ── (c) the apply funnel consults it, and reports through the mirror-gap funnel ─────────────
            if (!Program.Callees(applyEntry, mod).Any(c => c.MetadataToken == name.MetadataToken && c.Module == name.Module))
                yield return "L428 answer-unread: ApplyEntry does not reach NameTheDivergedField, so the one place " +
                             "that can see the differing entry (the Unchanged gate has already answered it, for " +
                             "free) says nothing — a backstop that detects what nobody can fix.";
            if (!Program.ReadsField(name, reported) ||
                !Program.Callees(name, mod).Any(c => c.MetadataToken == logMiss.MetadataToken && c.Module == logMiss.Module))
                yield return "L428 answer-unread: NameTheDivergedField does not read _crcReportedRoot or does not " +
                             "report through LogMissOnce — the correlation is not made, or it is made and thrown away.";

            // ── (d) WHOLE-SEGMENT root test, executed — with its own positive control ───────────────────
            Func<string, string, bool> match = (p, r) => (bool)under.Invoke(null, new object[] { p, r });
            if (match("S#95.SerializationData", "S#9"))
                yield return "L428 root-test-loose: PathUnderRoot claims 'S#95.SerializationData' sits under root " +
                             "'S#9'. A prefix test that ignores segment boundaries names fields of a root this " +
                             "peer never reported — a diagnostic that manufactures its own root cause is worse " +
                             "than none (DiffEngine.PrefixMatchOne carries the same whole-segment rule).";
            if (!match("S#95.SerializationData.TacUnits", "S#95") || !match("V#1", "V#1"))
                yield return "L428 root-test-dead: PathUnderRoot answers FALSE for a path genuinely under the " +
                             "reported root (or for the root's own entries, which carry the bare root as their " +
                             "path). The seam would then never name anything and the law's other arms would pass " +
                             "over a silent diagnostic.";
            if (!FakeSeam.LooseMatch("S#95.SerializationData", "S#9"))
                yield return "L428 positive-control-dead: the arm above does not flag FakeSeam.LooseMatch, the " +
                             "plain StartsWith this test exists to forbid. An arm that cannot fail the " +
                             "known-broken rule proves nothing about the shipped one.";
        }

        /// <summary>The naive rule — a bare prefix compare, which claims every root whose key merely starts
        /// with the reported one. Present so the arm is proven able to fail.</summary>
        private static class FakeSeam
        {
            internal static bool LooseMatch(string path, string root) =>
                path != null && root != null && path.StartsWith(root, StringComparison.Ordinal);
        }
    }
}
