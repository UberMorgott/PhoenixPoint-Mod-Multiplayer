using System;
using System.Collections.Generic;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Network.Parity;
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
        // SECURITY: first-seen binding of a persistent PlayerGuid to the authenticated transport
        // identity (SteamId) that claimed it. PlayerGuid is public (broadcast in PEER_LIST), so this
        // binding is what stops a peer from JOINing under another player's guid to inherit their slot /
        // permissions / owned soldiers (identity takeover). Released in RemoveClient (peer-detach
        // chokepoint) — it only ever covers a CURRENTLY CONNECTED peer; see the takeover check in
        // HandleConnectionRequest for why it must not survive a disconnect.
        private readonly Dictionary<Guid, ulong> _guidOwners = new Dictionary<Guid, ulong>();
        // Last display name seen for a peer whose roster row has since been purged. Written at the one
        // detach chokepoint (RemoveClient), read only by the leave notice (HandleLeave) so a farewell that
        // arrives after the row is gone still names the player instead of "a player". Never authoritative:
        // the live row wins wherever there is one (SessionLifecycle.LeaverName).
        private readonly Dictionary<ulong, string> _lastKnownNames = new Dictionary<ulong, string>();

        // "Have the others already been told this peer is gone?" — see DepartureLatch. Consulted by the
        // ONE departure chokepoint (AnnounceDeparture); re-armed at every return edge (ResumePeer, and a
        // fresh roster row in AddClient) so the peer's NEXT departure is news again.
        private readonly DepartureLatch _departureAnnounced = new DepartureLatch();

        // Host-authoritative chat backlog (whole-session history). Every line the host fans out via
        // BroadcastChat is appended here in arrival order; on a new client fully joining the host
        // replays this backlog to ONLY that client (ReplayChatHistoryTo) so late joiners see the
        // full prior conversation. Host-only — clients render straight from OnChatReceived and never
        // populate this. Capped to bound memory across a long session; the cap is generous so a
        // normal lobby never drops a line.
        private readonly List<ChatMessageData> _chatHistory = new List<ChatMessageData>();
        private const int ChatHistoryCap = 500;

        // A CHAT LINE HAS AN IDENTITY, AND THAT IS THE WHOLE FIX. The backlog above is replayed to a
        // joiner at OnSessionRequest, but the joiner is on the TRANSPORT's broadcast set from the first
        // packet it ever sends (SteamTransport.Update registers it, 5e6ab9d) — which is BEFORE the JOIN it
        // sent has been handled. Any line the host fans out inside that window is delivered LIVE to the
        // joiner and then again in its backlog replay, and until now the two copies were indistinguishable:
        // same sender, same text, no key. Reported 2026-08-06 — one message doubled at the moment a peer
        // connected and never again, which is exactly the shape of a one-time window.
        // Host stamps (_chatSeq, session-monotonic, carried into _chatHistory so the replay copy keeps the
        // live copy's number); every receiver drops a number it has already rendered (_seenChatSeq). This
        // also covers the other replay shapes for free — a repeated JOIN, or a retransmit.
        private uint _chatSeq;
        // ponytail: unbounded set, 4 bytes per line ever received; a session that overran it would have to
        // send millions of chat lines. Bound it to a ring only if that ever stops being absurd.
        private readonly HashSet<uint> _seenChatSeq = new HashSet<uint>();

        public ulong? HostPeerId { get; private set; }
        public IReadOnlyDictionary<ulong, ClientInfo> Clients => _clients;

        // Host's own lobby identity. The host is NOT in _clients (which holds remote peers only),
        // so its row is injected into the broadcast roster via a self-entry in BuildPeerList. The
        // lobby UI sets these; defaults give a sensible display before the player edits anything.
        public string HostNickname { get; set; } = ClientIdentity.LocalNickname ?? "Host";
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
        // FIX-1 deadline: the transfer/load liveness suspension below is honoured only while the
        // transfer shows PROGRESS (SaveTransferCoordinator.LastProgressMs keeps moving). A silently
        // dead host (STUN, missing Steam fail callback, TCP half-open) leaves the transfer flags
        // latched — without this deadline the client parked at "Waiting for players…" forever with
        // every host-loss detector off.
        private const long TransferStallMs = 60_000;
        private bool _transferStallLogged;
        private long _lastHeartbeatSend;
        // FIX-2: client-side outbound-liveness clock — the last time the host ACKed one of our
        // heartbeats. Distinct from _lastHeartbeat[host] (inbound liveness): it detects a HALF-OPEN
        // send channel (host traffic still arriving, but the host stopped receiving/acking ours).
        private long _lastHeartbeatAck;
        // Half-open recovery budget: one automatic session-reset + re-JOIN per connection before the
        // detector declares the link dead. Reset on each (re)connect in SetHostPeer.
        private bool _halfOpenRepairAttempted;

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
            var now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            _lastHeartbeat[hostPeerId] = now;
            _lastHeartbeatAck = now; // FIX-2: seed the outbound-liveness (ack) clock at connect
            _halfOpenRepairAttempted = false; // fresh one-shot repair budget per (re)connect
        }

        public void Update()
        {
            var now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            FlushPeerList(); // one roster per frame, however many things changed in it (see BroadcastPeerList)

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

            // FIX-1: while a co-op save transfer / world-load is in flight, SUSPEND every heartbeat
            // timeout. Over WAN a big bulk transfer can starve heartbeats, and a slow-HDD native
            // world-load can block the busy peer's main thread for >20 s — in both cases the peer is
            // perfectly alive but no heartbeat gets through the window. The transfer/barrier machinery
            // proves liveness chunk-by-chunk (and owns its OWN straggler timeout: Phase1LoadTimeoutMs /
            // BarrierLivenessGraceMs), and RefreshLiveness keeps _lastHeartbeat fresh as chunks arrive, so
            // nothing false-fires the instant the load ends.
            var st = _engine.SaveTransfer;
            bool loadInFlight = st != null && (st.TransferActive || st.InPhase2 || st.LoadPhaseStarted);

            // Suspension deadline: suspend only while the transfer shows PROGRESS. LastProgressMs is
            // bumped on every chunk / roster snapshot / barrier flag edge / phase-2 percent
            // (SaveTransferCoordinator.NoteProgress); a dead peer stops all of those, so after
            // TransferStallMs the normal detectors below resume and fire the standard host-loss /
            // client-reaper teardown. Engine-level — no per-scenario branches.
            if (loadInFlight && now - st.LastProgressMs > TransferStallMs)
            {
                if (!_transferStallLogged)
                {
                    _transferStallLogged = true;
                    Debug.LogWarning($"[Multiplayer] transfer/load shows no progress for " +
                                     $"{TransferStallMs / 1000}s — re-arming liveness detectors.");
                }
                loadInFlight = false;
            }
            else if (loadInFlight) _transferStallLogged = false;

            if (loadInFlight)
            {
                // suspended this tick (see note above). FIX-2: also keep the outbound-liveness (ack)
                // clock fresh so a long load that starves acks does not false-fire the half-open
                // detector the instant the load ends.
                _lastHeartbeatAck = now;
            }
            // Heartbeat timeout check (host only)
            else if (_engine.IsHost)
            {
                var toRemove = new List<ulong>();
                foreach (var kvp in _lastHeartbeat)
                {
                    if (now - kvp.Value > HeartbeatTimeoutMs)
                        toRemove.Add(kvp.Key);
                }
                // A silent peer is PAUSED, never kicked (N=50 mandate — see PausePeer). The socket is
                // left alone on purpose: a peer whose game hitched, whose ISP blipped or who is simply on
                // a bad link is still there, and tearing its transport down is what turned a hiccup into
                // "you were disconnected". The heartbeat row is kept too, so the resume edge is just the
                // next heartbeat that arrives (HandleHeartbeat → ResumePeer).
                foreach (var clientId in toRemove)
                    PausePeer(clientId, $"no heartbeat for {HeartbeatTimeoutMs / 1000}s");
            }
            // F3 (client side): host HEARTBEAT TIMEOUT. A wedged/half-open host socket may never send
            // FIN/RST, so OnPeerDisconnected (trigger b) never fires and the client would be stranded.
            // If we have not heard from the host within the timeout, route into the SAME host-leave
            // handler (its one-shot latch dedups against a graceful HostDisconnected packet / real drop,
            // and is also a no-op after the handler already ran). Reuses the existing heartbeat constants.
            else if (HostPeerId.HasValue && !HostLeaveHandler.AlreadyHandled
                     && _lastHeartbeat.TryGetValue(HostPeerId.Value, out var hostLast))
            {
                if (now - hostLast > HeartbeatTimeoutMs)
                {
                    Debug.LogWarning("[Multiplayer] Host heartbeat timed out — treating as host-leave.");
                    HostLeaveHandler.TriggerHostLeft();
                }
                // FIX-2 + P2P auto-repair: HALF-OPEN detection. Even while host traffic keeps ARRIVING
                // (inbound timeout above does not fire), if the host has not ACKed our heartbeats for the
                // whole window then our OUTBOUND (client->host) channel is dead — a half-open Steam P2P
                // session the transport still reports as Connected. Instead of immediately declaring it
                // dead, attempt ONE automatic repair (reset the P2P session + re-send JOIN); only if the
                // repaired link is STILL dead a full window later do we tear down via the SAME chokepoint
                // (Steam lobby Leave + ClearRichPresence + return-to-menu) with a never-silent reason.
                else switch (HalfOpenRepair.Decide(now, hostLast, _lastHeartbeatAck,
                                                    HeartbeatTimeoutMs, _halfOpenRepairAttempted))
                {
                    case HalfOpenAction.Repair:
                        _halfOpenRepairAttempted = true;
                        Debug.LogWarning("[Multiplayer][P2PRepair] Host heartbeat-ack timed out (client->host " +
                            "channel dead) — resetting the P2P session and re-sending JOIN (one-shot repair).");
                        _engine.RepairHostLink();
                        // Give the repaired link a fresh full window before the fatal path: reseed BOTH
                        // clocks so neither the inbound nor the ack timeout fires again until the repair
                        // has had a full HeartbeatTimeoutMs to prove out.
                        _lastHeartbeatAck = now;
                        _lastHeartbeat[HostPeerId.Value] = now;
                        break;
                    case HalfOpenAction.Fail:
                        Debug.LogWarning("[Multiplayer][P2PRepair] Host heartbeat-ack still timed out after repair — " +
                            "outbound channel dead; leaving.");
                        HostLeaveHandler.TriggerHostLeft(
                            "Connection to host lost (send channel dead — stage: heartbeat-ack timeout).");
                        break;
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
                // A fresh roster row is a peer that is HERE, so whatever was announced about its last
                // absence is spent: its next departure has to be announceable again.
                _departureAnnounced.Rearm(steamId);
                // SlotIndex is assigned in BuildPeerList (host), keyed by the client's persistent
                // PlayerGuid so a reconnecting client reuses its slot.
            }
        }

        /// <summary>
        /// THE involuntary-loss rule (N=50 mandate, 2026-08-05): a peer that stops answering — heartbeat
        /// silence, a dead socket, a send channel that failed — is PAUSED, not removed. Its roster row,
        /// slot, permissions and guid binding all survive, so when it comes back it resumes the seat it
        /// had instead of arriving as a stranger who must fight its way back in. Nothing in the mod counts
        /// peers any more, so a paused peer costs the session nothing while it is away.
        ///
        /// <see cref="RemoveClient"/> is now reserved for a peer that LEFT: the ClientLeave packet, or a
        /// stale row being reclaimed by its own returning owner.
        /// </summary>
        public void PausePeer(ulong steamId, string reason)
        {
            if (!_clients.TryGetValue(steamId, out var client)) return;
            if (client.IsPaused) return;
            client.IsPaused = true;
            Debug.LogWarning($"[Multiplayer] Peer {steamId} ({client.PlayerName}) PAUSED: {reason}. " +
                             "Roster row kept — it resumes its seat when it comes back.");
            if (_engine.IsHost)
            {
                // A NOTICE, not a chat line: the other players are mid-geoscape or mid-battle with no chat
                // panel open, and "did he rage-quit or is his wifi down?" is exactly the question this
                // feature exists to answer. The wording says what the host OBSERVED (silence), never that
                // the player left — it did not, and its seat is still here (L84).
                AnnounceDeparture(steamId, "pause(" + reason + ")",
                    SessionLifecycle.FormatConnectionLostNotice(client.PlayerName));
                BroadcastPeerList();
            }
        }

        /// <summary>The peer is answering again. Its own seq-gap resync + the rail's CRC backstop pull it
        /// back level; nothing here has to replay anything.</summary>
        public void ResumePeer(ulong steamId)
        {
            if (!_clients.TryGetValue(steamId, out var client) || !client.IsPaused) return;
            client.IsPaused = false;
            Debug.Log($"[Multiplayer] Peer {steamId} ({client.PlayerName}) RESUMED.");
            // The return edge closes this absence: whatever was announced about it is spent, and the
            // peer's NEXT departure has to reach everyone's screen again.
            _departureAnnounced.Rearm(steamId);
            if (_engine.IsHost)
            {
                SystemNotice(SessionLifecycle.FormatReconnectedNotice(client.PlayerName));
                BroadcastPeerList();
            }
        }

        public void RemoveClient(ulong steamId)
        {
            var existed = _clients.TryGetValue(steamId, out var client);
            if (existed)
            {
                client.IsReady = false;
                // RELEASE the guid→peer binding: this is the host-side peer-DETACH chokepoint, so the
                // attach in HandleJoin has exactly one symmetric partner here and every drop path
                // (transport disconnect, heartbeat reaper, graceful leave, stale-rejoin prune) inherits it.
                // Without this a returning player was refused forever — its peer id is a per-connection
                // counter, so a reconnect can never present the id the guid was bound to.
                // NOT cleared: the guid-keyed slot and permissions, which intentionally survive a detach so
                // a reconnecting player gets its own slot and rights back.
                if (client.PlayerGuid != Guid.Empty)
                    _guidOwners.Remove(client.PlayerGuid);
                // The name outlives the row by exactly one purpose: a ClientLeave that arrives after the row
                // is gone still has to say WHO left (SessionLifecycle.LeaverName). Same chokepoint argument
                // as the guid release above — every drop path passes through here.
                if (!string.IsNullOrEmpty(client.PlayerName))
                    _lastKnownNames[steamId] = client.PlayerName;
            }
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

        /// <summary>
        /// Client-only (graceful leave notice): tell the host we are leaving BEFORE the transport goes
        /// down (menu quit / Alt+F4), so it drops us instantly instead of waiting out the 20 s heartbeat
        /// timeout (which stays as the crash backstop). Host-side <see cref="HandleLeave"/> posts the
        /// "— X left —" line and re-broadcasts; the later transport drop then reports wasKnown=false →
        /// no duplicate notice. Best-effort: a no-op on the host or with no live host link.
        /// </summary>
        public void SendClientLeave()
        {
            if (_engine.IsHost || !HostPeerId.HasValue) return;
            _engine.SendToHost(new NetworkMessage(PacketType.ClientLeave,
                MessageSerializer.SerializeLeave(_engine.LocalSteamId)));
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

        // ─── The tactical "I have finished moving" ADVISORY flag ───────────
        //
        // WHY IT LIVES HERE AND NOT ON THE RAIL. It is per-peer bookkeeping, which is this class's whole
        // job — and RailCheck L91 arm (c) forbids the rail namespaces from holding ANY static peer-id
        // collection, precisely so a per-peer table can never grow into a quorum next to a decision. The
        // rail side (Multiplayer.Tactical.TacticalReadySync) therefore keeps two ints and one bool: the
        // numbers on a LABEL. Nothing in the mod reads this tally to decide anything — RailCheck L119.

        /// <summary>The HOST's own advisory flag (the host is not in <c>_clients</c>, same shape as
        /// <see cref="HostReady"/>).</summary>
        public bool HostTacReady { get; set; }

        /// <summary>Record one peer's advisory flag. Unknown peer = dropped: a flag from somebody who is
        /// not seated changes no seat, and the tally is rebuilt from the roster on every read anyway.</summary>
        public void SetTacReady(ulong peerId, bool ready)
        {
            if (_clients.TryGetValue(peerId, out var c)) c.TacReady = ready;
        }

        /// <summary>The advisory tally the tactical ready button LABELS itself with: <paramref name="ready"/>
        /// of <paramref name="total"/>. A PAUSED peer keeps its seat (it counts in <paramref name="total"/>,
        /// law L84) but counts as NOT ready: its last flag was sent before it went silent, and showing
        /// "everybody is ready" for a player who is not there is exactly the lie this tally must not tell.
        /// Rebuilt on every call rather than incrementally maintained — the roster is the truth and a
        /// cached count is one more thing that can disagree with it.</summary>
        public void TacReadyTally(out int ready, out int total)
        {
            total = 1;                        // the host's own seat
            ready = HostTacReady ? 1 : 0;
            foreach (var c in _clients.Values)
            {
                ++total;
                if (c.TacReady && !c.IsPaused) ++ready;
            }
        }

        /// <summary>Every seat back to "not ready" — driven from the game's own new-player-round edge
        /// (TacticalReadySync.OnNewTurn off <c>TacMission.OnNewTurn</c>).</summary>
        public void ResetTacReady()
        {
            HostTacReady = false;
            foreach (var c in _clients.Values) c.TacReady = false;
        }

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

            // HARDENING (second-line defense): a JOIN whose persistent playerGUID EQUALS the host's own
            // is a guid collision — both instances loaded the SAME persistentDataPath/identity.json (see
            // ClientIdentity). Onboarding it would silently collapse the client into host slot 0
            // (SlotAllocator seeds the host guid at slot 0) AND share the host's permission/ownership key.
            // REFUSE loudly — a colliding identity is an operational misconfig, not a game state; give the
            // 2nd same-machine instance a distinct identity (auto identity-N.json, or set MULTIPLAYER_IDENTITY).
            if (SessionLifecycle.IsSelfIdentityCollision(ClientIdentity.PlayerGuid, join.PlayerGuid))
            {
                Debug.LogError($"[Multiplayer] REJECTING JOIN from {clientId}: client playerGUID {join.PlayerGuid} " +
                               "EQUALS the host's own identity — both instances share identity.json (operational " +
                               "misconfig). Launch the 2nd instance with a distinct identity (auto identity-N.json, " +
                               "or set MULTIPLAYER_IDENTITY). Refusing to avoid collapsing the client into host slot 0.");
                var reject = new NetworkMessage(PacketType.ConnectionRejected,
                    NetworkMessage.BuildStringPayload(
                        "Identity collision: this instance shares the host's player identity. " +
                        "Relaunch the client with a distinct MULTIPLAYER_IDENTITY."));
                _engine.SendToClient(clientId, reject);
                return;
            }

            // HARDENING (identity takeover): PlayerGuid is the permission/ownership key but it is PUBLIC
            // (broadcast to every peer in PEER_LIST), so a peer could JOIN carrying ANOTHER player's guid
            // and be treated as that player "reconnecting" — evicting the victim (below) and inheriting
            // their slot/permissions/owned soldiers. Bind the guid to the peer id that claimed it, and
            // refuse a DIFFERENT peer re-asserting the same guid → reject BEFORE any stale-peer eviction.
            //
            // SCOPE (do not over-read this check): the binding is released in RemoveClient, so it only ever
            // covers a guid held by a CURRENTLY CONNECTED peer — which is the only window in which takeover
            // means anything. It deliberately does NOT survive a disconnect: on DirectTransport `clientId` is
            // a per-connection counter (DirectTransport._nextPeerId), NOT a stable authenticated identity, so
            // every reconnect legitimately arrives under a NEW id. Holding the binding past disconnect locked
            // returning players out of their own identity forever. Real cross-session identity authentication
            // needs a stable per-peer key the transport can vouch for; a counter cannot provide one.
            if (_guidOwners.TryGetValue(join.PlayerGuid, out var boundSteamId) && boundSteamId != clientId)
            {
                // Takeover defense only applies to a LIVE binding. A crash / half-open drop inside the
                // 20 s heartbeat window leaves the guid bound to a dead peer id the transport never
                // reported down; the returning player arrives under a NEW per-connection counter id and
                // would hit this reject against its OWN stale binding — the exact rejoin the stale-prune
                // below exists to allow. A silent binding falls through to that prune instead.
                if (IsPeerLive(boundSteamId))
                {
                    Debug.LogError($"[Multiplayer] REJECTING JOIN from {clientId}: playerGUID {join.PlayerGuid} is " +
                                   $"already bound to connected peer {boundSteamId} — identity-takeover attempt.");
                    var reject = new NetworkMessage(PacketType.ConnectionRejected,
                        NetworkMessage.BuildStringPayload("Player identity is already in use by another player."));
                    _engine.SendToClient(clientId, reject);
                    return;
                }
                Debug.Log($"[Multiplayer] playerGUID {join.PlayerGuid} is bound to SILENT peer {boundSteamId} — " +
                          $"treating JOIN from {clientId} as a returning-player rejoin, not a takeover.");
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


            // NOBODY IS REFUSED AT THE DOOR (N=50 mandate). Two refusals used to live here — a JOIN that
            // landed while a save transfer was in flight, and a mid-session JOIN while the host was in a
            // battle or mid-load — and both ended in ConnectionRejected + DisconnectPeer, i.e. "come back
            // later and hope". The first one's whole justification is gone with the LOADED quorum (there is
            // no phase-1 to stall any more). The second names a real limitation — a joiner is reproduced
            // from a GEOSCAPE save, and the host has none to hand mid-battle — but that is a reason to make
            // the peer WAIT, not to throw it out. So: onboard the lobby row now (it already has one from
            // OnPeerConnected), and DEFER only the save transfer until the host is somewhere it can serve
            // one. The peer sits in the lobby holding its seat and is pulled in the moment the host is back
            // on the geoscape — the same on-demand path every other joiner rides.
            bool sessionStarted = _engine.SaveTransfer?.SessionStarted == true;
            bool deferOnboard = sessionStarted &&
                                (_engine.SaveTransfer?.TransferActive == true ||
                                 SessionLifecycle.ShouldRejectMidSessionJoin(true, Sync.GeoRuntime.Instance.IsGeoscapeActive));

            // FIX-4 (SOFT GATE): host/client parity check — the mismatched client still JOINS the lobby
            // normally, but the host stores the exact diff text on its roster row. The diff rides the
            // PEER_LIST broadcast, so BOTH sides render the warning badge (host on that player's row,
            // the client on its own row) and the client locks its READY button; the host ALSO gates
            // Ready authoritatively in SetClientReady (never trust client UI). Host-authoritative: the
            // host computes the one diff text every UI shows.
            var hostManifest = ParityManifestCollector.Collect();
            var parityDiffs = ParityComparer.Compare(hostManifest, join.Manifest);
            var parityDiffText = ParityComparer.Format(parityDiffs);
            if (parityDiffs.Count > 0)
                Debug.LogWarning($"[Multiplayer] JOIN from {clientId} has parity mismatch (soft-gate, " +
                                 $"ready locked):\n{parityDiffText}");

            AddClient(clientId, $"Steam({clientId})");

            // Bind peerID <-> playerGUID.
            if (_clients.TryGetValue(clientId, out var client))
            {
                client.PlayerGuid = join.PlayerGuid;
                // ATTACH the guid→peer binding HERE, paired with the release in RemoveClient, and only once
                // the peer is actually on the roster. Binding at the takeover check instead (where it used to
                // live) leaked on every reject that returns after it — and the stale-rejoin prune below runs
                // RemoveClient for the SAME guid, which would wipe a binding made any earlier.
                _guidOwners[join.PlayerGuid] = clientId;
                if (!string.IsNullOrEmpty(join.Nickname))
                    client.PlayerName = join.Nickname;
                client.ParityDiffs = parityDiffText; // "" = parity OK

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

            // Send acceptance, carrying the HOST parity manifest so the client can auto-apply the
            // host's mod settings (ParityConfigSync) and re-send a fresh manifest (ParityUpdate).
            // A legacy client ignores the payload (it never read one) — harmless.
            var acceptMsg = new NetworkMessage(PacketType.ConnectionAccepted,
                MessageSerializer.SerializeParityManifest(hostManifest));
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
            {
                if (deferOnboard)
                {
                    Debug.Log($"[Multiplayer] JOIN from {clientId} accepted; save transfer DEFERRED (host is " +
                              "mid-battle/mid-load or already transferring) — it will be served automatically.");
                    SystemChat($"— {(string.IsNullOrEmpty(join.Nickname) ? "a player" : join.Nickname)} is waiting " +
                               "for the host to return to the Geoscape —");
                    _engine.SaveTransfer?.DeferOnDemandJoin(clientId);
                }
                else _engine.SaveTransfer?.HostOnDemandJoin(clientId);
            }
        }

        /// <summary>CLIENT-only, set on the accept: the pending "your Multiplayer version differs from the
        /// host's" notice ("" / null = versions match or unknown). MultiplayerUI drains it exactly once, on
        /// the same frame it would have opened the lobby, and shows it natively BEFORE the lobby appears.
        /// A plain field so the whole gate stays one decision in <see cref="ParityComparer"/>.</summary>
        public string VersionMismatchNotice { get; set; } = "";

        public void HandleConnectionAccepted(NetworkMessage msg)
        {
            // Client received host confirmation
            Debug.Log("[Multiplayer] Connection accepted by host");

            // Parity auto-apply (host-authoritative): the accept carries the HOST manifest. Apply the
            // host's scalar mod settings in-memory (originals snapshotted; restored at teardown), then
            // re-send a FRESH manifest so the host re-compares — if only config diffs existed the
            // mismatch clears (badge disappears, READY unlocks via the roster re-broadcast). A legacy
            // host sends no payload → skip. Unappliable values stay diffs by construction.
            if (_engine.IsHost || msg.Payload == null || msg.Payload.Length == 0) return;
            try
            {
                var hostManifest = MessageSerializer.DeserializeParityManifest(msg.Payload);

                // VERSION GATE, at the DOOR. This is the first instant either side holds BOTH Multiplayer
                // versions: our own rode the JOIN (ConnectionRequest, the very first client→host message),
                // the host's rides this accept — and the accept lands before the host's first PEER_LIST,
                // which is what MultiplayerUI waits on to open the lobby. So the notice raised from here is
                // shown while the player is still on the "Connecting…" box, never after they have settled
                // into a lobby. Stored, not popped: SessionManager owns no UI (and this runs on a packet
                // callback with no Timing.Current). Nobody is disconnected over it (L84) — the host's own
                // parity gate already keeps this peer's READY locked, and ONLY this peer's.
                VersionMismatchNotice = ParityComparer.VersionNoticeForClient(
                    hostManifest, ParityManifestCollector.Collect());
                if (!string.IsNullOrEmpty(VersionMismatchNotice))
                    Debug.LogWarning("[Multiplayer] " + VersionMismatchNotice.Replace("\n", " "));

                if (ParityConfigSync.ApplyHostSettings(hostManifest))
                {
                    var fresh = ParityManifestCollector.Collect();
                    _engine.SendToHost(new NetworkMessage(PacketType.ParityUpdate,
                        MessageSerializer.SerializeParityManifest(fresh)));
                }
            }
            catch (Exception e) { Debug.LogError("[Multiplayer] parity auto-apply on accept failed: " + e.Message); }
        }

        /// <summary>
        /// Host: a client re-sent its parity manifest after auto-applying our settings. Re-compare and,
        /// on any status change, update its roster row + re-broadcast (badge/ready-lock update flows
        /// through the SAME PEER_LIST rail as the initial state).
        /// </summary>
        public void HandleParityUpdate(NetworkMessage msg)
        {
            if (!_engine.IsHost) return;
            if (!_clients.TryGetValue(msg.SenderSteamId, out var client)) return;
            try
            {
                var manifest = MessageSerializer.DeserializeParityManifest(msg.Payload);
                var diffs = ParityComparer.Format(
                    ParityComparer.Compare(ParityManifestCollector.Collect(), manifest));
                if (string.Equals(diffs, client.ParityDiffs, StringComparison.Ordinal)) return;
                client.ParityDiffs = diffs;
                Debug.Log($"[Multiplayer] parity update from {msg.SenderSteamId}: " +
                          (diffs.Length == 0 ? "parity OK — READY unlocked." : $"still mismatched:\n{diffs}"));
                BroadcastPeerList();
            }
            catch (Exception e) { Debug.LogError("[Multiplayer] parity update failed: " + e.Message); }
        }

        public void HandleConnectionRejected(NetworkMessage msg)
        {
            var reason = NetworkMessage.ParseStringPayload(msg.Payload);
            Debug.LogError($"[Multiplayer] Connection rejected: {reason}");
            // Never-silent: surface the rejection to the joining player (was log-only, so the user saw
            // nothing). Routes through the existing connection-failure path (dialog + lobby teardown /
            // return-to-menu), covering ALL rejection reasons — empty GUID, identity collision,
            // mid-session battle, and the FIX-4 parity mismatch (whose reason carries the exact diffs).
            _engine.ReportConnectionRejected(reason);
        }

        // ─── Heartbeat ────────────────────────────────────────────────────

        /// <summary>
        /// FIX-1: refresh a peer's liveness on ANY inbound packet (not just Heartbeat), called from the
        /// single receive chokepoint (NetworkEngine.OnPacketReceived). Closes the gap where a healthy
        /// peer streaming non-heartbeat traffic (RosterProgress during a save-pick, SaveChunks during a
        /// download) was still timed out because only Heartbeat packets refreshed the clock. Refreshes
        /// ONLY peers we already track (host: clients seeded in AddClient; client: the host seeded in
        /// SetHostPeer) — it never CREATES an entry for an untracked/pre-registration sender, so a
        /// rejected JOIN can't leave a phantom entry that later "times out" into a spurious leave notice.
        /// </summary>
        public void RefreshLiveness(ulong peerId)
        {
            if (_lastHeartbeat.ContainsKey(peerId))
                _lastHeartbeat[peerId] = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }

        /// <summary>
        /// Host: when we last heard ANYTHING from the peer sitting in <paramref name="slot"/> (ms clock),
        /// i.e. the same row <see cref="RefreshLiveness"/> bumps on every inbound packet. The reveal barrier
        /// reads this to tell "still there, just slow" from "the process died" (SaveTransferMath.HoldsBarrier)
        /// — deliberately the RAW clock, not the reaper's verdict: a PAUSED peer keeps its roster row (L84) and
        /// would otherwise hold the barrier for ever. Slot 0 is the host itself, always live.
        ///
        /// NEVER 0. Both miss paths (no <c>_lastHeartbeat</c> row for a client we do track, and no
        /// <c>_clients</c> row for a slot still on the roster — a PAUSED or re-registering peer, whose
        /// roster row L84 keeps) used to return 0, which the caller reads as an epoch timestamp:
        /// <c>now - 0 ≈ 1.78e12 ms</c>, past every grace, so <see cref="SaveTransferMath.HoldsBarrier"/>
        /// declares that peer DEAD on the very first sample and the reveal barrier abandons a peer nobody
        /// ever heard from. ABSENCE OF DATA IS "NOT STARTED", NOT DEATH — only a clock that was once fresh
        /// and has since aged past the grace may mean dead, so seed the miss with <c>now</c> and let the
        /// grace be measured from here. (<c>SlotAdvancedMs</c> already does exactly this, by lazy-seeding.)
        /// </summary>
        public long LastSeenMsForSlot(byte slot)
        {
            var now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            if (slot == 0) return now;
            foreach (var c in _clients.Values)
                if (c.SlotIndex == slot)
                    return _lastHeartbeat.TryGetValue(c.SteamId, out var last) ? last : now;
            return now;
        }

        /// <summary>
        /// "Live" = we heard ANY packet from the peer within the last two heartbeat intervals. A healthy
        /// peer's row is refreshed on every packet (<see cref="RefreshLiveness"/>) and at worst every
        /// <see cref="HeartbeatIntervalMs"/>, so 2× interval is a generous bar a crashed / half-open peer
        /// only AGES past. Deliberately NOT <see cref="HeartbeatTimeoutMs"/> — that is the reaper's kick
        /// bar; using it for the identity-takeover check would refuse a crash-rejoin for the whole 20 s
        /// window (the exact case the stale-rejoin prune exists to allow).
        /// </summary>
        private bool IsPeerLive(ulong peerId)
        {
            if (!_lastHeartbeat.TryGetValue(peerId, out var last)) return false;
            var now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            return now - last <= 2 * HeartbeatIntervalMs;
        }

        public void HandleHeartbeat(NetworkMessage msg)
        {
            var now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

            if (_engine.IsHost)
            {
                // Only refresh (and ack) peers actually ON the roster — same rule as RefreshLiveness:
                // never CREATE a liveness row for an untracked sender. A kicked/rejected peer whose
                // socket is still up would otherwise re-create _lastHeartbeat rows forever, and the ack
                // would keep its half-dead session believing it is connected.
                if (!_clients.ContainsKey(msg.SenderSteamId)) return;
                _lastHeartbeat[msg.SenderSteamId] = now;
                ResumePeer(msg.SenderSteamId); // the resume edge — a paused peer is back (no-op otherwise)
                // Respond with ack
                var ack = new NetworkMessage(PacketType.HeartbeatAck,
                    BitConverter.GetBytes(now));
                _engine.SendToClient(msg.SenderSteamId, ack);
            }
            else
            {
                // Client received a host Heartbeat OR HeartbeatAck — both refresh inbound liveness.
                if (HostPeerId.HasValue)
                    _lastHeartbeat[HostPeerId.Value] = now;
                // FIX-2: a HeartbeatAck specifically proves our OUTBOUND channel is alive (the host
                // received our heartbeat and replied). Track it separately so a half-open send channel
                // (inbound flowing, outbound dead) is detectable while host packets keep arriving.
                if (msg.Type == PacketType.HeartbeatAck)
                    _lastHeartbeatAck = now;
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
                // A ready vote only counts for a peer actually ON the roster: a kicked/rejected peer's
                // late ClientReady must not enter _readyClients — the all-ready bar below compares
                // _readyClients.Count against _clients.Count, so one ghost vote could open the start
                // gate while a real client is still un-ready.
                if (!_clients.ContainsKey(steamId))
                {
                    Debug.LogWarning($"[Multiplayer] Ignoring READY/UNREADY from {steamId}: not on the roster.");
                    return;
                }
                // Parity soft-gate (host-authoritative — never trust the client's locked button): a
                // client whose roster row carries parity diffs may NOT ready up. Single chokepoint:
                // HandleReadyState (the ClientReady packet) routes through here too.
                if (ready && _clients.TryGetValue(steamId, out var pc)
                          && !ParityComparer.ReadyAllowed(pc.ParityDiffs))
                {
                    Debug.LogWarning($"[Multiplayer] Ignoring READY from {steamId}: parity mismatch " +
                                     $"(soft-gate):\n{pc.ParityDiffs}");
                    return;
                }
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

                // NO READY QUORUM (N=50 mandate). This used to compare _readyClients.Count against
                // _clients.Count and only then announce — a peer count decision, i.e. exactly the shape
                // that lets one slow/AFK/parity-blocked player hold fifty shut. Nothing downstream needs
                // the announcement any more: the start gate is LobbyController.CanStart (>=1 peer + save
                // chosen) and the roster's per-row Ready flags ride the PEER_LIST broadcast above.
                // PacketType.AllClientsReady stays a tombstone for wire compatibility with older peers.
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
                SlotIndex = 0,
                ParityDiffs = "" // the host is the parity reference — always OK
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
                    SlotIndex = c.SlotIndex,
                    ParityDiffs = c.ParityDiffs ?? ""
                });
            }
            return peers;
        }

        // COALESCED (N=50 mandate). The roster is O(N) entries and every join, ready toggle, rename,
        // pause and resume asked for a fresh broadcast — O(N) bytes to O(N) peers per change, i.e. O(N^2)
        // for something that changes constantly while fifty people are filing into a lobby. Marking dirty
        // and flushing ONCE per frame collapses a burst of changes into one roster, which is all any peer
        // could render anyway. Correctness is unaffected: the roster is an absolute snapshot, so the last
        // one in a frame is the only one that carries information.
        private bool _peerListDirty;

        public void BroadcastPeerList()
        {
            if (!_engine.IsHost) return;
            _peerListDirty = true;
        }

        /// <summary>Ship the roster if anything asked for it this frame. Driven from <see cref="Update"/>.</summary>
        private void FlushPeerList()
        {
            if (!_peerListDirty || !_engine.IsHost) return;
            _peerListDirty = false;
            var roster = BuildPeerList();
            var payload = MessageSerializer.SerializePeerList(roster);
            _engine.BroadcastToAll(new NetworkMessage(PacketType.PlayerListUpdate, payload));

            // NEVER-SILENT FAN-OUT. PEER_LIST is the one message a joiner cannot do without — it is what
            // ends its "Connecting…" box — and it travels by BROADCAST only. A peer the session has a
            // roster row for but the TRANSPORT will not broadcast to therefore waits forever, and until
            // this line existed that happened without a single log entry on either side: the host saw a
            // normal join, the joiner saw nothing at all. Coalesced to one flush per frame (see
            // _peerListDirty), and only ever on a roster CHANGE, so this is not per-packet spam.
            var transport = _engine.Transport;
            var recipients = new List<string>();
            var unreachable = new List<string>();
            foreach (var id in _clients.Keys)
            {
                recipients.Add(id.ToString());
                if (transport == null || !transport.CanReach(id)) unreachable.Add(id.ToString());
            }
            Debug.Log($"[Multiplayer] PEER_LIST fan-out: {roster.Count} row(s) → {recipients.Count} peer(s) " +
                      $"[{string.Join(",", recipients.ToArray())}]");
            if (unreachable.Count > 0)
                Debug.LogError($"[Multiplayer] PEER_LIST fan-out MISSES {unreachable.Count} roster peer(s) " +
                               $"[{string.Join(",", unreachable.ToArray())}] — the " +
                               $"transport will not broadcast to them, so they never get the roster and stay stuck " +
                               $"on \"Connecting…\" until their heartbeat gives up. Unicast to them still works, " +
                               $"which is why nothing else looks wrong.");
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
            // (client status-bar drift). Go through RemoveClient rather than re-implementing its cleanup:
            // this was the one detach path that bypassed the chokepoint, so anything RemoveClient starts
            // releasing was silently skipped here. Client-only code (host returns at the top of this
            // method), so RemoveClient's host-gated peer-list re-broadcast cannot recurse.
            foreach (var stale in PeerListPrune.PruneKeys(_clients.Keys, newClientKeys))
                RemoveClient(stale);
        }

        // LEAVE (C→H / H→all): graceful lobby/session leave.
        public void HandleLeave(NetworkMessage msg)
        {
            // SECURITY: a client may only leave ITSELF. On the host key the leave off the
            // transport-authenticated sender (msg.SenderSteamId), NOT the self-asserted payload id —
            // every peer's SteamId is public via PEER_LIST, so trusting the payload would let any peer
            // force-disconnect any victim (mirror HandleRename/HandleChat keying). On a client the host
            // has already stamped the authoritative leaver id into the payload it re-broadcast.
            var peerSteamId = _engine.IsHost
                ? msg.SenderSteamId
                : MessageSerializer.DeserializeLeave(msg.Payload);
            if (_engine.IsHost)
            {
                // Re-broadcast leave so all peers drop the client (RemoveClient refreshes the roster).
                _engine.BroadcastToAll(new NetworkMessage(PacketType.ClientLeave,
                    MessageSerializer.SerializeLeave(peerSteamId)));

                // ANNOUNCED BY THE LEAVER, not guessed by an observer: we are here because THAT peer sent
                // its own ClientLeave, which is the one and only thing that makes "left" a fact rather
                // than an inference. Every other way a peer can go silent lands in PausePeer above and is
                // reported as a lost connection. Name resolved BEFORE RemoveClient purges the row.
                var leaverNick = _clients.TryGetValue(peerSteamId, out var lc) ? lc.PlayerName : null;
                _lastKnownNames.TryGetValue(peerSteamId, out var lastKnown);
                AnnounceDeparture(peerSteamId, "leave",
                    SessionLifecycle.FormatLeaveNotice(
                        SessionLifecycle.LeaverName(leaverNick, lastKnown)));
            }
            // A VOLUNTARY leave is the one thing that frees a seat: give the slot back to the pool so a
            // long session cannot exhaust the byte-wide slot space (SlotAllocator.Assign). A peer that
            // merely lost its connection is PAUSED and keeps its slot — it is coming back.
            if (_engine.IsHost && _slots != null && _clients.TryGetValue(peerSteamId, out var leaver)
                && leaver.PlayerGuid != Guid.Empty)
                _slots.Release(leaver.PlayerGuid);
            RemoveClient(peerSteamId);
            // Host: same transport-registry leak as the heartbeat timeout — without this the leaver
            // stays in the transport's peer set and Broadcast writes to the dead id forever. The kick's
            // OnPeerDisconnected raise reports wasKnown=false (roster already purged above) → no
            // duplicate drop notice.
            if (_engine.IsHost)
                try { _engine.Transport?.DisconnectPeer(peerSteamId); } catch { }
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
            // Persist the local player's chosen nick so it survives a restart (loaded back in the
            // HostNickname default / client JOIN / rename-prompt current above). Typed value wins.
            ClientIdentity.LocalNickname = newNickname;
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

        /// <summary>
        /// Host-only: a system line that is also a session EVENT — somebody left, lost connection, or came
        /// back. It travels EXACTLY like a system chat line (same packet, same ordering, same host-
        /// authoritative fan-out, replayed to late joiners like any other backlog line) and additionally
        /// surfaces on every peer's SCREEN, because the players who need it are in the geoscape or in a
        /// battle with no chat panel open.
        ///
        /// NO NEW SURFACE and NO NEW WIDGET: the carrier is the existing chat packet with kind byte 2, and
        /// the screen is <see cref="SessionNotifier.ShowToast"/>, which is already the mod's one native
        /// notice path — the game's own <c>NotificationController</c> where a toast surface is live
        /// (geoscape, main menu) and the game's own message prompt in tactical, where none is.
        ///
        /// READS NOTHING AND REMOVES NOBODY. A notice is an announcement about ONE named peer; it never
        /// counts the roster and never touches a seat (L84, and L91's "no host decision reads other peers'
        /// membership" — a notice is not a decision, and it must not become one).
        /// </summary>
        public void SystemNotice(string text)
        {
            if (!_engine.IsHost || string.IsNullOrEmpty(text)) return;
            BroadcastChat(0, null, text, true, isNotice: true);
        }

        /// <summary>
        /// THE ONE PLACE A DEPARTURE REACHES A SCREEN. Both departure facts funnel through here — the
        /// transport's silence (<see cref="PausePeer"/>) and the peer's own farewell
        /// (<see cref="HandleLeave"/>) — because they are two reports of ONE event and no transport
        /// orders them: the ClientLeave frame and the socket FIN race, and on the losing interleaving
        /// the pause announces first (the roster row survives a pause by design, L84) and the farewell
        /// then announces the same absence a second time. Latched per peer for the duration of that
        /// absence, so whichever fact lands first is the single notice every remaining player sees.
        ///
        /// The <paramref name="producer"/> tag is only for the log line: departures used to leave NO
        /// trace at all (HandleLeave logged nothing), so a doubled prompt was unresolvable after the
        /// fact. Now every announcement AND every suppression names its own source.
        /// </summary>
        private void AnnounceDeparture(ulong peerId, string producer, string text)
        {
            if (!_departureAnnounced.TryAnnounce(peerId))
            {
                Debug.Log("[Multiplayer] departure notice SUPPRESSED (peer " + peerId + " already announced " +
                          "this absence) from " + producer + ": " + text);
                return;
            }
            Debug.Log("[Multiplayer] departure notice from " + producer + " for peer " + peerId + ": " + text);
            SystemNotice(text);
        }

        // Host-authoritative fan-out: raise locally + push to clients (mirrors BroadcastPeerList).
        private void BroadcastChat(ulong senderId, string senderNick, string text, bool isSystem,
                                   bool isNotice = false)
        {
            var chat = new ChatMessageData
            {
                SenderSteamId = senderId,
                SenderNick = senderNick ?? "",
                Text = text,
                IsSystem = isSystem,
                IsNotice = isNotice,
                // Stamped HERE, the one place a line becomes a line, and stamped BEFORE the backlog append
                // below so the stored copy and the wire copy carry the SAME number. That identity is what
                // lets a receiver drop the second delivery of one line (see _seenChatSeq).
                Seq = ++_chatSeq
            };
            // Record into the host-authoritative backlog (arrival order) BEFORE fan-out so late
            // joiners can be replayed the full session history. Bounded ring: drop the oldest line
            // once the cap is hit (matches the panel ChatLog's bounded behaviour).
            _chatHistory.Add(chat);
            if (_chatHistory.Count > ChatHistoryCap)
                _chatHistory.RemoveAt(0);

            OnChatReceived?.Invoke(chat.SenderNick, chat.Text, chat.IsSystem); // local echo on host
            // The HOST's own screen. It receives none of its own packets, so without this the one player
            // who always knows first is the one player who never sees it (the same missing half F1 had).
            if (isNotice) SessionNotifier.ShowToast(text, modalFallback: true);
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
                // A BACKLOG IS NOT NEWS. Replayed with IsNotice cleared, so a peer joining an hour-old
                // session gets the log it missed and not a burst of "X lost connection" prompts for
                // events that resolved before it arrived. The stored line keeps its flag; only the copy
                // on the wire is demoted.
                var replay = new ChatMessageData
                {
                    SenderSteamId = chat.SenderSteamId,
                    SenderNick = chat.SenderNick,
                    Text = chat.Text,
                    IsSystem = chat.IsSystem,
                    IsNotice = false,
                    // THE ONE FIELD THE REPLAY MUST NOT DEMOTE. IsNotice is deliberately cleared above (a
                    // backlog is not news); the identity is the opposite — it is what tells a joiner that
                    // already got this exact line live, in the window between its first packet and this
                    // replay, that it is holding the same line twice.
                    Seq = chat.Seq
                };
                _engine.SendToClient(clientId, new NetworkMessage(PacketType.ChatMessage,
                    MessageSerializer.SerializeChat(replay)));
            }
        }

        public void HandleChat(NetworkMessage msg)
        {
            var chat = MessageSerializer.DeserializeChat(msg.Payload);

            if (_engine.IsHost)
            {
                // Validate + stamp the authoritative sender (mirror HandleRename keying).
                if (chat.IsSystem) return; // clients may not inject system lines
                // Membership gate: only a peer actually ON the roster may inject chat — a rejected/
                // unjoined sender's socket can still be up, and the old "Player" fallback happily
                // broadcast its text to the whole lobby.
                if (!_clients.TryGetValue(msg.SenderSteamId, out var c)) return;
                BroadcastChat(msg.SenderSteamId, c.PlayerName, chat.Text, false);
            }
            else
            {
                // EXACTLY ONCE. The host stamps every line it fans out and the backlog replay carries the
                // same stamp, so a line delivered twice — live inside the join window AND in the replay,
                // or by any retransmit/repeated JOIN — is recognisable and the second copy stops here,
                // before it can reach a screen. Never-silent: say which line was dropped, because a
                // duplicate that vanishes without a trace is how this one survived a whole session.
                if (chat.Seq != 0 && !_seenChatSeq.Add(chat.Seq))
                {
                    Debug.Log("[Multiplayer] chat DUPLICATE dropped (seq " + chat.Seq + ", from " +
                              (string.IsNullOrEmpty(chat.SenderNick) ? "system" : chat.SenderNick) +
                              ") — already rendered; this is the join-window live+replay overlap.");
                    return;
                }
                // Client: render exactly what the host broadcast.
                OnChatReceived?.Invoke(chat.SenderNick, chat.Text, chat.IsSystem);
                // …and, for a session EVENT, put it on screen too — this peer is in the geoscape or in a
                // battle and will never look at the chat log. Reactive on ARRIVAL, never on the next open.
                if (chat.IsNotice) SessionNotifier.ShowToast(chat.Text, modalFallback: true);
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
        // Host-local: this peer stopped answering and is being held, not removed (SessionManager.PausePeer).
        // Deliberately NOT on the wire — the PEER_LIST format is unchanged; peers learn it from the
        // system-chat notice PausePeer/ResumePeer post.
        public bool IsPaused { get; set; }
        // ADVISORY ONLY: "this player says they have finished moving this round". Gates nothing, ever
        // (RailCheck L119) — it is read by one label and cleared on every new player round. Deliberately
        // NOT on the PEER_LIST wire: it rides the tactical band's own tally (SurfaceIds.TacTurn op 4).
        public bool TacReady { get; set; }
        public string ParityDiffs { get; set; } = ""; // FIX-4 soft-gate: exact diff text ("" = parity OK)
        public int LatencyMs { get; set; }
        public byte SlotIndex { get; set; }           // host-assigned stable slot (echoed in PEER_LIST)
        public DateTime ConnectedAt { get; set; }
    }
}
