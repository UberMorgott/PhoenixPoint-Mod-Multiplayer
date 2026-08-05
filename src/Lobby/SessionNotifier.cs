using System;
using Base.Core;
using Base.UI.MessageBox;
using UnityEngine;
using PhoenixPoint.Common.View.ViewControllers;

namespace Multiplayer.Network
{
    /// <summary>
    /// F1 — peer JOIN notification, plus the shared native TOAST surface. A single subscriber wired ONCE
    /// per engine init (<see cref="AttachTo"/>) to the dangling <c>OnClientConnected</c> event: the join
    /// CHAT line is already posted by <c>SessionManager.HandleConnectionRequest</c>, so all this adds is the
    /// transient toast via the game's own <see cref="NotificationController.ShowNotification"/>.
    ///
    /// DEPARTURES ARE NOT ANNOUNCED HERE — the roster level owns them end to end (SessionManager.PausePeer
    /// = lost connection, HandleLeave = left, ResumePeer = back), because only it can tell those three
    /// facts apart and name the player. See the comment on the deleted handler below. RailCheck L120 keeps
    /// this to ONE announcer per departure.
    ///
    /// Never throws into gameplay (every native call is guarded).
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
            _attached = engine;
        }

        /// <summary>Drop subscriptions from the currently-attached engine (idempotent).</summary>
        public static void Detach()
        {
            if (_attached == null) return;
            _attached.OnClientConnected -= OnClientConnected;
            _attached = null;
        }

        private static void OnClientConnected(ulong peerId)
        {
            // The join CHAT line is already posted by SessionManager.HandleConnectionRequest (with the
            // resolved name, once the JOIN handshake binds it). Here we add only the transient toast.
            // The name is not bound yet at transport-connect time, so the toast is name-agnostic.
            ShowToast("A player joined the session");
        }

        // THE DEPARTURE NOTICE IS NOT HERE ANY MORE (deleted 2026-08-05). It was a SECOND announcer for an
        // event the roster level already announces, and on every path it could still fire it was the wrong
        // one:
        //   • HOST — unreachable by construction. `wasKnown` is TryGetClientName:318, i.e. the row is in
        //     _clients; that is the very condition NetworkEngine:587 uses to PausePeer, and the raise passes
        //     `wasKnown && !paused` (:597). So on the host wasKnown ⇒ paused ⇒ this never ran. The host's
        //     departure lines come from PausePeer:266 (lost connection) and HandleLeave:1041 (left) — one
        //     each, named, and broadcast to every peer through SystemNotice.
        //   • CLIENT — the only peer a client has a transport link to is the HOST (star topology), so this
        //     fired only for the host's own drop, where HostLeaveHandler.OnPeerDroppedMaybeHost:55 is
        //     subscribed to the SAME event and already announces it (SessionEnd.Begin, "Host ended the
        //     session"). Two prompts, one event — and in tactical BOTH were native modals.
        //   • WORSE, on a client's OWN leave: HostLeaveHandler deliberately suppresses there
        //     (ShouldNotifyHostLeft:65), so this was left showing "— <host> left —" to the player who had
        //     just chosen to leave.
        // The join toast below stays: nothing else raises one.

        /// <summary>
        /// Show a transient toast via the live native NotificationController if one exists in the
        /// current context (geoscape / main menu). No-op in tactical (none present) — chat-only there.
        /// Never throws. INTERNAL: also the native "diverged" hint surface of the Inc5 CRC probe
        /// (<c>CrcProbeMirror</c>) — one toast per divergence transition, native-UI-first.
        /// </summary>
        internal static void ShowToast(string message, bool modalFallback = false)
        {
            try
            {
                var controller = UnityEngine.Object.FindObjectOfType<NotificationController>();
                if (controller != null)
                {
                    controller.ShowNotification(message);
                    return;
                }
                // No toast surface in this context (tactical). Opt-in per call site: the disconnect
                // notice falls back to the native prompt (same widget the F3 host-leave path uses —
                // works tactical + geoscape + home); the join toast and CRC divergence hint stay quiet.
                if (modalFallback)
                    GameUtl.GetMessageBox()?.ShowSimplePrompt(
                        message, MessageBoxIcon.Warning, MessageBoxButtons.OK, null, null);
            }
            catch (Exception e)
            {
                Debug.LogError("[Multiplayer] SessionNotifier toast failed: " + e.Message);
            }
        }
    }
}
