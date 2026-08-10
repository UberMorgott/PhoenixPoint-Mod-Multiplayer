using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;

namespace RailCheck
{
    /// <summary>
    /// L403 — THE LOBBY STARTS ITSELF WHEN EVERYONE IS READY, AND A CANCEL UN-READIES WHOEVER PRESSED IT.
    ///
    /// THE FEATURE (owner, 2026-08-10). The PLAY button is gone. The lobby footer's primary control is a
    /// READY toggle every player has — the host too — and when every row reads READY a five-second
    /// countdown arms itself and then does exactly what pressing PLAY did. Nobody presses go.
    ///
    /// THE ONE-LINE BUG THIS FEATURE IS BUILT AROUND, and the reason it needs a law of its own: the arm
    /// condition is "everyone is ready", so a cancel that stops ONLY the clock leaves that condition TRUE
    /// the instant the clock dies and the countdown RE-ARMS ON THE VERY NEXT FRAME. Forever. The veto is
    /// therefore "I am not ready", spelled as a stop — the cancel clears the CANCELLER'S OWN ready, which
    /// is the single fact that turns an infinite loop into a working button. It is also the one place this
    /// countdown deliberately differs from the deployment one L177 governs, whose veto validates nothing
    /// about its sender because there is nothing there for a veto to withdraw.
    ///
    /// AND THE SECOND THING AUTOMATION CHANGED: a gate that opens wrongly no longer offers to start a
    /// session, it STARTS one. Every guard on <c>LobbyController</c> that used to cost a wasted click now
    /// costs a launch — above all the host-is-not-alone clause, which is what stops a solo host ticking
    /// READY from counting itself down into a co-op session with nobody in it.
    ///
    ///   (a) <c>a-second-way-to-start</c> — <c>MultiplayerUI.OnLobbyPlay</c> is the ONE start path and the
    ///       countdown is its ONE caller. No widget in <c>LobbyPanel</c> may reach it: a button that starts
    ///       the session immediately is a way to skip the five seconds the other players were promised to
    ///       cancel in, whatever it is labelled.
    ///
    ///   (b) <c>countdown-arms-on-a-closed-gate</c> — <c>LobbyCountdown.ArmsFor</c> and <c>FireDue</c> are
    ///       the whole arm/release decision and are EXECUTED here. Only the host arms, only on an open
    ///       gate, never on top of a running one; and the release is a comparison of two floats, so the
    ///       countdown completes with every other peer asleep (the L177 property, restated for this clock).
    ///
    ///   (c) <c>cancel-leaves-everyone-ready</c>, THE DEFECT ITSELF. <c>LobbyCountdown.Cancel</c> must
    ///       clear the canceller's ready — <c>SetClientReady</c> for a peer, <c>SetHostReady</c> for the
    ///       host's own press — or the gate is still open and the next frame re-arms. It must also say WHO,
    ///       through the same formatter family as the join/leave notices. PER ROUTE, not unconditionally:
    ///       <c>CancelClearsReady</c> is executed here and must answer TRUE for the save route and FALSE
    ///       for the new campaign, whose countdown arms from one explicit button press — nothing
    ///       re-presses a button, so there is no loop to break and clearing three players' READY for a
    ///       vetoed campaign is a side effect nobody asked for.
    ///
    ///   (d) <c>the-veto-has-no-wire</c> — both ids must be ROUTED. The lobby countdown cannot ride the
    ///       0x67 sync rail (it is not live before the session starts), so it owns 0x49 (host→all, arm and
    ///       clear) and 0x4A (client→host, the veto). An unrouted id is a cancel that reaches
    ///       <c>NetworkEngine</c> and dies in the "Unrouted packet type" default branch.
    ///
    ///   (e) POSITIVE CONTROL, <c>nothing-drives-the-clock</c> — if <c>MultiplayerUI.Update</c> stops
    ///       calling <c>HostTick</c>, every arm above passes over a countdown that never arms and never
    ///       fires, and the lobby simply cannot start at all with the whole harness green.
    ///
    ///   (f) <c>new-campaign-skips-the-countdown</c> — THE SECOND ROUTE INTO A SHARED WORLD, added
    ///       2026-08-10. Starting a new campaign used to create the world the instant the host confirmed
    ///       the native new-game screen: no pause, no overlay, no veto, and the clients were curtained by
    ///       <c>ArmNewCampaignBootstrap</c>'s <c>BroadcastLoadBoundaryBegin</c> before anybody could object.
    ///       The host's settings cannot be captured and replayed (<c>GeoscapeGameParams</c> only exists
    ///       inside the native confirm body), so the countdown is the REFUSAL instead — the same shape
    ///       L177 pins for the drop. Four facts make "no route reaches world creation without the
    ///       countdown having fired" structural rather than a comment:
    ///         • <c>HoldsForCountdown</c> is EXECUTED and decides it: a co-op arm holds unless committed;
    ///         • the confirm prefix must call BOTH it and <c>LobbyCountdown.ArmNewCampaign</c>;
    ///         • <c>SaveTransferCoordinator.ArmNewCampaignBootstrap</c> — the bootstrap AND the client
    ///           curtain — may be reached from that prefix and nowhere else;
    ///         • <c>CommitNewCampaign</c>, which sets the latch that lets the prefix through, may be
    ///           reached from <c>MultiplayerUI.Update</c> and nowhere else, i.e. only from the fire.
    ///       Both caller sets must be NON-EMPTY, so the arm cannot pass by scanning nothing.
    ///
    /// Falsify (each turns it RED): wire any LobbyPanel button to OnLobbyPlay → <c>a-second-way-to-start</c>;
    /// make ArmsFor true for a client, for a closed gate, or on top of a running countdown, make
    /// ArmsForNewCampaign true on top of a running one, make FireDue non-monotone, or give both routes the
    /// same caption → <c>countdown-arms-on-a-closed-gate</c>; drop either ready-clearing call or the notice
    /// from Cancel, or make CancelClearsReady answer the same for both routes →
    /// <c>cancel-leaves-everyone-ready</c>; delete either case from <c>NetworkEngine.RouteMessage</c> →
    /// <c>the-veto-has-no-wire</c>; let the confirm prefix arm the bootstrap without holding, call
    /// ArmNewCampaignBootstrap from a second place, or call CommitNewCampaign from anywhere but Update →
    /// <c>new-campaign-skips-the-countdown</c>; drop the HostTick line from Update →
    /// <c>POSITIVE CONTROL</c>; rename a member → <c>premise-changed</c>.
    /// </summary>
    internal static class L403_TheLobbyStartsItselfAndACancelUnreadiesWhoPressedIt
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var mod = typeof(LobbyController).Assembly;
            var countdown = mod.GetType("Multiplayer.Network.LobbyCountdown");
            var ui = mod.GetType("Multiplayer.UI.MultiplayerUI");
            var panel = mod.GetType("Multiplayer.UI.LobbyPanel");
            var engine = mod.GetType("Multiplayer.Network.NetworkEngine");
            var session = typeof(SessionManager);

            var intercept = mod.GetType("Multiplayer.Harmony.NewCampaignInterceptPatch");
            var coord = mod.GetType("Multiplayer.Network.SaveTransferCoordinator");
            var routeType = countdown?.GetNestedType("Route", All);

            var armsFor = countdown?.GetMethod("ArmsFor", All);
            var armsForNewCampaign = countdown?.GetMethod("ArmsForNewCampaign", All);
            var cancelClearsReady = countdown?.GetMethod("CancelClearsReady", All);
            var captionSubject = countdown?.GetMethod("CaptionSubject", All);
            var armNewCampaign = countdown?.GetMethod("ArmNewCampaign", All);
            var holdsForCountdown = intercept?.GetMethod("HoldsForCountdown", All);
            var commitNewCampaign = intercept?.GetMethod("CommitNewCampaign", All);
            var confirmPrefix = intercept?.GetMethod("OnConfirm_Prefix", All);
            var armBootstrap = coord?.GetMethod("ArmNewCampaignBootstrap", All);
            var fireDue = countdown?.GetMethod("FireDue", All);
            var hostTick = countdown?.GetMethod("HostTick", All);
            var cancel = countdown?.GetMethod("Cancel", All);
            var handleCountdown = countdown?.GetMethod("HandleCountdown", All);
            var handleCancel = countdown?.GetMethod("HandleCancel", All);
            var play = ui?.GetMethod("OnLobbyPlay", All);
            var update = ui?.GetMethod("Update", All);
            var route = engine?.GetMethod("RouteMessage", All);
            var setClientReady = session.GetMethods(All)
                .FirstOrDefault(m => m.Name == "SetClientReady" && m.GetParameters().Length == 2);
            var setHostReady = session.GetMethod("SetHostReady", All);
            var systemChat = session.GetMethod("SystemChat", All);
            var notice = typeof(SessionLifecycle).GetMethod("FormatCountdownCancelledNotice", All);

            if (armsFor == null || fireDue == null || hostTick == null || cancel == null ||
                handleCountdown == null || handleCancel == null || play == null || update == null ||
                route == null || panel == null || setClientReady == null || setHostReady == null ||
                systemChat == null || notice == null || routeType == null || armsForNewCampaign == null ||
                cancelClearsReady == null || captionSubject == null || armNewCampaign == null ||
                holdsForCountdown == null || commitNewCampaign == null || confirmPrefix == null ||
                armBootstrap == null)
            {
                yield return "L403 premise-changed: one of LobbyCountdown.{Route,ArmsFor,ArmsForNewCampaign," +
                             "CancelClearsReady,CaptionSubject,ArmNewCampaign,FireDue,HostTick,Cancel," +
                             "HandleCountdown,HandleCancel}, MultiplayerUI.{OnLobbyPlay,Update}, " +
                             "NewCampaignInterceptPatch.{HoldsForCountdown,CommitNewCampaign,OnConfirm_Prefix}, " +
                             "SaveTransferCoordinator.ArmNewCampaignBootstrap, NetworkEngine.RouteMessage, " +
                             "LobbyPanel, SessionManager.{SetClientReady,SetHostReady,SystemChat} or " +
                             "SessionLifecycle.FormatCountdownCancelledNotice no " +
                             "longer resolves. Nothing else asserts that the lobby's automatic start is " +
                             "reachable, stoppable, does not re-arm the instant it is stopped, and that a new " +
                             "campaign cannot be created without it.";
                yield break;
            }

            var routeSave = Enum.Parse(routeType, "Save");
            var routeNewCampaign = Enum.Parse(routeType, "NewCampaign");

            // ── (a) ONE START PATH, ONE CALLER ─────────────────────────────────────────────────────
            foreach (var m in AllMethods(panel))
                if (Program.Callees(m, mod).Any(c => c.MetadataToken == play.MetadataToken &&
                                                     c.Module == play.Module))
                    yield return "L403 a-second-way-to-start: LobbyPanel." + m.Name + " calls " +
                                 "MultiplayerUI.OnLobbyPlay. The PLAY button was REMOVED, not hidden: with a " +
                                 "countdown that arms the moment everybody is ready, any control that starts " +
                                 "the session directly is a way to skip the five seconds every other player " +
                                 "was promised to cancel in.";

            if (!Program.Callees(update, mod).Any(c => c.MetadataToken == play.MetadataToken &&
                                                       c.Module == play.Module))
                yield return "L403 a-second-way-to-start: MultiplayerUI.Update never reaches OnLobbyPlay, so " +
                             "nothing starts the session when the countdown reaches zero. The countdown was " +
                             "specified as 'do exactly what pressing PLAY does today', which is why the start " +
                             "stayed in one method with the clock as its only caller — a second start path " +
                             "written beside it would drift from this one the first time either changed.";

            // ── (b) THE ARM AND THE RELEASE, EXECUTED ──────────────────────────────────────────────
            foreach (var c in new (bool isHost, bool open, bool running, bool arms, string why)[]
            {
                (false, true, false, false,
                 "a CLIENT must never arm — the countdown is the host's clock and the host is the only peer " +
                 "that can actually start the session; a client arming one would show every peer a number " +
                 "nothing was counting"),
                (true, false, false, false,
                 "a CLOSED gate must not arm. This is the whole safety of the feature: the gate is what " +
                 "knows the host is not alone, that every live peer readied, and that a save was chosen"),
                (true, true, true, false,
                 "a countdown already running must not be re-armed. Re-arming resets the clock, so a gate " +
                 "that flickers open every frame would hold the count at five forever"),
                (true, true, false, true,
                 "the ARM: host, open gate, nothing running. If this is false the lobby can never start at " +
                 "all and the READY toggles do nothing"),
            })
            {
                bool got = (bool)armsFor.Invoke(null, new object[] { c.isHost, c.open, c.running });
                if (got != c.arms)
                    yield return "L403 countdown-arms-on-a-closed-gate: ArmsFor(isHost=" + c.isHost +
                                 ", startGateOpen=" + c.open + ", running=" + c.running + ") answered " + got +
                                 ", wanted " + c.arms + " — " + c.why + ".";
            }

            // The NEW CAMPAIGN arm has no readiness gate in it — the host's confirm IS its gate — but it
            // keeps the one term both routes share.
            foreach (var c in new (bool isHost, bool running, bool arms, string why)[]
            {
                (false, false, false,
                 "a CLIENT must never arm — a client cannot confirm the host's new-game screen at all, and " +
                 "the confirm intercept blocks it long before this"),
                (true, true, false,
                 "the TWO COUNTDOWNS MUST NEVER RUN AT ONCE, in either order. The confirm that finds one " +
                 "already running is refused outright rather than let through, so this false is what keeps " +
                 "a campaign from being created behind a countdown that was counting down to something else"),
                (true, false, true,
                 "the ARM: the host confirmed the native new-game screen and nothing is running. If this is " +
                 "false the confirm is refused forever and a new campaign can never be started at all"),
            })
            {
                bool got = (bool)armsForNewCampaign.Invoke(null, new object[] { c.isHost, c.running });
                if (got != c.arms)
                    yield return "L403 countdown-arms-on-a-closed-gate: ArmsForNewCampaign(isHost=" + c.isHost +
                                 ", running=" + c.running + ") answered " + got + ", wanted " + c.arms +
                                 " — " + c.why + ".";
            }

            var savedCaption = (string)captionSubject.Invoke(null, new[] { routeSave });
            var campaignCaption = (string)captionSubject.Invoke(null, new[] { routeNewCampaign });
            if (savedCaption == campaignCaption)
                yield return "L403 countdown-arms-on-a-closed-gate: both routes caption the overlay \"" +
                             savedCaption + "\", so the same five seconds and the same CANCEL button say the " +
                             "same thing whether they are about to load the chosen save or create a brand new " +
                             "campaign over it. One plate serves both countdowns precisely because the caption " +
                             "is what tells them apart.";

            if (!(bool)fireDue.Invoke(null, new object[] { 10f, 10f }) ||
                !(bool)fireDue.Invoke(null, new object[] { 11f, 10f }) ||
                (bool)fireDue.Invoke(null, new object[] { 9f, 10f }))
                yield return "L403 countdown-arms-on-a-closed-gate: FireDue is no longer 'has this much local " +
                             "realtime passed'. It is the only input to whether the start happens, and the " +
                             "no-quorum claim rests on it being monotone in the clock and blind to everything " +
                             "else: once armed, proceeding is the default and requires nobody to act (P13, and " +
                             "the same property L177 pins down for the drop).";

            // ── (c) THE CANCEL UN-READIES ITS OWN SENDER ───────────────────────────────────────────
            var cancelCallees = Program.Callees(cancel, mod).ToList();
            foreach (var want in new[] { setClientReady, setHostReady })
                if (!cancelCallees.Any(c => c.MetadataToken == want.MetadataToken && c.Module == want.Module))
                    yield return "L403 cancel-leaves-everyone-ready: LobbyCountdown.Cancel never calls " +
                                 "SessionManager." + want.Name + ". Stopping the clock without clearing the " +
                                 "canceller's OWN ready leaves the arm condition — everybody is ready — true " +
                                 "at the instant the clock dies, so the countdown re-arms on the very next " +
                                 "frame and the cancel button does nothing you can see. Both halves are " +
                                 "needed: a peer's veto arrives over 0x4A and the host's own is a local press.";

            if (!cancelCallees.Any(c => c.MetadataToken == systemChat.MetadataToken && c.Module == systemChat.Module))
                yield return "L403 cancel-leaves-everyone-ready: LobbyCountdown.Cancel posts no system line. " +
                             "One peer's veto silently stops something every other player was watching count " +
                             "down; without a line naming who, the other players see a countdown vanish and " +
                             "have no way to tell a cancel from a bug.";

            // …BUT ONLY ON THE ROUTE THAT HAS A GATE TO RE-ARM IT. Executed, because "which route" is the
            // whole content of the rule and a callee scan cannot see a branch.
            if (!(bool)cancelClearsReady.Invoke(null, new[] { routeSave }))
                yield return "L403 cancel-leaves-everyone-ready: CancelClearsReady says the SAVE route's cancel " +
                             "leaves every READY standing. That is the infinite re-arm this law was written " +
                             "for: the arm condition is 'everybody is ready', so the gate is still open the " +
                             "instant the clock dies and the countdown comes back on the very next frame.";
            if ((bool)cancelClearsReady.Invoke(null, new[] { routeNewCampaign }))
                yield return "L403 cancel-leaves-everyone-ready: CancelClearsReady clears the canceller's READY " +
                             "on the NEW CAMPAIGN route too. That un-ready exists ONLY to stop the save route " +
                             "re-arming itself from a still-all-ready roster; the campaign countdown arms from " +
                             "one explicit confirm and nothing re-presses a button, so there is no loop to " +
                             "break — dropping a player's READY because somebody vetoed a campaign is a side " +
                             "effect nobody asked for, and it silently blocks the OTHER route afterwards.";

            var line = (string)notice.Invoke(null, new object[] { "Bob" });
            if (line.IndexOf("Bob", StringComparison.Ordinal) < 0)
                yield return "L403 cancel-leaves-everyone-ready: the cancel notice for a named player does not " +
                             "contain that name (got: \"" + line + "\"). It is the join/leave family of notices " +
                             "and carries the same obligation — a notice about one person that names nobody " +
                             "tells four people to go and count heads.";
            if ((string)notice.Invoke(null, new object[] { "" }) == line)
                yield return "L403 POSITIVE CONTROL: the cancel notice is the SAME string for a named player " +
                             "and for an unknown one, so the arm above would pass over a formatter that " +
                             "ignores its argument entirely.";

            // ── (d) BOTH IDS ARE ROUTED ────────────────────────────────────────────────────────────
            // Through a collection rather than as a literal comparison: `(byte)A == (byte)B` on two enum
            // members is folded by the compiler, so it would be dead code that reads like a check.
            var ids = new[] { PacketType.LobbyCountdown, PacketType.LobbyCountdownCancel }
                      .Select(p => (byte)p).Distinct().ToList();
            if (ids.Count != 2)
                yield return "L403 the-veto-has-no-wire: LobbyCountdown and LobbyCountdownCancel share one " +
                             "packet id (0x" + ids[0].ToString("X2") + "), so the host's arm and a client's " +
                             "veto are indistinguishable on the wire.";
            var routed = Program.Callees(route, mod).ToList();
            foreach (var h in new[] { handleCountdown, handleCancel })
                if (!routed.Any(c => c.MetadataToken == h.MetadataToken && c.Module == h.Module))
                    yield return "L403 the-veto-has-no-wire: NetworkEngine.RouteMessage never reaches " +
                                 "LobbyCountdown." + h.Name + ", so that packet falls into the 'Unrouted packet " +
                                 "type' default branch and is dropped with a warning nobody reads. The lobby " +
                                 "countdown needs its own top-level ids precisely because the 0x67 sync rail is " +
                                 "not live before the session starts.";

            // ── (f) A NEW CAMPAIGN CANNOT BE CREATED WITHOUT THE COUNTDOWN ─────────────────────────
            foreach (var c in new (bool armAllowed, bool committed, bool holds, string why)[]
            {
                (true, false, true,
                 "the HOLD. A co-op confirm that has not been through a countdown must be REFUSED — that " +
                 "refusal IS the countdown, because the host's difficulty/DLC choices only exist inside the " +
                 "native confirm body and cannot be captured and replayed five seconds later"),
                (true, true, false,
                 "the RELEASE, exactly once. The countdown re-invokes the same confirm with the latch set " +
                 "and it must fall through to the bootstrap arm and the native creation"),
                (false, false, false,
                 "not a co-op arm path — vanilla single player, a blocked client, a transfer in flight. " +
                 "Holding there would refuse confirms this file already answers, including the " +
                 "single-player new game the mod must never touch"),
                (false, true, false,
                 "a stale latch on a non-arm path changes nothing"),
            })
            {
                bool got = (bool)holdsForCountdown.Invoke(null, new object[] { c.armAllowed, c.committed });
                if (got != c.holds)
                    yield return "L403 new-campaign-skips-the-countdown: HoldsForCountdown(coopArmAllowed=" +
                                 c.armAllowed + ", committed=" + c.committed + ") answered " + got + ", wanted " +
                                 c.holds + " — " + c.why + ".";
            }

            var prefixCallees = Program.Callees(confirmPrefix, mod).ToList();
            foreach (var want in new[] { holdsForCountdown, armNewCampaign })
                if (!prefixCallees.Any(x => x.MetadataToken == want.MetadataToken && x.Module == want.Module))
                    yield return "L403 new-campaign-skips-the-countdown: NewCampaignInterceptPatch." +
                                 "OnConfirm_Prefix never calls " + want.Name + ". The native new-game CONFIRM " +
                                 "is the ONE convergence point of every route into campaign creation (the " +
                                 "lobby button, the main menu, TFTV's own re-invoke) and there is no pause " +
                                 "anywhere behind it: the world exists the moment that body runs. The prefix " +
                                 "refusing is therefore the only place a countdown can live.";

            // The two doors, from metadata rather than from a hand-written list.
            var callers = CallersOf(mod, armBootstrap, commitNewCampaign);
            foreach (var c in new (MethodBase target, string only, string why)[]
            {
                (armBootstrap, "NewCampaignInterceptPatch.OnConfirm_Prefix",
                 "arming the bootstrap is what curtains every client (BroadcastLoadBoundaryBegin) and what " +
                 "turns the next geoscape into everybody's campaign. Reached from anywhere but the confirm " +
                 "prefix — which holds unless a countdown released it — and a world is created behind a " +
                 "curtain that no peer was given five seconds to stop"),
                (commitNewCampaign, "MultiplayerUI.Update",
                 "CommitNewCampaign sets the latch that lets the prefix through, so it IS the door to world " +
                 "creation. Its only caller must be the frame the countdown reaches zero; a second caller is " +
                 "a way to create a campaign with no countdown at all, whatever it is labelled"),
            })
            {
                var hits = callers[c.target.MetadataToken];
                if (hits.Count == 0)
                    yield return "L403 new-campaign-skips-the-countdown: NOTHING in the mod calls " +
                                 c.target.Name + " — the arm scanned an empty caller set, so its green means " +
                                 "nothing, and the new-campaign route is dead code or has moved.";
                else if (hits.Count != 1 || hits[0] != c.only)
                    yield return "L403 new-campaign-skips-the-countdown: " + c.target.Name + " is called from [" +
                                 string.Join(", ", hits) + "], expected exactly " + c.only + " — " + c.why + ".";
            }

            // ── (e) POSITIVE CONTROL: something drives the clock ───────────────────────────────────
            if (!Program.Callees(update, mod).Any(c => c.MetadataToken == hostTick.MetadataToken &&
                                                       c.Module == hostTick.Module))
                yield return "L403 POSITIVE CONTROL: MultiplayerUI.Update never calls LobbyCountdown.HostTick, " +
                             "so nothing arms, ticks or releases the countdown and every arm above is a rule " +
                             "about a clock that does not run. With the PLAY button gone that is not a degraded " +
                             "lobby, it is a lobby that cannot start at all — and it would read fully green. " +
                             "It is driven from Update and NOT from the lobby panel's Refresh for the same " +
                             "reason L177 drives the drop from the engine tick: a countdown must not depend on " +
                             "any screen being open on any peer.";
        }

        /// <summary>Who calls each target, over EVERY method the mod assembly declares — the universe from
        /// metadata, so a new caller cannot slip past arm (f). One IL pass for all targets.</summary>
        private static Dictionary<int, List<string>> CallersOf(Assembly mod, params MethodBase[] targets)
        {
            var byToken = targets.ToDictionary(t => t.MetadataToken, t => new List<string>());
            foreach (var t in mod.GetTypes())
                foreach (var m in AllMethods(t))
                {
                    if (m.DeclaringType != t) continue;      // nested types are visited on their own turn
                    string who = t.Name + "." + m.Name;
                    foreach (var callee in Program.Callees(m, mod))
                    {
                        List<string> hits;
                        if (byToken.TryGetValue(callee.MetadataToken, out hits) && !hits.Contains(who))
                            hits.Add(who);
                    }
                }
            foreach (var hits in byToken.Values) hits.Sort(StringComparer.Ordinal);
            return byToken;
        }

        /// <summary>Every method of a type INCLUDING its compiler-generated nested display classes — a
        /// button's onClick is a lambda, and depending on what it captures it lands either on the type
        /// itself or on a closure class beneath it. Arm (a) has to see both.</summary>
        private static IEnumerable<MethodBase> AllMethods(Type t)
        {
            foreach (var m in t.GetMethods(All)) yield return m;
            foreach (var c in t.GetConstructors(All)) yield return c;
            foreach (var n in t.GetNestedTypes(All))
                foreach (var m in AllMethods(n)) yield return m;
        }
    }
}
