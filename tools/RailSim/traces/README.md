# Recorded traces

Drop a real session's `multiplayer.log` (and its `multiplayer-2.log` / `multiplayer-3.log` siblings)
here. `TraceReplay` reads the `[MP][windows] journal` lines out of them and replays that exact
sequence through the harness peers, so the model is exercised against real inputs rather than only
generated ones (spec §C.2).

Capture: run a 3-instance co-op session, then copy
`%USERPROFILE%\AppData\LocalLow\Snapshot Games Inc\Phoenix Point\Multiplayer\multiplayer*.log`
into this folder. The files are large; commit only the trimmed `[MP][windows]` lines.
