using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.UI;
using UnityEngine.UI;

namespace RailCheck
{
    /// <summary>
    /// L199 — THE COUNTDOWN CAPTION IS SIZED BY A LAYOUT COMPONENT, NOT BY A GUESS, SO IT CANNOT LEAVE ITS
    /// PLATE.
    ///
    /// THE LIVE BUG (2026-08-08, same session as L198, the overlay's first live exposure). "MISSION STARTS
    /// IN 5 SECONDS" drew straight through both edges of the panel background. Not a styling slip — a
    /// DEFAULT: `UiToolkit.CreateText` leaves `horizontalOverflow = Overflow` and
    /// `verticalOverflow = Overflow` on every label it builds, which is the correct default for the lobby's
    /// generously sized rows and exactly wrong for a 260-wide plate carrying a header-size sentence.
    ///
    /// THE FIX IS NOT A SMALLER FONT AND NOT AN OFFSET. Either would be a number that happens to work at the
    /// owner's resolution, in English, at this UI scale, and would break on the first of those to change —
    /// and the panel is already fully derived (`PanelH` is `Pad * 3 + TitleH + ButtonH`), so a magic
    /// constant would be the only hand-tuned thing in it. The frame stays fixed and the TEXT adapts, using
    /// the auto-size uGUI already ships: `resizeTextForBestFit` bounded by min/max, `HorizontalWrapMode.Wrap`
    /// so a long line breaks instead of running out, `VerticalWrapMode.Truncate` so the worst case is a
    /// clipped bottom rather than text over the map. These are the SAME five writes the mod already puts on
    /// the NATIVE End Turn button's own caption (`TacticalReadySync.FitLabel`:625-635) — the owner's stated
    /// reference for a correct button — so the countdown label behaves like the game's own.
    ///
    /// WHY THIS IS A LAW AND NOT A COMMIT. L177 covers this overlay and was GREEN through the defect,
    /// correctly: it asserts the countdown's DECISION (gate, clock, veto, mission untouched) and has no
    /// opinion about the widget. Nothing else in the harness looks at a from-code label at all, so the next
    /// caption added to any mod panel would repeat it — and the failure mode is silent by nature, because
    /// overflowing text renders perfectly happily.
    ///
    ///   (a) <c>label-can-overflow</c> — `CountdownPanel.FitInsideRect` must write all five: best-fit on, a
    ///       min and a max bound, wrap horizontally, truncate vertically. Four of five is not a weaker
    ///       version of the rule, it is the bug: best-fit without Wrap still shrinks a long line to
    ///       illegibility rather than breaking it, and Wrap without best-fit still spills DOWN out of the
    ///       plate.
    ///
    ///   (b) <c>title-keeps-the-toolkit-default</c> — the fit must be applied to the countdown's own title
    ///       AND to every label on its cancel button, and it must be applied AFTER the text is built.
    ///       Asserted in IL ORDER, because `UiToolkit.CreateText` sets the overflow modes itself: fitting a
    ///       label that does not exist yet is a no-op that reads exactly like a fix.
    ///
    ///   (c) POSITIVE CONTROL, <c>nothing-is-fitted</c> — if `Attach` stops calling `FitInsideRect`
    ///       entirely, arm (a) still passes over a helper nobody runs and the caption is back outside the
    ///       plate with no line anywhere to say so.
    ///
    /// Falsify (each turns it RED): delete the `horizontalOverflow` line (or any of the other four) from
    /// `FitInsideRect` → <c>label-can-overflow</c>; move the `FitInsideRect(_title)` call above the
    /// `UiToolkit.CreateText` that builds it, or drop the label loop from `SkinButton` →
    /// <c>title-keeps-the-toolkit-default</c>; delete both `FitInsideRect` calls → <c>POSITIVE CONTROL</c>;
    /// rename a member → <c>premise-changed</c>.
    /// </summary>
    internal static class L199_TheCountdownLabelFitsItsPlate
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>The five writes that together mean "this text cannot leave its rect": shrink to fit,
        /// bounded both ways so it neither grows past the design size nor becomes unreadable, break long
        /// lines instead of running out sideways, and clip rather than spill downwards.</summary>
        private static readonly string[] FitWrites =
        {
            "set_resizeTextForBestFit", "set_resizeTextMinSize", "set_resizeTextMaxSize",
            "set_horizontalOverflow", "set_verticalOverflow",
        };

        internal static IEnumerable<string> Check()
        {
            var panel = typeof(CountdownPanel);
            var mod = panel.Assembly;
            var ugui = typeof(Text).Assembly;

            var attach = panel.GetMethod("Attach", All);
            var fit = panel.GetMethod("FitInsideRect", All);
            var skin = panel.GetMethod("SkinButton", All);
            var createText = mod.GetType("Multiplayer.UI.UiToolkit")?.GetMethod("CreateText", All);
            var createButton = mod.GetType("Multiplayer.UI.UiToolkit")?.GetMethod("CreateButton", All);

            if (attach == null || fit == null || skin == null || createText == null || createButton == null)
            {
                yield return "L199 premise-changed: one of CountdownPanel.{Attach,FitInsideRect,SkinButton} " +
                             "or UiToolkit.{CreateText,CreateButton} no longer resolves. A vacuous pass here " +
                             "restores the exact shipped defect — a header-size sentence drawn straight " +
                             "through the edges of its own background plate — and nothing else in the harness " +
                             "looks at a from-code label.";
                yield break;
            }

            // ── (a) the auto-size is complete, not partial ─────────────────────────────────────────
            var writes = Program.Callees(fit, ugui).Select(c => c.Name).ToList();
            foreach (var w in FitWrites)
                if (!writes.Contains(w))
                    yield return "L199 label-can-overflow: FitInsideRect never writes Text." +
                                 w.Substring("set_".Length) + ". All five are one rule — the frame is fixed " +
                                 "and the TEXT adapts. Best-fit without Wrap shrinks a long caption to " +
                                 "illegibility instead of breaking it; Wrap without best-fit spills out of " +
                                 "the bottom; an unbounded best-fit picks whatever it likes. This is the " +
                                 "same set the mod puts on the native End Turn caption " +
                                 "(TacticalReadySync.FitLabel:625-635).";

            // ── (b) applied to the real labels, and AFTER they exist ───────────────────────────────
            var seq = Program.CalleeSequence(attach);
            int firstText = seq.FindIndex(c => Same(c, createText));
            int firstFit = seq.FindIndex(c => Same(c, fit));
            if (firstFit < 0)
                yield return "L199 title-keeps-the-toolkit-default: Attach never fits the countdown title, so " +
                             "it keeps UiToolkit.CreateText's own default of HorizontalWrapMode.Overflow — the " +
                             "default the whole defect was.";
            else if (firstText < 0 || firstFit < firstText)
                yield return "L199 title-keeps-the-toolkit-default: Attach fits a label BEFORE " +
                             "UiToolkit.CreateText builds it. CreateText assigns the overflow modes itself, so " +
                             "a fit applied first is overwritten one line later — a no-op that reads exactly " +
                             "like a fix and leaves the caption outside the plate.";

            int firstButton = seq.FindIndex(c => Same(c, createButton));
            int firstSkin = seq.FindIndex(c => Same(c, skin));
            if (firstSkin < 0 || (firstButton >= 0 && firstSkin < firstButton))
                yield return "L199 title-keeps-the-toolkit-default: Attach does not skin the cancel button " +
                             "after building it. SkinButton is where its label is fitted, and the button's " +
                             "caption is the one piece of this overlay that gets LOCALIZED — the case a fixed " +
                             "width is least able to survive.";

            if (!Program.Callees(skin, mod).Any(c => Same(c, fit)))
                yield return "L199 title-keeps-the-toolkit-default: SkinButton does not fit the button's " +
                             "labels. CreateButton builds its caption through the same CreateText that " +
                             "overflows by default, and it is sized to the button's own rect — so a longer " +
                             "translation draws over the plate exactly as the title did.";

            // ── (c) POSITIVE CONTROL ───────────────────────────────────────────────────────────────
            if (!Program.Callees(attach, mod).Any(c => Same(c, fit)) &&
                !Program.Callees(skin, mod).Any(c => Same(c, fit)))
                yield return "L199 POSITIVE CONTROL: nothing calls FitInsideRect. Every arm above then " +
                             "describes a helper no widget is ever handed to, and the countdown caption is " +
                             "back to being drawn outside its own background with the harness fully green.";
        }

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;
    }
}
