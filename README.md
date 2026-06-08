# Phoenix Point Cooperative Multiplayer Mod — Research

- Research + design docs for a **cooperative campaign** multiplayer mod for Phoenix Point.
- Built on the official **SDK** + **Harmony** patches.
- **Not** a traditional turn-based PvP/wait-for-each-other mode.
- One shared campaign; multiple players co-control a single faction by ownership + permissions.

## Document Index

### Research & Architecture

| # | Doc | Scope |
|---|-----|-------|
| 00 | [docs/00-project-goal.md](docs/00-project-goal.md) | Goal, co-op concept, ownership + permission model |
| 01 | [docs/01-architecture-authoritative-host.md](docs/01-architecture-authoritative-host.md) | Authoritative host model, client role, anti-desync stance |
| 02 | [docs/02-tactical-combat.md](docs/02-tactical-combat.md) | Tactical phase, what to sync vs not, validation |
| 03 | [docs/03-campaign-layer.md](docs/03-campaign-layer.md) | Shared campaign, dynamic permission system |
| 04 | [docs/04-networking.md](docs/04-networking.md) | Direct IP vs Steam relay, recommendation |
| 05 | [docs/05-investigation-tactical.md](docs/05-investigation-tactical.md) | Source dive: tactical entry points, turns, IDs, mission init |
| 06 | [docs/06-investigation-campaign.md](docs/06-investigation-campaign.md) | Source dive: campaign system entry points |
| 07 | [docs/07-randomness.md](docs/07-randomness.md) | RNG sources, host-only randomness feasibility |
| 08 | [docs/08-serialization.md](docs/08-serialization.md) | What is serializable, reuse of save/load |
| 09 | [docs/09-sync-strategy.md](docs/09-sync-strategy.md) | Action → validate → execute → broadcast pipeline |
| 10 | [docs/10-deliverables.md](docs/10-deliverables.md) | Expected outputs, risk, impl order, PoC roadmap |

### Feature Design: Connection / Lobby / Session Start

| # | Doc | Scope |
|---|-----|-------|
| 11 | [docs/11-connection-menu-ui.md](docs/11-connection-menu-ui.md) | "Network Game" menu, join screen, single-box autodetect input |
| 12 | [docs/12-lobby.md](docs/12-lobby.md) | Lobby-first model, peer identity, live nicknames |
| 13 | [docs/13-session-start-sync.md](docs/13-session-start-sync.md) | Save-picker timing, gzip transfer, barrier sync, per-player progress |
| 14 | [docs/14-transport-protocol.md](docs/14-transport-protocol.md) | ITransport, message envelope, types, reliability |
| 15 | [docs/15-mp-state-vs-save.md](docs/15-mp-state-vs-save.md) | Vanilla-save invariant, ownership model, peerID binding |
| 16 | [docs/16-open-questions-sdk.md](docs/16-open-questions-sdk.md) | Items needing PP source/SDK (unverifiable now) |

### Feature Design: Tactical Concurrency

| # | Doc | Scope |
|---|-----|-------|
| 17 | [docs/17-tactical-concurrency.md](docs/17-tactical-concurrency.md) | Simultaneous play, same-tile conflict, host receipt-order, destination reservation, turn-end ready-gate |

### Feature Design: Geoscape & Player Management

| # | Doc | Scope |
|---|-----|-------|
| 19 | [docs/19-geoscape-concurrency.md](docs/19-geoscape-concurrency.md) | Two state layers, shared clock, events, forced state transitions (yank-to-briefing) |
| 20 | [docs/20-player-management-persistence.md](docs/20-player-management-persistence.md) | Permission UI, transport-independent `playerGUID`, cross-session persistence + reconciliation |
| 21 | [docs/21-disconnect-reconnect.md](docs/21-disconnect-reconnect.md) | Orphan takeover, reconnect resync via start-barrier, mid-battle save caveat, host-loss, toasts |

## Quick Reference

- **Source of truth:** host only. Clients send actions, receive validated results.
- **Sync:** world-changing actions only (move/shoot/reload/ability/inventory/world-interact).
- **Never sync:** camera, selection, cursor, UI navigation.
- **Transport:** pluggable `ITransport` core. **DirectIP first** (loopback = solo dev/test on one PC), Steam P2P later. ZeroTier = just a direct IP, no code.
- **Connection input:** one box, autodetect IP vs Steam code; + Steam friends invite.
- **Lobby-first:** create → lobby → all ready → host picks save → gzip transfer → **barrier sync** (all `LOADED` → `BEGIN`) → play. On-disk save = single source of truth at start.
- **Identity:** stable `peerID` + mutable nickname; ownership binds to `peerID`, never nickname.
- **Vanilla save untouched:** ownership/nicks/permissions = mod runtime-state, re-assigned each session.
- **Networking pref:** Steam relay (NAT traversal + invites) over direct IP — confirm in [04](docs/04-networking.md).
- **Top desync risk:** RNG + hidden game systems — see [07](docs/07-randomness.md).
- **Blocked on SDK:** UI injection, Steam availability, save format, loading hook, 2nd-instance — see [16](docs/16-open-questions-sdk.md).
