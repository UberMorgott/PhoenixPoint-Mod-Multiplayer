using System;
using UnityEngine;
using PhoenixPoint.Common.View.ViewControllers;

namespace Multiplayer.Network
{
    /// <summary>
    /// F1 — peer connect/disconnect notification. A single subscriber wired ONCE per engine init
    /// (<see cref="AttachTo"/>) to the dangling <c>OnClientConnected</c> / <c>OnClientDisconnectedNamed</c>
    /// events. Renders two native surfaces, native-UI-first:
    ///   • a transient TOAST via the game's own <see cref="NotificationController.ShowNotification"/>
    ///     WHERE one is live (geoscape / main-menu) — absent in tactical, so it falls back to chat-only;
    ///   • a persistent system-chat line via <see cref="SessionManager.SystemChat"/> (host-only,
    ///     broadcast to every remaining client) for crash/timeout DROPS, so behaviour matches the
    ///     graceful-leave line <c>HandleLeave</c> already posts.
    /// Connect chat is already posted by <c>HandleConnectionRequest</c> and graceful-leave chat by
    /// <c>HandleLeave</c>; the notifier adds ONLY the missing crash-drop chat line (no duplicates) plus
    /// the toast on both events. Never throws into gameplay (every native call is guarded).
    /// </summary>
    public static class SessionNotifier
    {
        private static NetworkEngine _attached;

        /// <summary>
        /// Subscribe to a freshly-initialized engine. Detaches any prior engine first (the singleton
        /// is recreated per host/join, so re-attaching must never stack handlers). Safe to call on
        /// every Initialize.
        /// </summary>
        public static void AttachTo(NetworkEngine engine)
        {
            if (engine == null) return;
            if (ReferenceEquals(_attached, engine)) return; // already wired to this exact engine

            Detach();
            engine.OnClientConnected += OnClientConnected;
            engine.OnClientDisconnectedNamed += OnClientDisconnectedNamed;
            _attached = engine;
        }

        /// <summary>Drop subscriptions from the currently-attached engine (idempotent).</summary>
        public static void Detach()
        {
            if (_attached == null) return;
            _attached.OnClientConnected -= OnClientConnected;
            _attached.OnClientDisconnectedNamed -= OnClientDisconnectedNamed;
            _attached = null;
        }

        private static void OnClientConnected(ulong peerId)
        {
            // The join CHAT line is already posted by SessionManager.HandleConnectionRequest (with the
            // resolved name, once the JOIN handshake binds it). Here we add only the transient toast.
            // The name is not bound yet at transport-connect time, so the toast is name-agnostic.
            ShowToast("A player joined the session");
        }

        private static void OnClientDisconnectedNamed(ulong peerId, string playerName, bool wasKnown)
        {
            // A transport drop that arrives AFTER a graceful leave already removed the peer reports
            // wasKnown=false — HandleLeave already posted the chat line and the leaver is gone, so do
            // not double-notify. Genuine crash/timeout drops report wasKnown=true with the captured name.
            if (!wasKnown) return;

            var line = SessionLifecycle.FormatPeerEvent(connected: false, playerName: playerName);

            // Persistent chat line — host-only (SystemChat self-guards + broadcasts to every remaining
            // client, so they all see the drop, not just the host). Fills the crash/timeout gap so the
            // disconnect line is uniform with the graceful-leave line.
            try { _attached?.Session?.SystemChat(line); }
            catch (Exception e) { Debug.LogError("[Multiplayer] SessionNotifier chat failed: " + e.Message); }

            // Transient toast on THIS peer where a NotificationController is live (geoscape/menu).
            ShowToast(line);
        }

        /// <summary>
        /// Show a transient toast via the live native NotificationController if one exists in the
        /// current context (geoscape / main menu). No-op in tactical (none present) — chat-only there.
        /// Never throws.
        /// </summary>
        private static void ShowToast(string message)
        {
            try
            {
                var controller = UnityEngine.Object.FindObjectOfType<NotificationController>();
                if (controller != null)
                    controller.ShowNotification(message);
            }
            catch (Exception e)
            {
                Debug.LogError("[Multiplayer] SessionNotifier toast failed: " + e.Message);
            }
        }
    }
}
