<div align="center">

# Phoenix Point: Cooperative Multiplayer

**One campaign. Several commanders. Zero official co-op, so we built our own.**

[![English](https://img.shields.io/badge/lang-English-1f6feb?style=for-the-badge)](README.md)
[![Русский](https://img.shields.io/badge/lang-%D0%A0%D1%83%D1%81%D1%81%D0%BA%D0%B8%D0%B9-6e7681?style=for-the-badge)](README.ru.md)

[![Stars](https://img.shields.io/github/stars/UberMorgott/PhoenixPoint-Mod-Multiplayer?style=flat-square)](https://github.com/UberMorgott/PhoenixPoint-Mod-Multiplayer/stargazers)
[![Forks](https://img.shields.io/github/forks/UberMorgott/PhoenixPoint-Mod-Multiplayer?style=flat-square)](https://github.com/UberMorgott/PhoenixPoint-Mod-Multiplayer/network/members)
[![Issues](https://img.shields.io/github/issues/UberMorgott/PhoenixPoint-Mod-Multiplayer?style=flat-square)](https://github.com/UberMorgott/PhoenixPoint-Mod-Multiplayer/issues)
[![Last commit](https://img.shields.io/github/last-commit/UberMorgott/PhoenixPoint-Mod-Multiplayer?style=flat-square)](https://github.com/UberMorgott/PhoenixPoint-Mod-Multiplayer/commits)
[![CI](https://img.shields.io/github/actions/workflow/status/UberMorgott/PhoenixPoint-Mod-Multiplayer/ci.yml?style=flat-square&label=CI)](https://github.com/UberMorgott/PhoenixPoint-Mod-Multiplayer/actions)
[![Tests](https://img.shields.io/badge/tests-1625%20green-2ea043?style=flat-square)](Multiplayer.Tests)
[![License](https://img.shields.io/badge/license-PolyForm--NC--1.0.0-blue?style=flat-square)](LICENSE)

[![Report a bug](https://img.shields.io/badge/%F0%9F%90%9E%20report-a%20bug-d1242f?style=for-the-badge)](https://github.com/UberMorgott/PhoenixPoint-Mod-Multiplayer/issues/new?labels=bug)
[![Request a feature](https://img.shields.io/badge/%E2%9C%A8%20request-a%20feature-8957e5?style=for-the-badge)](https://github.com/UberMorgott/PhoenixPoint-Mod-Multiplayer/issues/new?labels=enhancement)
[![Roadmap](https://img.shields.io/badge/%F0%9F%97%BA%EF%B8%8F-roadmap-1f6feb?style=for-the-badge)](#roadmap)

</div>

## The pitch

Phoenix Point is a brilliant, brutal little turn-based strategy game. It is also stubbornly single player. My friends and I wanted the obvious thing: run one campaign together, take a base each, argue over who gets the next research slot, and lose soldiers as a team instead of alone.

That mode does not exist. Snapshot never shipped it. So we are writing it ourselves, one reverse-engineered Harmony patch at a time.

This is that mod. It turns a solo Phoenix Point campaign into a shared one where two or more people run the same Geoscape and fight the same tactical battles at once. No new servers, no rewritten game logic, just the real game with a lot of careful wiring bolted onto the side.

## How the co-op actually works

There is exactly one source of truth: the **host**. The host runs the real, unmodified game simulation. Everyone else runs a client that is, on purpose, a very convincing puppet.

- You click "start research" on your machine. The client does not run it. It swallows your click, sends the intent to the host, and waits.
- The host runs the action for real, then tells every client what happened.
- Your screen updates to match. So does everyone else's.

That one rule (the host decides, clients display) is what stops four people from quietly drifting into four different campaigns. The whole mod is really just the long, stubborn job of making every screen in the game obey it.

## Status, and the honest disclaimer

This is an in-development mod, not a finished product. A lot of the list below is built and passing its tests but has not yet survived a real two-player soak session. Expect rough edges. Expect to file bugs. That is genuinely useful.

You bring your own legally owned copy of Phoenix Point. The mod is built on Snapshot Games' official modding framework and ships no game code or assets of its own. It is a fan project and is not affiliated with, endorsed by, or supported by Snapshot Games.

<a name="roadmap"></a>
## Roadmap

The feature list is big, so here it is broken down the way the game is: the lobby you connect through, the Geoscape you share, and the tactical battles you fight. Status is honest.

**Legend:** &nbsp; ✅ done and verified &nbsp;·&nbsp; 🧪 built, not yet soak-tested in a live game &nbsp;·&nbsp; ⬜ planned or deliberately parked

<details open>
<summary><b>🛰️ Lobby &amp; Session</b></summary>

**Connecting**
- [x] ✅ Steam peer-to-peer transport
- [x] ✅ Direct TCP/IP transport (host listens, clients dial in)
- [x] ✅ STUN UDP hole-punch transport (finds your public address, punches through NAT)
- [x] ✅ One swappable transport layer, pick your connection type at launch
- [x] ✅ Heartbeats and timeout detection (5s pings, 20s of patience)

**Who is who**
- [x] ✅ Connection handshake (request, accept, reject)
- [x] ✅ Live player list
- [x] ✅ Player rename
- [x] ✅ Permission and ownership seeding on join
- [x] 🧪 Roster progress screen

**Getting everyone in**
- [x] ✅ Ready and un-ready toggles
- [x] ✅ "Everybody ready" start barrier
- [x] ✅ Chunked save transfer to pull every client onto the host's exact campaign (32 KB chunks, CRC checked)
- [x] ✅ Load-progress and "I am loaded" barrier
- [x] 🧪 Mid-session drop-in on the Geoscape (join a game already in progress)
- [x] 🧪 Three-plus player topology
- [ ] ⬜ Joining a battle that is already underway (parked on purpose)

**When someone leaves**
- [x] ✅ Clean host-left and client-left signaling
- [ ] ⬜ Reconnect and self-heal after an unexpected host drop
- [ ] ⬜ Host migration (out of scope for v1)

</details>

<details>
<summary><b>🌍 Geoscape</b></summary>

**Time and the shared clock**
- [x] ✅ One authoritative clock (the host owns time, pause, and speed)
- [x] ✅ Client time-control requests
- [x] ✅ NTP-style clock offset sync so everyone agrees on "now"
- [x] ✅ Client simulation freeze (clients never tick their own clock)

**Research**
- [x] ✅ Start and cancel research
- [x] ✅ Research completion sync
- [x] ✅ Reordering the research queue
- [x] ✅ Research rate and time-remaining mirror
- [x] ✅ Unlock availability (what a finished tech opens up)
- [x] 🧪 "Available research" reconcile after a project finishes

**Manufacturing and crafting**
- [x] ✅ Queue an item for manufacture
- [x] ✅ Manufacture completion sync
- [x] 🧪 Marketplace offers and selection mirror

**Base and facilities**
- [x] ✅ Build a facility
- [x] ✅ Repair a facility
- [x] ✅ Facility completion
- [x] ✅ Demolish or cancel construction
- [x] ✅ Inventory and storage mirror
- [x] 🧪 Facility power-state changes

**Aircraft and vehicles**
- [x] 🧪 Vehicle world position, smoothly interpolated
- [x] 🧪 Travel route line (the yellow path on the map)
- [x] 🧪 Site-exploration progress bar
- [x] 🧪 Move-vehicle and explore-site intents
- [x] 🧪 Mid-game vehicle creation mirror
- [x] 🧪 Aircraft health and repair
- [x] 🧪 Crew and loadout

**Personnel and recruits**
- [x] 🧪 Roster composition mirror (who is stationed where)
- [x] 🧪 Full soldier live-state
- [x] 🧪 Equip, augment, hire, transfer, dismiss, rename (permission and ownership gated)
- [x] 🧪 Recruit pool mirror (available, unarmed, and captured units)

**Events and choices**
- [x] ✅ Event popups raised and dismissed for everyone
- [x] ✅ Choice arbitration when two people answer at once (first click wins)
- [x] ✅ Single-choice prompt advance
- [x] 🧪 Objectives and quest-line state (including DLC and critical path)

**Report modals**
- [x] 🧪 Mission-brief mirror (including blocking ambush briefs)
- [x] 🧪 Mission-outcome report mirror
- [x] 🧪 Interception notices
- [x] 🧪 One ordered display queue so popups do not stampede each other

**The rest of the world**
- [x] ✅ Diplomacy and reputation
- [x] ✅ Resources and wallet (one silent balance writer, no double-counting)
- [x] 🧪 Resource-harvest floating numbers
- [x] 🧪 Mist and fog field mirror
- [x] 🧪 Site identity and world-activity tails (havens, alien bases, excavations, weather, timers)
- [x] 🧪 Behemoth presence and status
- [x] 🧪 Generic Geoscape ability relay (harvest, excavate, repair, scan, probe, activate, guard)

</details>

<details>
<summary><b>⚔️ Tactical</b></summary>

**Getting onto the field**
- [x] 🧪 Full deploy snapshot, with chunking for oversized squads

**Moving**
- [x] 🧪 Move intent to authoritative outcome
- [x] 🧪 Concurrent move animation (everyone moves at once, no waiting your turn)
- [x] 🧪 Position and facing deltas

**Fighting**
- [x] 🧪 Shoot and melee damage intents
- [x] 🧪 Authoritative damage resolution (the host does the maths)
- [x] 🧪 Generic ability relay (heal, will-recover, rally, psychic scream, reload, interact)
- [x] 🧪 Weapon and equipment swaps
- [x] 🧪 Overwatch arm, clear, and cone
- [ ] ⬜ Deploy-turret and open-crate abilities (parked)

**Seeing**
- [x] 🧪 Player-faction vision reconcile

**The battlefield itself**
- [x] 🧪 Ground surfaces and volumes (fire, goo, acid, mist)
- [x] 🧪 Structural destruction and destructibles
- [ ] ⬜ Window-break geometry capture (parked)

**Unit state**
- [x] 🧪 Per-actor action points, will points, and status effects
- [x] 🧪 Ammo and mind-control display
- [x] 🧪 Mid-battle spawns and despawns (reinforcements, eggs, turrets, loot)

**Turns**
- [x] 🧪 End-turn intent to outcome
- [x] 🧪 Per-player intent dedup for three-plus player games

**Ending a mission**
- [x] 🧪 Mission-conclusion mirror
- [ ] ⬜ Evac-zone list population (parked)

**Making it look good**
- [x] 🧪 Fire and melee-swing animations
- [x] ✅ Enemy-turn camera chase
- [x] 🧪 Area-effect and explosion VFX replay

</details>

<details>
<summary><b>🔧 Under the hood (netcode)</b></summary>

- [x] ✅ One unified sync envelope for every message (no per-feature side channels)
- [x] ✅ Host-authoritative surface router as the single chokepoint
- [x] ✅ One-touch registration for new synced state
- [x] ✅ Nonce dedup and per-surface sequencing (safe under a double-sending reliable transport)
- [x] ✅ Chunked save-transfer barrier with CRC validation
- [x] ✅ NTP-style clock sync
- [x] ✅ 1625 game-free tests, green, buildable with zero game DLLs
- [x] ✅ `Multiplayer.Core`, a pure logic library that builds and tests anywhere
- [ ] ⬜ Rolling divergence detection and low-frequency resync

</details>

## The interesting part: how it is engineered

If you enjoy netcode, this section is for you.

**One rail, not fifty.** Every host-to-client and client-to-host message rides a single envelope opcode. Inside it is a tiny header (which surface, what kind, how long) and a payload. There is no separate wallet channel and research channel and vehicle channel fighting each other. There is one rail and a router that reads the surface id and hands the bytes to the right place. Adding a new synced thing means claiming a surface id and writing a codec, not inventing a new mechanism. The project has a rule for this, printed at the top of the contributor guide: converge, do not multiply.

**One writer per field.** Currency is the clean example. Exactly one thing on the wire is allowed to write your resource balance. Everything else that touches money does so through the host, then reads the result back. This sounds obvious and is the entire difference between "we all have 500 tech" and a slow, maddening desync where everyone's numbers drift apart.

**Idle is free.** An actor standing still sends zero bytes. A site that did not change sends zero bytes. State is versioned and only differences travel, so ten players staring at a paused Geoscape cost almost nothing.

**Two guards keep order.** Client intents carry a nonce and are deduplicated per player, so the reliable transport resending a packet cannot make you research the same thing twice. Host outcomes carry a per-surface sequence number and apply last-writer-wins, so a late duplicate can never paint stale state over fresh state.

**We talk to the game through reflection.** There is no source access, so the mod binds into the real game's internals with HarmonyLib and `AccessTools`, patches the exact methods where actions happen, and applies host decisions through the game's own in-place update paths. When Phoenix Point patches and a method signature moves, a version guard is meant to fail loudly ("incompatible version, update the mod") instead of desyncing in silence.

**Clients are inert on purpose.** A client's clock is frozen. Client-side fire does not deal damage, it just plays the animation. Client-side surfaces are cosmetic. Everything that changes the world is a decision the host already made and streamed down. Presentation is immediate and symmetric (you see your own shot the instant you fire), but the outcome lands at the impact frame, from the host, for everyone.

**Three ways to connect.** Steam peer-to-peer for the easy case, direct TCP for LAN and port-forwarders, and a STUN UDP hole-puncher for getting through NAT without either. They all sit behind one interface, so the rest of the mod never cares which one you picked.

## Building it

The code is split deliberately, partly because the machine writing most of it does not even have the game installed.

- **`Multiplayer.Core`** is pure logic with no game or engine dependencies. It builds and runs its full test suite on any machine, no Phoenix Point required. This is where the wire formats, dedup, sequencing, and decision logic live.
- **The full mod** references the real game DLLs (Assembly-CSharp, the modding framework, Unity, HarmonyLib) and can only be compiled on a machine with a legal Phoenix Point install.

CI builds and tests the game-free half on every push. The game-facing half is built and verified separately, by a human who owns the game. See [`CONTRIBUTING.md`](CONTRIBUTING.md) for the full picture and [`docs/README.md`](docs/README.md) for the design and as-built documentation.

## License

Licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE): free for noncommercial use, attribution required. In short: play with it, learn from it, build on it, just do not sell it.
