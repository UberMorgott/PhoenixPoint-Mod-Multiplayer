using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using Multiplayer.Tactical;
using PhoenixPoint.Tactical.View.ViewModules;

namespace RailCheck
{
    /// <summary>
    /// L119 — THE READY COUNT IS A LABEL, AND IT MUST STAY ONE.
    ///
    /// The tactical screen grew a second button under the native End Turn one: "I have finished moving".
    /// The developer's framing is the whole specification — "it is ONLY a visual indicator of how many
    /// players have finished their moves… it doesn't oblige anything and doesn't force any game code to
    /// run". A courtesy so nobody has to type "are you done?" in chat.
    ///
    /// WHY IT NEEDS A LAW AT ALL. The shape of a ready count is one `if` away from the exact bug class this
    /// repo removed on 2026-08-05: `if (ready &lt; total) return;` in front of the End Turn button is a
    /// QUORUM, and a quorum is a hostage — L84 ("nobody waits for anybody", built from the ready vote and
    /// the LOADED barrier that held fifty players behind one AFK download) and L91 ("a host-side decision
    /// about peer P may read P's own intent and the shared game state, and nothing else"). The feature is
    /// harmless; the temptation is not, and it is the kind that arrives as a "small improvement" six months
    /// from now. So the negative is asserted mechanically instead of trusted to a comment.
    ///
    /// THE ARMS:
    ///   (a) PREMISE — the widget really is the game's own End Turn button, cloned at the module's own
    ///       Awake. A law about a native widget that has been replaced by a hand-rolled overlay is asserting
    ///       something about code that no longer exists.
    ///   (b) A PEER'S TOGGLE IS VISIBLE TO EVERY PEER'S LABEL — the OUTCOME, followed link by link across
    ///       both roles: client emits on the existing 0x81 family → the op is registered there → the host
    ///       records it on that peer's own seat → the host ships the aggregate on 0x80 → the receiving peer
    ///       APPLIES it and REPAINTS ON ARRIVAL. Each link is separately falsifiable, because each one on
    ///       its own produces the same silent symptom: a counter that never moves for somebody.
    ///   (c) THE FLAGS RESET ON A NEW PLAYER ROUND, off the game's own turn edge, and NOT off anything else
    ///       (an alien turn clearing the flags mid-round is the same bug wearing the other hat).
    ///   (d) THE ARM THAT MATTERS: NOTHING READS THE COUNT TO DECIDE ANYTHING. A set equality over every
    ///       method in the mod assembly that so much as TOUCHES the two counters, the local flag, the tally
    ///       or the per-peer field — a write is as suspicious as a read here, because a decision that
    ///       latches the count is the same quorum one variable further on. Plus the two tactical arbiters
    ///       EXECUTED against a hostile tally (0 of 99 ready, then 99 of 99) to prove their answers do not
    ///       move: an unrun assertion about indifference is a comment.
    ///
    /// Falsify (each verified to go RED, then restored):
    ///   • delete the <c>IntentRail.Send</c> arm of <c>Toggle</c> → <c>toggle-invisible-to-others</c>
    ///   • drop op 3 from <c>TacticalTurnSync.RegisterIntents</c> → <c>op-not-registered</c>
    ///   • stop <c>ApplyTally</c> calling <c>Repaint</c> → <c>label-not-reactive</c>
    ///   • remove the <c>TacticalReadySync.OnNewTurn</c> call from <c>TacNewTurnHook</c> → <c>no-round-reset</c>
    ///   • drop the faction guard in <c>OnNewTurn</c> → <c>reset-fires-off-round</c>
    ///   • read <c>ReadyCount</c> from any other method → <c>count-read-outside-the-label</c>
    ///   • gate <c>TacticalTurnSync.Validate</c> on the tally → <c>arbiter-reads-the-count</c>
    /// </summary>
    internal static class L119_AdvisoryReadyLabel
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>The ONLY methods allowed anywhere near the two label counters. <c>Repaint</c> reads them;
        /// the other three only ever write them. Anything else is a consumer, and a consumer of a count is
        /// the quorum this law exists to keep out.</summary>
        private static readonly string[] CountTouchers =
        {
            "TacticalReadyButton.Repaint",
            "TacticalReadySync.ApplyTally",
            "TacticalReadySync.HostBroadcastTally",
            "TacticalReadySync.Reset",
        };

        /// <summary>This peer's OWN flag: the click that flips it, the round edge that drops it, the teardown,
        /// and the paint that turns the button green. Nothing may branch on it either — "I am ready" gating
        /// my own input is a self-inflicted version of the same rule.</summary>
        private static readonly string[] FlagTouchers =
        {
            "TacticalReadyButton.Repaint",
            "TacticalReadySync.OnNewTurn",
            "TacticalReadySync.Reset",
            "TacticalReadySync.Toggle",
        };

        internal static IEnumerable<string> Check()
        {
            var sync = typeof(TacticalReadySync);
            var button = typeof(TacticalReadyButton);
            var mod = sync.Assembly;

            var readyCount = sync.GetField("ReadyCount", All);
            var totalCount = sync.GetField("TotalCount", All);
            var localReady = sync.GetField("LocalReady", All);
            var toggle = sync.GetMethod("Toggle", All);
            var handleSetReady = sync.GetMethod("HandleSetReady", All);
            var broadcast = sync.GetMethod("HostBroadcastTally", All);
            var applyTally = sync.GetMethod("ApplyTally", All);
            var onNewTurn = sync.GetMethod("OnNewTurn", All);
            var repaint = button.GetMethod("Repaint", All);
            var build = button.GetMethod("Build", All);
            var register = typeof(TacticalTurnSync).GetMethod("RegisterIntents", All);
            var inbound = typeof(TacticalTurnSync).GetMethod("HandleInbound", All);
            var turnHook = mod.GetType("Multiplayer.Tactical.TacNewTurnHook");
            var hookPostfix = turnHook?.GetMethod("Postfix", All);

            if (readyCount == null || totalCount == null || localReady == null || toggle == null ||
                handleSetReady == null || broadcast == null || applyTally == null || onNewTurn == null ||
                repaint == null || build == null || register == null || inbound == null || hookPostfix == null)
            {
                yield return "L119 premise-changed: the advisory ready family no longer resolves " +
                             "(TacticalReadySync.ReadyCount/TotalCount/LocalReady/Toggle/HandleSetReady/" +
                             "HostBroadcastTally/ApplyTally/OnNewTurn, TacticalReadyButton.Build/Repaint, " +
                             "TacticalTurnSync.RegisterIntents/HandleInbound, TacNewTurnHook.Postfix). Every arm " +
                             "below would pass vacuously, so the no-quorum guarantee is UNCHECKED rather than " +
                             "satisfied — re-point this law before assuming the count is still just a label";
                yield break;
            }

            // ═══ (a) IT IS THE GAME'S OWN END TURN BUTTON, CLONED AT THE MODULE'S OWN AWAKE ═══
            var nativeButton = typeof(UIModuleEndTurnContainer).GetField("Button", All);
            var nativeAwake = typeof(UIModuleEndTurnContainer).GetMethod("Awake", All);
            if (nativeButton == null || nativeAwake == null)
                yield return "L119 native-widget-gone: UIModuleEndTurnContainer.Button / .Awake no longer " +
                             "resolve. The ready button is a CLONE of the game's own End Turn button and is " +
                             "built on that module's own Awake — if either moved, the button is either absent " +
                             "or hand-rolled, and the project's native-UI-first rule was broken silently";
            else
            {
                if (!Program.ReadsField(build, nativeButton))
                    yield return "L119 not-the-native-button: TacticalReadyButton.Build never touches " +
                                 "UIModuleEndTurnContainer.Button, so whatever it puts on screen is not a clone " +
                                 "of the game's End Turn button. A from-code overlay is exactly what this " +
                                 "project's native-UI-first rule forbids, and it also loses the free visibility " +
                                 "that living inside the End Turn container's own subtree buys (enemy turn, " +
                                 "cinematics and tutorial lockouts hide it with no rule of ours in the middle)";

                var patch = mod.GetType("Multiplayer.Tactical.EndTurnContainerAwakePatch")
                               ?.GetCustomAttributes(typeof(HarmonyPatch), inherit: false)
                               .Cast<HarmonyPatch>().Select(a => a.info).FirstOrDefault();
                if (patch == null || patch.declaringType != typeof(UIModuleEndTurnContainer) ||
                    patch.methodName != "Awake")
                    yield return "L119 build-seam-moved: EndTurnContainerAwakePatch no longer patches " +
                                 "UIModuleEndTurnContainer.Awake (it patches " +
                                 (patch?.declaringType?.Name ?? "nothing") + "." + (patch?.methodName ?? "?") +
                                 "). Awake is the one moment the native code itself treats Button as linked; " +
                                 "anywhere earlier clones a null and anywhere later is a poll";
            }

            // ═══ (b) A TOGGLE ON ANY PEER REACHES EVERY PEER'S LABEL ═══
            // (b1) BOTH roles emit. The host has no intent to send and the client has nothing to broadcast,
            // so a Toggle missing either arm leaves exactly one role's press invisible to everybody else.
            if (!Program.Callees(toggle, mod).Any(c => c.DeclaringType == typeof(IntentRail) && c.Name == "Send"))
                yield return "L119 toggle-invisible-to-others: TacticalReadySync.Toggle does not reach " +
                             "IntentRail.Send, so a CLIENT pressing the button changes its own green and " +
                             "nothing else — every other peer's counter sits still and the button silently " +
                             "becomes a single-player toy";
            if (!Program.Callees(toggle, mod).Any(c => c.MetadataToken == broadcast.MetadataToken))
                yield return "L119 toggle-invisible-to-others: TacticalReadySync.Toggle does not reach " +
                             "HostBroadcastTally, so the HOST pressing the button never ships the new " +
                             "aggregate and every client's counter is one press behind forever";

            // (b2) The op is really on the existing 0x81 family (ldftn into the op table, not a direct call).
            if (!Program.Callees(register, mod).Any(c => c.MetadataToken == handleSetReady.MetadataToken))
                yield return "L119 op-not-registered: TacticalTurnSync.RegisterIntents no longer references " +
                             "TacticalReadySync.HandleSetReady, so op " + TacticalReadySync.OpSetReady +
                             " is not in the 0x81 op table. IntentRail answers an unknown op with a REJECT — a " +
                             "client's ready press would trigger a forced re-emit of the graph and still never " +
                             "reach anybody's label";

            // (b3) The host records it against the SENDER's own seat, then ships the aggregate.
            if (!Program.Callees(handleSetReady, mod)
                        .Any(c => c.DeclaringType == typeof(SessionManager) && c.Name == "SetTacReady"))
                yield return "L119 host-does-not-record: HandleSetReady never calls SessionManager.SetTacReady, " +
                             "so the peer's flag is read off the wire and dropped. The per-peer flags live on " +
                             "the lobby roster precisely because L91 arm (c) forbids the rail from holding a " +
                             "peer-id collection — bypassing the roster means either the flag is lost or a " +
                             "peer table has grown back on the rail";
            if (!Program.Callees(handleSetReady, mod).Any(c => c.MetadataToken == broadcast.MetadataToken))
                yield return "L119 tally-not-shipped: HandleSetReady does not reach HostBroadcastTally, so the " +
                             "host records the flag and tells nobody — including the peer that pressed it";
            if (!Program.Callees(broadcast, mod)
                        .Any(c => c.DeclaringType == typeof(TacticalTurnSync) && c.Name == "Send"))
                yield return "L119 tally-not-shipped: HostBroadcastTally does not reach TacticalTurnSync.Send, " +
                             "so nothing leaves the host at all. It must ride THAT sender: a second emitter with " +
                             "its own SurfaceSeq would break the one property 0x80 exists for — a single ordered " +
                             "stream in which nothing overtakes the turn edge that resets the flags";
            if (!Program.Callees(broadcast, mod).Any(c => c.MetadataToken == repaint.MetadataToken))
                yield return "L119 label-not-reactive: HostBroadcastTally does not repaint. The host applies its " +
                             "own change natively and receives none of its own messages, so its screen is the " +
                             "one that goes stale — law 11's missing half, the same one IntentRail.HandleInbound " +
                             "had to close for every geoscape family";

            // (b4) The receiving peer APPLIES it and repaints ON ARRIVAL, not on the next open.
            if (!Program.Callees(inbound, mod).Any(c => c.MetadataToken == applyTally.MetadataToken))
                yield return "L119 tally-not-applied: TacticalTurnSync.HandleInbound does not dispatch op " +
                             TacticalReadySync.OpReadyTally + " to TacticalReadySync.ApplyTally. The bytes arrive " +
                             "and fall through the op switch into the 'unknown op' arm, which shouts that the " +
                             "peer cannot follow the battle — over a label";
            if (!Program.Callees(applyTally, mod).Any(c => c.MetadataToken == repaint.MetadataToken))
                yield return "L119 label-not-reactive: ApplyTally does not call Repaint, so the new count sits " +
                             "in a static field until something else happens to repaint the screen. Reactivity " +
                             "is first-class here (the developer asked for it in as many words): an OPEN screen " +
                             "repaints on ARRIVAL, never lazily on the next open";

            // ═══ (c) THE FLAGS RESET ON A NEW PLAYER ROUND, AND ONLY THEN ═══
            if (!Program.Callees(hookPostfix, mod).Any(c => c.MetadataToken == onNewTurn.MetadataToken))
                yield return "L119 no-round-reset: TacNewTurnHook.Postfix does not call " +
                             "TacticalReadySync.OnNewTurn, so last round's flags survive into the next one and " +
                             "the button opens the round already showing 3/3. That edge is the game's OWN " +
                             "(TacMission.OnNewTurn, raised on every peer running the native turn machine) — " +
                             "a timer or a poll in its place is a second, drifting notion of when a round starts";
            if (!Program.Callees(onNewTurn, mod)
                        .Any(c => c.DeclaringType == typeof(SessionManager) && c.Name == "ResetTacReady"))
                yield return "L119 no-round-reset: OnNewTurn never calls SessionManager.ResetTacReady, so each " +
                             "peer drops its own green while the HOST's roster still holds every seat ready — " +
                             "the counter and the buttons would disagree for a whole round";

            // EXECUTED: an AI faction's turn (or a null one) must NOT clear the flags. Same edge, wrong beat:
            // resetting there wipes everyone's answer in the middle of the round they gave it for.
            bool savedLocal = TacticalReadySync.LocalReady;
            int savedReady = TacticalReadySync.ReadyCount, savedTotal = TacticalReadySync.TotalCount;
            TacticalReadySync.LocalReady = true;
            // The guard is the FIRST line of the body and the clear is the second, so the one bit this probe
            // reads is already decided before anything else runs. Everything past that point is Unity-bound
            // (NetworkEngine.Instance's null test is a native liveness call) and cannot execute in a console
            // host — so a throw from BELOW the guard is expected and says nothing about the guard itself.
            try { TacticalReadySync.OnNewTurn(null); } catch { }
            if (!TacticalReadySync.LocalReady)
                yield return "L119 reset-fires-off-round: OnNewTurn(null) cleared this peer's flag. The reset is " +
                             "gated on the incoming faction being PLAYER-CONTROLLED; without that guard the " +
                             "aliens' turn — and every other faction edge in the battle — wipes an answer the " +
                             "players gave for the round they are still in";
            TacticalReadySync.LocalReady = savedLocal;

            // ═══ (d) NOTHING READS THE COUNT TO DECIDE ANYTHING ═══
            var all = ModMethods(mod).ToList();
            if (all.Count == 0)
            {
                yield return "L119 no-methods-swept: not one method resolves in the mod assembly, so every " +
                             "set-equality arm below passes vacuously";
                yield break;
            }

            foreach (var v in TouchSet(all, readyCount, totalCount, CountTouchers,
                                       "count-read-outside-the-label",
                                       "reads or writes the advisory ready COUNT. Exactly four methods may: " +
                                       "Repaint reads it to draw the label, and ApplyTally / HostBroadcastTally / " +
                                       "Reset only ever write it. Any other toucher is a CONSUMER of a peer " +
                                       "tally, which is a quorum — the shape L84 and L91 both forbid, and the " +
                                       "one this feature is one `if` away from at all times"))
                yield return v;

            foreach (var v in TouchSet(all, localReady, null, FlagTouchers,
                                       "flag-read-outside-the-label",
                                       "reads or writes this peer's own advisory ready flag. Exactly four " +
                                       "methods may: the click that flips it, the round edge that drops it, the " +
                                       "battle teardown, and the paint that turns the button green. Branching on " +
                                       "it anywhere else makes a player's own courtesy press change what that " +
                                       "player is allowed to do"))
                yield return v;

            var tally = typeof(SessionManager).GetMethod("TacReadyTally", All);
            var perPeer = typeof(ClientInfo).GetMethod("get_TacReady", All);
            if (tally == null || perPeer == null)
                yield return "L119 premise-changed: SessionManager.TacReadyTally / ClientInfo.TacReady no longer " +
                             "resolve, so the two arms that keep the RAW per-peer flags out of every decision " +
                             "check nothing at all";
            else
            {
                foreach (var v in CallerSet(all, tally, new[] { "TacticalReadySync.HostBroadcastTally" },
                                            "tally-consumed-elsewhere",
                                            "calls SessionManager.TacReadyTally. Exactly one method may — the " +
                                            "one that stamps the two label fields and ships them. A second " +
                                            "caller is somebody asking 'how many are ready?' for a reason other " +
                                            "than painting it, and there is no such legitimate reason"))
                    yield return v;

                // BuildPeerList joined this list when the player panel landed (2026-08-07). It reads the flag
                // to PUBLISH it verbatim on the roster every peer already receives — the one reason that is
                // not a decision about the peer, because it reaches a status column and stops there. The
                // panel that renders it is in L158's presentation-seam set for exactly that reason, and the
                // WRITERS (SetTacReady / ResetTacReady) are deliberately unconditional so that neither of
                // them has to read the flag to skip a write.
                foreach (var v in CallerSet(all, perPeer,
                                            new[] { "SessionManager.TacReadyTally", "SessionManager.BuildPeerList" },
                                            "per-peer-flag-read-elsewhere",
                                            "reads ClientInfo.TacReady. Only the tally and the roster builder may " +
                                            "— one collapses the flags into two numbers for a label, the other " +
                                            "copies them onto the roster for a status column. Reading an " +
                                            "individual peer's flag anywhere else is a host decision about that " +
                                            "peer taken from something other than its own intent and the shared " +
                                            "game state, which is L91's sentence verbatim"))
                    yield return v;
            }

            // EXECUTED, AND THE POINT OF THE WHOLE LAW: both tactical arbiters must answer identically with a
            // hostile tally in place. An IL set-equality proves nobody reads the fields TODAY; this proves the
            // decisions do not move even if somebody found another route to the same numbers.
            const string g = "faction-guid-a";
            TacticalReadySync.ReadyCount = 0;
            TacticalReadySync.TotalCount = 99;
            string endTurnNobodyReady = TacticalTurnSync.Validate(g, g, true, true);
            string leaveNobodyReady = TacticalTurnSync.ValidateLeave(true, true, false, false);
            TacticalReadySync.ReadyCount = 99;
            string endTurnAllReady = TacticalTurnSync.Validate(g, g, true, true);
            string leaveAllReady = TacticalTurnSync.ValidateLeave(true, true, false, false);
            TacticalReadySync.ReadyCount = savedReady;
            TacticalReadySync.TotalCount = savedTotal;

            if (endTurnNobodyReady != null || endTurnAllReady != null || endTurnNobodyReady != endTurnAllReady)
                yield return "L119 arbiter-reads-the-count: TacticalTurnSync.Validate answers differently (or " +
                             "refuses outright) with 0 of 99 peers ready vs 99 of 99 — nobody-ready gave '" +
                             (endTurnNobodyReady ?? "accept") + "', all-ready gave '" + (endTurnAllReady ?? "accept") +
                             "'. The End Turn button must work at every instant for every peer no matter what " +
                             "the courtesy label says; the moment it does not, the label became a vote and one " +
                             "AFK player owns everybody's turn";
            if (leaveNobodyReady != null || leaveAllReady != null || leaveNobodyReady != leaveAllReady)
                yield return "L119 arbiter-reads-the-count: TacticalTurnSync.ValidateLeave answers differently " +
                             "(or refuses outright) with 0 of 99 peers ready vs 99 of 99 — nobody-ready gave '" +
                             (leaveNobodyReady ?? "accept") + "', all-ready gave '" + (leaveAllReady ?? "accept") +
                             "'. Leaving a finished battle is the peer-autonomy path (L64); gating it on a ready " +
                             "tally would strand every remaining player behind whoever forgot to press a button";
        }

        // ── The universe the set-equality arms quantify over: every method our own assembly declares,
        //    derived from metadata so a NEW consumer cannot slip past by not being on anybody's list. ──
        private static IEnumerable<MethodBase> ModMethods(Assembly mod)
        {
            Type[] types;
            try { types = mod.GetTypes(); } catch { yield break; }
            foreach (var t in types)
            {
                MethodInfo[] ms;
                ConstructorInfo[] cs;
                try { ms = t.GetMethods(All); cs = t.GetConstructors(All); } catch { continue; }
                foreach (var m in ms) if (m.DeclaringType == t) yield return m;
                foreach (var c in cs) yield return c;
            }
        }

        /// <summary>Every method that TOUCHES one of the given fields, minus the declared allowlist. A touch,
        /// not a read: <c>Program.ReadsField</c> matches any field operand, and that is the RIGHT granularity
        /// here — a decision that latches the count into a variable of its own is the same quorum one store
        /// further on, and there is no legitimate second writer either.</summary>
        private static IEnumerable<string> TouchSet(List<MethodBase> all, FieldInfo a, FieldInfo b,
                                                    string[] allowed, string tag, string why)
        {
            foreach (var m in all)
            {
                if (!Program.ReadsField(m, a) && (b == null || !Program.ReadsField(m, b))) continue;
                string who = Who(m);
                if (allowed.Contains(who, StringComparer.Ordinal)) continue;
                yield return "L119 " + tag + ": " + who + " " + why;
            }
        }

        /// <summary>Callers of one method, minus the declared allowlist.</summary>
        private static IEnumerable<string> CallerSet(List<MethodBase> all, MethodBase target,
                                                     string[] allowed, string tag, string why)
        {
            foreach (var m in all)
            {
                if (!Program.Callees(m, target.DeclaringType.Assembly)
                            .Any(c => c.MetadataToken == target.MetadataToken && c.Module == target.Module))
                    continue;
                string who = Who(m);
                if (allowed.Contains(who, StringComparer.Ordinal)) continue;
                yield return "L119 " + tag + ": " + who + " " + why;
            }
        }

        private static string Who(MethodBase m) =>
            (m.DeclaringType == null ? "?" : m.DeclaringType.Name) + "." + m.Name;
    }
}
