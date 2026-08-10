using UnityEngine;

namespace Multiplayer.Network
{
    /// <summary>
    /// F3 — the host leaving ends every client's session and puts each one back in its OWN fresh lobby
    /// (via the one-shot <see cref="ConsumeLobbyReopen"/> flag). Wired ONCE per engine init
    /// (<see cref="AttachTo"/>) to the two triggers that converge on ONE handler:
    ///   (a) GRACEFUL — the host sends <c>PacketType.HostDisconnected</c>; routed to
    ///       <c>SessionManager.HandleHostDisconnected</c> → <c>OnHostDisconnected</c>.
    ///   (b) CRASH / LINK-LOSS — the client's only transport peer is the host (star topology), so any
    ///       client-side <c>OnClientDisconnectedNamed</c> (or a heartbeat timeout surfacing as the same
    ///       drop) means the host is gone.
    /// Both call <see cref="HandleHostLeft"/>, guarded by a one-shot <see cref="HostLeaveLatch"/> so a
    /// graceful packet followed by the transport drop returns to the menu EXACTLY once. The handler is
    /// CLIENT-only (the host returning to menu uses its own native path); on the host it is inert.
    /// </summary>
    public static class HostLeaveHandler
    {
        private static NetworkEngine _attached;
        private static readonly HostLeaveLatch _latch = new HostLeaveLatch();

        // THE SESSION DIED UNDER US, SO GIVE THE PLAYER A LOBBY BACK — not a bare main menu.
        // Set ONLY on the HandleHostLeft path (host quit / crashed / went silent) and consumed exactly
        // once by MultiplayerUI.OnMenuReady, which is the first moment the menu the lobby draws over
        // actually exists again. Deliberately NOT set by a voluntary client leave, the host closing its
        // own lobby, quitting the game, or the campaign-end degrade (ReturnToMainMenuForCampaignEnd) —
        // each of those already puts the player exactly where they asked to be.
        private static bool _reopenLobby;

        /// <summary>Read-and-clear the one-shot "put this client back in its own lobby" flag.</summary>
        public static bool ConsumeLobbyReopen()
        {
            var reopen = _reopenLobby;
            _reopenLobby = false;
            return reopen;
        }

        /// <summary>Subscribe to a freshly-initialized engine; re-arm the idempotency latch.</summary>
        public static void AttachTo(NetworkEngine engine)
        {
            if (engine == null) return;
            if (ReferenceEquals(_attached, engine)) return;

            Detach();
            _latch.Reset(); // fresh session: allow the menu-return to fire once again
            // …and DROP any unconsumed reopen. The menu rebuild it was waiting for never came (the
            // client was already at the menu, or started a new session first), and a flag that outlives
            // its own session would spring a lobby open on some unrelated later return to the menu.
            _reopenLobby = false;
            if (engine.Session != null)
                engine.Session.OnHostDisconnected += OnHostDisconnectedGraceful;
            engine.OnClientDisconnectedNamed += OnPeerDroppedMaybeHost;
            _attached = engine;
        }

        /// <summary>Drop subscriptions from the currently-attached engine (idempotent).</summary>
        public static void Detach()
        {
            if (_attached == null) return;
            if (_attached.Session != null)
                _attached.Session.OnHostDisconnected -= OnHostDisconnectedGraceful;
            _attached.OnClientDisconnectedNamed -= OnPeerDroppedMaybeHost;
            _attached = null;
        }

        // Trigger (a): the host gracefully announced session end.
        private static void OnHostDisconnectedGraceful()
        {
            HandleHostLeft();
        }

        // Trigger (b): a transport peer dropped. On a CLIENT the only peer is the host (star topology),
        // so this is the host crash/link-loss path. Inert on the host (it has many client peers — those
        // are F1 disconnect notices, handled by SessionNotifier, NOT a session-fatal host-leave).
        private static void OnPeerDroppedMaybeHost(ulong peerId, string playerName, bool wasKnown)
        {
            var engine = _attached;
            if (engine == null || engine.IsHost) return; // host side: a client dropped, not a host-leave

            // Symptom B: a CLIENT clicking LEAVE closes its only peer (the host), producing a transport
            // drop indistinguishable from a real host crash. Suppress the false "Host ended the session"
            // toast + forced reload when THIS client initiated the teardown — its own leave path
            // (OnDisconnectClicked → Disconnect/Shutdown + TeardownLobbyState) already returns it to the
            // menu. A genuine host drop the client did NOT initiate still notifies.
            if (!SessionLifecycle.ShouldNotifyHostLeft(engine.IsIntentionalDisconnect)) return;

            HandleHostLeft();
        }

        /// <summary>
        /// Trigger (c): client-side host HEARTBEAT TIMEOUT. A wedged/half-open host socket may never
        /// send FIN/RST, so the transport drop (trigger b) never fires and the client would be stranded.
        /// SessionManager.Update routes here when it has not heard from the host within the timeout. The
        /// same one-shot latch dedups this against a graceful HostDisconnected packet or a later real drop.
        /// </summary>
        public static void TriggerHostLeft(string reason = null)
        {
            HandleHostLeft(reason);
        }

        /// <summary>True once this session's host-leave has been handled (the menu return fired).</summary>
        public static bool AlreadyHandled => _latch.Handled;

        /// <summary>
        /// Campaign-end (feat-campaign-end): the session is ending by CAMPAIGN CONCLUSION — the client is
        /// about to play its own native outro / GameOver screen, and the host will tear its transport down
        /// after ITS outro. Pre-consume the same one-shot latch <see cref="HandleHostLeft"/> uses so that
        /// later transport drop / graceful HostDisconnected / heartbeat timeout is a silent no-op (no
        /// "Host ended the session" prompt, no forced menu-return yanking the client out of the outro).
        /// The latch re-arms on the next session's <see cref="AttachTo"/> as usual. Idempotent.
        /// </summary>
        public static void SuppressForCampaignEnd()
        {
            if (_latch.TryHandle())
                Debug.Log("[Multiplayer] campaign end: F3 host-leave latch pre-consumed — the host's "
                          + "post-outro teardown will not interrupt this client's ending.");
        }

        /// <summary>
        /// Campaign-end DEGRADE teardown: the native outro replay failed, so return to the Main Menu
        /// through the same <see cref="SessionEnd"/> seam every other session end uses. No notice —
        /// CampaignEndFlow.ClientSteps already showed one before degrading here.
        /// </summary>
        public static void ReturnToMainMenuForCampaignEnd() => SessionEnd.Begin(null);

        // ONE handler for both triggers. Idempotent (one-shot latch): a graceful HostDisconnected packet
        // followed by the transport drop of the same host must return to the menu only once.
        // FIX-2 (half-open): callers may pass a specific never-silent reason (e.g. heartbeat-ack timeout
        // = dead send channel); the default is the generic host-ended-session notice.
        private static void HandleHostLeft(string reason = null)
        {
            if (!_latch.TryHandle()) return; // already handled this session
            var notice = reason ?? SessionLifecycle.HostEndedSession;
            Debug.LogWarning("[Multiplayer] F3: host left the session — returning client to its own lobby. " + notice);
            // Arm BEFORE Begin: FinishLevelAndGoToLobby is asynchronous but the flag is read much later
            // (next OnMenuReady), and arming first means an early/synchronous menu rebuild cannot beat us.
            // Rides the same one-shot latch above, so the graceful packet + the transport drop + the
            // heartbeat timeout of ONE host-leave arm it once.
            _reopenLobby = true;
            SessionEnd.Begin(notice);
        }
    }
}
