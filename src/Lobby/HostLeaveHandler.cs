using System;
using Base.Core;
using Base.UI.MessageBox;
using PhoenixPoint.Common.Game;
using UnityEngine;

namespace Multiplayer.Network
{
    /// <summary>
    /// F3 — the host leaving drops every client to the Main Menu. Wired ONCE per engine init
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

        /// <summary>Subscribe to a freshly-initialized engine; re-arm the idempotency latch.</summary>
        public static void AttachTo(NetworkEngine engine)
        {
            if (engine == null) return;
            if (ReferenceEquals(_attached, engine)) return;

            Detach();
            _latch.Reset(); // fresh session: allow the menu-return to fire once again
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
        /// Campaign-end DEGRADE teardown (notice shown first — CampaignEndFlow.ClientSteps ordering): the
        /// native outro replay failed, so return to the Main Menu via the SAME native quit-to-menu
        /// chokepoint <see cref="HandleHostLeft"/> uses. The existing FinishLevelAndGoToLobbyTearDownPatch
        /// postfix auto-runs NetworkEngine.TearDown(); the defensive TearDown belt mirrors HandleHostLeft.
        /// </summary>
        public static void ReturnToMainMenuForCampaignEnd()
        {
            try
            {
                var game = GameUtl.GameComponent<PhoenixGame>();
                game?.FinishLevelAndGoToLobby();
            }
            catch (Exception e) { Debug.LogError("[Multiplayer] campaign-end return-to-menu failed: " + e.Message); }
            try { NetworkEngine.Instance?.TearDown(); }
            catch (Exception e) { Debug.LogError("[Multiplayer] campaign-end TearDown failed: " + e.Message); }
            // Same Fix #5 remainder as HandleHostLeft: reset the UI lobby FSM + clear the chosen save so
            // the next host/join never inherits a stale ClientLobby state. Null-safe.
            try { Multiplayer.UI.MultiplayerUI.Instance?.TeardownLobbyOnSessionEnd(); }
            catch (Exception e) { Debug.LogError("[Multiplayer] campaign-end UI teardown failed: " + e.Message); }
        }

        // ONE handler for both triggers. Idempotent (one-shot latch): a graceful HostDisconnected packet
        // followed by the transport drop of the same host must return to the menu only once.
        private static void HandleHostLeft(string reason = null)
        {
            if (!_latch.TryHandle()) return; // already handled this session
            Debug.LogWarning("[Multiplayer] F3: host left the session — returning client to main menu. " +
                             (reason ?? SessionLifecycle.HostEndedSession));

            // Session-fatal: a modal prompt is acceptable here (works tactical + geoscape + home).
            // FIX-2 (half-open): callers may pass a specific never-silent reason (e.g. heartbeat-ack
            // timeout = dead send channel); default is the generic host-ended-session notice.
            try
            {
                var box = GameUtl.GetMessageBox();
                box?.ShowSimplePrompt(
                    reason ?? SessionLifecycle.HostEndedSession,
                    MessageBoxIcon.Warning, MessageBoxButtons.OK,
                    null, null);
            }
            catch (Exception e) { Debug.LogError("[Multiplayer] F3 prompt failed: " + e.Message); }

            // Force the client back to the Main Menu via the native quit-to-menu chokepoint. The
            // existing FinishLevelAndGoToLobbyTearDownPatch postfix auto-runs NetworkEngine.TearDown().
            try
            {
                var game = GameUtl.GameComponent<PhoenixGame>();
                game?.FinishLevelAndGoToLobby();
            }
            catch (Exception e) { Debug.LogError("[Multiplayer] F3 return-to-menu failed: " + e.Message); }

            // Defense-in-depth: ensure the network session is torn down even if the native return path
            // did not (e.g. called outside a level). TearDown is idempotent + safe to call twice.
            try { NetworkEngine.Instance?.TearDown(); }
            catch (Exception e) { Debug.LogError("[Multiplayer] F3 TearDown failed: " + e.Message); }

            // Fix #5 (host-left remainder): the network TearDown above does NOT reset the UI lobby FSM
            // or clear the chosen save, so without this the next host/join would inherit a stale
            // ClientLobby/Starting state + a phantom _pendingChosenSave (the same bug LEAVE/cancel/
            // OnConnectionFailed already guard via TeardownLobbyState). Route the host-left trigger
            // through the same single hook. Null-safe (no MultiplayerUI in a headless/edge teardown).
            try { Multiplayer.UI.MultiplayerUI.Instance?.TeardownLobbyOnSessionEnd(); }
            catch (Exception e) { Debug.LogError("[Multiplayer] F3 UI teardown failed: " + e.Message); }
        }
    }
}
