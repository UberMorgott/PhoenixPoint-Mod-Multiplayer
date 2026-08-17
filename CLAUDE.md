# CLAUDE.md

## Environment

- Use PowerShell and Windows paths for every command and subagent.
- Use `D:\Steam\steamapps\common\Phoenix Point` when the primary Phoenix Point installation is required.
- Co-op test instances are `D:\PP-Instance2` and `D:\PP-Instance3`; `E:\Dev Games\PP-Instance2` does not exist on this machine.
- Prefix build commands with `$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH`; the default `dotnet` has no SDK.
- Never publish or upload unless the user explicitly asks.

## Verification

- Build and deploy: `.\deploy.ps1 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'`.
- Run laws: `dotnet run -c Debug --project tools/RailCheck`.
- Run source integrity: `pwsh -NoProfile -File tools/law-integrity.ps1`.
- Enable commit hooks once per clone: `git config core.hooksPath .githooks`.
- Never claim verification without reporting the command and result.

## Field verification

- GREEN laws prove the harness path, not the running game. A behaviour change is not done until one field artifact is reported: a log line the change produces, a screenshot, or a live state dump before/after.
- Every Harmony patch must appear in `Harmony.GetAllPatchedMethods()` at startup under our owner id, and must increment an execution counter on first run. A declared patch that never applies or never executes means the feature is dead — report that instead of claiming success.
- Never claim a rail change repaints open UI without naming the repaint seam by `file:line` and the field artifact showing it repainted.
- When the field artifact needs a human at two clients, say exactly that. Reporting "laws are green" as verification is a correctness failure.
- Prefer a native full-rebuild entry point over replicating rendered rows; a rebuild re-enumerates the game's own sources and picks up rows other mods add.

## Laws

- Register every law through `Add(laws, () => ...)`; never use `laws.AddRange(...)`.
- For a file law, add `L<n>_<Name>.cs`, its registration, an executable guard, and increment `files=` in `tools/law-count.txt`.
- For an inline law, add its method and registration, then increment `inline=`; prefer file laws.
- Keep `tools/vacuity-exempt.txt` empty; fix unguarded laws instead of adding exemptions.
- Update the registration and runtime identity digests deliberately when the law set changes.
- Use `P<n>` for architecture principles and `L<n>` for executable laws; numbering is sparse.
- Never approve a law violation with `--update`; review baseline drift before updating snapshots.

## Mutation checks

- Run `tools/mutation-runner.ps1` to verify every registration reaches RED; shard with `-StartRegistration` and `-Limit`.
- Synthetic mutations test the harness path only, not production semantics.
- Require a compile-valid `src/` mutation for new or changed critical authority, barrier, codec, dedupe, ordering, parity, or lifecycle laws.
- A semantic kill requires the named law RED and restored baseline GREEN; record it in `docs/laws.md`.

## Commits and releases

- Preserve unrelated changes; inspect `git status` and recent commit subjects first.
- Use lowercase imperative Conventional Commits with a subsystem scope.
- Release only through `tools/release.ps1`; run `-WhatIf` first and write `## X.Y.Z-beta` in `CHANGELOG.md`.
- `meta.json` stores the bare version; tags and changelog headers carry the suffix.
- Never use `-Publish`, push tags, or rewrite published history without explicit user authorization.
