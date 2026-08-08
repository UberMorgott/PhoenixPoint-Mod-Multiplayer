using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Tactical;

namespace RailCheck
{
    /// <summary>
    /// L323 — A REPAINT THAT FAILED TRANSIENTLY IS EVENTUALLY APPLIED, NEVER SILENTLY DROPPED.
    ///
    /// THE REPORT (2026-08-08, client-2, 15:10:06.928, one line and one line only):
    /// <c>[Multiplayer][tac] repaint of UIStateCharacterSelected threw — screen kept, part of it may be
    /// stale (failure #1): System.InvalidOperationException: Map is updating, cannot calculate path!</c>,
    /// six queued <c>Breakable … exploded</c> requests earlier. The screen was never repainted again. Law 11
    /// says a change lands on an already-open screen; that peer's screen kept the pre-explosion ability bar
    /// for the rest of the battle, and the whole trace was one WARN.
    ///
    /// WHY IT WAS A DROP RATHER THAN A CRASH. <c>ViewStateUpdatePatch.Postfix</c> clears <c>_dirty</c>
    /// BEFORE it repaints — correct, because the repaint is what the mark asked for — and then every catch
    /// in the file counted the failure through <c>FailureBeat</c> and returned. The mark was already spent,
    /// so nothing re-attempted: the counter made the failure VISIBLE and did nothing to make it RECOVERABLE.
    /// That is this repo's dominant bug shape wearing a rate-limit costume, and it is the second time this
    /// exact file has worn it (the <c>HashSet</c>-to-<c>Dictionary</c> change fixed the visibility half and
    /// left this half untouched).
    ///
    /// THE THROW IS TRANSIENT BY CONSTRUCTION, WHICH IS THE WHOLE POINT. <c>TiledNav.CalculatePath</c>:447-460
    /// refuses while <c>IsUpdatingNavMesh()</c> holds — it THROWS rather than returning a stale path — and
    /// that predicate (<c>TiledNav</c>:604-607) is nothing but <c>_buildNavCrt != null</c>, the handle of the
    /// <c>BuildNav</c> coroutine, taken at :553 and nulled at :572 on completion. The identical repaint a few
    /// frames later succeeds. The engine's own code says so in the plainest possible way, twice:
    /// <c>TacticalLevelController</c>:493-497 and <c>TacticalMap</c>:275-279 both run
    /// <c>while (Map.INavMesh.IsUpdatingNavMesh()) yield return NextUpdate.NextFrame;</c> before touching
    /// anything that paths. So the fix defers on the ENGINE'S OWN SETTLE SIGNAL rather than on a timer, and
    /// adds no pump — the flush is already a per-frame postfix on <c>TacticalViewState.Update</c>.
    ///
    /// IT IS A CLASS, NOT A SCREEN, and that is why this law does not mention <c>UIStateCharacterSelected</c>
    /// once in its assertions. <c>UIStateShoot</c> was merely the state that happened to be open. BOTH
    /// repaint arms reach the pathfinder:
    ///   • <c>Repaint</c>'s Exit+Enter runs the state's own <c>EnterState</c>
    ///     (<c>UIStateCharacterSelected</c> → <c>SelectCharacter</c> → its move/shoot target refresh), and
    ///   • <c>RepaintModules</c>' <c>UIModuleAbilities.SetAbilities</c>:130 asks <c>IsEnabled()</c> →
    ///     <c>TacticalAbility.GetDisabledState</c>:464 → <c>MoveAbility.HasValidTargets</c>:26 →
    ///     <c>GetTargetsData</c> → <c>MoveAbilityTargets.CacheTargets</c>:17-27 →
    ///     <c>TacticalPathRequest.Calculate()</c>.
    /// So the deferral sits at the ONE seam all arms route through (above <c>_dirty = false</c>), and the
    /// re-arm is asserted on all THREE catches — including <c>RepaintContainerView</c>, which does not path
    /// but drops its mark exactly the same way for any other transient.
    ///
    /// THE LAW ASSERTS THE OUTCOME, NOT THE CALL. Arm (c) is the load-bearing one: it is not enough that the
    /// catches call a reporter, the REPORTER MUST ACTUALLY TOUCH <c>_dirty</c>. A green law over a
    /// <c>NoteRepaintFailure</c> whose re-arm line had been deleted would be precisely the "law green through
    /// the bug it names" failure this catalog keeps recording. <c>Program.ReadsField</c> is opcode-blind (it
    /// matches any <c>InlineField</c> reference), which is stated rather than hidden: the assertion it
    /// actually makes is "the reporter references <c>_dirty</c> at all", and the only reason that method has
    /// to name that field is the re-arm.
    ///
    /// AND THE RETRY IS BOUNDED, arm (e). A repaint that throws for a NON-transient reason must not
    /// Exit+Enter forever — the state's <c>ExitState</c> teardown would run every frame — so the budget is
    /// consecutive failures, cleared by the next success, and the giving-up is audible. "Retried forever"
    /// and "dropped on the first throw" are both wrong; the law pins the middle.
    ///
    /// Falsify: delete the <c>_dirty</c> re-arm from <c>NoteRepaintFailure</c> → <c>failure-dropped</c>;
    /// revert any catch to a bare <c>FailureBeat</c> → <c>arm-drops-its-failure</c> naming that arm; delete
    /// the <c>NavMeshUpdating</c> guard from the flush → <c>settle-unasked</c>; stop asking the engine's flag
    /// inside <c>NavMeshUpdating</c> → <c>settle-signal-unread</c>; let the game stop gating
    /// <c>CalculatePath</c> on <c>IsUpdatingNavMesh</c> → <c>settle-signal-gone</c>; remove the retry ceiling
    /// → <c>retry-unbounded</c>; give the control the real reporter's body → <c>control-not-red</c>.
    /// </summary>
    internal static class L323_ATransientRepaintFailureIsRetried
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>POSITIVE CONTROL for arm (c). A reporter that plainly does NOT re-arm — no mention of
        /// <c>_dirty</c> anywhere in its body. If the same test that arm (c) runs answers "re-arms" for
        /// this, the test is worthless and arm (c) is decoration.</summary>
        private static class FakeReporter
        {
            internal static int NoteRepaintFailure(string key) { return key == null ? 0 : 1; }
        }

        internal static IEnumerable<string> Check(Assembly game)
        {
            var repaint = typeof(TacticalUiRepaint);
            var mod = repaint.Assembly;

            var flush = repaint.GetNestedType("ViewStateUpdatePatch", All)?.GetMethod("Postfix", All);
            var navUpdating = repaint.GetMethod("NavMeshUpdating", All);
            var noteFailure = repaint.GetMethod("NoteRepaintFailure", All);
            var dirty = repaint.GetField("_dirty", All);
            var ceiling = repaint.GetField("RepaintRetryCeiling", All);

            var tiledNav = AccessTools.TypeByName("Base.Levels.Nav.Tiled.TiledNav");
            var calcPath = tiledNav?.GetMethod("CalculatePath", All);
            var isUpdating = AccessTools.TypeByName("Base.Levels.Nav.INavMesh")?.GetMethod("IsUpdatingNavMesh", All);

            if (flush == null || navUpdating == null || noteFailure == null || dirty == null ||
                tiledNav == null || calcPath == null || isUpdating == null)
            {
                yield return "L323 premise-changed: TacticalUiRepaint.{ViewStateUpdatePatch.Postfix," +
                             "NavMeshUpdating,NoteRepaintFailure,_dirty}, or Base.Levels.Nav.Tiled.TiledNav" +
                             ".CalculatePath, or INavMesh.IsUpdatingNavMesh, no longer resolves. The tactical " +
                             "repaint or the engine's navmesh settle signal has moved, and this law is " +
                             "asserting recovery over a shape that is gone — re-read both before trusting " +
                             "that a failed repaint still comes back.";
                yield break;
            }

            // ── (a) the engine still refuses to path while the map rebuilds ────────────────
            // If this stops holding, the deferral below is waiting on a flag that no longer gates anything.
            if (!Program.Callees(calcPath, game).Any(c => c.Name == "IsUpdatingNavMesh"))
                yield return "L323 settle-signal-gone: TiledNav.CalculatePath:447-460 no longer consults " +
                             "IsUpdatingNavMesh, so \"Map is updating, cannot calculate path!\" is no longer " +
                             "gated on the flag the repaint defers on. Either the throw is gone (drop the " +
                             "deferral) or it now has a DIFFERENT trigger (defer on that one) — but deferring " +
                             "on a flag that no longer guards the throw is a wait that protects nothing.";

            // ── (b) our deferral asks that same signal, and the flush actually asks our deferral ──
            if (!Program.Callees(navUpdating, game).Any(c => c.Name == "IsUpdatingNavMesh"))
                yield return "L323 settle-signal-unread: TacticalUiRepaint.NavMeshUpdating never calls " +
                             "INavMesh.IsUpdatingNavMesh, so it is answering some other question than \"has " +
                             "the map settled\". The engine's own wait (TacticalLevelController:493-497, " +
                             "TacticalMap:275-279) polls exactly that predicate; anything else is a guess " +
                             "dressed as a settle signal.";
            if (!Program.Callees(flush, mod).Any(c => c.MetadataToken == navUpdating.MetadataToken &&
                                                      c.Module == navUpdating.Module))
                yield return "L323 settle-unasked: the tactical flush (ViewStateUpdatePatch.Postfix) never " +
                             "calls NavMeshUpdating, so a repaint is ATTEMPTED while the navmesh rebuilds. " +
                             "That is the reported defect verbatim: the Exit+Enter throws halfway through " +
                             "TacticalPathRequest.Calculate, the screen is left half-entered, and the mark " +
                             "was already spent one line earlier.";

            // ── (c) THE OUTCOME — the reporter re-arms the mark, it does not merely count ───
            if (!Program.ReadsField(noteFailure, dirty))
                yield return "L323 failure-dropped: NoteRepaintFailure never references _dirty, so it COUNTS " +
                             "the failure and lets the mark die with it — a visible failure that is still " +
                             "unrecoverable, which is exactly the state this bug was found in (one WARN, " +
                             "'failure #1', and a screen that stayed stale for the rest of the battle). " +
                             "Counting is not retrying.";

            // ── (d) every repaint arm routes its failure through that reporter ─────────────
            // Named one by one: this is a CLASS (any repaint touching path/move targets during a navmesh
            // rebuild), and an arm reverted to a bare FailureBeat drops its batch exactly as before.
            foreach (var name in new[] { "Repaint", "RepaintModules", "RepaintContainerView" })
            {
                var arm = repaint.GetMethod(name, All);
                if (arm == null)
                {
                    yield return "L323 premise-changed: TacticalUiRepaint." + name + " no longer resolves — a " +
                                 "repaint arm this law quantifies over has been renamed or removed. Re-read " +
                                 "the file: the law can no longer tell whether that arm recovers.";
                    continue;
                }
                if (!Program.Callees(arm, mod).Any(c => c.MetadataToken == noteFailure.MetadataToken &&
                                                        c.Module == noteFailure.Module))
                    yield return "L323 arm-drops-its-failure: TacticalUiRepaint." + name + " does not report " +
                                 "through NoteRepaintFailure, so a throw inside it counts and vanishes. " +
                                 (name == "RepaintModules"
                                     ? "This arm is in the navmesh class too, not just Repaint: SetAbilities:130 " +
                                       "-> IsEnabled() -> GetDisabledState:464 -> MoveAbility.HasValidTargets:26 " +
                                       "reaches TacticalPathRequest.Calculate the same way the Exit+Enter does."
                                     : "Every arm re-arms or the class is only half fixed — the screen that " +
                                       "happened to be open when it threw is not the only one that can.");
            }

            // ── (e) the retry is BOUNDED and the giving-up is audible ──────────────────────
            var ceilingValue = ceiling == null ? 0 : Convert.ToInt32(ceiling.GetRawConstantValue());
            if (ceiling == null || ceilingValue <= 0)
                yield return "L323 retry-unbounded: TacticalUiRepaint.RepaintRetryCeiling is missing or <= 0, " +
                             "so a repaint that throws for a NON-transient reason is re-armed forever and " +
                             "Exit+Enters the state every frame — running its ExitState teardown every frame, " +
                             "which is worse than the stale pixel it was chasing. 'Retried forever' and " +
                             "'dropped on the first throw' are both wrong; the budget is consecutive failures, " +
                             "cleared by the next success, and the drop must say so.";

            // ── (f) POSITIVE CONTROL: arm (c)'s test can actually go red ───────────────────
            var fake = typeof(FakeReporter).GetMethod("NoteRepaintFailure", All);
            if (fake == null || Program.ReadsField(fake, dirty))
                yield return "L323 control-not-red: the re-arm test answers 'yes' for FakeReporter, whose body " +
                             "does not mention _dirty at all. Arm (c) therefore proves nothing about the real " +
                             "reporter and this whole law is decoration — fix the test before trusting the " +
                             "green.";
        }
    }
}
