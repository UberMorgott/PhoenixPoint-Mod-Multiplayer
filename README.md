<div align="center">

# Phoenix Point: Cooperative Multiplayer

**A shared campaign for several players. Phoenix Point has no official co-op mode, so we're building one.**

[![English](https://img.shields.io/badge/lang-English-1f6feb?style=for-the-badge)](README.md)
[![Русский](https://img.shields.io/badge/lang-%D0%A0%D1%83%D1%81%D1%81%D0%BA%D0%B8%D0%B9-6e7681?style=for-the-badge)](README.ru.md)

[![Stars](https://img.shields.io/github/stars/UberMorgott/PhoenixPoint-Mod-Multiplayer?style=flat-square)](https://github.com/UberMorgott/PhoenixPoint-Mod-Multiplayer/stargazers)
[![Forks](https://img.shields.io/github/forks/UberMorgott/PhoenixPoint-Mod-Multiplayer?style=flat-square)](https://github.com/UberMorgott/PhoenixPoint-Mod-Multiplayer/network/members)
[![Issues](https://img.shields.io/github/issues/UberMorgott/PhoenixPoint-Mod-Multiplayer?style=flat-square)](https://github.com/UberMorgott/PhoenixPoint-Mod-Multiplayer/issues)
[![Last commit](https://img.shields.io/github/last-commit/UberMorgott/PhoenixPoint-Mod-Multiplayer?style=flat-square)](https://github.com/UberMorgott/PhoenixPoint-Mod-Multiplayer/commits)
[![CI](https://img.shields.io/github/actions/workflow/status/UberMorgott/PhoenixPoint-Mod-Multiplayer/ci.yml?style=flat-square&label=CI)](https://github.com/UberMorgott/PhoenixPoint-Mod-Multiplayer/actions)
[![Tests](https://img.shields.io/badge/tests-1873%20green-2ea043?style=flat-square)](Multiplayer.Tests)
[![License](https://img.shields.io/badge/license-PolyForm--NC--1.0.0-blue?style=flat-square)](LICENSE)
[![Built for TFTV](https://img.shields.io/badge/built%20for-TFTV-e8590c?style=flat-square)](#tftv)

[![Report a bug](https://img.shields.io/badge/%F0%9F%90%9E%20report-a%20bug-d1242f?style=for-the-badge)](https://github.com/UberMorgott/PhoenixPoint-Mod-Multiplayer/issues/new?labels=bug)
[![Request a feature](https://img.shields.io/badge/%E2%9C%A8%20request-a%20feature-8957e5?style=for-the-badge)](https://github.com/UberMorgott/PhoenixPoint-Mod-Multiplayer/issues/new?labels=enhancement)
[![Roadmap](https://img.shields.io/badge/%F0%9F%97%BA%EF%B8%8F-roadmap-1f6feb?style=for-the-badge)](#roadmap)

</div>

## The pitch

Phoenix Point is a good turn-based tactics game, but it's single player only. My friends and I wanted a shared campaign: each of us plays from our own PC and runs the soldiers and jobs the group assigned us. No passing a single mouse around, no taking turns at one keyboard.

There's no official co-op mode — Snapshot never shipped one. So we're writing it ourselves, one reverse-engineered Harmony patch at a time.

The mod turns a solo Phoenix Point campaign into a shared one: two or more people run the same Geoscape and fight the same tactical battles, each from their own machine.

Because it's built for co-op, soldiers in a tactical mission act at the same time instead of in strict turn order. A fight with a full squad plays faster and doesn't turn into a slideshow.

The mod runs no servers and rewrites no game logic — it's the original game with a network layer added on top.

## How the co-op works

All game logic runs only on the **host**, which runs the original, unmodified simulation. Clients don't simulate the game: they send requests and display the result the host returns.

- A player clicks "start research". The client doesn't run it locally — it sends the request to the host and waits.
- The host runs the action and broadcasts the result to every client.
- Client screens update to match that state.

This keeps everyone on the same campaign. Most of the work in the mod is making every screen in the game follow that rule.

<a name="tftv"></a>
## Built for Terror From The Void

The mod is developed alongside **[TFTV (Terror From The Void)](https://github.com/Voland163/TFTV)**, the big community overhaul, and is tightly integrated with it. That's the target setup — it's how the mod is tested and played.

I haven't tested the mod on vanilla Phoenix Point. I don't play vanilla without TFTV anymore, so run it with TFTV; vanilla is untested.

Get TFTV: [Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=2872311902) · [GitHub (Epic / GOG)](https://github.com/Voland163/TFTV)

## Status

This is an in-development mod, not a finished product. A lot of the list below is built and passing its tests but hasn't been through a long two-player session yet. Expect rough edges — file bugs, it helps.

You bring your own legally owned copy of Phoenix Point. The mod is built on Snapshot Games' official modding framework and ships no game code or assets. It's a fan project, not affiliated with, endorsed by, or supported by Snapshot Games.

<a name="roadmap"></a>
## Roadmap

The feature list is big, so it's broken down the way the game is: the lobby, the shared Geoscape, and the tactical battles.

**Legend:** &nbsp; ✅ done and verified &nbsp;·&nbsp; 🧪 built, not yet soak-tested in a live game &nbsp;·&nbsp; ⬜ planned or parked

<details>
<summary><b>🛰️ Lobby &amp; Session</b></summary>

**Connecting**
- [x] ✅ Steam peer-to-peer transport
- [x] ✅ Direct TCP/IP transport (host listens, clients dial in)
- [x] ✅ STUN UDP hole-punch transport (finds your public address, punches through NAT)
- [x] ✅ One swappable transport layer, pick your connection type at launch
- [x] ✅ Heartbeats and timeout detection (5s pings, 20s of patience)

**Participants**
- [x] ✅ Connection handshake (request, accept, reject)
- [x] ✅ Live player list
- [x] ✅ Player rename
- [x] ✅ Permission and ownership seeding on join
- [x] 🧪 Roster progress screen

**Session start**
- [x] ✅ Ready and un-ready toggles
- [x] ✅ "Everybody ready" start barrier
- [x] ✅ Chunked save transfer to pull every client onto the host's exact campaign (32 KB chunks, CRC checked)
- [x] ✅ Load-progress and "I am loaded" barrier
- [x] 🧪 Starting a brand-new campaign from the lobby (host picks difficulty, everyone loads the same first frame)
- [x] 🧪 Mid-session save load (host loads another save, every client follows without restarting)
- [x] 🧪 Mid-session drop-in on the Geoscape (join a game already in progress)
- [x] 🧪 Three-plus player topology
- [ ] ⬜ Joining a battle that is already underway (parked on purpose)

**Disconnects**
- [x] ✅ Clean host-left and client-left signaling
- [x] 🧪 A dropped client can rejoin the running session (rides the same drop-in path)
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
- [x] ✅ Research speed multiplier from labs and other modules, plus time remaining
- [x] ✅ Unlock availability (what a finished tech opens up)
- [x] 🧪 "Available research" reconcile after a project finishes

**Manufacturing and crafting**
- [x] ✅ Queue an item for manufacture
- [x] ✅ Manufacture completion sync
- [x] ✅ Scrapping items back down into resources
- [x] 🧪 Marketplace offers and selection mirror
- [x] 🧪 Haven resource trade (buy and sell at havens, host-authoritative, stock mirrored)

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
- [x] 🧪 Air-combat interception (host resolves the fight and holds the clock; clients stay usable)

**Personnel and recruits**
- [x] 🧪 Roster composition mirror (who is stationed where)
- [x] 🧪 Full soldier live-state
- [x] ✅ Equip from either side: drag items between doll and inventory, unequip-all button, items returned to storage correctly (permission and ownership gated)
- [x] ✅ Augmentations (bionics and mutations) from either side: reactive UI repaint, wallet cost deducted, body-part sections updated live
- [x] 🧪 Hire, transfer, dismiss, rename (permission and ownership gated)
- [x] 🧪 Recruit pool mirror (available, unarmed, and captured units)
- [x] 🧪 Level-up spending from a client (buy abilities, spend stat points)
- [x] 🧪 Containment actions from a client (kill or harvest captured units)

**Events and choices**
- [x] ✅ Events with answer choices, shown to everyone at once
- [x] ✅ Choice arbitration when two people answer together (first click wins)
- [x] ✅ Single-choice prompt advance
- [x] ✅ Event windows as a notification queue: popups stack in order, unavailable response options are greyed out (already chosen by another player), host-authoritative outcome text and art
- [x] 🧪 Ambush events, including the blocking brief that locks the screen
- [x] 🧪 Point-of-interest exploration (scan, probe, excavate)
- [x] 🧪 Resource harvesting
- [x] 🧪 Quest missions with cutscenes
- [x] 🧪 Objectives and quest-line state (including DLC and critical path)

**Report modals**
- [x] 🧪 Mission-brief mirror
- [x] 🧪 Mission-outcome report mirror
- [x] 🧪 Interception notices

**The rest of the world**
- [x] ✅ Diplomacy and reputation
- [x] ✅ Resources and wallet (one silent balance writer, no double-counting)
- [x] 🧪 Resource-harvest floating numbers
- [x] 🧪 Mist and fog field mirror
- [x] 🧪 Site identity and world-activity tails (havens, alien bases, excavations, weather, timers)
- [x] 🧪 Behemoth presence and status
- [x] 🧪 One shared relay behind the Geoscape abilities (scan, probe, repair, activate, guard, and the rest)

**Campaign start and finish**
- [x] 🧪 New-game start (host starts a fresh campaign, everyone begins on the identical save)
- [x] 🧪 Campaign end, win or lose (everyone sees the same victory or defeat outro)

</details>

<details>
<summary><b>⚔️ Tactical</b></summary>

**Deployment**
- [x] ✅ Full deploy snapshot, with chunking for oversized squads

**Moving**
- [x] ✅ Move intent to authoritative outcome
- [x] ✅ Concurrent move animation (everyone moves at once, no waiting your turn)
- [x] ✅ Position and facing deltas

**Fighting**
- [x] ✅ Shooting and melee attacks
- [x] ✅ Aimed shots that land on the real aim point
- [x] ✅ Grenades and thrown weapons
- [x] ✅ Authoritative damage resolution (the host does the maths)
- [x] ✅ Medkits, plus heal, will-recover, rally, psychic scream, reload, interact
- [x] ✅ Weapon and equipment swaps
- [x] ✅ Overwatch arm, clear, and cone
- [x] 🧪 Deploy turret, deploy shield, open crate, drop and retrieve items
- [x] 🧪 Live in-mission inventory (each item move syncs as you make it, no waiting for the screen to close)
- [x] 🧪 Vehicle mount and dismount
- [x] 🧪 Mind control, frenzy, jet jump, dash, and the other special moves

**Seeing**
- [x] 🧪 Player-faction vision reconcile

**The battlefield**
- [x] ✅ Structural destruction, including blowing things up with grenades
- [x] 🧪 Ground surfaces and volumes (fire, goo, acid, mist)
- [x] 🧪 Window breaks, including glass smashed by soldiers vaulting through

**Unit state**
- [x] ✅ Per-actor action points, will points, and status effects (yours and the enemy's)
- [x] ✅ Ammo and reloads
- [x] ✅ Mind-control display
- [x] 🧪 Mid-battle spawns and despawns (reinforcements, eggs, turrets, loot)

**Turns**
- [x] ✅ End-turn intent to outcome
- [x] ✅ Per-player intent dedup for three-plus player games

**Ending a mission**
- [x] 🧪 Mission-conclusion mirror
- [x] 🧪 Live mission-objective tracking (kill target, survive N turns, activate console)
- [x] 🧪 Scripted story-mission events (mid-battle spawns, zone unlocks, TFTV script guards)
- [x] 🧪 Evacuation (exit mission, mounted evac, evac-zone unlocks)

**Presentation**
- [x] ✅ Fire and melee-swing animations
- [x] ✅ Cover snapping (soldiers crouch into cover instead of just standing there)
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
- [x] ✅ Startup reflection self-check: an incompatible game version blocks co-op with a clear error instead of desyncing
- [x] ✅ 1873 game-free tests, green, buildable with zero game DLLs
- [x] ✅ `Multiplayer.Core`, a pure logic library that builds and tests anywhere
- [x] 🧪 Rolling divergence detection (hourly CRC probes over deterministic state subsets)
- [ ] ⬜ Automatic resync after a detected divergence

</details>

## How it's engineered

This section is for anyone interested in the netcode.

**One transport envelope.** Every message between host and client rides a single envelope opcode. Inside it is a short header (which surface, message type, length) and a payload. There are no separate channels for the wallet, research, and vehicles: there's one router that reads the surface id and hands the bytes to the right handler. Adding new synced state means claiming a surface id and writing a codec. This rule is printed at the top of the contributor guide.

**One writer per field.** Currency is the clean example: exactly one side on the wire is allowed to write your resource balance. Everything else that touches money does so through the host and reads the result back. That's what separates consistent state from a desync where players' numbers drift apart.

**Only differences are synced.** An actor standing still sends zero bytes. A site that didn't change sends zero bytes. State is versioned and only differences travel, so a paused Geoscape costs almost nothing even with ten players.

**Dedup and ordering.** Client requests carry a nonce and are deduplicated per player, so a resent packet won't run the same action twice. Host outcomes carry a per-surface sequence number and apply last-writer-wins, so a late duplicate can't overwrite fresh state with stale state.

**Talking to the game through reflection.** The game is fully decompiled, so its internals are known down to the method, but the mod can't be compiled against them — it hooks into the shipping build at runtime. It uses HarmonyLib and `AccessTools` to patch the methods where actions happen and apply host decisions through the game's own update paths. When a Phoenix Point patch moves a method signature, the version check should fail with a clear error ("incompatible version, update the mod") rather than desync.

**Clients are passive.** A client's clock is frozen. Client-side fire deals no damage — it only plays the animation. Client-side surfaces are cosmetic. Everything that changes the world is a decision the host already made and sent down. The animation plays immediately and identically for everyone (you see your own shot the instant you fire), but the outcome applies at the impact frame, from the host, for everyone at once.

**Three ways to connect.** Steam peer-to-peer, direct TCP (for LAN and port-forwarding), and STUN UDP with NAT hole-punching. All three sit behind one interface, so the rest of the mod doesn't care which one you picked.

## Building it

The code is split deliberately, partly because the machine that writes most of it doesn't have the game installed.

- **`Multiplayer.Core`** is pure logic with no game or engine dependencies. It builds and runs its full test suite on any machine, no Phoenix Point required. The wire formats, dedup, sequencing, and decision logic live here.
- **The full mod** references the real game DLLs (Assembly-CSharp, the modding framework, Unity, HarmonyLib) and only compiles on a machine with Phoenix Point installed.

CI builds and tests the game-free half on every push. The game-facing half is built and verified separately, by someone who owns the game. See [`CONTRIBUTING.md`](CONTRIBUTING.md) for the full picture and [`docs/README.md`](docs/README.md) for the design and as-built documentation.

## About this project

This is a portfolio piece and the kind of problem I enjoy: take a closed-source, shipping Unity game with no built-in multiplayer, decompile it to understand how it works, and add a deterministic, host-authoritative co-op layer on top without touching a line of its source.

What that involved:

- A sync protocol built from scratch around one transport envelope and a host-authoritative router, instead of separate solutions per feature.
- Reflection and Harmony patching to hook the methods where the game makes its decisions, plus a version check so a game update fails with a clear error instead of desyncing.
- A pure logic core (`Multiplayer.Core`) with no game or engine dependencies, so more than 1600 tests build and run in CI without the game.
- Around 80k lines of C# and counting, most of it the work of making every screen agree with every other screen.

The most useful things to read are `Multiplayer.Core`, the [`docs/`](docs/README.md) folder, and the netcode section above.

## License

Licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE): free for noncommercial use, attribution required. In short: study it, use it, build on it — just don't sell it.
