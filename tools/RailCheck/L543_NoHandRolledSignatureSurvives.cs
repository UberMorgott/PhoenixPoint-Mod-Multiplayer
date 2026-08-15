using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L543 — NO HAND-ROLLED READ-SET SURVIVES BESIDE A DECLARED PREFIX SET.
    ///
    /// The five signature builders — AgendaSignature, InfoBarKey, CrewSignature, RosterSignature,
    /// CrewSlotKey — are read-sets written BACKWARDS: a human enumerating, in a string, everything a strip
    /// draws. That is the v1 per-widget sync this project abandoned, and InfoBarKey admitted its own
    /// incompleteness by adding a 1-second Time.realtimeSinceStartup floor. Leaving them beside a declared
    /// prefix set is the two-ordering-systems mistake repeated in the UI layer (§B.8).
    ///
    /// RepaintNeeded ITSELF IS KEPT — it is the fallback primitive and L492/L516 both execute it. This law
    /// forbids the KEYS, not the memory.
    ///
    /// ARMS:
    ///   (a) signature-builder-survives — none of the five methods is declared on OpenUiRepaint.
    ///   (b) repaint-needed-gone — POSITIVE CONTROL: RepaintNeeded and AgendaNeedsRebuild MUST still
    ///       exist. Without this the law is satisfied by deleting the whole gate, which would restore the
    ///       L492 flicker (a full row teardown ~10 times a second) and re-break L516.
    ///   (c) infobar-burns-the-key — EXECUTED, the reported ordering bug: ask the gate about key X while
    ///       the bar is NOT live, then ask about the SAME X while it IS live. The second call MUST
    ///       refresh. This fails the moment the liveness test moves after RepaintNeeded instead of before.
    ///   (d) infobar-live-changed-skipped / infobar-live-unchanged-refreshes — both of L492's directions
    ///       on the live bar, so the gate cannot be a constant.
    ///
    /// ROLES SEPARATED (§C.3): every arm is a pure execution or an assembly-shape statement, identical
    /// from either role.
    ///
    /// Falsify (compile-valid src mutations, each named): re-add `private static string InfoBarKey(...)`
    /// → (a); change InfoBarNeedsRefresh's `&amp;&amp;` to `&amp;` → (c); `=&gt; barLive;` → (d).
    /// </summary>
    internal static class L543_NoHandRolledSignatureSurvives
    {
        private static readonly string[] Builders =
            { "AgendaSignature", "InfoBarKey", "CrewSignature", "RosterSignature", "CrewSlotKey" };

        internal static IEnumerable<string> Check()
        {
            var repaint = typeof(OpenUiRepaint);
            const BindingFlags Any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.DeclaredOnly;

            var gate = repaint.GetMethod("InfoBarNeedsRefresh", Any);
            if (gate == null)
            {
                yield return "L543 premise-changed: OpenUiRepaint.InfoBarNeedsRefresh did not resolve, so " +
                             "arms (c) and (d) cannot execute the ordering they exist to pin.";
                yield break;
            }

            var surviving = Builders.Where(b => repaint.GetMethod(b, Any) != null)
                                    .OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (surviving.Count > 0)
                yield return "L543 signature-builder-survives: " + string.Join(", ", surviving) + " still " +
                             "exist(s) on OpenUiRepaint. A hand-rolled signature is a read-set written " +
                             "backwards — the v1 per-widget sync this project abandoned — and beside a " +
                             "declared prefix set it is two competing read-set mechanisms, which is the " +
                             "two-ordering-systems mistake repeated in the UI layer.";

            // (b) POSITIVE CONTROL: the primitive and L492/L516's gate must SURVIVE.
            if (repaint.GetMethod("RepaintNeeded", Any) == null ||
                repaint.GetMethod("AgendaNeedsRebuild", Any) == null)
                yield return "L543 repaint-needed-gone: RepaintNeeded and/or AgendaNeedsRebuild were " +
                             "deleted. §B.8 KEEPS RepaintNeeded as the fallback primitive; deleting the " +
                             "gate satisfies arm (a) while restoring the L492 flicker — a full agenda row " +
                             "teardown ~10 times a second — and re-breaking L516's ordering.";

            // (c) THE REPORTED ORDERING BUG, reproduced end to end.
            OpenUiRepaint.Reset();
            if (OpenUiRepaint.InfoBarNeedsRefresh(false, "UIModuleInfoBar#7"))
                yield return "L543 infobar-refreshes-while-dead: a bar that is not yet Init'd claimed a " +
                             "refresh. There is nothing on screen to refresh, and the body would " +
                             "dereference a null module.";
            if (!OpenUiRepaint.InfoBarNeedsRefresh(true, "UIModuleInfoBar#7"))
                yield return "L543 infobar-burns-the-key: the gate was asked about a key while the bar was " +
                             "NOT live, and that question was REMEMBERED. The first refresh after the bar " +
                             "came up compared EQUAL and skipped the repaint it owed — the same class as " +
                             "efc4782 / L516. The liveness test must be asked BEFORE RepaintNeeded, never " +
                             "after it.";

            // (d) BOTH DIRECTIONS on the live bar.
            if (!OpenUiRepaint.InfoBarNeedsRefresh(true, "UIModuleInfoBar#8"))
                yield return "L543 infobar-live-changed-skipped: a LIVE bar did not refresh on a CHANGED " +
                             "key. A stale strip is a defect in this repo, not a cosmetic issue.";
            if (OpenUiRepaint.InfoBarNeedsRefresh(true, "UIModuleInfoBar#8"))
                yield return "L543 infobar-live-unchanged-refreshes: a LIVE bar refreshed on an UNCHANGED " +
                             "key. The gate must ADD a condition, never remove one — every postfix another " +
                             "mod has hung on the info bar is paid on each refresh (TFTV's TopInforBar:127 " +
                             "does string Transform.Find lookups, a LINQ walk of the alien bases and a " +
                             "sprite load).";
            OpenUiRepaint.Reset();
        }
    }
}
