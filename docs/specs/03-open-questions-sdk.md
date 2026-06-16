# Open Questions (need PP source / SDK)

> Items below cannot be verified without Phoenix Point sources / SDK / a runtime assembly to inspect. Resolve each once the SDK is available. Do **not** assume from memory — ground every answer in the real source.

## Steam Networking

- Confirm Steam P2P is usable by a mod under the **game's App ID** (839770) with **no developer "activation"** required.
  - Expected (general Steamworks): friend-to-friend P2P / relay works for any owner; only dedicated-server SDR-with-tickets needs special setup.
- Which Steamworks bindings does PP link (Facepunch.Steamworks per [research/05-steam-networking](../research/05-steam-networking.md))? The mod calls into the same.
- Relevant: [research/05-steam-networking](../research/05-steam-networking.md), [engine/02-transport-layer](../engine/02-transport-layer.md).

## Native UI Injection

- PP main-menu UI architecture: the state class to patch (e.g. `UIStateMainMenu`?), and the button prefab to clone for native fonts/styling.
- UI state-stack push/pop API for sub-screens (Network Game / Join / Lobby).
- Relevant: [engine/03-harmony-patches](../engine/03-harmony-patches.md) (connection-menu injection), [specs/02-session-lifecycle-and-player-management](02-session-lifecycle-and-player-management.md).

## Local Dev / Testing

- Does PP enforce a **single-instance mutex** (blocks a 2nd process on one PC)?
  - If yes → workaround: launch the `.exe` directly (bypass Steam) and/or use a second game-folder copy.
- Needed for loopback `127.0.0.1` solo testing → [research/05-steam-networking](../research/05-steam-networking.md), [engine/02-transport-layer](../engine/02-transport-layer.md).

## Save System — RESOLVED

- PP save format: already compressed/binary on disk? (decides gzip vs send-as-is.) — partly answered: `.zsav`/`.zjsav` are gzip; see [research/04-serialization](../research/04-serialization.md). Confirm the exact API to invoke.
- ~~Save/load API to invoke for serialize (host) + load (client).~~
  - **RESOLVED.** Save = `PhoenixSaveManager.SaveGame(PPSavegameMetaData, ext, ByRef<bool> success, bool showCurtain)` (`PhoenixPoint.Common.Saves\PhoenixSaveManager.cs:191`). Load = `SaveManager.LoadGame(meta)`. Already driven by mod via `SaveLoadInterceptPatch`.
- Relevant: [research/04-serialization](../research/04-serialization.md), [specs/02-session-lifecycle-and-player-management](02-session-lifecycle-and-player-management.md) §2.

## Loading Progress Hook

- Locate PP's loading-progress source (class / float field / event / coroutine) to Harmony-hook for the **phase-2 load %**.
- Relevant: [specs/02-session-lifecycle-and-player-management](02-session-lifecycle-and-player-management.md) §2.

## Tactical / Campaign Entry Points

- The decompiled investigation already maps candidate sites ([research/01-tactical-action-pipeline](../research/01-tactical-action-pipeline.md), [research/03-campaign-layer](../research/03-campaign-layer.md)); confirm each Harmony intercept site + signature against the runtime assembly.

## Free Activation / AP Model

- Confirm the player phase uses **free activation** (no enforced soldier order) and the AP model assumed by simultaneous play → [research/07-tactical-concurrency](../research/07-tactical-concurrency.md).

## Geoscape State Machine / Time API

- PP global state-machine + UI state-stack push/pop API (to drive forced transitions).
- Geoscape event hook points (where to intercept event generation).
- Time-flow API (pause/speed) to drive the shared clock.
- Confirm the base UI has **no uncommitted edit buffer** that a forced "yank-to-briefing" would lose.
- Relevant: [research/08-geoscape-concurrency](../research/08-geoscape-concurrency.md).

## Mid-Battle Save & Reconnect — RESOLVED

- Reconnect-resync needs a **save while in a tactical mission** → [research/09-disconnect-reconnect](../research/09-disconnect-reconnect.md).
- ~~The game supports mid-battle save, but **possibly via an experimental mod, not vanilla** — confirm the source.~~
  - **RESOLVED = vanilla.** `TacticalView.QuickSaveGame():1133` → `SaveGameCrt():1141` exists in vanilla decompile. `PPSavegameMetaData` carries `isTacticalSave` bool (`Base.Serialization\PPSavegameMetaData.cs:50`); no tactical-specific SaveType. Mid-battle save is a vanilla feature.
- Relevant: [specs/02-session-lifecycle-and-player-management](02-session-lifecycle-and-player-management.md) §2, [research/09-disconnect-reconnect](../research/09-disconnect-reconnect.md).

## Persistent Config Location

- PP stable per-user config dir that survives a mod **update** (appdata / mod-config dir) — for `playerGUID` + `coop-perms.json` → [specs/02-session-lifecycle-and-player-management](02-session-lifecycle-and-player-management.md) §4.

## Trust / Identity

- `playerGUID` lives in a client-side file → copyable / spoofable. Accepted for friendly co-op (host is authoritative, stakes low). Revisit if competitive integrity ever matters.

## Deferred Scope (decided out of v1)

- Join-in-progress for a **brand-new** peer (mid-campaign / mid-mission). (Reconnect of a known peer is designed → [research/09-disconnect-reconnect](../research/09-disconnect-reconnect.md).)
- **Host migration / re-host** when the host drops (v1 = session ends) → [research/09-disconnect-reconnect](../research/09-disconnect-reconnect.md).
- Persistent ownership sidecar file → [specs/02-session-lifecycle-and-player-management](02-session-lifecycle-and-player-management.md) §3.
