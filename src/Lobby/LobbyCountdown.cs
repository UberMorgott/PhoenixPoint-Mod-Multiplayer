using Multiplayer.Network.MessageLayer;
using UnityEngine;

namespace Multiplayer.Network
{
    /// <summary>
    /// NOBODY PRESSES GO. When every player in the lobby — the host included — is ready, a five second
    /// countdown arms itself and then does exactly what the old PLAY button did
    /// (<c>MultiplayerUI.OnLobbyPlay</c>). The PLAY button is gone; readiness IS the start.
    ///
    /// THE SHAPE IS <see cref="Sync.DeployCountdown"/>'s, DELIBERATELY, because that one is already
    /// live-proven: the HOST owns the clock and writes the wire EXACTLY TWICE per countdown (the ARM and
    /// the CLEAR), and every peer counts its own display down from the arm off
    /// <c>Time.realtimeSinceStartup</c>. A per-second broadcast would repaint every peer's lobby once a
    /// second for a number, which is the defect that made the deployment countdown stop replicating its
    /// tick in the first place.
    ///
    /// WHAT IT COULD NOT BORROW IS THE TRANSPORT. DeployCountdown replicates over the 0xAC value rail, and
    /// THE RAIL IS NOT LIVE BEFORE THE SESSION STARTS — the lobby is precisely the window in which there is
    /// no rail. So this one rides two top-level packet ids of its own,
    /// <see cref="PacketType.LobbyCountdown"/> (0x49, host→all, [seconds:u8], 0 = clear) and
    /// <see cref="PacketType.LobbyCountdownCancel"/> (0x4A, client→host, no payload), modelled on
    /// <c>ClientUnready</c> 0x44 and <c>EntryTransferAbort</c> 0x47.
    ///
    /// THE CANCEL UN-READIES ITS OWN SENDER, and that is not a courtesy — it is what stops an infinite
    /// loop. The arm condition is "everyone is ready"; cancelling only the clock would leave that condition
    /// TRUE the instant the clock died, and the countdown would re-arm on the very next frame forever. So
    /// the veto is "I am not ready", spelled as a stop. This is the ONE place this feature deliberately
    /// differs from the deployment veto, which validates nothing about its sender because there is nothing
    /// there for a veto to withdraw.
    ///
    /// IT IS STILL NOT A QUORUM (P13, laws L84/L91/L145), and the distinction is the same mechanical one
    /// L177 pins down for the drop: once armed, PROCEEDING IS THE DEFAULT and requires nobody to act. The
    /// readiness it waits on is a LOBBY gate, which the owner ruling of 2026-08-07 scoped as legitimate —
    /// before the game is entered there is no play to block. Nothing here reads a roster: the single
    /// boolean it is handed is the FSM's own <c>LobbyController.CanStart</c>, projected once per frame by
    /// <c>MultiplayerUI.RefreshGateFacts</c>.
    ///
    /// IT DIES THE MOMENT THE GATE CLOSES, whatever closed it. One rule covers every edge: a peer leaves,
    /// a peer un-readies, the host swaps the save (which clears everyone's ready), the last client drops
    /// (<c>PeersReady</c> needs >= 1 live peer, so a host alone can never start — the guard that predates
    /// this feature and is untouched by it). THAT RULE IS THE SAVE ROUTE'S ALONE (see <see cref="Route"/>):
    /// a new campaign chooses no save and satisfies no start gate, so applying it there would cancel the
    /// countdown on the frame it armed.
    ///
    /// TWO THINGS CAN BE COUNTED DOWN TO, ONE CLOCK COUNTS THEM. Starting from a SAVE (the readiness gate
    /// above) and starting a NEW CAMPAIGN (the host's explicit confirm on the native new-game screen) are
    /// the same five seconds, the same overlay and the same any-peer veto; only the fire differs, so the
    /// route rides the arm (<see cref="Route"/>, the 0x49 payload's second byte) instead of being a second
    /// countdown built beside this one. They can never overlap in either order: an arm of either kind is
    /// refused while anything is running.
    /// </summary>
    internal static class LobbyCountdown
    {
        /// <summary>WHICH start a countdown is counting down to. Rides the 0x49 payload so a client's
        /// overlay names the right thing, and the host's fire dispatches to the right one.</summary>
        internal enum Route : byte
        {
            None = 0,
            /// <summary>The lobby readiness gate — <c>MultiplayerUI.OnLobbyPlay</c>, the old PLAY button.</summary>
            Save = 1,
            /// <summary>The host's confirm on the native new-game screen — the confirm is REFUSED and
            /// re-issued when this reaches zero (<c>NewCampaignInterceptPatch.CommitNewCampaign</c>).</summary>
            NewCampaign = 2,
        }

        /// <summary>Five, matching <c>DeployCountdown.CountdownSeconds</c>: long enough to reach the cancel
        /// button, short enough that nobody waits on somebody else's hesitation.</summary>
        internal const int CountdownSeconds = 5;

        /// <summary>Seconds carried by the last ARM this peer saw; 0 = nothing running. On the host it is
        /// set by <see cref="Arm"/>, on a client by <see cref="HandleCountdown"/>.</summary>
        private static int _armed;

        /// <summary>What the last ARM this peer saw was counting down to. Replicated (it is the second
        /// payload byte), because the caption a client reads is derived from it.</summary>
        private static Route _route;

        /// <summary>Peer-local display deadline (realtime). Never replicated — see the class summary.</summary>
        private static float _zeroAt;

        /// <summary>HOST-ONLY release instant. The only clock that decides anything.</summary>
        private static float _fireAt;

        /// <summary>Is a countdown in flight on THIS peer? Read off the last arm, so a client greys/shows
        /// the same thing the host does.</summary>
        internal static bool Running => _armed > 0;

        /// <summary>PURE (RailCheck L403). DOES A COUNTDOWN ARM THIS FRAME? The whole decision: only the
        /// host arms, only while the full start gate is open, and never on top of one already running.</summary>
        internal static bool ArmsFor(bool isHost, bool startGateOpen, bool running) =>
            isHost && startGateOpen && !running;

        /// <summary>PURE (RailCheck L403). The NEW CAMPAIGN arm. There is no readiness gate in it — this
        /// countdown arms from the host's explicit CONFIRM on the native new-game screen, which is its own
        /// gate (<c>SessionLifecycle.NewCampaignArmGuard</c> / <c>HostLoadGuard</c>, already applied by the
        /// caller). What it keeps is the one term both routes share: never on top of a running countdown,
        /// in either order.</summary>
        internal static bool ArmsForNewCampaign(bool isHost, bool running) => isHost && !running;

        /// <summary>PURE (RailCheck L403). DOES THIS CANCEL CLEAR THE CANCELLER'S READY? For the SAVE route
        /// it must (see the class summary: the arm condition is "everybody is ready", so a cancel that
        /// stops only the clock re-arms on the next frame, forever). For the NEW CAMPAIGN route it must
        /// NOT: that countdown arms from one explicit button press and nothing re-presses it, so there is
        /// no loop to break — and dropping three players' READY because somebody vetoed a campaign is a
        /// side effect nobody asked for.</summary>
        internal static bool CancelClearsReady(Route route) => route != Route.NewCampaign;

        /// <summary>PURE (RailCheck L403). What the overlay calls the thing it is counting down to. Five
        /// seconds and a CANCEL button must never be ambiguous about WHAT they are about to do.</summary>
        internal static string CaptionSubject(Route route) =>
            route == Route.NewCampaign ? "CAMPAIGN" : "SESSION";

        /// <summary>The caption subject for the countdown running on THIS peer (read by
        /// <c>CountdownPanel</c>).</summary>
        internal static string Subject => CaptionSubject(_route);

        /// <summary>PURE (RailCheck L403). The tick, with nothing but two numbers in it — the "waits on no
        /// human" property in one line, exactly as <c>DeployCountdown.DecrementDue</c> states it.</summary>
        internal static bool FireDue(float now, float fireAt) => now >= fireAt;

        /// <summary>What the overlay shows on THIS peer, derived from local realtime. Holds at 1 rather
        /// than reaching 0 by itself: zero is the host's word and it arrives as the CLEAR.</summary>
        internal static int DisplaySecondsLeft()
        {
            if (_armed <= 0) return 0;
            int left = Mathf.CeilToInt(_zeroAt - Time.realtimeSinceStartup);
            return left < 1 ? 1 : left;
        }

        /// <summary>
        /// HOST: arm, hold, or release. Driven from <c>MultiplayerUI.Update</c> (NOT from
        /// <c>SyncEngine.Tick</c>, which does not run before the session starts). Returns the route that
        /// fired on the one frame the count reaches zero (<see cref="Route.None"/> on every other frame) —
        /// the caller then runs that start, so the whole "what PLAY did" path stays in one place and this
        /// class starts nothing itself.
        /// </summary>
        /// <param name="startGateOpen">
        /// <c>LobbyController.CanStart</c> as of this frame: HostLobby, unlocked, >= 1 live peer, every
        /// live peer ready, the HOST ready, and a save chosen. It arms and holds the SAVE route only.
        /// </param>
        internal static Route HostTick(NetworkEngine engine, bool startGateOpen)
        {
            if (engine == null || !engine.IsHost) return Route.None;

            if (ArmsFor(true, startGateOpen, Running)) { Arm(engine, Route.Save); return Route.None; }
            if (!Running) return Route.None;

            // THE GATE CLOSED UNDER US. One rule for every edge — a peer left, a peer un-readied, the host
            // swapped the save, the last client dropped. Never silent (P1): a countdown that vanishes with
            // no line is indistinguishable from one that never armed.
            //
            // SAVE ROUTE ONLY. A new campaign picks no save and the host is not required to be READY for
            // it, so CanStart is false for the whole of that countdown — applying this to it would kill it
            // on the frame after it armed.
            if (_route == Route.Save && !startGateOpen)
            {
                MpLog.Log("[MP][lobby] countdown STOPPED — the start gate closed while it ran (somebody " +
                          "un-readied, left, or the host changed the save). Nothing was started; it re-arms " +
                          "by itself the moment every player is ready again.");
                Clear(engine);
                return Route.None;
            }

            if (!FireDue(Time.realtimeSinceStartup, _fireAt)) return Route.None;

            var fired = _route;
            MpLog.Log("[MP][lobby] countdown reached zero — " + (fired == Route.NewCampaign
                          ? "re-issuing the host's native new-game CONFIRM, which was REFUSED when it armed " +
                            "this (NewCampaignInterceptPatch.CommitNewCampaign): nothing was created until now"
                          : "starting the session down the SAME path the PLAY button used " +
                            "(MultiplayerUI.OnLobbyPlay → LobbyController.CommitStart)") +
                      ". The clear below runs FIRST so no peer is left staring at a number while the " +
                      "curtain drops.");
            Clear(engine);
            return fired;
        }

        /// <summary>
        /// HOST: arm the NEW CAMPAIGN route. Called from the native new-game CONFIRM prefix, which then
        /// REFUSES that confirm — so the settings screen stays open with nothing created and no residue,
        /// exactly as <c>DeployCountdown.Gate</c> holds a launch. Returns false when the arm was refused
        /// (something is already counting down), in which case the caller must still refuse the confirm:
        /// letting it through would be the one way to reach world creation with no countdown at all.
        /// </summary>
        internal static bool ArmNewCampaign(NetworkEngine engine)
        {
            if (engine == null || !engine.IsHost) return false;
            if (!ArmsForNewCampaign(true, Running))
            {
                // NEVER SILENT (P1): a confirm that produced neither a campaign nor a countdown must say so.
                MpLog.LogWarning("[MP][lobby] NEW CAMPAIGN confirm REFUSED — a " + CaptionSubject(_route) +
                                 " countdown is already running with " + DisplaySecondsLeft() + " s left. " +
                                 "The two never overlap in either order; cancel that one first, or let it " +
                                 "finish. Nothing was created and the settings screen is still open.");
                return false;
            }
            Arm(engine, Route.NewCampaign);
            return true;
        }

        /// <summary>The ARM — one of this countdown's two wire writes. [seconds:u8, route:u8].</summary>
        private static void Arm(NetworkEngine engine, Route route)
        {
            _armed = CountdownSeconds;
            _route = route;
            _zeroAt = Time.realtimeSinceStartup + CountdownSeconds;
            _fireAt = _zeroAt;
            engine.BroadcastToAll(new NetworkMessage(PacketType.LobbyCountdown,
                                                     new byte[] { (byte)CountdownSeconds, (byte)route }));
            MpLog.Log("[MP][lobby] " + (route == Route.NewCampaign
                          ? "the host confirmed a NEW CAMPAIGN — it is created in "
                          : "every player is ready — the session starts in ") + CountdownSeconds +
                      " s. Nobody has to press anything for it to complete (no quorum, P13): it is a clock. " +
                      "ANY ONE peer may cancel it for everyone" +
                      (CancelClearsReady(route)
                          ? ", and doing so clears that peer's own READY."
                          : "; nobody's READY changes, because this one was armed by a button press rather " +
                            "than by the readiness gate."));
        }

        /// <summary>The CLEAR — the second and last wire write of a countdown.</summary>
        private static void Clear(NetworkEngine engine)
        {
            _armed = 0;
            _route = Route.None;
            _zeroAt = 0f;
            _fireAt = 0f;
            engine?.BroadcastToAll(new NetworkMessage(PacketType.LobbyCountdown, new byte[] { 0, 0 }));
        }

        /// <summary>Client applier for 0x49: adopt the host's arm (or clear) and start counting locally.</summary>
        internal static void HandleCountdown(NetworkEngine engine, NetworkMessage msg)
        {
            if (engine == null || engine.IsHost) return;   // the host's own arm is local; it never echoes
            int seconds = msg?.Payload != null && msg.Payload.Length > 0 ? msg.Payload[0] : 0;
            if (seconds <= 0)
            {
                _armed = 0;
                _route = Route.None;
                _zeroAt = 0f;
                MpLog.Log("[MP][lobby] countdown CLEARED by the host — either it reached zero and the " +
                          "session is starting, or somebody cancelled it.");
                return;
            }
            _armed = seconds;
            // Second payload byte = the route. An older/short payload reads as the SAVE route, which is
            // what a one-byte arm meant before the new-campaign route existed.
            _route = msg.Payload.Length > 1 ? (Route)msg.Payload[1] : Route.Save;
            _zeroAt = Time.realtimeSinceStartup + seconds;
            MpLog.Log("[MP][lobby] " + CaptionSubject(_route) + " countdown ARMED by the host for " + seconds +
                      " s — this peer counts its own display down from here; the host's clock is the one " +
                      "that decides. CANCEL stops it for everyone.");
        }

        /// <summary>
        /// The cancel gesture, from either side of the wire. On the host it IS the decision; on a client it
        /// crosses as 0x4A and the host applies it. Logs before anything can drop it — the 2026-08-08 dead
        /// click (L198) was unknowable precisely because the path started at a silent method.
        /// </summary>
        internal static void RequestCancel()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive)
            {
                MpLog.LogWarning("[MP][lobby] CANCEL pressed with no active session — the veto goes nowhere. " +
                                 "The panel gates on IsActiveSession, so this is a stale overlay rather than " +
                                 "a lost cancel.");
                return;
            }
            if (engine.IsHost) { Cancel(engine, null); return; }
            MpLog.Log("[MP][lobby] CANCEL leaving this client as 0x" +
                      ((byte)PacketType.LobbyCountdownCancel).ToString("X2") + " — the host owns the " +
                      "countdown, so the veto takes one round trip and then clears it on every peer.");
            engine.SendToHost(new NetworkMessage(PacketType.LobbyCountdownCancel));
        }

        /// <summary>
        /// HOST applier. Stops the clock and — on the SAVE route — un-readies the peer that pressed it:
        /// see the class summary, without that half the arm condition is still satisfied the instant the
        /// clock dies and the countdown re-arms on the next frame, forever. On the NEW CAMPAIGN route it
        /// deliberately does NOT (<see cref="CancelClearsReady"/>): nothing re-presses a button, so there
        /// is no loop, and clearing three players' READY for a vetoed campaign is a side effect nobody
        /// asked for. <paramref name="senderPeerId"/> is null for the host's own press.
        ///
        /// It is not a tally and cannot become one: nothing here counts cancels, and the FIRST one applies.
        /// </summary>
        internal static void Cancel(NetworkEngine engine, ulong? senderPeerId)
        {
            if (engine == null || !engine.IsHost) return;
            var session = engine.Session;
            bool wasRunning = Running;
            var route = _route;
            bool clearsReady = CancelClearsReady(route);
            Clear(engine);

            string who = SessionLifecycle.UnknownPlayer;
            if (session != null)
            {
                if (senderPeerId.HasValue)
                {
                    ClientInfo c;
                    if (session.Clients.TryGetValue(senderPeerId.Value, out c) && !string.IsNullOrEmpty(c.PlayerName))
                        who = c.PlayerName;
                    if (clearsReady) session.SetClientReady(senderPeerId.Value, false);
                }
                else
                {
                    if (!string.IsNullOrEmpty(session.HostNickname)) who = session.HostNickname;
                    if (clearsReady) session.SetHostReady(false);
                }
                // Quietly, in the chat, in the same style as the join/leave notices — SystemChat and not
                // SystemNotice: a lobby cancel is not a session event worth a native toast on every screen.
                session.SystemChat(SessionLifecycle.FormatCountdownCancelledNotice(who));
            }

            MpLog.Log("[MP][lobby] " + CaptionSubject(route) + " countdown CANCELLED by " + who +
                      (wasRunning ? "" : " — but nothing was running (it had already fired, or another " +
                                         "peer's veto got here first)") +
                      (clearsReady
                          ? ". The canceller's own READY is cleared with it, which is what stops the gate " +
                            "re-arming the countdown on the very next frame."
                          : ". NOBODY'S READY CHANGES: this one armed from the host's confirm on the native " +
                            "new-game screen, so there is no gate to re-arm it — the screen is still open " +
                            "and the host can simply press confirm again."));
        }

        /// <summary>Host applier for the 0x4A packet. Reads the SENDER and nothing else about it — there is
        /// no "may this peer cancel" test to grow into a vote.</summary>
        internal static void HandleCancel(NetworkEngine engine, NetworkMessage msg)
        {
            if (engine == null || !engine.IsHost || msg == null) return;
            Cancel(engine, msg.SenderSteamId);
        }

        /// <summary>Local-only reset at a lobby teardown boundary: no broadcast (there may be no transport
        /// left), just make sure a torn-down session leaves no number frozen on the next lobby's overlay.</summary>
        internal static void Reset()
        {
            _armed = 0;
            _route = Route.None;
            _zeroAt = 0f;
            _fireAt = 0f;
        }
    }
}
