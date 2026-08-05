namespace Multiplayer.Network
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
    /// unlocked, with at least one connected client, and a save chosen. Host-alone can never start.
    /// On <see cref="CommitStart"/> the lobby LOCKS so a late join can never reopen the gate mid-start.
    ///
    /// NO READY QUORUM (N=50 mandate, 2026-08-05). The gate used to also require EVERY connected
    /// non-host peer to be ready, which is a quorum: one peer that is slow, AFK, parity-blocked or
    /// simply not looking at its screen held fifty players shut, and the bigger the roster the more
    /// certain that is. The host now starts on its OWN readiness and every other peer converges through
    /// the per-peer on-demand join path. Ready survives as what it always really was — a signal to the
    /// host that a player is at the keyboard — and is still rendered per row; it just no longer votes.
    /// </summary>
    public class LobbyController
    {
        public LobbyState State { get; private set; } = LobbyState.Idle;

        /// <summary>True from a successful CommitStart until Reset: Ready toggles + new joins frozen.</summary>
        public bool IsLocked { get; private set; }

        private int _connectedClientCount;
        private bool _saveChosen;

        /// <summary>
        /// The start gate. True only when the lobby is in HostLobby, unlocked, and
        /// connectedClientCount &gt;= 1 &amp;&amp; saveChosen. Deliberately NOT a function of anyone's
        /// ready flag — see the class summary.
        /// </summary>
        public bool CanStart =>
            State == LobbyState.HostLobby
            && !IsLocked
            && _connectedClientCount >= 1
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
        public void UpdateLobby(int connectedClientCount, bool saveChosen)
        {
            if (IsLocked) return;
            _connectedClientCount = connectedClientCount;
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
        /// Reversible counterpart to <see cref="CommitStart"/>: a start that was committed (lobby locked,
        /// State=Starting) failed DOWNSTREAM (e.g. the save-transfer coordinator could not get the game
        /// or timing), so reopen the lobby — UNLOCK and return to HostLobby — instead of leaving it
        /// permanently dead-locked. Safe/idempotent when not in Starting (no-op). The cached lobby facts
        /// are preserved, so if the gate was satisfied at commit it is satisfied again on reopen.
        /// </summary>
        public void CancelStart()
        {
            if (State != LobbyState.Starting) return;
            IsLocked = false;
            State = LobbyState.HostLobby;
        }

        /// <summary>
        /// The host swapped the chosen save: clients readied for a specific session, so their Ready
        /// must be cleared. Returns true so the caller can drive the actual roster reset. It no longer
        /// closes <see cref="CanStart"/> — ready is not part of the gate (see the class summary).
        /// </summary>
        public bool SaveChangedShouldResetReady() => !IsLocked;

        /// <summary>Full reset back to a fresh, reopenable Idle lobby (teardown path).</summary>
        public void Reset()
        {
            State = LobbyState.Idle;
            IsLocked = false;
            _connectedClientCount = 0;
            _saveChosen = false;
        }

    }
}
