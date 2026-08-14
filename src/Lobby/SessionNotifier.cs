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
            // QUIET IN THE LOBBY, LOUD ONCE THE CAMPAIGN IS RUNNING. Before the session starts players
            // drift in and out freely and the roster row + the chat line already say so — a toast per
            // arrival is pure noise on a screen the player is already looking at. Once the campaign is
            // running an arrival matters, so the toast comes back. Same flag the lobby panel hides
            // itself on (SaveTransferCoordinator.SessionStarted): no second notion of "started".
            if (_attached?.SaveTransfer?.SessionStarted != true) return;

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

        // ─── A REFUSED CLICK (L501) ─────────────────────────────────────────
        // The reject nudge's `notify` bit lands here. It is NOT a session event: it is the answer to one
        // click, so it gets the smallest surface that still answers, and it gets it AT MOST ONCE.
        //
        // WHAT WENT WRONG (2026-08-14, the session after L485 shipped). The refusals reached the player —
        // and read as a crash. Four "assign U#12 — TFTV refused the assignment" and three tac-cmd lines in
        // one session, each one an internal sentence written for a log: root keys, op codes, the name of a
        // third-party mod as the author of a failure. Zero exceptions in that whole session; the defect
        // count did not move. The session merely READ as broken, which is a real regression in felt quality.
        //
        // So the notice is a PLAYER's sentence and nothing else:
        //   • Scrub strips our internal identifiers (U#/V# root keys, op=, nonce=, peer=) at the ONE seam
        //     every notifying call site passes through, so a future caller cannot leak one by forgetting.
        //   • Collapses drops a repeat, and — where the only surface is the native PROMPT (tactical has no
        //     NotificationController) — drops ANY second notice inside the window, because four stacked
        //     modal boxes for four dead clicks is the alarming half of this bug.
        // Both are pure so RailCheck L501 drives the REAL decision instead of a copy of it.

        internal const float RefusalWindow = 10f;
        private static string _lastRefusalText;
        private static float _lastRefusalAt = -1000f;

        private static readonly System.Text.RegularExpressions.Regex InternalIds =
            new System.Text.RegularExpressions.Regex(@"\s*(U#\d+|V#[^\s]+|op=\d+|nonce=\d+|peer=\d+|\(throw\))",
                                                     System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>The player-facing form of a refusal reason: our internal identifiers removed and any
        /// separator they left behind trimmed. Null when nothing sayable survives (the caller then shows
        /// nothing rather than an empty box). PURE.</summary>
        internal static string Scrub(string why)
        {
            if (string.IsNullOrEmpty(why)) return null;
            var s = InternalIds.Replace(why, "").Trim(' ', '\t', '—', '-', ':', ',');
            return s.Length == 0 ? null : s;
        }

        /// <summary>Whether this refusal notice is DROPPED as a repeat. On a surface with the game's own
        /// transient toast, only an identical sentence collapses (two different refusals are two facts).
        /// Where the only surface is the modal prompt, ANY second notice inside the window collapses —
        /// stacked boxes are the thing being fixed. PURE.</summary>
        internal static bool Collapses(string text, string lastText, float sinceLast, bool transientSurface) =>
            sinceLast < RefusalWindow &&
            (!transientSurface || string.Equals(text, lastText, StringComparison.Ordinal));

        /// <summary>THE refusal seam: one brief notice for a click the host refused, deduplicated and rate
        /// limited, with the full detail left in the log by the caller. Never throws.</summary>
        internal static void ShowRefusal(string why)
        {
            var text = Scrub(why);
            if (text == null) return;
            try
            {
                float now = Time.realtimeSinceStartup;
                bool transient = FindToastSurface() != null;
                if (Collapses(text, _lastRefusalText, now - _lastRefusalAt, transient))
                {
                    MpLog.Log("[Multiplayer] refusal notice collapsed (one already shown within " +
                              RefusalWindow.ToString("0") + "s): " + text);
                    return;
                }
                _lastRefusalText = text;
                _lastRefusalAt = now;
                ShowToast(text, modalFallback: true);
            }
            catch (Exception e) { MpLog.LogError("[Multiplayer] refusal notice failed: " + e.Message); }
        }

        private static NotificationController FindToastSurface()
        {
            try { return UnityEngine.Object.FindObjectOfType<NotificationController>(); }
            catch { return null; }
        }

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
                var controller = FindToastSurface();
                if (controller != null)
                {
                    controller.ShowNotification(message);
                    return;
                }
                // No toast surface in this context (tactical). Opt-in per call site: the disconnect
                // notice falls back to the native prompt (same widget the F3 host-leave path uses —
                // works tactical + geoscape + home); the join toast and CRC divergence hint stay quiet.
                if (modalFallback)
                {
                    // THE user-visible surface, and until now a silent one: two stacked prompts left
                    // nothing whatsoever in Player.log, so "why did I get two boxes" could only be
                    // guessed at. One cheap line per prompt (they are rare — session events only) and
                    // the next occurrence names itself.
                    MpLog.Log("[Multiplayer] native prompt: " + message);
                    GameUtl.GetMessageBox()?.ShowSimplePrompt(
                        message, MessageBoxIcon.Warning, MessageBoxButtons.OK, null, null);
                }
            }
            catch (Exception e)
            {
                MpLog.LogError("[Multiplayer] SessionNotifier toast failed: " + e.Message);
            }
        }
    }
}
