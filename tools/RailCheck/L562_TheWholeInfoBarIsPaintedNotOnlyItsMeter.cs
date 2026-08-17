using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View.ViewModules;

namespace RailCheck
{
    /// <summary>
    /// L562 — THE WHOLE TOP-RIGHT INFO BAR IS PAINTED, NOT ONLY ITS POPULATION METER.
    ///
    /// <c>UIModuleInfoBar</c> belongs to NO view state, so no <c>UiNativeRepaint.Table</c> row can reach it
    /// and <c>OpenUiRepaint.RefreshInfoBar</c> is its only repaint. That refresh drove exactly ONE of the
    /// module's paint methods — <c>UpdatePopulation</c>:276 — which covers the population meter and TFTV's
    /// reputation postfix (TopInforBar:127) and NOTHING ELSE on the strip. Every other number has its own
    /// paint method whose only native trigger is an EVENT the rail never fires on a mirroring peer: Init
    /// :150-160 subscribes ScannerCapacityChanged / CharacterAdded / storage / resources / income, and
    /// <c>Update()</c>:216-247 only drains flags those handlers set. So resources, income, scanners,
    /// soldiers, vehicles, storage and containment all froze at their last Init on every peer that did not
    /// perform the gesture, while the population percentage beside them updated — which is worse than a
    /// dead strip, because half of it is visibly alive.
    ///
    /// THIS IS A DELIBERATE MODULE-TIER EXCEPTION and the law pins that too: <c>Init</c> must NEVER be the
    /// repaint. <c>UIModuleInfoBar.Init</c>:142-175 performs 18 unbalanced <c>+=</c> subscriptions and the
    /// module ships NO <c>Uninit</c> to match them, so re-driving Init leaks one subscription set per rail
    /// batch — arm (c) is what stops a future "just call Init, it is simpler" from landing.
    ///
    /// FALSIFY: drop an entry from <c>OpenUiRepaint.InfoBarPaints</c> → <c>L562 a-paint-was-dropped</c>;
    /// rename any of the four native members → <c>L562 native-paint-vanished</c>; stop calling
    /// <c>PaintInfoBar</c> from <c>RefreshInfoBar</c> → <c>L562 the-bar-is-not-painted</c>; drop the
    /// <c>InfoBarNeedsRefresh</c> gate → <c>L562 the-bar-repaints-unscoped</c>; make the refresh reach
    /// <c>UIModuleInfoBar.Init</c> → <c>L562 init-is-not-a-repaint</c>.
    /// </summary>
    internal static class L562_TheWholeInfoBarIsPaintedNotOnlyItsMeter
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>The four paints, named with the SIGNATURE each is bound by. Re-resolved here off the
        /// live game assembly rather than compared to whatever the mod happened to bind, so a native rename
        /// is RED on both sides of the seam instead of silently resolving to null and painting nothing.</summary>
        private static readonly (string Name, Type[] Args)[] Expected =
        {
            ("UpdatePopulation", Type.EmptyTypes),
            ("UpdateResourceInfo", new[] { typeof(GeoFaction), typeof(bool) }),
            ("SetScannersInfo", Type.EmptyTypes),
            ("UpdatePhoenixSpecificDataData", Type.EmptyTypes),
        };

        internal static IEnumerable<string> Check()
        {
            var repaint = typeof(OpenUiRepaint);
            var paints = repaint.GetField("InfoBarPaints", All);
            var paintBar = repaint.GetMethod("PaintInfoBar", All);
            var refresh = repaint.GetMethod("RefreshInfoBar", All);
            var needs = repaint.GetMethod("InfoBarNeedsRefresh", All);
            if (paints == null || paintBar == null || refresh == null || needs == null)
            {
                yield return "L562 premise-changed: OpenUiRepaint.{InfoBarPaints,PaintInfoBar," +
                             "RefreshInfoBar,InfoBarNeedsRefresh} no longer resolves. The top-right strip " +
                             "belongs to no view state, so this is its ONLY repaint — re-read it before " +
                             "assuming anything still paints its resources, scanners or storage.";
                yield break;
            }

            // ── (a) EVERY PAINT IS STILL BOUND, AND STILL EXISTS NATIVELY ──────────────────────
            var bound = paints.GetValue(null) as MethodInfo[];
            if (bound == null || bound.Length != Expected.Length)
                yield return "L562 a-paint-was-dropped: OpenUiRepaint.InfoBarPaints no longer holds the " +
                             Expected.Length + " paint methods the info bar has. A dropped entry is a " +
                             "permanently stale row of numbers on every peer that did not act, sitting " +
                             "beside a population meter that keeps updating.";
            else
                for (int i = 0; i < Expected.Length; i++)
                {
                    var native = typeof(UIModuleInfoBar).GetMethod(Expected[i].Name, All, null,
                                                                   Expected[i].Args, null);
                    if (native == null)
                        yield return "L562 native-paint-vanished: UIModuleInfoBar." + Expected[i].Name +
                                     " no longer resolves with the signature this repaint binds. A game " +
                                     "update renaming it makes the binding NULL, which paints nothing and " +
                                     "says nothing — the silent-swallow class this repo fights.";
                    else if (bound[i] == null || bound[i].MetadataToken != native.MetadataToken)
                        yield return "L562 a-paint-was-dropped: InfoBarPaints[" + i + "] is not " +
                                     Expected[i].Name + ". The list is what the repaint drives, so an " +
                                     "entry that is null or points elsewhere is one strip row that never " +
                                     "repaints again.";
                }

            // ── (b) THE LIST IS ACTUALLY DRIVEN, AND STILL SCOPED ──────────────────────────────
            var refreshCallees = Program.Callees(refresh, repaint.Assembly).ToList();
            if (!refreshCallees.Any(c => c.MetadataToken == paintBar.MetadataToken))
                yield return "L562 the-bar-is-not-painted: RefreshInfoBar no longer calls PaintInfoBar. " +
                             "Nothing else drives this module — its own native subscriptions never fire on " +
                             "a peer, because the rail writes model FIELDS and raises no game event.";
            if (!refreshCallees.Any(c => c.MetadataToken == needs.MetadataToken))
                yield return "L562 the-bar-repaints-unscoped: RefreshInfoBar no longer asks " +
                             "InfoBarNeedsRefresh. The refresh then runs on every flushed rail batch (~10 Hz " +
                             "on a live geoscape) including every postfix another mod hung on it, for a bar " +
                             "whose declared scope nothing touched.";

            // ── (c) INIT IS NEVER THE REPAINT ──────────────────────────────────────────────────
            // 18 unbalanced += and no Uninit: one leaked subscription set per rail batch, forever.
            var init = typeof(UIModuleInfoBar).GetMethod("Init", All);
            if (init != null &&
                (Il.CalledMethods(paintBar).Any(m => m.MetadataToken == init.MetadataToken &&
                                                         m.Module == init.Module) ||
                 Il.CalledMethods(refresh).Any(m => m.MetadataToken == init.MetadataToken &&
                                                        m.Module == init.Module)))
                yield return "L562 init-is-not-a-repaint: the info-bar refresh reaches UIModuleInfoBar.Init. " +
                             "Init:142-175 performs 18 `+=` on long-lived objects and the module ships no " +
                             "Uninit to balance them, so driving it from a rail batch leaks one subscription " +
                             "set per batch. The read-direction paints are the exception this seam exists to be.";

            // ── POSITIVE CONTROL: the gate can still say both yes and no ───────────────────────
            OpenUiRepaint.Reset();
            if (!OpenUiRepaint.InfoBarNeedsRefresh(true, "L562#0") ||
                OpenUiRepaint.InfoBarNeedsRefresh(true, "L562#0") ||
                OpenUiRepaint.InfoBarNeedsRefresh(false, "L562#1"))
                yield return "L562 control-not-red: InfoBarNeedsRefresh no longer distinguishes a moved key " +
                             "from an unmoved one, or no longer refuses a dead bar before touching its " +
                             "memory — arm (b) would then pass over a gate that decides nothing.";
            OpenUiRepaint.Reset(); // leave no repaint key behind for the next law
        }
    }
}
