# 12 — Lobby & Peer Identity

[← Index](../README.md)

## Lobby-First Model

- `Create Game` opens the **lobby immediately** — campaign is **NOT loaded yet**.
- Host cannot play (no geoscape interaction) while in lobby.
- **Why:** prevents divergence. If host played before others joined, host's live in-memory state would drift from the on-disk save (e.g. "aircraft already moved"). Lobby-first makes the **on-disk save the single source of truth** at start — nobody has played, everybody loads identical state.
- Bulk state sync happens **exactly once** (at session start, [13](13-session-start-sync.md)); afterwards the action pipeline keeps everyone consistent ([09](09-sync-strategy.md)).

## Lobby Room Contents

- Host view:
  - Own **IP / connection code** displayed for copy-paste.
  - `Invite Friends` button (Steam overlay).
  - Player list with **ready** status.
  - `Play` button (host only) → enabled when all ready.
- Client view:
  - Player list + ready status.
  - `Ready` toggle.
- Both: **nickname edit field** (see below).

## Peer Identity

- Each peer = **stable `peerID`** (transport-level: SteamID or connection id) — never changes during session.
- **`nickname`** = mutable display label mapped to `peerID`.
- Default nickname: Steam persona name (Steam transport) else `Player N`.

## Nicknames (live)

- Any player (including host) edits **their own** nickname in the lobby.
- Edit → small `RENAME{peerID, newName}` packet → broadcast → everyone's UI updates instantly.
- Synced through the same lobby-state broadcast as player list / ready / progress.

## Why Identity Matters

- Soldier ownership (and later permissions) bind to **`peerID`, NOT nickname**.
- Roster "Состав" dropdown **shows nickname, stores `peerID`** → renaming never breaks assignments; reconnect re-binds by id.
- This is the "stable networking id" referenced in [05 — Investigation: Tactical](05-investigation-tactical.md).
- Ownership/nickname/permission data is **mod runtime-state, never written to the vanilla save** → [15 — MP State vs Save](15-mp-state-vs-save.md).
