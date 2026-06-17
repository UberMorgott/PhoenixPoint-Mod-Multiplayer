namespace Multipleer.Network
{
    /// <summary>
    /// The co-op lobby lifecycle states. <c>Starting</c> / <c>InGame</c> internals are out of axis
    /// (owned by SaveTransferCoordinator + the in-game sync workstream); this controller owns the
    /// entry guard into <c>Starting</c> and the reset back to <c>Idle</c>.
    /// </summary>
    public enum LobbyState
    {
        Idle,
        HostLobby,
        Joining,
        ClientLobby,
        Starting,
        InGame,
    }

    /// <summary>
    /// Pure, Unity-free finite-state machine + start gate for the co-op lobby. Single source of
    /// truth for "what lobby state are we in" and "may the host start now". The UI reads <see
    /// cref="State"/> / <see cref="CanStart"/> and emits intents (BeginHost/BeginJoin/CommitStart/
    /// Reset); it never mutates network state directly.
    ///
    /// Bug B is killed here by design: <see cref="CanStart"/> is true ONLY while in HostLobby,
    /// unlocked, with at least one connected client, all connected clients ready, and a save chosen.
    /// Host-alone can never start. On <see cref="CommitStart"/> the lobby LOCKS so a late ready/
    /// un-ready flip can never reopen the gate mid-start.
    /// </summary>
    public class LobbyController
    {
        public LobbyState State { get; private set; } = LobbyState.Idle;

        /// <summary>True from a successful CommitStart until Reset: Ready toggles + new joins frozen.</summary>
        public bool IsLocked { get; private set; }

        private int _connectedClientCount;
        private bool _allClientsReady;
        private bool _saveChosen;

        /// <summary>
        /// The start gate. True only when the lobby is in HostLobby, unlocked, and
        /// connectedClientCount &gt;= 1 &amp;&amp; allConnectedClientsReady &amp;&amp; saveChosen.
        /// </summary>
        public bool CanStart =>
            State == LobbyState.HostLobby
            && !IsLocked
            && _connectedClientCount >= 1
            && _allClientsReady
            && _saveChosen;

        /// <summary>Idle → HostLobby. Returns false (no-op) if not currently Idle.</summary>
        public bool BeginHost()
        {
            if (State != LobbyState.Idle) return false;
            State = LobbyState.HostLobby;
            return true;
        }

        /// <summary>Idle → Joining. Returns false (no-op) if not currently Idle.</summary>
        public bool BeginJoin()
        {
            if (State != LobbyState.Idle) return false;
            State = LobbyState.Joining;
            return true;
        }

        /// <summary>Joining → ClientLobby (host accepted us). Returns false if not Joining.</summary>
        public bool JoinConfirmed()
        {
            if (State != LobbyState.Joining) return false;
            State = LobbyState.ClientLobby;
            return true;
        }

        /// <summary>
        /// Push the latest lobby facts. Ignored once locked (post-start) so a mid-start race can
        /// never reopen the gate.
        /// </summary>
        public void UpdateLobby(int connectedClientCount, bool allConnectedClientsReady, bool saveChosen)
        {
            if (IsLocked) return;
            _connectedClientCount = connectedClientCount;
            _allClientsReady = allConnectedClientsReady;
            _saveChosen = saveChosen;
        }

        /// <summary>
        /// Host pressed Start. Re-validates the gate at the instant of the press (defense-in-depth
        /// against the stale-frame race) and, only if open, LOCKS the lobby and enters Starting.
        /// Returns true iff the start was committed.
        /// </summary>
        public bool CommitStart()
        {
            if (!CanStart) return false;
            IsLocked = true;
            State = LobbyState.Starting;
            return true;
        }

        /// <summary>
        /// The host swapped the chosen save: clients readied for a specific session, so their Ready
        /// must be cleared. Returns true so the caller can drive the actual roster reset; locally
        /// it also drops the cached ready fact so <see cref="CanStart"/> closes until re-readied.
        /// </summary>
        public bool SaveChangedShouldResetReady()
        {
            if (IsLocked) return false;
            _allClientsReady = false;
            return true;
        }

        /// <summary>Full reset back to a fresh, reopenable Idle lobby (teardown path).</summary>
        public void Reset()
        {
            State = LobbyState.Idle;
            IsLocked = false;
            _connectedClientCount = 0;
            _allClientsReady = false;
            _saveChosen = false;
        }
    }
}
