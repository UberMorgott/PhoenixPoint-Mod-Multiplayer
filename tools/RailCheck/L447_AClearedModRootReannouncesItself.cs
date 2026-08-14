using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L447 — A MOD ROOT THAT EMPTIES ITSELF AT A RELOAD BOUNDARY MUST RE-ANNOUNCE ITSELF.
    ///
    /// The mod-root contract (IdentityResolver.cs:205-206) says a mod root must be EMPTY at every reload
    /// boundary, and <c>AssignSync.ResetForReloadBoundary</c> obeys it. Obeying it is only half the job, and
    /// the missing half is silent:
    ///   1. <c>SyncEngine.ResetForReloadBoundary</c> clears "M#assign" (SyncEngineStub.cs:276).
    ///   2. The next tick re-projects the WHOLE table one line before the walk (SyncEngineStub.cs:157-159).
    ///   3. If that walk's baseline is HONEST — same level, i.e. a save transfer taken on a live geoscape,
    ///      which is every mid-session JOIN — <c>DiffEngine</c> records the refilled table as "the clients
    ///      already have this" and emits NOTHING (DiffEngine.cs:1000-1009).
    ///   4. The client's transferred save carries no TFTV ModData, so its own cleared table never refills.
    /// Result: the ASSIGNMENTS board is frozen on that peer for the rest of the campaign, with the only
    /// evidence being one "CLIENT is holding an EMPTY \"M#assign\" mirror" line — observed 2026-08-14
    /// (multiplayer-prev.log:1268 BASELINE → multiplayer-2.log:107 hold).
    ///
    /// THE ARM CANNOT LIVE IN THE RESET. <c>SyncEngine.ResetForReloadBoundary</c> runs AssignSync's reset
    /// BEFORE <c>DiffEngine.ResetForReloadBoundary</c>, and that method clears <c>_forcePrefixes</c> — a
    /// <c>ForceReemit</c> issued from inside the reset is wiped the same frame. So the arm is a latch set in
    /// the reset and consumed by the first HOST projection after it, which runs strictly later.
    ///
    /// ARMS
    ///   (a) <c>arm-never-set</c> — the reset must set the latch.
    ///   (b) <c>arm-never-consumed</c> — the host projection must read it AND reach
    ///       <c>DiffEngine.ForceReemit</c>.
    ///   (c) <c>arm-inside-the-reset</c> — the reset must NOT call <c>ForceReemit</c> itself; that is the
    ///       obvious one-liner, and it is the one that silently does nothing.
    ///   (d) <c>boundary-order-changed</c> — the premise of (c): AssignSync's reset really does precede
    ///       DiffEngine's in <c>SyncEngine.ResetForReloadBoundary</c>, and DiffEngine's really does touch
    ///       <c>_forcePrefixes</c>. If either stops being true, (c) is asserting a hazard that no longer
    ///       exists and the law must be re-argued rather than left green.
    ///   (e) <c>armed-prefix-dropped-by-a-baseline</c> — the fix leans on <c>DiffAndEmit</c>'s baseline arm
    ///       keeping an armed prefix alive (DiffEngine.cs:1006-1008). If that early return stops reading
    ///       <c>_forcePrefixes</c> or stops re-flushing, the re-emit is swallowed by the very walk it exists
    ///       to survive and every arm above stays green.
    ///
    /// Falsify: delete `_reemitPending = true` → (a); delete the consume line in <c>HostProject</c> → (b);
    /// move the <c>ForceReemit</c> back into <c>ResetForReloadBoundary</c> → (c); drop the
    /// `if (_forcePrefixes.Count > 0) FlushNow();` from the baseline return → (e).
    /// </summary>
    internal static class L447_AClearedModRootReannouncesItself
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var asm = typeof(AssignSync).Assembly;
            var sync = typeof(AssignSync);
            var diff = typeof(DiffEngine);
            var reset = sync.GetMethod("ResetForReloadBoundary", All);
            var project = sync.GetMethod("HostProject", All);
            var latch = sync.GetField("_reemitPending", All);
            var forceReemit = diff.GetMethod("ForceReemit", All);
            var flushNow = diff.GetMethod("FlushNow", All);
            var diffReset = diff.GetMethod("ResetForReloadBoundary", All);
            var diffAndEmit = diff.GetMethod("DiffAndEmit", All);
            var forcePrefixes = diff.GetField("_forcePrefixes", All);
            var engineBoundary = typeof(SyncEngine).GetMethod("ResetForReloadBoundary", All);

            if (reset == null || project == null || latch == null || forceReemit == null || flushNow == null ||
                diffReset == null || diffAndEmit == null || forcePrefixes == null || engineBoundary == null)
            {
                yield return "L447 premise-changed: the mod-root re-announce seam no longer resolves " +
                             "(AssignSync.ResetForReloadBoundary / HostProject / _reemitPending, " +
                             "DiffEngine.ForceReemit / FlushNow / ResetForReloadBoundary / DiffAndEmit / " +
                             "_forcePrefixes, or SyncEngine.ResetForReloadBoundary). Re-point this law at " +
                             "whatever re-announces a cleared mod root now; do not delete it — the defect it " +
                             "holds is a board that freezes on one peer with a single log line as evidence";
                yield break;
            }

            // ═══ (a) the latch is armed by the boundary ═══
            if (!Program.FieldRefs(reset, OpCodes.Stsfld)
                    .Any(f => f.MetadataToken == latch.MetadataToken && f.Module == latch.Module))
                yield return "L447 arm-never-set: AssignSync.ResetForReloadBoundary clears the \"M#assign\" " +
                             "table without arming the re-announce. The next host tick refills it before the " +
                             "walk, an honest baseline records it as already-delivered, and every client's " +
                             "ASSIGNMENTS board stays frozen on the table it loaded.";

            // ═══ (b) and consumed by the projection that refills the table ═══
            if (!Program.ReadsField(project, latch))
                yield return "L447 arm-never-consumed: AssignSync.HostProject never reads _reemitPending, so " +
                             "the boundary arms a re-announce nothing ever fires.";
            if (!Program.Callees(project, asm).Any(m => m.MetadataToken == forceReemit.MetadataToken))
                yield return "L447 arm-never-consumed: AssignSync.HostProject no longer reaches " +
                             "DiffEngine.ForceReemit. The re-projected table is then only ever offered to the " +
                             "value diff, which has nothing to disagree with after a baseline.";

            // ═══ (c) and NOT from inside the reset, where it is wiped the same frame ═══
            if (Program.Callees(reset, asm).Any(m => m.MetadataToken == forceReemit.MetadataToken))
                yield return "L447 arm-inside-the-reset: AssignSync.ResetForReloadBoundary calls " +
                             "DiffEngine.ForceReemit directly. SyncEngine runs it BEFORE " +
                             "DiffEngine.ResetForReloadBoundary, which clears _forcePrefixes — the prefix is " +
                             "dropped in the same frame it was armed and the fix reads as present while doing " +
                             "nothing.";

            // ═══ (d) the premise of (c), executed rather than asserted in prose ═══
            var order = Program.CalleeSequence(engineBoundary);
            int assignAt = order.FindIndex(m => m.MetadataToken == reset.MetadataToken &&
                                                m.Module == reset.Module);
            int diffAt = order.FindIndex(m => m.MetadataToken == diffReset.MetadataToken &&
                                              m.Module == diffReset.Module);
            if (assignAt < 0 || diffAt < 0 || assignAt > diffAt)
                yield return "L447 boundary-order-changed: SyncEngine.ResetForReloadBoundary no longer runs " +
                             "AssignSync's boundary reset before DiffEngine's (assign=" + assignAt +
                             ", diff=" + diffAt + "). Arm (c) forbids the direct ForceReemit only because " +
                             "DiffEngine's reset comes later and wipes it — re-argue the ordering before " +
                             "changing either.";
            if (!Program.ReadsField(diffReset, forcePrefixes))
                yield return "L447 boundary-order-changed: DiffEngine.ResetForReloadBoundary no longer touches " +
                             "_forcePrefixes, so the hazard arm (c) names may be gone. Re-argue this law " +
                             "rather than leaving it asserting a wipe that no longer happens.";

            // ═══ (e) and the baseline arm the whole fix rides on ═══
            if (!Program.ReadsField(diffAndEmit, forcePrefixes) ||
                !Program.Callees(diffAndEmit, asm).Any(m => m.MetadataToken == flushNow.MetadataToken))
                yield return "L447 armed-prefix-dropped-by-a-baseline: DiffEngine.DiffAndEmit's baseline return " +
                             "no longer keeps an armed forced prefix alive (read _forcePrefixes + FlushNow). " +
                             "A ForceReemit that races the post-boundary baseline then ships nothing at all, " +
                             "which is the exact swallow this law exists to prevent.";
        }
    }
}
