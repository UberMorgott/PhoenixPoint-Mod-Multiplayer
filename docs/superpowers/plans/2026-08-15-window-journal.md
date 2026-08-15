# Window Journal Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the host's append-only window journal the single presentation order for every peer, with a per-peer read cursor where reading deletes the entry, and delete the two ordering systems it replaces.

**Architecture:** One host-minted `uint` position per window, claimed at the single generic capture seam (the `GeoscapeViewSwitchQuery.QueryStateSwitch` postfix), carried on the existing `0xB6`/`0xB7` raise payloads. Each peer holds an append-only unread list ordered by that position; presenting an entry deletes it locally, and a globally-dismissed entry is removed everywhere by an explicit host-minted void record. The client never invents a key and never sorts; `RailOrdinal`, `DurableInboxModel.HostOrderKey`, `WindowOrder.Reorder` and the settle timer all stop being ordering authorities.

**Tech Stack:** C# (net472), Harmony 2.x, Unity 2019 (Phoenix Point), RailCheck executable-law harness (`tools/RailCheck`), the new engine-free deterministic simulation harness (`tools/RailSim`), PowerShell 7 verification scripts.

---

## Read before the first task

- Spec (the authority, zero open questions): `E:\DEV\PhoenixPoint\Multiplayer2\docs\superpowers\specs\2026-08-15-window-journal-and-scoped-reactivity-design.md`
- Repo rules: `E:\DEV\PhoenixPoint\Multiplayer2\CLAUDE.md`, `E:\DEV\PhoenixPoint\Multiplayer2\docs\laws.md`

## Hard constraints that no task in this plan may violate

- **SERIALISE EVERYTHING.** Every task below touches `src/` or `tools/RailCheck`. Agents editing this repo concurrently sweep each other's commits and collide on law numbers. **One agent at a time, one task at a time.** Read-only research may run in parallel; nothing else may.
- **No quorum, ever.** No gate that waits on another human's ACTION. The save gate of Task 9 reads ONLY the local peer's cursor. Waiting on a LOAD that ends by itself is allowed and is untouched by this plan.
- **Reactivity is mandatory.** Every replicated-state change must repaint already-open UI. The journal's repaint seam is the existing `OpenUiRepaint.MarkDirty(Type, GeoLevelController)` / `UiEventMap.Fire` path; each task that changes replicated state names its repaint seam by `file:line` in the commit body.
- **Native UI only.** Task 14's centre-of-screen button is `UIModuleConfirmation` reached through `GeoscapeModulesData.ConfirmationModule`. No `new GameObject()`, no custom Canvas, no cloned button row.
- **No guessed engine signature.** Read it from `E:\DEV\PhoenixPoint\decompiled\AssemblyCSharp` or `E:\DEV\PhoenixPoint\refs\TFTV-src` and cite `file:line` in the commit body.
- **Commits are LOCAL on `main`. Never push.** `git add` with explicit paths only — `TFTV.dll` and `TFTV.meta.json` at the repo root must stay untracked, so `git add -A` is forbidden.
- **Never `--update`, never weaken a law, never add to `tools/vacuity-exempt.txt`.**
- The repo must be GREEN and shippable after every commit. Task order is dependency order — do not reorder.

## Law numbers claimed by this plan

`L520 L521 L522 L523 L524 L525 L526`. The highest existing id is `L516`; these are free and sparse. The companion plan `2026-08-15-scoped-reactivity.md` claims `L540`–`L545`, so the two plans cannot collide.

## The law ceremony — the full recipe, needed by every task that adds a law

Every task below that adds a law performs exactly these seven edits. They are written out in full inside each task; this section exists so the shape is visible in one place.

1. Create `tools/RailCheck/L<n>_<Name>.cs` with `internal static class L<n>_<Name> { internal static IEnumerable<string> Check() { … } }`. It MUST contain a `yield return "L<n> premise-changed: …"` arm or a `positive-control` arm, or `tools/law-integrity.ps1` fails it as vacuous.
2. Register it in `tools/RailCheck/Program.cs` with `Add(laws, () => L<n>_<Name>.Check());` — never `laws.AddRange(...)`.
3. Increment `files=` in `tools/law-count.txt` by 1. It reads `files=276` before this plan's first law lands.
4. Increment `ExpectedLawRegistrations` in `tools/RailCheck/Program.cs:488` by 1 and amend the comment above it naming the added law.
5. Increment the two `336` literals in `tools/RailCheck/L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs:98` and `:100` (three occurrences: `LawExecutionIsValid(336, 336, …)`, `LawExecutionIsValid(336, 335, …)`, `LawExecutionIsValid(336, 336, "wrong")`).
6. Run RailCheck once; it prints `RAILCHECK ABORTED — executed N/… identities=N, digest=<hex>`. Paste `<hex>` into `ExpectedExecutionIdentityDigest` (`Program.cs:494`) and into the two `L193` digest string literals at `:99` and `:101`.
7. Run `pwsh -NoProfile -File tools/law-integrity.ps1`; it prints `law identity set changed (digest <hex> != committed …)`. Paste that `<hex>` into `$expectedRegistrationDigest` (`tools/law-integrity.ps1:50`) and amend the comment above it.

Then prove the law can fail: apply a compile-valid semantic mutation to `src/`, run RailCheck, see the NAMED law RED, revert the mutation, run RailCheck, see GREEN — and record the mutation and the RED line in `docs/laws.md`.

## Verification commands (quote these literally; never claim a result you did not run)

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
.\deploy.ps1 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'
dotnet run -c Debug --project tools/RailCheck
pwsh -NoProfile -File tools/law-integrity.ps1
dotnet run -c Debug --project tools/RailSim -- --seed 1
```

Baseline to restore before every commit:

```
RAILCHECK GREEN — laws-run=336/336 law-violations=0
laws: 276 file(s) + 60 inline = 336
```

(That `336` grows by one per law this plan adds; the final state after Task 16 is `laws-run=343/343` and `files=283`.)

---

## File structure

### Created

| Path | Single responsibility |
|---|---|
| `tools/RailSim/RailSim.csproj` | net472 console project referencing the real `Multiplayer.csproj` + the game's `Assembly-CSharp`, so scenarios execute production code, not a reimplementation. |
| `tools/RailSim/Program.cs` | The harness host: `--seed` parsing, the injectable `SimClock`, the injectable in-memory `SimNet` transport with seeded delay/reorder, the scenario runner and the `RAILSIM GREEN/RED` verdict line. Its header states the §C.2 limitations verbatim. |
| `tools/RailSim/Scenarios.cs` | The five §C.1 properties as assertions on observable histories. |
| `tools/RailSim/TraceReplay.cs` | §C.2 recorded-trace replay: reads a captured `multiplayer.log` and replays its journal lines through the same peers. |
| `tools/RailSim/traces/README.md` | How to capture a trace from a real 3-instance session and where to drop it. |
| `src/Rail/WindowJournal.cs` | The whole journal: host position minting, the per-peer unread list, read⇒delete, the void record, the family dismissal-scope table, the 4096 canary, the local-cursor save predicate. Pure C#, no Unity types, so RailCheck and RailSim both execute it directly. |
| `tools/RailCheck/L520_TheOnlyPublicationIsTheQueryPostfix.cs` | One host publication path per raise; no reflective invoke of a native window-raise handler; restore and the initial-state raise also claim a position. |
| `tools/RailCheck/L521_TheAppendIsScreenIndependent.cs` | The append path runs with `GeoLevel() == null`. |
| `tools/RailCheck/L522_AClientNeverSortsAWindowQueue.cs` | No client-side ordering comparison survives. |
| `tools/RailCheck/L523_DurableIsAnswerOnceNotOrdering.cs` | No ordering comparison reads a durable key. |
| `tools/RailCheck/L524_OnlyAVoidRemovesAnUnreadEntry.cs` | Read⇒delete and host-minted void are the only removals; the canary logs once and keeps appending. |
| `tools/RailCheck/L525_TheSaveGateReadsOnlyTheLocalCursor.cs` | The manual-save gate touches no remote peer state and exempts `SaveType.Autosave`. |
| `tools/RailCheck/L526_AnUndeclaredFamilyIsLocal.cs` | Undeclared ⇒ LOCAL; only the host mints a void; no client removes an entry from its own `Servable()` evaluation. |

### Modified

| Path | Change | Direction |
|---|---|---|
| `src/Rail/GeoWindowCoverage.cs` (53.0 KB) — `:586`, `:620-635`, `:663-676` | `QueueCap` and `TrimQueue` **deleted** (Task 8); the postfix keeps `Announce` + `GeoModalMirror.HostBroadcastQueued`, and gains the journal position mint. | **SHRINKS** — the whole bound/trim section (~55 lines + its 25-line doc comment) dies; the coverage table survives untouched. |
| `src/Rail/WindowOrder.cs` (40.6 KB) | `BindDurable`, `TryGetDurable`, `DurablePriorityHead`, `Compare`, `SettleExpired`, `Reorder`, `OrdinalOf`, `QueuedAt`, `OrderKey`, `_stamps`, `_durable`, `SettleSeconds` all **deleted**. `Stamp`/`StampAt` become journal-position binding. `ReadyToDequeue` keeps only the open-screen hold + `DropResolvedSubjects` and gains the journal-order head check. `HoldsForOpenScreen`, `HoldsHead`, `MapStates`, `HeldTransitionStates`, `CurrentViewStateOf`, `DropResolvedSubjects` all **survive unchanged**. | **SHRINKS** hard (Tasks 6, 7) — two of its three jobs (ordinal ordering, durable priority) leave; the open-screen hold stays. |
| `src/Rail/WindowQueueSync.cs` (85.6 KB) | `TryDurablePriorityPreemption` and the suspend/resume preemption **deleted** (Task 7); `:203-214` local-only removal rerouted to the host-minted void (Task 10); `TrackDurableNativeCarrier` / `ConfirmDurableNativeOpen` survive as answer-once bookkeeping. | **SHRINKS** — the preemption engine dies, the intent plumbing survives. |
| `src/Rail/DurableInboxModel.cs` (38.7 KB) | `HostOrderKey` (`:97`) and every read of it **deleted** (Task 7). | **SHRINKS** — one field and its comparators; the occurrence ledger survives. |
| `src/Rail/DurableWindowRegistry.cs` (32.4 KB) | `MayPresent`'s ordering role **dies**; its `MapStates` `HashSet<Type>` (`:334-335`) **deleted** in favour of `WindowOrder.MapStates` (name form). `PriorityOf` **deleted** with `DurablePriorityHead`. `LastQueuedRequest` survives. | **SHRINKS.** |
| `src/Rail/DurableInboxEngine.cs` (66.2 KB), `DurableInboxState.cs` (12.4 KB), `DurableInboxStore.cs` (31.3 KB), `DurableInboxCodec.cs` (26.9 KB) | Every ordering member deleted; **answer-exactly-once semantics survive** (Task 7). | **SHRINK; none dies outright** — answer-once is still needed. |
| `src/Rail/RailOrdinal.cs` (6.6 KB) | `ForNewWindow` and the window back-fill (`Mint()` `:66-73` provisional list) **deleted** (Task 7). `Mint`/`Current` survive for their non-window users. | **SHRINKS.** |
| `src/Rail/EventPopup.cs` (156.1 KB) | `:411-413` second broadcast **deleted** (Task 2); `:993` `BindDurable` **deleted** (Task 7); `Encode`/`Decode` gain `journalPos` (Task 4). | **SHRINKS slightly.** |
| `src/Rail/GeoModalMirror.cs` (71.0 KB) | `HostBroadcastQueued` becomes the only publication path and mints the position; `Encode`/`Decode` gain `journalPos` and `kind = 2` (Void). | **GROWS by ~40 lines** — it absorbs what `EventPopup` and `WindowOrder` gave up. |
| `src/Rail/ResearchSync.cs` (37.5 KB) | `ViewResearchCompletedMethod`/`LogResearchCompletedMethod` (`:86-89`) and `PumpDeferredCompletions`'s reflective replay (`:373-400`) **deleted** (Task 3); `_everOnMapSurface` latch (`:117-121`, `:320`) **deleted** (Task 12); `:555-564` observability postfix **untouched**. | **SHRINKS.** |
| `src/Rail/DeploymentWindow.cs` (70.3 KB) | `DropUnservableQueued` (`:490-524`) demoted to native-queue hygiene; it stops touching the journal (Task 10). | **SHRINKS slightly.** |
| `src/Rail/ClientSimGate.cs` (41.8 KB) | **UNTOUCHED.** `GeoscapeEventRaiseGate` (`:354-370`) and `ClientResearchGate` (`:557-571`) are sim-authority gates, not window strategies (§A.9). | **UNTOUCHED — do not edit.** |
| `src/Rail/SurfaceIds.cs` | `GeoModalRaise = 0xB7`'s doc comment gains `kind = 2` (Void) and the trailing `journalPos:u32`; `GeoEventRaise = 0xB6`'s gains `journalPos:u32`. | Comment only. |
| `src/Lobby/SessionManager.cs` `:455`, `:475` | Drop `({client.PlayerName})` from the two `MpLog` calls (Task 16). | Two lines. |
| `tools/RailCheck/Program.cs` | Seven `Add(laws, () => …)` registrations, `ExpectedLawRegistrations` 336→343, `ExpectedExecutionIdentityDigest` updated seven times. | Ceremony. |
| `tools/RailCheck/L193_…​.cs` `:98-102` | The `336` and digest constants, updated seven times. | Ceremony. |
| `tools/RailCheck/L475_…​.cs` | **RE-EXPRESSED, never deleted or weakened** (Task 12): asserts a completion arriving with no map surface is APPENDED, not dropped. | Reworded — no count change. |
| `tools/RailCheck/L496_…​.cs` | **RETIRED** (Task 7) — it legitimised a duplicate gate (R7). Its file is deleted, its registration removed, `files=` decremented by 1 in the same commit that adds `L523`, so the net for Task 7 is zero. | **DIES.** |
| `tools/law-count.txt`, `tools/law-integrity.ps1:50`, `docs/laws.md` | Counts, registration digest, law rows + mutation kills. | Ceremony. |

### Not touched by this plan (say no, loudly)

`src/Rail/ClientSimGate.cs`, `src/Rail/GenericApplier.cs`, `src/Rail/UiEventMap.cs`, `src/Rail/OpenUiRepaint.cs` (all owned by the scoped-reactivity plan), `PhoenixSaveManager.AutosaveGame()` and its five `GeoLevelController` triggers, the `GeoWindowCoverage` declared not-covered families, and the ~63 kindless `MarkDirty()` sites.

---

## Task 1: The RailSim harness — project, injectable clock, injectable transport, seeded determinism

**Files:**
- Create: `tools/RailSim/RailSim.csproj`
- Create: `tools/RailSim/Program.cs`
- Create: `tools/RailSim/Scenarios.cs`
- Create: `tools/RailSim/traces/README.md`
- Test: `tools/RailSim/Scenarios.cs` — `SeededTransportIsReproducible`

- [ ] **Step 1: Create the project file.** Write `tools/RailSim/RailSim.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>RailSim</AssemblyName>
    <RootNamespace>RailSim</RootNamespace>
    <TargetFramework>net472</TargetFramework>
    <LangVersion>latest</LangVersion>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <GenerateAssemblyInfo>False</GenerateAssemblyInfo>
  </PropertyGroup>
  <!-- $(GameManaged) / $(ModSDK) come from the repo-root Directory.Build.props, same as tools/RailCheck. -->
  <ItemGroup>
    <!-- The REAL shipped rail bits: a scenario must execute production code, never a reimplementation. -->
    <ProjectReference Include="..\..\Multiplayer.csproj" />
    <Reference Include="Assembly-CSharp">
      <HintPath>$(GameManaged)\Assembly-CSharp.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="0Harmony">
      <HintPath>$(ModSDK)\0Harmony.dll</HintPath>
      <Private>true</Private>
    </Reference>
    <Reference Include="UnityEngine.CoreModule">
      <HintPath>$(GameManaged)\UnityEngine.CoreModule.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="UnityEngine.PhysicsModule">
      <HintPath>$(GameManaged)\UnityEngine.PhysicsModule.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write the harness host with its honest header.** Write `tools/RailSim/Program.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace RailSim
{
    /// <summary>
    /// ENGINE-FREE DETERMINISTIC SIMULATION over the mod's rail + window journal.
    ///
    /// WHAT THIS CANNOT PROVE — stated first because it is the thing a reader must not forget:
    /// IT TESTS THE MOD'S MODEL, NOT THE GAME. It will not catch a wrong Harmony seam, a patch that does
    /// not bind, a native method that behaves differently from the fake, or anything at all about the
    /// Unity hierarchy — the same limitation L512 and L514 already admit in their own headers. It is
    /// therefore PAIRED with recorded-trace replay from a real session (TraceReplay.cs), so the model is
    /// exercised against real inputs and not only against generated ones.
    ///
    /// The feasibility condition for deterministic simulation testing is INJECTABLE CLOCK AND TRANSPORT,
    /// not "can you boot the engine" (TigerBeetle VOPR, WarpStream, Antithesis). The rail and the journal
    /// are pure C# and meet it: RailMeta's digest clock is already a BCL Stopwatch chosen precisely so a
    /// headless harness can execute the codec in-process (src/Rail/RailMeta.cs:1449-1453).
    ///
    /// Every scenario asserts an OBSERVABLE HISTORY — the ordered list of what each peer presented — never
    /// the shape of a seam. Runs are seeded and reproducible from the seed alone.
    /// </summary>
    internal static class Program
    {
        private static readonly string[] InstallProbes =
        {
            Environment.GetEnvironmentVariable("PhoenixPointDir"),
            @"D:\Steam\steamapps\common\Phoenix Point",
            @"C:\Steam\steamapps\common\Phoenix Point",
            @"E:\Steam\steamapps\common\Phoenix Point",
        };

        private static string _managed = InstallProbes
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => Path.Combine(p, @"PhoenixPointWin64_Data\Managed"))
            .FirstOrDefault(Directory.Exists) ?? "";

        private static int Main(string[] args)
        {
            var m = Array.IndexOf(args, "--managed");
            if (m >= 0 && m + 1 < args.Length) _managed = args[m + 1];
            if (!Directory.Exists(_managed))
            {
                Console.Error.WriteLine("RailSim: Phoenix Point not found. Pass --managed \"X:\\...\\Managed\" " +
                                        "or set the PhoenixPointDir environment variable.");
                return 2;
            }
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var p = Path.Combine(_managed, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(p) ? Assembly.LoadFrom(p) : null;
            };
            int seed = 1;
            var si = Array.IndexOf(args, "--seed");
            if (si >= 0 && si + 1 < args.Length) int.TryParse(args[si + 1], out seed);
            try { return Run(seed); }
            catch (Exception ex) { Console.Error.WriteLine("RailSim CRASHED: " + ex); return 2; }
        }

        // NoInlining for the same reason RailCheck.Program.Run has it: the JIT resolves a method's type
        // references on entry, so every game type must stay out of Main until the resolver is installed.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(int seed)
        {
            var failures = new List<string>();
            int ran = 0;
            foreach (var scenario in Scenarios.All())
            {
                ran++;
                try { failures.AddRange(scenario.Value(seed)); }
                catch (Exception ex)
                {
                    failures.Add(scenario.Key + " CRASHED: " + ex.GetType().Name + " (" +
                                 ex.Message.Replace("\r", "").Replace("\n", " ") + "). A scenario that " +
                                 "threw proved NOTHING and is not a pass.");
                }
            }
            if (failures.Count > 0)
            {
                foreach (var f in failures) Console.Error.WriteLine("  " + f);
                Console.Error.WriteLine("RAILSIM RED — scenarios=" + ran + " failures=" + failures.Count +
                                        " seed=" + seed);
                return 1;
            }
            Console.WriteLine("RAILSIM GREEN — scenarios=" + ran + "/" + ran + " failures=0 seed=" + seed);
            return 0;
        }
    }

    /// <summary>THE INJECTED CLOCK. Nothing in a scenario reads a wall clock: time only moves when a
    /// scenario says so, which is what makes an armed-timer property (a gap self-releasing) assertable
    /// without sleeping.</summary>
    internal sealed class SimClock
    {
        internal float Now { get; private set; }
        internal void Advance(float seconds) { Now += seconds; }
    }

    /// <summary>THE INJECTED TRANSPORT. An in-memory host→peers link with seeded per-message delay, so a
    /// run reorders identically for identical seeds and differently for different ones. Delivery is by
    /// (dueAt, sequence) — never by insertion order — so out-of-order arrival is the DEFAULT shape a
    /// scenario sees, which is exactly the 363 ms inter-channel skew that produced P1 in the field.</summary>
    internal sealed class SimNet
    {
        private sealed class Pending
        {
            internal int To; internal float DueAt; internal long Order; internal byte[] Payload;
        }

        private readonly List<Pending> _inFlight = new List<Pending>();
        private readonly Random _rng;
        private readonly SimClock _clock;
        private long _order;

        internal SimNet(int seed, SimClock clock) { _rng = new Random(seed); _clock = clock; }

        /// <summary>Maximum delay a message may take, in seconds. 0.4 covers the measured 363 ms skew.</summary>
        internal float MaxDelaySeconds = 0.4f;

        internal void Send(int toPeer, byte[] payload)
        {
            _inFlight.Add(new Pending
            {
                To = toPeer,
                DueAt = _clock.Now + (float)(_rng.NextDouble() * MaxDelaySeconds),
                Order = _order++,
                Payload = payload,
            });
        }

        /// <summary>Everything due at or before the clock's current value, in delivery order, removed from
        /// flight. Ties break on send order so a run is a pure function of the seed.</summary>
        internal List<KeyValuePair<int, byte[]>> Drain()
        {
            var due = _inFlight.Where(p => p.DueAt <= _clock.Now)
                               .OrderBy(p => p.DueAt).ThenBy(p => p.Order).ToList();
            foreach (var p in due) _inFlight.Remove(p);
            return due.Select(p => new KeyValuePair<int, byte[]>(p.To, p.Payload)).ToList();
        }

        internal int InFlightCount { get { return _inFlight.Count; } }
    }
}
```

- [ ] **Step 3: Write the failing scenario.** Write `tools/RailSim/Scenarios.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace RailSim
{
    internal static class Scenarios
    {
        /// <summary>Every scenario, by name. A scenario returns zero strings when it holds and one
        /// human-readable failure per broken assertion otherwise.</summary>
        internal static IEnumerable<KeyValuePair<string, Func<int, IEnumerable<string>>>> All()
        {
            yield return Pair("seeded-transport-is-reproducible", SeededTransportIsReproducible);
        }

        private static KeyValuePair<string, Func<int, IEnumerable<string>>> Pair(
            string name, Func<int, IEnumerable<string>> body) =>
            new KeyValuePair<string, Func<int, IEnumerable<string>>>(name, body);

        /// <summary>C-requirement: "seeded runs, reproducible from the seed alone". Two SimNets built from
        /// the same seed must deliver the same messages in the same order, and a different seed must be
        /// able to produce a different one — otherwise the harness is not simulating reordering at all and
        /// every later ordering property would be vacuously true.</summary>
        private static IEnumerable<string> SeededTransportIsReproducible(int seed)
        {
            var a = DeliveryOrder(seed);
            var b = DeliveryOrder(seed);
            if (!a.SequenceEqual(b))
                yield return "seeded-transport-is-reproducible: two runs of seed " + seed +
                             " delivered different orders (" + string.Join(",", a) + " vs " +
                             string.Join(",", b) + "). A run that is not a pure function of its seed " +
                             "cannot reproduce a failure from the seed alone.";

            bool anyDifferent = false;
            for (int other = seed + 1; other <= seed + 32 && !anyDifferent; other++)
                anyDifferent = !DeliveryOrder(other).SequenceEqual(a);
            if (!anyDifferent)
                yield return "seeded-transport-is-reproducible: 32 different seeds all produced the " +
                             "delivery order " + string.Join(",", a) + ". The transport is not reordering, " +
                             "so every ordering scenario in this harness would pass without proving " +
                             "anything.";
        }

        /// <summary>Send 8 numbered messages to peer 1 at t=0, let the clock run past the delay ceiling,
        /// and report the order they came out in.</summary>
        private static List<int> DeliveryOrder(int seed)
        {
            var clock = new SimClock();
            var net = new SimNet(seed, clock);
            for (int i = 0; i < 8; i++) net.Send(1, new[] { (byte)i });
            clock.Advance(1.0f);
            return net.Drain().Select(kv => (int)kv.Value[0]).ToList();
        }
    }
}
```

- [ ] **Step 4: Run it and watch it fail to build, then fail.** Run:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
dotnet run -c Debug --project tools/RailSim -- --seed 1
```

Before Step 1–3 land this fails with `MSB1003`/`project file does not exist`. After they land it must print `RAILSIM GREEN — scenarios=1/1 failures=0 seed=1`. If instead it prints the `32 different seeds all produced the delivery order` line, `MaxDelaySeconds` is being ignored — fix `SimNet.Send`, do not weaken the scenario.

- [ ] **Step 5: Prove the negative arm bites.** Temporarily change `SimNet.MaxDelaySeconds` to `0f`, re-run the command from Step 4 and confirm it prints:

```
  seeded-transport-is-reproducible: 32 different seeds all produced the delivery order 0,1,2,3,4,5,6,7. The transport is not reordering, so every ordering scenario in this harness would pass without proving anything.
RAILSIM RED — scenarios=1 failures=1 seed=1
```

Restore `MaxDelaySeconds = 0.4f` and re-run to see `RAILSIM GREEN` again.

- [ ] **Step 6: Write the trace README.** Write `tools/RailSim/traces/README.md`:

```markdown
# Recorded traces

Drop a real session's `multiplayer.log` (and its `multiplayer-2.log` / `multiplayer-3.log` siblings)
here. `TraceReplay` reads the `[MP][windows] journal` lines out of them and replays that exact
sequence through the harness peers, so the model is exercised against real inputs rather than only
generated ones (spec §C.2).

Capture: run a 3-instance co-op session, then copy
`%USERPROFILE%\AppData\LocalLow\Snapshot Games Inc\Phoenix Point\Multiplayer\multiplayer*.log`
into this folder. The files are large; commit only the trimmed `[MP][windows]` lines.
```

- [ ] **Step 7: Confirm RailCheck is untouched and commit.** Run:

```powershell
dotnet run -c Debug --project tools/RailCheck
pwsh -NoProfile -File tools/law-integrity.ps1
```

Expect `RAILCHECK GREEN — laws-run=336/336 law-violations=0` and `law-integrity: OK`. Then:

```powershell
git add tools/RailSim/RailSim.csproj tools/RailSim/Program.cs tools/RailSim/Scenarios.cs tools/RailSim/traces/README.md
git commit -m "test(railsim): add the engine-free deterministic simulation harness"
```

---

## Task 2: Collapse the doubled host broadcast to the one postfix (§8 item 1, R2)

Two host publication paths exist today: `GeoModalMirror.HostBroadcastQueued` at the `QueryStateSwitch` postfix (`src/Rail/GeoWindowCoverage.cs:672`) and `EventPopup`'s explicit `0xB6` broadcast (`src/Rail/EventPopup.cs:411-413`). Once a journal position is minted at the postfix, both would append — a duplicate entry. Collapse first, journal after.

**Files:**
- Create: `tools/RailCheck/L520_TheOnlyPublicationIsTheQueryPostfix.cs`
- Modify: `src/Rail/EventPopup.cs:336-414` (delete the terminal broadcast; keep the unicast arm)
- Modify: `src/Rail/GeoModalMirror.cs:366-376` (`HostBroadcastQueued` learns the event family)
- Modify: `tools/RailCheck/Program.cs:438-440` (registration), `:488`, `:494`
- Modify: `tools/RailCheck/L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs:98-102`
- Modify: `tools/law-count.txt`, `tools/law-integrity.ps1:50`, `docs/laws.md`

- [ ] **Step 1: Write the law that must fail while two paths exist.** Create `tools/RailCheck/L520_TheOnlyPublicationIsTheQueryPostfix.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L520 — THERE IS EXACTLY ONE HOST PUBLICATION OF A WINDOW, AND IT IS THE QueryStateSwitch POSTFIX.
    ///
    /// This asserts "there is ONE mechanism", never "the mechanisms agree" — L496 was written the second
    /// way and legitimised the duplicate it should have killed (R7). Two arms:
    ///
    ///   (a) one-publisher — exactly ONE method in the mod assembly encodes a SurfaceIds.GeoEventRaise or
    ///       SurfaceIds.GeoModalRaise envelope for broadcast, and it is reached from
    ///       GeoModalMirror.HostBroadcast. Measured cause: on 2026-08-15 the host queued research then
    ///       event and both clients presented event then research, 363 ms apart, because two independent
    ///       publication paths keyed the same queue.
    ///   (b) no-reflective-raise — no method in the mod assembly obtains a MethodInfo for a native
    ///       window-raise handler (GeoscapeView.OnFactionResearchCompleted, GeoscapeLog.Faction_ResearchCompleted)
    ///       and Invokes it. A client presents a window by draining its journal cursor, never by replaying
    ///       a private native handler whose signature can change silently under it.
    ///
    /// ROLES SEPARATED (spec §C.3): both arms are statements about which METHODS exist and what they call,
    /// which is role-independent by construction — a host-only publication path is as visible as a
    /// client-only one. L507's blind spot was executing both roles in one process; there is no execution
    /// here to confuse.
    ///
    /// Falsify (compile-valid src mutations): re-add the BroadcastToAll of a GeoEventRaise envelope at the
    /// end of EventPopup.HostBroadcast -> (a); re-add
    /// AccessTools.Method(typeof(GeoscapeView), "OnFactionResearchCompleted") and an Invoke of it in
    /// ResearchSync -> (b).
    /// </summary>
    internal static class L520_TheOnlyPublicationIsTheQueryPostfix
    {
        internal static IEnumerable<string> Check()
        {
            var asm = typeof(WindowJournal).Assembly;
            var mirror = typeof(GeoModalMirror);
            var publish = mirror.GetMethod("HostBroadcast", BindingFlags.Static | BindingFlags.NonPublic |
                                                            BindingFlags.Public);
            if (publish == null)
            {
                yield return "L520 premise-changed: GeoModalMirror.HostBroadcast did not resolve, so this " +
                             "law cannot see the one publication path it exists to protect. Re-point it " +
                             "before believing the verdict.";
                yield break;
            }

            // (a) ONE PUBLISHER. Every method that mentions BOTH a raise surface id and a broadcast call.
            var broadcast = ResolveBroadcast();
            if (broadcast == null)
            {
                yield return "L520 premise-changed: no SyncEngine broadcast method resolved; arm (a) has " +
                             "nothing to count.";
                yield break;
            }
            var publishers = asm.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Instance |
                                              BindingFlags.Public | BindingFlags.NonPublic |
                                              BindingFlags.DeclaredOnly))
                .Where(m => MentionsRaiseSurface(m) && Il.References(m, broadcast))
                .Select(m => m.DeclaringType.Name + "." + m.Name)
                .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

            if (publishers.Count != 1)
                yield return "L520 one-publisher: " + publishers.Count + " method(s) broadcast a window " +
                             "raise surface (" + string.Join(", ", publishers) + "). Exactly one may — " +
                             "GeoModalMirror.HostBroadcast, reached from the QueryStateSwitch postfix. " +
                             "Two publication paths is how the host queued research then event on " +
                             "2026-08-15 while both clients presented event then research, 363 ms apart.";
            else if (publishers[0] != "GeoModalMirror.HostBroadcast")
                yield return "L520 one-publisher: the single publisher is " + publishers[0] +
                             ", not GeoModalMirror.HostBroadcast. The publication seam moved without this " +
                             "law being re-pointed, so nothing is guarding the seam any more.";

            // (b) NO REFLECTIVE NATIVE WINDOW RAISE.
            var forbidden = new[] { "OnFactionResearchCompleted", "Faction_ResearchCompleted",
                                    "OnGeoscapeEventRaised" };
            var offenders = asm.GetTypes()
                .SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Instance |
                                             BindingFlags.Public | BindingFlags.NonPublic |
                                             BindingFlags.DeclaredOnly)
                                  .Where(f => typeof(MethodBase).IsAssignableFrom(f.FieldType))
                                  .Select(f => t.Name + "." + f.Name))
                .Where(n => forbidden.Any(bad => n.IndexOf(bad, StringComparison.Ordinal) >= 0))
                .OrderBy(x => x, StringComparer.Ordinal).ToList();
            // A MethodInfo field named after the handler is the shape ResearchSync used (:86-89); the
            // string literal is what survives a rename of the field, so both are checked.
            var literalHolders = asm.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Instance |
                                              BindingFlags.Public | BindingFlags.NonPublic |
                                              BindingFlags.DeclaredOnly))
                .Where(m => Il.MentionsAnyString(m, forbidden))
                .Select(m => m.DeclaringType.Name + "." + m.Name)
                .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

            if (offenders.Count > 0 || literalHolders.Count > 0)
                yield return "L520 no-reflective-raise: the mod still reaches a native window-raise " +
                             "handler by reflection (fields: " + string.Join(", ", offenders) +
                             "; methods naming it: " + string.Join(", ", literalHolders) + "). A client " +
                             "presents a window by draining its journal cursor. Replaying a PRIVATE native " +
                             "handler means reconstructing its arguments and swallowing its failures into " +
                             "a LogWarning, so every native signature change breaks presentation silently.";

            // POSITIVE CONTROL: the arms above are only meaningful while the publisher and the surface ids
            // they name still exist. If either premise evaporates the law must say so rather than pass.
            if (!MentionsRaiseSurface(publish))
                yield return "L520 positive-control: GeoModalMirror.HostBroadcast no longer mentions a " +
                             "window raise surface id, so arm (a) counted a set that cannot contain the " +
                             "real publisher and would report 0 publishers as a pass.";
        }

        private static MethodBase ResolveBroadcast()
        {
            var engine = typeof(WindowJournal).Assembly.GetTypes()
                .FirstOrDefault(t => t.Name == "SyncEngine" || t.Name == "SyncEngineStub");
            return engine == null ? null : (MethodBase)engine.GetMethod("BroadcastToAll",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        private static bool MentionsRaiseSurface(MethodBase m) =>
            Il.MentionsByte(m, SurfaceIds.GeoEventRaise) || Il.MentionsByte(m, SurfaceIds.GeoModalRaise);
    }
}
```

- [ ] **Step 2: Add the shared IL helper the law uses.** Create `tools/RailCheck/Il.cs`:

```csharp
using System;
using System.Reflection;

namespace RailCheck
{
    /// <summary>The three IL questions the window-journal laws ask, in one place so seven laws do not each
    /// carry a copy. Same cross-assembly token resolve L492/L516 use: a callee in UnityEngine or
    /// Assembly-CSharp can never match a raw token compare inside the mod assembly.</summary>
    internal static class Il
    {
        internal static byte[] Body(MethodBase m)
        {
            try { return m?.GetMethodBody()?.GetILAsByteArray(); } catch { return null; }
        }

        internal static bool References(MethodBase m, MethodBase callee)
        {
            var il = Body(m);
            if (il == null || callee == null) return false;
            for (int i = 0; i + 4 <= il.Length; i++)
            {
                int token = BitConverter.ToInt32(il, i);
                if (token == callee.MetadataToken && m.Module == callee.Module) return true;
                MethodBase resolved = null;
                try { resolved = m.Module.ResolveMethod(token); } catch { }
                if (resolved != null && resolved.MetadataToken == callee.MetadataToken &&
                    resolved.Module == callee.Module) return true;
            }
            return false;
        }

        /// <summary>Does the method load this exact byte constant? ldc.i4.s = 0x1F, ldc.i4 = 0x20.</summary>
        internal static bool MentionsByte(MethodBase m, byte value)
        {
            var il = Body(m);
            if (il == null) return false;
            for (int i = 0; i + 1 < il.Length; i++)
                if (il[i] == 0x1F && il[i + 1] == value) return true;
            for (int i = 0; i + 4 < il.Length; i++)
                if (il[i] == 0x20 && BitConverter.ToInt32(il, i + 1) == value) return true;
            return false;
        }

        internal static bool MentionsAnyString(MethodBase m, string[] needles)
        {
            var il = Body(m);
            if (il == null) return false;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x72) continue; // ldstr
                string s = null;
                try { s = m.Module.ResolveString(BitConverter.ToInt32(il, i + 1)); } catch { }
                if (s == null) continue;
                foreach (var n in needles) if (s == n) return true;
            }
            return false;
        }
    }
}
```

- [ ] **Step 3: Register the law and run it RED.** In `tools/RailCheck/Program.cs`, immediately after the line `Add(laws, () => L516_AnOffScreenStripNeverVouchesForItsRows.Check());` (`:440`), add:

```csharp
            Add(laws, () => L520_TheOnlyPublicationIsTheQueryPostfix.Check());
```

Change `tools/RailCheck/Program.cs:488` from `private const int ExpectedLawRegistrations = 336;` to `= 337;` and amend the comment above it to name L520. Change `tools/law-count.txt` from `files=276` to `files=277`. In `L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs:98-102` change `LawExecutionIsValid(336, 336,` → `(337, 337,`, `LawExecutionIsValid(336, 335,` → `(337, 336,`, and `LawExecutionIsValid(336, 336, "wrong")` → `(337, 337, "wrong")`. Then run:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
dotnet run -c Debug --project tools/RailCheck
```

It aborts on the digest first:

```
RAILCHECK ABORTED — executed 337/337 committed law registration(s), identities=337, digest=<hex>. Zero, duplicate, partial, or unexpected execution cannot earn or update a verdict.
```

Paste `<hex>` into `Program.cs:494` `ExpectedExecutionIdentityDigest` and into the two digest literals in `L193…​.cs:99` and `:101`. Re-run. Now the law itself must be RED with:

```
L520 one-publisher: 2 method(s) broadcast a window raise surface (EventPopup.HostBroadcast, GeoModalMirror.HostBroadcast). Exactly one may — GeoModalMirror.HostBroadcast, reached from the QueryStateSwitch postfix. …
```

That RED is the point of this step. Do not proceed until you have seen it.

- [ ] **Step 4: Delete the second broadcast.** In `src/Rail/EventPopup.cs`, in `HostBroadcast` (`:336`), delete these four statements (the terminal broadcast at `:411-413` and its log line):

```csharp
                var env = SyncProtocol.EncodeEnvelope(SurfaceIds.GeoEventRaise, SyncKind.StateDelta, Encode(seq, p));
                var msg = new NetworkMessage(PacketType.SyncEnvelope, env);
                engine.BroadcastToAll(msg);
                MpLog.Log("[MP][events] HOST raised '" + p.EventId + "' seq=" + seq + " priority=" + p.Priority +
                          " site=" + (p.SiteRef == "" ? "none" : p.SiteRef) + " vehicle=" +
                          (p.VehicleRef == "" ? "none" : p.VehicleRef) + " titleLen=" + p.Title.Length +
                          " narrLen=" + p.Narrative.Length);
```

and replace them with a hand-off to the one publisher:

```csharp
                // ONE PUBLICATION PATH (L520). This method still BUILDS the event payload — that is
                // capture, and capture is right (§A.9) — but it no longer puts it on the wire. The
                // QueryStateSwitch postfix is the single seam that mints a journal position and publishes,
                // so a window that leaves here would be a window with no position: a bypass by definition.
                GeoModalMirror.HostBroadcastEventPayload(seq, Encode(seq, p));
                MpLog.Log("[MP][events] HOST raised '" + p.EventId + "' seq=" + seq + " priority=" + p.Priority +
                          " site=" + (p.SiteRef == "" ? "none" : p.SiteRef) + " vehicle=" +
                          (p.VehicleRef == "" ? "none" : p.VehicleRef) + " titleLen=" + p.Title.Length +
                          " narrLen=" + p.Narrative.Length);
```

`GeoModalMirror.HostBroadcastEventPayload` is added in Step 5 below; write both edits before building.

The MISSION UNICAST arm above it (`:388-409`) is NOT a second publication of the same window — it is the same window addressed to one peer instead of all — so it stays exactly as it is. It already `return`s before reaching this point.

- [ ] **Step 5: Add the hand-off on the one publisher.** In `src/Rail/GeoModalMirror.cs`, immediately before `internal static void HostBroadcastQueued(GeoscapeViewStateSwitchRequest request)` (`:366`), add:

```csharp
        /// <summary>THE EVENT FAMILY'S DOOR INTO THE ONE PUBLICATION PATH. EventPopup captures the host's
        /// native event raise and builds its payload; it used to also put that payload on the wire, which
        /// made two publishers for one queue (L520, R2). It now hands the built payload here, and this file
        /// remains the only place a window raise is broadcast.</summary>
        internal static void HostBroadcastEventPayload(uint seq, byte[] encoded)
        {
            var engine = LiveEngine();
            if (engine == null) return;
            engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope,
                SyncProtocol.EncodeEnvelope(SurfaceIds.GeoEventRaise, SyncKind.StateDelta, encoded)));
        }
```

`LiveEngine()` is not a new accessor — `GeoModalMirror.HostBroadcast` (`:379`) already obtains the live engine to send with. Read `src/Rail/GeoModalMirror.cs:379-440` first, reuse the exact expression it uses, and cite that `file:line` in the commit body. Do not invent an accessor.

- [ ] **Step 6: Build, run RailCheck GREEN.** Run:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
.\deploy.ps1 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'
dotnet run -c Debug --project tools/RailCheck
```

Expect `RAILCHECK GREEN — laws-run=337/337 law-violations=0`. Then run `pwsh -NoProfile -File tools/law-integrity.ps1`; it fails with `law identity set changed (digest <hex> != committed a266880416118cdda8b542f0a3f71dad93f62082b6f659069b03f8e4ac19f60f)`. Paste `<hex>` into `tools/law-integrity.ps1:50` and amend the comment above it to say `L520 ADDED (the only publication of a window is the QueryStateSwitch postfix) -- 336 -> 337 registrations`. Re-run: expect `laws: 277 file(s) + 60 inline = 337; 277 file registration(s); 0 unguarded (0 exempt)` and `law-integrity: OK`.

- [ ] **Step 7: Semantic mutation kill.** Re-add the deleted `engine.BroadcastToAll(msg);` block in `EventPopup.HostBroadcast` (a compile-valid `src/` mutation, restoring the real historical defect). Run `dotnet run -c Debug --project tools/RailCheck` and confirm it prints `L520 one-publisher: 2 method(s) broadcast a window raise surface (EventPopup.HostBroadcast, GeoModalMirror.HostBroadcast)…` and `RAILCHECK RED`. Revert the mutation, re-run, confirm `RAILCHECK GREEN — laws-run=337/337 law-violations=0`.

- [ ] **Step 8: Record the law and commit.** Append a row to the `## Law index` table in `docs/laws.md` (the table starts at `:58`, columns `| id | title | P | origin | evidence | guard |`), immediately after the `L516` row:

```
| L520 | THE ONLY HOST PUBLICATION OF A WINDOW IS THE `QueryStateSwitch` POSTFIX: exactly one method in the mod assembly broadcasts a `GeoEventRaise`/`GeoModalRaise` envelope, and no method reaches a native window-raise handler by reflection. Asserts ONE mechanism, never "the two agree" (R7, L496's mistake) | P1 P12 | incident | 2026-08-15 3-instance session: host queued research→event, both clients presented event→research 363 ms apart; `settled queue re-ordered by rail ordinal` appears zero times in all three logs. Two publication paths (`EventPopup.HostBroadcast` + `GeoModalMirror.HostBroadcastQueued`) keyed one queue. MUTATION KILL: re-adding `engine.BroadcastToAll(msg)` in `EventPopup.HostBroadcast` → `L520 one-publisher: 2 method(s)…`, RED; reverted → GREEN 337/337 | premise-changed + POSITIVE CONTROL |
```

Then:

```powershell
git add src/Rail/EventPopup.cs src/Rail/GeoModalMirror.cs tools/RailCheck/L520_TheOnlyPublicationIsTheQueryPostfix.cs tools/RailCheck/Il.cs tools/RailCheck/Program.cs tools/RailCheck/L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs tools/law-count.txt tools/law-integrity.ps1 docs/laws.md
git commit -m "refactor(windows): collapse the doubled host broadcast to the query postfix"
```

---

## Task 3: Delete the reflective native window replay (§8 item 2)

`ResearchSync` blocks the client's research completion and then serves the window itself by reflectively invoking two PRIVATE native handlers whose arguments it reconstructs and whose failures it swallows into `LogWarning`. §A.9 forbids it: CAPTURE-and-publish is the single strategy for every family.

**Files:**
- Modify: `src/Rail/ResearchSync.cs:86-89` (delete the two `MethodInfo` fields), `:373-400` (`PumpDeferredCompletions`)
- Test: `tools/RailCheck/L520_TheOnlyPublicationIsTheQueryPostfix.cs` arm (b) — already written in Task 2

- [ ] **Step 1: See arm (b) RED before touching anything.** Arm (b) was written in Task 2 and the repo went GREEN, which means it is currently NOT biting on `ResearchSync` — verify why before assuming the law is wrong. Run:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
dotnet run -c Debug --project tools/RailCheck
```

If it prints GREEN, arm (b)'s field scan is missing `ResearchSync`'s two `MethodInfo` fields because they are `private static readonly System.Reflection.MethodInfo` on a static class — confirm with the field names `ViewResearchCompletedMethod` and `LogResearchCompletedMethod` (`src/Rail/ResearchSync.cs:86-89`) and widen the arm's `forbidden` match to the field NAMES as well:

```csharp
            var forbidden = new[] { "OnFactionResearchCompleted", "Faction_ResearchCompleted",
                                    "OnGeoscapeEventRaised", "ViewResearchCompletedMethod",
                                    "LogResearchCompletedMethod" };
```

Re-run and confirm RailCheck now prints:

```
L520 no-reflective-raise: the mod still reaches a native window-raise handler by reflection (fields: ResearchSync.LogResearchCompletedMethod, ResearchSync.ViewResearchCompletedMethod; methods naming it: ResearchSync..cctor). …
RAILCHECK RED
```

- [ ] **Step 2: Delete the two `MethodInfo` fields.** In `src/Rail/ResearchSync.cs`, delete lines `:83-89`:

```csharp
        // Native research-complete PRESENTATION handlers (private), invoked directly on the client so the
        // completed modal + log line appear WITHOUT raising GeoFaction.ResearchCompletedEventHandler
        // (whose other subscribers — GeoscapeEventSystem, stats, pedia — are host-side logic/state).
        private static readonly System.Reflection.MethodInfo ViewResearchCompletedMethod =
            AccessTools.Method(typeof(PhoenixPoint.Geoscape.View.GeoscapeView), "OnFactionResearchCompleted");
        private static readonly System.Reflection.MethodInfo LogResearchCompletedMethod =
            AccessTools.Method(typeof(GeoscapeLog), "Faction_ResearchCompleted");
```

- [ ] **Step 3: Reduce `PumpDeferredCompletions` to a journal append.** Replace the whole body of `PumpDeferredCompletions` (`src/Rail/ResearchSync.cs:373-400`) with:

```csharp
        internal static void PumpDeferredCompletions(GeoLevelController geo)
        {
            if (_deferredCompleted.Count == 0) return;
            var research = geo == null || geo.ViewerFaction == null ? null : geo.ViewerFaction.Research;
            if (research == null || research.AllResearchesArray == null) return;
            var pending = _deferredCompleted.ToArray();
            _deferredCompleted.Clear();
            foreach (var id in pending)
            {
                // NO MOD-SERVED RAISE (§A.9, L520). The host raised this window natively, the capture
                // postfix minted its journal position and published it, and this peer presents it by
                // draining its cursor like every other family. Nothing here reflects into a private
                // native handler and nothing here reconstructs a native argument list.
                MpLog.Log("[MP][rail] ResearchSync completion '" + id + "' is journalled; presentation " +
                          "drains from the window journal cursor");
            }
        }
```

- [ ] **Step 4: Build and run GREEN.** Run:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
.\deploy.ps1 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'
dotnet run -c Debug --project tools/RailCheck
pwsh -NoProfile -File tools/law-integrity.ps1
```

Expect `RAILCHECK GREEN — laws-run=337/337 law-violations=0` and `law-integrity: OK`. If the build fails with `CS0246: AccessTools` or `CS0246: GeoscapeLog` now unused, remove only the `using` lines that became unused; do not remove a `using` another member still needs.

- [ ] **Step 5: Semantic mutation kill for arm (b).** Re-add the `ViewResearchCompletedMethod` field and one `ViewResearchCompletedMethod?.Invoke(geo.View, new object[] { el.Faction, el });` call inside the `foreach` of `PumpDeferredCompletions`. Run RailCheck, confirm `L520 no-reflective-raise: … fields: ResearchSync.ViewResearchCompletedMethod …` and `RAILCHECK RED`. Revert, re-run, confirm GREEN 337/337.

- [ ] **Step 6: Update the L520 row and commit.** In `docs/laws.md`, append to the L520 row's evidence cell (before the trailing ` | premise-changed + POSITIVE CONTROL |`):

```
 SECOND MUTATION KILL (arm b): re-adding `ViewResearchCompletedMethod` + its `Invoke` in `ResearchSync.PumpDeferredCompletions` → `L520 no-reflective-raise: … ResearchSync.ViewResearchCompletedMethod`, RED; reverted → GREEN 337/337
```

Then:

```powershell
git add src/Rail/ResearchSync.cs tools/RailCheck/L520_TheOnlyPublicationIsTheQueryPostfix.cs docs/laws.md
git commit -m "refactor(windows): delete the reflective native research-window replay"
```

---

## Task 4: Create the journal and mint a position at the one capture seam (§8 item 3)

**Files:**
- Create: `src/Rail/WindowJournal.cs`
- Create: `tools/RailCheck/L521_TheAppendIsScreenIndependent.cs`
- Modify: `src/Rail/GeoWindowCoverage.cs:663-676` (the postfix mints and appends)
- Modify: `src/Rail/GeoModalMirror.cs` (`Encode`/`Decode` carry `journalPos`), `src/Rail/EventPopup.cs` (same)
- Modify: `src/Rail/SurfaceIds.cs:82-83` (doc comments)
- Modify: `tools/RailSim/Scenarios.cs` (property C.1.1)
- Modify: `tools/RailCheck/Program.cs`, `L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs`, `tools/law-count.txt`, `tools/law-integrity.ps1`, `docs/laws.md`

- [ ] **Step 1: Write the journal.** Create `src/Rail/WindowJournal.cs`:

```csharp
using System;
using System.Collections.Generic;
using Multiplayer.Util;

namespace Multiplayer.Network.Sync
{
    /// <summary>How a window family's dismissal travels. DEFAULT IS LOCAL and an undeclared family IS
    /// local — a new family needs no code (§A.5).</summary>
    internal enum DismissScope : byte { Local = 0, Global = 1 }

    /// <summary>One pending window, as the journal holds it. <see cref="Payload"/> is the family's own
    /// already-encoded raise payload, carried verbatim — the journal orders windows, it does not know
    /// what is in them.</summary>
    internal sealed class JournalEntry
    {
        internal uint Pos;
        internal string Family;
        internal byte[] Payload;
    }

    /// <summary>
    /// THE WINDOW JOURNAL — one append-only, host-ordered stream of pending windows with a per-peer read
    /// cursor. Claiming a position is the ONLY way a window can exist (LMAX single-entrance, §A.1): any
    /// path that can present a window without one is a bypass and is closed by L520/L521.
    ///
    /// RETENTION IS THE WHOLE POLICY AND IT IS ONE RULE: A PEER'S ENTRY IS DELETED THE MOMENT THAT PEER
    /// HAS READ IT. No cap, no tail-trim, no time-based staleness, no LRU, no compaction pass. The
    /// backlog's length is bounded by what the local player has not looked at, which is a quantity the
    /// player controls. Deletion is PER-PEER; removing an entry a peer has NOT read needs the explicit
    /// host-minted void of <see cref="ApplyVoid"/> (§A.5) — an implicit per-peer timeout makes two peers
    /// diverge (FIX gap-fill, §2.5).
    ///
    /// NOT PERSISTED, EVER (§A.2b). A savegame contains zero journal entries: no codec, no
    /// SerializationData field, no restore path. A reconnecting peer receives only entries appended AFTER
    /// its reconnect and that is INTENDED — do not add a catch-up replay and do not log it as an anomaly.
    /// An AUTOSAVE always proceeds and whatever is unread at that moment is lost (§A.2c); the empty-journal
    /// gate covers PLAYER-INITIATED saves only and reads only <see cref="UnreadCount"/>, i.e. only this
    /// peer's own cursor. It is therefore not a quorum and must never become one.
    ///
    /// PURE C#, no Unity types and no engine call, so RailCheck laws and the RailSim harness both execute
    /// this class directly rather than asserting its shape.
    /// </summary>
    internal static class WindowJournal
    {
        /// <summary>RUNAWAY-RAISER CANARY ONLY (§A.6). Crossing it logs ONE error for the session and the
        /// append CONTINUES. It never drops an entry and never stops the append — it exists to make a
        /// raiser loop visible in a log, nothing more.</summary>
        internal const int RunawayCanaryAt = 4096;

        private static uint _nextPos;                       // host only: the single ordered stream
        private static readonly List<JournalEntry> _unread = new List<JournalEntry>();
        private static bool _canaryLogged;

        /// <summary>The declaration table, and the ONLY place a family's scope may be written. No
        /// `if (family == …)` anywhere else in the codebase (§A.5).</summary>
        private static readonly Dictionary<string, DismissScope> FamilyScope =
            new Dictionary<string, DismissScope>(StringComparer.Ordinal)
            {
                // The mission family is the one GLOBAL family: once ANYONE has acted on a mission the
                // decision to deploy is taken, so it is meaningless for the others to accept or refuse.
                { "UIStateGeoMissionBrief", DismissScope.Global },
                { "UIStateRosterDeployment", DismissScope.Global },
            };

        /// <summary>Undeclared ⇒ LOCAL. A new window family needs no code at all.</summary>
        internal static DismissScope ScopeOf(string family) =>
            family != null && FamilyScope.TryGetValue(family, out var scope) ? scope : DismissScope.Local;

        /// <summary>HOST ONLY: claim the next position in the one ordered stream. Monotonic and never
        /// reused within a session. Positions start at 1 so 0 can mean "no position", which is what makes
        /// an unpositioned window detectable rather than silently first.</summary>
        internal static uint MintHostPosition() => ++_nextPos;

        /// <summary>APPEND AT THE TAIL. Idempotent on <paramref name="pos"/>: a re-delivered raise is the
        /// same entry, not a second window. Ordered by position on insert, so a message that arrives late
        /// lands in the host's order rather than in arrival order — this is the whole of P1's fix.</summary>
        internal static void Append(uint pos, string family, byte[] payload)
        {
            if (pos == 0)
            {
                MpLog.LogError("[Multiplayer][windows] refused a journal append with no position — family '" +
                               (family ?? "<null>") + "'. Claiming a position is the only way a window can " +
                               "exist; this raise bypassed the mint seam");
                return;
            }
            for (int i = 0; i < _unread.Count; i++) if (_unread[i].Pos == pos) return;   // re-delivery
            int at = _unread.Count;
            while (at > 0 && _unread[at - 1].Pos > pos) at--;
            _unread.Insert(at, new JournalEntry { Pos = pos, Family = family, Payload = payload });
            if (_unread.Count >= RunawayCanaryAt && !_canaryLogged)
            {
                _canaryLogged = true;
                MpLog.LogError("[Multiplayer][windows] unread window backlog crossed " + RunawayCanaryAt +
                               " entries — a raiser is looping. NOTHING IS DROPPED and the append " +
                               "continues; this line exists to make the loop visible (once per session)");
            }
        }

        /// <summary>READ ⇒ DELETED. Takes the lowest unread position and removes it in the same call, so
        /// there is no window in which an entry is both read and still present.</summary>
        internal static bool TryRead(out JournalEntry entry)
        {
            if (_unread.Count == 0) { entry = null; return false; }
            entry = _unread[0];
            _unread.RemoveAt(0);
            return true;
        }

        /// <summary>Look at the head without consuming it — the drain gate needs to ask "is the next
        /// window this one?" without spending the read.</summary>
        internal static JournalEntry PeekHead() => _unread.Count == 0 ? null : _unread[0];

        /// <summary>THE HOST-MINTED VOID (§A.5). Removes an entry a peer has NOT read. Returns true when
        /// something was removed, so the caller knows whether it must also close an already-open copy.
        /// A CLIENT NEVER CALLS THIS OFF ITS OWN EVALUATION — only off a received void record.</summary>
        internal static bool ApplyVoid(uint pos)
        {
            for (int i = 0; i < _unread.Count; i++)
                if (_unread[i].Pos == pos) { _unread.RemoveAt(i); return true; }
            return false;
        }

        internal static int UnreadCount => _unread.Count;

        /// <summary>THE SAVE PREDICATE (§A.2b). Reads ONLY this peer's own cursor: no roster, no peer
        /// list, no message, no acknowledgement. An AFK peer blocks only their OWN save.</summary>
        internal static bool LocalJournalEmpty => _unread.Count == 0;

        /// <summary>Session teardown. Positions restart because the journal is session-scoped (§A.2b) —
        /// and because a reconnecting peer receives only what is appended after it rejoins.</summary>
        internal static void Reset()
        {
            _unread.Clear();
            _nextPos = 0;
            _canaryLogged = false;
        }
    }
}
```

- [ ] **Step 2: Write the harness property C.1.1 and watch it bite.** In `tools/RailSim/Scenarios.cs` add `using Multiplayer.Network.Sync;` to the usings, add to `All()`:

```csharp
            yield return Pair("every-peer-presents-in-the-same-order", EveryPeerPresentsInTheSameOrder);
```

and add the scenario plus its framing helper:

```csharp
        /// <summary>§C.1 property 1: EVERY PEER'S PRESENTATION ORDER IS IDENTICAL. The measured P1 shape,
        /// reproduced: the host raises research then event; the transport delivers them to each peer in
        /// whatever seeded order it likes (the field skew was 363 ms, longer than the old 150 ms settle);
        /// every peer must still present them in the host's order.</summary>
        private static IEnumerable<string> EveryPeerPresentsInTheSameOrder(int seed)
        {
            var histories = new List<List<string>>();
            for (int peer = 0; peer < 3; peer++)
            {
                var clock = new SimClock();
                var net = new SimNet(seed + peer, clock);
                WindowJournal.Reset();

                uint researchPos = WindowJournal.MintHostPosition();
                uint eventPos = WindowJournal.MintHostPosition();
                net.Send(peer, Frame(researchPos, "UIStateGeoModal"));
                net.Send(peer, Frame(eventPos, "UIStateGeoscapeEvent"));

                clock.Advance(1.0f);
                foreach (var msg in net.Drain())
                {
                    uint pos = BitConverter.ToUInt32(msg.Value, 0);
                    string family = System.Text.Encoding.UTF8.GetString(msg.Value, 4, msg.Value.Length - 4);
                    WindowJournal.Append(pos, family, msg.Value);
                }

                var presented = new List<string>();
                JournalEntry e;
                while (WindowJournal.TryRead(out e)) presented.Add(e.Family);
                histories.Add(presented);
            }
            WindowJournal.Reset();

            var reference = histories[0];
            for (int p = 1; p < histories.Count; p++)
                if (!histories[p].SequenceEqual(reference))
                    yield return "every-peer-presents-in-the-same-order: peer 0 presented [" +
                                 string.Join(",", reference) + "] and peer " + p + " presented [" +
                                 string.Join(",", histories[p]) + "]. This is P1 verbatim — the " +
                                 "2026-08-15 session had the host queue research→event while both clients " +
                                 "presented event→research, 363 ms apart, exactly one diff cycle.";

            if (reference.Count != 2 || reference[0] != "UIStateGeoModal")
                yield return "every-peer-presents-in-the-same-order: the presented order was [" +
                             string.Join(",", reference) + "], not [UIStateGeoModal,UIStateGeoscapeEvent]. " +
                             "The HOST was the wrong peer in the field, so the host's own order is " +
                             "asserted explicitly and not merely compared with the clients'.";
        }

        private static byte[] Frame(uint pos, string family)
        {
            var name = System.Text.Encoding.UTF8.GetBytes(family);
            var frame = new byte[4 + name.Length];
            Buffer.BlockCopy(BitConverter.GetBytes(pos), 0, frame, 0, 4);
            Buffer.BlockCopy(name, 0, frame, 4, name.Length);
            return frame;
        }
```

Run:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
dotnet run -c Debug --project tools/RailSim -- --seed 1
```

Without Step 1 this fails to compile with `CS0103: The name 'WindowJournal' does not exist in the current context`. With Step 1 it prints `RAILSIM GREEN — scenarios=2/2 failures=0 seed=1`. Prove the arm bites: temporarily replace the ordered insert in `WindowJournal.Append` with `_unread.Add(new JournalEntry { … });` and re-run — expect

```
  every-peer-presents-in-the-same-order: peer 0 presented [UIStateGeoModal,UIStateGeoscapeEvent] and peer 1 presented [UIStateGeoscapeEvent,UIStateGeoModal]. …
RAILSIM RED — scenarios=2 failures=1 seed=1
```

Restore the ordered insert and confirm GREEN again.

- [ ] **Step 3: Mint the position at the one capture seam.** In `src/Rail/GeoWindowCoverage.cs`, replace the body of `GeoWindowCoverageGate.Postfix` (currently `Announce` → `HostBroadcastQueued` → `TrimQueue`) with:

```csharp
        private static void Postfix(GeoscapeViewSwitchQuery __instance, GeoscapeViewStateSwitchRequest request)
        {
            if (!EventPopup.InSession) return;   // solo: there is no other peer to be out of sync with
            try
            {
                GeoWindowCoverage.Announce(request?.State?.GetType());
                // THE SINGLE ENTRANCE (§A.1, L520/L521). The host claims the next journal position HERE
                // and nowhere else, then publishes. This is a Harmony postfix on the queue itself, so it
                // is reachable with GeoLevel() == null — creation, queueing and publication never depend
                // on which screen the host is in (§A.4); only DISPLAY is postponed.
                if (GeoModalMirror.HostMayPublish())
                {
                    uint pos = WindowJournal.MintHostPosition();
                    string family = request?.State?.GetType().Name ?? "<unknown>";
                    GeoModalMirror.HostBroadcastQueued(request, pos);
                    WindowJournal.Append(pos, family, null); // host's own copy: the live request IS the payload
                }
            }
            catch (Exception ex) { MpLog.LogError("[MP][windows] coverage gate threw: " + ex); }
        }
```

`GeoModalMirror.HostMayPublish()` must be extracted from the existing host/session/`SyncApplyScope` guard inside `HostBroadcast` at `src/Rail/GeoModalMirror.cs:378-382` — read those lines, lift the exact expression into a new `internal static bool HostMayPublish()`, and have `HostBroadcast` call it so there is still one copy of the predicate. Cite `src/Rail/GeoModalMirror.cs:378-382` in the commit body. Do not invent a host test.

Delete the `GeoWindowCoverage.TrimQueue(__instance);` call — the trim itself dies in Task 8; removing its only caller here is safe and keeps this commit self-consistent because `TrimQueue` is `internal` and unreferenced until then. If the build complains `CS0169 'TrimQueue' is never used`, leave the method and delete it in Task 8 as planned.

- [ ] **Step 4: Carry the position on the wire.** In `src/Rail/GeoModalMirror.cs`:
  - change `internal static void HostBroadcastQueued(GeoscapeViewStateSwitchRequest request)` (`:366`) to `internal static void HostBroadcastQueued(GeoscapeViewStateSwitchRequest request, uint journalPos)`;
  - change `internal static void HostBroadcast(StateKind kind, ModalType modalType, object modalData, int priority)` (`:379`) to take a trailing `uint journalPos` and pass it through;
  - in the payload struct add `internal uint JournalPos;` and `internal string FamilyName;`;
  - in `Encode` append, as the LAST two writes: `w.Write(p.JournalPos);` and `MessageSerializer.WriteBoundedString(w, p.FamilyName ?? "");`
  - in `Decode` read them last in the same order.

Do the same two trailing fields in `src/Rail/EventPopup.cs`'s `Encode`/`Decode` for the `0xB6` payload. Trailing placement is deliberate: an older decoder stops before them rather than mis-reading an earlier field.

Update `src/Rail/SurfaceIds.cs:82` (`GeoEventRaise`) and `:83` (`GeoModalRaise`) doc comments to end with `…[journalPos:u32][family:string]`, and add to `GeoModalRaise`'s `kind` enumeration: `2 = Void — a host-minted void record; its payload is [seq:u32][kind:u8=2][journalPos:u32] and nothing else`.

- [ ] **Step 5: Append on the receiving side, and repaint.** In `src/Rail/GeoModalMirror.cs`'s `HandleEnvelope` arm for `SurfaceIds.GeoModalRaise` (`:567-591`) and in `EventPopup`'s `0xB6` arm, replace the immediate raise with an append:

```csharp
                // A CLIENT DOES NOT RAISE ON ARRIVAL — IT APPENDS. Presentation happens when this peer's
                // drain gate reaches this position, which is what makes the host's order the only order.
                WindowJournal.Append(p.JournalPos, p.FamilyName, payload);
                // REACTIVITY (hard mandate). The arrival changes replicated presentation state, so an
                // already-open screen must hear about it without the player leaving and re-entering. The
                // kindless arm at src/Rail/OpenUiRepaint.cs:728 is the right one here: a journal arrival
                // carries no rail path, and the drain gate re-runs from GeoscapeView.Update:1358 on the
                // next frame, so the window opens itself once the head matches.
                OpenUiRepaint.MarkDirty();
```

- [ ] **Step 6: Write L521.** Create `tools/RailCheck/L521_TheAppendIsScreenIndependent.cs`:

```csharp
using System;
using System.Collections.Generic;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L521 — THE APPEND IS SCREEN-INDEPENDENT AND THE HOST'S POSITION IS THE ONLY ORDER.
    ///
    /// Creation, queueing and publication of a window never depend on which screen a peer is in, and never
    /// on a GeoscapeView existing at all. Only DISPLAY is postponed (§A.4). The one structural dependency
    /// before this work was that publication rode the QueryStateSwitch postfix and therefore needed a live
    /// GeoscapeView (src/Rail/GeoWindowCoverage.cs:663-676): no view → no postfix → no publication. The
    /// journal exists with or without a view, so the append is where that dependency dies.
    ///
    /// EXECUTED, never asserted. Every arm calls the real WindowJournal with no game, no level and no
    /// view — which is the strongest possible statement of "this needs no screen", because the law is
    /// running in a process that has none.
    ///
    /// ROLES SEPARATED (§C.3). Arm (a) executes the HOST role (MintHostPosition) and arms (b)-(d) execute
    /// the CLIENT role (Append of positions this process did not mint), in separate journal generations
    /// divided by Reset. L507's blind spot was running both roles in one undivided process; P1 was a
    /// HOST-ONLY fault — two host windows sharing one back-filled ordinal — so it gets its own arm.
    ///
    /// Falsify (compile-valid src mutations, each named): `MintHostPosition() => 1;` → (a); replace the
    /// ordered insert in Append with `_unread.Add` → (b); delete the `pos == 0` refusal → (c); make
    /// TryRead peek instead of remove → (d).
    /// </summary>
    internal static class L521_TheAppendIsScreenIndependent
    {
        internal static IEnumerable<string> Check()
        {
            WindowJournal.Reset();

            // (a) HOST ROLE: two windows minted back to back get two DIFFERENT, INCREASING positions.
            uint first = WindowJournal.MintHostPosition();
            uint second = WindowJournal.MintHostPosition();
            if (first == 0 || second <= first)
                yield return "L521 host-positions-tie: two windows minted in a row got positions " + first +
                             " and " + second + ". They must be distinct and increasing. A tie is exactly " +
                             "P1: on 2026-08-15 the host's research and event shared one back-filled " +
                             "RailOrdinal and fell back to insert order, and both clients — the CORRECT " +
                             "peers — presented the other way round.";

            // (b) CLIENT ROLE, fresh generation: positions arriving OUT OF ORDER present IN order.
            WindowJournal.Reset();
            WindowJournal.Append(2, "UIStateGeoscapeEvent", new byte[] { 2 });
            WindowJournal.Append(1, "UIStateGeoModal", new byte[] { 1 });
            var order = new List<string>();
            JournalEntry entry;
            while (WindowJournal.TryRead(out entry)) order.Add(entry.Family);
            if (order.Count != 2 || order[0] != "UIStateGeoModal")
                yield return "L521 arrival-order-wins: entries appended as 2 then 1 presented as [" +
                             string.Join(",", order.ToArray()) + "]. The HOST's position decides, never " +
                             "arrival — the measured inter-channel skew was 363 ms, which no client-side " +
                             "settle can be tuned to cover.";

            // (c) NO POSITION, NO WINDOW — the LMAX single-entrance property, executed.
            WindowJournal.Reset();
            WindowJournal.Append(0, "UIStateGeoModal", new byte[] { 0 });
            if (WindowJournal.UnreadCount != 0)
                yield return "L521 unpositioned-window-accepted: a window with position 0 entered the " +
                             "journal. Claiming a position is the ONLY way a window can exist; an entry " +
                             "with no position sorts first by accident and is a bypass by construction.";

            // (d) READ IS DELETE — the whole retention policy, executed rather than described.
            WindowJournal.Reset();
            WindowJournal.Append(1, "UIStateGeoModal", new byte[] { 1 });
            JournalEntry read;
            WindowJournal.TryRead(out read);
            if (WindowJournal.UnreadCount != 0 || !WindowJournal.LocalJournalEmpty)
                yield return "L521 read-did-not-delete: after reading the only entry the journal still " +
                             "reports " + WindowJournal.UnreadCount + " unread. Read ⇒ deleted is the " +
                             "entire retention policy — there is no cap, no trim and no staleness pass, " +
                             "so an entry that survives its own read is an unbounded leak.";

            // POSITIVE CONTROL: the journal must be capable of holding something, or every arm above is a
            // statement about an object that does nothing.
            WindowJournal.Reset();
            WindowJournal.Append(7, "UIStateGeoModal", new byte[] { 7 });
            if (WindowJournal.UnreadCount != 1 || WindowJournal.LocalJournalEmpty)
                yield return "L521 positive-control: a valid append did not land, so every arm above " +
                             "passed against an inert journal and proved nothing.";
            WindowJournal.Reset();
        }
    }
}
```

- [ ] **Step 7: Register L521, refresh both digests, run everything GREEN.** In `tools/RailCheck/Program.cs` add `Add(laws, () => L521_TheAppendIsScreenIndependent.Check());` after the L520 registration. Set `ExpectedLawRegistrations = 338;` and amend its comment to name L521 (337 → 338). Set `tools/law-count.txt` to `files=278`. In `L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs:98-102` change `(337, 337,` → `(338, 338,`, `(337, 336,` → `(338, 337,`, `(337, 337, "wrong")` → `(338, 338, "wrong")`. Run:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
.\deploy.ps1 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'
dotnet run -c Debug --project tools/RailCheck
```

Copy the `digest=<hex>` from the `RAILCHECK ABORTED` line into `Program.cs:494` and into the two `L193` digest string literals at `:99` and `:101`; re-run for `RAILCHECK GREEN — laws-run=338/338 law-violations=0`. Then run `pwsh -NoProfile -File tools/law-integrity.ps1`, paste the digest from its `law identity set changed (digest <hex> != committed …)` line into `tools/law-integrity.ps1:50`, amend the comment above it to `L521 ADDED (the append is screen-independent) -- 337 -> 338 registrations`, and re-run for `laws: 278 file(s) + 60 inline = 338` and `law-integrity: OK`. Finally `dotnet run -c Debug --project tools/RailSim -- --seed 1` for `RAILSIM GREEN — scenarios=2/2 failures=0 seed=1`.

- [ ] **Step 8: Semantic mutation kill.** Change `WindowJournal.MintHostPosition()` to `=> 1;`. Run RailCheck: expect `L521 host-positions-tie: two windows minted in a row got positions 1 and 1.` and `RAILCHECK RED — 1 executable law violation(s)`. Revert, re-run, confirm `RAILCHECK GREEN — laws-run=338/338 law-violations=0`.

- [ ] **Step 9: Record and commit.** Add to `docs/laws.md` after the L520 row:

```
| L521 | THE APPEND IS SCREEN-INDEPENDENT AND THE HOST'S POSITION IS THE ONLY ORDER: two windows minted in a row get distinct increasing positions; entries arriving 2-then-1 present 1-then-2; a position-0 window is refused; a read entry is deleted. Every arm EXECUTES `WindowJournal` in a process with no game, no level and no view, host role and client role in separate generations (§C.3) | P1 P12 P13 | incident | 2026-08-15 3-instance session: `RailOrdinal.Mint` back-filled the whole pending list with ONE ordinal so the host's research and event tied to insert order; both clients presented the opposite order 363 ms later, and the 150 ms `SettleSeconds` was shorter than the skew by construction. MUTATION KILL: `MintHostPosition() => 1;` → `L521 host-positions-tie: … positions 1 and 1`, RED; reverted → GREEN 338/338 | POSITIVE CONTROL |
```

Then:

```powershell
git add src/Rail/WindowJournal.cs src/Rail/GeoWindowCoverage.cs src/Rail/GeoModalMirror.cs src/Rail/EventPopup.cs src/Rail/SurfaceIds.cs tools/RailSim/Scenarios.cs tools/RailCheck/L521_TheAppendIsScreenIndependent.cs tools/RailCheck/Program.cs tools/RailCheck/L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs tools/law-count.txt tools/law-integrity.ps1 docs/laws.md
git commit -m "feat(windows): mint a host journal position at the one capture seam"
```

The commit body MUST state, per §A.4: where the append now happens (`src/Rail/GeoWindowCoverage.cs`, `GeoWindowCoverageGate.Postfix`), that it is reachable with `GeoLevel() == null` (`src/Rail/GenericApplier.cs:130-136`), the `file:line` of the host predicate lifted in Step 3, and the repaint seam from Step 5.

---

## Task 5: The client reconciles to the published order and never sorts (§8 item 4)

**Files:**
- Create: `tools/RailCheck/L522_AClientNeverSortsAWindowQueue.cs`
- Create: `src/Rail/WindowGap.cs` (the armed self-release timer used by the drain gate)
- Modify: `src/Rail/WindowOrder.cs` — delete `SettleSeconds`, `Compare`, `SettleExpired`, `Reorder`, `OrdinalOf`, `QueuedAt`, `OrderKey`, `_stamps`, `Stamp`, `StampAt`; rewrite the tail of `ReadyToDequeue`
- Modify: `src/Rail/ReplenishSync.cs:135-156` (the `QueueRankPatch` call into `WindowOrder.Stamp`)
- Modify: `tools/RailCheck/Program.cs`, `L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs`, `tools/law-count.txt`, `tools/law-integrity.ps1`, `docs/laws.md`

- [ ] **Step 1: Write the gap timer the new gate needs.** Create `src/Rail/WindowGap.cs`:

```csharp
using System.Collections.Generic;
using System.Diagnostics;
using Multiplayer.Util;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// A GAP MUST NEVER BE PERMANENT, AND IT MUST NEVER BE A WAIT ON A HUMAN.
    ///
    /// The journal head can name a window whose native raise this peer has not produced yet — the payload
    /// arrived before the local queue caught up. The gate holds, but only for a bounded, armed interval
    /// that ends BY ITSELF (§7.6: waiting on work that ends by itself is allowed; waiting on another
    /// human's ACTION never is). The authoritative resolution is still the host-minted void record of
    /// §A.5 — without an explicit void, two peers time out differently and diverge (FIX gap-fill, §2.5).
    /// This timer is the safety net under that void, not a replacement for it.
    ///
    /// A BCL Stopwatch deliberately, for the same reason RailMeta's digest clock is one
    /// (src/Rail/RailMeta.cs:1449-1453): UnityEngine.Time is an ECall that throws outside the player, and
    /// RailCheck and RailSim both execute this class in-process.
    /// </summary>
    internal static class WindowGap
    {
        /// <summary>How long a gap may hold the drain. Generous against the measured 363 ms inter-channel
        /// skew and still far under a player's patience.</summary>
        internal const double SelfReleaseSeconds = 2.0;

        private static readonly Dictionary<uint, double> _armedAt = new Dictionary<uint, double>();
        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        private static readonly HashSet<uint> _logged = new HashSet<uint>();

        /// <summary>TRUE = the gap has outlived its armed interval and the drain proceeds anyway. Arms the
        /// timer on first sight of <paramref name="pos"/>.</summary>
        internal static bool SelfReleased(uint pos) => SelfReleasedAt(pos, _clock.Elapsed.TotalSeconds);

        /// <summary>The decision itself, with the clock INJECTED so a law and the harness execute the real
        /// one rather than reading its IL.</summary>
        internal static bool SelfReleasedAt(uint pos, double now)
        {
            double armed;
            if (!_armedAt.TryGetValue(pos, out armed)) { _armedAt[pos] = now; return false; }
            if (now - armed < SelfReleaseSeconds) return false;
            if (_logged.Add(pos))
                MpLog.LogWarning("[Multiplayer][windows] journal position " + pos + " self-released after " +
                                 SelfReleaseSeconds + "s — its raise never reached this peer's native " +
                                 "queue. The authoritative resolution is a host-minted void; this timer " +
                                 "exists so a gap can never be permanent");
            return true;
        }

        /// <summary>A position that was voided or presented no longer needs a timer.</summary>
        internal static void Forget(uint pos) { _armedAt.Remove(pos); _logged.Remove(pos); }

        internal static void Reset() { _armedAt.Clear(); _logged.Clear(); }
    }
}
```

- [ ] **Step 2: Write L522.** Create `tools/RailCheck/L522_AClientNeverSortsAWindowQueue.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L522 — A CLIENT NEVER SORTS A WINDOW QUEUE. The host publishes an ORDERED stream; the client
    /// reconciles its own queue to it. It does not invent a key and it does not compare two windows.
    ///
    /// This is the Kafka single-partition constraint (§2.5): a total order exists only within ONE
    /// partition, and two order keys IS the bug. The client half of the fix is not "sort with the right
    /// key", it is "do not sort".
    ///
    /// ARMS:
    ///   (a) client-comparator-survives / client-resort-survives — WindowOrder exposes neither Compare nor
    ///       Reorder.
    ///   (b) settle-survives — WindowOrder exposes neither SettleSeconds nor SettleExpired. The settle was
    ///       a 150 ms hold-and-reorder and the measured skew was 363 ms; beside the journal it is a second
    ///       ordering system, which is the exact mistake §A.7 deletes.
    ///   (c) head-is-not-the-lowest-position — EXECUTED, CLIENT ROLE ONLY: no MintHostPosition call appears
    ///       in this arm, so a host-side fault cannot make it pass (§C.3).
    ///   (d) gap-never-permanent — EXECUTED with the clock INJECTED: a gap holds, then self-releases. A
    ///       gate that could hold forever would be a wait on another peer, which §7.6 forbids outright.
    ///
    /// Falsify (compile-valid src mutations, each named): re-add
    /// `internal static int Compare(int, uint, int, uint)` to WindowOrder → (a); re-add
    /// `internal const float SettleSeconds = 0.15f;` → (b); make `WindowJournal.Append` use `_unread.Add`
    /// → (c); make `WindowGap.SelfReleasedAt` return false unconditionally → (d).
    /// </summary>
    internal static class L522_AClientNeverSortsAWindowQueue
    {
        internal static IEnumerable<string> Check()
        {
            var order = typeof(WindowOrder);
            const BindingFlags Any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.DeclaredOnly;

            if (order.GetMethod("ReadyToDequeue", Any) == null)
            {
                yield return "L522 premise-changed: WindowOrder.ReadyToDequeue did not resolve, so the " +
                             "drain gate this law is about has moved. Re-point it before believing the " +
                             "verdict.";
                yield break;
            }

            if (order.GetMethod("Compare", Any) != null)
                yield return "L522 client-comparator-survives: WindowOrder.Compare still exists. The host " +
                             "publishes an ordered stream and the client reconciles to it — a client that " +
                             "can compare two windows is a second ordering authority, and two order keys " +
                             "on one stream is the bug this work removes.";

            if (order.GetMethod("Reorder", Any) != null)
                yield return "L522 client-resort-survives: WindowOrder.Reorder still exists. Re-sorting a " +
                             "settled queue is the device that never once ran in the field — the string " +
                             "'settled queue re-ordered by rail ordinal' appears ZERO times across all " +
                             "three complete logs of the 2026-08-15 session.";

            if (order.GetField("SettleSeconds", Any) != null || order.GetMethod("SettleExpired", Any) != null)
                yield return "L522 settle-survives: WindowOrder still carries the settle timer. It was a " +
                             "150 ms hold-and-reorder against a measured 363 ms skew — it cannot be tuned " +
                             "into correctness, and beside the journal it is a second ordering system.";

            // (c) CLIENT ROLE ONLY.
            WindowJournal.Reset();
            WindowJournal.Append(9, "UIStateGeoscapeEvent", new byte[] { 9 });
            WindowJournal.Append(4, "UIStateGeoModal", new byte[] { 4 });
            WindowJournal.Append(7, "UIStateAssetDeployment", new byte[] { 7 });
            var head = WindowJournal.PeekHead();
            if (head == null || head.Pos != 4)
                yield return "L522 head-is-not-the-lowest-position: after appending 9, 4, 7 the head was " +
                             (head == null ? "<null>" : head.Pos.ToString()) + ", not 4. The client's " +
                             "entire reconciliation is 'the head is the lowest unread HOST position'; if " +
                             "that is false the client is ordering by arrival again.";

            int drained = 0;
            JournalEntry e;
            while (WindowJournal.TryRead(out e)) drained++;
            if (drained != 3)
                yield return "L522 positive-control: the journal yielded " + drained + " of 3 appended " +
                             "entries, so arm (c) inspected a queue that does not hold windows and would " +
                             "report a correct head against an empty list.";
            WindowJournal.Reset();

            // (d) THE GAP ENDS BY ITSELF.
            WindowGap.Reset();
            if (WindowGap.SelfReleasedAt(42, 100.0))
                yield return "L522 gap-released-immediately: a gap released on first sight. It must hold " +
                             "long enough for the raise to arrive, or the host's order is being abandoned " +
                             "the moment it is inconvenient.";
            if (WindowGap.SelfReleasedAt(42, 100.0 + WindowGap.SelfReleaseSeconds - 0.01))
                yield return "L522 gap-released-early: a gap released before its armed interval elapsed.";
            if (!WindowGap.SelfReleasedAt(42, 100.0 + WindowGap.SelfReleaseSeconds + 0.01))
                yield return "L522 gap-never-permanent: a gap did NOT self-release after its armed " +
                             "interval. A drain gate that can hold forever is a wait on another peer, " +
                             "which the no-blockers rule forbids outright — one player must be able to " +
                             "drive the whole game while every other peer is AFK.";
            WindowGap.Reset();
        }
    }
}
```

- [ ] **Step 3: Register L522 and see it RED.** Add `Add(laws, () => L522_AClientNeverSortsAWindowQueue.Check());` to `Program.cs`, set `ExpectedLawRegistrations = 339;`, `tools/law-count.txt` to `files=279`, and the three `L193` counts to `(339, 339,` / `(339, 338,` / `(339, 339, "wrong")`. Refresh the execution digest from the `RAILCHECK ABORTED` line, then run:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
.\deploy.ps1 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'
dotnet run -c Debug --project tools/RailCheck
```

Expect RED with all three of:

```
L522 client-comparator-survives: WindowOrder.Compare still exists. …
L522 client-resort-survives: WindowOrder.Reorder still exists. …
L522 settle-survives: WindowOrder still carries the settle timer. …
```

- [ ] **Step 4: Delete the comparator, the re-sort and the settle.** In `src/Rail/WindowOrder.cs` delete these members outright: `SettleSeconds` (`:79`), the `OrderKey` class and `_stamps` `ConditionalWeakTable` (`:105-106`), `Stamp` (`:153-158`), `StampAt` (`:163-183`), `Compare` (`:186-187`), `SettleExpired` (`:192`), `Reorder` (`:365`, whole method), `OrdinalOf` (`:544-545`), `QueuedAt` (`:547-548`). In `src/Rail/ReplenishSync.cs:135-156` delete only the `WindowOrder.Stamp(request);` call from `QueueRankPatch` — read the method first and keep every other statement it has.

- [ ] **Step 5: Make the drain gate consult the journal head.** In `WindowOrder.ReadyToDequeue`, replace the settle-and-reorder tail (the `if (!SettleExpired(QueuedAt(pending[0]), Time.realtimeSinceStartup)) return false;` line and the whole `if (Reorder(pending, OrdinalOf)) { … }` block) with:

```csharp
                // THE HOST'S ORDER, AND NOTHING ELSE. The journal head is the next window every peer owes
                // the player; this peer drains only when its own native queue's head IS that window. No
                // key is invented here, nothing is compared and nothing is re-sorted (L522).
                var head = WindowJournal.PeekHead();
                if (head == null) return true;   // nothing journalled: the native queue drains as vanilla
                var localHead = pending[0].State == null ? null : pending[0].State.GetType().Name;
                if (string.Equals(localHead, head.Family, StringComparison.Ordinal))
                {
                    WindowGap.Forget(head.Pos);
                    return true;
                }
                // NOT A WAIT ON A HUMAN (§7.6). The armed interval ends by itself and the authoritative
                // resolution is the host-minted void of §A.5; nothing here waits for a peer to ACT.
                return WindowGap.SelfReleased(head.Pos);
```

- [ ] **Step 6: Build, run everything, kill the mutation.** Run:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
.\deploy.ps1 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'
dotnet run -c Debug --project tools/RailCheck
pwsh -NoProfile -File tools/law-integrity.ps1
dotnet run -c Debug --project tools/RailSim -- --seed 1
```

Expect `RAILCHECK GREEN — laws-run=339/339 law-violations=0` (after pasting the integrity digest into `tools/law-integrity.ps1:50` and amending its comment), `laws: 279 file(s) + 60 inline = 339`, `law-integrity: OK`, and `RAILSIM GREEN — scenarios=2/2 failures=0 seed=1`. Then apply the mutation: re-add

```csharp
        internal static int Compare(int priorityA, uint ordinalA, int priorityB, uint ordinalB) =>
            priorityA != priorityB ? priorityB.CompareTo(priorityA) : ordinalA.CompareTo(ordinalB);
```

to `WindowOrder`. Run RailCheck, confirm `L522 client-comparator-survives: WindowOrder.Compare still exists.` and `RAILCHECK RED`. Revert, re-run, confirm GREEN 339/339.

- [ ] **Step 7: Record and commit.** Add to `docs/laws.md` after the L521 row:

```
| L522 | A CLIENT NEVER SORTS A WINDOW QUEUE: `WindowOrder.Compare`, `Reorder`, `SettleSeconds` and `SettleExpired` are gone; the journal head is the lowest unread HOST position regardless of arrival order (arm c executes the CLIENT role only, with no `MintHostPosition` call, §C.3); and a gap holds then SELF-RELEASES on an injected clock, so the drain can never wait on another human | P1 P12 P13 | incident | 2026-08-15: `settled queue re-ordered by rail ordinal` appears ZERO times in all three complete logs — no peer ever re-sorted, every peer drained in local insert order, and the 150 ms `SettleSeconds` was shorter than the measured 363 ms inter-channel skew by construction. MUTATION KILL: re-adding `WindowOrder.Compare` → `L522 client-comparator-survives`, RED; reverted → GREEN 339/339 | premise-changed + POSITIVE CONTROL |
```

Then:

```powershell
git add src/Rail/WindowOrder.cs src/Rail/WindowGap.cs src/Rail/ReplenishSync.cs tools/RailCheck/L522_AClientNeverSortsAWindowQueue.cs tools/RailCheck/Program.cs tools/RailCheck/L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs tools/law-count.txt tools/law-integrity.ps1 docs/laws.md
git commit -m "refactor(windows): reconcile the client to the host order and delete the settle sort"
```

Field verification the spec asks for on this item: re-run the P1 repro on three instances (host queues research→event) and report the presentation lines from all three logs, which must now agree.

---

## Task 6: Delete the second ordering system (§8 item 5). ONLY after Tasks 4 and 5 are green (R1)

The DurableInbox surgery added a SECOND complete ordering system (ledger + `HostOrderKey` + suspend/resume preemption) beside `RailOrdinal` + settle + reorder, and wired both into one drain. Task 5 removed the first. This removes the second, and `DurableInbox` survives ONLY as answer-exactly-once semantics.

**Files:**
- Create: `tools/RailCheck/L523_DurableIsAnswerOnceNotOrdering.cs`
- Delete: `tools/RailCheck/L496_*.cs` (it legitimised a duplicate gate — R7)
- Modify: `src/Rail/DurableInboxModel.cs:97` (`HostOrderKey` and every read of it)
- Modify: `src/Rail/WindowOrder.cs:105-141` (`_durable`, `BindDurable`, `TryGetDurable`, `DurablePriorityHead`), `:496-499`
- Modify: `src/Rail/EventPopup.cs:993` (`WindowOrder.BindDurable(request, durableOccurrence);`)
- Modify: `src/Rail/WindowQueueSync.cs` (`TryDurablePriorityPreemption` and the suspend/resume path)
- Modify: `src/Rail/DurableWindowRegistry.cs` (`PriorityOf`), `src/Rail/DurableInboxEngine.cs`, `DurableInboxState.cs`, `DurableInboxStore.cs`, `DurableInboxCodec.cs` (ordering members only)
- Modify: `src/Rail/RailOrdinal.cs:66-73` (`ForNewWindow` + the provisional back-fill)
- Modify: `tools/RailCheck/Program.cs`, `L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs`, `tools/law-count.txt`, `tools/law-integrity.ps1`, `docs/laws.md`

- [ ] **Step 1: Write L523.** Create `tools/RailCheck/L523_DurableIsAnswerOnceNotOrdering.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L523 — DURABLE MEANS ANSWERED EXACTLY ONCE. IT DOES NOT MEAN ORDERED.
    ///
    /// The DurableInbox surgery (93fed1a, 55e5f82, ae2099d, adc31a0, d193852) added a SECOND complete
    /// ordering system — ledger + HostOrderKey + suspend/resume preemption — BESIDE RailOrdinal + settle +
    /// reorder, and wired both into the same drain (WindowOrder.ReadyToDequeue). Neither was
    /// authoritative, so which one decided depended on whether a request happened to be durable-bound.
    /// The journal is now the one ordered stream (§A.1); durability keeps answer-once and nothing else.
    ///
    /// This asserts ONE mechanism, never "the mechanisms agree". L496 was written the second way — it
    /// LEGITIMISED a duplicate presentation gate — and is retired in the same commit as this law (R7).
    ///
    /// ARMS:
    ///   (a) host-order-key-survives — DurableInboxModel exposes no HostOrderKey member.
    ///   (b) durable-priority-head-survives — WindowOrder exposes no DurablePriorityHead / BindDurable /
    ///       TryGetDurable, and WindowQueueSync exposes no TryDurablePriorityPreemption.
    ///   (c) window-backfill-survives — RailOrdinal exposes no ForNewWindow: the provisional back-fill
    ///       that gave the host's research and event ONE shared ordinal is the mechanism of P1 and it is
    ///       gone as a WINDOW ordering authority. RailOrdinal itself stays for its other users, so this
    ///       arm names the window entry point and not the type.
    ///   (d) answer-once-survives — POSITIVE CONTROL in the strict sense: the ledger must STILL be there.
    ///       A law that only deletes things passes trivially once the whole subsystem is removed, and
    ///       removing answer-once would let one occurrence be answered twice across a reload.
    ///
    /// ROLES SEPARATED (§C.3): every arm is a statement about which members exist in the shipped assembly,
    /// which is role-independent — a host-only ordering key is as visible as a client-only one.
    ///
    /// Falsify (compile-valid src mutations, each named): re-add `internal uint HostOrderKey;` to
    /// DurableInboxModel's entry type → (a); re-add `WindowOrder.DurablePriorityHead` → (b); re-add
    /// `RailOrdinal.ForNewWindow()` → (c); delete the ledger type → (d).
    /// </summary>
    internal static class L523_DurableIsAnswerOnceNotOrdering
    {
        internal static IEnumerable<string> Check()
        {
            var asm = typeof(WindowJournal).Assembly;
            const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public |
                                     BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            var model = asm.GetTypes().FirstOrDefault(t => t.Name == "DurableInboxModel");
            if (model == null)
            {
                yield return "L523 premise-changed: DurableInboxModel did not resolve. If durability was " +
                             "deleted outright rather than reduced to answer-once, that is a different " +
                             "change and this law must be re-pointed before its verdict means anything.";
                yield break;
            }

            var orderKeyHolders = asm.GetTypes()
                .SelectMany(t => t.GetFields(Any).Select(f => t.Name + "." + f.Name)
                                  .Concat(t.GetProperties(Any).Select(p => t.Name + "." + p.Name)))
                .Where(n => n.EndsWith(".HostOrderKey", StringComparison.Ordinal))
                .OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (orderKeyHolders.Count > 0)
                yield return "L523 host-order-key-survives: " + string.Join(", ", orderKeyHolders) +
                             " still exist(s). HostOrderKey was the second ordering system's key; with " +
                             "the journal authoritative it is a second order on one stream, which is the " +
                             "Kafka single-partition violation §2.5 names as the bug itself.";

            var order = typeof(WindowOrder);
            foreach (var gone in new[] { "DurablePriorityHead", "BindDurable", "TryGetDurable" })
                if (order.GetMethod(gone, Any) != null)
                    yield return "L523 durable-priority-head-survives: WindowOrder." + gone + " still " +
                                 "exists. The drain consulted DurablePriorityHead AND Reorder, so which " +
                                 "system decided depended on whether a request happened to be " +
                                 "durable-bound — the definition of no authority at all.";

            var queueSync = asm.GetTypes().FirstOrDefault(t => t.Name == "WindowQueueSync");
            if (queueSync != null && queueSync.GetMethod("TryDurablePriorityPreemption", Any) != null)
                yield return "L523 preemption-survives: WindowQueueSync.TryDurablePriorityPreemption still " +
                             "exists. Suspend/resume preemption is an ordering device — it decides which " +
                             "window is in front — and there is exactly one of those now.";

            var ordinal = asm.GetTypes().FirstOrDefault(t => t.Name == "RailOrdinal");
            if (ordinal != null && ordinal.GetMethod("ForNewWindow", Any) != null)
                yield return "L523 window-backfill-survives: RailOrdinal.ForNewWindow still exists. Its " +
                             "Mint() back-filled the WHOLE pending provisional list with ONE ordinal, so " +
                             "the host's research and the host's event collided and tied to insert order " +
                             "— that is the measured mechanism of P1, on 2026-08-15, in a 3-instance " +
                             "session whose three logs are complete to shutdown.";

            // (d) POSITIVE CONTROL — answer-once must SURVIVE. Without this arm the law is satisfied by
            // deleting durability entirely, which would let one occurrence be answered twice on reload.
            bool ledgerAlive = asm.GetTypes().Any(t => t.Name == "DurableInboxStore") &&
                               asm.GetTypes().Any(t => t.Name == "OccurrenceId");
            if (!ledgerAlive)
                yield return "L523 positive-control: the durable ledger (DurableInboxStore / OccurrenceId) " +
                             "is gone. This law forbids durability from ORDERING; it does not authorise " +
                             "deleting answer-exactly-once, and every arm above would pass vacuously " +
                             "against an assembly with no durability at all.";
        }
    }
}
```

- [ ] **Step 2: Register L523, retire L496, run RED.** Add `Add(laws, () => L523_DurableIsAnswerOnceNotOrdering.Check());` to `Program.cs` and DELETE the `Add(laws, () => L496_… .Check());` registration line. Delete the file `tools/RailCheck/L496_*.cs` (use `git rm`). `files=` stays at `279` (one added, one removed) and `ExpectedLawRegistrations` stays `339`, but the two DIGESTS both change because the identity SET changed. Run:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
.\deploy.ps1 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'
dotnet run -c Debug --project tools/RailCheck
```

Paste the `digest=<hex>` from `RAILCHECK ABORTED` into `Program.cs:494` and the two `L193` digest literals, re-run, and confirm the law is RED with `L523 host-order-key-survives: DurableInboxLedgerEntry.HostOrderKey still exist(s).` and `L523 window-backfill-survives: RailOrdinal.ForNewWindow still exists.`

- [ ] **Step 3: Delete `HostOrderKey`.** In `src/Rail/DurableInboxModel.cs` delete the `HostOrderKey` field at `:97` and every read of it. `WindowOrder.DurablePriorityHead` reads it in its `OrderByDescending(...).ThenBy(x => x.HostOrderKey)` chain — that whole method goes in Step 4. Search the assembly for remaining reads with `Grep` on `HostOrderKey` and delete each; every one is an ordering read by construction.

- [ ] **Step 4: Delete the durable ordering members from `WindowOrder`.** In `src/Rail/WindowOrder.cs` delete: the `DurableBinding` class and `_durable` `ConditionalWeakTable` (`:107-109`), `BindDurable` (`:111-116`), `TryGetDurable` (`:118-123`), `DurablePriorityHead` (`:128-141`), and the `DurablePriorityHead`/preemption consultation inside `ReadyToDequeue` (`:496-499`). In `src/Rail/EventPopup.cs` delete the two `WindowOrder.BindDurable(...)` calls (`:384` in `HostBroadcast` and `:993` after `q.QueryStateSwitch(request)`).

- [ ] **Step 5: Delete the preemption engine.** In `src/Rail/WindowQueueSync.cs` delete `TryDurablePriorityPreemption` and the suspend/resume path it drives. KEEP `TrackDurableNativeCarrier` and `ConfirmDurableNativeOpen` — those are answer-once bookkeeping, not ordering. In `src/Rail/DurableWindowRegistry.cs` delete `PriorityOf` (its only caller was `DurablePriorityHead`). In `DurableInboxEngine.cs`, `DurableInboxState.cs`, `DurableInboxStore.cs` and `DurableInboxCodec.cs` delete only members whose job is ordering (anything reading or writing `HostOrderKey`, anything sorting occurrences); leave the occurrence ledger, its persistence and its answer-once checks alone.

- [ ] **Step 6: Delete the window back-fill.** In `src/Rail/RailOrdinal.cs` delete `ForNewWindow` and the provisional-list back-fill inside `Mint()` (`:66-73`). `Mint()` keeps minting for its non-window users; only the "back-fill every pending provisional window with the ordinal I just minted" behaviour goes. If `Provisional(Action<uint>)` has no callers left after this, delete it too.

- [ ] **Step 7: Build, run everything GREEN, kill the mutation.** Run:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
.\deploy.ps1 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'
dotnet run -c Debug --project tools/RailCheck
pwsh -NoProfile -File tools/law-integrity.ps1
dotnet run -c Debug --project tools/RailSim -- --seed 1
```

Expect `RAILCHECK GREEN — laws-run=339/339 law-violations=0`, `laws: 279 file(s) + 60 inline = 339`, `law-integrity: OK` (after pasting its digest into `tools/law-integrity.ps1:50` and amending the comment to `L523 ADDED, L496 RETIRED (it legitimised a duplicate gate) -- 339 registrations, identity set changed`), and `RAILSIM GREEN — scenarios=2/2 failures=0 seed=1`. Then apply the mutation: re-add `internal static uint ForNewWindow() => Mint();` to `RailOrdinal`. Run RailCheck, confirm `L523 window-backfill-survives: RailOrdinal.ForNewWindow still exists.` and RED; revert; confirm GREEN 339/339.

- [ ] **Step 8: Record and commit.** Add to `docs/laws.md` after the L522 row, and add a line under `## Attention` recording that L496 was retired:

```
| L523 | DURABLE MEANS ANSWERED EXACTLY ONCE, NOT ORDERED: no `HostOrderKey` member survives anywhere in the assembly, `WindowOrder.DurablePriorityHead`/`BindDurable`/`TryGetDurable` and `WindowQueueSync.TryDurablePriorityPreemption` are gone, `RailOrdinal.ForNewWindow` is gone — and the durable LEDGER must still exist (positive control), because deleting answer-once would satisfy every other arm vacuously and let one occurrence be answered twice across a reload | P1 P7 P12 | incident | the DurableInbox surgery (93fed1a +686, 55e5f82 +321, ae2099d +709, adc31a0 +1271, d193852) wired a SECOND ordering system into the same drain as the first, so which one decided depended on whether a request happened to be durable-bound. `RailOrdinal.Mint` back-filling the whole pending list with one ordinal is the measured mechanism of P1 (2026-08-15). MUTATION KILL: re-adding `RailOrdinal.ForNewWindow` → `L523 window-backfill-survives`, RED; reverted → GREEN 339/339. L496 RETIRED in the same commit: it asserted "both gates agree" instead of "there is one gate" (R7) | premise-changed + POSITIVE CONTROL |
```

Then:

```powershell
git rm tools/RailCheck/L496_ClientPresentationGate.cs
git add src/Rail/DurableInboxModel.cs src/Rail/DurableInboxEngine.cs src/Rail/DurableInboxState.cs src/Rail/DurableInboxStore.cs src/Rail/DurableInboxCodec.cs src/Rail/DurableWindowRegistry.cs src/Rail/WindowOrder.cs src/Rail/WindowQueueSync.cs src/Rail/EventPopup.cs src/Rail/RailOrdinal.cs tools/RailCheck/L523_DurableIsAnswerOnceNotOrdering.cs tools/RailCheck/Program.cs tools/RailCheck/L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs tools/law-count.txt tools/law-integrity.ps1 docs/laws.md
git commit -m "refactor(windows): delete the second ordering system and retire l496"
```

Use the real filename printed by `Get-ChildItem tools/RailCheck/L496*` for the `git rm`; do not assume the suffix. The commit body must report the net line count of the diff — §7.1 expects it to be negative.

---

## Task 7: Remove the queue cap and add the runaway canary (§8 item 6a)

`QueueCap = 64` trims from the TAIL, i.e. drops the NEWEST, which directly contradicts accumulation. Read⇒delete replaced it in Task 4; this deletes the trim and proves the canary never drops.

**Files:**
- Create: `tools/RailCheck/L524_OnlyAVoidRemovesAnUnreadEntry.cs`
- Modify: `src/Rail/GeoWindowCoverage.cs:586` (`QueueCap`), `:620-635` (`TrimQueue`), `:640` (`_dropped` in `Reset`)
- Modify: `tools/RailSim/Scenarios.cs` (the 4096 append scenario)
- Modify: `tools/RailCheck/Program.cs`, `L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs`, `tools/law-count.txt`, `tools/law-integrity.ps1`, `docs/laws.md`

- [ ] **Step 1: Write the harness scenario that must fail while the trim exists.** In `tools/RailSim/Scenarios.cs` add to `All()`:

```csharp
            yield return Pair("the-backlog-is-never-trimmed", TheBacklogIsNeverTrimmed);
```

and:

```csharp
        /// <summary>§A.6: the journal has NO cap and NO trim of any kind. Append past the runaway canary
        /// and assert that every single entry is still there and the append kept working. The canary is a
        /// LOG LINE, never a policy — the old QueueCap = 64 trimmed from the TAIL, i.e. dropped the
        /// NEWEST, which is the exact opposite of accumulating what the player has not looked at.</summary>
        private static IEnumerable<string> TheBacklogIsNeverTrimmed(int seed)
        {
            WindowJournal.Reset();
            const int n = WindowJournal.RunawayCanaryAt + 64;
            for (uint i = 1; i <= n; i++) WindowJournal.Append(i, "UIStateGeoModal", new byte[] { 1 });

            if (WindowJournal.UnreadCount != n)
                yield return "the-backlog-is-never-trimmed: appended " + n + " entries and the journal " +
                             "holds " + WindowJournal.UnreadCount + ". An entry is dropped ONLY by being " +
                             "read or by a host-minted void — never by a cap, a trim, a staleness sweep " +
                             "or an LRU. The 4096 canary logs once and KEEPS APPENDING.";

            var head = WindowJournal.PeekHead();
            if (head == null || head.Pos != 1)
                yield return "the-backlog-is-never-trimmed: the head is " +
                             (head == null ? "<null>" : head.Pos.ToString()) + ", not 1. A trim that " +
                             "removed from the FRONT would drop the oldest unread window, which is the " +
                             "one the player is owed next.";

            uint last = 0;
            JournalEntry e;
            while (WindowJournal.TryRead(out e)) last = e.Pos;
            if (last != n)
                yield return "the-backlog-is-never-trimmed: the last entry drained was " + last +
                             ", not " + n + ". The shipped QueueCap = 64 trimmed from the TAIL — it " +
                             "dropped the NEWEST window, i.e. exactly the one that had just been raised.";
            WindowJournal.Reset();
        }
```

Run `dotnet run -c Debug --project tools/RailSim -- --seed 1` and expect `RAILSIM GREEN — scenarios=3/3 failures=0 seed=1` (the journal itself never trimmed). To prove the scenario bites, temporarily add `while (_unread.Count > 64) _unread.RemoveAt(_unread.Count - 1);` at the end of `WindowJournal.Append` and re-run: expect `the-backlog-is-never-trimmed: appended 4160 entries and the journal holds 64.` and `RAILSIM RED`. Remove the temporary trim.

- [ ] **Step 2: Write L524.** Create `tools/RailCheck/L524_OnlyAVoidRemovesAnUnreadEntry.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L524 — AN UNREAD JOURNAL ENTRY IS REMOVED ONLY BY BEING READ OR BY A HOST-MINTED VOID.
    ///
    /// There is no cap, no tail-trim, no time-based staleness, no LRU and no compaction pass (§A.2, §A.6).
    /// The backlog's length is bounded by what the local player has not looked at — a quantity the player
    /// controls. The 4096 line is a RUNAWAY-RAISER CANARY: it logs ONCE and the append CONTINUES; it never
    /// drops an entry and never stops the append.
    ///
    /// The shipped QueueCap = 64 (src/Rail/GeoWindowCoverage.cs:586) trimmed from the TAIL (:620-635),
    /// i.e. dropped the NEWEST pending window. Under accumulation that is exactly backwards, so both the
    /// constant and the trim go.
    ///
    /// ARMS, all EXECUTED against the real journal in a process with no game:
    ///   (a) trim-survives — no QueueCap constant and no TrimQueue method survive in the assembly.
    ///   (b) canary-drops — appending past RunawayCanaryAt keeps every entry.
    ///   (c) canary-stops-appending — the entry appended AFTER the canary fires is still present.
    ///   (d) void-removes — an explicit host-minted void DOES remove an unread entry (positive control:
    ///       without this, arms (b) and (c) pass against a journal from which nothing can ever be removed,
    ///       and the GLOBAL dismissal of §A.5 would silently not work).
    ///
    /// ROLES SEPARATED (§C.3): (b) and (c) execute the CLIENT role (append only), (d) executes the effect
    /// of a HOST-minted record; no arm mints a position, so a host mint fault cannot mask a client fault.
    ///
    /// Falsify (compile-valid src mutations, each named): re-add QueueCap/TrimQueue to GeoWindowCoverage
    /// → (a); add a `while (_unread.Count > 64) _unread.RemoveAt(_unread.Count - 1);` tail-trim to
    /// WindowJournal.Append → (b); make the canary `return;` instead of falling through → (c); make
    /// ApplyVoid always return false without removing → (d).
    /// </summary>
    internal static class L524_OnlyAVoidRemovesAnUnreadEntry
    {
        internal static IEnumerable<string> Check()
        {
            var asm = typeof(WindowJournal).Assembly;
            const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public |
                                     BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            var coverage = asm.GetTypes().FirstOrDefault(t => t.Name == "GeoWindowCoverage");
            if (coverage == null)
            {
                yield return "L524 premise-changed: GeoWindowCoverage did not resolve, so arm (a) cannot " +
                             "see whether the cap it exists to forbid is still there.";
                yield break;
            }
            if (coverage.GetField("QueueCap", Any) != null || coverage.GetMethod("TrimQueue", Any) != null)
                yield return "L524 trim-survives: GeoWindowCoverage still carries QueueCap and/or " +
                             "TrimQueue. That trim removed from the TAIL — it dropped the NEWEST pending " +
                             "window — which directly contradicts accumulating what the local player has " +
                             "not yet looked at. Read ⇒ delete is the replacement, not a bigger cap.";

            WindowJournal.Reset();
            const int n = WindowJournal.RunawayCanaryAt + 8;
            for (uint i = 1; i <= n; i++) WindowJournal.Append(i, "UIStateGeoModal", new byte[] { 1 });
            if (WindowJournal.UnreadCount != n)
                yield return "L524 canary-drops: appended " + n + " entries, journal holds " +
                             WindowJournal.UnreadCount + ". The canary is a log line and nothing else — " +
                             "it NEVER drops an entry. An accepted entry that vanishes is a window the " +
                             "player will never be asked about.";

            uint after = (uint)(n + 1);
            WindowJournal.Append(after, "UIStateGeoscapeEvent", new byte[] { 2 });
            if (WindowJournal.UnreadCount != n + 1)
                yield return "L524 canary-stops-appending: the entry appended after the canary fired did " +
                             "not land. The canary must keep appending — it exists to make a raiser loop " +
                             "VISIBLE in a log, not to become the loop's silent enforcement.";

            if (!WindowJournal.ApplyVoid(after) || WindowJournal.UnreadCount != n)
                yield return "L524 positive-control: an explicit host-minted void did not remove an unread " +
                             "entry. Without a working void, the arms above pass against a journal from " +
                             "which nothing can ever be removed, and the GLOBAL dismissal of §A.5 would " +
                             "silently not work — two peers would then time out differently and diverge " +
                             "(FIX gap-fill, §2.5).";
            WindowJournal.Reset();
        }
    }
}
```

- [ ] **Step 3: Register L524, see it RED, then delete the cap.** Add `Add(laws, () => L524_OnlyAVoidRemovesAnUnreadEntry.Check());` to `Program.cs`, set `ExpectedLawRegistrations = 340;`, `tools/law-count.txt` to `files=280`, the three `L193` counts to `(340, 340,` / `(340, 339,` / `(340, 340, "wrong")`, refresh the execution digest from the `RAILCHECK ABORTED` line, and run RailCheck. Expect RED with:

```
L524 trim-survives: GeoWindowCoverage still carries QueueCap and/or TrimQueue. …
```

Then in `src/Rail/GeoWindowCoverage.cs` delete: the `QueueCap` constant and its doc comment (`:584-586`), the `RequestsField` `FieldInfo` and `_dropped` counter (`:588-592`) if nothing else uses them, the whole `TrimQueue` method with its ~25-line doc comment (`:596-635`), and the `_dropped = 0;` term from `Reset()` (`:640`). Also delete the `— The BOUND: a queue nobody drains must not grow without limit —` section header.

- [ ] **Step 4: Build, run everything, kill the mutation.** Run:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
.\deploy.ps1 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'
dotnet run -c Debug --project tools/RailCheck
pwsh -NoProfile -File tools/law-integrity.ps1
dotnet run -c Debug --project tools/RailSim -- --seed 1
```

Expect `RAILCHECK GREEN — laws-run=340/340 law-violations=0`, `laws: 280 file(s) + 60 inline = 340`, `law-integrity: OK` (paste its digest into `tools/law-integrity.ps1:50` and amend the comment), `RAILSIM GREEN — scenarios=3/3 failures=0 seed=1`. Then apply the mutation: add `while (_unread.Count > 64) _unread.RemoveAt(_unread.Count - 1);` as the last statement of `WindowJournal.Append`. Run RailCheck, confirm `L524 canary-drops: appended 4104 entries, journal holds 64.` and RED; revert; confirm GREEN 340/340.

- [ ] **Step 5: Record and commit.**

```
| L524 | AN UNREAD JOURNAL ENTRY IS REMOVED ONLY BY BEING READ OR BY A HOST-MINTED VOID: `GeoWindowCoverage.QueueCap`/`TrimQueue` are gone; appending past the 4096 canary keeps every entry AND the append after it; and an explicit void DOES remove one (positive control, without which the law passes against a journal nothing can leave) | P1 P13 | principle + incident | `QueueCap = 64` trimmed from the TAIL (`src/Rail/GeoWindowCoverage.cs:620-635`) — it dropped the NEWEST pending window, the exact opposite of accumulating what the local player has not looked at, with an error log on the 1st and every 32nd drop. Read ⇒ delete replaces it; the 4096 line is a runaway-raiser canary only. MUTATION KILL: adding a 64-entry tail trim to `WindowJournal.Append` → `L524 canary-drops: … journal holds 64`, RED; reverted → GREEN 340/340 | premise-changed + POSITIVE CONTROL |
```

```powershell
git add src/Rail/GeoWindowCoverage.cs tools/RailSim/Scenarios.cs tools/RailCheck/L524_OnlyAVoidRemovesAnUnreadEntry.cs tools/RailCheck/Program.cs tools/RailCheck/L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs tools/law-count.txt tools/law-integrity.ps1 docs/laws.md
git commit -m "refactor(windows): delete the queue cap in favour of read-then-delete"
```

---

## Task 8: The empty-journal save gate, player-initiated saves only (§8 item 6b, §A.2b, §A.2c)

**Files:**
- Create: `src/Rail/JournalSaveGate.cs`
- Create: `tools/RailCheck/L525_TheSaveGateReadsOnlyTheLocalCursor.cs`
- Modify: `tools/RailSim/Scenarios.cs` (two-peer independence scenario)
- Modify: `tools/RailCheck/Program.cs`, `L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs`, `tools/law-count.txt`, `tools/law-integrity.ps1`, `docs/laws.md`

- [ ] **Step 1: Read the two native signatures before writing a line.** Read `PhoenixPoint.Common.Saves\PhoenixSaveManager.cs:414` (`public IEnumerator<NextUpdate> AutosaveGame()`) and `Base.Serialization\SaveType.cs:8` (`Autosave`) in `E:\DEV\PhoenixPoint\decompiled\AssemblyCSharp`, plus the manual/quick save entry point and where `SaveType` is stamped (`PhoenixSaveManager.cs:430`). Record all four `file:line` values — they go in the commit body and they decide which method the gate patches. **Do NOT patch, skip, delay or wrap `AutosaveGame()` or any of its five `GeoLevelController` triggers (`:701`, `:1236`, `:1328`, `:1424`, `:1447`).**

- [ ] **Step 2: Write the gate.** Create `src/Rail/JournalSaveGate.cs`:

```csharp
using Base.Serialization;
using HarmonyLib;
using Multiplayer.Util;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// A PLAYER MAY NOT SAVE UNTIL THEIR OWN JOURNAL IS EMPTY — PLAYER-INITIATED SAVES ONLY.
    ///
    /// Every entry read is deleted (§A.2), so "journal empty" is reachable by the local player simply
    /// looking at their own windows. THE GATE READS ONLY THE LOCAL PEER'S CURSOR: no roster, no peer list,
    /// no message, no acknowledgement, no network round-trip. IT IS THEREFORE NOT A QUORUM AND MUST NEVER
    /// BECOME ONE (§7.6) — an AFK peer blocks only their OWN save, and every other peer saves freely.
    ///
    /// AN AUTOSAVE ALWAYS PROCEEDS (§A.2c). It is never blocked, never deferred, and never drains the
    /// journal first. Unread entries at that moment are LOST, exactly as they are lost on any ordinary
    /// session exit. That is intended behaviour and must never be "fixed" by adding persistence — the
    /// journal is session-scoped and no journal state is ever written to or read from a save file.
    /// </summary>
    internal static class JournalSaveGate
    {
        /// <summary>THE DECISION, pure and named so a law executes the real one with no game. TRUE = the
        /// save proceeds. Reads exactly two things: the save's own type, and this peer's own cursor.</summary>
        internal static bool MaySave(SaveType type, bool localJournalEmpty) =>
            type == SaveType.Autosave || localJournalEmpty;

        [HarmonyPatch]
        internal static class ManualSaveGuard
        {
            // Bind to the PLAYER-INITIATED save entry point read in Step 1. AutosaveGame is deliberately
            // NOT bound: the exemption is belt and braces, because MaySave already answers true for it.
            private static bool Prefix(SaveType type)
            {
                if (JournalSaveGate.MaySave(type, WindowJournal.LocalJournalEmpty)) return true;
                MpLog.LogWarning("[Multiplayer][windows] save refused: this peer still has " +
                                 WindowJournal.UnreadCount + " unread window(s). Read them and the save " +
                                 "proceeds — nothing here waits on another player, and every other peer " +
                                 "can save right now");
                return false;
            }
        }
    }
}
```

The `[HarmonyPatch]` attribute above `ManualSaveGuard` must name the exact method found in Step 1 — write it as `[HarmonyPatch(typeof(PhoenixSaveManager), nameof(PhoenixSaveManager.<TheMethod>))]` with the real name, and cite the `file:line`. If the real method's parameter is not called `type`, rename the `Prefix` parameter to match; Harmony binds by name.

- [ ] **Step 3: Write the harness independence scenario.** In `tools/RailSim/Scenarios.cs` add to `All()`:

```csharp
            yield return Pair("one-peers-backlog-never-blocks-another", OnePeersBacklogNeverBlocksAnother);
```

and:

```csharp
        /// <summary>§A.2b / R8: the save gate reads ONLY the local cursor. Peer B sits on a backlog it has
        /// not read; peer A, with an empty journal, must still be able to save. This is the property that
        /// keeps the gate from being a quorum — an AFK peer blocks only their own save.</summary>
        private static IEnumerable<string> OnePeersBacklogNeverBlocksAnother(int seed)
        {
            // Peer B: a fat unread backlog.
            WindowJournal.Reset();
            for (uint i = 1; i <= 25; i++) WindowJournal.Append(i, "UIStateGeoModal", new byte[] { 1 });
            bool peerBMaySave = JournalSaveGate.MaySave(SaveType.Manual, WindowJournal.LocalJournalEmpty);
            if (peerBMaySave)
                yield return "one-peers-backlog-never-blocks-another: peer B saved with 25 unread " +
                             "windows. The gate is the whole reason the journal needs no persistence — " +
                             "if it does not hold, a save can carry state the journal will not restore.";

            // Peer A: its own journal, drained. Nothing about peer B is readable from here, and that is
            // the point — the gate takes only (SaveType, localJournalEmpty).
            WindowJournal.Reset();
            bool peerAMaySave = JournalSaveGate.MaySave(SaveType.Manual, WindowJournal.LocalJournalEmpty);
            if (!peerAMaySave)
                yield return "one-peers-backlog-never-blocks-another: peer A could not save with an EMPTY " +
                             "journal. A gate that consults anything but the local cursor is a quorum, " +
                             "which the no-blockers rule forbids outright.";

            if (!JournalSaveGate.MaySave(SaveType.Autosave, false))
                yield return "one-peers-backlog-never-blocks-another: an AUTOSAVE was refused with a " +
                             "non-empty journal. An autosave always proceeds — never blocked, never " +
                             "deferred, never draining first — and whatever is unread is lost, exactly " +
                             "as on any ordinary session exit (§A.2c).";
            WindowJournal.Reset();
        }
```

Add `using Base.Serialization;` to the file's usings. Read the real `SaveType` member name for a player-initiated save from `Base.Serialization\SaveType.cs` and use it in place of `SaveType.Manual` if it differs; cite the `file:line`.

- [ ] **Step 4: Write L525.** Create `tools/RailCheck/L525_TheSaveGateReadsOnlyTheLocalCursor.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Base.Serialization;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L525 — THE SAVE GATE READS ONLY THE LOCAL CURSOR, AND AN AUTOSAVE ALWAYS PROCEEDS.
    ///
    /// R8: if the empty-journal gate ever consults another peer's state it is a quorum and violates the
    /// no-blockers rule outright — one player must be able to drive the whole game to completion while
    /// every other peer is AFK. And §A.2c: an autosave is never blocked, never deferred and never forces
    /// a drain; a law that could turn RED because an autosave went through with a non-empty journal is
    /// wrong by construction and must be rejected in review.
    ///
    /// ARMS:
    ///   (a) autosave-blocked — EXECUTED: MaySave(SaveType.Autosave, localJournalEmpty: false) is TRUE.
    ///   (b) manual-not-gated / manual-over-gated — EXECUTED, both directions, so the gate cannot be
    ///       satisfied by a constant.
    ///   (c) gate-reads-remote-state — SIGNATURE: MaySave takes exactly (SaveType, bool). A parameter
    ///       list that cannot express a remote peer is a stronger statement than any IL walk, because
    ///       there is nothing remote in scope for it to read.
    ///   (d) autosave-patched — the mod patches no method named AutosaveGame anywhere.
    ///
    /// ROLES SEPARATED (§C.3): the gate is a LOCAL decision by construction and (c) is what proves it —
    /// there is no host arm and no client arm because there is no remote input at all.
    ///
    /// Falsify (compile-valid src mutations, each named): drop the `type == SaveType.Autosave ||` term
    /// → (a); make MaySave `=> true;` → (b); add a third parameter carrying a peer count → (c); add a
    /// [HarmonyPatch] on AutosaveGame → (d).
    /// </summary>
    internal static class L525_TheSaveGateReadsOnlyTheLocalCursor
    {
        internal static IEnumerable<string> Check()
        {
            var gate = typeof(JournalSaveGate);
            var maySave = gate.GetMethod("MaySave", BindingFlags.Static | BindingFlags.Public |
                                                    BindingFlags.NonPublic);
            if (maySave == null)
            {
                yield return "L525 premise-changed: JournalSaveGate.MaySave did not resolve, so this law " +
                             "cannot execute the decision it exists to constrain.";
                yield break;
            }

            // (c) SIGNATURE: nothing remote is even in scope.
            var ps = maySave.GetParameters();
            if (ps.Length != 2 || ps[0].ParameterType != typeof(SaveType) || ps[1].ParameterType != typeof(bool))
                yield return "L525 gate-reads-remote-state: MaySave's parameters are (" +
                             string.Join(", ", ps.Select(p => p.ParameterType.Name).ToArray()) +
                             "), not (SaveType, Boolean). The gate must be unable to see a remote peer at " +
                             "all — a save gate that consults another peer's state is a quorum, and one " +
                             "player must be able to finish the game while every other peer is AFK.";

            // (a) AUTOSAVE ALWAYS PROCEEDS.
            if (!JournalSaveGate.MaySave(SaveType.Autosave, false))
                yield return "L525 autosave-blocked: an autosave was refused with a non-empty journal. " +
                             "An autosave is never blocked, never deferred and never drains the journal " +
                             "first; whatever is unread at that moment is LOST, exactly as on any session " +
                             "exit, and that is intended (§A.2c) — never to be 'fixed' with persistence.";

            // (b) BOTH DIRECTIONS on the player-initiated path.
            if (JournalSaveGate.MaySave(SaveType.Manual, false))
                yield return "L525 manual-not-gated: a player-initiated save proceeded with unread " +
                             "entries. The journal is never written to a save file, so a save taken over " +
                             "a backlog silently discards windows the player was owed.";
            if (!JournalSaveGate.MaySave(SaveType.Manual, true))
                yield return "L525 manual-over-gated: a player-initiated save was refused with an EMPTY " +
                             "journal. The gate must add exactly one condition — the LOCAL cursor — and " +
                             "must never become a reason a player cannot save at all.";

            // (d) NOTHING PATCHES THE AUTOSAVE.
            var offenders = typeof(WindowJournal).Assembly.GetTypes()
                .Where(t => t.GetCustomAttributes(false)
                             .Any(a => a.GetType().Name == "HarmonyPatch" && a.ToString().IndexOf(
                                      "AutosaveGame", StringComparison.Ordinal) >= 0))
                .Select(t => t.FullName).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (offenders.Count > 0)
                yield return "L525 autosave-patched: " + string.Join(", ", offenders) + " patch(es) " +
                             "AutosaveGame. None of its five GeoLevelController triggers (:701, :1236, " +
                             ":1328, :1424, :1447) may be patched, skipped, delayed or wrapped by this " +
                             "work.";
        }
    }
}
```

- [ ] **Step 5: Register, run, kill the mutation, commit.** Add `Add(laws, () => L525_TheSaveGateReadsOnlyTheLocalCursor.Check());`, set `ExpectedLawRegistrations = 341;`, `tools/law-count.txt` to `files=281`, `L193` counts to `(341, 341,` / `(341, 340,` / `(341, 341, "wrong")`, refresh both digests from the `RAILCHECK ABORTED` and `law identity set changed` lines. Run:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
.\deploy.ps1 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'
dotnet run -c Debug --project tools/RailCheck
pwsh -NoProfile -File tools/law-integrity.ps1
dotnet run -c Debug --project tools/RailSim -- --seed 1
```

Expect `RAILCHECK GREEN — laws-run=341/341 law-violations=0`, `laws: 281 file(s) + 60 inline = 341`, `law-integrity: OK`, `RAILSIM GREEN — scenarios=4/4 failures=0 seed=1`. Mutation: change `MaySave` to `=> localJournalEmpty;` (dropping the autosave term). Run RailCheck, confirm `L525 autosave-blocked: an autosave was refused with a non-empty journal.` and RED; revert; confirm GREEN 341/341. Record:

```
| L525 | THE SAVE GATE READS ONLY THE LOCAL CURSOR AND AN AUTOSAVE ALWAYS PROCEEDS: `MaySave` takes exactly `(SaveType, bool)` so nothing remote is in scope; an autosave passes with a non-empty journal; a player-initiated save is refused with unread entries and permitted with none; and no type in the mod assembly patches `AutosaveGame` | P13 | principle | R8 — an empty-journal gate that consults another peer's state is a quorum, and one player must be able to finish the campaign while every other peer is AFK. §A.2c: an autosave is never blocked, never deferred and never drains first; unread entries are LOST exactly as on any session exit, and that must never be "fixed" with persistence. MUTATION KILL: `MaySave => localJournalEmpty;` → `L525 autosave-blocked`, RED; reverted → GREEN 341/341 | premise-changed |
```

```powershell
git add src/Rail/JournalSaveGate.cs tools/RailSim/Scenarios.cs tools/RailCheck/L525_TheSaveGateReadsOnlyTheLocalCursor.cs tools/RailCheck/Program.cs tools/RailCheck/L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs tools/law-count.txt tools/law-integrity.ps1 docs/laws.md
git commit -m "feat(windows): gate player-initiated saves on an empty local journal"
```

---

## Task 9: Dismissal scope and host-minted void records (§8 item 7)

**Files:**
- Create: `tools/RailCheck/L526_AnUndeclaredFamilyIsLocal.cs`
- Modify: `src/Rail/GeoModalMirror.cs` (mint and handle `kind = 2` Void), `src/Rail/WindowJournal.cs` (nothing new — `ScopeOf`/`ApplyVoid` already exist from Task 4)
- Modify: `src/Rail/DeploymentWindow.cs:490-524` (`DropUnservableQueued` demoted), `src/Rail/WindowQueueSync.cs:203-214`
- Modify: `tools/RailSim/Scenarios.cs` (properties C.1.4 and C.1.5)
- Modify: `tools/RailCheck/Program.cs`, `L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs`, `tools/law-count.txt`, `tools/law-integrity.ps1`, `docs/laws.md`

- [ ] **Step 1: Mint the void on the host.** In `src/Rail/GeoModalMirror.cs` add:

```csharp
        /// <summary>THE HOST-MINTED VOID (§A.5). A GLOBAL dismissal removes an entry from every peer's
        /// UNPRESENTED backlog and closes it if already open. Explicit by design: without a void record,
        /// two peers time out differently and diverge (FIX gap-fill, §2.5). Only the host mints one.</summary>
        internal static void HostMintVoid(uint journalPos, string family)
        {
            if (!HostMayPublish()) return;
            if (WindowJournal.ScopeOf(family) != DismissScope.Global)
            {
                MpLog.LogWarning("[Multiplayer][windows] refused to void position " + journalPos +
                                 " for family '" + family + "': its declared dismissal scope is LOCAL. " +
                                 "A LOCAL dismissal removes only the dismissing peer's own entry");
                return;
            }
            var engine = LiveEngine();
            if (engine == null) return;
            uint seq = Seq.Next(SurfaceIds.GeoModalRaise);
            engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope,
                SyncProtocol.EncodeEnvelope(SurfaceIds.GeoModalRaise, SyncKind.StateDelta,
                                            EncodeVoid(seq, journalPos))));
            ApplyVoidLocally(journalPos);
            MpLog.Log("[Multiplayer][windows] HOST voided journal position " + journalPos + " (family '" +
                      family + "', scope GLOBAL) seq=" + seq);
        }

        /// <summary>[seq:u32][kind:u8 = 2][journalPos:u32] and nothing else — a void names a position, not
        /// a window.</summary>
        private static byte[] EncodeVoid(uint seq, uint journalPos)
        {
            using (var ms = new System.IO.MemoryStream())
            using (var w = new System.IO.BinaryWriter(ms))
            {
                w.Write(seq);
                w.Write((byte)2);
                w.Write(journalPos);
                return ms.ToArray();
            }
        }

        /// <summary>Remove it from the backlog and close it if this peer already has it open. Same code on
        /// host and client — the host applies its own void through here so both roles converge on one
        /// implementation rather than on two that must agree (R7).</summary>
        internal static void ApplyVoidLocally(uint journalPos)
        {
            bool removed = WindowJournal.ApplyVoid(journalPos);
            WindowGap.Forget(journalPos);
            CloseOpenCopyOf(journalPos);
            // REACTIVITY (hard mandate): the backlog changed, so the open screen must repaint without the
            // player leaving and re-entering. Kindless arm — a void carries no rail path.
            OpenUiRepaint.MarkDirty();
            if (!removed)
                MpLog.Log("[Multiplayer][windows] void for position " + journalPos +
                          " found nothing unread — this peer had already read or never received it");
        }
```

`CloseOpenCopyOf` must close an already-presented copy through the native door. Read `GeoscapeViewSwitchQuery.FinishCurrentStateSwitch` (`:116`) and `GeoscapeView.ResetViewState()` (decompile `GeoscapeView.cs:413`) before writing it, and cite both `file:line`. Do not guess a close path.

- [ ] **Step 2: Handle the void on arrival.** In `GeoModalMirror`'s `HandleEnvelope` arm for `SurfaceIds.GeoModalRaise` (`:567`), branch on `kind` before decoding the rest:

```csharp
                // kind 2 = VOID. It names a position and carries nothing else, so it must be read before
                // the raise decoder, whose fields it does not have.
                if (kind == 2) { ApplyVoidLocally(r.ReadUInt32()); return true; }
```

- [ ] **Step 3: Demote `DropUnservableQueued` to native-queue hygiene.** In `src/Rail/DeploymentWindow.cs:490-524` change the method so that it removes only from the NATIVE queue and never from `WindowJournal`, and add at its head:

```csharp
            // NATIVE-QUEUE HYGIENE ONLY (§A.6). This predicate reads GAME state, and evaluating it
            // per-peer is asymmetric by construction — one peer's Servable() answer is not another's.
            // A JOURNAL entry is removed only by being read or by a host-minted void; the HOST evaluates
            // Servable() and mints that void (GeoModalMirror.HostMintVoid). A client MUST NOT remove a
            // journal entry from its own evaluation, and this method must never touch WindowJournal.
```

On the host, where `Servable()` returns false for a queued mission window that carries a journal position, call `GeoModalMirror.HostMintVoid(pos, family)` instead of removing locally. In `src/Rail/WindowQueueSync.cs:203-214` delete the local-only removal path that has no broadcast.

- [ ] **Step 4: Write the two harness properties.** In `tools/RailSim/Scenarios.cs` add to `All()`:

```csharp
            yield return Pair("a-local-dismissal-removes-only-mine", ALocalDismissalRemovesOnlyMine);
            yield return Pair("a-global-dismissal-removes-it-everywhere", AGlobalDismissalRemovesItEverywhere);
```

and:

```csharp
        /// <summary>§C.1 property 4: a dismissal marked LOCAL never removed another peer's entry. Two
        /// peers, same journal position; peer A reads (= dismisses) it; peer B must still hold it.</summary>
        private static IEnumerable<string> ALocalDismissalRemovesOnlyMine(int seed)
        {
            // Peer A.
            WindowJournal.Reset();
            WindowJournal.Append(1, "UIStateGeoModal", new byte[] { 1 });
            JournalEntry read;
            WindowJournal.TryRead(out read);
            int aRemaining = WindowJournal.UnreadCount;

            // Peer B — a separate journal generation, and NOTHING peer A did reaches it. That isolation
            // IS the property: a LOCAL dismissal is a per-peer delete with no wire form at all.
            WindowJournal.Reset();
            WindowJournal.Append(1, "UIStateGeoModal", new byte[] { 1 });
            int bRemaining = WindowJournal.UnreadCount;

            if (aRemaining != 0)
                yield return "a-local-dismissal-removes-only-mine: peer A still holds " + aRemaining +
                             " entries after reading its only one. Read ⇒ deleted, locally.";
            if (bRemaining != 1)
                yield return "a-local-dismissal-removes-only-mine: peer B holds " + bRemaining +
                             " entries, not 1. Peer A's dismissal reached peer B — the default scope is " +
                             "LOCAL and only the mission family is GLOBAL, so an ordinary window a peer " +
                             "closes must remain for everyone else.";

            if (WindowJournal.ScopeOf("UIStateGeoModal") != DismissScope.Local)
                yield return "a-local-dismissal-removes-only-mine: UIStateGeoModal is not declared LOCAL. " +
                             "Default is LOCAL and an UNDECLARED family IS local — a new window family " +
                             "needs no code at all (§A.5).";
            WindowJournal.Reset();
        }

        /// <summary>§C.1 property 5: a GLOBAL dismissal removed it everywhere, including closing it if
        /// already open. Modelled as the host-minted void applied to a peer that had not read it.</summary>
        private static IEnumerable<string> AGlobalDismissalRemovesItEverywhere(int seed)
        {
            if (WindowJournal.ScopeOf("UIStateRosterDeployment") != DismissScope.Global)
                yield return "a-global-dismissal-removes-it-everywhere: the mission family is not declared " +
                             "GLOBAL. It is the ONE global family: once anyone has acted on a mission the " +
                             "decision to deploy is taken, and it is meaningless for the others to accept " +
                             "or refuse.";

            WindowJournal.Reset();
            WindowJournal.Append(1, "UIStateRosterDeployment", new byte[] { 1 });
            WindowJournal.Append(2, "UIStateGeoModal", new byte[] { 2 });
            bool removed = WindowJournal.ApplyVoid(1);
            if (!removed || WindowJournal.UnreadCount != 1)
                yield return "a-global-dismissal-removes-it-everywhere: the void left " +
                             WindowJournal.UnreadCount + " entries and reported removed=" + removed +
                             ". A host-minted void removes an entry a peer has NOT read — that is the " +
                             "only mechanism that can, and it is explicit precisely because an implicit " +
                             "per-peer timeout makes two peers diverge.";
            var head = WindowJournal.PeekHead();
            if (head == null || head.Pos != 2)
                yield return "a-global-dismissal-removes-it-everywhere: after voiding position 1 the head " +
                             "is " + (head == null ? "<null>" : head.Pos.ToString()) + ", not 2. A void " +
                             "must remove exactly the named position and disturb no other.";

            if (WindowJournal.ApplyVoid(999))
                yield return "a-global-dismissal-removes-it-everywhere: a void for a position this peer " +
                             "never held reported success. It must be a no-op — a reconnecting peer " +
                             "legitimately receives voids for entries it never got (§A.2b).";
            WindowJournal.Reset();
        }
```

- [ ] **Step 5: Write L526.** Create `tools/RailCheck/L526_AnUndeclaredFamilyIsLocal.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L526 — DISMISSAL SCOPE IS A DECLARED PROPERTY OF A FAMILY, NEVER A SPECIAL CASE.
    ///
    /// Default is LOCAL and an UNDECLARED family IS local, so a new window family needs NO new code
    /// (§A.5). Only the mission family is GLOBAL, and a GLOBAL dismissal is effected by an explicit
    /// host-minted void record — never by each peer independently deciding, because without an explicit
    /// void two peers time out differently and diverge (FIX gap-fill, §2.5).
    ///
    /// ARMS:
    ///   (a) undeclared-is-not-local — EXECUTED: a family nobody has ever heard of is LOCAL.
    ///   (b) mission-is-not-global — EXECUTED, the other direction, so ScopeOf cannot be a constant.
    ///   (c) scope-decided-elsewhere — the declaration table is the ONLY place a family's scope is
    ///       written: no method outside WindowJournal compares a family name against a mission window
    ///       state name.
    ///   (d) client-removes-on-its-own-verdict — DeploymentWindow.DropUnservableQueued does not reference
    ///       WindowJournal at all. The predicate reads GAME state and is asymmetric per peer; a client
    ///       evaluating it and removing its own journal entry re-creates the divergence the void exists
    ///       to prevent.
    ///
    /// ROLES SEPARATED (§C.3): (a)/(b) are role-free pure calls; (c)/(d) are statements about the shipped
    /// assembly. The HOST-only arm is that HostMintVoid exists and is the only minter — arm (e).
    ///
    /// Falsify (compile-valid src mutations, each named): make ScopeOf return Global by default → (a);
    /// remove the mission rows from FamilyScope → (b); add an `if (family == "UIStateRosterDeployment")`
    /// outside WindowJournal → (c); call WindowJournal.ApplyVoid from DropUnservableQueued → (d).
    /// </summary>
    internal static class L526_AnUndeclaredFamilyIsLocal
    {
        internal static IEnumerable<string> Check()
        {
            var journal = typeof(WindowJournal);
            if (journal.GetMethod("ScopeOf", BindingFlags.Static | BindingFlags.NonPublic |
                                             BindingFlags.Public) == null)
            {
                yield return "L526 premise-changed: WindowJournal.ScopeOf did not resolve, so the " +
                             "declaration table this law protects has moved.";
                yield break;
            }

            if (WindowJournal.ScopeOf("UIStateSomeFamilyThatDoesNotExistYet") != DismissScope.Local)
                yield return "L526 undeclared-is-not-local: an undeclared family did not come back LOCAL. " +
                             "A new window family — including one raised by a mod we do not control — must " +
                             "need NO new code, and LOCAL is the recoverable direction: a window that " +
                             "stays for the other peers costs a click, a window that vanishes for them is " +
                             "a decision they were never asked about.";
            if (WindowJournal.ScopeOf(null) != DismissScope.Local)
                yield return "L526 undeclared-is-not-local: a null family did not come back LOCAL.";

            if (WindowJournal.ScopeOf("UIStateRosterDeployment") != DismissScope.Global)
                yield return "L526 mission-is-not-global: the mission family is not GLOBAL. Once ANYONE " +
                             "has acted on a mission the decision to deploy is taken, so it is meaningless " +
                             "for the others to accept or refuse — they get the centre-of-screen button " +
                             "instead. Without this arm ScopeOf could simply return Local always.";

            var asm = typeof(WindowJournal).Assembly;
            const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public |
                                     BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            // (c) THE TABLE IS THE ONLY PLACE.
            var missionNames = new[] { "UIStateRosterDeployment", "UIStateGeoMissionBrief" };
            var elsewhere = asm.GetTypes().Where(t => t != journal)
                .SelectMany(t => t.GetMethods(Any).Cast<MethodBase>().Concat(t.GetConstructors(Any)))
                .Where(m => Il.MentionsAnyString(m, missionNames))
                .Select(m => m.DeclaringType.Name + "." + m.Name)
                .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (elsewhere.Count > 0)
                yield return "L526 scope-decided-elsewhere: " + string.Join(", ", elsewhere) + " name(s) a " +
                             "mission window state outside WindowJournal's declaration table. The table is " +
                             "the ONLY place a family's scope may be written; an `if (family == …)` " +
                             "anywhere else is the special case §A.5 forbids.";

            // (d) NO CLIENT REMOVES ON ITS OWN VERDICT.
            var deployment = asm.GetTypes().FirstOrDefault(t => t.Name == "DeploymentWindow");
            var drop = deployment == null ? null : deployment.GetMethod("DropUnservableQueued", Any);
            var applyVoid = journal.GetMethod("ApplyVoid", BindingFlags.Static | BindingFlags.NonPublic |
                                                           BindingFlags.Public);
            if (drop != null && applyVoid != null && Il.References(drop, applyVoid))
                yield return "L526 client-removes-on-its-own-verdict: DropUnservableQueued reaches " +
                             "WindowJournal.ApplyVoid. Its Servable() predicate reads GAME state and is " +
                             "PER-PEER and ASYMMETRIC — the host evaluates it and mints the void, and a " +
                             "client applies what it is sent. It may remain native-queue hygiene only.";

            // (e) POSITIVE CONTROL, HOST ROLE: the void minter exists. Every arm above forbids things; if
            // the minter were gone, a GLOBAL family would simply never be dismissed anywhere.
            var mirror = asm.GetTypes().FirstOrDefault(t => t.Name == "GeoModalMirror");
            if (mirror == null || mirror.GetMethod("HostMintVoid", Any) == null)
                yield return "L526 positive-control: GeoModalMirror.HostMintVoid does not exist, so " +
                             "nothing can ever mint a void and the GLOBAL scope declared above has no " +
                             "effect at all.";
        }
    }
}
```

- [ ] **Step 6: Register, run, kill the mutation, commit.** Add `Add(laws, () => L526_AnUndeclaredFamilyIsLocal.Check());`, set `ExpectedLawRegistrations = 342;`, `tools/law-count.txt` to `files=282`, `L193` counts to `(342, 342,` / `(342, 341,` / `(342, 342, "wrong")`, refresh both digests. Run:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
.\deploy.ps1 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'
dotnet run -c Debug --project tools/RailCheck
pwsh -NoProfile -File tools/law-integrity.ps1
dotnet run -c Debug --project tools/RailSim -- --seed 1
```

Expect `RAILCHECK GREEN — laws-run=342/342 law-violations=0`, `laws: 282 file(s) + 60 inline = 342`, `law-integrity: OK`, `RAILSIM GREEN — scenarios=6/6 failures=0 seed=1`. Mutation: change `WindowJournal.ScopeOf` to `=> DismissScope.Global;`. Run RailCheck, confirm `L526 undeclared-is-not-local: an undeclared family did not come back LOCAL.` and RED; revert; confirm GREEN 342/342. Record:

```
| L526 | DISMISSAL SCOPE IS A DECLARED PROPERTY, NEVER A SPECIAL CASE: an undeclared (and a null) family is LOCAL, the mission family is GLOBAL, no method outside `WindowJournal` names a mission window state, `DropUnservableQueued` never reaches `WindowJournal.ApplyVoid`, and `GeoModalMirror.HostMintVoid` exists (positive control — without a minter the GLOBAL scope has no effect) | P1 P13 | principle | §A.5 + `DeploymentWindow.DropUnservableQueued` (`src/Rail/DeploymentWindow.cs:490-524`) removed per-peer and asymmetrically from a GAME-state predicate with no broadcast (`src/Rail/WindowQueueSync.cs:203-214`) and an early return at `view == null` (`:505`). Under one global order the removal is a HOST decision expressed as a void. MUTATION KILL: `ScopeOf => DismissScope.Global;` → `L526 undeclared-is-not-local`, RED; reverted → GREEN 342/342 | premise-changed + POSITIVE CONTROL |
```

```powershell
git add src/Rail/GeoModalMirror.cs src/Rail/DeploymentWindow.cs src/Rail/WindowQueueSync.cs tools/RailSim/Scenarios.cs tools/RailCheck/L526_AnUndeclaredFamilyIsLocal.cs tools/RailCheck/Program.cs tools/RailCheck/L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs tools/law-count.txt tools/law-integrity.ps1 docs/laws.md
git commit -m "feat(windows): declare dismissal scope and mint host void records"
```

---

## Task 10: Remove the never-created case; re-express L475; assert the gap property in the harness (§A.11, C.1.2)

A research completion latched before this peer has ever stood on a map surface is currently SWALLOWED. §A.11 removes that: no family bypasses the journal. `L475` guards the latch today and must be RE-EXPRESSED — never weakened, never deleted — to assert the strictly stronger replacement invariant.

**Files:**
- Modify: `src/Rail/ResearchSync.cs:96` (`_everOnMapSurface` reset), `:117-121` (`LatchCompletion`), `:320` (`PresentFromMirror`'s `onMapSurface`)
- Modify: `tools/RailCheck/L475_*.cs` (re-expressed, same id, same file, no count change)
- Modify: `tools/RailSim/Scenarios.cs` (property C.1.2)
- Modify: `docs/laws.md`

- [ ] **Step 1: Add the C.1.2 harness property.** In `tools/RailSim/Scenarios.cs` add to `All()`:

```csharp
            yield return Pair("no-gap-is-permanent", NoGapIsPermanent);
```

and:

```csharp
        /// <summary>§C.1 property 2: NO GAP IS PERMANENT. A gap self-releases on an armed timer AND is
        /// resolved by an explicit host-minted void record. Both halves are asserted, because the timer
        /// alone would let two peers time out differently and diverge (FIX gap-fill, §2.5), and the void
        /// alone would let a lost void hold a peer forever — which would be a wait on another peer.</summary>
        private static IEnumerable<string> NoGapIsPermanent(int seed)
        {
            WindowGap.Reset();
            double t = 1000.0;
            if (WindowGap.SelfReleasedAt(5, t))
                yield return "no-gap-is-permanent: the gap released on first sight, so the host's order is " +
                             "abandoned the instant a raise is a frame late.";

            // Half the interval: still holding. The hold is what makes the host's order authoritative.
            if (WindowGap.SelfReleasedAt(5, t + WindowGap.SelfReleaseSeconds / 2))
                yield return "no-gap-is-permanent: the gap released after half its armed interval.";

            // Past the interval: released, by itself, with no peer having done anything.
            if (!WindowGap.SelfReleasedAt(5, t + WindowGap.SelfReleaseSeconds + 0.001))
                yield return "no-gap-is-permanent: the gap did NOT self-release after its armed interval. " +
                             "A drain gate that can hold forever is a wait on another peer — one player " +
                             "must be able to drive the whole game while every other peer is AFK.";

            // And the AUTHORITATIVE resolution: a void clears the position outright, timer or no timer.
            WindowJournal.Reset();
            WindowJournal.Append(5, "UIStateRosterDeployment", new byte[] { 5 });
            WindowJournal.ApplyVoid(5);
            WindowGap.Forget(5);
            if (WindowJournal.PeekHead() != null)
                yield return "no-gap-is-permanent: a host-minted void did not clear the gapped position. " +
                             "The timer is the safety net; the void is the resolution, and it must be " +
                             "explicit so two peers cannot resolve the same gap differently.";
            WindowGap.Reset();
            WindowJournal.Reset();
        }
```

Run `dotnet run -c Debug --project tools/RailSim -- --seed 1`; expect `RAILSIM GREEN — scenarios=7/7 failures=0 seed=1`.

- [ ] **Step 2: Delete the swallow.** In `src/Rail/ResearchSync.cs`:
  - delete the `_everOnMapSurface` field and its `= false;` reset in `Reset()` (`:96`);
  - change `LatchCompletion` (`:117-121`) to:

```csharp
        /// <summary>LATCH ONE COMPLETION FOR PRESENTATION. NOTHING IS SWALLOWED (§A.11). A completion that
        /// latches before this peer has ever stood on a map surface is APPENDED like any other and is
        /// presented when the peer reaches a surface that can present it, by the ordinary cursor rule
        /// (§A.2) plus the open-screen hold (WindowOrder.HoldsForOpenScreen). There is no family that
        /// bypasses the journal — the LMAX single-entrance property asserted with no exceptions carved out
        /// of it (§A.1, §A.11).</summary>
        internal static bool LatchCompletion(string researchId, bool onMapSurfaceNow)
        {
            _deferredCompleted.Add(researchId);
            return true;
        }
```

  - in `PresentFromMirror` (`:320`) delete the `bool onMapSurface = DurableWindowRegistry.MayPresent(...)` computation and the `if (!seeded) continue;` backlog swallow, and call `LatchCompletion(el.ResearchID, true)`. Keep `_presentedCompleted.Add(el.ResearchID)` — that is answer-once, not a swallow.

- [ ] **Step 3: Re-express L475.** Open the existing `tools/RailCheck/L475_*.cs`. Do NOT delete it and do NOT weaken it. Replace its arms with the strictly stronger invariant, keeping the id, the filename and the registration untouched (so no count and no digest changes):

```csharp
            // THE INVARIANT IS STRICTLY STRONGER UNDER THE JOURNAL. It used to be "a completion latched
            // before this peer's first map surface is swallowed on purpose, and only then". It is now
            // "a completion is NEVER lost" — the swallow is gone (§A.11) and every completion is appended.
            var sync = typeof(ResearchSync);
            const BindingFlags Any = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public |
                                     BindingFlags.DeclaredOnly;
            if (sync.GetField("_everOnMapSurface", Any) != null)
                yield return "L475 swallow-survives: ResearchSync._everOnMapSurface still exists. A " +
                             "completion latched before this peer's first map surface used to be dropped " +
                             "silently; under the journal it is APPENDED like every other family, and " +
                             "presentation is decided by the cursor plus the open-screen hold. No family " +
                             "bypasses the journal (§A.11).";

            var latch = sync.GetMethod("LatchCompletion", Any);
            if (latch == null)
            {
                yield return "L475 premise-changed: ResearchSync.LatchCompletion did not resolve, so the " +
                             "one door into the deferral list has moved and this law is blind.";
                yield break;
            }
            // EXECUTED, both directions, so the law cannot be satisfied by a method that always drops.
            ResearchSync.ResetForReloadBoundary();
            if (!ResearchSync.LatchCompletion("PX_TestResearch_A", onMapSurfaceNow: false))
                yield return "L475 completion-dropped-off-surface: a completion arriving with NO map " +
                             "surface was refused. This is the arm that replaces the old swallow: it must " +
                             "be appended, not dropped — the invariant 'a completion is never silently " +
                             "lost' is stronger under the journal, so the law got stronger too.";
            if (!ResearchSync.LatchCompletion("PX_TestResearch_B", onMapSurfaceNow: true))
                yield return "L475 completion-dropped-on-surface: a completion arriving WITH a map surface " +
                             "was refused. Without this direction the law would pass against a latch that " +
                             "accepts nothing at all.";
            ResearchSync.ResetForReloadBoundary();
```

Update the file's `<summary>` to describe the new invariant and to record, in prose, that the old latch was removed by §A.11 rather than that the law was relaxed.

- [ ] **Step 4: Build, run, kill the mutation.** Run:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
.\deploy.ps1 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'
dotnet run -c Debug --project tools/RailCheck
pwsh -NoProfile -File tools/law-integrity.ps1
dotnet run -c Debug --project tools/RailSim -- --seed 1
```

Expect `RAILCHECK GREEN — laws-run=342/342 law-violations=0`, `laws: 282 file(s) + 60 inline = 342`, `law-integrity: OK` (no digest change: the registration SET is unchanged, only one law's body moved), `RAILSIM GREEN — scenarios=7/7 failures=0 seed=1`. Mutation: re-add `private static bool _everOnMapSurface;` to `ResearchSync` and `if (!_everOnMapSurface) return false;` at the head of `LatchCompletion`. Run RailCheck, confirm both `L475 swallow-survives: ResearchSync._everOnMapSurface still exists.` and `L475 completion-dropped-off-surface:` RED; revert; confirm GREEN 342/342.

- [ ] **Step 5: Record and commit.** In `docs/laws.md`, EDIT the existing `L475` row in place — do not add a second one — so its title reads `A RESEARCH COMPLETION IS NEVER SILENTLY LOST: `_everOnMapSurface` is gone and `LatchCompletion` appends whether or not this peer has ever stood on a map surface (both directions executed)`, and append to its evidence cell:

```
RE-EXPRESSED 2026-08-15 under §A.11: the swallow was REMOVED (no family bypasses the journal, TFTV's windows included), so the invariant this law protects — a completion is never silently lost — became strictly stronger and the law was strengthened to match rather than weakened or deleted. MUTATION KILL: re-adding `_everOnMapSurface` + its early return → `L475 swallow-survives` and `L475 completion-dropped-off-surface`, RED; reverted → GREEN 342/342
```

```powershell
git add src/Rail/ResearchSync.cs tools/RailCheck/L475_ResearchCompletionNeverLost.cs tools/RailSim/Scenarios.cs docs/laws.md
git commit -m "refactor(windows): journal every research completion and re-express l475"
```

Use the real `L475` filename from `Get-ChildItem tools/RailCheck/L475*` in the `git add`.

---

## Task 11: Close the two bypasses (§8 item 8, §A.8)

Two paths can still put a window up without claiming a journal position: save-restore `RestoreData` (`src/Rail/WindowOrder.cs:71`) and the mission-outcome modal raised from `UIStateInitial.EnterState:112` (`src/Rail/WindowOrder.cs:427`).

**Files:**
- Modify: `src/Rail/WindowOrder.cs:71` (the `RestoreData` note and its patch), `:427` (the initial-state raise), and the ordinal-0 restored-request path at `:541-545` (already deleted in Task 5 — verify nothing reintroduced it)
- Modify: `tools/RailCheck/L520_TheOnlyPublicationIsTheQueryPostfix.cs` (a third arm)
- Modify: `docs/laws.md`

- [ ] **Step 1: Add the third arm to L520 and see it RED.** Append to `L520_TheOnlyPublicationIsTheQueryPostfix.Check()`, before the positive control:

```csharp
            // (c) THE TWO BYPASSES ARE CLOSED. A window that reaches the queue without passing the mint
            // seam has no position, and a position-0 entry is refused by the journal (L521 arm c) — which
            // means such a window would simply never be journalled and would present in local order on
            // whichever peer happened to raise it. Both known bypasses must therefore claim a position.
            var restore = AccessTools.Method(typeof(GeoscapeViewSwitchQuery), "RestoreData");
            var mint = typeof(WindowJournal).GetMethod("MintHostPosition",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var restorePatch = typeof(WindowOrder).GetNestedType("RestoreClaimsPositions",
                BindingFlags.NonPublic | BindingFlags.Public);
            if (restore == null || mint == null)
                yield return "L520 premise-changed: GeoscapeViewSwitchQuery.RestoreData or " +
                             "WindowJournal.MintHostPosition did not resolve, so arm (c) cannot see the " +
                             "restore bypass it exists to close.";
            else if (restorePatch == null ||
                     !restorePatch.GetMethods(BindingFlags.Static | BindingFlags.NonPublic |
                                              BindingFlags.Public | BindingFlags.DeclaredOnly)
                                  .Any(m => Il.References(m, mint)))
                yield return "L520 restore-bypasses-the-journal: no patch on RestoreData claims a journal " +
                             "position. A restored request used to carry ordinal 0, i.e. it sorted first " +
                             "among its priority peers on every peer independently — which is a second " +
                             "order by another name, and it survives a save/load across every battle " +
                             "(GeoscapeView.GetStateSwitchInstanceData:1298-1300 → RestoreData:39-56, " +
                             "replayed at GeoscapeView.cs:349).";

            var initialPatch = typeof(WindowOrder).GetNestedType("InitialStateClaimsPositions",
                BindingFlags.NonPublic | BindingFlags.Public);
            if (initialPatch == null ||
                !initialPatch.GetMethods(BindingFlags.Static | BindingFlags.NonPublic |
                                         BindingFlags.Public | BindingFlags.DeclaredOnly)
                             .Any(m => mint != null && Il.References(m, mint)))
                yield return "L520 initial-state-bypasses-the-journal: no patch on UIStateInitial.EnterState " +
                             "claims a journal position for the mission-outcome modal it raises (:112). " +
                             "Closing this bypass means the RAISE claims a position; it does NOT mean the " +
                             "mission-outcome family becomes rail-covered, which stays out of scope " +
                             "(src/Rail/GeoWindowCoverage.cs:313, 11 ModalTypes).";
```

Add `using HarmonyLib;` and `using PhoenixPoint.Geoscape.View;` to the law's usings. Run RailCheck and confirm RED with both `L520 restore-bypasses-the-journal` and `L520 initial-state-bypasses-the-journal`.

- [ ] **Step 2: Close the restore bypass.** In `src/Rail/WindowOrder.cs`, replace the note at `:71` about unstamped restored requests and add the patch:

```csharp
        /// <summary>BYPASS 1, CLOSED (§A.8). A save/load across a battle replays the serialised queue
        /// through GeoscapeViewSwitchQuery.RestoreData (:39-56, from GetStateSwitchInstanceData:1298-1300,
        /// replayed at GeoscapeView.cs:349) WITHOUT going through QueryStateSwitch, so the mint seam never
        /// saw it. A restored request used to carry ordinal 0 and therefore sorted first on every peer
        /// independently. It now claims a position like anything else — postfix, so the native restore
        /// happens exactly as it always did whatever the verdict is.</summary>
        [HarmonyPatch(typeof(GeoscapeViewSwitchQuery), "RestoreData")]
        internal static class RestoreClaimsPositions
        {
            private static void Postfix(GeoscapeViewSwitchQuery __instance)
            {
                if (!EventPopup.InSession || !GeoModalMirror.HostMayPublish()) return;
                try
                {
                    var pending = RequestsField?.GetValue(__instance) as IList<GeoscapeViewStateSwitchRequest>;
                    if (pending == null) return;
                    foreach (var request in pending)
                    {
                        uint pos = WindowJournal.MintHostPosition();
                        string family = request?.State?.GetType().Name ?? "<unknown>";
                        GeoModalMirror.HostBroadcastQueued(request, pos);
                        WindowJournal.Append(pos, family, null);
                    }
                }
                catch (Exception ex)
                { MpLog.LogError("[MP][windows] restore position claim threw: " + ex); }
            }
        }
```

Read the real `RestoreData` signature at `GeoscapeViewSwitchQuery.cs:39-56` in the decompile before writing the `[HarmonyPatch]`, and cite it in the commit body.

- [ ] **Step 3: Close the mission-outcome bypass.** In `src/Rail/WindowOrder.cs`, beside the existing `:427` note about `UIStateInitial.EnterState:112`, add:

```csharp
        /// <summary>BYPASS 2, CLOSED (§A.8). The mission-outcome modal is opened by
        /// UIStateInitial.EnterState:112 AFTER the queue is rebuilt, so it never enters QueryStateSwitch at
        /// all (L117). The raise now claims a journal position. This does NOT make the mission-outcome
        /// family rail-covered — GeoWindowCoverage declares it not-covered (:313, 11 ModalTypes) and that
        /// stays out of scope; only the BYPASS is in scope.</summary>
        [HarmonyPatch(typeof(UIStateInitial), "EnterState")]
        internal static class InitialStateClaimsPositions
        {
            private static void Postfix()
            {
                if (!EventPopup.InSession || !GeoModalMirror.HostMayPublish()) return;
                try
                {
                    uint pos = WindowJournal.MintHostPosition();
                    WindowJournal.Append(pos, "UIStateGeoModal", null);
                    GeoModalMirror.HostBroadcastQueued(null, pos);
                }
                catch (Exception ex)
                { MpLog.LogError("[MP][windows] initial-state position claim threw: " + ex); }
            }
        }
```

Read `UIStateInitial.EnterState` in the decompile first and confirm whether the modal is raised unconditionally; if it is conditional, mirror the same condition here rather than minting a position for a window that will not exist. Cite the `file:line` either way.

- [ ] **Step 4: Build, run, kill the mutation, commit.** Run:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
.\deploy.ps1 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'
dotnet run -c Debug --project tools/RailCheck
pwsh -NoProfile -File tools/law-integrity.ps1
dotnet run -c Debug --project tools/RailSim -- --seed 1
```

Expect `RAILCHECK GREEN — laws-run=342/342 law-violations=0`, `law-integrity: OK`, `RAILSIM GREEN — scenarios=7/7 failures=0 seed=1`. No digest change: L520's body moved, its registration did not. Mutation: delete the `WindowJournal.MintHostPosition()` call from `RestoreClaimsPositions.Postfix` and use the literal `1u` instead. Run RailCheck, confirm `L520 restore-bypasses-the-journal: no patch on RestoreData claims a journal position.` and RED; revert; confirm GREEN 342/342. Append to the L520 row in `docs/laws.md`:

```
 THIRD MUTATION KILL (arm c): replacing `WindowJournal.MintHostPosition()` with the literal `1u` in `RestoreClaimsPositions.Postfix` → `L520 restore-bypasses-the-journal`, RED; reverted → GREEN 342/342
```

```powershell
git add src/Rail/WindowOrder.cs tools/RailCheck/L520_TheOnlyPublicationIsTheQueryPostfix.cs docs/laws.md
git commit -m "fix(windows): make restore and the initial-state raise claim journal positions"
```

Field verification: save and load across a battle and confirm the restored queue presents in the host's order on all three instances; report the presentation lines.

---

## Task 12: The centre-of-screen "enter deployment preparation" button (§A.10)

When a mission entry is globally dismissed because another player acted on it, the remaining peers get a centre-of-screen prompt. **`UIModuleConfirmation`, two-button form, zero adaptation. No hand-rolled overlay is authorised.**

**Files:**
- Modify: `src/Rail/GeoModalMirror.cs` (`ApplyVoidLocally` raises the prompt for the mission family)
- Test: `tools/RailSim/Scenarios.cs` — extend `a-global-dismissal-removes-it-everywhere` with the prompt decision

- [ ] **Step 1: Re-read the native widget before writing anything.** In `E:\DEV\PhoenixPoint\decompiled\AssemblyCSharp` read: `PhoenixPoint.Geoscape.View.ViewModules\UIModuleConfirmation.cs:11` (class), `:72` (`Init`), `:13` (`ConfirmationCallback`), `:15-19` (`ConfirmationCallbackResult`), `:100-110` (`Cancel`/`Close`), `:56-70` (`Awake` wires the buttons and starts hidden); `Base.UI\GeoscapeModulesData.cs:50` (`public UIModuleConfirmation ConfirmationModule;`); `Base.UI\LocalizedTextBind.cs:37-41` (`doNotLocalize`); `PhoenixPoint.Geoscape.View\GeoscapeView.cs:596` (`ToDeploymentState`). Record every `file:line` — they go in the commit body. The vanilla precedent to copy is `PhoenixPoint.Geoscape.View.ViewModules\UIModuleMutationSection.cs:299`.

- [ ] **Step 2: Add the pure decision first, so a law and the harness can execute it.** In `src/Rail/WindowJournal.cs` add:

```csharp
        /// <summary>Does a void of <paramref name="family"/> owe this peer the centre-of-screen "enter
        /// deployment preparation" prompt? Only when the entry was GLOBALLY dismissed because another
        /// player acted on it — i.e. only for the mission family, and only when this peer had not already
        /// read it. Pure, so it is executable with no game.</summary>
        internal static bool VoidOwesDeploymentPrompt(string family, bool wasStillUnread) =>
            wasStillUnread && ScopeOf(family) == DismissScope.Global;
```

- [ ] **Step 3: Raise the native prompt.** In `src/Rail/GeoModalMirror.ApplyVoidLocally`, after `CloseOpenCopyOf(journalPos)`, add:

```csharp
            if (WindowJournal.VoidOwesDeploymentPrompt(family, removed)) ShowDeploymentPrompt();
```

and add:

```csharp
        /// <summary>THE CENTRE-OF-SCREEN PROMPT (§A.10). NATIVE WIDGET, ZERO ADAPTATION: Phoenix Point
        /// ships no single-button centre-screen widget, and UIModuleConfirmation is the closest native
        /// element — a full-screen Overlay behind a centred Dialog with Title, ConfirmationMsg and two
        /// PhoenixGeneralButtons. The TWO-BUTTON form is used deliberately because it needs no adaptation
        /// at all: OK enters deployment preparation, Cancel dismisses the prompt.
        ///
        /// Init IS the show — Dialog.SetActive(true) is its first statement — and a click routes through
        /// the private Confirm(res), which invokes the callback and calls Close(). SO THE DIALOG CLOSES
        /// ITSELF and this method must NOT hide it afterwards. The module is always present and only the
        /// Dialog toggles (Awake starts it hidden), so there is nothing to instantiate and no prefab to
        /// clone.
        ///
        /// Init takes LocalizedTextBind, not string. The established answer in this mod is
        /// `new LocalizedTextBind(text, doNotLocalize: true)`, which Localize() returns verbatim — the
        /// same pattern as src/Rail/EventPopup.cs:1061. No new mechanism is needed.
        ///
        /// FORBIDDEN here and asserted in review: no `new GameObject()` overlay, no custom Canvas, no
        /// cloned button row, no third-party UI.</summary>
        private static void ShowDeploymentPrompt()
        {
            var view = GenericApplier.GeoLevel()?.View;
            var module = view?.GeoscapeModules?.ConfirmationModule;
            if (module == null)
            {
                MpLog.LogWarning("[Multiplayer][windows] no ConfirmationModule on this view — the " +
                                 "deployment prompt cannot be shown for this dismissal");
                return;
            }
            module.Init(
                data: null,
                confirmCallback: (result, data) =>
                {
                    if (result == ConfirmationCallbackResult.ConfirmationOK) view.ToDeploymentState();
                },
                confirmationMsg: new LocalizedTextBind(
                    "Another player has taken this mission. Enter deployment preparation?", true),
                title: new LocalizedTextBind("Mission taken", true));
        }
```

Verify `GeoscapeView.ToDeploymentState`'s real signature at the symbol (`GeoscapeView.cs:596`) before calling it, and verify `Init`'s parameter names against `UIModuleConfirmation.cs:72` — if a name differs, use positional arguments rather than guessing a name. Cite both in the commit body.

- [ ] **Step 4: Extend the harness scenario.** In `AGlobalDismissalRemovesItEverywhere` add:

```csharp
            if (!WindowJournal.VoidOwesDeploymentPrompt("UIStateRosterDeployment", wasStillUnread: true))
                yield return "a-global-dismissal-removes-it-everywhere: a global void of an UNREAD mission " +
                             "entry did not owe the centre-of-screen prompt. The remaining peers must get " +
                             "a way into deployment preparation — the decision to deploy is already taken, " +
                             "so the alternative is a mission they can see and cannot join.";
            if (WindowJournal.VoidOwesDeploymentPrompt("UIStateRosterDeployment", wasStillUnread: false))
                yield return "a-global-dismissal-removes-it-everywhere: a peer that had ALREADY read the " +
                             "entry was offered the prompt again. It is offered once, to the peers whose " +
                             "entry the void removed.";
            if (WindowJournal.VoidOwesDeploymentPrompt("UIStateGeoModal", wasStillUnread: true))
                yield return "a-global-dismissal-removes-it-everywhere: a LOCAL family owed the deployment " +
                             "prompt. Only the mission family is GLOBAL and only a global dismissal owes " +
                             "the prompt.";
```

- [ ] **Step 5: Build, run, commit.** Run:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
.\deploy.ps1 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'
dotnet run -c Debug --project tools/RailCheck
pwsh -NoProfile -File tools/law-integrity.ps1
dotnet run -c Debug --project tools/RailSim -- --seed 1
```

Expect `RAILCHECK GREEN — laws-run=342/342 law-violations=0`, `law-integrity: OK`, `RAILSIM GREEN — scenarios=7/7 failures=0 seed=1`. Then:

```powershell
git add src/Rail/WindowJournal.cs src/Rail/GeoModalMirror.cs tools/RailSim/Scenarios.cs
git commit -m "feat(windows): offer the native confirmation prompt after a global dismissal"
```

Commit body must list every decompile `file:line` from Step 1 and state that no UI was constructed. Field verification: two instances, one player launches a mission, the other sees the centre-screen prompt and OK enters deployment preparation — report the observation.

---

## Task 13: Recorded-trace replay (§C.2)

The harness tests the mod's model, not the game. §C.2 makes trace replay MANDATORY, not optional: the model must be exercised against real inputs.

**Files:**
- Create: `tools/RailSim/TraceReplay.cs`
- Modify: `tools/RailSim/Program.cs` (a `--trace <path>` argument)
- Modify: `tools/RailSim/Scenarios.cs` (register the replay as a scenario when a trace is present)

- [ ] **Step 1: Write the replay.** Create `tools/RailSim/TraceReplay.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Multiplayer.Network.Sync;

namespace RailSim
{
    /// <summary>
    /// RECORDED-TRACE REPLAY (§C.2). The generated scenarios exercise the model against inputs the harness
    /// invented; this exercises it against inputs a REAL 3-instance session produced. It is the paired
    /// half of the honesty statement in Program's header: the harness cannot see a wrong Harmony seam, a
    /// patch that does not bind, or the Unity hierarchy — replaying real traffic is what stops the model
    /// drifting away from what the game actually emits.
    ///
    /// Trace format: any log file containing lines the mod itself wrote, matched on
    /// `[MP][windows] journal pos=<n> family=<name>`. Nothing else in the file is read, so a trimmed log
    /// and a full one behave identically.
    /// </summary>
    internal static class TraceReplay
    {
        private static readonly Regex Line =
            new Regex(@"\[MP\]\[windows\] journal pos=(\d+) family=(\S+)", RegexOptions.Compiled);

        internal static IEnumerable<string> Replay(string path, int seed)
        {
            if (!File.Exists(path))
            {
                yield return "trace-replay: no trace at '" + path + "'. §C.2 makes replay MANDATORY — " +
                             "capture a real 3-instance session's multiplayer*.log into tools/RailSim/traces " +
                             "and pass --trace. A model validated only against generated inputs is the " +
                             "failure mode this pairing exists to prevent.";
                yield break;
            }

            var recorded = new List<KeyValuePair<uint, string>>();
            foreach (var raw in File.ReadLines(path))
            {
                var m = Line.Match(raw);
                if (m.Success) recorded.Add(new KeyValuePair<uint, string>(
                    uint.Parse(m.Groups[1].Value), m.Groups[2].Value));
            }
            if (recorded.Count == 0)
            {
                yield return "trace-replay: '" + path + "' contains no '[MP][windows] journal pos=' lines. " +
                             "Either the trace predates the journal or the log line was renamed; a replay " +
                             "over zero records is a pass nobody earned.";
                yield break;
            }

            // Feed the recorded stream through the injected transport, so real positions meet simulated
            // reordering — the exact combination that produced P1 in the field.
            var clock = new SimClock();
            var net = new SimNet(seed, clock);
            WindowJournal.Reset();
            foreach (var rec in recorded)
            {
                var name = System.Text.Encoding.UTF8.GetBytes(rec.Value);
                var frame = new byte[4 + name.Length];
                Buffer.BlockCopy(BitConverter.GetBytes(rec.Key), 0, frame, 0, 4);
                Buffer.BlockCopy(name, 0, frame, 4, name.Length);
                net.Send(0, frame);
            }
            clock.Advance(5.0f);
            foreach (var msg in net.Drain())
                WindowJournal.Append(BitConverter.ToUInt32(msg.Value, 0),
                                     System.Text.Encoding.UTF8.GetString(msg.Value, 4, msg.Value.Length - 4),
                                     msg.Value);

            var presented = new List<uint>();
            JournalEntry e;
            while (WindowJournal.TryRead(out e)) presented.Add(e.Pos);
            WindowJournal.Reset();

            var expected = recorded.Select(r => r.Key).Distinct().OrderBy(x => x).ToList();
            if (!presented.SequenceEqual(expected))
                yield return "trace-replay: replaying " + recorded.Count + " recorded raises through the " +
                             "reordering transport presented [" + string.Join(",", presented.Take(20)) +
                             "…] but the host minted [" + string.Join(",", expected.Take(20)) + "…]. " +
                             "Real traffic must present in the host's order exactly as generated traffic " +
                             "does.";
        }
    }
}
```

- [ ] **Step 2: Wire the argument.** In `tools/RailSim/Program.cs`, add beside the `--seed` parse:

```csharp
            string trace = null;
            var ti = Array.IndexOf(args, "--trace");
            if (ti >= 0 && ti + 1 < args.Length) trace = args[ti + 1];
```

pass `trace` into `Run(seed, trace)`, and in `Run` append after the generated scenarios:

```csharp
            if (trace != null)
            {
                ran++;
                try { failures.AddRange(TraceReplay.Replay(trace, seed)); }
                catch (Exception ex) { failures.Add("trace-replay CRASHED: " + ex.Message); }
            }
```

- [ ] **Step 3: Emit the line the replay reads.** In `src/Rail/WindowJournal.Append`, after the successful insert, add:

```csharp
            if (MpDiag.On)
                MpLog.Log("[Multiplayer][windows] journal pos=" + pos + " family=" + (family ?? "none") +
                          " unread=" + _unread.Count);
```

with `using Multiplayer.Net;` (or whatever namespace `MpDiag` lives in — read `src/Net/MpDiag.cs` and use it exactly). The `MpDiag.On` guard is mandatory: this runs at rail-batch rate, and the concatenation must be INSIDE the guard, not just the write — one live session logged 23642 unguarded diag lines and cost real frame time in string building alone.

- [ ] **Step 4: Run both modes and commit.** Run:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
.\deploy.ps1 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'
dotnet run -c Debug --project tools/RailCheck
dotnet run -c Debug --project tools/RailSim -- --seed 1
dotnet run -c Debug --project tools/RailSim -- --seed 1 --trace tools/RailSim/traces/multiplayer.log
```

Expect `RAILCHECK GREEN — laws-run=342/342 law-violations=0`, `RAILSIM GREEN — scenarios=7/7 failures=0 seed=1` without a trace, and — once a real session's log is captured into `tools/RailSim/traces/` — `RAILSIM GREEN — scenarios=8/8 failures=0 seed=1` with it. Without a captured trace the second command prints `trace-replay: no trace at …` and RED, which is correct: §C.2 makes replay mandatory. Capture the trace before claiming this task complete.

```powershell
git add tools/RailSim/TraceReplay.cs tools/RailSim/Program.cs src/Rail/WindowJournal.cs tools/RailSim/traces/multiplayer.log
git commit -m "test(railsim): replay a recorded session trace through the journal model"
```

---

## Task 14: A log line names a player by NUMBER, never by NAME (§2.4, Q7)

**Files:**
- Modify: `src/Lobby/SessionManager.cs:455`, `:475`

- [ ] **Step 1: Fix the two sites.** In `src/Lobby/SessionManager.cs:455` change:

```csharp
                MpLog.LogWarning($"[Multiplayer] Peer {steamId} ({client.PlayerName}) PAUSED: {reason}. …");
```

to drop the name clause and keep the Steam id (a Steam id is an account handle, not a display name, and is already logged elsewhere):

```csharp
                MpLog.LogWarning($"[Multiplayer] Peer {steamId} PAUSED: {reason}. …");
```

and at `:475` change:

```csharp
                MpLog.Log($"[Multiplayer] Peer {steamId} ({client.PlayerName}) RESUMED.");
```

to:

```csharp
                MpLog.Log($"[Multiplayer] Peer {steamId} RESUMED.");
```

Keep the `…` tails of both messages exactly as they are — read the two lines and reproduce them verbatim minus the `({client.PlayerName})` clause. If a slot number is in hand at either site, add `slot={slotIndex}` in the existing style (`slot=` at `src/SaveTransfer/SaveTransferCoordinator.cs:2145`).

- [ ] **Step 2: Confirm the in-game notices are NOT touched.** `SessionLifecycle.FormatLeaveNotice` / `FormatConnectionLostNotice` / `FormatReconnectedNotice` / `FormatCountdownCancelledNotice` (`src/Lobby/SessionLifecycle.cs:36-58`), the lobby roster, the Steam-invite row (`src/Transport/SteamInvite.cs:25`, `:40`) and the gate panel (`src/Lobby/NetworkGatePanel.cs:426`, `:447`) are PLAYER-FACING and keep names. Run `Grep` for `PlayerName` across `src/` and confirm the only remaining `MpLog` interpolations of it are zero:

```powershell
Select-String -Path src\**\*.cs -Pattern 'MpLog\.[A-Za-z]+\([^)]*PlayerName'
```

Expect no output.

- [ ] **Step 3: No law.** §2.4 authorises a content law ONLY if it can carry an executable guard and a compile-valid `src/` semantic mutation kill. Asserting "no call to an `MpLog` method takes `ClientInfo.PlayerName` (or a local loaded from it) as an argument" is an argument-ORIGIN trace, strictly harder than L432's callee-name check, and a law that cannot fail is worse than none. **Leave this as documented discipline. Do NOT add it to `tools/vacuity-exempt.txt` under any circumstances.** Record the decision in the commit body.

- [ ] **Step 4: Build, verify, commit.** Run:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
.\deploy.ps1 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'
dotnet run -c Debug --project tools/RailCheck
pwsh -NoProfile -File tools/law-integrity.ps1
```

Expect `RAILCHECK GREEN — laws-run=342/342 law-violations=0` (L432 stays green — nothing here adds a logging door) and `law-integrity: OK`. Then:

```powershell
git add src/Lobby/SessionManager.cs
git commit -m "fix(lobby): identify a paused or resumed peer by id, never by persona name"
```

---

## Spec coverage — every section to its task

| Spec section | Decision | Task |
|---|---|---|
| §A.1 one authority: the host's order | host mints, client reconciles | 4, 5 |
| §A.2 append-only, per-peer cursor, read ⇒ deleted | `WindowJournal.TryRead` deletes; no cap | 4, 7 |
| §A.2b not persisted; manual save needs an empty local journal | no codec, no restore path; `JournalSaveGate` | 4, 8 |
| §A.2c an autosave always proceeds, unread entries lost | `MaySave` exempts `SaveType.Autosave`; L525 arm (d) forbids patching `AutosaveGame` | 8 |
| §A.3 session lifetime, across the tactical boundary | journal is mod-owned static state; restore claims positions | 4, 11 |
| §A.4 creation/queueing/publication screen-independent | mint at the postfix; L521 executes with no view | 4 |
| §A.5 dismissal is a declared property; host-minted void | `FamilyScope`, `ScopeOf`, `HostMintVoid`; L526 | 9 |
| §A.6 removals reconciled; cap removed; 4096 canary | `TrimQueue` deleted; canary; `DropUnservableQueued` demoted | 7, 9 |
| §A.7 delete the second ordering system | `HostOrderKey`, `DurablePriorityHead`, `Reorder`, settle, `ForNewWindow` | 5, 6 |
| §A.8 close the two bypasses | `RestoreClaimsPositions`, `InitialStateClaimsPositions`; L520 arm (c) | 11 |
| §A.9 CAPTURE-and-publish, one strategy per concern | one broadcast; reflective replay deleted; `ClientSimGate` untouched | 2, 3 |
| §A.10 centre-of-screen button, native widget | `UIModuleConfirmation`, two-button form | 12 |
| §A.11 the never-created case removed; L475 re-expressed | `_everOnMapSurface` deleted; L475 strengthened | 10 |
| §C.1 harness + all five properties | 1 (skeleton), 4 (P1), 10 (P2), 12 (P3 is plan 2's), 9 (P4, P5) | 1, 4, 9, 10 |
| §C.2 what the harness cannot prove + trace replay | verbatim header; `TraceReplay` | 1, 13 |
| §C.3 roles separated in every new law | stated in each law's header and enforced by its arms | 2–11 |
| §2.4 log a player by number, never by name | the two `SessionManager` sites | 14 |
| §2.4 a content law only if it can carry a real guard and kill | explicitly NOT written, with the reason | 14 |
| R1 order of deletion | Task 6 runs only after 4 and 5 are green | 6 |
| R2 doubled broadcast before the append | Task 2 precedes Task 4 | 2, 4 |
| R5 removing the cap | read ⇒ delete plus the canary | 7 |
| R7 no law legitimising a duplicate | L496 retired; every law says "one mechanism" | 6 |
| R8 the save gate becoming a blocker | L525 arm (c): the signature cannot see a remote peer | 8 |

**§C.1 property 3** — "a surface whose declared prefixes were untouched did NOT repaint" — belongs to scoped reactivity and is implemented by `2026-08-15-scoped-reactivity.md`, which builds on the same harness. It is deliberately NOT in this plan.

**Not in scope, by §4:** making the declared not-covered families covered, per-widget opt-in sync, replicating a peer's surface, persisting the journal, touching the ~63 kindless `MarkDirty()` sites, and any broad refactor "while we are in there".

