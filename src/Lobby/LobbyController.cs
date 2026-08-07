using Multiplayer.Network.MessageLayer;   // PeerListEntry — the roster row AllLivePeersReady folds over
using Multiplayer.Network.Parity;         // ParityComparer.ReadyAllowed — the second exclusion

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
    /// READINESS LIVES IN THE LOBBY, AND ONLY IN THE LOBBY (owner ruling, 2026-08-07). This REVERSES
    /// <c>afc111a</c> (2026-08-05), which removed exactly this gate under "start on the host's own
    /// readiness, never on a peer quorum". The no-quorum postulate is now scoped: it governs progress
    /// AFTER the game has been entered — once play is running, nothing may gate one peer on another peer
    /// ACTING (L91, L145, L151, and the load barrier's own rule that it waits on a LOAD, never a person).
    /// Before the game starts there is no play to block, so the host may wait for the table to sit down.
    /// The in-game half of the mandate is untouched and undiminished.
    ///
    /// THE ONE THING THAT MAKES IT SAFE is <see cref="AllLivePeersReady"/>: a peer that is PAUSED — the
    /// single state every involuntary loss funnels into, because drops use <c>PausePeer</c> and never
    /// <c>RemoveClient</c>, so a gone peer keeps its roster row forever — is NOT COUNTED. Without that
    /// exclusion this gate is an infinite blocker one floor down: a crashed player would hold the lobby
    /// shut for everyone with nobody able to ready on its behalf. Live peers only, always.
    ///
    /// LIVE ALSO MEANS JOINED (2026-08-07 incident, L180/L181). A roster row is minted by the TRANSPORT
    /// connect, before any JOIN — so a peer whose handshake never completed owns a row with no identity,
    /// no name and no way to press READY, and it is not <c>Paused</c> while its socket looks alive. That
    /// row held the host's NEW CAMPAIGN button down naming nobody. <see cref="IsLivePeer"/> is now the ONE
    /// definition and BOTH halves of the gate read it, so such a row can neither block the start nor
    /// stand in for the player the host is not supposed to start without.
    /// </summary>
    public class LobbyController
    {
        public LobbyState State { get; private set; } = LobbyState.Idle;

        /// <summary>True from a successful CommitStart until Reset: Ready toggles + new joins frozen.</summary>
        public bool IsLocked { get; private set; }

        private int _connectedClientCount;
        private bool _saveChosen;
        private bool _allLivePeersReady;

        /// <summary>
        /// THE ONE DEFINITION OF A PEER THAT IS ACTUALLY AT THE TABLE, so both halves of the gate ask the
        /// same question. A row is LIVE iff there is a human behind it right now:
        ///   • not the host's own row;
        ///   • not <c>Paused</c> — every involuntary loss funnels there (drops use <c>PausePeer</c>, never
        ///     <c>RemoveClient</c>), so a gone peer keeps its roster row. Nobody is home;
        ///   • its JOIN actually arrived — <c>PlayerGuid</c> is the identity the JOIN carries and NOTHING
        ///     else sets it, so <c>Guid.Empty</c> means the row was minted by the TRANSPORT connect and
        ///     the handshake never completed (<c>SessionLifecycle.StaleRejoinPeers</c> names the same
        ///     pre-JOIN row). Such a peer has no name, no slot, no permissions and no way to press READY —
        ///     the 2026-08-07 phantom, which sat in the host's lobby holding it shut after its owner had
        ///     quit the game and switched his VPN off.
        /// </summary>
        public static bool IsLivePeer(PeerListEntry p) =>
            p != null && !p.IsHost && !p.Paused && p.PlayerGuid != System.Guid.Empty;

        /// <summary>
        /// WHY THE READINESS HALF OF THE GATE IS SHUT — "" when it is open, otherwise a reason that NAMES
        /// the peers it is waiting on. The blocked message used to say only "not every live peer has
        /// readied", which is unactionable exactly when it matters: the host cannot see which row is
        /// holding the button down, and a phantom row named nobody at all.
        ///
        /// A row that is live but PARITY-BLOCKED is not waited on: <c>SessionManager.SetClientReady</c>
        /// refuses its ready host-authoritatively, so counting it would mean waiting for a vote the host
        /// itself will not accept. It still counts as being AT the table (parity is a soft gate — such a
        /// peer joins and plays), so it satisfies the host-is-not-alone half.
        /// </summary>
        public static string PeersBlockedBy(System.Collections.Generic.IEnumerable<PeerListEntry> roster)
        {
            var waiting = new System.Collections.Generic.List<string>();
            int live = 0;
            if (roster != null)
                foreach (var p in roster)
                {
                    if (!IsLivePeer(p)) continue;
                    live++;
                    if (!ParityComparer.ReadyAllowed(p.ParityDiffs)) continue;
                    if (!p.Ready)
                        waiting.Add(string.IsNullOrEmpty(p.Nickname) ? p.SteamId.ToString() : p.Nickname);
                }
            if (live == 0)
                return "no peer has finished joining — a row that never completed its handshake, or one " +
                       "that dropped out, is not somebody you can start a co-op session with";
            if (waiting.Count > 0)
                return "waiting on " + string.Join(", ", waiting.ToArray());
            return "";
        }

        /// <summary>
        /// THE LOBBY READINESS FOLD. Pure and total so RailCheck EXECUTES it rather than describing it
        /// (L84 arm (c)). Counts the non-host rows that are LIVE (<see cref="IsLivePeer"/>) and answers
        /// whether every one of them has readied. An empty live set is vacuously ready HERE; the
        /// ">= 1 connected peer" half of the gate is what stops a host starting alone, and it now counts
        /// the same live set (<c>MultiplayerUI.RefreshGateFacts</c>) so a phantom can neither hold the
        /// gate shut nor pass the host off as not-alone.
        /// </summary>
        public static bool AllLivePeersReady(System.Collections.Generic.IEnumerable<PeerListEntry> roster)
        {
            if (roster == null) return true;
            foreach (var p in roster)
            {
                if (!IsLivePeer(p)) continue;
                if (!ParityComparer.ReadyAllowed(p.ParityDiffs)) continue;
                if (!p.Ready) return false;
            }
            return true;
        }

        /// <summary>
        /// The readiness half of the gate, on its own because the NEW CAMPAIGN button needs it WITHOUT
        /// the chosen-save half (a fresh campaign picks no save). Same lobby state, same lock, same
        /// host-is-not-alone rule.
        /// </summary>
        public bool PeersReady =>
            State == LobbyState.HostLobby
            && !IsLocked
            && _connectedClientCount >= 1
            && _allLivePeersReady;

        /// <summary>The start gate: <see cref="PeersReady"/> plus a chosen save.</summary>
        public bool CanStart => PeersReady && _saveChosen;

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
        public void UpdateLobby(int connectedClientCount, bool saveChosen, bool allLivePeersReady)
        {
            if (IsLocked) return;
            _connectedClientCount = connectedClientCount;
            _saveChosen = saveChosen;
            _allLivePeersReady = allLivePeersReady;
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
        /// must be cleared. Returns true so the caller can drive the actual roster reset. Clearing them
        /// DOES close <see cref="CanStart"/> again now that readiness is part of the lobby gate — which is
        /// the point: a peer readied for the save the host just swapped away has not agreed to this one.
        /// </summary>
        public bool SaveChangedShouldResetReady() => !IsLocked;

        /// <summary>Full reset back to a fresh, reopenable Idle lobby (teardown path).</summary>
        public void Reset()
        {
            State = LobbyState.Idle;
            IsLocked = false;
            _connectedClientCount = 0;
            _saveChosen = false;
            _allLivePeersReady = false;
        }

    }
}
