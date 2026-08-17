using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L559 — THE AGENDA STRIP'S NATIVE REBUILD IS THROTTLED, AND A THROTTLED REBUILD IS NEVER LOST.
    ///
    /// The top-right activity strip is repainted by re-driving the module's OWN public entry point,
    /// <c>UIModuleFactionAgendaTracker.Init(context)</c> → <c>InitialSetup</c>:144, which is the only place
    /// rows are CREATED. That is the right primitive (it picks up every row source, including ones other
    /// mods append by postfixing InitialSetup) but it is not free: <c>InitialSetup</c>:148 EMPTIES the row
    /// container before rebuilding it. Its only caller is <c>RefreshPersistentHud</c>, i.e. once per FLUSHED
    /// RAIL BATCH — on a busy client roughly ten full teardown+rebuild cycles a second, which blinks.
    ///
    /// So the rebuild is floored at <c>AgendaRebuildMinInterval</c> (0.25 s wall clock; the module's own
    /// <c>UpdateModuleDataCrt</c> poll refreshes existing rows only once per second, so four rebuilds a
    /// second is invisible). A floor alone would be a REACTIVITY defect — the last change of a burst would
    /// be the one dropped — so a refused rebuild is OWED: <c>AgendaRebuildDue</c> re-arms the HUD flag, and
    /// <c>FlushIfDirty</c>'s existing per-frame pass carries it the first frame past the floor. No
    /// coroutine, no Update hook, no timer object was added for this.
    ///
    /// FALSIFY: delete the <c>AgendaRebuildDue</c> call from <c>RefreshAgendaTracker</c> → <c>L559
    /// rebuild-is-unconditional-again</c>; make the method <c>return true</c> always → <c>L559
    /// rebuild-is-not-throttled</c>; drop the <c>MarkHudDirty()</c> from its refusal arm → <c>L559
    /// a-throttled-rebuild-is-lost</c>; make the refusal permanent (never advance <c>_nextAgendaRebuildAt</c>,
    /// or floor it at some huge interval) → <c>L559 owed-rebuild-is-never-served</c>.
    /// </summary>
    internal static class L559_TheAgendaRebuildIsThrottledButNeverLost
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var due = typeof(OpenUiRepaint).GetMethod("AgendaRebuildDue", All);
            var refresh = typeof(OpenUiRepaint).GetMethod("RefreshAgendaTracker", All);
            var markHud = typeof(OpenUiRepaint).GetMethod("MarkHudDirty", All);
            if (due == null || refresh == null || markHud == null)
            {
                yield return "L559 premise-changed: OpenUiRepaint.{AgendaRebuildDue,RefreshAgendaTracker," +
                             "MarkHudDirty} no longer resolves. The seam that keeps the native agenda rebuild " +
                             "off every single rail batch has moved — re-read it before assuming the strip " +
                             "still repaints without tearing its rows down ten times a second.";
                yield break;
            }

            // ── (a) THE REBUILD IS NOT UNCONDITIONAL ────────────────────────────────────────────
            // A stfld is invisible to a callee walk, so the throttle is a NAMED method for exactly the
            // reason MarkHudDirty is: this is the only way to prove the decision is still consulted.
            if (!Program.Callees(refresh, typeof(OpenUiRepaint).Assembly)
                    .Any(c => c.MetadataToken == due.MetadataToken))
                yield return "L559 rebuild-is-unconditional-again: RefreshAgendaTracker no longer asks " +
                             "AgendaRebuildDue. Init(context) is a full teardown of the row container " +
                             "(InitialSetup:148), and its caller runs once per flushed rail batch, so an " +
                             "unthrottled rebuild is a visible blink on any peer taking rail traffic.";
            if (!Program.Callees(due, typeof(OpenUiRepaint).Assembly)
                    .Any(c => c.MetadataToken == markHud.MetadataToken))
                yield return "L559 a-throttled-rebuild-is-lost: AgendaRebuildDue no longer marks the HUD " +
                             "dirty on the refusal path. Nothing then carries the LAST change of a burst, " +
                             "and a peer sits on a strip missing the row it was just told about until some " +
                             "unrelated batch happens to land past the floor — REACTIVITY is a hard mandate.";

            // ── (b) THE DECISION, EXECUTED: floor honoured, owed, then served ───────────────────
            OpenUiRepaint.Reset();
            const float t = 1000f; // absolute value is irrelevant: the floor is a difference
            if (!OpenUiRepaint.AgendaRebuildDue(t))
                yield return "L559 first-rebuild-refused: the very first rebuild of a session is throttled " +
                             "away, so a peer that joins mid-burst paints no strip at all.";
            if (OpenUiRepaint.AgendaRebuildDue(t) || OpenUiRepaint.AgendaRebuildDue(t + 0.1f))
                yield return "L559 rebuild-is-not-throttled: a second rebuild inside the floor was allowed. " +
                             "Rail batches arrive far faster than that and every one of them would empty and " +
                             "rebuild the row container.";
            if (!OpenUiRepaint.HudRepaintOwed)
                yield return "L559 owed-rebuild-was-not-recorded: a throttled skip left no HUD refresh owed, " +
                             "so FlushIfDirty's existing per-frame pass has nothing to carry it and the last " +
                             "change of a burst never reaches the strip.";
            if (!OpenUiRepaint.AgendaRebuildDue(t + 0.25f))
                yield return "L559 owed-rebuild-is-never-served: the floor did not expire at 0.25 s, so the " +
                             "owed rebuild is deferred forever — a throttle that drops changes instead of " +
                             "delaying them.";

            // ── POSITIVE CONTROL: the gate can still say yes on a fresh session ─────────────────
            OpenUiRepaint.Reset();
            if (!OpenUiRepaint.AgendaRebuildDue(t + 0.26f))
                yield return "L559 control-not-red: the rebuild gate refuses even after Reset cleared the " +
                             "floor, so every arm above would pass by saying no to everything.";
            OpenUiRepaint.Reset(); // leave no floor and no owed flag behind for the next law
        }
    }
}
