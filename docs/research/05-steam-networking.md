# Steam Integration & Networking Analysis

Source: `decompiled\AssemblyCSharp\Assembly-CSharp\src`

## Game Online Infrastructure

Phoenix Point ships with **two complete online SDKs** compiled in, but uses **neither for multiplayer**:

| SDK | Compiled? | Initialized? | Used For |
|-----|-----------|--------------|----------|
| Facepunch.Steamworks | Full C# library | Yes (at startup) | Workshop mods, overlay, DLC, language |
| Epic Online Services (EOS) | Full C# bindings | No (code exists but never called) | Unused |

## Facepunch.Steamworks — Available APIs

The library is **already compiled into `Assembly-CSharp.dll`** and available to any Harmony-patched mod via the `Steamworks` namespace. It is initialized at game startup with `SteamClient.Init(839770u, true)`.

### Currently Used Features
- `SteamClient` — Init/Shutdown/RunCallbacks/IsLoggedOn/SteamId
- `SteamApps` — GameLanguage, IsDlcInstalled
- `SteamFriends` — OpenWebOverlay, OpenStoreOverlay
- `SteamUser` — GetAuthSessionTicketAsync
- `SteamUGC` — Workshop mod loading
- `SteamUtils` — IsOverlayEnabled

### Available But Unused (For Multiplayer)

| API Class | Features |
|-----------|----------|
| **SteamNetworking** | `SendP2PPacket`, `ReadP2PPacket`, `AcceptP2PSession`, `CloseP2PSession`, `GetP2PSessionState` |
| **SteamMatchmaking** | `CreateLobby`, `JoinLobby`, `LeaveLobby`, `SetLobbyData`, `GetLobbyData`, `SendLobbyChatMsg` |
| **SteamFriends** | `GetFriends`, `SetRichPresence`, `ActivateGameOverlay` (invites) |
| **SteamUser** | Steam ID peer identification |

## Epic Online Services — Available But Not Recommended

Full C# bindings for EOS subsystems exist in `Assembly-CSharp.dll` but `PlatformEpic.Init()` is never called (only `PlatformSteam` runs). Access would require:
1. Manually initializing the EOS platform
2. Auth token handling
3. More complex API surface

## Networking Approach Recommendation

> **Summary of the decision:** prefer the **Steam relay** (NAT traversal + no manual port forwarding + invite codes / friend invites) when available; keep **direct IP as the fallback** for non-Steam / LAN scenarios. ZeroTier / VPN counts as "just a direct IP" — no extra code. Loopback `127.0.0.1` enables solo dev/test on one PC (subject to the single-instance caveat → [specs/03-open-questions-sdk](../specs/03-open-questions-sdk.md)). The mod's core is **transport-agnostic** — all three transports sit behind one `ITransport`; see [engine/02-transport-layer](../engine/02-transport-layer.md).

**Option A: Facepunch.Steamworks P2P (RECOMMENDED)**

| Factor | Assessment |
|--------|------------|
| Extra assemblies needed | **Zero** — library already compiled in |
| NAT traversal | **Automatic** — Steam P2P handles it |
| Matchmaking | Via `SteamMatchmaking` lobbies or friend invites |
| Encryption | **Built-in** — Steam P2P encrypts packets |
| Reliability | Configurable (unreliable, reliable, reliable-with-buffering) |
| Dev complexity | Low — simple `SendP2PPacket`/`ReadP2PPacket` API |

**Option B: Raw TCP/UDP Sockets**

| Factor | Assessment |
|--------|------------|
| Extra assemblies | **None** — .NET built-in |
| NAT traversal | **Manual** — requires STUN/TURN/port forwarding |
| Extra complexity | High — reimplement what Steam does for free |

**Option C: EOS P2P**

| Factor | Assessment |
|--------|------------|
| Not recommended | EOS platform not initialized; harder setup than Steam |

## Platform Abstraction

```csharp
// Base.Platforms\PlatformComponent.cs — line 31
return new PlatformSteam();  // hardcoded to Steam
```

The mod can assume Steam is the active platform. Platform abstraction exists at `Base.Platforms` but is irrelevant for the networking layer — the mod will call `SteamNetworking` directly.

## Key Files for Reference

| File | Path in decompiled source |
|------|---------------------------|
| PlatformSteam.cs | `Base.Platforms.Steam\PlatformSteam.cs` |
| PlatformEpic.cs | `Base.Platforms.Epic\PlatformEpic.cs` |
| SteamNetworking (Facepunch) | Compiled into Assembly-CSharp.dll |
| SteamMatchmaking (Facepunch) | Compiled into Assembly-CSharp.dll |

## Implementation Stub

```csharp
// Using Facepunch.Steamworks P2P — no extra DLLs needed

public class MultiplayerNetwork
{
    public void Initialize()
    {
        // SteamClient is already initialized by the game
        // Just start accepting P2P sessions
    }

    public void SendToPeer(ulong steamId, byte[] data, bool reliable = true)
    {
        SteamNetworking.SendP2PPacket(steamId, data, 
            reliable ? P2PSend.Reliable : P2PSend.Unreliable, 0);
    }

    public void Update()
    {
        // Read all incoming packets
        while (SteamNetworking.IsP2PPacketAvailable())
        {
            var packet = SteamNetworking.ReadP2PPacket();
            ProcessPacket(packet.SteamId, packet.Data);
        }
    }
}
```
