using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Entities;

namespace RailCheck
{
    /// <summary>
    /// L177 — THE DEPLOYMENT COUNTDOWN COMPLETES ON ITS OWN, AND ANY ONE PEER'S CANCEL STOPS IT FOR
    /// EVERYONE. NOBODY WAITS FOR ANYBODY.
    ///
    /// THE FEATURE (owner, 2026-08-07, design fixed). Pressing Deploy throws every peer into the battle
    /// immediately, and somebody who has not finished re-arming a soldier has no way to say "wait". So the
    /// press now arms a five-second countdown that every peer sees over whatever is on screen, with a CANCEL
    /// button any single peer may press for everyone.
    ///
    /// THIS LAW EXISTS BECAUSE THE FEATURE IS ONE MISREADING AWAY FROM A QUORUM (P13, laws L84/L91/L145,
    /// and the standing project mandate that forbids one outright). A countdown with a button on it looks
    /// exactly like a ready-vote, and the next person to touch it will be tempted to make it "wait until
    /// everyone has confirmed". The two are opposites, and the difference is mechanical rather than a matter
    /// of intent: PROCEEDING IS THE DEFAULT AND REQUIRES NOBODY TO ACT. Arms (a)-(c) pin that down so it
    /// cannot drift.
    ///
    ///   (a) <c>gate-not-executed</c> — <see cref="DeployCountdown.RunsNative"/> is the whole hold/release
    ///       decision and it is executed here over all four cases. Solo is untouched (a co-op-only rule that
    ///       leaked into single-player would delay every mission in the game by five seconds); a CLIENT runs
    ///       native and is then blocked by MissionSync's own capture, because the countdown belongs to the
    ///       one peer that can actually start a battle; the host holds once and releases exactly once.
    ///
    ///   (b) <c>countdown-waits-on-a-person</c>, THE MANDATE ITSELF. <see cref="DeployCountdown.DecrementDue"/>
    ///       is a comparison of two floats and must stay one, and NO method of the whole class may consult a
    ///       roster, a peer list, a ping table or a readiness flag. The moment it reads one, "the drop starts
    ///       in five seconds" becomes "the drop starts when the others are ready", which is the forbidden
    ///       shape whatever the code around it says. Asserted structurally, over every method on the type,
    ///       because the tempting edit is a one-line "…&amp;&amp; everyone acknowledged".
    ///
    ///   (c) <c>veto-is-a-vote</c> — a cancel must be a SINGLE peer's veto applied immediately, not a
    ///       tallied thing. <see cref="DeployCountdown.HandleCancel"/> must therefore validate nothing about
    ///       the sender and count nothing: it forwards straight to <c>Cancel</c>. A handler that grew a
    ///       counter, a set of peers, or a "which peer may cancel" test would be the vote, arriving as a
    ///       safety check.
    ///
    ///   (d) <c>cancel-cancels-the-mission</c> — the OTHER way this feature can do damage. Stopping the drop
    ///       must not call <c>GeoMission.Cancel</c>: that writes <c>Site.ActiveMission = null</c> and can
    ///       <c>DestroySite</c> (the reason <c>MissionCancelGate</c> exists at all), so one peer saying "hold
    ///       on, I am still equipping" would delete the mission from everybody's campaign. The countdown is
    ///       cancelled; the mission is not.
    ///
    ///   (e) POSITIVE CONTROL, <c>gate-not-consulted</c> — the arms above describe a gate. If
    ///       <c>MissionSync.CaptureLaunch</c> stops calling it, every one of them still passes while the
    ///       press launches immediately again, and the whole feature is gone with no line to say so. Same
    ///       for the driver: <c>SyncEngine.Tick</c> must reach <c>HostTick</c>, or an armed countdown never
    ///       decrements and the launch is not delayed but LOST — which is worse than the defect and would
    ///       look, from a peer's chair, exactly like the silent-swallow class this repo keeps paying for.
    ///
    /// Falsify (each verified RED, then restored): make <c>RunsNative</c> return true for an uncommitted
    /// host, or false for solo/a client → <c>gate-not-executed</c>; make <c>DecrementDue</c> non-monotone →
    /// <c>countdown-waits-on-a-person</c>; read <c>SessionManager.GetLobbyRoster</c> anywhere in
    /// <c>DeployCountdown</c> → same; give <c>HandleCancel</c> a per-peer test → <c>veto-is-a-vote</c>; call
    /// <c>GeoMission.Cancel</c> from <c>Cancel</c> → <c>cancel-cancels-the-mission</c>; drop the
    /// <c>DeployCountdown.Gate</c> line from <c>CaptureLaunch</c> or the <c>HostTick</c> line from the
    /// engine tick → <c>POSITIVE CONTROL</c>; rename a member → <c>premise-changed</c>.
    /// </summary>
    internal static class L177_TheDropCountsDownAloneAndOneVetoStopsIt
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var countdown = typeof(DeployCountdown);
            var mod = countdown.Assembly;
            var game = typeof(GeoMission).Assembly;

            var runsNative = countdown.GetMethod("RunsNative", All);
            var decrementDue = countdown.GetMethod("DecrementDue", All);
            var handleCancel = countdown.GetMethod("HandleCancel", All);
            var cancel = countdown.GetMethod("Cancel", All);
            var gate = countdown.GetMethod("Gate", All);
            var hostTick = countdown.GetMethod("HostTick", All);
            var capture = typeof(MissionSync).GetMethod("CaptureLaunch", All);
            var engineTick = mod.GetType("Multiplayer.Network.Sync.SyncEngine")?.GetMethod("Tick", All);

            if (runsNative == null || decrementDue == null || handleCancel == null || cancel == null ||
                gate == null || hostTick == null || capture == null || engineTick == null)
            {
                yield return "L177 premise-changed: one of DeployCountdown.{RunsNative,DecrementDue," +
                             "HandleCancel,Cancel,Gate,HostTick}, MissionSync.CaptureLaunch or SyncEngine.Tick " +
                             "no longer resolves. This law is the only thing standing between a five-second " +
                             "courtesy and a ready-vote, so a vacuous pass here is not a harness gap — it is " +
                             "the mandate going unenforced.";
                yield break;
            }

            // ── (a) the hold/release decision, all four cases ──────────────────────────────────────
            foreach (var c in new (bool inSession, bool isHost, bool committed, bool native, string why)[]
            {
                (false, false, false, true,
                 "SOLO must be untouched — a co-op rule that leaks into single-player delays every " +
                 "mission in the campaign by five seconds and hands the player a cancel button for a " +
                 "session of one"),
                (false, true, false, true,
                 "solo again, with IsHost incidentally true: there is nobody to warn and nothing to veto"),
                (true, false, false, true,
                 "a CLIENT must run native here and be blocked one line later by MissionSync's own capture, " +
                 "which turns the press into the 0xB8 launch intent. Holding it on the client too would " +
                 "count down twice and delay the host's own countdown by another five seconds"),
                (true, true, false, false,
                 "the HOST's uncommitted launch is the one call this gate exists to refuse — letting it " +
                 "through IS the defect, the drop with no warning and no way to stop it"),
                (true, true, true, true,
                 "the RELEASE: when the count reaches zero the host re-issues the same call with the commit " +
                 "flag set, and it must pass through untouched or the countdown eats the launch it was only " +
                 "supposed to delay"),
            })
            {
                bool got = (bool)runsNative.Invoke(null, new object[] { c.inSession, c.isHost, c.committed });
                if (got != c.native)
                    yield return "L177 gate-not-executed: RunsNative(inSession=" + c.inSession + ", isHost=" +
                                 c.isHost + ", committed=" + c.committed + ") answered " + got + ", wanted " +
                                 c.native + " — " + c.why + ".";
            }

            // ── (b) the tick is a clock, and the class knows nothing about peers ───────────────────
            if (!(bool)decrementDue.Invoke(null, new object[] { 10f, 10f }) ||
                !(bool)decrementDue.Invoke(null, new object[] { 11f, 10f }) ||
                (bool)decrementDue.Invoke(null, new object[] { 9f, 10f }))
                yield return "L177 countdown-waits-on-a-person: DecrementDue is no longer 'has this much " +
                             "local realtime passed'. It is the only input to whether the drop advances, and " +
                             "the entire no-quorum claim rests on it being monotone in the clock and blind to " +
                             "everything else: once true it stays true, so the countdown completes with every " +
                             "other peer asleep.";

            // "LobbyCountdown" added 2026-08-10 with the lobby's own five-second start countdown. It is the
            // same shape by design and shares this file's overlay widget, but it is the OPPOSITE kind of
            // thing on the one axis this arm guards: it waits on a readiness gate (legitimately — it runs
            // BEFORE the game is entered, where there is no play to block) and its cancel un-readies its
            // own sender. Reading it from here would import a readiness term into the in-game drop, which
            // is precisely the mandate violation this arm exists to make impossible.
            var peerish = new[] { "SessionManager", "PingTable", "PeerListEntry", "LobbyController",
                                  "PlayerPanel", "CountdownPanel", "LobbyCountdown" };
            foreach (var m in countdown.GetMethods(All).Concat<MethodBase>(countdown.GetConstructors(All)))
                foreach (var c in Program.Callees(m, mod))
                    if (c.DeclaringType != null && peerish.Contains(c.DeclaringType.Name))
                        yield return "L177 countdown-waits-on-a-person: DeployCountdown." + m.Name + " calls " +
                                     c.DeclaringType.Name + "." + c.Name + ". Nothing about WHO ELSE is here " +
                                     "may enter this decision — not a roster, not a readiness flag, not the " +
                                     "panel that draws the button. The countdown proceeds by itself and the " +
                                     "cancel is one peer's veto; the moment either reads the others, this is " +
                                     "the ready-vote the mandate forbids, however it is spelled.";

            // ── (c) the veto is applied, not tallied ──────────────────────────────────────────────
            var cancelCallees = Program.Callees(handleCancel, mod).ToList();
            if (!cancelCallees.Any(c => c.MetadataToken == cancel.MetadataToken && c.Module == cancel.Module))
                yield return "L177 veto-is-a-vote: HandleCancel does not simply forward to Cancel. A veto " +
                             "names no state and claims nothing about the sender, so there is nothing for the " +
                             "host to validate — and anything it grew instead (a count, a set of peers, a " +
                             "'may this peer cancel' test) is a tally, which is the one shape this feature " +
                             "must never take.";

            // ── (d) stopping the drop is not cancelling the mission ───────────────────────────────
            foreach (var m in new[] { cancel, handleCancel, hostTick, gate })
                foreach (var c in Program.Callees(m, game))
                    if (c.Name == "Cancel" && c.DeclaringType != null &&
                        typeof(GeoMission).IsAssignableFrom(c.DeclaringType))
                        yield return "L177 cancel-cancels-the-mission: DeployCountdown." + m.Name + " calls " +
                                     c.DeclaringType.Name + ".Cancel. GeoMission.Cancel:253 writes " +
                                     "Site.ActiveMission = null and can DestroySite — so 'hold on, I am still " +
                                     "equipping' would delete the mission from every peer's campaign, which is " +
                                     "precisely what MissionCancelGate exists to stop a client doing. The " +
                                     "countdown is cancelled; the mission stays exactly where it was.";

            // ── (e) POSITIVE CONTROL: something consults the gate, something drives the clock ──────
            if (!Program.Callees(capture, mod).Any(c => c.MetadataToken == gate.MetadataToken && c.Module == gate.Module))
                yield return "L177 POSITIVE CONTROL: MissionSync.CaptureLaunch never calls DeployCountdown.Gate, " +
                             "so every arm above is a rule about a gate nothing asks. GeoMission.Launch is the " +
                             "ONE model funnel every route into a battle reaches — the deployment button, the " +
                             "SkipDeploymentSelection arm, the haven paths, and the host applying a client's " +
                             "0xB8 intent — and off that funnel the countdown covers nothing at all.";
            if (!Program.Callees(engineTick, mod).Any(c => c.MetadataToken == hostTick.MetadataToken && c.Module == hostTick.Module))
                yield return "L177 POSITIVE CONTROL: SyncEngine.Tick never calls DeployCountdown.HostTick, so " +
                             "an armed countdown never decrements and never releases. That is not a delayed " +
                             "launch, it is a LOST one: the peer who pressed Deploy sits on the deployment " +
                             "screen forever with a number frozen on everybody's overlay. Driving it from the " +
                             "engine tick and not from any screen is also what keeps it independent of whether " +
                             "a single peer is looking at it.";
        }
    }
}
