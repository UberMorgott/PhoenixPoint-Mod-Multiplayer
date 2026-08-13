# Changelog

Player-facing notes for each release. Releases before `0.9.5-beta` are documented on the
[GitHub releases page](https://github.com/UberMorgott/PhoenixPoint-Mod-Multiplayer/releases).

## 0.9.11-beta

A patch release on top of `0.9.10-beta`. Same mod, same install — replace the `Multiplayer` folder
in your `Mods` directory. **Every player must run the same version**; a mismatch is reported when
you join. Nothing changed in how the machines talk to each other, so this is only the version
check — connect codes are unchanged from `0.9.10-beta`.

This one is a round of fixes to things that stopped working on the geoscape and at the end of a
mission, plus a sharper diagnostic for the desync report.

### The geoscape

- **The deployment prep button survives leaving the prep screen.** Pressing Back on a deployment you
  were not ready to drop yet took the join button away from everybody — including from you, leaving
  no way back into a deployment your own aircraft was still parked for. The button now stays as long
  as the drop itself is still open, and only a launch, a cancel or the carrier leaving ends it.
- **The map pauses again.** An arriving aircraft, a finished exploration or excavation no longer
  sails past unnoticed, and the host's own spacebar stops the clock. Both had been swallowed by the
  rule that keeps another player's tab from pausing your game; opening a tab still leaves the clock
  running for everybody else.
- **The host sees his own event windows again.** Events with a single choice — the intro sequence,
  `PROG_NJ0_WIN`, `SDI_01` — are completed by the game before their window is ever shown, and that
  was read as "another player already answered this", so the host got nothing while both clients saw
  all five. Events with a real choice to lose are unaffected.

### The end of a mission

- **A cancelled return countdown is cancelled for everyone.** Two players both pressing Continue was
  ordinary, and the second press went on ticking invisibly after somebody pressed Cancel — then
  expired and pulled the whole group to the geoscape with no countdown on any screen. Any peer can
  now cancel, and any later press arms a fresh five seconds.

### Diagnostics

- **A desync report now names the field that diverged**, instead of only the part of the campaign it
  was under, so a log from a real session says what actually differed.
- **The "exploration was not re-seeded" warning stopped crying wolf** — it fired on aircraft that
  were simply not exploring anything. It now warns only where there is real evidence of a lost order.

### Known issues / unverified

**This release has not been playtested.** Everything above is verified against the RailCheck law
harness and against the source only.

## 0.9.10-beta

A patch release on top of `0.9.9-beta`. Same mod, same install — replace the `Multiplayer` folder
in your `Mods` directory. **Every player must run the same version**; a mismatch is reported when
you join. This release changes how actors in a battle are named on the wire, so a `0.9.9-beta`
player and a `0.9.10-beta` player cannot play together. Connect codes are unchanged from
`0.9.9-beta`.

This one is mostly one fix: the reason tactical missions drifted apart between players.

### Battles that stay in step

- **The long-standing tactical desync is fixed at its root.** Every actor in a battle — soldier,
  enemy, civilian, crate — used to be named by the order it turned up on your machine. One actor
  that a peer had not revealed yet shifted the name of every actor after it, so from that point on
  the two machines were talking about different things while both believed they agreed. That is
  what was behind shots landing seconds late on the other player's screen, enemies standing
  somewhere else or not appearing at all, loot lying in different places, and the mod deciding the
  whole squad had diverged and resyncing it. Actors are now named by the game's own scene ids, so
  the name is the same on every machine whatever each player has seen.
- **A crash when a paired status was mirrored is fixed** — convincing a civilian objective target
  could throw and leave the mirror half-applied.
- **A status now carries the actor that applied it**, not only its definition, so effects that care
  about their source behave the same on every machine.

### Objectives and the end of a mission

- **Objectives now read the same for everybody**, including escort and convince targets, hold-this-
  tile and capture objectives. An objective that completes completes for the whole group, not only
  on the machine where it happened.
- **The end-of-mission board, XP and rewards are scored from the host's objectives** instead of each
  player recounting his own, so nobody gets a different result screen from the same battle.

### Getting into and out of a mission

- **Joining a mission somebody else is setting up.** While another player has the deployment screen
  open, a **deployment prep** button appears at the top of your idle geoscape and takes you straight
  in. Before, trying to join was simply refused with a message that read like a bug. The button
  disappears again if the player who opened the screen drops.
- **Nobody is stranded on the mission-summary screen any more.** The return countdown could be
  cleared by another player's machine, swallowing your own click so your return never ran. Each
  player now finishes his own way back to the geoscape.

### Smaller things

- **Countdown timers tick audibly** while they run.
- **A locked-in event choice is explained instead of reported as a failure.** Answering an event
  somebody already answered showed a diagnostic that one tester read as "mission joining is broken";
  it now says plainly that the first choice is frozen for everyone.
- **A refused action names the real cause** — the key, the status or the definition behind it —
  instead of a generic failure.

### Known issues / unverified

**This release has not been playtested.** No co-op session was run against it; everything above is
verified against the RailCheck law harness and against the source only. Treat the whole build as
untested, the identity change above most of all, since it touches every actor in every battle.

One older report is still open: the **objective list in the top-left corner does not appear on the
client**. It is not fixed here and it is not confirmed either — this build adds a log line naming
which faction built the panel, so a client-side log from a real session should finally say why.

## 0.9.9-beta

A patch release on top of `0.9.8-beta`. Same mod, same install — replace the `Multiplayer` folder
in your `Mods` directory. **Every player must run the same version**; a mismatch is reported when
you join. This release adds two new messages to the wire (the shared post-battle countdown), so a
`0.9.8-beta` player and a `0.9.9-beta` player cannot play together. Connect codes are unchanged
from `0.9.8-beta`.

This one is about the seams around a mission: who is shown the window that starts it, and what
happens on the way back out of the battle.

### Reaching and starting a mission

- **A mission window is now shown only to the player whose aircraft raised it.** Everybody else's
  screen stays clear instead of collecting windows about a mission they are not flying. The launch
  itself still reaches everyone, as before.
- **Mission briefs that open without a preceding event popup are routed correctly**, so a scavenging
  or haven brief no longer lands on the wrong player.
- **A mission window that has been answered is dropped from the queue when the mission ends**,
  instead of coming back afterwards.
- **An event that was disabled and then re-enabled can be completed again**, instead of being
  refused as stuck.
- **A client's answer to a shared window is no longer refused as invalid.** Answering from a
  non-host machine worked in some windows and silently failed in others.

### After a mission

- **The "returning to geoscape" countdown is shown to every player, not just the one who clicked.**
  Any player can start it, and any player can cancel it for the whole group — it is an opt-out, not
  a vote, and it still runs out by itself if nobody touches it.
- **Leaving the battle is announced when the return actually happens**, not the moment the button is
  pressed, so the other players no longer see you gone while you are still on the map.
- **A return that was still waiting when the session ended is finished instead of dropped**, and if
  the game's own exit throws, the host retries it rather than leaving everyone stranded in the
  after-battle screen.
- **The mod's own exits are no longer blocked by the return strip.**

### In battle

- **A soldier is released when his order is refused.** He used to stay locked, unable to take a new
  order for the rest of the turn.
- **You are told when a held order is given up on**, instead of it quietly disappearing.
- **A refused action now says what the game actually refused**, rather than a generic failure.

### Hosting and joining

- **A connect code typed with the letter `O` instead of a zero is accepted.**
- **A save transfer that produces no bytes no longer strands the host** on the transfer screen.
- **Connecting is cleaner under churn**: a connection that completes after you have already left is
  discarded, a second start no longer leaves a stray listener behind, and a listener whose socket is
  gone is shut down instead of lingering.

### Robustness

- **Every value read off the wire is now bounded before it is used** — a corrupt or hostile packet
  can no longer make the mod allocate or loop on a number it was simply told to trust.
- **Fog-of-war state that finished encoding across a load boundary is discarded** rather than applied
  to the wrong campaign.
- **A geoscape check that could fault on a torn-down level asks the question safely.**
- **Internal housekeeping**: dead code, an unused per-soldier ownership model and a superseded pause
  menu were removed, and several oversized source files were split up. No behaviour change intended
  from any of it.

## 0.9.8-beta

A release on top of `0.9.6-beta`, and it also carries everything that was tagged as `0.9.7-beta`
but never written up. Same mod, same install — replace the `Multiplayer` folder in your `Mods`
directory. **Every player must run the same version**; a mismatch is reported when you join. This
release changes what travels over the wire, so `0.9.8-beta` cannot play with older builds.

**Connect codes have changed and old ones no longer work.** A code is now 11 symbols and carries a
check symbol, so a mistyped code is rejected on the spot instead of dialling some random address.
Any code you wrote down from an earlier version is dead — ask the host for a new one.

Most of this release is about the windows a mission is made of: they now reach every player, each
player answers his own copy, and a decline no longer ends the mission for the group.

### Events, story and research

- **Event and story windows reach clients at all.** They used to appear only for the host —
  everybody else simply never saw them.
- **The research-completion window opens for every player**, not only the host.

### Reaching and starting a mission

- **The START/CANCEL window is answered per player.** Everyone gets their own live copy. One
  player's choice no longer greys out everyone else's buttons, a decline closes only that player's
  window, and the mission still launches exactly once no matter how many people press START.
- **A client pressing START actually launches the mission.** It used to fail with an internal error
  and drop the player back on the map.
- **Declining a mission no longer kills it.** Click the site again and it is re-offered on every
  peer — no flying away and coming back — and everyone lands in the same stage.
- **Everyone on the geoscape is taken to the squad/deployment screen when a mission arrives.** A
  player who is deep inside another screen is not yanked out of it; he gets the screen when he
  returns.
- **Squad preparation is collaborative.** Edits one player makes to the deployment loadout now show
  up for the others.
- **Backing out of deployment preparation no longer cancels the mission.** It just steps back, and
  you can go in again.
- **A deployment window closes for everyone once the last aircraft that raised it has left**,
  instead of lingering as a dead screen on the other players' geoscapes.

### On the geoscape

- **Time no longer stops for everyone when another player opens a tab.** It pauses only when the
  player who dispatched the aircraft leaves the map.

### After a mission

- **A five-second "returning to geoscape" countdown** is shown at the top.
- **Resupply is the first thing offered**, and each player's log states plainly whether anything was
  actually short.

### Hosting and joining

- **The lobby now asks whether you are creating or joining a session**, instead of dropping you
  straight into one flow; the session starts from there once you are ready.
- **Hosting works again without Steam.** On GOG and Epic, CREATE SESSION did nothing at all.
- **Sharing a session is simpler.** What you hand a friend is a short endpoint code rather than a
  raw Steam id, the session card reads as a labelled value you can copy, and the invite button greys
  out and tells you when a value has been copied. A pasted short code now also tries a direct
  connection instead of only the relayed route.
- **The join list names the host and shows how many players are in the session.**
- **Clicking JOIN keeps you on the join page** with a "connecting" line, instead of flashing you
  back to the main menu — and the join itself is near-instant rather than taking about ten seconds.
- **Ping recovers from a spike within a second** instead of drifting back down over twenty.
- **The READY button stays visible while it is locked and says why** — `MODS ✗` when the mod lists
  differ, `NO SAVE` before a save has been chosen.
- **Same-machine testing is supported**: the host card on the join page shows a
  `This PC (same machine)` address.

### Robustness

- A mismatched or oversized piece of shared state no longer silently stops reaching a player.
- A failed save transfer no longer leaves the receiving player's campaign identity overwritten.
- One player's very long nickname can no longer stop the player list from reaching everyone.
- A slow handler no longer stalls the network reader.
- The mod no longer breaks on installs where Steam support is not present — a missing Steam
  component is survived instead of failing outright.
- A window that got wedged open no longer pins the session's verdicts; it can recover on its own.

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
- **A deployment window closes for everyone once the last aircraft flies away from the mission.**
  Each player gets his own deployment window and reads it in his own time, but if the aircraft that
  could actually deploy there leaves the site, the window was offering a landing nobody could make —
  it now disappears on every player's screen, not just the one who moved the aircraft. The mission
  itself stays on the map; fly back and it opens again. Previously only the *mission ending* closed
  those windows, so an aircraft leaving left a dead window sitting on every other player's geoscape.
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

### Saving and loading

- **The mod no longer writes anything of its own into your campaign saves.** Your open windows are
  saved and restored by the game itself, the way they always were before — so loading a save puts
  you back with the game's own window queue rather than with a half-finished set of windows the mod
  remembered separately. Two side effects you may notice: answering a dialog no longer writes extra
  saves behind your back, and a long campaign's save files stop growing the way they did.
- **Saves written by an earlier test build still load.** They carry a leftover section the mod no
  longer understands; it is skipped rather than refused.

### Known issues / unverified

This build is published for testing. Everything above has been built and verified against the
RailCheck law harness, but only the campaign-start fixes and item transfer have been confirmed in a
live three-player session. The rest — animation timing above all, since it changes the path of every
action in combat — still needs a real game.

The deployment-window and save-state changes above are **new and completely unplayed**. Nobody has
loaded a save or flown an aircraft off a mission site with this build. If you are testing one thing,
test those: park an aircraft on a mission, open the deployment window on two machines, fly it away,
and check the window goes on both.

If the animation change misbehaves, it is a single revert: nothing else depends on it.

Every player needs the same mod set. A session running with mismatched mods will pass items whose
definitions the other side cannot resolve.

### Internals

The RailCheck law harness now stands at 215 file-backed laws plus 60 inline checks (275 total). The
durable-window work that shipped through this release was cut back afterwards: ten laws covering a
persistence and reconnect design that was never wired were deleted, and the audit and mission-start
work that followed added more than it took away.

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
