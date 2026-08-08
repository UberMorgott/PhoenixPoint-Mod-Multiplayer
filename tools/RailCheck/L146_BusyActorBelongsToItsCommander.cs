using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;

namespace RailCheck
{
    /// <summary>
    /// L146 — AN ACTOR EXECUTING AN ORDER TAKES NO COMMAND FROM ANOTHER PEER, AND IS RELEASED WHEN IT ENDS.
    ///
    /// FIRST-COME-FIRST-SERVED, AND ONLY HALF OF IT WAS ARBITRATED. A CLIENT's second order was already
    /// refused: <c>TacticalCommandSync.Validate</c>'s <c>actorBusy</c> arm answers
    /// <c>TacticalCommandSync.BusyRefusal</c> and <c>IntentRail.Reject</c> nudges it home. The HOST's own click
    /// never reaches that path — <c>TacticalViewState.ActivateAbility</c>:270 calls <c>Activate</c> natively —
    /// so client-first + host-second was unarbitrated, and the report is exactly what an unarbitrated second
    /// activation looks like: "the character freezes in place, nothing happens, and then it teleports abruptly
    /// to where I told it to run". The freeze is the second activation landing on a busy actor
    /// (<c>ShootAbility.Activate</c>:167 chooses <c>EnqueueAction(soloAfterCurrent)</c> or a
    /// <c>PlayAction(cancelCurrent: true)</c> that tears the first order's channel out); the teleport is the
    /// host's own settle then dragging every peer to where the SECOND order pointed.
    ///
    /// THIS IS NOT A BLOCKER, AND THE DISTINCTION IS THE REASON IT IS ALLOWED (postulate 2, and the NO QUORUMS
    /// mandate this repo states as a hard product rule). What the mandate forbids is a gate that stays shut
    /// until a PERSON acts: a ready-vote, an acknowledgement, all-peers-confirmed. This lock has none of that
    /// shape. It holds ONE actor, for the duration of ITS OWN action, and it opens BY ITSELF — the release is
    /// <c>TacticalActorBase.HasExecutingAbility()</c> going false, i.e. the engine finishing the animation it
    /// started (arm (b) executes exactly that, and the ceiling is the engine's, not a timer of ours). An AFK
    /// peer cannot extend it by one frame, because an AFK peer issues no orders and therefore drives no actor;
    /// the peer holding it cannot renew it either. Every OTHER soldier stays commandable throughout, which is
    /// L145's half of the same change. It is the same category the mandate marks ALLOWED for the load barrier:
    /// a wait on something that ends on its own, not a wait on a human.
    ///
    /// ARM (c) IS THE ONE THAT MATTERS FOR THIS REPO'S DOMINANT BUG CLASS. A refusal a player cannot see is a
    /// silent swallow wearing a guard's uniform — and the remote half was ALREADY silent: the host passed the
    /// 3-argument <c>IntentRail.Reject</c>, so the reason reached the losing client's LOG and never its screen
    /// (client log 22:28:30, <c>reject nudge ... another peer commanded it first</c>, no <c>[notify]</c>). Both
    /// halves of the race must now say the same sentence — <c>BusyRefusal</c>, one const, three call sites —
    /// the remote one through the nudge's <c>notify</c> bit and the local one through
    /// <c>SessionNotifier.ShowToast(modalFallback: true)</c>, which is the same widget on the far end.
    ///
    /// WHY THE GATE IS AT <c>TacticalViewState.ActivateAbility</c> AND NOT ON <c>TacticalAbility.Activate</c>:
    /// :259-277 is the single seam every UI state turns a click into an activation through
    /// (<c>UIStateCharacterSelected</c>:455/:624/:940/:958, <c>UIStateAbilitySelected</c>:617/:621/:629) and it
    /// has NO overrides anywhere in the assembly, while nothing the engine raises for itself — hurt reactions,
    /// EndTurn, idle, the overwatch shot — passes through it. Gating <c>Activate</c> instead would refuse the
    /// engine's own ambient abilities on a busy actor, which is a wedge, not a guard. Arm (d) pins the seam.
    ///
    /// AND THE REFUSED PEER GETS ITS SCREEN BACK, which is the opposite of what this law was written with.
    /// Returning false from the prefix skips :274-275's <c>SwitchToState(new UIStateWaiting())</c> too, and
    /// while the click still played locally that was the RIGHT outcome (nothing changed, so nothing repaints).
    /// A9/L230 suppresses the native body for every clicked rider, so it became the lock-up instead: the
    /// refused player stood in the targeting state his click never left, holding a toast about a button that
    /// no longer did anything. The refusal now shares
    /// <c>TacticalCommandSync.ReleaseLocalUiHolding</c> with every other suppressed click (L232). WHAT THIS
    /// LAW STILL CANNOT ASSERT is the frame itself — proving it needs a live state stack, which a console host
    /// does not have. Arm (d) asserts the seam; L232 asserts the outcome; the frame is the game's.
    ///
    /// Falsify: make <c>PeerMayCommand</c> return true always → <c>L146 second-command-lands</c>; make it
    /// return false always → <c>L146 idle-actor-is-locked</c> and <c>L146 own-soldier-is-locked</c>; ignore the
    /// executing test (return <c>!executionBelongsToAnotherPeer</c>) → <c>L146 lock-outlives-the-action</c>;
    /// drop the toast → <c>L146 refusal-is-silent</c>; drop the notify bit on the host's reject →
    /// <c>L146 remote-refusal-is-silent</c>; move the gate off <c>ActivateAbility</c> →
    /// <c>L146 gate-is-not-on-the-input-seam</c>.
    /// </summary>
    internal static class L146_BusyActorBelongsToItsCommander
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var drive = typeof(TacticalActorDrive);
            var mayCommand = drive.GetMethod("PeerMayCommand", All);
            var refuse = drive.GetMethod("RefuseLocalCommand", All);
            // ONE PREFIX, NOT TWO. The gate used to be its own patch class on the same method as
            // ClickedOrderWaitsForTheEcho, both unprioritised — Harmony stops at the first prefix that returns
            // false and the order between two unprioritised patch classes is undeclared, so on any given load
            // one of them silently did not run. The verdict now lives at the top of PublishClickedOrder, which
            // IS that one prefix's body, so arm (c) asks it there and arm (d) pins the surviving patch class.
            var gate = drive.Assembly.GetType("Multiplayer.Tactical.ClickedOrderWaitsForTheEcho");
            var prefix = typeof(TacticalCommandSync).GetMethod("PublishClickedOrder", All);
            var handle = typeof(TacticalCommandSync).GetMethod("HandleActivate", All);
            if (mayCommand == null || refuse == null || prefix == null || gate == null || handle == null)
            {
                yield return "L146 premise-changed: TacticalActorDrive.{PeerMayCommand,RefuseLocalCommand}, the " +
                             "ActivateAbility gate or TacticalCommandSync.{PublishClickedOrder,HandleActivate} " +
                             "no longer resolves. " +
                             "Nothing else stops two peers commanding one soldier — re-read this law before " +
                             "assuming first-to-act-wins still holds in both directions.";
                yield break;
            }

            // ── (a) THE OUTCOME, all four rows. Exactly one refuses. ──────────
            foreach (var executing in new[] { true, false })
                foreach (var anotherPeer in new[] { true, false })
                {
                    bool got = TacticalActorDrive.PeerMayCommand(executing, anotherPeer);
                    bool want = !(executing && anotherPeer);
                    if (got == want) continue;
                    string row = "executing=" + executing + " anotherPeerOwnsIt=" + anotherPeer;
                    if (got && !want)
                        yield return "L146 second-command-lands (" + row + "): a soldier already running ANOTHER " +
                                     "peer's order accepts this peer's command. That is the reported freeze and " +
                                     "teleport — the second activation either queues soloAfterCurrent or cancels " +
                                     "the first mid-walk, and the host's settle then drags every peer to where " +
                                     "the second order pointed, not where the first one was sent.";
                    else if (!got && !executing)
                        yield return "L146 idle-actor-is-locked (" + row + "): a soldier with nothing executing is " +
                                     "refused. The lock is only allowed to exist BECAUSE it ends by itself when " +
                                     "the action does; one that survives an idle actor is a lock on a human, " +
                                     "which the NO QUORUMS mandate forbids outright.";
                    else
                        yield return "L146 own-soldier-is-locked (" + row + "): the peer whose OWN order is " +
                                     "running cannot command its own soldier through the seam that is supposed " +
                                     "to arbitrate between DIFFERENT peers.";
                }

            // ── (b) THE RELEASE, stated as the thing that ends it ─────────────
            // The lock's whole legitimacy is that it opens on the engine's own state. Assert both directions:
            // it must be shut while the foreign action runs, and open the instant it stops.
            if (!TacticalActorDrive.PeerMayCommand(false, true))
                yield return "L146 lock-outlives-the-action: ownership survives the order it belongs to — the " +
                             "actor is no longer executing anything and a second peer is still refused. That is " +
                             "the falsification this law exists for in the other direction: a lock that does not " +
                             "release is exactly the blocker postulate 2 forbids, and it would be permanent, " +
                             "because nothing else ever clears it.";
            if (TacticalActorDrive.PeerMayCommand(true, true))
                yield return "L146 outcome-is-vacuous: the one row this law exists for — busy with another " +
                             "peer's order — is permitted, so every other arm passes over a defect still there.";

            // ── (c) THE REFUSAL IS VISIBLE, on BOTH sides of the wire ─────────
            var asm = drive.Assembly;
            if (!Program.Callees(prefix, asm).Any(c => c.Name == "ShowToast"))
                yield return "L146 refusal-is-silent: the local gate refuses a click and shows the player " +
                             "nothing. A dead click with no word is this repo's dominant bug class — it was " +
                             "already measured once (five refused shots in 45 s while the host answered itself " +
                             "in its own log, law L123) and this seam refuses at exactly the same rate.";
            if (!Program.Callees(prefix, asm).Any(c => c.MetadataToken == refuse.MetadataToken))
                yield return "L146 gate-does-not-consult-the-verdict: the ActivateAbility prefix no longer calls " +
                             "RefuseLocalCommand, so arm (a) executes a decision nothing makes.";
            {
                // The remote half must carry the popup bit. The 3-arg Reject overload is the QUIET one by
                // construction (IntentRail's own doc), so the host must be reaching the 4-arg one.
                var rejectNotify = typeof(Multiplayer.Network.Sync.IntentRail)
                    .GetMethod("Reject", All, null,
                               new[] { typeof(byte), typeof(ulong), typeof(string), typeof(bool), typeof(string[]) },
                               null);
                if (rejectNotify == null)
                    yield return "L146 premise-changed: IntentRail's notifying Reject overload is gone, so the " +
                                 "losing peer's refusal can no longer reach a screen at all.";
                else if (!Program.Callees(handle, asm).Any(c => c.MetadataToken == rejectNotify.MetadataToken))
                    yield return "L146 remote-refusal-is-silent: HandleActivate refuses the losing peer through " +
                                 "the QUIET Reject overload. The reason then reaches that peer's log and never " +
                                 "its screen — which is what the 22:28:30 client log shows verbatim — so the " +
                                 "player who lost the race sees a wind-up, a cancel, and no word, while the " +
                                 "player who lost it LOCALLY gets a full sentence. One race, two answers.";
            }
            // One sentence, not two hand-written ones that can drift apart.
            if (string.IsNullOrEmpty(TacticalCommandSync.BusyRefusal) ||
                TacticalCommandSync.BusyRefusal.IndexOf("first-to-act-wins", StringComparison.Ordinal) < 0)
                yield return "L146 refusal-text-drifted: TacticalCommandSync.BusyRefusal no longer names " +
                             "first-to-act-wins. It is the ONE string the host's arbitration, the reject nudge " +
                             "and the local gate all show; three copies of it is three chances to say different " +
                             "things to the two players in one race.";

            // ── (d) the gate sits on the LOCAL INPUT seam, and only there ─────
            var view = typeof(PhoenixPoint.Tactical.View.TacticalViewState);
            var target = gate.GetMethod("TargetMethod", All);
            if (target == null)
                yield return "L146 gate-has-no-target: the ActivateAbility gate declares no TargetMethod, so " +
                             "Harmony has nothing to bind and L125 is the only thing left that would notice.";
            else
            {
                var bound = target.Invoke(null, null) as MethodBase;
                if (bound == null || bound.DeclaringType != view || bound.Name != "ActivateAbility")
                    yield return "L146 gate-is-not-on-the-input-seam: the gate binds " +
                                 (bound == null ? "<nothing>" : bound.DeclaringType + "." + bound.Name) +
                                 " instead of TacticalViewState.ActivateAbility. That seam is chosen because it " +
                                 "is the ONLY path a player's click takes and NO path the engine's own ambient " +
                                 "abilities take — gate TacticalAbility.Activate instead and a busy actor stops " +
                                 "accepting its own hurt reactions and its own EndTurn, which wedges it rather " +
                                 "than protecting it.";
            }
        }
    }
}
