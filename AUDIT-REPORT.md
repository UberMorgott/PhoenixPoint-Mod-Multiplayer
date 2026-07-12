# Audit Report — Multiplayer (Phoenix Point co-op mod)

- **Project:** Multiplayer (Phoenix Point co-op campaign mod) · `csharp/net472`
- **Commit:** `c8d83c2` · **Date:** 2026-07-12
- **Engine:** full-audit v1.11, **Level 3** (waves 1, 2, 2.5, 3, verify) · 15 agents
- **Machine-readable:** [`audit-bugs.json`](./audit-bugs.json) (49 findings, 12 suspected)

## Executive Summary

This is a very well-defended codebase. Build is clean (0 errors / 0 warnings, warnings-as-errors and analyzers on), 2271 tests pass, gitleaks finds no secrets, and there are no vulnerable or deprecated packages. The pure sync cores are unit-tested, idempotent, and defensively coded (fail-open guards, per-item try/catch, drift-poll backstops, a curated reflection guard), so most historically-recurring bug classes are already mitigated at HEAD. The **only HIGH-severity defects live at the peer-trust boundary** — three spoofable-identity / authorization bypasses (LEAVE, JOIN, and the unbounded outer-frame decoder) plus two save-transfer paths that can strand a client with no recovery — **and one tactical desync path** (an unguarded per-actor apply loop that can durably freeze a batch suffix). Everything below HIGH is either latent version-drift fragility, doc/comment staleness, dead code, or tracked incremental-convergence debt. The dominant systemic risk is not the design but its verification maturity: the reflection-glue layer that actually exercises co-op against the real game has no automated coverage and a large in-game-soak backlog.

## Severity counts

| Severity | Count |
|---|---|
| Critical | 0 |
| High | 6 |
| Medium | 16 |
| Low | 27 |
| Suspected / unconfirmed | 12 |
| **Total (confirmed)** | **49** |

---

## HIGH findings

### FA-0001 — LEAVE handler trusts self-asserted steamId in payload
- **File:** `src/Network/SessionManager.cs:749` · **confidence 90** · **verify-gate CONFIRMED (static)**
- **Attack:** `HandleLeave` reads `peerSteamId` from the packet **payload**, not from the transport-authenticated sender (`msg.SenderSteamId`). Every peer's SteamId is public via PEER_LIST, so any peer sends a `ClientLeave` carrying a victim's id and the host evicts that victim (roster drop + leave broadcast). Pure spoofed-actor authz bypass; `RemoveClient` runs unconditionally. The sibling `HandleRename`/`HandleChat` already key off the authenticated sender — LEAVE is the lone un-hardened handler.
- **Fix:** On the host, ignore the payload id and key the leave off `msg.SenderSteamId`. A client may only leave itself.

### FA-0002 — Phase-1 straggler kick strands a slow-but-alive client (or re-admits it as a ghost)
- **File:** `src/Network/SaveTransferCoordinator.cs:1663` · **confidence 88** · **reproduction REPRODUCED (static)**
- **Failure:** After a 180 s phase-1 timeout the host `RemoveClient()`s the straggler then `Begin()`s, but `RemoveClient` does **not** disconnect the peer at transport and sends **no** terminal notice. A still-preparing straggler drops the early `SessionBegin` (delivered but not latched) → `IsBarrierPending` stays true forever behind the load curtain, its late LOADED ack is a silent no-op, and its host-heartbeat timeout is suspended → only a manual Leave escapes. A straggler that had finished prepare instead enters the level as an untracked, un-commandable ghost.
- **Fix:** On kick, force a transport-level Disconnect (or send `ConnectionRejected`/`OnClientTransferFailed`) **before** `Begin()`, keeping roster and transport state consistent.

### FA-0003 — Unbounded payload length in the outer frame decoder (remote memory-exhaustion DoS)
- **File:** `Multiplayer.Core/Network/MessageLayer/NetworkMessage.cs:67` · **confidence 85** · **verify-gate CONFIRMED (static)**
- **Attack:** `payloadLen = BitConverter.ToInt32(...)` is read from the wire with no upper bound, then `new byte[payloadLen]` is allocated **before** the presence check. This is the outermost decoder, run for every inbound packet from any peer before auth/routing. A ~37-byte packet declares hundreds of MB; on net472 the allocation commits eagerly, so a spamming peer drives sustained GC pressure / OOM. Same class recurs in FA-0012 / FA-0016 / FA-0043.
- **Fix:** Reject before allocating — require `payloadLen >= 0 && payloadLen <= buffer.Length - 37` and cap at a sane transport max (e.g. 8 MB); drop on violation.

### FA-0004 — Client save-reassembly pins the heartbeat-suspension on a faulting-allocation transfer
- **File:** `src/Network/SaveTransferCoordinator.cs:1073` · **confidence 85** · **reproduction REPRODUCED (static)**
- **Failure:** `OnSaveChunk` sizes `_rxBuffer = new byte[chunk.TotalBytes]` from the wire with no bound. A positive-but-allocation-faulting `TotalBytes` throws **after** `_rxTotalBytes` is set, leaving `_rxBuffer` null; the throw is swallowed. `OnSaveDone`'s unknown-transfer branch returns **without** `ResetRx()` (unlike the incomplete/CRC branches), so `_rxTotalBytes>0` keeps `TransferActive` true → `loadInFlight` stays true → the client's host-heartbeat + half-open detector are suspended indefinitely → it never detects a dead host and never enters the level. Real on the best-effort Stun/WAN path or from a malicious host.
- **Fix:** Bound-check `TotalBytes` (0 < TotalBytes ≤ ~64 MB) before allocating and abort on violation; add `ResetRx()` to the unknown-transfer early-return.

### FA-0005 — Identity takeover via spoofed PlayerGuid in JOIN
- **File:** `src/Network/SessionManager.cs:351` · **confidence 82** · **verify-gate CONFIRMED (static)**
- **Attack:** Identity/ownership is keyed on `PlayerGuid`, which the joiner asserts in JOIN and which is broadcast to all peers in PEER_LIST (not secret). A JOIN carrying another live player's guid is treated as a "reconnect": `StaleRejoinPeers` matches by guid **regardless of SteamId**, so the host evicts the victim and rebinds the attacker's transport id to the victim's guid — granting the attacker the victim's `FullCommander`, soldiers, and slot. No SteamId↔PlayerGuid binding exists.
- **Fix:** Record SteamId↔PlayerGuid on first JOIN; on a matching-guid reconnect require `msg.SenderSteamId` to equal the stored SteamId, else reject.

### FA-0006 — tac.actorstate (0x8F) apply: one throwing actor aborts the batch suffix and skips the seq Mark
- **File:** `src/Sync/Tactical/TacticalActorStateSync.cs:325` · **confidence 70** · **verify-gate CONFIRMED (static)**
- **Failure:** The per-actor `foreach` has **no** per-actor try/catch, and `LiveSeq.Mark` sits inside the try **after** the loop. A reflected `SetStat`/`GetProp` Invoke that throws (a StatChangeEvent subscriber NRE-ing on a frozen mirror actor — a documented failure mode) unwinds the whole loop and skips `Mark`, so `_clientLast[TacActorState]` freezes and every later batch re-aborts at the same actor → the batch suffix is silently, durably desynced for the rest of the mission. The sibling `TacticalStructDamageSync`/`TacticalSurfaceSync` already use the correct per-record-guard + Mark-after pattern; this is the lone deviation. Concrete instance of the FA-0013 reflection-fragility family, undetected due to FA-0022.
- **Fix:** Wrap the per-actor body in its own try/catch that logs the netId and `continue`s, keeping `Mark` after the loop.

---

## MEDIUM / LOW findings

| id | sev | file:line | title | one-line fix |
|---|---|---|---|---|
| FA-0007 | MED | Multiplayer.Core/Sync/Tactical/TacticalActorStateDiff.cs:344 | Stale "GATED OFF" comments + dead IsSyncableStatusType allowlist (SyncStatuses is actually ON) | Rewrite the 330–368 block to the live ShouldMirrorStatus policy; delete the dead allowlist + tests |
| FA-0008 | MED | Multiplayer.Core/Sync/Tactical/TacticalActorStateDiff.cs:17 | File-header claims IsSyncableStatusType syncs Panic/stat-mods & excludes DoT — contradicts body + code | Update header to the live policy or drop it with the dead method |
| FA-0009 | MED | src/UI/SavePickerPanel.cs:30 | Dead class SavePickerPanel (628 LOC, zero refs) ships in assembly | Delete the file (and FA-0041 csproj ref) |
| FA-0010 | MED | src/Transport/SteamTransport.cs:38 | Deprecated SteamNetworking P2P API (weak spoofing protection, no encryption) | Add HMAC over the P2P channel; migration blocked by game DLL — document |
| FA-0011 | MED | Multiplayer.csproj:43 | Facepunch.Steamworks game DLL behind latest (2.3.x vs 2.5.2) | No action — locked to game DLL; document constraint |
| FA-0012 | MED | Multiplayer.Core/Network/MessageLayer/MessageSerializer.cs:85 | Unbounded counts/length in parity-manifest / JOIN decode → alloc DoS | Bound every wire count/length vs remaining stream before alloc/loop |
| FA-0013 | MED | src/Network/ReflectionGuard.cs:68 | Reflection version-fragility: ~1342 AccessTools sites, ~19 guarded → silent per-site desync | Extend ReflectionGuard.Critical to load-bearing bindings; add a CI reflection-manifest test |
| FA-0014 | MED | Multiplayer.Core/Validation/PermissionManager.cs:85 | Per-soldier ownership gate vacuous under blanket FullCommander grant (false assurance / IDOR) | Gate FullCommander behind a host toggle + test a non-owner is rejected before a per-owner mode ships |
| FA-0015 | MED | Multiplayer.Core/Sync/Tactical/TacticalActorRegistry.cs:36 | Actor registry keyed by IActorRef with no value-equality → latent double-mint/stale netId | Give TacticalActorAdapter value-equality or key on the wrapped actor object |
| FA-0016 | MED | Multiplayer.Core/Sync/Tactical/TacticalDeployChunkCodec.cs:90 | Unbounded chunk count/totalLen in deploy reassembler → alloc bomb (host→client) | Cap count/totalLen before returning a Fragment |
| FA-0017 | MED | Multiplayer.Core/Net/UpnpPortMapper.cs:247 | SSRF: WebRequest.Create with SSDP-LAN-sourced URL (HttpGet) | Validate scheme (http/https) + restrict to RFC1918 |
| FA-0018 | MED | Multiplayer.Core/Net/UpnpPortMapper.cs:263 | SSRF: WebRequest.Create with SSDP-LAN-sourced controlUrl (SoapPost) | Same as FA-0017 |
| FA-0019 | MED | src/Network/Sync/SyncEngine.cs:521 | Client unconditionally suppresses IHostOnlyApply event-answer outcomes; non-channelled ones desync | Map each event outcome kind to a channel or add an explicit outcome push |
| FA-0020 | MED | src/Sync/Tactical/TacticalFireAnimSync.cs:24 | Canon inv.5 impact-frame ordering is co-timed, not a real cross-surface barrier | Complete Inc3: queue tac.damage apply behind the fire.start impact callback |
| FA-0021 | MED | docs/superpowers/plans/2026-06-25-…-spine-roadmap.md:14 | Verification debt: reflection glue has no auto coverage; shipping outruns in-game soak | Treat the in-game soak backlog as release-blocking; add a repeatable smoke harness |
| FA-0022 | MED | Multiplayer.Core/Network/Sync/SurfaceSeq.cs:35 | Tactical 0x67 rail has no divergence probe; one-sided seq skew silently drops the rail | Carry a per-mission epoch or extend the Inc5 CRC probe to a tactical subset |
| FA-0023 | LOW | (27 files) | 454 whitespace formatting violations | `dotnet format`; add a CI format check |
| FA-0024 | LOW | .git | 3 stale merged feature branches | `git branch -d …` the three |
| FA-0025 | LOW | tools/launch-instance.bat:33 | Hardcoded machine-specific absolute paths | Read from env / gitignored .env |
| FA-0026 | LOW | Multiplayer.csproj:5 | .NET 4.7.2 behind 4.8.1 (supported, game-required) | No action; net472 required by runtime |
| FA-0027 | LOW | src/Harmony/Tactical/FireAnimSyncPatches.cs:147 | Stale "no ammo-sync surface built" comment — TS5 built it | Amend the parenthetical; drop the TODO |
| FA-0028 | LOW | tools/launch-instance.bat:107 | Hardcoded developer SteamID64 | Move to env / gitignored local config |
| FA-0029 | LOW | .gitattributes | No .gitattributes — EOL normalization unset | Add `* text=auto eol=lf` etc. |
| FA-0030 | LOW | docs/…/multiplayer-sync-canon-design.md:18 | "0x91 melee = STUB" is stale — melee is a full symmetric replay | Refresh the S2 snapshot |
| FA-0031 | LOW | .github/workflows/ci.yml:32 | Mutable action tag actions/checkout@v7 | Pin to a full SHA |
| FA-0032 | LOW | .github/workflows/ci.yml:35 | Mutable action tag actions/setup-dotnet@v5 | Pin to a full SHA |
| FA-0033 | LOW | src/Harmony/Tactical/AbilityBarStateDiagPatch.cs:28 | Diagnostic-only Harmony patch ships in prod (168 LOC) | `#if DEBUG`/flag it or remove |
| FA-0034 | LOW | Multiplayer.csproj:47 | 0Harmony version unknown, latest 2.4.2 (no CVEs) | Document the ModSDK Harmony version |
| FA-0035 | LOW | docs/…/multiplayer-sync-canon-design.md:68 | Canon spec silent on the new display-only faction mirror | Document ActorFieldFaction as display-only |
| FA-0036 | LOW | src/Transport/SteamTransport.cs:38 | Historical GNS CVEs (patched 2020) affect the newer API, not the one used | No action; note if migrating |
| FA-0037 | LOW | src/Network/Sync/SyncEngine.cs:53 | God-class concentration; SyncEngine is the one genuine multi-responsibility hub | Opportunistically lift drift-poll + reload-reset out of SyncEngine; accept the rest |
| FA-0038 | LOW | src/Sync/Tactical/TacticalMeleeAnimSync.cs:42 | Presentation symmetry by copy-paste, not a shared abstraction | Extract a shared presentation-replay spine next time the contract changes |
| FA-0039 | LOW | Multiplayer.Core/Util/LanIpResolver.cs:14 | LanIpResolver unused in product (test-only, 41 LOC) | Delete until a LAN-discovery feature exists |
| FA-0040 | LOW | src/Harmony/ClientTftvGeoscapeUiTeardownPatch.cs:158 | TFTV teardown-NRE suppression is a hand-maintained 7-hook allowlist | Consider one shape-keyed finalizer scoped to the CurrentLevel-null window |
| FA-0041 | LOW | Multiplayer.csproj:79 | UnityEngine.ImageConversionModule ref used only by dead code | Remove with FA-0009 |
| FA-0042 | LOW | src/Network/SessionManager.cs:122 | Heartbeat suspension during transfer/load disables host-loss detection for the window | Add an absolute wall-clock ceiling to the suspension |
| FA-0043 | LOW | src/Network/SaveTransferCoordinator.cs:992 | Reassembly buffer sized from wire TotalBytes (host→client alloc) | Reject TotalBytes ≤ 0 or > cap before alloc |
| FA-0044 | LOW | src/Network/Sync/WalletApplier.cs:35 | Wallet apply dropped during geo→tac transition; recovery path unverified | Buffer + re-apply on gate clear, or cite the promised re-broadcast |
| FA-0045 | LOW | Multiplayer.Core/Net/UpnpPortMapper.cs:315 | Regex without timeout on SSDP input (ParseLocationFromSsdp) | Add a Regex timeout |
| FA-0046 | LOW | Multiplayer.Core/Net/UpnpPortMapper.cs:344 | Regex without timeout on SOAP input (ParseExternalIp) | Add a Regex timeout |
| FA-0047 | LOW | src/Sync/Tactical/TacticalDeploySync.cs (class) | 79 swallowing catch blocks; history shows one hid a real deploy failure | Log-once on catches in state-applying reflected loops |
| FA-0048 | LOW | src/Transport/SteamTransport.cs:444 | Steam peer-connect not marshalled to main thread (unlike DirectTransport) | Marshal OnPeerConnected through the Update()-drained queue |
| FA-0049 | LOW | Multiplayer.Core/Sync/Tactical/TacticalActorStateDiff.cs:120 | Faction-flipper exclude is a 3-string exact-name match; a DLC flipper mirrors as non-neutralizing "inert" | Exclude by IS-A/trait + fail-safe if the Applied pre-set throws |

---

## Suspected / unconfirmed

Collected from all agents; below the confidence floor or needing runtime confirmation. Full detail in [`audit-bugs.json`](./audit-bugs.json) → `suspected_unconfirmed`.

| id | sev | file:line | note |
|---|---|---|---|
| HIST-S01 | MED | src/Network/Sync/SyncEngine.cs:53 | Dirty-mark-bypass desync: channels without a drift-poll backstop still exposed to out-of-band host mutation (needs per-channel tracing) |
| ADV-S02 | MED | src/Sync/Tactical/TacticalActorLifecycleSync.cs:84 | Actor death single-railed on reliable 0x88 with the 0x8F re-assertion deliberately disabled (defended today: all tactical sends reliable) |
| DIFF-S02 | LOW | src/Network/Sync/State/MarketplaceReflection.cs:369 | TryBuy affordability skipped if HasResources reflection fails to bind while Take binds — fail-closed fix |
| LOG-S01 | LOW | Multiplayer.Core/Sync/Tactical/TacticalDeployChunkCodec.cs:176 | ChunkReassembler trusts first-fragment TotalLen vs summed lengths → silent truncate/pad on mismatch |
| DIFF-S01 | LOW | src/Network/Sync/State/PersonnelEditReflection.cs:894 | Stale "never below the floor" comment after EffectiveStat reframe (no exploit) |
| SEC-U1 | LOW | src/Network/SessionManager.cs:872 | Chat text not stripped of control chars before re-broadcast (cosmetic/log-forge at most) |
| LOG-S02 | LOW | src/Network/Sync/SyncEngine.cs:81 | IntentDedup 512-ring could re-apply an evicted intent's twin (theoretical bound) |
| ADV-S01 | LOW | Multiplayer.Core/Network/Sync/HostBlockingPromptGate.cs:35 | Blocking-modal gate released only by a hand-maintained close-patch allowlist (bounded by boundary Reset) |
| SUS-001 | LOW | src/Network/SaveTransferCoordinator.cs:1592 | Host RevealAll never reaches a phase-1-kicked peer (self-reveal belt exists) |
| SUS-002 | LOW | Multiplayer.Core/Network/ClientSimFreeze.cs:18 | Client sim-freeze release relies on ShouldFreeze going false, not an explicit unpause |
| CONV-S01 | LOW | docs/…/spine-roadmap.md:14 | One-writer-per-field not yet met for pos/AP-WP/health (sanctioned convergence debt) |
| ARCH-S01 | LOW | Multiplayer.Core/Sync/Tactical/TacticalLiveCodec.cs:852 | Reserved-but-unencoded 0x8F bits (Equip/Overwatch) — tracked forward-planning |

---

## Audit process meta

Process defects, spec-mapping caveats, and tool-availability gaps encountered during the run are recorded in [`audit/AUDIT-PROCESS-DEFECTS.md`](./audit/AUDIT-PROCESS-DEFECTS.md).

## Limitations

- **No game/Steam runtime on the audit machine** (Assembly-CSharp, UnityEngine.*, 0Harmony, TFTV, Facepunch all `Private=false`). Every runtime-dependent finding is a static blast-radius / repro-trace; the stuck-session HIGHs (FA-0002, FA-0004) need a live 2-instance run (`tools/launch-instance.bat`) to observe the in-game hang — marked `reproduced=skipped_runtime`, static-confirmed.
- **Reflection-heavy host-authoritative mod:** correctness of nearly every reflected apply depends on runtime binding against absent game/TFTV types. The reflection-fragility findings (FA-0013 parent; FA-0006 / FA-0049 / FA-0015 instances) are latent / version-drift-conditional — structurally confirmed at HEAD but not runtime-reproducible without a mismatched game assembly.
- **Dead-code / SCA CLI tools from versions.lock not installed** (NuGone, SonarAnalyzer.CSharp, trivy, dotnet-outdated, dotnet-project-licenses). Dead-code analysis is grep + Serena-LSP based; deps are all local DLL references (`Private=false`, no version metadata / no packages.config), so automated version tracking is impossible.
- **Web/ASP.NET slices are N/A** — offline P2P Unity mod (net472), no web surface, DB, ORM, HTTP server, or auth cookies. The relevant analog (peer-identity binding) is covered by FA-0001 / FA-0005.
- **net472 alloc-bomb severity** (FA-0003 / FA-0012 / FA-0016 / FA-0043) assumes eager `new T[n]` allocation; `new byte[int.MaxValue]` likely OOMs at the 2 GB single-object ceiling, so the DoS relies on mid-range declared lengths allocating repeatedly under spam.
- **verify-gate re-adjudicated only the 5 top findings** (FA-0001/0003/0005/0014/0006 → SEC-001/002/003/005, LOG-001), all CONFIRMED, none demoted. The reproduction-confirmed set (FA-0002/0004/0007/0013) carries reproduction-adjusted confidences; the sibling alloc findings carry their agent confidences.
- **Spec-mapping caveats:** csharp.md concurrency/diff-scanner checklists are ASP.NET/TPL-tuned and map poorly onto the Unity-main-thread + Steam-callback + coroutine model. "Canon violation = HIGH minimum" was read as MEDIUM for FA-0020 and CONV-S01 because they are the canon's own roadmap-tracked incremental-convergence items, not newly-introduced bypasses — a stricter reading would raise FA-0020 to HIGH.
