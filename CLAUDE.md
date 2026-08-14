# CLAUDE.md

## Environment

- Use PowerShell and Windows paths for every command and subagent.
- Use `E:\Dev Games\PP-Instance2` when a Phoenix Point installation is required.
- Prefix build commands with `$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH`; the default `dotnet` has no SDK.
- Never publish or upload unless the user explicitly asks.

## Verification

- Build and deploy: `.\deploy.ps1 -GameDir 'E:\Dev Games\PP-Instance2'`.
- Run laws: `dotnet run -c Debug --project tools/RailCheck`.
- Run source integrity: `pwsh -NoProfile -File tools/law-integrity.ps1`.
- Enable commit hooks once per clone: `git config core.hooksPath .githooks`.
- Never claim verification without reporting the command and result.

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
