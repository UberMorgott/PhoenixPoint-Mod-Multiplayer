# CLAUDE.md

## Shell
- Run every command through **PowerShell**, never the Bash tool — PowerShell syntax (`$env:VAR`, backtick escape) and Windows paths.
- Same for subagents: brief them to use PowerShell.

## Deploy target
- Deploy only into the Steam game folder: `D:\Steam\steamapps\common\Phoenix Point` (confirmed by appid `839770` in `D:\Steam\steamapps\libraryfolders.vdf`; only Steam library on this machine).
- `& .\deploy.ps1 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'` — `-GameDir` wins over `$env:PhoenixPointDir` and the script's hardcoded probe list.
- Never publish to the Steam Workshop, never upload — local file install only, unless explicitly asked.
- `$extraModRoots` in `deploy.ps1` also mirrors to `D:\PP-Instance2` / `D:\PP-Instance3` (co-op test instances) — game folder is the target that matters.
- Docs elsewhere say `E:\Dev Games\PP-Instance2`; that path does not exist here — the dev instance is `D:\PP-Instance2`.

## Verify
- `deploy.ps1` — builds `Multiplayer.dll` into `Mods/Multiplayer`; needs a Phoenix Point install.
- `dotnet run -c Debug --project tools/RailCheck` — law harness; needs a Phoenix Point install (reflects over real game assemblies).
- `pwsh -File tools/law-integrity.ps1` — source-level only, runs anywhere; also `.github/workflows/laws.yml`.
- Never claim verified without pasting one of those actually run — "looks right" is not verification.
- `git config core.hooksPath .githooks` — wires both into pre-commit; one-time, per clone.

## Laws
- Two classes, both registered by one `Add(laws, () => ...)` in `tools/RailCheck/Program.cs`: file-backed `L<n>_<Name>.cs` and inline private methods (`RoundTrip`, `CrcBackstopLaw`, `RootOwnershipLaw`, ...).
- **Never write `laws.AddRange(...)`** — that shape let one throwing law abort the run and silently disable every law after it (`Program.Add`, L193 arm (d) forbids it coming back).
- `tools/law-count.txt` holds `files=` and `inline=`; bump the right one — the failure message names which class shrank.
- New file-backed law needs all three: the `L<n>_<Name>.cs` file, `Add(laws, () => L<n>...Check(...))`, `files=` bumped, plus a `premise-changed` or `POSITIVE CONTROL` guard.
- New inline law needs the method plus `Add(laws, () => <Method>(...))` and `inline=` bumped. Prefer a new `L<n>_<Name>.cs` file — inline laws carry no vacuity check.
- RailCheck REFUSES to run against a `Multiplayer.dll` older than `src/**/*.cs` (exit 2, `RAILCHECK REFUSED`) — so never gate on `dotnet run --no-build`. In a shared tree it also fires when another agent edits during your build: re-run.
- Guard is mandatory on file-backed laws — an unguarded law passes while checking nothing once its subject stops resolving.
- `tools/vacuity-exempt.txt` = ratchet of pre-existing unguarded laws. Never add; only remove, after adding a guard.
- Deleting either class: lower the matching number in `tools/law-count.txt` + explain why in the commit body — each law encodes a real past bug.
- `L<n>` = RailCheck laws only. `ARCHITECTURE.md` principles are being renamed `P<n>` — never conflate.
- `L<n>` numbering is sparse: L78, L85-L90 are unused; L104 exists as a file but emits no `"L104 ..."` string.

## Load-bearing
- `IdentityResolver.RootKinds` (`src/Rail/IdentityResolver.cs:241`) order: first arrival wins the visited set, `"GL"` stays last — enforced by L28. Do not reorder.
- `docs/rail-baseline.txt` — reviewed snapshot; read the diff, never blind-run `--update` to clear a failure.

## Commits
- Conventional Commits, lowercase, imperative, subsystem scope: `fix(tactical):`, `fix(personnel):`, `docs(changelog):`, `chore(release):`.
- Subject states the behaviour change, not the files — e.g. `fix(tactical): release the local UI a mirrored order takes over`.
- `git log --format='%s' -20` before writing; match it exactly.
