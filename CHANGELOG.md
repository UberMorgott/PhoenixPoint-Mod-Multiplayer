# Changelog

Player-facing notes for each release. Releases before `0.9.5-beta` are documented on the
[GitHub releases page](https://github.com/UberMorgott/PhoenixPoint-Mod-Multiplayer/releases).

## 0.9.5-beta

A patch release on top of `0.9.4-beta`. Same mod, same install — replace the `Multiplayer` folder
in your `Mods` directory. **Every player must run the same version**; a mismatch is reported when
you join.

This one is mostly about the two places a co-op session used to simply stop: starting a new
campaign, and one player taking a turn.

### Starting and joining a game

- **A new campaign now actually starts in co-op.** Previously the host finished its loading screen
  and everyone else waited forever, with nothing in the log to say why. The campaign is now handed
  to the other players once the geoscape is genuinely ready, instead of one frame too early.
- **Joining over Steam works on a retry.** If your packets reached the host without Steam raising a
  fresh session request — which is what happens the second time you try to join the same host —
  you were accepted but never added to the list the host broadcasts to. You sat on "Connecting…"
  for about 75 seconds and dropped, silently. That peer is now registered from its own first
  packet.
- **Mission start opens the pre-mission squad screen for everyone.** The second player used to be
  dropped straight into the battle with no chance to pick a squad.
- **Chat messages are no longer duplicated when someone joins.**

### Loading screens

- **Everyone enters and leaves the loading screen together.** Every load boundary now raises the
  curtain on every player before the host starts loading, not partway through.
- **Waiting players see the host's real progress bar.** Instead of a static "waiting" label, the
  loading bar now fills from the host's actual load progress, on every path that starts a load —
  the lobby's PLAY button and a new campaign included, which previously showed an empty bar for the
  entire wait.
- **Your controls work after loading.** Keyboard, hotkeys, aircraft control and cutscene skip used
  to stay dead on a client after a load; the input unlock is now held as an invariant rather than
  hoping one code path reaches it.
- **Intro and event windows no longer appear twice on clients.**

### In battle

- **Another player's action no longer freezes yours.** A shot, a grenade or a jetpack flight
  anywhere on the map used to park every other player in a "waiting" state with their movement
  ranges cleared. Only the acting soldier is locked now; your own soldiers stay yours to command.
- **First come, first served on a busy soldier.** A soldier already executing someone's order can
  no longer be commanded by a second player. You get a visible refusal instead of the old
  freeze-then-teleport.
- **Overwatch from a client behaves.** It now ends that soldier's turn on the host too, the
  overwatch cone no longer stays stuck on screen after the shot fires, and mirrored orders no
  longer stall for about ten seconds before snapping into place.
- **Fixed a client crash on a mirrored shot** — the first-person aiming HUD was left holding a
  target that no longer existed.
- **Abilities with no explicit target list** (self-buffs and similar) are accepted from a client
  instead of being refused.

### Soldiers and the roster

- **Appearance changes and renames replicate**, and they now update on an already-open screen
  instead of only after you leave to the geoscape and come back.
- **Skill points can no longer be spent twice.** Two players confirming a purchase on the same
  soldier at the same moment could both succeed and drive the balance negative. The affordability
  check now happens at the confirm, not only at the click that opened the window, and the window
  that offered a spend you can no longer afford is closed.

### Known issues / unverified

**Everything above is verified by the automated RailCheck law harness — none of it has been through
a live multi-machine session yet.** Treat this as a test build and file what you hit.

Known remainders, not fixed in this release:

- A boss status-effect plaque can stay stuck on the host's screen.
- A client's aircraft can become un-redirectable after it completes a leg of its route
  (`CanRedirect` is not replicated).

### Internals

19 new laws (**L134–L152**) were added to the RailCheck harness to hold these fixes down.
