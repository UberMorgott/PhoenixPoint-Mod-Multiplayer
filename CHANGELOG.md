# Changelog

Player-facing notes for each release. Releases before `0.9.5-beta` are documented on the
[GitHub releases page](https://github.com/UberMorgott/PhoenixPoint-Mod-Multiplayer/releases).

## 0.9.6-beta

A patch release on top of `0.9.5-beta`. Same mod, same install — replace the `Multiplayer` folder
in your `Mods` directory. **Every player must run the same version**; a mismatch is reported when
you join. This release changes what travels over the wire, so a `0.9.5-beta` player and a
`0.9.6-beta` player genuinely cannot play together.

A co-op session that used to fall apart at the seams now holds together: peers see the same screens
in the same order, a client's shots actually fire, and a soldier answers for the weapon in his
hands.

### In battle

- **Attack animations start at the same moment on every peer.** Every attack — shot, free-aim,
  grenade, melee, ability — now plays from the host's record on every machine, including the player
  who pressed the button. No more "the grenade already exploded here but is still in the air over
  there". The acting player trades a ping's worth of delay for everyone seeing the same battle.
- **A client can fire.** Free-aim and aimed shots from a client were refused outright by the host;
  grenades and cone weapons went through the same refusal and should now work as well.
- **A wounded soldier answers for the weapon he is holding.** With a broken arm, pressing reload on
  the pistol swapped to the unusable rifle and failed; overwatch answered for the rifle too. Both
  now resolve against the selected weapon, and a refused order no longer burns the ability's use for
  that turn.
- **A crash that silently disabled the movement overlay** on a peer watching someone else's move is
  gone.

### Starting a campaign and reaching a mission

- **Campaign start plays in one order for everyone.** The intro runs first on every peer and the
  opening dialogs follow it, instead of the host getting dialogs while clients got the video.
- **The intro cutscene can be skipped on every peer.** Escape used to be silently dead on one
  client, which then sat through the entire video.
- **Everyone reaches the squad screen**, including the players who did not answer the mission event
  themselves. The screen now opens when the mission arrives, not when a local dialog closes.
- **Stealing an aircraft and other haven infiltration missions work from a client.** The client used
  to create the mission only on its own copy of the world, so the host rejected the launch as "no
  runnable mission".
- **Returning from a mission no longer leaves a finished mission's window on screen**, and host and
  clients arrive at the geoscape with the same windows.
- **A client no longer finishes site exploration on its own schedule.** Exploration timers were
  compared against the wrong clock, so a client could complete — or lose — an exploration the host
  never agreed to.

### Seeing the other players

- **Ping markers tell you who pinged.** Your own pings are green, everyone else's are blue.
- **The off-screen ping arrow is 2.5× larger and takes a click** — clicking it smoothly moves the
  camera onto the pinged target using the game's own camera moves.
- **Per-peer latency in the tactical player panel** — name, ping bars and status for every player,
  measured on the existing heartbeat.
- **A ready indicator beside End Turn** showing how many players have finished their turn, echoed as
  a tick or cross per player in the panel. Purely informational — it gates nothing, and the round
  never waits for anybody.
- **A five-second deployment countdown** when someone launches a mission, with a Cancel any single
  player can press to stop it for everyone.
- **The countdown panel's text fits inside it, and its Cancel button works.**
- **The ready button can be hovered and clicked again.**

### Known issues / unverified

This build is published for testing. Everything above has been built and verified against the
RailCheck law harness, but only the campaign-start fixes and item transfer have been confirmed in a
live three-player session. The rest — animation timing above all, since it changes the path of every
action in combat — still needs a real game.

If the animation change misbehaves, it is a single revert: nothing else depends on it.

Every player needs the same mod set. A session running with mismatched mods will pass items whose
definitions the other side cannot resolve.

### Internals

67 new law files were added to the RailCheck harness to hold these fixes down, bringing it to 134
file-backed laws plus 60 inline checks (194 total).

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
