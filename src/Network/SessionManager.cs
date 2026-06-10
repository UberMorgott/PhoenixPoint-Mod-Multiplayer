using System;
using System.Collections.Generic;
using Multipleer.Network.MessageLayer;
using Multipleer.Validation;
using UnityEngine;

namespace Multipleer.Network
{
    public class SessionManager
    {
        private readonly NetworkEngine _engine;
        private readonly Dictionary<ulong, ClientInfo> _clients = new Dictionary<ulong, ClientInfo>();
        private readonly Dictionary<ulong, long> _lastHeartbeat = new Dictionary<ulong, long>();
        private readonly HashSet<ulong> _readyClients = new HashSet<ulong>();

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
        public event Action<Guid, CampaignPermission, bool> OnPermissionUpdated;
        public event Action<byte[]> OnInitialGameStateReceived;
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
                    Debug.LogWarning($"[Multipleer] Client {clientId} timed out");
                    RemoveClient(clientId);
                }
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

        public IEnumerable<ulong> GetConnectedClients()
        {
            return _clients.Keys;
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
                Debug.LogWarning($"[Multipleer] Rejecting JOIN from {clientId}: missing/empty playerGUID.");
                var reject = new NetworkMessage(PacketType.ConnectionRejected,
                    NetworkMessage.BuildStringPayload("Invalid player identity (empty GUID)."));
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
            }

            // Send acceptance
            var acceptMsg = new NetworkMessage(PacketType.ConnectionAccepted);
            _engine.SendToClient(clientId, acceptMsg);

            // Roster changed → broadcast authoritative peer list.
            BroadcastPeerList();

            SystemChat($"— {(string.IsNullOrEmpty(join.Nickname) ? "a player" : join.Nickname)} joined —");
        }

        public void HandleConnectionAccepted(NetworkMessage msg)
        {
            // Client received host confirmation
            Debug.Log("[Multipleer] Connection accepted by host");
        }

        public void HandleConnectionRejected(NetworkMessage msg)
        {
            var reason = NetworkMessage.ParseStringPayload(msg.Payload);
            Debug.LogError($"[Multipleer] Connection rejected: {reason}");
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

        public void SetClientReady(ulong steamId)
        {
            if (_engine.IsHost)
            {
                _readyClients.Add(steamId);
                if (_clients.TryGetValue(steamId, out var readyClient))
                    readyClient.IsReady = true;
                OnClientReady?.Invoke(steamId);

                // Ready-state changed → refresh the authoritative roster for all peers.
                BroadcastPeerList();

                if (_readyClients.Count >= _clients.Count && _clients.Count > 0)
                {
                    var allReady = new NetworkMessage(PacketType.AllClientsReady);
                    _engine.BroadcastToAll(allReady);
                    OnAllClientsReady?.Invoke();
                }
            }
            else
            {
                var readyMsg = new NetworkMessage(PacketType.ClientReady);
                _engine.SendToHost(readyMsg);
            }
        }

        public void HandleReadyState(NetworkMessage msg)
        {
            if (msg.Type == PacketType.ClientReady && _engine.IsHost)
            {
                SetClientReady(msg.SenderSteamId);
            }
            else if (msg.Type == PacketType.AllClientsReady)
            {
                OnAllClientsReady?.Invoke();
            }
        }

        // ─── Permission Updates ───────────────────────────────────────────

        public void HandlePermissionUpdate(NetworkMessage msg)
        {
            var (guid, flagBit, value) =
                MessageSerializer.DeserializePermissionUpdate(msg.Payload);

            var flag = (CampaignPermission)(1 << flagBit);
            PermissionManager.SetPermission(guid, flag, value);

            // Mirror the resulting mask onto the matching ClientInfo (looked up by GUID).
            var mask = PermissionManager.GetPermissions(guid);
            foreach (var client in _clients.Values)
            {
                if (client.PlayerGuid == guid)
                {
                    client.Permissions = mask;
                    break;
                }
            }

            OnPermissionUpdated?.Invoke(guid, flag, value);
        }

        // ─── Lobby Roster / Identity ──────────────────────────────────────

        // PEER_LIST (H→all): authoritative lobby roster broadcast. Build from _clients.
        public List<PeerListEntry> BuildPeerList()
        {
            var peers = new List<PeerListEntry>(_clients.Count + 1);

            // Host self-entry first: the host is not in _clients, but the lobby roster (on both the
            // host and every client) must show it. Marked IsHost so each side can identify the host
            // row regardless of its display id (LocalSteamId may be 0 on DirectIP).
            peers.Add(new PeerListEntry
            {
                SteamId = _engine.LocalSteamId,
                PlayerGuid = ClientIdentity.PlayerGuid,
                Nickname = HostNickname,
                Permissions = 0,
                Ready = HostReady,
                IsHost = true
            });

            foreach (var c in _clients.Values)
            {
                peers.Add(new PeerListEntry
                {
                    SteamId = c.SteamId,
                    PlayerGuid = c.PlayerGuid,
                    Nickname = c.PlayerName,
                    Permissions = c.Permissions,
                    Ready = c.IsReady,
                    IsHost = false
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

            foreach (var p in peers)
            {
                // Do NOT mirror the host self-entry into _clients: _clients holds remote *clients*
                // only (used by ClientCount and other handlers). The host row is served to the UI
                // via _clientRoster / GetLobbyRoster() instead.
                if (p.IsHost) continue;

                if (!_clients.TryGetValue(p.SteamId, out var client))
                {
                    client = new ClientInfo { SteamId = p.SteamId, ConnectedAt = DateTime.UtcNow };
                    _clients[p.SteamId] = client;
                }
                client.PlayerGuid = p.PlayerGuid;
                client.PlayerName = p.Nickname;
                client.Permissions = p.Permissions;
                client.IsReady = p.Ready;
            }
        }

        // ASSIGN_OWNER (H→all): soldierID→playerGUID ownership (Guid.Empty = unassign).
        public void HandleAssignOwner(NetworkMessage msg)
        {
            var (geoUnitId, owner) = MessageSerializer.DeserializeAssignOwner(msg.Payload);
            if (owner == Guid.Empty)
            {
                var current = PermissionManager.GetOwnerOfSoldier(geoUnitId);
                if (current.HasValue)
                    PermissionManager.UnassignSoldier(current.Value, geoUnitId);
            }
            else
            {
                PermissionManager.AssignSoldier(owner, geoUnitId);
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
            OnChatReceived?.Invoke(chat.SenderNick, chat.Text, chat.IsSystem); // local echo on host
            _engine.BroadcastToAll(new NetworkMessage(PacketType.ChatMessage,
                MessageSerializer.SerializeChat(chat)));
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
            Debug.LogWarning("[Multipleer] Host disconnected — session ended");
            OnHostDisconnected?.Invoke();
        }

        // ─── Game State Sync ──────────────────────────────────────────────

        public void HandleInitialGameState(NetworkMessage msg)
        {
            OnInitialGameStateReceived?.Invoke(msg.Payload);
        }

        public void HandleStateSync(NetworkMessage msg)
        {
            // Will be expanded for delta state sync
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
        public DateTime ConnectedAt { get; set; }
    }
}
