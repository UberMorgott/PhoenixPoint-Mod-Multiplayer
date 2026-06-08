# Multipleer Transport Layer (as-built)

> The transport layer is the foundation of Multipleer's networking architecture. It abstracts the mechanics of passing byte messages between peers, letting the layers above (`NetworkEngine`, `SessionManager`) stay independent of the concrete network: Steam P2P, direct TCP/IP, or UDP with STUN traversal.
>
> Three transports implement a single `ITransport` interface and are interchangeable — the choice happens at initialization and is transparent to all logic above it. Design rationale → [specs/01-design](../specs/01-design.md); the message catalog by phase (below) is the as-built realization of the transport-protocol design.

---

## ITransport — Interface

`src/Transport/ITransport.cs`

```csharp
public interface ITransport
{
    TransportType TransportType { get; }
    ConnectionState State { get; }
    bool IsHost { get; }
    string LocalEndpoint { get; }

    event Action<ConnectionState> OnStateChanged;
    event Action<ulong, byte[]> OnPacketReceived;
    event Action<ulong, string> OnPeerConnected;
    event Action<ulong, string> OnPeerDisconnected;

    void Initialize();
    void Shutdown();
    void Host(int port = 0);
    void Connect(string address, int port);
    void Disconnect();
    void Send(ulong peerId, byte[] data, bool reliable = true);
    void Broadcast(byte[] data, bool reliable = true);
    void Update();
}
```

**Key decisions:**

- **`peerId` is a `ulong`** — a uniform identifier across all transports. Steam uses the `SteamId`; Direct and Stun generate local monotonically-increasing IDs. A single type simplifies routing.
- **`State` + the `OnStateChanged` event** — the consumer (`NetworkEngine`) subscribes to state changes and reacts to `Failed`.
- **Separate `Host()` and `Connect()`** — each transport listens on a port (TCP/UDP) or opens a P2P session in its own way.
- **`Update()` — pull model** — transports do not spawn their own threads for event dispatch; they push incoming packets into an internal queue, and `Update()` (called from `MonoBehaviour.Update` every frame) drains it and fires `OnPacketReceived`. This guarantees events arrive on Unity's main thread.
- **`reliable` flag** — only `SteamTransport` relies on Steam's built-in reliability; `DirectTransport` always delivers over TCP (guaranteed); `StunTransport` duplicates the packet for a "pseudo-reliable" mode.

> Transport-agnostic core: the same `ITransport` lets the cross-session identity model carry `playerGUID` in the `JOIN` payload over *any* transport — mixed Steam/DirectIP/STUN groups "just work". See [specs/02-session-lifecycle-and-player-management](../specs/02-session-lifecycle-and-player-management.md) §4.

---

## TransportType

`src/Transport/TransportType.cs`

```csharp
public enum TransportType : byte
{
    SteamP2P = 0,
    DirectIP = 1,
    StunUDP  = 2
}

public enum ConnectionState : byte
{
    Disconnected,
    Connecting,
    Connected,
    Failed
}
```

`TransportType` is the single point where the implementation is selected. `NetworkEngine` creates the transport via the factory method `CreateTransport()` from this enum's value.

---

## SteamTransport — Reflection & P2P over Steam

`src/Transport/SteamTransport.cs`

`SteamTransport` accesses `Facepunch.Steamworks` through **reflection** (no direct assembly reference), to avoid a hard dependency — the mod must still work without Steam (e.g. on pirated copies or while debugging without a Steam client).

### Resolving Types

```csharp
static SteamTransport()
{
    var assembly = typeof(UnityEngine.Application).Assembly
        .GetType("Steamworks.SteamClient")?.Assembly;
    // fallback: Assembly-CSharp
    _steamClientType = assembly?.GetType("Steamworks.SteamClient");
    _steamNetworkingType = assembly?.GetType("Steamworks.SteamNetworking");

    var prop = _steamNetworkingType?.GetProperty("Instance",
        BindingFlags.Public | BindingFlags.Static);
    _steamNetworkingInstance = prop?.GetValue(null, null);
}
```

The search starts from `UnityEngine.Application` (a known assembly), then falls back to `Assembly-CSharp`. If no assembly is found, all calls are silently ignored and the transport goes to `Failed`.

### P2P Session

- **Host** — simply sets a flag; Steam P2P needs no listening socket.
- **Connect** — calls `AcceptP2PSession(targetId)`, adds the peer to `_connectedPeers`, and goes straight to `Connected`.
- **Incoming request** — Steam Networking raises an `OnP2PSessionRequest` event. The `OnSessionRequest` handler calls `AcceptP2PSession` and notifies `OnPeerConnected`.

### NAT & Relay

Steam Networking automatically chooses: **NAT hole punching** (where possible) or **relay through Steam Relay Servers**. The mod controls none of this — the Steam library decides everything, which is the main advantage: zero-config for the user. The cost is a required, running Steam client.

### Receiving Packets

`Update()` polls the static `SteamNetworking.IsP2PPacketAvailable()` / `ReadP2PPacket()` reflectively, queues the results, and dispatches them on the main thread.

---

## DirectTransport — TCP with Length-Prefixed Framing

`src/Transport/DirectTransport.cs`

A direct TCP connection with no intermediary. Used for LAN games, direct IP connections, and testing. Loopback `127.0.0.1` enables full solo dev/testing on one PC (subject to the single-instance caveat → [specs/03-open-questions-sdk](../specs/03-open-questions-sdk.md)); ZeroTier / VPN "just works" as a normal IP, no extra code.

### Thread Architecture

```
[HOST]                          [CLIENT]
TcpListener (listen thread)     TcpClient
    │                                │
    ├─ AcceptTcpClient() ────────────┤
    │   StartReadLoop(peerId, tcp)   │
    │      └─ per-peer read thread   │
    │                                │
```

- **ListenThread** — a background thread that loops calling `AcceptTcpClient()`. A new read thread is created per connection.
- **ReadThread (per-peer)** — reads messages with fixed framing: 4 length bytes (Little-Endian int32), then the payload.
- **Send/Write** — a blocking `NetworkStream.Write` call under a `lock`.

### Framing Protocol

```csharp
// Send
var lenBytes = BitConverter.GetBytes(data.Length);
stream.Write(lenBytes, 0, 4);      // length
stream.Write(data, 0, data.Length); // body

// Receive — strict ordering
var lenBuf = new byte[4];
ReadFully(stream, lenBuf, 4);       // guaranteed read
var msgLen = BitConverter.ToInt32(lenBuf, 0);
var msgBuf = new byte[msgLen];
ReadFully(stream, msgBuf, msgLen);  // exactly msgLen bytes
```

`ReadFully` is implemented by hand as a loop — the standard `Stream.Read` does not guarantee a full buffer in a single call.

### Control

- **Host(port)** — starts a `TcpListener` on `IPAddress.Any:{port}` (default 14242).
- **Connect(address, port)** — creates a `TcpClient`, connects, and immediately starts a read thread.
- **Disconnect() / Shutdown()** — closes all clients, stops the listener, waits for threads to stop (Join up to 1 s).

### Trade-offs

- **+** Full control, no external dependencies.
- **+** Guaranteed, ordered delivery (TCP).
- **−** Requires an open port (firewall/NAT).
- **−** No automatic NAT traversal.

---

## StunTransport — RFC 5389 & UDP Hole Punching

`src/Transport/StunTransport.cs`

A compromise between Steam and Direct: a UDP transport with STUN external-IP discovery and hole punching.

### STUN Binding Request (RFC 5389)

```csharp
private static byte[] CreateStunBindingRequest()
{
    var transId = Guid.NewGuid().ToByteArray();
    var msg = new byte[20];
    msg[0] = 0x00; msg[1] = 0x01;   // Binding Request (0x0001)
    msg[2] = 0x00; msg[3] = 0x00;   // Length (0 — no attributes)
    msg[4] = 0x21; msg[5] = 0x12;   // Magic Cookie (0x2112A442)
    msg[6] = 0xA4; msg[7] = 0x42;
    Array.Copy(transId, 0, msg, 8, 12); // Transaction ID (12 bytes)
    return msg;
}
```

The request contains only the header (20 bytes) — no attributes, since this is the simplest possible discovery.

### Parsing XOR-MAPPED-ADDRESS

```csharp
if (attrType == AttrXorMappedAddress)
{
    port ^= (ushort)(StunMagicCookie >> 16);           // XOR with 0x2112
    for (int i = 0; i < 4; i++)
        addrBytes[i] ^= (byte)((StunMagicCookie >> (24 - i * 8)) & 0xFF);
}
```

Per RFC 5389, `XOR-MAPPED-ADDRESS` masks the IP and port by XOR-ing with the Magic Cookie. The parser supports both `MAPPED-ADDRESS` (RFC 3489, fallback) and `XOR-MAPPED-ADDRESS` (RFC 5389, modern).

### Discovery Flow

1. Start a `UdpClient` on a random port.
2. Send a Binding Request sequentially to five Google STUN servers (`stun{1-4}.l.google.com:19302`).
3. Up to 3 attempts per server with a 100 ms wait.
4. Extract the external endpoint from the first successful response.

### UDP Hole Punching

```csharp
// Client sends HOLE_PUNCH to the target peer
SendRaw(remoteEp, Encoding.UTF8.GetBytes("HOLE_PUNCH"));

// Host receives it, remembers the from-address as the peerId, replies HOLE_PUNCH_ACK
if (dataStr == "HOLE_PUNCH" && IsHost)
{
    var peerId = (ulong)Interlocked.Increment(ref _nextPeerId);
    lock (_lock) { _peers[peerId] = from; }
    OnPeerConnected?.Invoke(peerId, $"STUN({from})");
    SendRaw(from, Encoding.UTF8.GetBytes("HOLE_PUNCH_ACK"));
}
```

The peer address is dynamic — the host does not know in advance which IP/port a client's packet will come from. `HOLE_PUNCH` both punches a hole in the NAT and tells the host the peer's real address. All subsequent traffic goes directly after this handshake.

### Reliability

UDP does not guarantee delivery. The `reliable = true` mode simply sends the packet twice. This is a minimal solution — it could be extended to an ACK protocol in the future.

---

## NetworkEngine — Transport Coordination

`src/Network/NetworkEngine.cs`

`NetworkEngine` acts as the facade and mediator between transport, session, and UI.

```csharp
public void Initialize(TransportType transportType)
{
    Transport = CreateTransport(transportType);   // factory
    Session = new SessionManager(this);

    Transport.OnPacketReceived += OnPacketReceived;
    Transport.OnPeerConnected += OnPeerConnected;
    Transport.OnPeerDisconnected += OnPeerDisconnected;
    Transport.OnStateChanged += OnTransportStateChanged;

    Transport.Initialize();
    IsActive = true;
}
```

- **Data flow:** network → `ITransport._incomingQueue` → `Update()` → `OnPacketReceived` → `RouteMessage()` → handlers (SessionManager, events). Full routing detail → [01-networking-core](01-networking-core.md).
- **Update** is called from `MultipleerMain.Update()` every frame, which also drives `Session.Update()`.
- **IsActive** — readiness flag: if `Initialize()` was not called, all external APIs stay silent.
- **Factory** is just a switch over `TransportType`:

```csharp
private static ITransport CreateTransport(TransportType type) => type switch
{
    TransportType.SteamP2P => new SteamTransport(),
    TransportType.DirectIP => new DirectTransport(),
    TransportType.StunUDP  => new StunTransport(),
    _ => throw new ArgumentException($"Unknown transport: {type}")
};
```

The transport is selected in the UI (`MultiplayerUI` class), where the user switches the type before pressing "Host" or "Join".

---

## Comparison Table

| Characteristic | SteamTransport | DirectTransport | StunTransport |
|---|---|---|---|
| **Protocol** | Steam P2P (UDP) | TCP | UDP + STUN |
| **NAT traversal** | Automatic (Steam Relay) | ❌ No | ✅ STUN + hole punching |
| **Dependencies** | Steamworks.dll / Steam client | None | None (DNS + UDP) |
| **Reliability** | Built-in (Steam) | TCP (guaranteed) | Duplication (best-effort) |
| **Ordering** | Yes (Steam) | Yes (TCP) | No |
| **Open port** | Not needed | Required (default 14242) | Not needed (outbound UDP) |
| **Performance** | Medium (relay) | High (LAN) | Medium (UDP) |
| **Latency** | Low–medium | Low | Low |
| **Auto-discovery** | Via Steam friends list | IP/port manually | External IP via STUN |
| **Best scenario** | Online via Steam (primary) | LAN / debug / tests | Direct P2P without Steam |

> **Latency stance:** the model is **authoritative-host, NOT lockstep**, so it is latency-tolerant. Turn-based tactical / geoscape tolerates 100–200 ms unnoticed; there is no need to chase minimal ping.

---

## Message Protocol — Envelope & Type Catalog

The wire envelope and the binary per-message formats (`NetworkMessage` header, `TacticalActionMessage`, etc.) are documented in [01-networking-core](01-networking-core.md) §4. This section catalogs the **message types by phase** that the protocol carries.

- Envelope framing: a fixed binary header (`[type][sender][messageId][timestamp][payloadLen]`) followed by the payload. Control payloads are small binary structs; the save blob is raw gzip'd bytes, chunked for Steam P2P.
- Reliability: lobby / save / action messages are **reliable + ordered** (TCP gives it free; Steam uses the reliable flag). `PROGRESS` may be **unreliable** (a lost progress frame is harmless).

### Message Types by Phase

- **Lobby:** `JOIN` (carries the persistent `playerGUID` → [specs/02-session-lifecycle-and-player-management](../specs/02-session-lifecycle-and-player-management.md) §4), `RENAME`, `READY`, `PEER_LIST`, `LEAVE`.
- **Start / sync:** `SAVE_CHUNK`, `SAVE_DONE`, `PROGRESS`, `LOADED`, `BEGIN` (the session-start barrier → [specs/02-session-lifecycle-and-player-management](../specs/02-session-lifecycle-and-player-management.md) §2).
- **In-game:** `ACTION`, `ACTION_RESULT`, `ACTION_REJECT`, `ASSIGN_OWNER`, `PERMISSION`.
- **Tactical:** `END_TURN_READY` → [research/07-tactical-concurrency](../research/07-tactical-concurrency.md).
- **Geoscape:** `TIME_STATE`, `EVENT`, `STATE_ENTER` → [research/08-geoscape-concurrency](../research/08-geoscape-concurrency.md).
- **Session / system:** `NOTICE` (toasts: disconnect / takeover / reconnect / host-loss) → [research/09-disconnect-reconnect](../research/09-disconnect-reconnect.md).

> The as-built `PacketType` enum ([01-networking-core](01-networking-core.md) §3) groups these into the `0x01`–`0xF0` ranges; the phase catalog above is the design-level naming that those codes implement.

---

## Summary

The three transports cover different Multipleer deployment scenarios:

1. **SteamTransport** — the primary route for the released game: zero-config for the user, automatic NAT traversal.
2. **DirectTransport** — for LAN, tests, and Steam-less environments. Maximum control and performance at the cost of manual configuration.
3. **StunTransport** — an intermediate option: a direct UDP channel without Steam, but it needs a STUN server and is not guaranteed to traverse symmetric NATs.

The single `ITransport` interface lets you switch between them without changing any game logic — just change the `TransportType` in the UI.
