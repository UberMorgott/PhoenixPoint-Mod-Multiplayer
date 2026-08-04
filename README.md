# Phoenix Point: Cooperative Multiplayer

[English](README.md) · [Русский](README.ru.md)

Phoenix Point is a good turn-based tactics game, but it is single player only. My friends and I wanted a shared campaign: each of us playing from our own PC, running the soldiers and the jobs the group handed us. No passing a single mouse around, no taking turns at one keyboard.

Snapshot never shipped a co-op mode, so this mod adds one. Two or more people run the same Geoscape and fight the same battles, each from their own machine. There are no servers and no rewritten game logic — the host runs the original simulation, and a network layer sits on top of it.

## How a session plays

One player hosts and picks the save. Everyone else joins, and the host's campaign is handed to them through the game's own save loader. From then on there is one campaign that everybody is inside of.

On the Geoscape, anyone can click anything. Your click does not run on your machine: it is blocked before it touches the model and sent to the host as a request. The host runs the real action and the resulting state change comes back to everyone.

In a battle there is no ownership. All peers fight the same mission, and during your team's turn everyone acts at once — six soldiers means six commanders, not six turns in a row. Any player may command any soldier, and whoever clicks first gets it. The arbiter is the game's own action-point and busy state, not a table of who owns whom.

## How it is built

### Two kinds of message, and nothing else

**Intent** goes client → host: *I want to do this.* Only the address of the gesture crosses the wire — a character id, a def, a slot. **Delta** goes host → everyone: *state changed like so.* There is no third kind, and there is no snapshot push: joining and every load boundary ride the game's own save transfer instead.

Host authority is one gate in one file. On a client, a native state mutation outside an apply scope is blocked and converted into an intent; the host executes the game's own method; the result mirrors back. Every action family in the mod routes through that same gate, which is why authority cannot be forgotten in a new feature — there is nowhere else for a write to go.

### The diff engine

Almost none of the synchronisation is written per feature. One engine walks the live campaign object graph and enumerates each object's persistent fields using **the game's own save-serializer metadata**. If the game would write a field into a save file, the engine already sees it. Coverage is opt-out rather than opt-in, which is why fields added by other mods ride along with no code written for them.

As committed in [`docs/rail-baseline.txt`](docs/rail-baseline.txt): **96 types classified, 454 fields covered, 80 excluded** — each exclusion carrying a written reason. The walk starts from 11 roots (timing, the clock anchor, factions, sites, characters, vehicles, the event system, the mission generator, the marketplace, statistics, the level controller) plus any root a mod registers at runtime for its own state.

Addressing uses **stable game ids** — `SiteId`, the tactical unit id, vehicle id plus owning faction, def GUIDs. Never a list index, never a serializer object id (those are session-local). A path reads `S#<siteId>.Member.Member#key`, and both peers resolve it over their own graph. A subtree with no derivable stable key is refused from the engine and reported rather than addressed by position. That refusal is why the manufacture queue — where duplicate item defs are legal, so no element key exists — rides its own order channel instead.

Two rules the engine enforces because the alternative is silent corruption:

- Some live instances are reachable from both a mirrored entity and the game's **definition** graph (`ItemDef.GetDisplayName` hands back def state by reference). Writing through one of those would corrupt a client's static game data, and no static signal identifies them, so the engine builds a reference-identity set of everything reachable from the def repository and refuses at walk time.
- A reference that decodes but cannot be resolved yet — an unknown def GUID, an entity not spawned — becomes an explicit *unresolved* marker, never `null`, and the apply skips the write instead of clobbering a valid live reference.

The walk is time-sliced at roughly 3 ms per frame with a cycle starting at most twice a second, and a cycle ships as one batch; before slicing it cost 49–138 ms per tick on the main thread. Output is canonical, so the same state produces the same bytes: sorted roots and children, fixed field order. One ordered sequence per channel, idempotent apply, safe under reordering, and a gap makes the client ask for a resend. A per-root checksum runs once a second as a backstop and the host answers a mismatch with a scoped re-emit.

A delta arriving while a screen is open repaints that screen immediately, through a table of the game's own refresh methods per screen type. A screen with no entry falls back to leaving and re-entering it, and logs itself once — that log is the to-do list.

### Why the tactical side is different

Geoscape entities carry serialized identity. Tactical entities do not, so the tactical half needed its own keys: player soldiers use the `GeoUnitId` the game itself serializes; aliens get a battle key derived from their start position, built once at the first turn edge; anything spawned mid-battle is keyed by the host and adopted verbatim, because a derived key is a function of a board that a late arrival was never in; destructibles use the game's own scene GUID; ground piles use the game's own find-or-create identity.

**Commands ship as orders, not poses.** A command mirrors as *what was ordered*, and every peer plays it through the same `TacticalAbility.Activate` and re-derives the path with its own pathfinder. The one thing a peer cannot reproduce is the cost — move AP is charged at the end of traversal against a distance that depends on interrupts — so once the action has really ended the host sends an authoritative settle of position, AP and will points. The acting peer plays its own click immediately, and the settle is what makes that safe.

**Damage is never recomputed.** There is no seedable RNG in a battle: scatter, damage rolls, shred and fumbles all draw from the global unseeded `UnityEngine.Random`. Worse, a shot has no hit/miss roll at all — it is a scattered trajectory plus a physics raycast, so two peers rolling independently would hit *different targets*, not merely different numbers. The host therefore ships resolved damage results and mirrors apply them verbatim, with the client's own damage maths switched off during the apply.

**Autonomous reactions never cross.** Overwatch, return fire and zone control are raised independently by every peer off the same replicated board. Mirroring one would be a second shot.

The tactical stream is discrete, unlike the Geoscape one: there is no periodic re-emit to heal a hole, so the client checks sequence contiguity and asks for a full resnapshot when it finds a gap.

### The law harness

`tools/RailCheck` is a headless program that asserts **97 numbered laws** about the architecture — not unit tests of functions, but invariants checked against the real `Assembly-CSharp` metadata and, in many cases, against the IL of the game's own methods. It runs in seconds with no game process and no save file, and returns exit code 1 when a law is broken.

A few, to show the shape of them:

- Every list-classed field must have an apply strategy: a field licensed but not applyable *is* a resync storm, by construction.
- What a real apply wrote must re-encode to the host's exact bytes at the field codec.
- A repaint may not leave and re-enter a screen whose `EnterState` moves the view stack — the law resolves each allow-listed screen type and walks its IL looking for stack moves.
- The aim-pose mirror may never reach a navigation traversal: the law walks the transitive IL closure of every game method the mirror calls, and also scans string literals, because the deleted code used to reach the view stack by method name.

The committed baseline file is the gate. It is a full snapshot of the classifier table — every covered field with its class and live alias, every excluded field with its reason — and **any drift in it is a red build**. A field moving between covered and excluded is therefore a reviewable diff instead of a silent side effect.

Green is not proof of correctness, and the harness says so about itself: it does not simulate, it cannot compare live host and client trees, and several things (def and entity reference round-trips, the diff layer's snapshot/compare/emit) need a live game to check and are deliberately not faked. An in-game two-instance session is a mandatory second gate regardless.

### Sessions, joining and loading

The lobby comes first and no campaign is loaded while it fills. The host listens on **direct IP, STUN and Steam at the same time**, so a joiner arriving by any of the three lands in the same peer list, and the host picks a save only once everyone is ready. Slot 0 is always the host; a reconnecting player is matched by a persistent id and gets its original slot back.

Mod parity is blocking, not advisory. Before a join completes, the entitled DLC, the enabled mods with their versions and each mod's settings are compared against the host's. Simple settings can be applied from the host automatically; anything more complex holds the ready-lock until the players match it by hand.

Entering a battle is treated as joining a new level, so it rides the same save transfer and adds no wire messages of its own: the host writes a mid-tactical save, sends it in chunks, and the client loads it. The capture waits for the host's battle to have actually started, so a half-built mission is never shipped.

Every load — the lobby load, a mid-session reload, entering a battle and returning from one — passes through **one barrier**: each peer parks behind the loading curtain and reports in, and the curtain lifts for everybody only when the last peer has arrived. It is armed at the single level-transition method the game funnels all of them through.

Sends never block the game: a message is queued per peer and written by that peer's own thread, so a peer that has stopped reading can no longer freeze the host's main thread. A half-open link — inbound alive, outbound dead — is told apart from a genuine host drop and gets one reset-and-rejoin attempt before the session is called dead, and a transfer that stops making progress for 60 seconds re-arms the host-loss detector instead of parking everyone at "waiting for players" forever.

## What is synchronised today

**Geoscape — works**
Campaign clock, pause and speed · research, its progress and the queue order · resources and the shared wallet · manufacturing, scrapping and the shared scrap cart · base facilities: build, demolish, repair, power · personnel: hire, fire, reassign, level-up spending, abilities, second specialisation, skill reset · equipment, loadout commits and augmentation · vehicles: travel orders, crew, equipment, site exploration · site state, ownership and production · mist and fog of war · geoscape events and their answers, with first-click-wins arbitration · modal windows · cutscenes · the window queue, advanceable by any peer so an idle host cannot wedge it · the Kaos marketplace, including the offer roll · mission launch · mission outcome and reward pages.

**Geoscape — partial**
Post-mission replenish: repair and single-item reload cross, but the *Replenish All* button's reloads bypass the capture (its manufacturing and loadout halves still cross) · diplomacy: faction state is fully covered, the relations dictionary is not, and diplomacy has no intent surface of its own because it moves as a consequence of research, events and missions.

**Geoscape — not synchronised**
The *raise* half of four windows: mission brief, soldier join, interception, asset deployment (the queue-advance half exists) · other mods' geoscape state stored through the game's mod-save hooks, which is refused rather than deferred — those hooks mutate campaign state on record and are load-shaped on apply, so repeated application cannot be made safe from this side. Mods that want their state shared can register their own root with the diff engine.

**Geoscape — deliberately local**
In-flight vehicle position, re-derived closed-form by each client from the mirrored order, a fixed speed and the mirrored clock, so aircraft glide instead of stepping at the walk cadence · site creation and destruction, because the game has no runtime path for it — the world is generated once and rides the save transfer · per-player context help.

**Tactical — works**
Battle entry · turn order and end turn · every ability, through one generic command · enemy and AI actions, mirrored for every faction, with the AI's *decision* staying host-only because it draws from unseeded random before anything activates · weapon and equipment selection · damage, death and loot · actor spawns and lifecycle · evacuation · destructible environment · vision, re-tested in both directions on a settle · committed manual-aim stance.

**Tactical — partial**
In-battle inventory commits as a whole batch because the game's own does, and container membership is enforced while the order within a container is not.

**Tactical — not synchronised**
Pre-battle deployment placement is the host's: the tactical save is captured after the host has finished deploying, so clients enter an already-deployed battle.

**Tactical — deliberately local**
Overwatch, return fire and zone control · falls and no-support collapse · camera, selection highlight, cover-hug, idle animation, hover-preview aiming.

## Limits

Being straight about this is more useful than a full-green table:

- The mod does not check the game build. A Phoenix Point patch that moves a method will fail at runtime rather than refusing to start with a clear message.
- Mod settings are compared, not distributed. Mods read their config at load time, so a mismatch blocks the join instead of syncing.
- A player sitting inside the aim screen keeps a stale ability bar and a dead target's crosshair until their own next transition. That is the stated cost of never re-entering a screen whose enter path moves the view stack.
- A player already holding the aim stance fires instantly while everyone else first plays the entry-into-aim animation. Accepted as cosmetic.
- Two players answering the same event window at the same instant are arbitrated, but genuine ties are not covered.
- Captured aliens never appear in a mirrored reward list — a generated recruit has no id in any registry, so there is nothing to address.
- The client's clock trails the host by one-way latency, roughly 20–80 ms of game time; the network leg itself is not compensated.
- Shared-seed determinism is a separate future project. Nothing in the current design assumes it.
- Version `0.9.0`, and beta means beta. Several subsystems are verified by the harness and by two-player sessions, but not by a long soak. Expect rough edges, and file bugs — it helps.

## Requirements

- **Phoenix Point** and the game's built-in modding support. No external mod loader, no other dependencies.
- **TFTV is tolerated, not required.** There is no declared dependency on it. If it is present, its hooks bind late through an assembly-load callback (TFTV loads after this mod), and three TFTV-only personnel actions become available. I develop and play with TFTV installed, so that is the better-tested setup.
- **All players need the same mods at the same versions and the same DLC entitlements.** A mismatch blocks the join.
- **Player count:** the Steam invite lobby holds two — host plus one. Direct IP and STUN have no engine-side cap, and sessions with three or more players work.
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

Then enable **Multiplayer** in the game's mod list. If you already have a mod with this id installed, this replaces it — same id, same DLL name, same folder.

To play: one player hosts, which opens the lobby and starts listening on direct IP (port 14242 by default), STUN and Steam at once. The others join with an IP address, an invite code or a Steam invite. When everyone is in and ready, the host loads the campaign.

## Build from source

You need a .NET SDK that can target `net472`, and a Phoenix Point install to reference — `ModSDK` for `0Harmony.dll`, and `PhoenixPointWin64_Data/Managed` for `Assembly-CSharp.dll`, the Steamworks binding and the Unity modules. Your install path is a build property, not something hardcoded into the project file: `Directory.Build.props` is the single place it is resolved, for both the mod and the harness. It takes `-p:PhoenixPointDir` on the command line first, then an environment variable of the same name, then the usual Steam and Epic locations, and fails the build with a message naming the override if none of them exist.

```powershell
dotnet build Multiplayer.csproj -c Release
dotnet build Multiplayer.csproj -c Release -p:PhoenixPointDir="X:\path\to\Phoenix Point"
```

`deploy.ps1` does the same build and copies `Multiplayer.dll`, its `.pdb` and `meta.json` into `Mods/Multiplayer`; it finds the game the same way, and `-GameDir "X:\path\to\Phoenix Point"` overrides it. The game references are marked non-private on purpose: the mod loader loads the entry DLL from bytes with no assembly-resolve handler, so the mod has to ship as a single self-contained assembly and nothing else is copied.

To run the law harness:

```powershell
cd tools/RailCheck
dotnet run -c Debug
```

It loads the game's assemblies at runtime and resolves the install itself: `--managed "X:\...\Phoenix Point\PhoenixPointWin64_Data\Managed"` first, then the `PhoenixPointDir` environment variable, then the same probed locations. Exit code 0 is green. When a change to the classifier is intended, `dotnet run -c Debug -- --update` rewrites the baseline, and the baseline goes in the same commit as the change.

## About this project

This is a portfolio piece and the kind of problem I enjoy: take a closed-source, shipping Unity game with no built-in multiplayer, decompile it to understand how it works, and add a host-authoritative co-op layer on top without touching a line of its source.

What that involved:

- One diff engine driven by the game's own serializer metadata, instead of a hand-written synchronisation per feature — which is what makes the covered surface as wide as it is, and what makes other mods' state ride along for free.
- Identity work: finding a stable, serialized key for everything that has to be addressed across two machines, and refusing to address what has none rather than papering over it with list indices.
- Harmony and reflection to hook the game where it makes decisions, and to replay every mirrored action through the game's own methods rather than reimplementing them.
- A headless law harness that reads the game's IL, so a broken architectural invariant fails the build instead of surfacing as a desync three hours into a session.
- About 41k lines of C# in the mod and 16k in the harness, most of it the work of making every screen agree with every other screen.

The most useful things to read are `src/Rail` (the engine), `ARCHITECTURE.md`, and `docs/rail-baseline.txt` — which is the coverage evidence, not prose about it.

## License

Noncommercial. Free to use, study and build on, with attribution; not for sale. Terms: [Creative Commons Attribution-NonCommercial 4.0 International](LICENSE) (CC BY-NC 4.0).
