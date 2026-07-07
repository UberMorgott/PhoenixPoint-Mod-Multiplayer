using System;
using System.Collections.Generic;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Validation;
using UnityEngine;

namespace Multiplayer.Network
{
    public class SessionManager
    {
        private readonly NetworkEngine _engine;
        private readonly Dictionary<ulong, ClientInfo> _clients = new Dictionary<ulong, ClientInfo>();
        private readonly Dictionary<ulong, long> _lastHeartbeat = new Dictionary<ulong, long>();
        private readonly HashSet<ulong> _readyClients = new HashSet<ulong>();

        // Host-authoritative chat backlog (whole-session history). Every line the host fans out via
        // BroadcastChat is appended here in arrival order; on a new client fully joining the host
        // replays this backlog to ONLY that client (ReplayChatHistoryTo) so late joiners see the
        // full prior conversation. Host-only — clients render straight from OnChatReceived and never
        // populate this. Capped to bound memory across a long session; the cap is generous so a
        // normal lobby never drops a line.
        private readonly List<ChatMessageData> _chatHistory = new List<ChatMessageData>();
        private const int ChatHistoryCap = 500;

        public ulong? HostPeerId { get; private set; }
        public IReadOnlyDictionary<ulong, ClientInfo> Clients => _clients;

        // Host's own lobby identity. The host is NOT in _clients (which holds remote peers only),
        // so its row is injected into the broadcast roster via a self-entry in BuildPeerList. The
        // lobby UI sets these; defaults give a sensible display before the player edits anything.
        public string HostNickname { get; set; } = "Host";
        public bool HostReady { get; set; }

        // Host's chosen save (rail display + read-only client mirror). Set on the rail save-pick.
        public string ChosenSaveName { get; private set; }
        public string ChosenSaveMeta { get; private set; }

        // Last roster the CLIENT received from the host (preserves IsHost + every peer's nickname/
        // ready exactly as the host broadcast it). On the host the live BuildPeerList() is authoritative.
        private List<PeerListEntry> _clientRoster = new List<PeerListEntry>();

        // Host-side stable slotIndex allocation (host = slot 0; clients in arrival order, reconnect
        // reuses by PlayerGuid). Lazily built from the host identity on first BuildPeerList.
        private SlotAllocator _slots;
        /// <summary>This peer's own host-assigned slot (host = 0; clients learn it from PEER_LIST).</summary>
        public byte LocalSlotIndex { get; private set; }

        /// <summary>
        /// Unified lobby roster for UI: host → live BuildPeerList(); client → last received PEER_LIST.
        /// Always includes the host self-entry (IsHost=true). Never null.
        /// </summary>
        public List<PeerListEntry> GetLobbyRoster()
        {
            return _engine.IsHost ? BuildPeerList() : _clientRoster;
        }

        public event Action OnAllClientsReady;
        public event Action<ulong> OnClientReady;
        public event Action OnHostDisconnected;
        public event Action<string, string, bool> OnChatReceived;   // (senderNick, text, isSystem)
        public event Action<string, string> OnChosenSaveChanged;    // (saveName, saveMeta)

        private const int HeartbeatIntervalMs = 5000;
        private const int HeartbeatTimeoutMs = 20000;
        private long _lastHeartbeatSend;

        public SessionManager(NetworkEngine engine)
        {
            _engine = engine;
        }

        public void InitializeAsHost()
        {
            HostPeerId = null; // we are the host
        }

        public void SetHostPeer(ulong hostPeerId)
        {
            HostPeerId = hostPeerId;
            _lastHeartbeat[hostPeerId] = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }

        public void Update()
        {
            var now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

            // Periodic heartbeat
            if (now - _lastHeartbeatSend > HeartbeatIntervalMs)
            {
                _lastHeartbeatSend = now;
                var heartbeat = new NetworkMessage(PacketType.Heartbeat,
                    BitConverter.GetBytes(now));

                if (_engine.IsHost)
                {
                    _engine.BroadcastToAll(heartbeat);
                }
                else if (HostPeerId.HasValue)
                {
                    _engine.SendToHost(heartbeat);
                }
            }

            // Heartbeat timeout check (host only)
            if (_engine.IsHost)
            {
                var toRemove = new List<ulong>();
                foreach (var kvp in _lastHeartbeat)
                {
                    if (now - kvp.Value > HeartbeatTimeoutMs)
                        toRemove.Add(kvp.Key);
                }
                foreach (var clientId in toRemove)
                {
                    Debug.LogWarning($"[Multiplayer] Client {clientId} timed out");
                    // F1: a heartbeat timeout removes the client directly (it never reaches the
                    // transport OnPeerDisconnected path), so post the "-- X left --" line HERE to keep
                    // the drop notice uniform with crash/graceful-leave. Capture the name before purge.
                    var timedOutNick = TryGetClientName(clientId, out var n) ? n : SessionLifecycle.UnknownPlayer;
                    RemoveClient(clientId);
                    SystemChat(SessionLifecycle.FormatPeerEvent(connected: false, timedOutNick));
                }
            }
            // F3 (client side): host HEARTBEAT TIMEOUT. A wedged/half-open host socket may never send
            // FIN/RST, so OnPeerDisconnected (trigger b) never fires and the client would be stranded.
            // If we have not heard from the host within the timeout, route into the SAME host-leave
            // handler (its one-shot latch dedups against a graceful HostDisconnected packet / real drop,
            // and is also a no-op after the handler already ran). Reuses the existing heartbeat constants.
            else if (HostPeerId.HasValue && !HostLeaveHandler.AlreadyHandled
                     && _lastHeartbeat.TryGetValue(HostPeerId.Value, out var hostLast)
                     && now - hostLast > HeartbeatTimeoutMs)
            {
                Debug.LogWarning("[Multiplayer] Host heartbeat timed out — treating as host-leave.");
                HostLeaveHandler.TriggerHostLeft();
            }
        }

        // ─── Client Management ────────────────────────────────────────────

        public void AddClient(ulong steamId, string endpoint)
        {
            if (!_clients.ContainsKey(steamId))
            {
                _clients[steamId] = new ClientInfo
                {
                    SteamId = steamId,
                    Endpoint = endpoint,
                    ConnectedAt = DateTime.UtcNow
                };
                _lastHeartbeat[steamId] = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                // SlotIndex is assigned in BuildPeerList (host), keyed by the client's persistent
                // PlayerGuid so a reconnecting client reuses its slot.
            }
        }

        public void RemoveClient(ulong steamId)
        {
            var existed = _clients.TryGetValue(steamId, out var client);
            if (existed)
                client.IsReady = false;
            _clients.Remove(steamId);
            _lastHeartbeat.Remove(steamId);
            _readyClients.Remove(steamId);

            // Roster changed → host re-broadcasts the authoritative peer list.
            if (existed && _engine.IsHost)
                BroadcastPeerList();
        }

        /// <summary>
        /// Resolve a connected peer's display name by its transport id. Used by the disconnect
        /// notifier (F1) to capture the real player name BEFORE <see cref="RemoveClient"/> purges the
        /// <c>_clients</c> map, so the toast/chat shows the name and not a raw id. Returns false (and
        /// a null name) for an unknown id — e.g. a transport drop arriving AFTER a graceful leave
        /// already removed the peer, which lets the caller suppress a duplicate notice.
        /// </summary>
        public bool TryGetClientName(ulong steamId, out string name)
        {
            if (_clients.TryGetValue(steamId, out var client) && !string.IsNullOrEmpty(client.PlayerName))
            {
                name = client.PlayerName;
                return true;
            }
            name = null;
            return false;
        }

        /// <summary>
        /// Host-only (F3 graceful path): tell every client the session is ending so they drop to the
        /// main menu, BEFORE the host tears the transport down. Idempotent at the call site (the
        /// teardown chokepoint fires once); a no-op on a client or when no transport is up.
        /// </summary>
        public void SendHostDisconnected()
        {
            if (!_engine.IsHost) return;
            _engine.BroadcastToAll(new NetworkMessage(PacketType.HostDisconnected));
        }

        public IEnumerable<ulong> GetConnectedClients()
        {
            return _clients.Keys;
        }

        // (peerId → persistent playerGUID) projection of the live roster, fed to the pure
        // returning-peer decision (SessionLifecycle.StaleRejoinPeers). Snapshot list, not a lazy
        // iterator: the caller mutates _clients (RemoveClient) while consuming the result.
        private List<KeyValuePair<ulong, Guid>> GetClientIdentities()
        {
            var ids = new List<KeyValuePair<ulong, Guid>>(_clients.Count);
            foreach (var c in _clients.Values)
                ids.Add(new KeyValuePair<ulong, Guid>(c.SteamId, c.PlayerGuid));
            return ids;
        }

        /// <summary>All slotIndexes currently in the roster (host slot 0 + every connected client).</summary>
        public IEnumerable<byte> GetRosterSlots()
        {
            yield return 0; // host
            foreach (var c in _clients.Values) yield return c.SlotIndex;
        }

        /// <summary>Host: resolve a transport sender id to its slotIndex (host self = slot 0).</summary>
        public bool TryGetSlotForPeer(ulong peerId, out byte slot)
        {
            if (peerId == _engine.LocalSteamId) { slot = 0; return true; }
            if (_clients.TryGetValue(peerId, out var c)) { slot = c.SlotIndex; return true; }
            slot = 0;
            return false;
        }

        public int ClientCount => _clients.Count;

        // ─── Connection Handshake ─────────────────────────────────────────

        public void HandleConnectionRequest(NetworkMessage msg)
        {
            if (!_engine.IsHost) return;

            var clientId = msg.SenderSteamId;

            // HARDENING: a JOIN must carry a non-empty persistent playerGUID. Guid.Empty must never
            // become a live identity, because it is the permission/ownership key — an empty key would
            // collide across peers and could be granted flags. Quarantine: reject the connection and
            // do NOT register the client.
            JoinMessage join = null;
            if (msg.Payload != null && msg.Payload.Length > 0)
                join = MessageSerializer.DeserializeJoin(msg.Payload);

            if (join == null || join.PlayerGuid == Guid.Empty)
            {
                Debug.LogWarning($"[Multiplayer] Rejecting JOIN from {clientId}: missing/empty playerGUID.");
                var reject = new NetworkMessage(PacketType.ConnectionRejected,
                    NetworkMessage.BuildStringPayload("Invalid player identity (empty GUID)."));
                _engine.SendToClient(clientId, reject);
                return;
            }

            // Inc5 part 2 — returning-peer reconnect: a JOIN whose persistent identity is ALREADY bound
            // in the roster is a reconnect of a known player whose previous connection died (possibly a
            // death the transport never reported — crash inside the 20 s heartbeat window — and possibly
            // over a different transport address, or over the SAME reused peer id). Prune the dead
            // connection's session residue FIRST, so the returning peer onboards through the SAME
            // on-demand join path as a brand-new peer with nothing stale counted against the LOADED
            // barrier or the ready gate: its roster entry (ready flag + heartbeat; a same-id reconnect
            // is re-added fresh by AddClient below), its per-peer geo intent-dedup window (fresh engine
            // restarts nonces at 1 — OnJoinReady's reset only covers the arrival leg), and its
            // save-transfer download/loaded rows (ForgetPeer AFTER RemoveClient so a barrier now
            // releasable with the remaining peers releases immediately). Decision is pure + idempotent
            // (SessionLifecycle.StaleRejoinPeers — a double-reconnect race prunes cleanly twice); other
            // clients keep playing untouched. Permissions need no prune: the grant below keys by the
            // persistent guid (idempotent) and each client re-seeds from the next PEER_LIST broadcast
            // (HandlePeerList precedent).
            foreach (var staleId in SessionLifecycle.StaleRejoinPeers(GetClientIdentities(), join.PlayerGuid))
            {
                Debug.Log($"[Multiplayer] Returning peer {join.PlayerGuid} (JOIN from {clientId}): " +
                          $"pruning stale peer {staleId}.");
                _engine.Sync?.ResetIntentDedupForPeer(staleId);
                RemoveClient(staleId);
                _engine.SaveTransfer?.ForgetPeer(staleId);
            }

            // P1 mid-session join boundary: a peer that connects AFTER the session started can only be
            // onboarded while the host is in the live GEOSCAPE (the joiner is reproduced from a geoscape
            // save; the tactical deploy snapshot is turn-0, so an in-progress battle cannot be joined). If a
            // battle is live (or the host is mid-load) reject with a user-visible reason instead of adding a
            // stateless peer. Pre-start (lobby) this is false — the normal lobby join/start path owns it.
            bool sessionStarted = _engine.SaveTransfer?.SessionStarted == true;
            if (SessionLifecycle.ShouldRejectMidSessionJoin(sessionStarted, Sync.GeoRuntime.Instance.IsGeoscapeActive))
            {
                Debug.LogWarning($"[Multiplayer] Rejecting mid-session JOIN from {clientId}: host is not on the " +
                                 "Geoscape (a battle is in progress or the host is loading).");
                var reject = new NetworkMessage(PacketType.ConnectionRejected,
                    NetworkMessage.BuildStringPayload(
                        "Cannot join right now — a battle is in progress. Try again once the host is back on the Geoscape."));
                _engine.SendToClient(clientId, reject);
                return;
            }

            AddClient(clientId, $"Steam({clientId})");

            // Bind peerID <-> playerGUID.
            if (_clients.TryGetValue(clientId, out var client))
            {
                client.PlayerGuid = join.PlayerGuid;
                if (!string.IsNullOrEmpty(join.Nickname))
                    client.PlayerName = join.Nickname;

                // Co-op "allow everything" policy (no per-player permission menu yet): grant the
                // joining client FullCommander, keyed by its persistent playerGUID (the permission
                // key). Without this the client has 0 permissions and HostArbiter rejects every
                // command (e.g. "Missing permission: ManageAircraft"). Host-authoritative arbiter
                // serializes commands, so "last command wins" naturally. Granted here (not in
                // AddClient) because PlayerGuid is only bound after AddClient returns. Mirror the
                // resulting mask onto ClientInfo so the PEER_LIST roster reflects real permissions.
                PermissionManager.SetPermission(client.PlayerGuid, CampaignPermission.FullCommander, true);
                client.Permissions = PermissionManager.GetPermissions(client.PlayerGuid);
            }

            // Send acceptance
            var acceptMsg = new NetworkMessage(PacketType.ConnectionAccepted);
            _engine.SendToClient(clientId, acceptMsg);

            // Roster changed → broadcast authoritative peer list.
            BroadcastPeerList();

            // Backlog replay: send the full prior chat history to ONLY this new client (in arrival
            // order) so a late joiner sees the conversation from the beginning. Done BEFORE the live
            // "joined" SystemChat below — that notice is then fanned out to everyone (incl. the new
            // client) exactly once via BroadcastToAll, so it is never duplicated for the joiner.
            ReplayChatHistoryTo(clientId);

            SystemChat($"— {(string.IsNullOrEmpty(join.Nickname) ? "a player" : join.Nickname)} joined —");

            // P1: if this peer joined AFTER the session started, kick a PER-PEER on-demand save transfer so
            // it converges to the host's current state. Unicast + no barrier/counter reset, so the already-
            // connected peers are untouched. A lobby (pre-start) join is handled by the normal start path.
            if (sessionStarted)
                _engine.SaveTransfer?.HostOnDemandJoin(clientId);
        }

        public void HandleConnectionAccepted(NetworkMessage msg)
        {
            // Client received host confirmation
            Debug.Log("[Multiplayer] Connection accepted by host");
        }

        public void HandleConnectionRejected(NetworkMessage msg)
        {
            var reason = NetworkMessage.ParseStringPayload(msg.Payload);
            Debug.LogError($"[Multiplayer] Connection rejected: {reason}");
        }

        public void HandleClientDisconnected(NetworkMessage msg)
        {
            RemoveClient(msg.SenderSteamId);
        }

        // ─── Heartbeat ────────────────────────────────────────────────────

        public void HandleHeartbeat(NetworkMessage msg)
        {
            var now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

            if (_engine.IsHost)
            {
                _lastHeartbeat[msg.SenderSteamId] = now;
                // Respond with ack
                var ack = new NetworkMessage(PacketType.HeartbeatAck,
                    BitConverter.GetBytes(now));
                _engine.SendToClient(msg.SenderSteamId, ack);
            }
            else
            {
                // Client received host heartbeat — update host heartbeat time
                if (HostPeerId.HasValue)
                    _lastHeartbeat[HostPeerId.Value] = now;
            }
        }

        // ─── Ready State ──────────────────────────────────────────────────

        // Back-compat shim: a bare call means "ready up". Toggle off via SetClientReady(steamId, false).
        public void SetClientReady(ulong steamId) => SetClientReady(steamId, true);

        // Ready is a real toggle. ready=true adds the client + lights its roster flag; ready=false
        // removes it + clears the flag, which de-arms the host start gate (RefreshGateFacts reads the
        // authoritative roster Ready flags). Either way the host re-broadcasts the roster and recomputes
        // AllClientsReady. On the client, this sends the matching ClientReady / ClientUnready packet.
        public void SetClientReady(ulong steamId, bool ready)
        {
            if (_engine.IsHost)
            {
                if (ready) _readyClients.Add(steamId);
                else _readyClients.Remove(steamId);
                if (_clients.TryGetValue(steamId, out var readyClient))
                    readyClient.IsReady = ready;
                OnClientReady?.Invoke(steamId);

                // Ready-state changed → refresh the authoritative roster for all peers.
                BroadcastPeerList();

                // Re-broadcast the authoritative wallet so a (late) ready client converges. No-op
                // until the geoscape wallet is bound (BroadcastFullWallet self-guards); versioned, so
                // already-current peers drop it as stale.
                _engine.Sync?.BroadcastFullWallet();

                // Same convergence for every state channel (inventory, …). Snapshot self-guards until
                // the geoscape model is live; versioned per channel, so current peers drop stale echoes.
                _engine.Sync?.BroadcastAllChannels();

                // AllClientsReady only fires when EVERY connected client is ready. An unready that drops
                // the count below the bar must NOT broadcast it (the gate de-arms via the roster flags).
                if (_readyClients.Count >= _clients.Count && _clients.Count > 0)
                {
                    var allReady = new NetworkMessage(PacketType.AllClientsReady);
                    _engine.BroadcastToAll(allReady);
                    OnAllClientsReady?.Invoke();
                }
            }
            else
            {
                var readyMsg = new NetworkMessage(ready ? PacketType.ClientReady : PacketType.ClientUnready);
                _engine.SendToHost(readyMsg);
            }
        }

        // Host-only: clear every connected client's Ready flag and re-broadcast the authoritative
        // roster. Called when the host swaps the chosen save (clients readied for a specific session).
        public void ResetAllClientsReady()
        {
            if (!_engine.IsHost) return;
            _readyClients.Clear();
            foreach (var c in _clients.Values)
                c.IsReady = false;
            BroadcastPeerList();
        }

        public void HandleReadyState(NetworkMessage msg)
        {
            if (msg.Type == PacketType.ClientReady && _engine.IsHost)
            {
                SetClientReady(msg.SenderSteamId, true);
            }
            else if (msg.Type == PacketType.ClientUnready && _engine.IsHost)
            {
                SetClientReady(msg.SenderSteamId, false);
            }
            else if (msg.Type == PacketType.AllClientsReady)
            {
                OnAllClientsReady?.Invoke();
            }
        }

        // ─── Lobby Roster / Identity ──────────────────────────────────────

        // PEER_LIST (H→all): authoritative lobby roster broadcast. Build from _clients.
        public List<PeerListEntry> BuildPeerList()
        {
            var peers = new List<PeerListEntry>(_clients.Count + 1);

            // Host owns a single SlotAllocator instance, lazily built from its persistent identity so
            // the host is always slot 0. Reused across every BuildPeerList call (never recreated), so
            // a reconnecting client's PlayerGuid maps back to its original slot.
            if (_slots == null) _slots = new SlotAllocator(ClientIdentity.PlayerGuid);
            LocalSlotIndex = 0; // host self

            // Co-op "allow everything" policy: the host also gets FullCommander, keyed by its
            // persistent playerGUID. Idempotent (SetPermission ORs the flag), so safe to call on
            // every roster rebuild. Even though the host bypasses the HostArbiter gate today, this
            // keeps the permission model consistent and lets the roster show the host's real mask.
            PermissionManager.SetPermission(ClientIdentity.PlayerGuid, CampaignPermission.FullCommander, true);

            // Host self-entry first: the host is not in _clients, but the lobby roster (on both the
            // host and every client) must show it. Marked IsHost so each side can identify the host
            // row regardless of its display id (LocalSteamId may be 0 on DirectIP).
            peers.Add(new PeerListEntry
            {
                SteamId = _engine.LocalSteamId,
                PlayerGuid = ClientIdentity.PlayerGuid,
                Nickname = HostNickname,
                Permissions = PermissionManager.GetPermissions(ClientIdentity.PlayerGuid),
                Ready = HostReady,
                IsHost = true,
                SlotIndex = 0
            });

            foreach (var c in _clients.Values)
            {
                c.SlotIndex = _slots.Assign(c.PlayerGuid);
                peers.Add(new PeerListEntry
                {
                    SteamId = c.SteamId,
                    PlayerGuid = c.PlayerGuid,
                    Nickname = c.PlayerName,
                    Permissions = c.Permissions,
                    Ready = c.IsReady,
                    IsHost = false,
                    SlotIndex = c.SlotIndex
                });
            }
            return peers;
        }

        public void BroadcastPeerList()
        {
            if (!_engine.IsHost) return;
            var payload = MessageSerializer.SerializePeerList(BuildPeerList());
            _engine.BroadcastToAll(new NetworkMessage(PacketType.PlayerListUpdate, payload));
        }

        // Client receives the authoritative roster; mirror into local _clients map.
        public void HandlePeerList(NetworkMessage msg)
        {
            if (_engine.IsHost) return;
            var peers = MessageSerializer.DeserializePeerList(msg.Payload);

            // Cache the raw roster (incl. host self-entry + IsHost flags) for the lobby UI.
            _clientRoster = peers;

            // Learn THIS peer's own host-assigned slot by matching the local persistent identity.
            foreach (var p in peers)
                if (p.PlayerGuid == ClientIdentity.PlayerGuid)
                {
                    LocalSlotIndex = p.SlotIndex;
                    // Seed the client's local PermissionManager from the authoritative roster mask so
                    // PermissionGate.Check (which reads ClientIdentity.PlayerGuid) reflects what the host
                    // actually granted. Without this the client's gate is blind — an empty PermissionManager
                    // denies every category, so no client action is ever forwarded to the host. Re-runs on
                    // every roster update, so a future live grant/revoke that re-sends the roster re-seeds the
                    // client automatically. The host remains the sole authority (it re-checks every
                    // ActionRequest); this client-side gate is UX only. Live mid-game grant/revoke via a
                    // dedicated PermissionUpdate packet + a host management UI are deferred future work that
                    // layers on top of this seeding.
                    PermissionManager.SetPermissionsRaw(p.PlayerGuid, p.Permissions);
                }

            // Build the authoritative non-host keep-set while mirroring, then prune any _clients entry
            // absent from it (next block). The host self-entry is never a _clients key, so excluding it
            // from the keep-set preserves it by construction.
            var newClientKeys = new HashSet<ulong>();
            foreach (var p in peers)
            {
                // Do NOT mirror the host self-entry into _clients: _clients holds remote *clients*
                // only (used by ClientCount and other handlers). The host row is served to the UI
                // via _clientRoster / GetLobbyRoster() instead.
                if (p.IsHost) continue;

                newClientKeys.Add(p.SteamId);

                if (!_clients.TryGetValue(p.SteamId, out var client))
                {
                    client = new ClientInfo { SteamId = p.SteamId, ConnectedAt = DateTime.UtcNow };
                    _clients[p.SteamId] = client;
                }
                client.PlayerGuid = p.PlayerGuid;
                client.PlayerName = p.Nickname;
                client.Permissions = p.Permissions;
                client.IsReady = p.Ready;
                client.SlotIndex = p.SlotIndex;
            }

            // Prune peers that vanished from the roster. On a crash/timeout drop the host re-broadcasts
            // ONLY PEER_LIST (no ClientLeave packet → no RemoveClient here), so without this a dropped
            // peer would linger in _clients forever → ClientCount / GetConnectedClients over-count
            // (client status-bar drift). Mirror RemoveClient's cleanup (heartbeat + ready set) so the
            // derived counts stay consistent.
            foreach (var stale in PeerListPrune.PruneKeys(_clients.Keys, newClientKeys))
            {
                _clients.Remove(stale);
                _lastHeartbeat.Remove(stale);
                _readyClients.Remove(stale);
            }
        }

        // LEAVE (C→H / H→all): graceful lobby/session leave.
        public void HandleLeave(NetworkMessage msg)
        {
            var peerSteamId = MessageSerializer.DeserializeLeave(msg.Payload);
            if (_engine.IsHost)
            {
                // Re-broadcast leave so all peers drop the client (RemoveClient refreshes the roster).
                _engine.BroadcastToAll(new NetworkMessage(PacketType.ClientLeave,
                    MessageSerializer.SerializeLeave(peerSteamId)));

                var leaverNick = _clients.TryGetValue(peerSteamId, out var lc) ? lc.PlayerName : "a player";
                SystemChat($"— {leaverNick} left —");
            }
            RemoveClient(peerSteamId);
        }

        // RENAME (any→H→all): live nickname edit.
        // RENAME: live nickname edit. Host-authoritative keying — a client cannot know its own
        // host-side transport peer id, so the host keys the rename by msg.SenderSteamId (the
        // authoritative routing handle), ignoring any id in the payload. After applying, the host
        // re-broadcasts the full roster so every lobby re-renders (clients do not handle this packet
        // directly for state; the PEER_LIST is the single source of truth for the UI).
        public void HandleRename(NetworkMessage msg)
        {
            if (!_engine.IsHost) return;

            var (_, name) = MessageSerializer.DeserializeRename(msg.Payload);
            var senderId = msg.SenderSteamId;

            if (_clients.TryGetValue(senderId, out var client))
            {
                client.PlayerName = name;
                BroadcastPeerList();
            }
        }

        // Client → host: request a nickname change for myself. The host keys it by sender id.
        // On the host this is a local edit (HostNickname) handled directly by the lobby UI.
        public void SendRename(string newNickname)
        {
            if (_engine.IsHost)
            {
                HostNickname = newNickname;
                BroadcastPeerList();
                return;
            }
            // SteamId field is a placeholder; the host substitutes the authoritative sender id.
            var payload = MessageSerializer.SerializeRename(_engine.LocalSteamId, newNickname);
            _engine.SendToHost(new NetworkMessage(PacketType.PlayerRename, payload));
        }

        // HOST_LEFT (H→all): session-end notice.
        // ─── Chat ─────────────────────────────────────────────────────────

        // Local→network user chat. Host stamps + broadcasts directly; client relays to host.
        public void SendChat(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var trimmed = text.Trim();

            if (_engine.IsHost)
            {
                BroadcastChat(_engine.LocalSteamId, HostNickname, trimmed, false);
            }
            else
            {
                var chat = new ChatMessageData
                {
                    SenderSteamId = _engine.LocalSteamId,
                    SenderNick = "",          // host substitutes the authoritative nick
                    Text = trimmed,
                    IsSystem = false
                };
                _engine.SendToHost(new NetworkMessage(PacketType.ChatMessage,
                    MessageSerializer.SerializeChat(chat)));
            }
        }

        // Host-only: emit a SYSTEM line to everyone (join/leave/host-set-save notices).
        public void SystemChat(string text)
        {
            if (!_engine.IsHost || string.IsNullOrEmpty(text)) return;
            BroadcastChat(0, null, text, true);
        }

        // Host-authoritative fan-out: raise locally + push to clients (mirrors BroadcastPeerList).
        private void BroadcastChat(ulong senderId, string senderNick, string text, bool isSystem)
        {
            var chat = new ChatMessageData
            {
                SenderSteamId = senderId,
                SenderNick = senderNick ?? "",
                Text = text,
                IsSystem = isSystem
            };
            // Record into the host-authoritative backlog (arrival order) BEFORE fan-out so late
            // joiners can be replayed the full session history. Bounded ring: drop the oldest line
            // once the cap is hit (matches the panel ChatLog's bounded behaviour).
            _chatHistory.Add(chat);
            if (_chatHistory.Count > ChatHistoryCap)
                _chatHistory.RemoveAt(0);

            OnChatReceived?.Invoke(chat.SenderNick, chat.Text, chat.IsSystem); // local echo on host
            _engine.BroadcastToAll(new NetworkMessage(PacketType.ChatMessage,
                MessageSerializer.SerializeChat(chat)));
        }

        // Host-only: replay the entire chat backlog to a SINGLE newly-joined client, in arrival
        // order, as ordinary ChatMessage packets (the client's existing HandleChat receive path
        // appends each one). Sent only to that client (SendToClient), so peers already in sync are
        // never re-sent the backlog. No new packet type — reuses the proven chat serialization.
        public void ReplayChatHistoryTo(ulong clientId)
        {
            if (!_engine.IsHost || _chatHistory.Count == 0) return;
            foreach (var chat in _chatHistory)
            {
                _engine.SendToClient(clientId, new NetworkMessage(PacketType.ChatMessage,
                    MessageSerializer.SerializeChat(chat)));
            }
        }

        public void HandleChat(NetworkMessage msg)
        {
            var chat = MessageSerializer.DeserializeChat(msg.Payload);

            if (_engine.IsHost)
            {
                // Validate + stamp the authoritative sender (mirror HandleRename keying).
                if (chat.IsSystem) return; // clients may not inject system lines
                var nick = "Player";
                if (_clients.TryGetValue(msg.SenderSteamId, out var c))
                    nick = c.PlayerName;
                BroadcastChat(msg.SenderSteamId, nick, chat.Text, false);
            }
            else
            {
                // Client: render exactly what the host broadcast.
                OnChatReceived?.Invoke(chat.SenderNick, chat.Text, chat.IsSystem);
            }
        }

        // ─── Chosen save (host picks; clients mirror read-only) ───────────

        // Host-only: record the chosen save, broadcast it (SetSave) + a SYSTEM chat line.
        public void SetChosenSave(string saveName, string saveMeta)
        {
            if (!_engine.IsHost) return;
            ChosenSaveName = saveName;
            ChosenSaveMeta = saveMeta;
            OnChosenSaveChanged?.Invoke(saveName, saveMeta);
            _engine.BroadcastToAll(new NetworkMessage(PacketType.SetSave,
                MessageSerializer.SerializeSetSave(saveName, saveMeta)));
            SystemChat($"— host set save: {saveName} —");
        }

        public void HandleSetSave(NetworkMessage msg)
        {
            if (_engine.IsHost) return;
            var (name, meta) = MessageSerializer.DeserializeSetSave(msg.Payload);
            ChosenSaveName = name;
            ChosenSaveMeta = meta;
            OnChosenSaveChanged?.Invoke(name, meta);
        }

        public void HandleHostDisconnected(NetworkMessage msg)
        {
            Debug.LogWarning("[Multiplayer] Host disconnected — session ended");
            OnHostDisconnected?.Invoke();
        }
    }

    public class ClientInfo
    {
        public ulong SteamId { get; set; }            // per-session peerID (transport handle)
        public Guid PlayerGuid { get; set; }          // persistent identity (JOIN); permission/ownership key
        public string Endpoint { get; set; }
        public string PlayerName { get; set; } = "Unknown";
        public int Permissions { get; set; }
        public bool IsReady { get; set; }             // mirror of _readyClients for PEER_LIST broadcast
        public int LatencyMs { get; set; }
        public byte SlotIndex { get; set; }           // host-assigned stable slot (echoed in PEER_LIST)
        public DateTime ConnectedAt { get; set; }
    }
}
