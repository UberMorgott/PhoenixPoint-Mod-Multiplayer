# Multiplayer Networking Core (as-built)

> Describes the implemented networking core: the `NetworkEngine` singleton, message routing, packet format, and `SessionManager`. Design rationale → [specs/01-design](../specs/01-design.md); transport layer → [02-transport-layer](02-transport-layer.md).

## 1. NetworkEngine — Singleton & Lifecycle

`NetworkEngine` implements the classic singleton via a static `Create()` method:

```csharp
public static void Create()
{
    if (Instance == null)
        Instance = new NetworkEngine();
}
```

**Lifecycle:**

| Phase | Method | What happens |
|-------|--------|--------------|
| Initialize | `Initialize(TransportType)` | Creates the transport (SteamP2P/DirectIP/StunUDP), instantiates `SessionManager`, resolves the local SteamId, subscribes to transport event handlers |
| Start host | `StartHost(port)` | Sets `IsHost = true`, puts the transport into listen-for-connections mode, initializes `SessionManager` as host |
| Client connect | `JoinGame(address, port)` | `IsHost = false`, the transport initiates an outbound connection |
| Tick | `Update()` | Called every frame — forwards to `Transport.Update()` and `Session.Update()` |
| Shutdown | `Shutdown()` | Stops the transport, nulls references, resets flags |

`NetworkEngine` is **not** a `MonoBehaviour` — it lives as a root singleton in the mod code and is hooked into the game loop through a manual `Update()` call from Unity's main thread.

---

## 2. Message Routing System

A packet flows from the transport to the business logic through exactly three layers:

```
Transport (Steam/Direct/STUN)
    │  OnPacketReceived(senderId, rawBytes)
    ▼
NetworkEngine.OnPacketReceived()
    │  Deserialize(rawBytes) → NetworkMessage
    │  msg.SenderSteamId = senderId
    ▼
NetworkEngine.RouteMessage()
    │  switch(msg.Type) → dispatch
    ▼
SessionManager / Engine events
```

### Dispatch Details

`RouteMessage` is a single `switch` over `PacketType`. Each case either calls a `SessionManager` method or invokes the corresponding C# event on `NetworkEngine`:

- **Session packets** (ConnectionRequest, Heartbeat, ClientReady, PermissionUpdate, StateSync, InitialGameState) → `SessionManager.Handle*()`.
- **Game actions** (TacticalActionRequest, CampaignActionRequest) → events `OnTacticalActionRequest`, `OnCampaignActionRequest`. External code (GameManager) subscribes to these events, validates the action, and calls `Approve*` / `Reject*`.
- **Host results** (TacticalActionApproved, TacticalActionRejected, CampaignActionApproved) → events `OnHostTacticalActionResult` / `OnHostCampaignActionResult` for the client that sent the request.
- **End-of-turn flow** (EndTurnRequest, EndTurnAccepted) is reserved for the turn-synchronization mechanism (see [research/07-tactical-concurrency](../research/07-tactical-concurrency.md)).

Handling is split strictly by `IsHost`: the host performs validation and broadcast; clients only send requests and apply results.

---

## 3. PacketType — Message Classification

`PacketType` is an `enum : byte`, an 8-bit identifier. Ranges:

| Section | Codes | Purpose |
|---------|-------|---------|
| `0x01–0x07` | Connection | Handshake, disconnect, heartbeat |
| `0x10–0x17` | Session | State sync, ready-state, pause |
| `0x20–0x27` | Tactical | Tactical actions, end of turn |
| `0x30–0x34` | Campaign | Campaign actions and state broadcast |
| `0x40–0x42` | Management | Permissions, assignments, player list |
| `0x50` | Chat | Chat messages |
| `0xF0` | Internal | STUN hole-punch and internal transport needs |

---

## 4. Network Message Format

### NetworkMessage Header (37+ bytes)

Every packet has a fixed binary header before the payload:

```
Offset  Size  Field        Type
──────  ────  ───────────  ──────────────
0       1     Type         byte (PacketType)
1       8     SenderId     ulong (sender SteamId)
9       16    MessageId    Guid (16 bytes)
25      8     Timestamp    long (Ticks)
33      4     PayloadLen   int (Payload length)
37+     N     Payload      byte[]
```

`MessageId` (GUID) is generated when `NetworkMessage()` is constructed — enables deduplication and acknowledgement tracking.

### Binary Message Formats (MessageSerializer)

#### TacticalActionMessage

```
Field         Type       Size
──────        ────       ──────
ActionId      Guid       16
ActionType    byte       1
ActorGeoId    int        4
AbilityDefId  string*    (7-bit len prefix + UTF-8)
TargetData    int + N    4 + N (raw bytes)
Timestamp     long       8
```

The `AbilityDefId` string is serialized with `BinaryWriter.Write(String)` — a length prefix as a compressed int.

#### CampaignActionMessage

```
Field         Type       Size
──────        ────       ──────
ActionId      Guid       16
ActionType    byte       1
TargetId      string*    (7-bit len prefix + UTF-8)
Payload       int + N    4 + N
Timestamp     long       8
```

#### PermissionUpdate

```
Field         Type       Size
──────        ────       ──────
TargetSteamId ulong      8
Permissions   int        4
```

#### GameState

```
Field         Type       Size
──────        ────       ──────
LevelName     string*    (7-bit len prefix + UTF-8)
StateData     int + N    4 + N
```

### Composite Payloads for Approved/Rejected

The methods `ApproveTacticalAction` and `RejectTacticalAction` build a composite payload:

```
[ActionPayload][4-byte-ResultLen][ResultData]
or
[ActionPayload][4-byte-ReasonLen][ReasonUTF8]
```

where `ActionPayload` is the result of `MessageSerializer.SerializeTacticalAction(action)`.

---

## 5. SessionManager

### Client Tracking

`_clients: Dictionary<ulong, ClientInfo>` — the host stores every connected peer. `ClientInfo` holds SteamId, endpoint, name, permissions, latency, and connect time.

### Heartbeat / Timeout

- Send interval: **5000 ms**.
- Timeout: **20000 ms** with no response.
- The host broadcasts `Heartbeat` to all — each client replies with `HeartbeatAck`.
- A client treats the moment it receives a `Heartbeat` as the host's time.
- On timeout the host calls `RemoveClient`, which triggers `OnClientDisconnected`.

### Ready-State Coordination

The synchronous round-start mechanism (the session-start barrier, see [specs/02-session-lifecycle-and-player-management](../specs/02-session-lifecycle-and-player-management.md) §2):

1. **Client** → sends `ClientReady`.
2. **Host** → `SetClientReady(id)` adds it to `_readyClients`, invokes `OnClientReady`.
3. When `_readyClients.Count >= _clients.Count && _clients.Count > 0`:
   - The host broadcasts `AllClientsReady` to all.
   - `OnAllClientsReady` is invoked.
4. Clients receive `AllClientsReady` → start the game locally.

Resetting the ready-state for the next round must be implemented by external code.

---

## 6. Reliable Message Flow (Client → Host → Broadcast)

Using a tactical action as an example:

```
Client                          Host
  │                               │
  │  1. SendTacticalAction()      │
  │  ──TacticalActionRequest──►   │
  │                               │  2. OnTacticalActionRequest
  │                               │     (validated by GameManager)
  │                               │  3. ApproveTacticalAction()
  │  ◄──TacticalActionApproved──  │     (with ResultData)
  │                               │  4. BroadcastExcept(sender,
  │  ◄──TacticalActionBroadcast── │     TacticalActionBroadcast)
  │     (action to all except     │
  │      the sender)              │
  │                               │
  │  5. OnHostTacticalActionResult │
  │     → client applies the      │
  │     result locally            │
```

**Key guarantees:**

- The action is **not executed** on the client until `TacticalActionApproved` is received.
- The validation result is computed on the **host** — the single authoritative source of truth.
- `TacticalActionBroadcast` notifies the other clients that the action was approved — they can update their predictive state.

**For campaign actions** — the same pattern, but without the composite result block inside `ApproveCampaignAction` (only `CampaignActionApproved` is sent).

This matches the action → validate → execute → broadcast pipeline in [specs/01-design](../specs/01-design.md) §3.

---

## 7. Game-Loop Integration

`NetworkEngine.Update()` is called **every frame** from Unity's main thread:

```csharp
public void Update()
{
    Transport?.Update();    // Poll the transport (Steam callbacks, buffers)
    Session?.Update();      // Heartbeat logic, timeouts
}
```

`ITransport.Update()` is the transport's entry point: it processes incoming packets from the Steam/Direct/STUN queues and calls `OnPacketReceived` for each. Because the call happens on the main thread, all callbacks (the `NetworkEngine` events) are guaranteed to run without a race condition against the Unity API.

`SessionManager.Update()` runs the heartbeat timers and checks timeouts on the host side.

**Expected hook point** — `MonoBehaviour.Update()` or `LateUpdate()` on the mod's root object.
