namespace Multipleer.Network
{
    /// <summary>
    /// Pure, Unity-free session-lifecycle helpers shared by the live wiring (SessionNotifier,
    /// MultiplayerUI, the in-game load intercept, the host-leave handler). Kept here, separate from
    /// the MonoBehaviour/Harmony code, so the message formatting, the F2 host-load guard, and the F3
    /// idempotency latch can be unit-tested directly (linked into Multipleer.Tests).
    /// </summary>
    public static class SessionLifecycle
    {
        /// <summary>Fallback display name when a peer's real name is unknown (id-only event).</summary>
        public const string UnknownPlayer = "a player";

        /// <summary>
        /// The persistent system-chat / toast line for a peer connecting or dropping. Used by
        /// SessionNotifier for BOTH surfaces so the toast and the chat line never drift. The name is
        /// sanitized to <see cref="UnknownPlayer"/> when null/empty so a raw id is never shown.
        /// </summary>
        public static string FormatPeerEvent(bool connected, string playerName)
        {
            var name = string.IsNullOrEmpty(playerName) ? UnknownPlayer : playerName;
            return connected ? $"— {name} joined —" : $"— {name} left —";
        }

        /// <summary>The session-fatal line shown to a client when the host ends the session (F3).</summary>
        public const string HostEndedSession = "Host ended the session";

        /// <summary>
        /// F2 host-load guard. True iff the host may re-run the chunked save transfer mid-session to
        /// pull every client into a newly picked save: we are the host, a networked session is up, the
        /// session has already started (we are in-game, not in the lobby), at least one client is
        /// connected, and no transfer is already in flight (re-entry guard — lock during, unlock after).
        /// </summary>
        public static bool HostLoadGuard(bool isHost, bool isActiveSession, bool sessionStarted,
            int connectedClientCount, bool transferActive)
        {
            return isHost
                && isActiveSession
                && sessionStarted
                && connectedClientCount >= 1
                && !transferActive;
        }

        /// <summary>
        /// Lobby save-pick gate. True iff a host with an ACTIVE co-op lobby whose session has NOT
        /// started chose a save from ANY native Load screen: that pick must become the lobby's chosen
        /// save (broadcast as a label only — NO campaign load), NOT trigger a real load. This is the
        /// DURABLE replacement for gating the intercept on the fragile static <c>_armed</c> flag (which
        /// could be false at click time because the lobby re-makes native menu buttons clickable, so the
        /// host can reach the native Load screen un-armed and the old guard let the load run).
        ///
        /// Deliberately EXCLUDES the mid-session F2 host load (<paramref name="sessionStarted"/> == true):
        /// that path is host-authoritative and SHOULD load immediately (re-running the chunked transfer;
        /// see <see cref="HostLoadGuard"/>). Also excludes non-host peers and single-player
        /// (<paramref name="lobbyActive"/> == false) so vanilla load is never captured.
        /// </summary>
        public static bool ShouldCaptureAsLobbyPick(bool isHost, bool lobbyActive, bool sessionStarted)
        {
            return isHost && lobbyActive && !sessionStarted;
        }
    }

    /// <summary>
    /// One-shot idempotency latch for the F3 host-leave handler. The host leaving can be signalled
    /// twice — a graceful <c>HostDisconnected</c> packet followed by the transport drop of the same
    /// peer (or a heartbeat timeout) — and the client must return to the main menu exactly once.
    /// <see cref="TryHandle"/> returns true only on the FIRST call; every later call returns false.
    /// Pure + Unity-free so it is unit-testable; <see cref="Reset"/> re-arms it for the next session.
    /// </summary>
    public sealed class HostLeaveLatch
    {
        private bool _handled;

        /// <summary>True once the host-leave has been handled (the menu return has fired).</summary>
        public bool Handled => _handled;

        /// <summary>Latch and return true on the FIRST call only; false on every subsequent call.</summary>
        public bool TryHandle()
        {
            if (_handled) return false;
            _handled = true;
            return true;
        }

        /// <summary>Re-arm the latch for a fresh session (called on a new host/join).</summary>
        public void Reset() => _handled = false;
    }
}
