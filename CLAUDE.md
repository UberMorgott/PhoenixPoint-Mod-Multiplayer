# CLAUDE.md

## Shell
- Run every command through **PowerShell**, never the Bash tool — PowerShell syntax (`$env:VAR`, backtick escape) and Windows paths.
- Same for subagents: brief them to use PowerShell.

## Deploy target
- The only Phoenix Point install on this machine is `E:\Dev Games\PP-Instance2` (verified: `PhoenixPointWin64_Data\Managed\Assembly-CSharp.dll` present). Deploy there.
- `& .\deploy.ps1 -GameDir 'E:\Dev Games\PP-Instance2'` — `-GameDir` wins over `$env:PhoenixPointDir` and the script's hardcoded probe list, none of which resolve here.
- Never publish to the Steam Workshop, never upload — local file install only, unless explicitly asked.
- Paths named elsewhere in the docs do NOT exist on this machine and the probe list will not find them: `D:\Steam\steamapps\common\Phoenix Point`, `D:\PP-Instance2`, `D:\PP-Instance3` (the last two are `$extraModRoots` in `deploy.ps1`, so they log `SKIPPED (not found)` — that warning is expected, not a failure).

## Verify
- `deploy.ps1` — builds `Multiplayer.dll` into `Mods/Multiplayer`; needs a Phoenix Point install.
- `dotnet run -c Debug --project tools/RailCheck` — law harness; needs a Phoenix Point install (reflects over real game assemblies).
- `pwsh -File tools/law-integrity.ps1` — source-level only, runs anywhere; also `.github/workflows/laws.yml`.
- PATH `dotnet` here is the x86 host with no SDK (`C:\Program Files (x86)\dotnet\dotnet.exe`), so a hand-run `deploy.ps1` / RailCheck dies with `sdk-not-found` + `build failed (-2147450725)` — broken PATH, not a broken build. Prefix with `$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH` (SDK 10.0.300). `.githooks/pre-commit` already falls back on its own (`RailCheck: PATH dotnet has no SDK, using ...`) — that is why commits pass; do not "fix" it.
- Never claim verified without pasting one of those actually run — "looks right" is not verification.
- `git config core.hooksPath .githooks` — wires both into pre-commit; one-time, per clone.

## Laws
- Two classes, both registered by one `Add(laws, () => ...)` in `tools/RailCheck/Program.cs`: file-backed `L<n>_<Name>.cs` and inline private methods (`RoundTrip`, `CrcBackstopLaw`, `RootOwnershipLaw`, ...).
- **Never write `laws.AddRange(...)`** — that shape let one throwing law abort the run and silently disable every law after it (`Program.Add`, L193 arm (d) forbids it coming back).
- `tools/law-count.txt` holds `files=` and `inline=`; bump the right one — the failure message names which class shrank.
- New file-backed law needs all three: the `L<n>_<Name>.cs` file, `Add(laws, () => L<n>...Check(...))`, `files=` bumped, plus an executable `premise-changed` or positive-control failure arm.
- New inline law needs the method plus `Add(laws, () => <Method>(...))` and `inline=` bumped. Prefer a new `L<n>_<Name>.cs` file — inline laws carry no vacuity check.
- RailCheck REFUSES to run against a `Multiplayer.dll` older than `src/**/*.cs` (exit 2, `RAILCHECK REFUSED`) — so never gate on `dotnet run --no-build`. In a shared tree it also fires when another agent edits during your build: re-run.
- Guard is mandatory on file-backed laws — an unguarded law passes while checking nothing once its subject stops resolving.
- `tools/vacuity-exempt.txt` must remain empty. Exemptions are forbidden; fix an unguarded law instead.
- Deleting either class: lower the matching number in `tools/law-count.txt` + explain why in the commit body — each law encodes a real past bug.
- `L<n>` = RailCheck laws only. `ARCHITECTURE.md` principles are being renamed `P<n>` — never conflate.
- `L<n>` numbering is sparse: L78, L85-L90 are unused; L104 exists as a file but emits no `"L104 ..."` string.

### Mutation checks

- Run `pwsh -NoProfile -File tools/mutation-runner.ps1` for the exhaustive 299/299 harness mutation check; shard with `-StartRegistration` and `-Limit`.
- Synthetic mutations prove the execution-to-RED path only; never call them semantic coverage.
- Require compile-valid `src/` mutations for critical authority, barrier, codec, dedupe, ordering, parity, and lifecycle laws.
- A semantic kill requires the named law RED and restored baseline GREEN; document it in `docs/laws.md`.
- Do not maintain one production patch per law; add targeted mutations for critical boundaries and fixed critical bugs.

## Load-bearing
- `IdentityResolver.RootKinds` (`src/Rail/IdentityResolver.cs:241`) order: first arrival wins the visited set, `"GL"` stays last — enforced by L28. Do not reorder.
- `docs/rail-baseline.txt` — reviewed snapshot; read the diff, never blind-run `--update` to clear a failure.

## Commits
- Conventional Commits, lowercase, imperative, subsystem scope: `fix(tactical):`, `fix(personnel):`, `docs(changelog):`, `chore(release):`.
- Subject states the behaviour change, not the files — e.g. `fix(tactical): release the local UI a mirrored order takes over`.
- `git log --format='%s' -20` before writing; match it exactly.

## Releases
- One command, never by hand: `pwsh -File tools/release.ps1 [-Version X.Y.Z] [-GameDir '<pp install>']`. Omit `-Version` to auto-bump the patch.
- Always rehearse first: add `-WhatIf` — prints every step, changes nothing, builds nothing.
- Write the CHANGELOG section BEFORE running: header must be exactly `## X.Y.Z-beta`; the script refuses without it.
- The version lives in **`meta.json` only** (bare `"0.9.11"`, no suffix). The `-beta` suffix exists only in the tag `vX.Y.Z-beta` and the CHANGELOG header — no csproj/README/assembly copy to keep in sync.
- Script refuses on: not `main`, dirty tree, tag already exists, missing CHANGELOG section, and any RED from `deploy.ps1` / RailCheck / `law-integrity.ps1` — gates run before the commit, so a failure leaves nothing committed or tagged.
- It commits `chore(release): X.Y.Z-beta` (only `meta.json` + `CHANGELOG.md`) and puts the **annotated** tag on that exact commit, then verifies the tag landed on HEAD.
- It NEVER pushes without `-Publish`. Plain run prints `git push origin main vX.Y.Z-beta`; running that is a deliberate human step.
- `-Publish` is what makes a release visible: push, then pack `artifacts/Multiplayer-X.Y.Z-beta.zip` (root folder `Multiplayer/` with dll+pdb+meta.json) and `gh release create` as a pre-release, notes = the CHANGELOG section, assets = zip+dll+pdb. Without it you get a bare tag and no release page — that is how `v0.9.11`/`v0.9.12`/`v0.9.13-beta` shipped unannounced.
- Historical tags are inconsistent — `v0.9.0/1/2/5-beta` are lightweight, `v0.9.3-beta` sits 23 commits past its release commit, `v0.9.5-beta`/`v0.9.10-beta` one commit past, and `v0.9.7-beta` shipped with `meta.json` still reading `0.9.6`. Published tags are left alone; do not "fix" them without the user's explicit OK.
