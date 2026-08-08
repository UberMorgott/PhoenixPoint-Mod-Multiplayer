using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Tactical;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.UI;

namespace RailCheck
{
    /// <summary>
    /// L310 — THE MOVE OVERLAY'S DRAW COROUTINE NEVER READS A NULL SWEEP, SO UNITY NEVER ABORTS ITS CHAIN.
    ///
    /// WHAT THE LOG SHOWED. One client, four times in a finished three-instance session (02:41:56, 02:45:15,
    /// 02:47:20, 02:48:41): <c>ArgumentNullException: Value cannot be null. Parameter name: source</c> out of
    /// <c>System.Linq.Enumerable.Where</c> inside
    /// <c>MoveAbilitySceneViewElement+&lt;UpdateMoveAreas&gt;d__36.MoveNext</c>, each followed IN THE SAME FRAME
    /// by Unity's <c>CHECK/REPORT PREVIOUS ERROR!!! Broken coroutine call chain</c>, and each preceded one frame
    /// earlier by our own <c>released this peer's UI from Soldier_9</c> and <c>move-range sweep WITHHELD</c>.
    /// The broken chain is the severity: Unity aborts the whole chain, so every coroutine downstream of that one
    /// silently stops for the rest of the battle. The host was clean; only the peer whose UI was released was hit.
    ///
    /// WHY THE WITHHELD EMPTY ARRAY WAS THE CAUSE AND NOT THE CURE. L168's gate answers an EMPTY sweep from
    /// <c>MoveAbility.GetTargetsData</c> while another peer drives the actor. <c>MoveAbility</c>:26 declares
    /// <c>HasValidTargets =&gt; GetTargetsData().Any()</c> and <c>TacticalAbility</c>:465-468 turns
    /// <c>!HasValidTargets</c> into <c>AbilityDisabledState.NoValidTarget</c> — so the empty answer is exactly
    /// what makes the ability report NOT ENABLED, and <c>MoveAbilitySceneViewElement.ValidMoves</c>:69-79 then
    /// answers <c>null</c>. <c>UpdateMoveAreas</c> re-reads that property AFTER its yields (:237, :243, :253,
    /// :259) and guards it exactly once, before the first one (<c>HasValidMoves</c>, :223). A draw that was legal
    /// when it started and withheld one frame later therefore hands <c>Where</c> a null source. Returning empty
    /// was never enough: it IS the flip.
    ///
    /// WHY FEED AND NOT STOP THE COROUTINE. The seam cannot stop the frame that throws — <c>ValidMoves</c>:73
    /// evaluates <c>IsEnabled()</c> (and so reaches the withhold) BEFORE it returns, two instructions from the
    /// caller that dereferences it, so any cancellation we could reach for lands after the exception. Feeding an
    /// EMPTY list at that read is the whole fix and needs no cancellation machinery: the loop finds no moves
    /// (:240), <c>UpdateMoveArea</c>:268-271 yield-breaks on an empty list, and the coroutine ends normally
    /// having drawn nothing over the <c>ClearGroundMarkers</c> it already did at :227 — which IS the blank
    /// overlay the withhold promises the player, on the same frame, with no re-enter. The native lifecycle covers
    /// the rest by itself: every LATER restart (<c>UIStateCharacterSelected</c>:1165 <c>StartDrawing</c> →
    /// <c>DrawTargetMarkers</c>, and :214, :255, :757) re-enters <c>UpdateMoveAreas</c>:223 where
    /// <c>HasValidMoves</c> is now false, and yield-breaks BEFORE its first yield.
    ///
    /// IT IS THE PROPERTY, NOT THE RELEASE. Siting the fix on <c>ValidMoves</c> rather than on
    /// <c>ReleaseLocalUiHolding</c> is what makes it cover every path that can take this peer's UI off an actor
    /// mid-sweep — a mirrored Move (reported), a mirrored ANY ability through the same
    /// <c>ApplyActivate</c> release, the FORCED settle's release, and the standing L168 case where nothing
    /// released anything and the player merely re-selected a soldier another peer drives.
    ///
    ///   (a) THE OUTCOME, EXECUTED — <see cref="TacticalCommandSync.MoveOverlayMustNotSeeNull"/> must answer
    ///       "feed" in the exact state the log captured: the sweep is withheld and the engine answered null.
    ///   (b) POSITIVE CONTROL, EXECUTED — it must answer "leave it" for a null the engine produced for its OWN
    ///       reasons (solo game, this peer's own order, no AP left) and for a real non-null sweep. Without this
    ///       the cheap wrong fix — never let that getter be null — passes (a) while deleting the engine's own
    ///       contract for every caller written to receive it.
    ///   (c) THE PREMISE IS STILL REAL IN THIS BUILD — <c>ValidMoves</c> still consults <c>IsEnabled</c> and
    ///       <c>HasValidTargets</c> still consults <c>GetTargetsData</c>. If either link goes, the withhold no
    ///       longer produces the null and this law is guarding nothing.
    ///   (d) THE SEAM — a Harmony POSTFIX on that getter which takes <c>__result</c> BY REF, routes through the
    ///       decision, and feeds an EMPTY, non-null list rather than a fabricated target.
    ///   (e) IT MUST ACTUALLY BIND — the trap this repo pays for: a patch whose target does not resolve is
    ///       silently never applied. The attribute must name the getter and <c>AccessTools.PropertyGetter</c>
    ///       must resolve it.
    ///   (f) THE GATE AND THE FEED ASK ONE QUESTION — both route through
    ///       <see cref="TacticalCommandSync.SweepIsWithheldFor"/>, which routes through
    ///       <see cref="TacticalCommandSync.MovePollMustBeWithheld"/>. Two copies of a three-axis predicate is
    ///       how a frame ends up fed on a frame the sweep ran.
    ///   (g) POSITIVE CONTROL, EXECUTED — the (d) scan is run over <see cref="FakeSeam"/> below, which takes
    ///       <c>__result</c> by value, consults nothing and holds a null fed value; the scan must flag all three.
    ///       STATED PLAINLY: the control is FULL for two of the three and one-directional for
    ///       <c>overlay-feed-is-unconditional</c>. <c>FakeSeam</c> lives in RailCheck's assembly, so a call it
    ///       makes into <c>TacticalCommandSync</c> is a cross-assembly memberref the IL walker does not resolve
    ///       — a CORRECTED <c>FakeSeam</c> still reads as "consults nothing". Turning that arm red therefore
    ///       has to be done on the real seam, and it was (2026-08-08: stop consulting the decision in the
    ///       postfix → the string appears), so the arm is falsified but not doubly so.
    ///
    /// Falsified 2026-08-08, every arm. <c>MoveOverlayMustNotSeeNull</c> returning false unconditionally →
    /// <c>L310 overlay-still-sees-null</c>; returning true unconditionally → <c>L310 engine-null-overwritten</c>
    /// and <c>L310 real-sweep-replaced</c>; dropping the <c>ref</c> on <c>__result</c> →
    /// <c>L310 overlay-seam-cannot-answer</c>; not calling the decision from the postfix →
    /// <c>L310 overlay-feed-is-unconditional</c>; giving <c>Empty</c> a member →
    /// <c>L310 fed-sweep-is-not-empty</c>; misspelling the patched property name →
    /// <c>L310 overlay-patch-cannot-bind</c>; inlining the three axes back into the prefix →
    /// <c>L310 gate-and-feed-disagree</c>; emptying <see cref="FakeSeam"/> → <c>L310 control-not-red</c>.
    /// </summary>
    internal static class L310_TheMoveOverlayNeverReadsANullSweep
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var cmd = typeof(TacticalCommandSync);
            var decision = cmd.GetMethod("MoveOverlayMustNotSeeNull", All);
            var shared = cmd.GetMethod("SweepIsWithheldFor", All);
            var poll = cmd.GetMethod("MovePollMustBeWithheld", All);
            var seam = cmd.Assembly.GetType("Multiplayer.Tactical.TheMoveOverlayIsNeverHandedANullSweep");
            var withhold = cmd.Assembly.GetType(
                "Multiplayer.Tactical.MoveRangeIsNotSweptWhileAnotherPeerDrivesTheActor");
            var getter = typeof(MoveAbilitySceneViewElement).GetProperty("ValidMoves", All)?.GetGetMethod(true);
            if (decision == null || shared == null || poll == null || seam == null || withhold == null ||
                getter == null)
            {
                yield return "L310 premise-changed: TacticalCommandSync.MoveOverlayMustNotSeeNull / " +
                             "SweepIsWithheldFor / MovePollMustBeWithheld, the " +
                             "TheMoveOverlayIsNeverHandedANullSweep or " +
                             "MoveRangeIsNotSweptWhileAnotherPeerDrivesTheActor patch class, or the game's own " +
                             "MoveAbilitySceneViewElement.ValidMoves getter no longer resolves. Nothing else " +
                             "keeps the withheld sweep from handing that getter's null to a coroutine mid-flight " +
                             "— re-read this law before assuming the empty array is enough.";
                yield break;
            }

            // ── (c) the null this law exists to prevent must still be REACHABLE in this build ──
            var isEnabled = Program.Callees(getter, typeof(MoveAbilitySceneViewElement).Assembly)
                                   .Any(c => c.Name == "IsEnabled");
            var hasValid = typeof(MoveAbility).GetProperty("HasValidTargets", All)?.GetGetMethod(true);
            var routesToSweep = hasValid != null &&
                                Program.Callees(hasValid, typeof(MoveAbility).Assembly)
                                       .Any(c => c.Name == "GetTargetsData");
            if (!isEnabled || !routesToSweep)
            {
                yield return "L310 premise-changed: in this build MoveAbilitySceneViewElement.ValidMoves no " +
                             "longer consults IsEnabled, or MoveAbility.HasValidTargets no longer consults " +
                             "GetTargetsData. That chain (MoveAbility:26 -> TacticalAbility:465-468 -> " +
                             "ValidMoves:69-79) is what turns the withheld empty sweep into a null under a " +
                             "running coroutine. If it is gone, this law guards nothing and the fix below may " +
                             "now be papering over a different null.";
                yield break;
            }

            // ── (a) THE OUTCOME, in the exact state the four log entries captured ────────────
            if (!TacticalCommandSync.MoveOverlayMustNotSeeNull(sweepWithheld: true, engineAnsweredNull: true))
                yield return "L310 overlay-still-sees-null: the sweep is withheld for this actor and the game " +
                             "answered null, and the overlay is handed that null anyway. That is the reported " +
                             "defect verbatim — ArgumentNullException out of Enumerable.Where inside " +
                             "MoveAbilitySceneViewElement.UpdateMoveAreas, and then Unity's 'Broken coroutine " +
                             "call chain', which aborts every coroutine downstream of it for the rest of the " +
                             "battle.";

            // ── (b) POSITIVE CONTROL: what must NOT change ──────────────────────────────────
            if (TacticalCommandSync.MoveOverlayMustNotSeeNull(sweepWithheld: false, engineAnsweredNull: true))
                yield return "L310 engine-null-overwritten: a null the ENGINE produced on its own — a solo game, " +
                             "this peer's own order, an actor with no movement left — is being replaced. Every " +
                             "caller of that getter was written to receive it (IsValidMove:449-454 tests for it " +
                             "by hand), and a mod outside its own gate must leave the game's behaviour byte for " +
                             "byte.";
            if (TacticalCommandSync.MoveOverlayMustNotSeeNull(sweepWithheld: true, engineAnsweredNull: false))
                yield return "L310 real-sweep-replaced: a REAL, non-null sweep is being thrown away because the " +
                             "gate happens to be withholding. The overlay would then go blank on frames the " +
                             "engine had answers for, which is a worse bug than the one this fixes.";

            // ── (d) THE SEAM ────────────────────────────────────────────────────────────────
            foreach (var s in Scan(seam, "TheMoveOverlayIsNeverHandedANullSweep")) yield return s;

            // ── (e) IT MUST ACTUALLY BIND ───────────────────────────────────────────────────
            var declared = seam.GetCustomAttributes(typeof(HarmonyPatch), inherit: false)
                               .Cast<HarmonyPatch>().Select(a => a.info).FirstOrDefault();
            var resolved = AccessTools.PropertyGetter(typeof(MoveAbilitySceneViewElement), "ValidMoves");
            if (declared == null || declared.declaringType != typeof(MoveAbilitySceneViewElement) ||
                declared.methodName != "ValidMoves" || declared.methodType != MethodType.Getter ||
                resolved == null || resolved.MetadataToken != getter.MetadataToken)
                yield return "L310 overlay-patch-cannot-bind: the postfix does not declare the " +
                             "MoveAbilitySceneViewElement.ValidMoves GETTER, or Harmony cannot resolve it. A " +
                             "patch whose target does not resolve is skipped in silence — no exception, no log " +
                             "line — and the coroutine keeps reading the raw null while every other arm of this " +
                             "law stays green.";

            // ── (f) THE GATE AND THE FEED ASK ONE QUESTION ──────────────────────────────────
            var prefix = withhold.GetMethod("Prefix", All);
            var postfix = seam.GetMethod("Postfix", All);
            var bothRoute = prefix != null && postfix != null &&
                            Calls(prefix, "SweepIsWithheldFor") && Calls(postfix, "SweepIsWithheldFor") &&
                            Calls(shared, "MovePollMustBeWithheld");
            if (!bothRoute)
                yield return "L310 gate-and-feed-disagree: the withhold and the feed no longer route through the " +
                             "same TacticalCommandSync.SweepIsWithheldFor, or it no longer routes through " +
                             "MovePollMustBeWithheld. Two copies of a three-axis predicate is exactly how one " +
                             "frame ends up fed an empty list while the sweep actually ran — or left null while " +
                             "it did not, which is the crash back again.";

            // ── (g) POSITIVE CONTROL, EXECUTED: the same scan over a seam that gets it wrong ─
            var control = Scan(typeof(FakeSeam), "FakeSeam").ToList();
            foreach (var want in new[] { "overlay-seam-cannot-answer", "overlay-feed-is-unconditional",
                                         "fed-sweep-is-not-empty" })
                if (!control.Any(c => c.Contains(want)))
                    yield return "L310 control-not-red: FakeSeam takes __result by value, consults no decision " +
                                 "and holds a null fed value, and the scan did not report " + want + ". The scan " +
                                 "above is therefore decorative and arm (d) proves nothing.";
        }

        /// <summary>The seam test, factored so arm (g) can run it over a seam KNOWN to be wrong.</summary>
        private static IEnumerable<string> Scan(Type seam, string label)
        {
            var postfix = seam.GetMethod("Postfix", All);
            if (postfix == null)
            {
                yield return "L310 overlay-seam-missing: " + label + " declares no Postfix, so nothing runs " +
                             "after the getter and the null reaches UpdateMoveAreas unchanged.";
                yield break;
            }
            var result = postfix.GetParameters().FirstOrDefault(p => p.Name == "__result");
            if (result == null || !result.ParameterType.IsByRef ||
                result.ParameterType.GetElementType() != typeof(List<MoveAbilityTargetData>))
                yield return "L310 overlay-seam-cannot-answer: " + label + "'s Postfix does not take " +
                             "ref List<MoveAbilityTargetData> __result, so it cannot change what the getter " +
                             "returns. Harmony hands a by-value __result back to nobody and the seam becomes a " +
                             "log line attached to the crash it was meant to stop.";
            if (!Calls(postfix, "MoveOverlayMustNotSeeNull") || !Calls(postfix, "SweepIsWithheldFor"))
                yield return "L310 overlay-feed-is-unconditional: " + label + "'s Postfix no longer consults " +
                             "both MoveOverlayMustNotSeeNull and SweepIsWithheldFor, so it either feeds on " +
                             "frames the engine owns the null or stops asking whether the sweep is withheld at " +
                             "all. Arms (a) and (b) would then be proved about a decision the live seam does not " +
                             "make — the shape this repo keeps paying for.";
            var fed = seam.GetField("Empty", All);
            var list = fed == null ? null : fed.GetValue(null) as List<MoveAbilityTargetData>;
            if (list == null || list.Count != 0)
                yield return "L310 fed-sweep-is-not-empty: " + label + " does not feed an EMPTY, non-null list. " +
                             "Null puts the crash straight back; a POPULATED one paints move tiles for a soldier " +
                             "another peer is driving, which TacticalActorDrive.RefuseLocalCommand will refuse " +
                             "the moment the player clicks one (L146).";
        }

        private static bool Calls(MethodBase m, string callee) =>
            m != null && Program.Callees(m, m.DeclaringType.Assembly).Any(c => c.Name == callee);

        /// <summary>A seam that gets every part of it wrong, so arm (g) can prove the scan is not decorative:
        /// <c>__result</c> by value, no decision consulted, and a null fed value.</summary>
        private static class FakeSeam
        {
            internal static readonly List<MoveAbilityTargetData> Empty = null;

            internal static void Postfix(MoveAbilitySceneViewElement __instance,
                                         List<MoveAbilityTargetData> __result)
            {
                if (__instance == null || __result == null) return;
            }
        }
    }
}
