using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;

namespace RailCheck
{
    /// <summary>
    /// L145 — A PEER KEEPS CONTROL OF THE ACTORS IT IS NOT COMMANDING WHILE ANOTHER PEER ACTS.
    ///
    /// THE DEFECT WAS VANILLA'S, USED OUTSIDE ITS ASSUMPTION. <c>TacticalActor.ShouldViewWaitForMe</c>
    /// :1342-1360 answers true for as long as an actor has an executing ability —
    /// <c>TacticalAbility.StartPlayingAction</c>:1015-1018 sets <c>ShouldForceViewInWaitingState</c> for EVERY
    /// ability, not a special few — and <c>TacticalView.IsWaitingForActiveAbilitiesAndMapUpdate</c>:864 folds
    /// that over <c>Map.GetActors&lt;TacticalActor&gt;().Any(...)</c>, the WHOLE MAP. <c>UIStateInitial</c>:48
    /// then pushes <c>UIStateWaiting</c> (<c>SetInputState("Waiting")</c>, :20) and
    /// <c>UIStateWaiting.UpdateState</c>:25 refuses to leave while the fold holds. In single player the only
    /// actor executing anything is the one YOU ordered, so the fold and the per-actor fact are the same
    /// sentence. In a shared battle they are not: another human's jetpack flight is an executing ability on
    /// this peer's map, so this peer's entire view parked for the length of somebody else's action — over
    /// soldiers standing still, on the far side of the map, that this peer had every right to command.
    ///
    /// THE MOVEMENT BOUNDARIES ARE THE SAME FACT, NOT A SECOND BUG. The ranges are drawn by
    /// <c>UIStateCharacterSelected</c> out of <c>ValidMoves</c>:154-160 (redrawn at :1594-1597), and the park
    /// takes that state off the stack with <c>ClearStackAndPush</c>. "Even the movement-range boundaries
    /// disappear" is what <c>UIStateWaiting</c> looks like from the player's chair.
    ///
    /// MEASURED, NOT ARGUED. The client's own log for the 22:22:30 build carries <c>released this peer's UI
    /// from Soldier_3 (a mirrored Move_AbilityDef) — it was held in UIStateWaiting</c>. `bb1ca52`'s release
    /// seam found this peer ALREADY parked, which places the park upstream of that seam and rules it out as
    /// the cause: L139 hands the actor back, and the fold immediately parked the peer again.
    ///
    /// THE NARROWING IS THE ONE PER-ACTOR FUNCTION THE FOLD ALREADY CALLS, and it keeps the game's own answer
    /// everywhere this mod has no opinion — arm (a) is what makes that a fact rather than a claim:
    ///  • an AI faction's turn is untouched (there the wait IS the presentation of a side nobody commands);
    ///  • a status that forces the wait on its own (<c>ShouldViewWaitForMe</c>:1351-1358) is untouched;
    ///  • THIS peer's own soldier still parks THIS peer — the one player who legitimately cannot be
    ///    interrupted is the one performing the action, which is the user's own carve-out.
    ///
    /// WHAT THIS LAW CANNOT ASSERT, said plainly: that the released peer then actually draws its ranges. That
    /// needs a live <c>UIStateCharacterSelected</c>, a navigation mesh and a Unity frame, none of which exist
    /// in a console host. The seam is chosen so the redraw is the GAME'S — arm (c) requires the postfix to
    /// return the native answer unchanged whenever the narrowing does not apply, and arm (d) requires the
    /// narrowing to be reached from the game's own getter rather than from some mod-side poll — because a
    /// hand-rolled "unfreeze" that pokes the view is the shape L139 already forbids.
    ///
    /// Falsify: restore the global lock by making <c>ViewMustWaitFor</c> return <c>nativeSaysWait</c> →
    /// <c>L145 another-peers-action-parks-this-one</c>; drop the AI-turn arm →
    /// <c>L145 unparks-an-ai-turn</c>; drop the status arm → <c>L145 ignores-a-status-that-forces-the-wait</c>;
    /// make it return false unconditionally → <c>L145 nobody-ever-waits</c>; delete the postfix on
    /// <c>ShouldViewWaitForMe</c> → <c>L145 seam-is-not-the-games-own-getter</c>; stop marking mirrors →
    /// <c>L145 mirror-is-not-foreign</c>.
    /// </summary>
    internal static class L145_OnePeersActionDoesNotFreezeAnother
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var drive = typeof(TacticalActorDrive);
            var pure = drive.GetMethod("ViewMustWaitFor", All, null,
                                       new[] { typeof(bool), typeof(bool), typeof(bool), typeof(bool) }, null);
            var classify = drive.GetMethod("ActivationIsForeign", All);
            var postfix = drive.Assembly
                               .GetType("Multiplayer.Tactical.OnePeersActionDoesNotParkAnother")
                               ?.GetMethod("Postfix", All);
            var origin = drive.Assembly.GetType("Multiplayer.Tactical.AbilityDriveOrigin");
            if (pure == null || classify == null || postfix == null || origin == null)
            {
                yield return "L145 premise-changed: TacticalActorDrive.{ViewMustWaitFor,ActivationIsForeign} or " +
                             "the ShouldViewWaitForMe postfix no longer resolves. Nothing else in the mod turns " +
                             "the engine's whole-map 'is anybody busy' fold back into the per-actor question it " +
                             "was asking — re-read this law before assuming a peer can still move while another " +
                             "peer acts.";
                yield break;
            }

            // ── (a) THE OUTCOME, exhaustively over all four axes ──────────────
            // 16 rows, and the rule the law asserts is exactly one of them: the ONLY row the mod is allowed to
            // turn from true to false is "the game says wait, a player faction is mid-turn, another peer is
            // driving that actor, and no status forces it".
            foreach (var native in new[] { true, false })
                foreach (var playerTurn in new[] { true, false })
                    foreach (var foreign in new[] { true, false })
                        foreach (var status in new[] { true, false })
                        {
                            bool got = TacticalActorDrive.ViewMustWaitFor(native, playerTurn, foreign, status);
                            bool want = native && !(playerTurn && foreign && !status);
                            if (got == want) continue;
                            string row = "native=" + native + " playerTurn=" + playerTurn +
                                         " anotherPeerDrives=" + foreign + " statusForces=" + status;
                            if (got && !want)
                                yield return "L145 another-peers-action-parks-this-one (" + row + "): this peer's " +
                                             "view is told to wait for a soldier ANOTHER peer is driving. That is " +
                                             "UIStateInitial:48 pushing UIStateWaiting, SetInputState(\"Waiting\"), " +
                                             "and UIStateCharacterSelected — with its ValidMoves ranges — cleared " +
                                             "off the stack, for the length of somebody else's jetpack flight.";
                            else if (!got && want && !playerTurn)
                                yield return "L145 unparks-an-ai-turn (" + row + "): the narrowing reaches a turn " +
                                             "no player commands. The enemy's move is presentation on every peer; " +
                                             "handing input back mid-AI-turn lets a player click through it.";
                            else if (!got && want && status)
                                yield return "L145 ignores-a-status-that-forces-the-wait (" + row + "): " +
                                             "ShouldViewWaitForMe:1351-1358 has a SECOND arm — a TacStatus can " +
                                             "force the waiting state with no ability executing at all. " +
                                             "Overriding it means overriding a wait whose reason was never an " +
                                             "order and is not this mod's to narrow.";
                            else
                                yield return "L145 nobody-ever-waits (" + row + "): the seam answers 'do not wait' " +
                                             "for a case the GAME asked to wait on and this mod has no opinion " +
                                             "about. Every actor's own action would then draw over its own HUD.";
                        }

            // ── (b) the vacuity guard: the narrowing must actually narrow ─────
            if (TacticalActorDrive.ViewMustWaitFor(true, true, true, false))
                yield return "L145 outcome-is-vacuous: the one row this law exists for — the game says wait, a " +
                             "player faction is mid-turn, and another peer is driving that soldier — still says " +
                             "WAIT. Then nothing changed and every arm above passes over a defect that is still " +
                             "there.";

            // ── (c) WHOSE order it is: both doors, and only those two ─────────
            if (!TacticalActorDrive.ActivationIsForeign(true, false))
                yield return "L145 mirror-is-not-foreign: an activation running inside SyncApplyScope is not " +
                             "counted as another peer's. That is the mirror path itself (ApplyActivate wraps " +
                             "ability.Activate in it), so with this false the freeze is back for every relayed " +
                             "order and the law's own arm (a) is testing an input nothing produces.";
            if (!TacticalActorDrive.ActivationIsForeign(false, true))
                yield return "L145 host-replay-is-not-foreign: the HOST replaying a client's intent is counted as " +
                             "the host's own click. A host click and a host replay both reach Activate natively " +
                             "and are otherwise indistinguishable, so this is the only thing that tells them " +
                             "apart — without it the host freezes itself for orders it is merely relaying, and " +
                             "L146 cannot tell whose soldier is whose either.";
            if (TacticalActorDrive.ActivationIsForeign(false, false))
                yield return "L145 own-click-is-foreign: this peer's OWN click is classified as another peer's. " +
                             "Then the acting player is the one peer left able to interrupt his own soldier, " +
                             "which is the carve-out the user named in the opposite direction.";

            // ── (d) the seam is the GAME's own getter, not a mod-side poll ────
            var game = typeof(PhoenixPoint.Tactical.Entities.TacticalActorBase).Assembly;
            var originPrefix = origin.GetMethod("Prefix", All);
            if (originPrefix == null ||
                !Program.Callees(originPrefix, drive.Assembly).Any(c => c.Name == "MarkForeign"))
                yield return "L145 drive-origin-does-not-mark: AbilityDriveOrigin no longer marks whose order an " +
                             "activation is, so the ledger every arm above reads is permanently empty and the " +
                             "narrowing can never fire. L125 checks that this patch BINDS; this checks that it " +
                             "still does the one thing it binds for.";
            var live = drive.GetMethod("ViewMustWaitFor", All, null,
                                       new[] { typeof(PhoenixPoint.Tactical.Entities.TacticalActor), typeof(bool) },
                                       null);
            if (live == null || !Program.Callees(postfix, drive.Assembly)
                                        .Any(c => c.MetadataToken == live.MetadataToken))
                yield return "L145 seam-is-not-the-games-own-getter: the ShouldViewWaitForMe postfix no longer " +
                             "routes through TacticalActorDrive's live decision. Whatever replaced it either " +
                             "polls the view from outside or pokes the state stack by hand — the second is the " +
                             "shape L139 spent a whole law forbidding, and the first cannot be right on the frame " +
                             "the ability starts, because PlayingAction.SetState(Playing) adds to " +
                             "ExecutingAbilities synchronously inside Activate.";
            if (live != null && !Program.Callees(live, drive.Assembly).Any(c => c.Name == "DrivenByAnotherPeer"))
                yield return "L145 verdict-is-decorative: the live seam no longer asks WHO is driving the actor, " +
                             "so arm (a) executes a decision nothing makes. This is the exact shape L137 was " +
                             "green in for four days while the thing it named was broken.";
            if (Program.Callees(postfix, game).Any(c => c.Name == "ResetViewState" || c.Name == "SwitchToState"))
                yield return "L145 seam-drives-the-view-by-hand: the ShouldViewWaitForMe postfix moves the view " +
                             "state itself. This law's whole design is that the game's own poll " +
                             "(UIStateWaiting.UpdateState:25 -> UIStateInitial:48 -> UIStateCharacterSelected) " +
                             "does the release, on its own frame; a getter that mutates the state stack while " +
                             "being read from a state's update loop is a re-entrancy bug waiting for a report.";
        }
    }
}
