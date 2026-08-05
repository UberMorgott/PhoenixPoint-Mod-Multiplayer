# Phoenix Point: Cooperative Multiplayer

[English](README.md) · [Русский](README.ru.md)

Phoenix Point is a good turn-based tactics game with no multiplayer. My friends and I wanted a shared campaign, each on our own PC, running the soldiers and the jobs the group handed us.

Snapshot never shipped co-op, so this mod adds it. Two or more people run the same Geoscape and fight the same battles from their own machines. No servers, no rewritten game logic: the host runs the original simulation with a network layer on top.

## How a session works

One player hosts and picks the save. Everyone else joins, and the host's campaign reaches them through the game's own save loader.

Battles have no ownership. All peers fight the same mission, and during your team's turn everyone acts at once: six soldiers, six commanders. Any player may command any soldier, whoever clicks first gets it. The arbiter is the game's own action-point and busy state.

## Networking model

The protocol has two message types.

**Intent** (client → host): *I want to do this.* Only the address of the gesture crosses the wire: a character id, a def, a slot.

**Delta** (host → everyone): *state changed like so.*

That's it. Joining a game and every load boundary reuse Phoenix Point's own save transfer instead of a third protocol.

## Host authority

On the Geoscape anyone can click anything, and nothing runs on your machine. All state changes go through a single gate in a single file: a mutation attempted outside an apply scope is intercepted before the model changes and turned into an intent, the host executes the game's own method, and the resulting state replicates to every peer. Every action family routes through that gate, so a new feature can't accidentally bypass host authority.

## The diff engine

Almost none of the synchronisation is written per feature. One engine walks the live campaign object graph and reads each object's persistent fields from **the game's own save-serializer metadata**: if the game would write a field into a save, the engine already sees it. Coverage is opt-out, so fields added by other mods ride along for free.

From [`docs/rail-baseline.txt`](docs/rail-baseline.txt): **96 types classified, 454 fields covered, 80 excluded**, every exclusion with a written reason. The walk starts from 11 roots (timing, the clock anchor, factions, sites, characters, vehicles, the event system, the mission generator, the marketplace, statistics, the level controller) plus any root a mod registers at runtime.

Addressing uses **stable game ids**: `SiteId`, the tactical unit id, vehicle id plus owning faction, def GUIDs. List indices and serializer object ids are session-local, so they're out. A path reads `S#<siteId>.Member.Member#key`, and both peers resolve it over their own graph. A subtree with no derivable stable key is refused and reported, never addressed by position. The manufacture queue is one of those: duplicate item defs are legal there, so it rides its own order channel.

Two refusals keep the engine from corrupting a client. Live instances reachable from the game's **definition** graph (`ItemDef.GetDisplayName` hands back def state by reference) go into a reference-identity set and get skipped at walk time; nothing static marks them, and a write through one damages static game data. A reference that decodes but can't resolve yet, unknown def GUID or unspawned entity, becomes an *unresolved* marker, and the apply skips that write instead of putting `null` over a valid live reference.

The walk is time-sliced: ~3 ms per frame, a cycle starting at most twice a second, shipped as one batch. Before slicing it cost 49–138 ms per tick on the main thread.

Output is canonical, sorted roots and children, fixed field order, so the same state produces the same bytes. One ordered sequence per channel, idempotent apply, safe under reordering; a gap makes the client ask for a resend, and a per-root checksum once a second catches the rest, answered by a scoped re-emit.

A delta arriving while a screen is open repaints it through a table of the game's own refresh methods per screen type. A screen with no entry falls back to leaving and re-entering, and logs itself once. That log is the to-do list.

## Tactical sync

Geoscape entities carry serialized identity, tactical ones don't, so the tactical half needed its own keys:

- Player soldiers: the `GeoUnitId` the game serializes itself.
- Aliens: a battle key derived from the start position, built once at the first turn edge.
- Mid-battle spawns: keyed by the host and adopted verbatim, because a derived key is a function of a board a late arrival was never in.
- Destructibles: the game's own scene GUID.
- Ground piles: the game's own find-or-create identity.

**Commands ship as orders, never as poses.** Every peer replays the order through the same `TacticalAbility.Activate`, re-deriving the path with its own pathfinder. Cost is the one thing a peer can't reproduce, since move AP is charged at the end of traversal over a distance that interrupts change, so once the action has really ended the host sends an authoritative settle of position, AP and will points. The acting peer plays its own click immediately; the settle makes that safe.

The host resolves damage, and mirrors apply it verbatim with their own damage maths switched off for the duration. A battle has no seedable RNG: scatter, damage rolls, shred and fumbles all draw from the global unseeded `UnityEngine.Random`. A shot has no hit/miss roll at all, just a scattered trajectory and a physics raycast, so two peers rolling independently would hit *different targets*.

Overwatch, return fire and zone control stay off the wire. Every peer raises them off the same replicated board, and mirroring one would be a second shot.

The tactical stream is discrete, with no periodic re-emit to heal a hole. The client checks sequence contiguity and asks for a full resnapshot on a gap.

## The law harness

`tools/RailCheck` is a headless program asserting **97 numbered laws** about the architecture, checked against the real `Assembly-CSharp` metadata and often against the IL of the game's own methods. No game process, no save file, runs in seconds, exit code 1 when a law breaks.

A few, for shape: every list-classed field must have an apply strategy, because a field licensed but not applyable *is* a resync storm; what a real apply wrote must re-encode to the host's exact bytes at the field codec; a repaint may not leave and re-enter a screen whose `EnterState` moves the view stack; the aim-pose mirror may never reach a navigation traversal. The last two are checked by walking IL, the allow-listed screen types and the transitive closure of every game method the mirror calls, string literals included, since the deleted code reached the view stack by method name.

The committed baseline file is the gate: the whole classifier table, every covered field with its class and live alias, every excluded field with its reason. **Any drift in it is a red build**, so a field moving between covered and excluded becomes a reviewable diff.

Green isn't proof, and the harness says so about itself: no simulation, no way to compare live host and client trees, and def/entity reference round-trips plus the diff layer's snapshot/compare/emit need a live game and are deliberately not faked. An in-game two-instance session is a mandatory second gate.

## Sessions, joining and loading

The lobby comes first, and no campaign loads while it fills. The host listens on **direct IP, STUN and Steam at the same time**, so a joiner arriving by any of the three lands in the same peer list, and the save is picked only once everyone is ready. Slot 0 is always the host; a reconnecting player is matched by a persistent id and gets its original slot back.

Mod parity blocks the join. Entitled DLC, enabled mods with their versions and each mod's settings are compared against the host's. Mods read their config at load time, so settings are compared, not distributed: simple ones can be applied from the host automatically, anything more complex holds the ready-lock until the players match it by hand.

Entering a battle counts as joining a new level, so it rides the same save transfer and adds no wire messages: the host writes a mid-tactical save, sends it in chunks, the client loads it. The capture waits for the host's battle to have actually started, so a half-built mission never ships.

Every load, lobby, mid-session reload, entering a battle, returning from one, passes through **one barrier**, armed at the single level-transition method the game funnels them all through. Peers park behind the loading curtain and report in; it lifts for everybody when the last one arrives.

Sends never block the game: a message is queued per peer and written by that peer's own thread, so a peer that stopped reading can't freeze the host's main thread. A half-open link (inbound alive, outbound dead) is told apart from a genuine host drop and gets one reset-and-rejoin attempt before the session is called dead. A transfer with no progress for 60 seconds re-arms the host-loss detector instead of parking everyone at "waiting for players" forever.

## What is synchronised today

**Geoscape - works**
Campaign clock, pause and speed · research, progress and queue order · resources and the shared wallet · manufacturing, scrapping and the shared scrap cart · base facilities: build, demolish, repair, power · personnel: hire, fire, reassign, level-up spending, abilities, second specialisation, skill reset · equipment, loadout commits and augmentation · vehicles: travel orders, crew, equipment, site exploration · site state, ownership and production · mist and fog of war · geoscape events and their answers, first-click-wins · modal windows · cutscenes · the window queue, advanceable by any peer so an idle host can't wedge it · the Kaos marketplace, including the offer roll · mission launch · mission outcome and reward pages.

**Geoscape - partial**
Post-mission replenish: repair and single-item reload cross, but the *Replenish All* button's reloads bypass the capture (its manufacturing and loadout halves still cross) · diplomacy: faction state is fully covered, the relations dictionary is not, and diplomacy has no intent surface of its own because it moves as a consequence of research, events and missions.

**Geoscape - not synchronised**
The *raise* half of four windows: mission brief, soldier join, interception, asset deployment (the queue-advance half exists) · other mods' geoscape state stored through the game's mod-save hooks. The engine refuses those outright: they mutate campaign state on record and are load-shaped on apply, so repeated application can't be made safe from this side. A mod that wants its state shared can register its own root with the diff engine.

**Geoscape - deliberately local**
In-flight vehicle position, re-derived closed-form by each client from the mirrored order, a fixed speed and the mirrored clock, so aircraft glide instead of stepping at the walk cadence · site creation and destruction, because the game has no runtime path for it and the world is generated once and rides the save transfer · per-player context help.

**Tactical - works**
Battle entry · turn order and end turn · every ability, through one generic command · enemy and AI actions, mirrored for every faction, with the AI's *decision* staying host-only because it draws from unseeded random before anything activates · weapon and equipment selection · damage, death and loot · actor spawns and lifecycle · evacuation · destructible environment · vision, re-tested in both directions on a settle · committed manual-aim stance.

**Tactical - partial**
In-battle inventory commits as a whole batch because the game's own does; container membership is enforced, order within a container is not.

**Tactical - not synchronised**
Pre-battle deployment placement is the host's: the tactical save is captured after the host has finished deploying, so clients enter an already-deployed battle.

**Tactical - deliberately local**
Overwatch, return fire and zone control · falls and no-support collapse · camera, selection highlight, cover-hug, idle animation, hover-preview aiming.

## Limitations

- The mod doesn't check the game build. A Phoenix Point patch that moves a method fails at runtime instead of refusing to start with a clear message.
- A player sitting inside the aim screen keeps a stale ability bar and a dead target's crosshair until their own next transition, the cost of never re-entering a screen whose enter path moves the view stack.
- A player already holding the aim stance fires instantly while everyone else first plays the entry-into-aim animation. Accepted as cosmetic.
- Two players answering the same event window at the same instant are arbitrated; genuine ties are not covered.
- Captured aliens never appear in a mirrored reward list: a generated recruit has no id in any registry, so there's nothing to address.
- The client's clock trails the host by one-way latency, roughly 20–80 ms of game time; the network leg itself is not compensated.
- Shared-seed determinism is a separate future project, and nothing in the current design assumes it.
- Version `0.9.0`, and beta means beta. Several subsystems are verified by the harness and by two-player sessions, but not by a long soak. Expect rough edges, and file bugs.

## Requirements

- **Phoenix Point** and the game's built-in modding support. No external mod loader, no other dependencies.
- **TFTV is tolerated, not required.** There's no declared dependency on it. If it's present, its hooks bind late through an assembly-load callback (TFTV loads after this mod) and three TFTV-only personnel actions become available. I develop and play with TFTV installed, so that's the better-tested setup.
- **All players need the same mods at the same versions and the same DLC entitlements.** A mismatch blocks the join.
- **Player count:** the Steam invite lobby holds two, host plus one. Direct IP and STUN have no engine-side cap, and sessions with three or more players work.
- The mod cannot be safely disabled mid-session.

You bring your own legally owned copy of Phoenix Point. This mod is built on Snapshot Games' official modding framework and ships no game code or assets. It is a fan project, not affiliated with, endorsed by, or supported by Snapshot Games.

## Install

Put the files here, inside your Phoenix Point install folder:

```
Phoenix Point/
└── Mods/
    └── Multiplayer/
        ├── Multiplayer.dll
        └── meta.json
```

Then enable **Multiplayer** in the game's mod list. A mod with this id already installed gets replaced: same id, same DLL name, same folder.

To play: one player hosts, which opens the lobby and starts listening on direct IP (port 14242 by default), STUN and Steam at once. The others join with an IP address, an invite code or a Steam invite. When everyone is ready, the host loads the campaign.

## Build from source

You need a .NET SDK that can target `net472` and a Phoenix Point install to reference: `ModSDK` for `0Harmony.dll`, `PhoenixPointWin64_Data/Managed` for `Assembly-CSharp.dll`, the Steamworks binding and the Unity modules.

The install path is a build property resolved in one place, `Directory.Build.props`, for both the mod and the harness. Order: `-p:PhoenixPointDir` on the command line, then an environment variable of the same name, then the usual Steam and Epic locations. If none exist, the build fails with a message naming the override.

```powershell
dotnet build Multiplayer.csproj -c Release
dotnet build Multiplayer.csproj -c Release -p:PhoenixPointDir="X:\path\to\Phoenix Point"
```

`deploy.ps1` runs the same build and copies `Multiplayer.dll`, its `.pdb` and `meta.json` into `Mods/Multiplayer`; it finds the game the same way, and `-GameDir "X:\path\to\Phoenix Point"` overrides it. Game references are non-private on purpose: the mod loader loads the entry DLL from bytes with no assembly-resolve handler, so the mod ships as a single self-contained assembly.

To run the law harness:

```powershell
cd tools/RailCheck
dotnet run -c Debug
```

It loads the game's assemblies at runtime and resolves the install itself: `--managed "X:\...\Phoenix Point\PhoenixPointWin64_Data\Managed"`, then the `PhoenixPointDir` environment variable, then the same probed locations. Exit code 0 is green. For an intended classifier change, `dotnet run -c Debug -- --update` rewrites the baseline, in the same commit as the change.

## About this project

A portfolio piece, and the kind of problem I enjoy: take a closed-source, shipping Unity game with no built-in multiplayer, decompile it, and add a host-authoritative co-op layer with Harmony and reflection, without touching a line of its source. About 41k lines of C# in the mod and 16k in the harness, most of it making every screen agree with every other screen.

Best things to read: `src/Rail` (the engine), `ARCHITECTURE.md`, `docs/rail-baseline.txt`.

## License

Noncommercial. Free to use, study and build on, with attribution; not for sale. Terms: [Creative Commons Attribution-NonCommercial 4.0 International](LICENSE) (CC BY-NC 4.0).
