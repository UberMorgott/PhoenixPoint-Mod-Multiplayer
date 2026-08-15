using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L128 — A BASELINE IS A PROMISE ABOUT ONE LEVEL, AND EVERY HOST WRITE ACROSS A LEVEL BOUNDARY SHIPS.
    ///
    /// THE REPORT (2026-08-05): "we finished a tactical mission and came back to the geoscape. The site we
    /// fought at shows COMPLETED on the host; on the clients it is still an active quest site with the blue
    /// quest marker."
    ///
    /// IT WAS NOT A REPAINT AND IT WAS NOT THE SITE. The host's own log has the whole thing in fifteen
    /// frames:
    ///   f15953  `curtain Loaded→Playing` — the returned geoscape is live
    ///   f15954  `[MP][outcome] HOST mission reward seq=1 res=0 items=4` — GeoFactionReward.Apply has run,
    ///           i.e. ApplyOutcomes:463-474 already did `Site.ActiveMission = null` and DestroySite:846-867
    ///           already did `State = Destroyed`
    ///   f15969  `DiffEngine BASELINE: entities=3721 fields=23125 (no emit — clients share the save)`
    /// The baseline walk read the RETIRED site and declared it common knowledge. It was not: every peer had
    /// rebuilt its geoscape from the tactical transfer blob's embedded PRE-mission section
    /// (SaveTransferCoordinator:1598-1627), and a client never runs the campaign write at all —
    /// ClientMissionResultGate blocks GeoMission.Complete and leaves only CompleteSilently (law 3). So the
    /// host's entire mission outcome — site state, retired mission, and whatever else that transition wrote —
    /// entered the snapshot as the starting point and no later diff could ever disagree with it.
    ///
    /// THE PREMISE IS TIME-SCOPED AND THE CODE TREATED IT AS PERMANENT. "Clients share the save" is true at
    /// the INSTANT the boundary is taken. The walk that consumes it is a different event: HostTick bails
    /// while GeoLevel() is null, so on a co-op mission `_baselined` stays false from mission ENTRY across
    /// the WHOLE battle, and the first walk that finally runs is the post-return one — after the host has
    /// written the outcome. Nothing raced; the ordering is structural and the swallow was total.
    ///
    /// THE ARMS:
    ///   (a) <c>baseline-outlives-its-level</c> — EXECUTED against the real predicate. Same level → baseline
    ///       (a session starting on a live geoscape genuinely is what the joiners downloaded). Different
    ///       level → EMIT. No level at the boundary → EMIT, because a promise about nothing is not a promise.
    ///   (b) <c>boundary-forgets-the-clients-roots</c> — the reload boundary must NOT clear `_prevRoots`.
    ///       Values are only half the loss: a mission going non-null→null is a STRUCTURAL destroy derived
    ///       from that set, and re-seeding it from the post-transition walk means the destroy for
    ///       `S#&lt;id&gt;.SerializationData.ActiveMission` is never emitted — gone on the host, live forever on
    ///       every client, and the CRC backstop can only NAME that divergence (DiffEngine:529-537), never
    ///       heal it. That is what keeps the blue wrapper on screen even once the values are fixed.
    ///   (c) <c>session-keeps-a-foreign-root-set</c> — the other direction: full session teardown MUST clear
    ///       it, or the next session diffs its level against a previous campaign's roots.
    ///
    /// Falsify: make <c>BaselineIsHonest</c> return true unconditionally → <c>baseline-outlives-its-level</c>
    /// (verified red 2026-08-05: "still baselines a walk on a DIFFERENT level"); put
    /// <c>_prevRoots.Clear()</c> back into <c>ResetForReloadBoundary</c> →
    /// <c>boundary-forgets-the-clients-roots</c>; drop it from <c>Reset</c> →
    /// <c>session-keeps-a-foreign-root-set</c>.
    /// </summary>
    internal static class L128_BoundaryBaselineOwnsItsLevel
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var engine = typeof(DiffEngine);
            var honest = engine.GetMethod("BaselineIsHonest", All);
            var boundary = engine.GetMethod("ResetForReloadBoundary", All);
            var reset = engine.GetMethod("Reset", All);
            var prevRoots = engine.GetField("_prevRoots", All);
            var seeded = engine.GetField("_rootsSeeded", All);

            if (honest == null || boundary == null || reset == null || prevRoots == null || seeded == null)
            {
                yield return "L128 premise-changed: one of DiffEngine.{BaselineIsHonest,ResetForReloadBoundary," +
                             "Reset,_prevRoots,_rootsSeeded} no longer resolves. The baseline/boundary seam has " +
                             "moved and this law is asserting something about a shape the rail no longer has — " +
                             "re-read it before assuming a post-mission return still ships the host's outcome.";
                yield break;
            }

            // ── (a) the decision, executed ───────────────────────────────────
            Func<int, int, bool> ask = (b, w) => (bool)honest.Invoke(null, new object[] { b, w });

            if (!ask(4242, 4242))
                yield return "L128 baseline-never-taken: BaselineIsHonest answers FALSE for a walk on the very " +
                             "level the boundary was taken on. That case IS the honest one — a session starting " +
                             "on a live geoscape ships its own save to every joiner — and refusing it turns each " +
                             "session start into a whole-graph emit for state everyone already has.";
            if (ask(4242, 9001))
                yield return "L128 baseline-outlives-its-level: BaselineIsHonest still baselines a walk on a " +
                             "DIFFERENT level than the boundary was taken on. That is the post-mission geoscape " +
                             "return: the clients rebuilt from the PRE-mission section of the transfer blob, the " +
                             "host then completed the mission during the load (GeoLevelController:694-711), and " +
                             "this walk records the retired site as common knowledge. The host reads COMPLETED, " +
                             "every client keeps an active quest site, and no later diff can ever disagree.";
            if (ask(0, 9001))
                yield return "L128 baseline-on-no-level: BaselineIsHonest baselines a boundary that was taken with " +
                             "no level at all. There was nothing to promise the clients they share, so the only " +
                             "safe reading of that walk is a delta. This is the COMMON row, not a corner case: " +
                             "SaveTransferCoordinator.PrepareEntryFromBlobCrt:1926 takes the boundary before it " +
                             "reads the save metadata, so every load entry stamps #0 — and a tactical return " +
                             "does too, which is why 'adopt the first walked level' is not an optimisation but " +
                             "the 2026-08-05 defect with extra steps.";

            // ── (b)+(c) the structural half: whose root set is the reference ──
            if (Program.ReadsField(boundary, prevRoots) || Program.ReadsField(boundary, seeded))
                yield return "L128 boundary-forgets-the-clients-roots: ResetForReloadBoundary clears _prevRoots / " +
                             "_rootsSeeded again. The clients still hold the roots of the level the boundary was " +
                             "taken on, so that set is the only honest reference the deferred first walk has; " +
                             "dropping it makes StructuralDiff re-seed from the POST-transition walk instead " +
                             "(`if (!_rootsSeeded) { SeedRoots(); return; }`) and the destroy for a mission the " +
                             "host retired on the way back — S#<id>.SerializationData.ActiveMission — is never " +
                             "emitted. The client keeps the mission object, and with it the blue wrapper " +
                             "GeoSiteVisualsController.RefreshMissionVisuals:620 draws off it, forever.";
            if (!Program.ReadsField(reset, prevRoots) || !Program.ReadsField(reset, seeded))
                yield return "L128 session-keeps-a-foreign-root-set: DiffEngine.Reset no longer clears _prevRoots / " +
                             "_rootsSeeded. Keeping the set across a RELOAD is the fix; keeping it across a full " +
                             "session teardown diffs the next session's level against a previous campaign's roots, " +
                             "which emits creates and destroys for entities the new peers never had.";
        }
    }
}
