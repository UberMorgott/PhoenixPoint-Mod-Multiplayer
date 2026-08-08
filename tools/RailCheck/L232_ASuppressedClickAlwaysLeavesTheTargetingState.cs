using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;

namespace RailCheck
{
    /// <summary>
    /// L232 — A PEER THAT SUPPRESSED ITS NATIVE STATE SWITCH ALWAYS ENDS UP OUT OF THE TARGETING STATE.
    ///
    /// THE OUTCOME, NOT THE CALL. A9/L230 made <c>ClickedOrderWaitsForTheEcho</c> skip the whole native
    /// <c>TacticalViewState.ActivateAbility</c> body — <c>SwitchToState(new UIStateWaiting())</c> at :274-275
    /// included — so the clicking peer STAYS in <c>UIStateShoot</c> / <c>UIStateAbilitySelected</c> /
    /// <c>UIStateOverwatchAbilitySelected</c> and <c>TacticalCommandSync.ReleaseLocalUiHolding</c> is the ONLY
    /// exit that exists anywhere in the mod. L139 already asserts that the MIRROR reaches it. It was green for
    /// a week while two other answers did not, and the symptom was a player with dead input for the rest of
    /// the battle:
    ///
    ///  • THE REFUSED ORDER ON A CLIENT. The host rejects and sends only <c>HostSettle(forced: true)</c> — no
    ///    0x82 mirror is ever produced. <c>QueueSettle</c> called <c>NoteEchoArrived(key)</c> at ARRIVAL, which
    ///    removed the <c>_awaitingEcho</c> entry and with it the 12 s ECHO LOST release; and
    ///    <c>ApplySettle</c>'s own release sat behind <c>actor.HasExecutingAbility()</c>, a guard written when
    ///    the acting peer still played speculatively. A9 retired the speculation, so that actor is IDLE in
    ///    exactly the case that needs the release. Wait cleared, no mirror, no release, no exit.
    ///  • THE HOST, ON EVERY ORDER INCLUDING ACCEPTED ONES. <c>HandleInbound</c> and <c>ClientTick</c> both
    ///    return early for the host, so <c>ApplyActivate</c>, <c>TickEchoWaits</c> and <c>ApplySettle</c> are
    ///    client paths one and all; <c>HandleActivate</c>'s four exits contained no release; and
    ///    <c>_awaitingEcho</c> is never armed on the host, which made its one remaining call site dead code
    ///    there. The host had NO exit from a targeting state at all.
    ///
    /// ARM (a) IS THE LAW. <see cref="TacticalCommandSync.SuppressedClickIsStranded"/> is pure, so it is RUN,
    /// all 32 rows: every answer a suppressed click can get must leave this peer out of the state, on the host
    /// and on a client, on ACCEPT and on REFUSAL. The single TRUE row — a client still inside the ceiling with
    /// nothing back yet — is the honest transient and is what stops the law passing vacuously.
    ///
    /// ARM (b) pins the four seams the rows name to the one release, because a decision that says "released
    /// here" over a seam that does not call it is L137's shape again.
    ///
    /// ARM (c) IS THE REGRESSION ITSELF, stated as two halves of one fact: the disarm must live WHERE THE
    /// RELEASE IS. <c>QueueSettle</c> may not clear the echo wait (it clears the bound and releases nothing);
    /// <c>ApplySettle</c> must (it is the release site). This is the exact edit that turned a refused order
    /// into a battle-long lock-up, and it is one line in each direction.
    ///
    /// ARM (d) — ONE PREFIX ON THE CLICK SEAM. <c>TacticalActorDrive.BusyActorBelongsToThePeerThatStartedIt</c>
    /// used to patch <c>TacticalViewState.ActivateAbility</c> a SECOND time, unprioritised, alongside
    /// <c>ClickedOrderWaitsForTheEcho</c>. Harmony stops at the first prefix that returns false and the order
    /// between two unprioritised patch classes is undeclared, so on any given load exactly one of the local
    /// drive gate and the echo publish silently did not run — and both of them deliberately leave the peer's
    /// state where it was, which made the two stories indistinguishable on screen. The verdict now lives at the
    /// top of <c>PublishClickedOrder</c>, i.e. inside the one prefix. This arm counts the patch classes
    /// mechanically so a second one cannot come back by being added rather than by being argued for.
    ///
    /// Falsify: make <see cref="TacticalCommandSync.SuppressedClickIsStranded"/> return true for the host row
    /// → <c>L232 host-has-no-exit</c>; for the settle row → <c>L232 refused-order-has-no-exit</c>; for the
    /// mirror row → <c>L232 mirror-has-no-exit</c>; make it return false always → <c>L232 outcome-is-vacuous</c>;
    /// delete the release from <c>PublishClickedOrder</c> → <c>L232 seam-does-not-release</c>; put
    /// <c>NoteEchoArrived</c> back into <c>QueueSettle</c> → <c>L232 disarm-outlives-the-release</c>; add a
    /// second patch class on <c>ActivateAbility</c> → <c>L232 two-prefixes-on-one-click</c>.
    /// </summary>
    internal static class L232_ASuppressedClickAlwaysLeavesTheTargetingState
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var cmd = typeof(TacticalCommandSync);
            var stranded = cmd.GetMethod("SuppressedClickIsStranded", All);
            var release = cmd.GetMethod("ReleaseLocalUiHolding", All);
            var publish = cmd.GetMethod("PublishClickedOrder", All);
            var applyActivate = cmd.GetMethod("ApplyActivate", All);
            var applySettle = cmd.GetMethod("ApplySettle", All);
            var tickEcho = cmd.GetMethod("TickEchoWaits", All);
            var queueSettle = cmd.GetMethod("QueueSettle", All);
            var noteArrived = cmd.GetMethod("NoteEchoArrived", All);
            if (stranded == null || release == null || publish == null || applyActivate == null ||
                applySettle == null || tickEcho == null || queueSettle == null || noteArrived == null)
            {
                yield return "L232 premise-changed: TacticalCommandSync.{SuppressedClickIsStranded," +
                             "ReleaseLocalUiHolding,PublishClickedOrder,ApplyActivate,ApplySettle," +
                             "TickEchoWaits,QueueSettle,NoteEchoArrived} no longer resolves. Since A9 the " +
                             "native state switch is suppressed at the click, so ReleaseLocalUiHolding is the " +
                             "only thing in this mod that ever leaves a targeting state — re-read this law " +
                             "before assuming a player can still get his screen back.";
                yield break;
            }

            // ── (a) THE OUTCOME, all 32 rows. One TRUE, and it is the transient. ──
            bool sawStranded = false;
            foreach (var suppressed in new[] { true, false })
                foreach (var isHost in new[] { true, false })
                    foreach (var mirror in new[] { true, false })
                        foreach (var settle in new[] { true, false })
                            foreach (var expired in new[] { true, false })
                            {
                                bool got = TacticalCommandSync.SuppressedClickIsStranded(
                                    suppressed, isHost, mirror, settle, expired);
                                bool want = suppressed && !isHost && !mirror && !settle && !expired;
                                if (got) sawStranded = true;
                                if (got == want) continue;
                                string row = "suppressed=" + suppressed + " host=" + isHost + " mirror=" +
                                             mirror + " settle=" + settle + " ceilingExpired=" + expired;
                                if (!got)
                                    yield return "L232 transient-is-denied (" + row + "): the one row where a " +
                                                 "client's order is legitimately still in flight reports the " +
                                                 "peer as already released. Then every other row passes by " +
                                                 "saying nothing, which is how L139 stayed green through this.";
                                else if (isHost)
                                    yield return "L232 host-has-no-exit (" + row + "): the HOST suppressed its " +
                                                 "own click's state switch and nothing puts its view back. " +
                                                 "HandleInbound and ClientTick both return early on the host, " +
                                                 "so every other release in the mod is a client path — an " +
                                                 "ACCEPTED order strands the host exactly as a refused one does.";
                                else if (mirror)
                                    yield return "L232 mirror-has-no-exit (" + row + "): the host's 0x82 mirror " +
                                                 "came back and this peer is still standing in the targeting " +
                                                 "state its click never left. ApplyActivate releases on all " +
                                                 "four of its exits precisely so this row cannot happen.";
                                else if (settle)
                                    yield return "L232 refused-order-has-no-exit (" + row + "): the host REFUSED " +
                                                 "the order, so it mirrored nothing and answered with a forced " +
                                                 "settle — and that answer left this peer aiming forever. This " +
                                                 "is the reported bug verbatim: dead input for the rest of the " +
                                                 "battle after one refused click.";
                                else
                                    yield return "L232 ceiling-does-not-release (" + row + "): the echo ceiling " +
                                                 "expired and the peer is still held. The ceiling is the last " +
                                                 "bound there is; past it nothing else is coming.";
                            }
            if (!sawStranded)
                yield return "L232 outcome-is-vacuous: no combination of answers leaves a suppressed click " +
                             "stranded, not even a client with nothing back and the ceiling not yet reached. " +
                             "The decision then permits everything and this law's green means nothing.";

            // ── (b) the four seams the rows name all reach the ONE release ────
            var mod = cmd.Assembly;
            foreach (var seam in new[] { publish, applyActivate, applySettle, tickEcho })
                if (!Program.Callees(seam, mod).Any(c => c.MetadataToken == release.MetadataToken))
                    yield return "L232 seam-does-not-release: " + seam.Name + " no longer reaches " +
                                 "ReleaseLocalUiHolding, so arm (a)'s row for it is a decision nothing makes. " +
                                 "PublishClickedOrder is the HOST's only exit (it releases after " +
                                 "HandleActivate, which covers all four of that method's exits), ApplyActivate " +
                                 "is the mirror's, ApplySettle is the refusal's and TickEchoWaits is the bound.";

            // ── (c) the disarm lives WHERE THE RELEASE IS, and nowhere else ───
            bool queueDisarms = Program.Callees(queueSettle, mod).Any(c => c.MetadataToken == noteArrived.MetadataToken);
            bool settleDisarms = Program.Callees(applySettle, mod).Any(c => c.MetadataToken == noteArrived.MetadataToken);
            if (queueDisarms)
                yield return "L232 disarm-outlives-the-release: QueueSettle clears the echo wait at ARRIVAL " +
                             "again. A settle is not a mirror — clearing the wait there deletes the 12 s ECHO " +
                             "LOST release while the settle's own release still sits one tick downstream, so a " +
                             "refused order has no exit left at all. That single line is the week-long lock-up.";
            if (!settleDisarms)
                yield return "L232 disarm-is-not-at-the-release: ApplySettle no longer clears the echo wait, so " +
                             "the wait it answers keeps ticking to its ceiling and reports an ECHO LOST for an " +
                             "order the host demonstrably answered. The disarm and the release are one fact and " +
                             "have to sit at one seam.";

            // ── (d) exactly ONE patch class on the click seam ─────────────────
            var view = typeof(PhoenixPoint.Tactical.View.TacticalViewState);
            var seamMethod = view.GetMethods(All).FirstOrDefault(m => m.Name == "ActivateAbility");
            if (seamMethod == null)
                yield return "L232 premise-changed: TacticalViewState.ActivateAbility no longer resolves, so " +
                             "the seam every clicked order passes through cannot be counted.";
            else
            {
                var patchers = new List<string>();
                foreach (var t in mod.GetTypes())
                {
                    if (BindsTo(t, view, "ActivateAbility")) patchers.Add(t.Name);
                }
                if (patchers.Count > 1)
                    yield return "L232 two-prefixes-on-one-click: " + patchers.Count + " patch classes bind " +
                                 "TacticalViewState.ActivateAbility (" + string.Join(", ", patchers.ToArray()) +
                                 "). Harmony stops at the first prefix that returns false and the order between " +
                                 "unprioritised patch classes is undeclared, so on any given load one of them " +
                                 "silently does not run — and both of them suppress the state switch, which " +
                                 "makes the two failures indistinguishable on screen. One seam, one decision: " +
                                 "fold the second verdict into PublishClickedOrder.";
                if (patchers.Count == 0)
                    yield return "L232 click-seam-unpatched: nothing in the mod binds " +
                                 "TacticalViewState.ActivateAbility any more, so no clicked order is published " +
                                 "at all and arm (d) is counting an empty set.";
            }
        }

        /// <summary>Does this type patch <paramref name="declaring"/>.<paramref name="name"/> — through the
        /// <c>[HarmonyPatch]</c> attribute or through a <c>TargetMethod</c> the class resolves itself? Both
        /// shapes are in this repo and a count that saw only one of them would be the silent skip it exists to
        /// forbid.</summary>
        private static bool BindsTo(Type t, Type declaring, string name)
        {
            try
            {
                foreach (var a in t.GetCustomAttributes(typeof(HarmonyLib.HarmonyPatch), true))
                {
                    var info = ((HarmonyLib.HarmonyPatch)a).info;
                    if (info != null && info.declaringType == declaring && info.methodName == name) return true;
                }
                var target = t.GetMethod("TargetMethod", All, null, Type.EmptyTypes, null);
                if (target == null || !target.IsStatic) return false;
                var bound = target.Invoke(null, null) as MethodBase;
                return bound != null && bound.DeclaringType == declaring && bound.Name == name;
            }
            catch { return false; }   // a type that cannot answer is not a patcher this arm can count
        }
    }
}
