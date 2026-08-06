using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;

namespace RailCheck
{
    /// <summary>
    /// L139 — A MIRRORED ORDER LETS GO OF THE LOCAL UI THAT WAS HOLDING THAT SOLDIER.
    ///
    /// THE GAP, and it is not on the ability — it is on the VIEW. The local input path does NOT activate
    /// through <c>TacticalAbility.Activate</c> alone. It goes through
    /// <c>TacticalViewState.ActivateAbility</c> (<c>TacticalViewState.cs</c>:259-277), which does TWO things:
    /// <c>ability.Activate(target)</c> (:270) and then <c>SwitchToState(new UIStateWaiting(),
    /// ClearStackAndPush)</c> (:274-275) — it takes the local view OUT of the state that was holding the actor
    /// BEFORE the engine begins driving him. <see cref="TacticalCommandSync"/>'s mirror path called only the
    /// first. Nothing else in the game closes that gap: <c>TacticalView</c> subscribes to
    /// <c>AbilityExecutedEvent</c> (<c>TacticalView.cs</c>:259) but <c>OnAbilityExecuted</c>:542-575 only
    /// refreshes prompts, and neither <c>UIStateShoot</c> nor <c>UIStateFreeCam</c> subscribes to ANY
    /// cancel/end callback — they exit on local INPUT only, so nothing external can force them out.
    ///
    /// BOTH 2026-08-06 REPORTS ARE THAT ONE GAP, which is why one seam answers both (postulate 3).
    ///  • THE CRASH. <c>UIModuleFreeFirstPersonShooting.Update</c>:132-137 gates on nothing but
    ///    <c>gameObject.activeInHierarchy &amp;&amp; _context != null</c>, and <c>_context</c> is written once
    ///    (<c>InitFirstPersonShootingLayout</c>:141) and NEVER cleared. Left running over an actor the mirror
    ///    just handed to the engine, its <c>UpdateAccuracyIndicator</c>:259-321 dereferences
    ///    <c>CurrentBehavior as FirstPersonCamera</c> (:316-317 — an <c>as</c> cast with no null test) and
    ///    <c>_context.View.SelectedActor</c> (:264-265 — nulled by <c>TacticalView</c>:1150/:1175). The client
    ///    log carries that NRE 797 times, once per frame, the only managed exception in the whole file, ending
    ///    in a native <c>Crash!!!</c>. The teardown that stops it is the state's OWN
    ///    (<c>UIStateFreeCam.ExitState</c>:447 → <c>ClearFirstPersonShootingLayout</c>:170 disables the
    ///    module's GameObject, which is exactly the <c>Update</c> gate).
    ///  • THE 10-SECOND STALL, which is the playability complaint. <c>UIStateCharacterSelected</c> reads
    ///    <c>ValidMoves</c>:158-160 — <c>ActorMoveAbility.GetTargetsData()</c> — every frame it draws
    ///    (:142-148, :1594-1597), and <c>MoveAbility.GetTargetsData</c>:170-173 is the exact line that logs
    ///    "shouldn't be called while X is executing abilities. This will invalidate the situation cache at
    ///    wrong time." The engine names its own damage. That call re-enters the path-request machinery WHILE
    ///    the actor's navigation is live: <c>GetTargetsDataInRange</c>:207-236 →
    ///    <c>MoveAbilityTargets.CacheTargets</c>:17-28, which <c>Clear()</c>s and <c>Calculate()</c>s a request
    ///    AND mutates the STATIC <c>NavigationSettings.Defaults</c> singleton (<c>TacticalPathRequest</c>:34-38
    ///    assigns that reference; <c>NavigationSettings</c>:46 declares it <c>static readonly</c>), turning
    ///    <c>PathRequestPostProcess</c> off — read at <c>TacticalPathRequest</c>:64, where it clears
    ///    <c>PointInfos</c> and returns. On the ACTING peer this is structurally impossible: its view left for
    ///    <c>UIStateWaiting</c> before <c>Activate</c> ever ran. On a mirror it ran every frame for 403 frames
    ///    while the soldier covered 0.14 of 1.41 units, never terminated, and was force-settled at 10 s.
    ///
    /// THE RELEASE IS THE GAME'S OWN, AND IT IS ONE CALL FOR EVERY ABILITY: <c>TacticalView.ResetViewState</c>
    /// (<c>TacticalView.cs</c>:262-268) switches the stack to <c>UIStateInitial</c>, running <c>ExitState</c>
    /// on whatever held the actor — free-aim, shoot, character-selected, or anything a mod adds later. No
    /// per-state code and no ability named anywhere in the seam, which is what arm (c) is for.
    ///
    /// WHY NOT <c>ToWaitViewState</c>:270-276, which is what the LOCAL path uses: <c>UIStateWaiting</c> waits
    /// for the ability to FINISH, so on a mirror it would park this player behind another human's action —
    /// precisely what postulate 2 forbids, and the stall this change exists to remove. Arm (e) forbids it
    /// explicitly, because it is the obvious "make it match the local path" edit and it is the wrong one.
    ///
    /// THE OUTCOME AND ITS HONEST LIMIT. What this law asserts is what the peer is LEFT HOLDING — arm (a) runs
    /// <see cref="TacticalCommandSync.LocalUiStillHoldsAfterMirror"/> over both axes and requires FALSE
    /// wherever the release was reached, and requires TRUE where it was not, so the law cannot pass by being
    /// vacuous. What it CANNOT assert is the next link: that the mirrored ability then terminates on its own,
    /// because navigation, the animator and the situation cache do not exist in a console host. That half is
    /// L133's (the settle ceiling is the backstop that makes the case non-fatal either way), and this law's
    /// job is to remove the cause rather than to prove the consequence. ALSO NOT ASSERTED: the ORDER of the
    /// release relative to <c>Activate</c> / <c>CancelActions</c>. <c>Program.CalleeSequence</c> is private to
    /// <c>Program.cs</c> and a second IL walker here is the duplication that file warns against; the cost of
    /// getting the order wrong is one frame of NRE rather than the storm, so the gap is bounded and named.
    ///
    /// Falsify: delete the <c>ReleaseLocalUiHolding</c> call from <c>ApplyActivate</c> or from
    /// <c>ApplySettle</c> → <c>L139 seam-does-not-release</c>; make <c>LocalUiMustRelease</c> return true
    /// unconditionally → <c>L139 release-disturbs-an-uninvolved-peer</c>; make it return false → <c>L139
    /// holder-is-never-released</c> plus <c>L139 mirror-leaves-the-ui-holding</c>; point
    /// <c>ReleaseLocalUiHolding</c> at <c>ToWaitViewState</c> → <c>L139 release-parks-this-player</c>; strip
    /// the <c>ResetViewState</c> call → <c>L139 release-is-not-the-game-s-own</c>.
    /// </summary>
    internal static class L139_MirroredOrderReleasesTheLocalUi
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var cmd = typeof(TacticalCommandSync);
            var release = cmd.GetMethod("ReleaseLocalUiHolding", All);
            var mustRelease = cmd.GetMethod("LocalUiMustRelease", All);
            var stillHolds = cmd.GetMethod("LocalUiStillHoldsAfterMirror", All);
            var applyActivate = cmd.GetMethod("ApplyActivate", All);
            var applySettle = cmd.GetMethod("ApplySettle", All);
            if (release == null || mustRelease == null || stillHolds == null ||
                applyActivate == null || applySettle == null)
            {
                yield return "L139 premise-changed: TacticalCommandSync.{ReleaseLocalUiHolding," +
                             "LocalUiMustRelease,LocalUiStillHoldsAfterMirror,ApplyActivate,ApplySettle} no " +
                             "longer resolves. Nothing else in the mod takes this peer's UI off an actor the " +
                             "engine is about to drive — re-read this law before assuming something does.";
                yield break;
            }

            var mod = cmd.Assembly;
            var game = typeof(PhoenixPoint.Tactical.Entities.TacticalActorBase).Assembly;

            // ── (a) THE OUTCOME: what is this peer LEFT holding? ──────────────
            // Both axes, exhaustively. TRUE is the bug in every row where the release ran, and the second
            // half is what stops the law passing vacuously: with no release, the hold MUST survive.
            foreach (var held in new[] { true, false })
            {
                if (TacticalCommandSync.LocalUiStillHoldsAfterMirror(held, releaseReached: true))
                    yield return "L139 mirror-leaves-the-ui-holding: a mirrored order went through the seam and " +
                                 "this peer's view is STILL holding that soldier (held=" + held + "). That is the " +
                                 "state MoveAbility.GetTargetsData:170-173 calls invalid in the engine's own words " +
                                 "and the state UIModuleFreeFirstPersonShooting.Update:132 keeps drawing from — " +
                                 "the 403-frame stall and the 797-NRE crash are the two ways it ends.";
                if (held && !TacticalCommandSync.LocalUiStillHoldsAfterMirror(true, releaseReached: false))
                    yield return "L139 outcome-is-vacuous: with the release NOT reached, a view that was holding " +
                                 "the actor is reported as no longer holding it. Then this law's green says " +
                                 "nothing about anything and every falsification below would pass.";
            }

            // ── (b) WHO gets released — postulate 2 lives in the negative half ──
            if (!TacticalCommandSync.LocalUiMustRelease(true, true))
                yield return "L139 holder-is-never-released: the one peer whose UI IS holding this soldier is " +
                             "left holding him. That is the entire defect this seam exists to close.";
            if (TacticalCommandSync.LocalUiMustRelease(true, false))
                yield return "L139 release-disturbs-an-uninvolved-peer: a peer whose view is holding somebody " +
                             "ELSE has its state stack cleared by an order that has nothing to do with it. A busy " +
                             "host would then turn over every other player's selection once per order, which is " +
                             "postulate 2 broken in the other direction — unplayable for a different reason.";
            if (TacticalCommandSync.LocalUiMustRelease(false, true) ||
                TacticalCommandSync.LocalUiMustRelease(false, false))
                yield return "L139 release-without-a-view: the seam claims a release is due with no tactical view " +
                             "in existence. Off the tactical band that is a null deref on the way to fixing " +
                             "nothing.";

            // ── (c) it must be the GAME'S OWN release, and the verdict must be consulted ──
            var toGame = Program.Callees(release, game).Select(c => c.Name).ToList();
            if (!toGame.Contains("ResetViewState"))
                yield return "L139 release-is-not-the-game-s-own: ReleaseLocalUiHolding no longer reaches " +
                             "TacticalView.ResetViewState. Whatever replaced it is a hand-rolled state switch, " +
                             "and the thing that actually stops the crash is the state's OWN ExitState chain " +
                             "(UIStateFreeCam:447 -> ClearFirstPersonShootingLayout:170 disables the module's " +
                             "GameObject). A poke that skips ExitState leaves the module updating.";
            if (toGame.Contains("ToWaitViewState"))
                yield return "L139 release-parks-this-player: the release routes through ToWaitViewState, so this " +
                             "peer now sits in UIStateWaiting until ANOTHER human's ability finishes. That is the " +
                             "10-second block this change exists to delete, reintroduced as the fix for it.";
            if (!Program.Callees(release, mod).Any(c => c.MetadataToken == mustRelease.MetadataToken))
                yield return "L139 verdict-is-decorative: ReleaseLocalUiHolding no longer consults " +
                             "LocalUiMustRelease, so arm (b) is executing a decision nothing makes. This is the " +
                             "exact shape L132 was green in for four days while every client overwatch was refused.";

            // ── (d) BOTH seams reach it: the mirrored order AND the forced tear ──
            foreach (var seam in new[] { applyActivate, applySettle })
                if (!Program.Callees(seam, mod).Any(c => c.MetadataToken == release.MetadataToken))
                    yield return "L139 seam-does-not-release: " + seam.Name + " no longer reaches " +
                                 "ReleaseLocalUiHolding. Both seams need it and for the same reason — " +
                                 "ApplyActivate hands the actor to the engine, ApplySettle TEARS his action " +
                                 "channel out from under whatever is drawing him — and the crash needed only " +
                                 "one of the two to be open.";
        }
    }
}
