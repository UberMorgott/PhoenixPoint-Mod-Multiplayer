# Scoped Reactivity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Carry the exact changed rail path all the way to the repaint decision, mark dirty only when a value actually differs, and let a surface declare the static path prefixes it reads — so an unrelated peer's manufacturing tick stops repainting (and resetting) the soldier-edit screen.

**Architecture:** `GenericApplier.ApplyEntry` already holds `(kindId, path, fieldIdx, subKey, value)` and throws the path away at `touched.Add(entity)`. This plan keeps it: `touched` gains the path, `UiEventMap.Fire` forwards it, and `OpenUiRepaint` accumulates a set of touched paths BESIDE the existing global bool. A surface declares static path prefixes in one table; an undeclared surface repaints on everything, so a forgotten surface degrades to today's behaviour and never to stale data. Separately and mandatorily, every repaint path with model/animation state is routed to a non-destructive paint.

**Tech Stack:** C# (net472), Harmony 2.x, Unity 2019 (Phoenix Point), RailCheck executable-law harness (`tools/RailCheck`), the engine-free deterministic simulation harness (`tools/RailSim`).

---

## Read before the first task

- Spec (the authority, zero open questions): `E:\DEV\PhoenixPoint\Multiplayer2\docs\superpowers\specs\2026-08-15-window-journal-and-scoped-reactivity-design.md` — sections **B** and **C** are this plan; section **A** is the companion plan.
- Repo rules: `E:\DEV\PhoenixPoint\Multiplayer2\CLAUDE.md`, `E:\DEV\PhoenixPoint\Multiplayer2\docs\laws.md`

## Relationship to the window-journal plan — READ THIS BEFORE TASK 1

This plan is **independent and runnable on its own**, but it **shares the deterministic simulation harness** (spec §C) with `docs/superpowers/plans/2026-08-15-window-journal.md`.

- **If the window-journal plan has already landed Task 1:** `tools/RailSim/` exists with `RailSim.csproj`, `Program.cs` (the `SimClock`, the `SimNet` and the verdict line) and `Scenarios.cs`. **SKIP Task 1 entirely** and start at Task 2; Task 5 then simply adds a scenario to the existing `Scenarios.cs`.
- **If it has not:** run Task 1 here. It builds **exactly the same four files with exactly the same content** as that plan's Task 1 — `tools/RailSim/RailSim.csproj`, `tools/RailSim/Program.cs`, `tools/RailSim/Scenarios.cs`, `tools/RailSim/traces/README.md`. **These are the duplicated tasks.** If the other plan later reaches its Task 1 and the files already exist with the same content, it skips them too; nothing needs merging.
- Nothing else is shared. This plan never touches `src/Rail/WindowJournal.cs`, `WindowOrder.cs`'s ordering members, `GeoWindowCoverage.cs`, `DurableInbox*.cs` or `EventPopup.cs`. The one overlap is Task 8's `MapStates` collapse, which deletes a member of `DurableWindowRegistry.cs`; that task is written so it is a **no-op if the other plan already deleted the file**, and its law arm asserts the invariant either way.
- **Law numbers do not collide:** this plan claims `L540`–`L545`; the window-journal plan claims `L520`–`L526`. The highest existing id is `L516`.

## Hard constraints that no task in this plan may violate

- **SERIALISE EVERYTHING.** Every task touches `src/` or `tools/RailCheck`. Agents editing this repo concurrently sweep each other's commits and collide on law numbers. **One agent at a time, one task at a time.** Read-only research may run in parallel; nothing else may.
- **Reactivity is mandatory and this plan makes repaints RARER — so every task must show it did not make one STALE.** Requiring a player to leave a screen and re-enter is a DEFECT. The safe direction is always "repaint anyway": an undeclared surface, a null path, an unreadable model and a kindless mark all repaint on everything, by construction, and each task asserts that.
- **Do NOT touch the ~63 kindless `MarkDirty()` sites** (AssignSync 9, PersonnelSync 15, MissionSync 12, VehicleSync 6, GenericApplier 8 structural, IntentRail 3, DeployPrep 2, DeploymentWindow 2, EquipSync 2, TradeSync 1). They carry no path and MUST keep an unconditional repaint-everything arm. Task 6 asserts that they still do; it does not "improve" them.
- **The global `_dirty` bool STAYS.** This plan adds a path set beside it; it does not replace it.
- **No quorum, no gate waiting on another human's ACTION.** Nothing in this plan waits at all — the existing per-frame coalescing and the drag/typing defer (`LocalInputInFlight`, ≤300 frames) are the batch-end evaluate and are kept as they are.
- **Native UI only.** This plan draws nothing; it decides whether an existing native refresh runs and which overload it reaches.
- **No guessed engine signature.** Read it from `E:\DEV\PhoenixPoint\decompiled\AssemblyCSharp` or `E:\DEV\PhoenixPoint\refs\TFTV-src` and cite `file:line` in the commit body. Guessing the repaint method broke this subsystem three times already.
- **Commits are LOCAL on `main`. Never push.** `git add` with explicit paths only — `TFTV.dll` and `TFTV.meta.json` at the repo root must stay untracked, so `git add -A` is forbidden.
- **Never `--update`, never weaken a law, never add to `tools/vacuity-exempt.txt`.**
- The repo must be GREEN and shippable after every commit.

## The law ceremony — the full recipe, needed by every task that adds a law

1. Create `tools/RailCheck/L<n>_<Name>.cs` with `internal static class L<n>_<Name> { internal static IEnumerable<string> Check() { … } }`. It MUST contain a `yield return "L<n> premise-changed: …"` arm or a `positive-control` arm, or `tools/law-integrity.ps1` fails it as vacuous.
2. Register it in `tools/RailCheck/Program.cs` with `Add(laws, () => L<n>_<Name>.Check());` — never `laws.AddRange(...)`.
3. Increment `files=` in `tools/law-count.txt` by 1. It reads `files=276` before this plan's first law lands; if the companion plan landed first it reads higher — increment whatever is there.
4. Increment `ExpectedLawRegistrations` in `tools/RailCheck/Program.cs:488` by 1 and amend the comment above it naming the added law. It reads `336` before this plan's first law lands.
5. Increment the three `336` literals in `tools/RailCheck/L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs:98-102`: `LawExecutionIsValid(336, 336, …)`, `LawExecutionIsValid(336, 335, …)`, `LawExecutionIsValid(336, 336, "wrong")`.
6. Run RailCheck once; it prints `RAILCHECK ABORTED — executed N/… identities=N, digest=<hex>`. Paste `<hex>` into `ExpectedExecutionIdentityDigest` (`Program.cs:494`) and into the two `L193` digest string literals at `:99` and `:101`.
7. Run `pwsh -NoProfile -File tools/law-integrity.ps1`; it prints `law identity set changed (digest <hex> != committed …)`. Paste that `<hex>` into `$expectedRegistrationDigest` (`tools/law-integrity.ps1:50`) and amend the comment above it.

Then prove the law can fail: apply a compile-valid semantic mutation to `src/`, run RailCheck, see the NAMED law RED, revert, run RailCheck, see GREEN — and record the mutation and the RED line in `docs/laws.md`.

## Verification commands (quote these literally; never claim a result you did not run)

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
.\deploy.ps1 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'
dotnet run -c Debug --project tools/RailCheck
pwsh -NoProfile -File tools/law-integrity.ps1
dotnet run -c Debug --project tools/RailSim -- --seed 1
```

Baseline to restore before every commit (as of 2026-08-15, before either plan lands):

```
RAILCHECK GREEN — laws-run=336/336 law-violations=0
laws: 276 file(s) + 60 inline = 336
```

Six laws are added here, so the final state of this plan alone is `laws-run=342/342` and `files=282`.

---

## File structure

### Created

| Path | Single responsibility |
|---|---|
| `tools/RailSim/RailSim.csproj`, `tools/RailSim/Program.cs`, `tools/RailSim/Scenarios.cs`, `tools/RailSim/traces/README.md` | **Task 1 only, and only if the companion plan has not already created them.** The engine-free harness: injectable clock, injectable seeded transport, scenario runner, verdict line, and the §C.2 limitations stated verbatim in `Program`'s header. |
| `tools/RailCheck/L540_MarkOnlyOnValueInequality.cs` | A mark is raised by a value that DIFFERS, never by "a write happened". |
| `tools/RailCheck/L541_AnUndeclaredSurfaceRepaintsOnEverything.cs` | Safe degradation: no declaration, a null path, an empty touched set ⇒ repaint. Asserted, never assumed. |
| `tools/RailCheck/L542_TheKindlessSitesStillRepaintEverything.cs` | The ~63 kindless `MarkDirty()` sites still reach the unconditional arm, and `L38`'s blind spot outside `UiEventMap.Fire` is closed. |
| `tools/RailCheck/L543_NoHandRolledSignatureSurvives.cs` | No hand-rolled read-set signature survives beside a declared prefix set, and there is exactly ONE `MapStates` set in the assembly. |
| `tools/RailCheck/L544_NoPathSegmentIsAnIndex.cs` | The keyless arm still reaches `Incident` and has no blob/index fallback. |
| `tools/RailCheck/L545_PatchDoNotRebuild.cs` | No repaint path reaches a `resetAnimation: true` overload or `RebuildCharacter` for a surface with model/animation state, and Exit+Enter is unreachable for those surfaces. |

### Modified

| Path | Change | Direction |
|---|---|---|
| `src/Rail/GenericApplier.cs` (182.6 KB) — `:272-278`, `:1272`, `:323`, `:508` | `touched` becomes a set of `(entity, path, field)`; `LeafChanged` gates the mark on value inequality; `UiEventMap.Fire` receives the paths. | **GROWS by ~60 lines** — one new pure predicate, one richer touched record. |
| `src/Rail/UiEventMap.cs` (77.2 KB) — `:75-190` (the 8 arms), `:325` (`ReseedIdentityDisplay`), `:661-665` (`IgnoredKinds`), `:939` (`RepaintAugmentScreen`, the model to copy) | `Fire` forwards the path to `MarkDirty(kind, geo, path)`; the new `UiNativeRepaint.DeclaredPrefixes` table lands beside `IgnoredKinds`; `ReseedIdentityDisplay` stops being reachable from an unrelated change. | **GROWS by ~40 lines** (the declaration table), shrinks by the reseed's unconditional arm. |
| `src/Rail/OpenUiRepaint.cs` (74.1 KB) | **ADDS:** `MarkDirty(Type, GeoLevelController, string)`, `_touchedPaths`, `_scopedDirty`, `SurfaceRepaints`. **DELETES:** `RosterSignature` (`:233`), `CrewSignature` (`:324`), `CrewSlotKey` (`:350`), `AgendaSignature` (`:465`), `InfoBarKey` (`:566`) and the four `RepaintNeeded` call sites that fed them. **KEEPS:** `RepaintNeeded` (`:439`) as the fallback primitive, `AgendaNeedsRebuild` (`:424`) and its L516 visibility ordering, `MarkDirty()` (`:728`), `MarkDirty(Type, geo)` (`:746`), `MarkHudDirty` (`:785`), `FlushIfDirty` (`:893`), `LocalInputInFlight` (`:931`), `RepaintOpenGeoscapeScreen` (`:959`), `Repaint` (`:985`). **FIXES:** `RefreshInfoBar` (`:511`) asks `bar == null` / `_context == null` BEFORE `RepaintNeeded`. | **NET SHRINKS** — five signature builders (~150 lines with their doc comments) leave, ~70 lines of scoping arrive. |
| `src/Rail/DiffEngine.cs` (134.9 KB) — `:1283-1302` | **UNCHANGED CODE**, but L544 pins the keyless-abort arm so no index fallback can be added later. | **UNTOUCHED** — asserted only. |
| `src/Rail/DurableWindowRegistry.cs` (32.4 KB) — `:334-335`, `:368-369` | `MapStates` (`HashSet<Type>`) and its by-Type comparison in `MayPresent` deleted; `MayPresent` routes through `WindowOrder.MapStates` by NAME. **No-op if the companion plan already deleted the file.** | **SHRINKS** (or is already gone). |
| `src/Rail/WindowOrder.cs` (40.6 KB) — `:245-248`, `:355-359` | **UNCHANGED.** Its `MapStates` (`HashSet<string>`, name form) is the surviving copy — `WindowOrder.cs:243-244` records the deliberate reason: "DECLARED AS THE MAP AND NOT AS THE SCREENS, so an unknown state HOLDS rather than interrupts", and a name set can also name a state from a mod whose Type this assembly cannot reference. | **UNTOUCHED — do not edit.** |
| `src/Rail/RailTypes.cs` (36.3 KB) — `:132-134` | **UNTOUCHED.** The `BaseStat.StatChangeEvent` echo → `RefreshCrewBars` is a NON-DESTRUCTIVE paint (L497) and stays. | **UNTOUCHED.** |
| `tools/RailCheck/L38_*.cs` | Extended so its "no arm of `Fire` reaches parameterless `MarkDirty()`" coverage reaches beyond `UiEventMap.Fire` — or, if that cannot be done without weakening it, left alone and superseded by `L542`, which is then written to cover the kindless sites. Decide by reading the law; record the decision in the commit body. | Reworded or untouched. |
| `tools/RailCheck/Program.cs`, `L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs`, `tools/law-count.txt`, `tools/law-integrity.ps1`, `docs/laws.md` | Six registrations, six count bumps, twelve digest updates, six law rows plus mutation kills. | Ceremony. |

### Not touched by this plan (say no, loudly)

The ~63 kindless `MarkDirty()` call sites, the global `_dirty` bool, the per-frame coalescing and the drag/typing defer, `src/Rail/WindowJournal.cs` and every window-ordering file, `src/Rail/ClientSimGate.cs`, and every surface that has no declaration — an undeclared surface keeps today's behaviour on purpose.

---

## Task 1: The RailSim harness (SKIP if `tools/RailSim/` already exists)

**DUPLICATED TASK.** This is byte-identical to Task 1 of `docs/superpowers/plans/2026-08-15-window-journal.md`. Run `Test-Path tools/RailSim/RailSim.csproj` first: if it is `True`, skip this whole task and go to Task 2.

**Files:**
- Create: `tools/RailSim/RailSim.csproj`, `tools/RailSim/Program.cs`, `tools/RailSim/Scenarios.cs`, `tools/RailSim/traces/README.md`
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
    /// therefore PAIRED with recorded-trace replay from a real session, so the model is exercised against
    /// real inputs and not only against generated ones.
    ///
    /// The feasibility condition for deterministic simulation testing is INJECTABLE CLOCK AND TRANSPORT,
    /// not "can you boot the engine" (TigerBeetle VOPR, WarpStream, Antithesis). The rail and the queue
    /// are pure C# and meet it: RailMeta's digest clock is already a BCL Stopwatch chosen precisely so a
    /// headless harness can execute the codec in-process (src/Rail/RailMeta.cs:1449-1453).
    ///
    /// Every scenario asserts an OBSERVABLE HISTORY — what each peer presented, what each surface
    /// repainted — never the shape of a seam. Runs are seeded and reproducible from the seed alone.
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
    /// scenario says so.</summary>
    internal sealed class SimClock
    {
        internal float Now { get; private set; }
        internal void Advance(float seconds) { Now += seconds; }
    }

    /// <summary>THE INJECTED TRANSPORT. An in-memory link with seeded per-message delay, so a run
    /// reorders identically for identical seeds and differently for different ones. Delivery is by
    /// (dueAt, sequence) — never by insertion order — so out-of-order arrival is the DEFAULT shape a
    /// scenario sees.</summary>
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

- [ ] **Step 3: Write the first scenario.** Write `tools/RailSim/Scenarios.cs`:

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
        /// able to produce a different one — otherwise the harness is not simulating reordering at all.</summary>
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

- [ ] **Step 4: Run it and see it pass, then see the arm bite.** Run:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
dotnet run -c Debug --project tools/RailSim -- --seed 1
```

Expect `RAILSIM GREEN — scenarios=1/1 failures=0 seed=1`. Temporarily set `SimNet.MaxDelaySeconds = 0f` and re-run; expect

```
  seeded-transport-is-reproducible: 32 different seeds all produced the delivery order 0,1,2,3,4,5,6,7. The transport is not reordering, so every ordering scenario in this harness would pass without proving anything.
RAILSIM RED — scenarios=1 failures=1 seed=1
```

Restore `0.4f` and re-run for GREEN.

- [ ] **Step 5: Write the trace README.** Write `tools/RailSim/traces/README.md`:

```markdown
# Recorded traces

Drop a real session's `multiplayer.log` (and its `multiplayer-2.log` / `multiplayer-3.log` siblings)
here. Trace replay reads the mod's own `[MP]` lines out of them and replays that exact sequence
through the harness, so the model is exercised against real inputs rather than only generated ones
(spec §C.2).

Capture: run a 3-instance co-op session, then copy
`%USERPROFILE%\AppData\LocalLow\Snapshot Games Inc\Phoenix Point\Multiplayer\multiplayer*.log`
into this folder. The files are large; commit only the trimmed lines.
```

- [ ] **Step 6: Confirm RailCheck is untouched and commit.** Run:

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

## Task 2: Mark dirty ONLY on value inequality (§B.2)

Marking on WRITE is the direct cause of the manufacturing-resets-the-soldier symptom: a rail batch writes a leaf with the value it already had, the applier marks, and the open screen rebuilds. Bevy's `set_if_neq` and Unity DOTS chunk version numbers are the prior art; reference memoization is useless here because the game mutates state in place, so the comparison must be by VALUE or hash.

**Files:**
- Create: `tools/RailCheck/L540_MarkOnlyOnValueInequality.cs`
- Modify: `src/Rail/GenericApplier.cs:1272` (the mark site) and a new pure `LeafChanged` beside it
- Modify: `tools/RailCheck/Program.cs`, `L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs`, `tools/law-count.txt`, `tools/law-integrity.ps1`, `docs/laws.md`

- [ ] **Step 1: Add the pure predicate.** In `src/Rail/GenericApplier.cs`, immediately above the `touched.Add(entity);` site (`:1272`), add:

```csharp
        /// <summary>
        /// DID THIS LEAF ACTUALLY CHANGE? A mark is raised by a value that DIFFERS, never by "a write
        /// happened" (§B.2, Bevy set_if_neq / DOTS chunk versions). Marking on write is the direct cause of
        /// the reported symptom: an unrelated peer's manufacturing tick rewrites leaves with the values
        /// they already hold, the applier marks, and the open soldier-edit screen rebuilds — resetting the
        /// model and restarting the animation.
        ///
        /// COMPARED BY VALUE OR BY BYTES, NEVER BY REFERENCE. The game mutates state in place, so
        /// reference memoization is useless here (reselect FAQ, §2.5) — two references being equal says
        /// nothing about whether the contents moved.
        ///
        /// PURE and internal so RailCheck executes the real one with no game.
        /// </summary>
        internal static bool LeafChanged(object before, object after)
        {
            if (before == null || after == null) return !ReferenceEquals(before, after);
            var a = before as byte[];
            var b = after as byte[];
            if (a != null && b != null)
            {
                if (a.Length != b.Length) return true;
                for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return true;
                return false;
            }
            // Equals, not ==: a boxed value type compares by value here, and a string compares by
            // content. A reference type with no Equals override degrades to reference equality, which is
            // the SAFE direction — it reports "changed" and costs a repaint, never a stale screen.
            return !before.Equals(after);
        }
```

- [ ] **Step 2: Gate the mark on it.** At `src/Rail/GenericApplier.cs:1272`, capture the field's value BEFORE the switch that writes it and compare after. Read the surrounding method first (`ApplyEntry`, from `:1025`) and place the capture immediately before the `switch` that dispatches on `FieldClass`:

```csharp
                object beforeValue = null;
                try { beforeValue = field.GetValue(target); } catch { }
```

then replace `touched.Add(entity);` with:

```csharp
                object afterValue = null;
                try { afterValue = field.GetValue(target); } catch { }
                // §B.2: only a value that DIFFERS raises a mark. An unreadable field (either read threw)
                // compares as changed and marks — the safe direction, because REACTIVITY is a hard mandate
                // and an unreadable model may cost a repaint but may never cost a stale screen.
                if (LeafChanged(beforeValue, afterValue)) touched.Add(entity);
```

Leave `MarkOrderChange(geo, path, entity, field.Name);` and the `NoteScopedAnswer(path)` line exactly where they are and unconditional — they are not repaint marks.

- [ ] **Step 3: Write L540.** Create `tools/RailCheck/L540_MarkOnlyOnValueInequality.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L540 — A MARK IS RAISED BY A VALUE THAT DIFFERS, NEVER BY "A WRITE HAPPENED".
    ///
    /// THE REPORTED SYMPTOM. An unrelated peer's manufacturing tick repainted the soldier-edit screen,
    /// which routes to a DESTRUCTIVE native refresh (UIStateEditSoldier.DisplaySoldier →
    /// UIModuleActorCycle.DisplaySoldier(c, resetAnimation: true) → CommonCharacterUtils
    /// .ResetCharacterAnimation = Animator.Play(0,-1,0f)), resetting the soldier model and its animation.
    /// A large share of those repaints were owed to nothing: the batch rewrote leaves with the values they
    /// already held. Change detection must fire on VALUE INEQUALITY (Bevy set_if_neq, Unity DOTS chunk
    /// version numbers, §2.5).
    ///
    /// COMPARED BY VALUE OR BYTES, NEVER BY REFERENCE — the game mutates state in place, so reference
    /// memoization is useless (reselect FAQ, §2.5). That is arm (d), and it is the arm a naive
    /// implementation fails.
    ///
    /// ARMS, all EXECUTED against the real GenericApplier.LeafChanged with no game:
    ///   (a) equal-value-marks — two equal boxed values do NOT change.
    ///   (b) different-value-silent — two different boxed values DO change (without this the law passes
    ///       against a predicate that always says "unchanged", which would freeze every screen).
    ///   (c) equal-bytes-mark / different-bytes-silent — the blob path, both directions.
    ///   (d) reference-equality-used — two DISTINCT byte arrays with identical contents compare as
    ///       UNCHANGED. A reference compare would call them different and mark on every batch.
    ///   (e) null-is-not-a-change — null vs null is unchanged; null vs a value is changed. An unreadable
    ///       field must degrade to "changed", never to "unchanged".
    ///
    /// ROLES SEPARATED (§C.3): LeafChanged is a pure function of two values with no peer, no session and
    /// no role — there is nothing role-dependent for one role to hide from the other.
    ///
    /// Falsify (compile-valid src mutations, each named): `LeafChanged => true;` → (a), (c); `=> false;`
    /// → (b); replace the byte loop with `ReferenceEquals(a, b)` → (d); return false for null/null
    /// asymmetrically → (e).
    /// </summary>
    internal static class L540_MarkOnlyOnValueInequality
    {
        internal static IEnumerable<string> Check()
        {
            var applier = typeof(GenericApplier);
            var changed = applier.GetMethod("LeafChanged", BindingFlags.Static | BindingFlags.NonPublic |
                                                           BindingFlags.Public);
            if (changed == null)
            {
                yield return "L540 premise-changed: GenericApplier.LeafChanged did not resolve, so this " +
                             "law cannot execute the decision it exists to constrain. Re-point it before " +
                             "believing the verdict.";
                yield break;
            }

            if (GenericApplier.LeafChanged(42, 42))
                yield return "L540 equal-value-marks: two equal values reported a change. A batch that " +
                             "rewrites a leaf with the value it already holds must raise NO mark — " +
                             "marking on write is the direct cause of an unrelated peer's manufacturing " +
                             "tick resetting the local soldier model and animation.";

            if (!GenericApplier.LeafChanged(42, 43))
                yield return "L540 different-value-silent: two DIFFERENT values reported no change. " +
                             "Without this direction the law is satisfied by a predicate that always " +
                             "answers 'unchanged', which freezes every screen — and a stale screen is a " +
                             "defect in this repo, not a cosmetic issue.";

            if (GenericApplier.LeafChanged(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3 }))
                yield return "L540 reference-equality-used: two DISTINCT byte arrays with identical " +
                             "contents reported a change. The game mutates state in place, so identity " +
                             "memoization is useless here — the comparison must be by VALUE or by hash, " +
                             "never by reference.";

            if (!GenericApplier.LeafChanged(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 4 }))
                yield return "L540 different-bytes-silent: two different blobs reported no change.";
            if (!GenericApplier.LeafChanged(new byte[] { 1, 2 }, new byte[] { 1, 2, 3 }))
                yield return "L540 different-bytes-silent: blobs of different length reported no change.";

            if (GenericApplier.LeafChanged(null, null))
                yield return "L540 null-is-not-a-change: null vs null reported a change, so every " +
                             "unreadable field would mark on every batch and the scoping would buy " +
                             "nothing.";
            if (!GenericApplier.LeafChanged(null, 7))
                yield return "L540 positive-control: null vs a value reported NO change. An unreadable " +
                             "before-value must degrade to 'changed' — REACTIVITY is a hard mandate, so " +
                             "an unreadable model may cost a repaint and may never cost a stale screen.";
        }
    }
}
```

- [ ] **Step 4: Register, run RED then GREEN.** Add `Add(laws, () => L540_MarkOnlyOnValueInequality.Check());` to `tools/RailCheck/Program.cs` after the last existing registration. Bump `ExpectedLawRegistrations` by 1 (336 → 337 if this plan runs alone), `tools/law-count.txt` `files=` by 1 (276 → 277), and the three `L193` literals to match. Run:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
.\deploy.ps1 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'
dotnet run -c Debug --project tools/RailCheck
```

Paste the `digest=<hex>` from the `RAILCHECK ABORTED` line into `Program.cs:494` and the two `L193` digest literals; re-run for `RAILCHECK GREEN — laws-run=337/337 law-violations=0`. Then `pwsh -NoProfile -File tools/law-integrity.ps1`, paste its digest into `tools/law-integrity.ps1:50`, amend the comment to `L540 ADDED (a mark is raised by a value that differs) -- 336 -> 337 registrations`, re-run for `laws: 277 file(s) + 60 inline = 337` and `law-integrity: OK`.

- [ ] **Step 5: Semantic mutation kill.** Change `LeafChanged`'s byte-array branch to `return !ReferenceEquals(a, b);`. Run RailCheck, confirm:

```
L540 reference-equality-used: two DISTINCT byte arrays with identical contents reported a change. …
RAILCHECK RED — 1 executable law violation(s); --update cannot baseline them.
```

Revert, re-run, confirm `RAILCHECK GREEN — laws-run=337/337 law-violations=0`.

- [ ] **Step 6: Record and commit.** Append to the `## Law index` table in `docs/laws.md`:

```
| L540 | A MARK IS RAISED BY A VALUE THAT DIFFERS, NEVER BY "A WRITE HAPPENED": `GenericApplier.LeafChanged` is EXECUTED in both directions on boxed values and on blobs; two DISTINCT byte arrays with identical contents compare UNCHANGED (a reference compare would mark on every batch); and null-vs-value degrades to CHANGED so an unreadable field costs a repaint, never a stale screen | P11 | incident | reported: an unrelated peer's manufacturing tick repainted the soldier-edit screen, which routes to `UIModuleActorCycle.DisplaySoldier(c, resetAnimation: true)` → `CommonCharacterUtils.ResetCharacterAnimation` = `Animator.Play(0,-1,0f)` (`CommonCharacterUtils.cs:66-73`), resetting the model and restarting the animation. Many of those repaints were owed to nothing — the batch rewrote leaves with the values they already held. MUTATION KILL: byte branch → `!ReferenceEquals(a, b)` → `L540 reference-equality-used`, RED; reverted → GREEN 337/337 | premise-changed + POSITIVE CONTROL |
```

```powershell
git add src/Rail/GenericApplier.cs tools/RailCheck/L540_MarkOnlyOnValueInequality.cs tools/RailCheck/Program.cs tools/RailCheck/L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs tools/law-count.txt tools/law-integrity.ps1 docs/laws.md
git commit -m "fix(uirepaint): mark dirty only when a leaf value actually differs"
```

The commit body must state that the ~63 kindless sites and the global bool are untouched, and that `MarkOrderChange` stays unconditional.

---

## Task 3: Carry the path through (§B.1)

The rail knows the exact leaf that changed and throws it away at `touched.Add(entity)`. Downstream it degrades further: a set of entity INSTANCES → a TYPE → one global bool. This task keeps the path all the way to the mark.

**Files:**
- Modify: `src/Rail/GenericApplier.cs` — `touched` becomes `HashSet<TouchedLeaf>`; the two `UiEventMap.Fire(touched, geo)` sites at `:323` and `:508`
- Modify: `src/Rail/UiEventMap.cs:67` (`Fire`'s signature) and its 8 arms at `:75-190`
- Modify: `src/Rail/OpenUiRepaint.cs` — the new `MarkDirty(Type, GeoLevelController, string)` overload
- Test: no new law here; Task 4's `L541` is what executes the result. This task must leave RailCheck GREEN at the count Task 2 left it.

- [ ] **Step 1: Add the touched record.** In `src/Rail/GenericApplier.cs`, above `ApplyEntry` (`:1025`), add:

```csharp
        /// <summary>ONE TOUCHED LEAF, with the information the rail already had and used to discard
        /// (§B.1). Equality is by (Entity, Path) so a HashSet still collapses repeats the way the old
        /// HashSet&lt;object&gt; did — the Field is carried for diagnostics and for a future finer scope,
        /// and deliberately does NOT participate in equality: two fields of one entity on one path are one
        /// touch as far as a repaint is concerned.</summary>
        internal sealed class TouchedLeaf : IEquatable<TouchedLeaf>
        {
            internal readonly object Entity;
            internal readonly string Path;
            internal readonly string Field;

            internal TouchedLeaf(object entity, string path, string field)
            { Entity = entity; Path = path; Field = field; }

            public bool Equals(TouchedLeaf other) =>
                other != null && ReferenceEquals(Entity, other.Entity) &&
                string.Equals(Path, other.Path, StringComparison.Ordinal);

            public override bool Equals(object obj) => Equals(obj as TouchedLeaf);

            public override int GetHashCode() =>
                (Entity == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Entity)) ^
                (Path == null ? 0 : Path.GetHashCode());
        }
```

- [ ] **Step 2: Change the two collections and the mark site.** In `src/Rail/GenericApplier.cs`, change `var touched = new HashSet<object>();` at `:259` and `:426` to:

```csharp
                    var touched = new HashSet<TouchedLeaf>();
```

change the `HashSet<object> touched` parameters at `:611` (`…(GeoLevelController geo, string rootKey, byte[] blob, HashSet<object> touched)`), `:677` (`…ApplyDescendDestroy(GeoLevelController geo, string rootKey, HashSet<object> touched)`) and `:1025` (`ApplyEntry`) to `HashSet<TouchedLeaf> touched`, and change the gated add from Task 2 to carry the path:

```csharp
                if (LeafChanged(beforeValue, afterValue))
                    touched.Add(new TouchedLeaf(entity, path, field.Name));
```

At the two structural sites (`:611`, `:677`) a create/destroy has no leaf path — add with a null path, which routes to the unconditional arm exactly as a kindless mark does:

```csharp
            touched.Add(new TouchedLeaf(entity, null, null));
```

- [ ] **Step 3: Forward the path from `Fire`.** In `src/Rail/UiEventMap.cs`, change `public static void Fire(HashSet<object> touched, GeoLevelController geo)` (`:67`) to:

```csharp
        public static void Fire(HashSet<GenericApplier.TouchedLeaf> touched, GeoLevelController geo)
```

and change the `foreach (var entity in touched)` head to unwrap:

```csharp
            foreach (var leaf in touched)
            {
                var entity = leaf.Entity;
                var path = leaf.Path;
```

Then, in each of the 8 kind arms, change `OpenUiRepaint.MarkDirty(entity.GetType(), geo);` to `OpenUiRepaint.MarkDirty(entity.GetType(), geo, path);`. Do the same in the `default:` arm at `:179-189`. The `Timing`/`TimingInstanceData` no-op arm at `:107-114` stays a no-op. **Do not change any other behaviour in the arms** — the native derives (`RaiseResourcesChanged`, `ResearchSync.PresentFromMirror`, …) run exactly as they do today.

- [ ] **Step 4: Add the path-carrying mark.** In `src/Rail/OpenUiRepaint.cs`, immediately after the existing `MarkDirty(Type kind, GeoLevelController geo)` (`:746-764`), add:

```csharp
        /// <summary>
        /// THE SAME MARK, CARRYING THE EXACT RAIL PATH THAT CHANGED. The rail has always known the leaf —
        /// GenericApplier.ApplyEntry holds (kindId, path, fieldIdx, subKey, value) — and used to throw the
        /// path away one line before it was needed. This is where it stops being thrown away (§B.1).
        ///
        /// It accumulates into a SET OF TOUCHED PATHS BESIDE the global bool, and NEVER instead of it. A
        /// null path (structural create/destroy, an intent reject, a reseed) falls straight through to the
        /// unconditional arm, which is exactly what the ~63 kindless call sites already do and must keep
        /// doing (§B.4).
        ///
        /// CONSERVATIVE BY CONSTRUCTION, in the same direction as the overload above: an unknown kind, an
        /// undeclared screen, a null path or no open screen at all marks everything.
        /// </summary>
        public static void MarkDirty(Type kind, GeoLevelController geo, string path)
        {
            if (path == null) { MarkDirty(kind, geo); return; }   // no path = today's behaviour, exactly
            var screen = geo?.View?.CurrentViewState;
            if (screen != null && kind != null &&
                UiNativeRepaint.IgnoredKinds.TryGetValue(screen.GetType(), out var ignored) &&
                ignored.Contains(kind))
            {
                // Identical to the kind-only overload's skip: the refusal is never invisible, once per
                // kind per screen, and the persistent HUD still hears about it.
                if (_loggedSkips.Add(screen.GetType().Name + ":" + kind.Name))
                    MpLog.Log("[MP][uirepaint] SKIP " + kind.Name + " on " + screen.GetType().Name +
                              " — kind declared irrelevant to this screen (logged once per kind per screen)");
                MarkHudDirty();
                return;
            }
            _touchedPaths.Add(path);
            _scopedDirty = true;
            _marksSinceFlush++;
        }
```

and add the two fields beside `_dirty` (`:42`):

```csharp
        // §B.4: the global bool STAYS and serves the ~63 kindless sites and all structural create/destroy.
        // The scoped set is BESIDE it. _dirty always wins — a kindless mark repaints everything.
        private static bool _scopedDirty;
        private static readonly System.Collections.Generic.HashSet<string> _touchedPaths =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
```

and clear them in `Reset()` (`:867`) beside `_dirty = false;`:

```csharp
            _scopedDirty = false;
            _touchedPaths.Clear();
```

- [ ] **Step 5: Build and confirm nothing changed yet.** Run:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
.\deploy.ps1 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'
dotnet run -c Debug --project tools/RailCheck
pwsh -NoProfile -File tools/law-integrity.ps1
dotnet run -c Debug --project tools/RailSim -- --seed 1
```

Expect `RAILCHECK GREEN — laws-run=337/337 law-violations=0`, `laws: 277 file(s) + 60 inline = 337`, `law-integrity: OK`, `RAILSIM GREEN — scenarios=1/1 failures=0 seed=1`. Behaviour is deliberately unchanged at this point: `_scopedDirty` is set but `FlushIfDirty` does not read it yet, so nothing is skipped. That is the correct intermediate state — the plan lands the CARRIER before the DECISION so no commit can ship a screen that has stopped repainting.

If any law goes RED here it will be one that reflects over `UiEventMap.Fire`'s signature (`L38`, `L60`); re-point that law to the new signature rather than reverting the change, and say so in the commit body.

- [ ] **Step 6: Commit.**

```powershell
git add src/Rail/GenericApplier.cs src/Rail/UiEventMap.cs src/Rail/OpenUiRepaint.cs
git commit -m "refactor(rail): carry the changed leaf path through to the repaint mark"
```

The commit body must state that no repaint is skipped yet, name the two `Fire` call sites (`src/Rail/GenericApplier.cs:323`, `:508`), and confirm the global bool and the ~63 kindless sites are untouched.

---

## Task 4: The declaration table, and an undeclared surface repaints on everything (§B.3, §B.7)

**Files:**
- Create: `tools/RailCheck/L541_AnUndeclaredSurfaceRepaintsOnEverything.cs`
- Modify: `src/Rail/UiEventMap.cs:661-665` (the new `DeclaredPrefixes` beside `IgnoredKinds`)
- Modify: `src/Rail/OpenUiRepaint.cs` (`SurfaceRepaints`, and `FlushIfDirty` consulting it)
- Modify: `tools/RailCheck/Program.cs`, `L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs`, `tools/law-count.txt`, `tools/law-integrity.ps1`, `docs/laws.md`

- [ ] **Step 1: Add the declaration table.** In `src/Rail/UiEventMap.cs`, immediately after `UiNativeRepaint.IgnoredKinds` (`:661-665`), add:

```csharp
        /// <summary>
        /// WHAT EACH SURFACE READS, AS STATIC RAIL PATH PREFIXES. Keyed by the view state's NAME, not its
        /// Type — the name form is what lets a surface belonging to a mod this assembly cannot reference
        /// (TFTV's own panels) be declared at all, and it is the same form WindowOrder.MapStates already
        /// uses for the same reason (src/Rail/WindowOrder.cs:243-248).
        ///
        /// STATIC, NEVER INFERRED AT RUNTIME. Automatic read tracking only sees synchronous reads, so an
        /// inferred declaration silently misses a dependency (MobX missed-dependency class, §2.5). The
        /// declaration is written here, once, by a human who read the screen.
        ///
        /// NO DECLARATION ⇒ THE SURFACE REPAINTS ON EVERYTHING. Declaration is in the OPT-IN-TO-SCOPE
        /// direction ONLY; there is no way to declare "I read nothing". A forgotten surface therefore
        /// degrades to today's behaviour and never to stale data.
        ///
        /// PREFIX DEPTH IS THE WHOLE TUNING KNOB (Firebase, §2.5). A prefix that stops at the root
        /// repaints on everything — that is a no-op declaration, not a bug.
        ///
        /// THE ONE REAL TRAP (§B.3). The rail has TWO collection shapes and they declare differently:
        ///   • FieldClass.EntityCollection (KEYED) — each element has its own `…#&lt;stableKey&gt;` subtree
        ///     (DiffEngine.cs:1306-1311). A prefix is safe and meaningful at ANY depth, and inserting an
        ///     element renames nothing because the key comes from the element's own identity.
        ///   • FieldClass.EntityList (KEYLESS) — the WHOLE list is ONE canonical value blob AT THE FIELD
        ///     PATH (DiffEngine.cs:1229-1232, AddEntityListEntry). There are no per-element paths at all,
        ///     so a prefix DEEPER than the field name can never match and would silently subscribe to
        ///     nothing. CHECK THE FIELD'S FieldClass BEFORE WRITING A DEEP PREFIX.
        ///
        /// ORDER IS CARRIED AT THE FIELD PATH. A pure reorder of a keyed ordered collection changes no
        /// element value and no key set, so AddKeyOrder ships an explicit ORDER vector as a SubKey == ""
        /// entry ON THE FIELD (DiffEngine.cs:1304-1305, :1388-1400). A surface that renders ORDER must
        /// therefore declare the FIELD path; declaring only `…#&lt;key&gt;` prefixes will miss reorders.
        ///
        /// NO SEGMENT IS EVER AN INDEX (§B.3, Q5). DiffEngine.cs:1231 states the rule in the file — "law 2
        /// forbids element indices in the path" — and enforces it: a null key sets keyless = true (:1243)
        /// and the field is ABORTED with a loud Incident (:1299-1302), with no index fallback anywhere.
        /// L544 pins that. So prefix subscriptions are safe on every path the rail emits.
        /// </summary>
        internal static readonly System.Collections.Generic.Dictionary<string, string[]> DeclaredPrefixes =
            new System.Collections.Generic.Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                // Start with ONE surface and one that is worth it: the reported defect. The soldier-edit
                // screen paints one character; a GeoSite timer, a manufacture tick and another peer's
                // wallet are all provably nothing to do with it. Every prefix below must be justified
                // from the screen's own reads by whoever adds it — see the arms of L541.
                { "UIStateEditSoldier", new[] { "U#" } },
            };
```

Adding a second surface later is a one-row edit and needs no code. **Do not add a row you have not read the screen for**; a wrong prefix is a stale screen, and REACTIVITY is a hard mandate.

- [ ] **Step 2: Add the pure decision.** In `src/Rail/OpenUiRepaint.cs`, beside `RepaintNeeded` (`:439`), add:

```csharp
        /// <summary>
        /// DOES THIS SURFACE OWE A REPAINT FOR THESE TOUCHED PATHS? Pure, named and internal so RailCheck
        /// and RailSim execute the real decision with no live screen — the whole reason the property
        /// "a surface whose declared prefixes were untouched did NOT repaint" is testable at all.
        ///
        /// EVERY UNCERTAIN ANSWER IS "REPAINT": an unknown surface name, an undeclared surface, a null
        /// path in the set, or an empty declaration all return true. Declaration is opt-in to SCOPE, never
        /// opt-in to reactivity.
        /// </summary>
        internal static bool SurfaceRepaints(string surfaceName,
                                             System.Collections.Generic.ICollection<string> touchedPaths)
        {
            if (touchedPaths == null || touchedPaths.Count == 0) return false; // nothing changed at all
            if (surfaceName == null) return true;                              // no surface = repaint
            string[] prefixes;
            if (!UiNativeRepaint.DeclaredPrefixes.TryGetValue(surfaceName, out prefixes) ||
                prefixes == null || prefixes.Length == 0)
                return true;                                                   // UNDECLARED = repaint
            foreach (var path in touchedPaths)
            {
                if (path == null) return true;                                 // unknown path = repaint
                foreach (var prefix in prefixes)
                    if (prefix != null && path.StartsWith(prefix, StringComparison.Ordinal)) return true;
            }
            return false;
        }
```

- [ ] **Step 3: Consult it at batch end.** In `src/Rail/OpenUiRepaint.FlushIfDirty` (`:893`), change the early-out block so the scoped set is evaluated when the global bool is clear. Replace:

```csharp
            if (!_dirty)
            {
                if (_hudDirty) { _hudDirty = false; RefreshPersistentHud(); }
                return;
            }
```

with:

```csharp
            if (!_dirty && _scopedDirty)
            {
                // §B.5 — MARK DURING THE BATCH, EVALUATE ONCE AT BATCH END. Two-phase mark/evaluate is
                // what avoids a glitch mid-batch; this is the evaluate, and it runs at exactly the same
                // point the global bool is already evaluated.
                _scopedDirty = false;
                var surface = GenericApplier.GeoLevel()?.View?.CurrentViewState?.GetType().Name;
                bool owes = SurfaceRepaints(surface, _touchedPaths);
                _touchedPaths.Clear();
                // The persistent HUD spans view states and is in no declaration, so it always hears about
                // a change — the same split MarkHudDirty already makes for a declined KIND (L60).
                if (!owes) { _hudDirty = true; }
                else _dirty = true;
            }
            if (!_dirty)
            {
                if (_hudDirty) { _hudDirty = false; RefreshPersistentHud(); }
                return;
            }
```

and clear the scoped set alongside `_dirty = false;` at the flush tail (`:922`):

```csharp
            _scopedDirty = false;
            _touchedPaths.Clear();
```

Do not touch `LocalInputInFlight`, `MaxDeferFrames` or the coalescing — they are the batch-end evaluate and §B.5 keeps them.

- [ ] **Step 4: Write L541.** Create `tools/RailCheck/L541_AnUndeclaredSurfaceRepaintsOnEverything.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L541 — AN UNDECLARED SURFACE REPAINTS ON EVERYTHING. ASSERTED, NEVER ASSUMED.
    ///
    /// This is the safe-degradation property and it is the reason scoping can be added to a live game at
    /// all: declaration is opt-in to SCOPE, never opt-in to reactivity, so a surface nobody has declared
    /// keeps today's behaviour and can never go stale. A stale screen is a defect in this repo, not a
    /// cosmetic issue, and the whole of §B is only safe while this arm is green.
    ///
    /// R3 is the risk this law also covers from the other side: a declaration that stops at the root
    /// silently reverts a surface to repaint-on-everything. That is SAFE but INVISIBLE, so arm (e)
    /// executes a declared surface in both directions — a useless declaration shows up as a surface that
    /// repaints on a path it declared nothing about.
    ///
    /// ARMS, all EXECUTED against the real OpenUiRepaint.SurfaceRepaints with no live screen:
    ///   (a) undeclared-surface-scoped — a surface with no row in DeclaredPrefixes repaints.
    ///   (b) unknown-surface-scoped — a null surface name repaints.
    ///   (c) null-path-scoped — a null path inside the touched set repaints, because an unknown path
    ///       cannot be proven irrelevant.
    ///   (d) empty-declaration-scoped — a row present but empty repaints; an empty array must not read as
    ///       "reads nothing", because there is no way to declare that.
    ///   (e) declared-surface-never-scopes / declared-surface-always-repaints — the declared surface must
    ///       refuse an untouched prefix AND accept a touched one. Without both, SurfaceRepaints could be
    ///       a constant and every other arm would be vacuous.
    ///   (f) nothing-touched-repaints — an EMPTY touched set must NOT repaint, or the scoping buys
    ///       nothing at all.
    ///
    /// ROLES SEPARATED (§C.3): SurfaceRepaints is a pure function of (surface name, path set) with no
    /// peer, no session and no role — nothing role-dependent exists for one role to mask.
    ///
    /// Falsify (compile-valid src mutations, each named): make SurfaceRepaints return false for an
    /// undeclared surface → (a); drop the `path == null` guard → (c); return `prefixes.Length == 0` as
    /// false → (d); `=> true;` → (f); `=> false;` → (e).
    /// </summary>
    internal static class L541_AnUndeclaredSurfaceRepaintsOnEverything
    {
        internal static IEnumerable<string> Check()
        {
            var repaint = typeof(OpenUiRepaint);
            var decide = repaint.GetMethod("SurfaceRepaints", BindingFlags.Static | BindingFlags.NonPublic |
                                                              BindingFlags.Public);
            if (decide == null || UiNativeRepaint.DeclaredPrefixes == null)
            {
                yield return "L541 premise-changed: OpenUiRepaint.SurfaceRepaints or " +
                             "UiNativeRepaint.DeclaredPrefixes did not resolve. Re-point this law before " +
                             "believing the verdict — while it cannot see the decision, every surface in " +
                             "the game is unprotected.";
                yield break;
            }

            var anyPath = new List<string> { "S#76.SerializationData.HavenData.AssignedResearchId" };

            if (!OpenUiRepaint.SurfaceRepaints("UIStateNoSuchScreenHasEverExisted", anyPath))
                yield return "L541 undeclared-surface-scoped: a surface with no declaration refused a " +
                             "repaint. NO DECLARATION MUST MEAN REPAINT ON EVERYTHING — declaration is " +
                             "opt-in to SCOPE, never opt-in to reactivity, and a forgotten surface must " +
                             "degrade to today's behaviour, never to stale data.";

            if (!OpenUiRepaint.SurfaceRepaints(null, anyPath))
                yield return "L541 unknown-surface-scoped: a NULL surface name refused a repaint. The " +
                             "current view state is legitimately null for a single frame " +
                             "(src/Rail/WindowOrder.cs:298), and that frame must not be able to swallow a " +
                             "change.";

            if (!OpenUiRepaint.SurfaceRepaints("UIStateEditSoldier", new List<string> { null }))
                yield return "L541 null-path-scoped: a null path inside the touched set was scoped away. " +
                             "An unknown path cannot be proven irrelevant to anything.";

            // (d) an EMPTY declaration must behave like NO declaration.
            var savedEmpty = AddTemporaryRow("UIStateL541EmptyProbe", new string[0]);
            if (!OpenUiRepaint.SurfaceRepaints("UIStateL541EmptyProbe", anyPath))
                yield return "L541 empty-declaration-scoped: an EMPTY prefix array read as 'this surface " +
                             "reads nothing'. There is no way to declare that, by design — an empty row " +
                             "is a row somebody has not finished writing.";
            RemoveTemporaryRow("UIStateL541EmptyProbe", savedEmpty);

            // (e) BOTH DIRECTIONS on a real declared surface.
            var saved = AddTemporaryRow("UIStateL541Probe", new[] { "U#" });
            if (OpenUiRepaint.SurfaceRepaints("UIStateL541Probe",
                    new List<string> { "S#76.SerializationData.HavenData.AssignedResearchId" }))
                yield return "L541 declared-surface-never-scopes: a surface declaring only 'U#' repainted " +
                             "for a path under 'S#'. This is the whole point of the work — an unrelated " +
                             "peer's site tick must stop repainting a screen that provably cannot show it.";
            if (!OpenUiRepaint.SurfaceRepaints("UIStateL541Probe",
                    new List<string> { "U#4.SerializationData.Progression" }))
                yield return "L541 declared-surface-always-repaints: a surface declaring 'U#' refused a " +
                             "repaint for a path under 'U#'. Without this direction SurfaceRepaints could " +
                             "simply return false and every arm above would be vacuous — and every " +
                             "declared screen would go stale.";
            RemoveTemporaryRow("UIStateL541Probe", saved);

            // (f) POSITIVE CONTROL, the other way: nothing touched, nothing repainted.
            if (OpenUiRepaint.SurfaceRepaints("UIStateEditSoldier", new List<string>()))
                yield return "L541 positive-control: an EMPTY touched set still demanded a repaint, so the " +
                             "scoping buys nothing and every arm above is measuring a function that " +
                             "always says yes.";
        }

        /// <summary>Insert a probe row so the declared-surface arms do not depend on which real screens
        /// happen to be declared today. Returns the previous value so it can be put back exactly.</summary>
        private static string[] AddTemporaryRow(string name, string[] prefixes)
        {
            string[] previous;
            UiNativeRepaint.DeclaredPrefixes.TryGetValue(name, out previous);
            UiNativeRepaint.DeclaredPrefixes[name] = prefixes;
            return previous;
        }

        private static void RemoveTemporaryRow(string name, string[] previous)
        {
            if (previous == null) UiNativeRepaint.DeclaredPrefixes.Remove(name);
            else UiNativeRepaint.DeclaredPrefixes[name] = previous;
        }
    }
}
```

- [ ] **Step 5: Register, run, kill the mutation.** Add `Add(laws, () => L541_AnUndeclaredSurfaceRepaintsOnEverything.Check());`, bump `ExpectedLawRegistrations` 337 → 338, `tools/law-count.txt` to `files=278`, the three `L193` literals to `(338, 338,` / `(338, 337,` / `(338, 338, "wrong")`, and refresh both digests exactly as in Task 2 Step 4. Run:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
.\deploy.ps1 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'
dotnet run -c Debug --project tools/RailCheck
pwsh -NoProfile -File tools/law-integrity.ps1
```

Expect `RAILCHECK GREEN — laws-run=338/338 law-violations=0`, `laws: 278 file(s) + 60 inline = 338`, `law-integrity: OK`. Mutation: change `SurfaceRepaints`'s undeclared branch from `return true;` to `return false;`. Run RailCheck, confirm:

```
L541 undeclared-surface-scoped: a surface with no declaration refused a repaint. NO DECLARATION MUST MEAN REPAINT ON EVERYTHING …
RAILCHECK RED — 1 executable law violation(s); --update cannot baseline them.
```

Revert, re-run, confirm GREEN 338/338.

- [ ] **Step 6: Record and commit.**

```
| L541 | AN UNDECLARED SURFACE REPAINTS ON EVERYTHING, ASSERTED NEVER ASSUMED: `OpenUiRepaint.SurfaceRepaints` is EXECUTED for an undeclared surface, a null surface name, a null path and an EMPTY prefix array (all repaint), for a declared surface in BOTH directions (refuses an untouched prefix, accepts a touched one), and for an empty touched set (does not repaint) | P11 | principle | §B.3 — declaration is opt-in to SCOPE, never opt-in to reactivity, so a forgotten surface degrades to today's behaviour and never to stale data; a stale screen is a defect in this repo, not a cosmetic issue. R3: a declaration that stops at the root is safe but INVISIBLE, so both directions are executed. MUTATION KILL: undeclared branch `return true;` → `return false;` → `L541 undeclared-surface-scoped`, RED; reverted → GREEN 338/338 | premise-changed + POSITIVE CONTROL |
```

```powershell
git add src/Rail/UiEventMap.cs src/Rail/OpenUiRepaint.cs tools/RailCheck/L541_AnUndeclaredSurfaceRepaintsOnEverything.cs tools/RailCheck/Program.cs tools/RailCheck/L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs tools/law-count.txt tools/law-integrity.ps1 docs/laws.md
git commit -m "feat(uirepaint): scope a repaint to the surface's declared path prefixes"
```

The commit body must name the repaint seam (`src/Rail/OpenUiRepaint.cs:893` `FlushIfDirty`) and state that the persistent HUD still refreshes when a screen declines, so nothing that spans view states can go stale.

---

## Task 5: The harness property no law can express — §C.1.3

"A surface whose declared prefixes were untouched did NOT repaint" is the acceptance criterion for the whole of section B, and it is the reason the harness exists.

**Files:**
- Modify: `tools/RailSim/Scenarios.cs`

- [ ] **Step 1: Add the scenario.** In `tools/RailSim/Scenarios.cs` add `using Multiplayer.Network.Sync;` and to `All()`:

```csharp
            yield return Pair("an-untouched-surface-does-not-repaint", AnUntouchedSurfaceDoesNotRepaint);
```

then:

```csharp
        /// <summary>§C.1 property 3 — the property no existing law can express, and the acceptance
        /// criterion for the whole of section B. An OBSERVABLE HISTORY: a run of rail batches produces a
        /// list of repaint decisions per surface, and the surface whose declared prefixes were never
        /// touched must appear with ZERO repaints while the surface that was touched appears with some.
        ///
        /// The reported defect in one line: an unrelated peer's MANUFACTURING tick repainted the
        /// soldier-edit screen, and that screen's repaint routes to a DESTRUCTIVE native refresh which
        /// resets the soldier model and restarts its animation.</summary>
        private static IEnumerable<string> AnUntouchedSurfaceDoesNotRepaint(int seed)
        {
            const string editSoldier = "UIStateEditSoldier";
            const string probeOther = "UIStateRailSimProbeOther";

            string[] savedEdit, savedOther;
            UiNativeRepaint.DeclaredPrefixes.TryGetValue(editSoldier, out savedEdit);
            UiNativeRepaint.DeclaredPrefixes.TryGetValue(probeOther, out savedOther);
            UiNativeRepaint.DeclaredPrefixes[editSoldier] = new[] { "U#" };
            UiNativeRepaint.DeclaredPrefixes[probeOther] = new[] { "S#" };

            // A run of batches, in the order and shape a real session produces them: a manufacturing tick
            // and a site tick, neither of which is a soldier, plus one genuine soldier progression change.
            var batches = new[]
            {
                new[] { "S#76.SerializationData.HavenData.AssignedResearchId" },
                new[] { "S#76.SerializationData.HavenData.Population" },
                new[] { "S#12.SerializationData.Manufacture.Current" },
                new[] { "U#4.SerializationData.Progression.Experience" },
                new[] { "S#12.SerializationData.Manufacture.Current" },
            };

            int editRepaints = 0, otherRepaints = 0;
            foreach (var batch in batches)
            {
                if (OpenUiRepaint.SurfaceRepaints(editSoldier, batch)) editRepaints++;
                if (OpenUiRepaint.SurfaceRepaints(probeOther, batch)) otherRepaints++;
            }

            if (savedEdit == null) UiNativeRepaint.DeclaredPrefixes.Remove(editSoldier);
            else UiNativeRepaint.DeclaredPrefixes[editSoldier] = savedEdit;
            if (savedOther == null) UiNativeRepaint.DeclaredPrefixes.Remove(probeOther);
            else UiNativeRepaint.DeclaredPrefixes[probeOther] = savedOther;

            if (editRepaints != 1)
                yield return "an-untouched-surface-does-not-repaint: the soldier-edit surface repainted " +
                             editRepaints + " times across 5 batches, of which exactly ONE touched a 'U#' " +
                             "path. Four of those batches were another peer's site and manufacturing " +
                             "ticks, and repainting that screen for them is what resets the soldier model " +
                             "and restarts its animation.";

            if (otherRepaints != 4)
                yield return "an-untouched-surface-does-not-repaint: the 'S#'-declaring surface repainted " +
                             otherRepaints + " times, not 4. Scoping must not have become a blanket " +
                             "refusal — a surface whose declared prefixes WERE touched must repaint every " +
                             "time, and a stale screen is a defect, not a cosmetic issue.";
        }
```

- [ ] **Step 2: Run it and prove it bites.** Run:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
dotnet run -c Debug --project tools/RailSim -- --seed 1
```

Expect `RAILSIM GREEN — scenarios=2/2 failures=0 seed=1`. Then temporarily change `OpenUiRepaint.SurfaceRepaints`'s final `return false;` to `return true;` — the pre-scoping behaviour — rebuild and re-run; expect:

```
  an-untouched-surface-does-not-repaint: the soldier-edit surface repainted 5 times across 5 batches, of which exactly ONE touched a 'U#' path. …
RAILSIM RED — scenarios=2 failures=1 seed=1
```

That RED is the whole defect, reproduced with no game running. Revert and confirm GREEN.

- [ ] **Step 3: Commit.**

```powershell
git add tools/RailSim/Scenarios.cs
git commit -m "test(railsim): assert an untouched surface does not repaint"
```

---

## Task 6: The kindless sites still repaint everything (§B.4)

**Files:**
- Create: `tools/RailCheck/L542_TheKindlessSitesStillRepaintEverything.cs`
- Modify: `tools/RailCheck/Program.cs`, `L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs`, `tools/law-count.txt`, `tools/law-integrity.ps1`, `docs/laws.md`
- Read only: `tools/RailCheck/L38_*.cs` (decide whether it can be extended; if not, this law supersedes its blind spot)

- [ ] **Step 1: Read L38 and decide.** Open `tools/RailCheck/L38_*.cs`. Its "no arm of `Fire` reaches parameterless `MarkDirty()`" arm covers only `UiEventMap.Fire`; the ~63 kindless call sites are outside its reach, and it is green with ONE row in `IgnoredKinds` and green for over-repaint of every undeclared screen. **Decide, and write the decision into the commit body:** either extend L38's scan to every method in the assembly, or leave L38 exactly as it is and let L542 cover the kindless sites. **Do not weaken L38 in either case.** The simpler option — leave it alone, add L542 — is preferred (ponytail: the smallest correct diff), because widening L38's scan changes what an existing green law means.

- [ ] **Step 2: Write L542.** Create `tools/RailCheck/L542_TheKindlessSitesStillRepaintEverything.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L542 — THE KINDLESS MARK SITES STILL REPAINT EVERYTHING.
    ///
    /// ~63 call sites across 11 syncer files call OpenUiRepaint.MarkDirty() with NO kind and NO path —
    /// AssignSync 9, PersonnelSync 15, MissionSync 12, VehicleSync 6, GenericApplier 8 structural,
    /// IntentRail 3, DeployPrep 2, DeploymentWindow 2, EquipSync 2, TradeSync 1. Intent rejects,
    /// structural create/destroy, reseeds. They carry no path BY NATURE — a create has no leaf that
    /// changed — so they MUST keep an unconditional repaint-everything arm and must not be "improved"
    /// into the scoped path (§B.4).
    ///
    /// L38's own scan covers only UiEventMap.Fire, so these sites are outside its reach by construction;
    /// this law is what closes that. It does NOT weaken or replace L38 — L38 still owns the claim that no
    /// arm of Fire falls back to the parameterless mark.
    ///
    /// ARMS:
    ///   (a) kindless-mark-gone — the parameterless MarkDirty() overload still exists and is still public.
    ///   (b) kindless-sites-vanished — a substantial number of methods across the mod assembly still call
    ///       it. The count is asserted as a FLOOR, not an exact number: a site legitimately disappearing
    ///       with the code that raised it must not turn this red, but the whole family disappearing must.
    ///   (c) kindless-mark-is-scoped — the parameterless overload's IL does NOT reach SurfaceRepaints or
    ///       touch the path set. It sets the global bool and nothing else; anything more would make an
    ///       intent reject skippable, and an intent reject is exactly the moment a screen must repaint.
    ///   (d) global-bool-gone — POSITIVE CONTROL: the field the kindless arm sets still exists. §B.4 says
    ///       the global bool STAYS; a law that only checks callers would pass against an assembly where
    ///       the bool had been deleted and the mark had become a no-op.
    ///
    /// ROLES SEPARATED (§C.3): all four arms are statements about the shipped assembly, identical from
    /// either role.
    ///
    /// Falsify (compile-valid src mutations, each named): route MarkDirty() through the scoped path →
    /// (c); delete _dirty and set only _scopedDirty → (d); convert most kindless call sites to
    /// MarkDirty(type, geo, path) → (b).
    /// </summary>
    internal static class L542_TheKindlessSitesStillRepaintEverything
    {
        /// <summary>The floor. 63 sites exist today across 11 files; a third of them could legitimately go
        /// with the features that raise them before this number is wrong.</summary>
        private const int MinimumKindlessCallSites = 40;

        internal static IEnumerable<string> Check()
        {
            var repaint = typeof(OpenUiRepaint);
            var kindless = repaint.GetMethod("MarkDirty", BindingFlags.Static | BindingFlags.Public |
                                                          BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (kindless == null)
            {
                yield return "L542 kindless-mark-gone: OpenUiRepaint.MarkDirty() (no arguments) does not " +
                             "exist. ~63 sites carry no path BY NATURE — a structural create has no leaf " +
                             "that changed, an intent reject has no leaf at all — and they must keep an " +
                             "unconditional repaint-everything arm (§B.4).";
                yield break;
            }

            var asm = typeof(OpenUiRepaint).Assembly;
            var callers = asm.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Instance |
                                              BindingFlags.Public | BindingFlags.NonPublic |
                                              BindingFlags.DeclaredOnly).Cast<MethodBase>())
                .Where(m => m.DeclaringType != repaint && Il.References(m, kindless))
                .Select(m => m.DeclaringType.Name + "." + m.Name)
                .Distinct().ToList();

            if (callers.Count < MinimumKindlessCallSites)
                yield return "L542 kindless-sites-vanished: only " + callers.Count + " method(s) still " +
                             "call the kindless MarkDirty(), below the floor of " +
                             MinimumKindlessCallSites + ". The kindless sites were converted to the " +
                             "scoped path, which §B.4 forbids: they have no path to declare, so a scoped " +
                             "mark from one of them would be a mark nothing matches — i.e. a silent " +
                             "refusal to repaint after an intent reject or a structural destroy.";

            var scoped = repaint.GetMethod("SurfaceRepaints", BindingFlags.Static | BindingFlags.NonPublic |
                                                              BindingFlags.Public);
            if (scoped != null && Il.References(kindless, scoped))
                yield return "L542 kindless-mark-is-scoped: the parameterless MarkDirty() reaches " +
                             "SurfaceRepaints. It must set the global bool and nothing else — a kindless " +
                             "mark is the one that cannot be proven irrelevant to anything.";

            var dirty = repaint.GetField("_dirty", BindingFlags.Static | BindingFlags.NonPublic);
            if (dirty == null || dirty.FieldType != typeof(bool))
                yield return "L542 positive-control: OpenUiRepaint._dirty (bool) is gone. §B.4 says the " +
                             "global bool STAYS; without it the kindless arm this law counts callers of " +
                             "would be a no-op, and every arm above would pass while nothing repainted.";
        }
    }
}
```

This law uses the `Il` helper. If `tools/RailCheck/Il.cs` does not exist (the companion plan creates it in its Task 2), create it now with exactly this content:

```csharp
using System;
using System.Reflection;

namespace RailCheck
{
    /// <summary>The IL questions the reactivity and window laws ask, in one place so several laws do not
    /// each carry a copy. Same cross-assembly token resolve L492/L516 use: a callee in UnityEngine or
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

`Il.cs` has no `L` prefix, so `tools/law-integrity.ps1` does not count it as a law file — `files=` is unaffected by it.

- [ ] **Step 3: Register, run, kill the mutation, commit.** Add `Add(laws, () => L542_TheKindlessSitesStillRepaintEverything.Check());`, bump `ExpectedLawRegistrations` 338 → 339, `tools/law-count.txt` to `files=279`, the `L193` literals to `(339, 339,` / `(339, 338,` / `(339, 339, "wrong")`, refresh both digests. Run:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
.\deploy.ps1 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'
dotnet run -c Debug --project tools/RailCheck
pwsh -NoProfile -File tools/law-integrity.ps1
dotnet run -c Debug --project tools/RailSim -- --seed 1
```

Expect `RAILCHECK GREEN — laws-run=339/339 law-violations=0`, `laws: 279 file(s) + 60 inline = 339`, `law-integrity: OK`, `RAILSIM GREEN — scenarios=2/2 failures=0 seed=1`. Mutation: change `OpenUiRepaint.MarkDirty()` to

```csharp
        public static void MarkDirty()
        {
            if (SurfaceRepaints(null, _touchedPaths)) _dirty = true;
            _marksSinceFlush++;
        }
```

Run RailCheck, confirm `L542 kindless-mark-is-scoped: the parameterless MarkDirty() reaches SurfaceRepaints.` and RED; revert; confirm GREEN 339/339. Record:

```
| L542 | THE KINDLESS MARK SITES STILL REPAINT EVERYTHING: the parameterless `OpenUiRepaint.MarkDirty()` exists, at least 40 methods across the assembly still call it, its IL does NOT reach `SurfaceRepaints`, and `_dirty` still exists (positive control — without the bool the arm this law counts callers of is a no-op) | P11 | principle | §B.4 — ~63 kindless sites across 11 syncer files (AssignSync 9, PersonnelSync 15, MissionSync 12, VehicleSync 6, GenericApplier 8 structural, IntentRail 3, DeployPrep 2, DeploymentWindow 2, EquipSync 2, TradeSync 1) carry no path BY NATURE: a structural create has no leaf that changed and an intent reject has no leaf at all. `L38`'s scan covers only `UiEventMap.Fire`, so these are outside its reach by construction. MUTATION KILL: routing `MarkDirty()` through `SurfaceRepaints` → `L542 kindless-mark-is-scoped`, RED; reverted → GREEN 339/339 | POSITIVE CONTROL |
```

```powershell
git add tools/RailCheck/L542_TheKindlessSitesStillRepaintEverything.cs tools/RailCheck/Il.cs tools/RailCheck/Program.cs tools/RailCheck/L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs tools/law-count.txt tools/law-integrity.ps1 docs/laws.md
git commit -m "test(railcheck): assert the kindless mark sites still repaint everything"
```

Drop `tools/RailCheck/Il.cs` from the `git add` if it was already committed by the companion plan.

---

## Task 7: Delete the hand-rolled signatures; fix `RefreshInfoBar`'s ordering (§B.8)

The five signature builders are read-sets written backwards — exactly the v1 per-widget sync that was abandoned. `RepaintNeeded(strip, key)` is KEPT as the fallback primitive; what dies is the hand-rolled key each strip computes for it. Leaving two competing read-set mechanisms is the two-ordering-systems mistake repeated in the UI layer.

**Files:**
- Create: `tools/RailCheck/L543_NoHandRolledSignatureSurvives.cs`
- Modify: `src/Rail/OpenUiRepaint.cs` — delete `RosterSignature` (`:233`), `CrewSignature` (`:324`), `CrewSlotKey` (`:350`), `AgendaSignature` (`:465`), `InfoBarKey` (`:566`); add `ScopeKey`, `BumpScopeGenerations`, `InfoBarNeedsRefresh`; fix `RefreshInfoBar` (`:511`)
- Modify: `src/Rail/UiEventMap.cs` (`DeclaredPrefixes` gains the four HUD module rows)
- Modify: `tools/RailCheck/Program.cs`, `L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs`, `tools/law-count.txt`, `tools/law-integrity.ps1`, `docs/laws.md`

- [ ] **Step 1: Add the scope generation counter.** In `src/Rail/OpenUiRepaint.cs`, beside `RepaintNeeded` (`:439`), add:

```csharp
        /// <summary>
        /// THE REPLACEMENT FOR THE HAND-ROLLED SIGNATURES (§B.8). A persistent-HUD strip used to compute
        /// its own read-set as a string — AgendaSignature, InfoBarKey, CrewSignature, RosterSignature,
        /// CrewSlotKey — which is a read-set written BACKWARDS, i.e. the v1 per-widget sync this project
        /// abandoned, and which InfoBarKey itself admitted was incomplete by adding a 1-second
        /// Time.realtimeSinceStartup floor.
        ///
        /// The strip now asks the SAME question the screens ask: did anything I DECLARED change? Its key
        /// is a generation number that moves exactly when one of its declared prefixes was touched, so
        /// RepaintNeeded's memory semantics — and therefore L492's flicker fix and L516's
        /// visibility-before-memory ordering — are preserved unchanged.
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<string, int> _scopeGeneration =
            new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>The strip's current key: stable while nothing it declared moved, different the moment
        /// something did. Pure with respect to its own memory — it reads, never writes.</summary>
        internal static string ScopeKey(string declaredName)
        {
            int generation;
            return declaredName + "#" +
                   (_scopeGeneration.TryGetValue(declaredName, out generation) ? generation : 0);
        }

        /// <summary>Advance the generation of every declared name whose prefixes this batch touched. Runs
        /// ONCE per flush, at batch end (§B.5), before any module is refreshed — two-phase mark/evaluate,
        /// so no strip sees a half-advanced world.</summary>
        internal static void BumpScopeGenerations(
            System.Collections.Generic.ICollection<string> touchedPaths)
        {
            if (touchedPaths == null || touchedPaths.Count == 0) return;
            foreach (var name in UiNativeRepaint.DeclaredPrefixes.Keys.ToArray())
            {
                if (!SurfaceRepaints(name, touchedPaths)) continue;
                int generation;
                _scopeGeneration[name] = (_scopeGeneration.TryGetValue(name, out generation) ? generation : 0) + 1;
            }
        }
```

Add `using System.Linq;` if the file does not already have it, and clear `_scopeGeneration` in `Reset()` (`:867`) beside `_repaintKeys.Clear();`:

```csharp
            _scopeGeneration.Clear();
```

- [ ] **Step 2: Declare the four HUD modules.** In `src/Rail/UiEventMap.cs`'s `DeclaredPrefixes`, add four rows beside the `UIStateEditSoldier` row. Read each module's own rebuild source before writing its prefixes — the existing signature builders' doc comments name the exact reads (`AgendaSignature`'s comment at `src/Rail/OpenUiRepaint.cs:449-463` enumerates them row source by row source) and those are the facts to convert:

```csharp
                // The persistent-HUD strips. They belong to no view state (that is the premise of
                // RefreshPersistentHud), so they are declared by MODULE name. Each prefix set is the
                // strip's OWN reads, converted from the signature builder that is being deleted — see
                // the doc comment that used to sit on AgendaSignature for the row-by-row source.
                { "UIModuleFactionAgendaTracker", new[] { "S#", "V#" } },
                { "UIModuleInfoBar",              new[] { "S#" } },
                { "UIModuleVehicleSelection",     new[] { "V#", "U#" } },
                { "UIModuleGeoRoster",            new[] { "U#" } },
```

- [ ] **Step 3: Delete the five builders and re-point their callers.** In `src/Rail/OpenUiRepaint.cs`:
  - delete `RosterSignature` (`:233`), `CrewSignature` (`:324`), `CrewSlotKey` (`:350`), `AgendaSignature` (`:465`), `InfoBarKey` (`:566`) together with their doc comments;
  - in `RefreshAgendaTracker` (`:361`) replace the `AgendaSignature(faction)` argument with `ScopeKey("UIModuleFactionAgendaTracker")`, leaving the call shape `AgendaNeedsRebuild(tracker.gameObject.activeInHierarchy, …)` **exactly as it is** — L516 arm (f) asserts that `RefreshAgendaTracker` reads `GameObject.activeInHierarchy`, and L516/L492 both execute `AgendaNeedsRebuild(bool, string)` with literal keys, so neither the method nor its signature may change;
  - in `RefreshRosterSlots` (`:196`) replace `RepaintNeeded("roster", RosterSignature(slots))` with `RepaintNeeded("roster", ScopeKey("UIModuleGeoRoster"))`;
  - in `RefreshVehicleCrew` (`:284`) replace `RepaintNeeded("crew", CrewSignature(vehicle))` with `RepaintNeeded("crew", ScopeKey("UIModuleVehicleSelection"))`;
  - in `RefreshPersistentHud` (`:158`) call `BumpScopeGenerations(_touchedPaths)` as its FIRST statement, before any module refresh, so all four strips see one consistent generation for the batch.

Then in `FlushIfDirty`, move the `_touchedPaths.Clear()` you added in Task 4 Step 3 to AFTER `RepaintOpenGeoscapeScreen()` / after `RefreshPersistentHud()` — the strips read the set, so it must not be cleared before they run. Verify by reading the method top to bottom after the edit.

- [ ] **Step 4: Fix `RefreshInfoBar`'s ordering.** `RefreshInfoBar` consults `RepaintNeeded` (`:514`) BEFORE the `bar == null` / `_context == null` bails (`:~524`), so a key change is CONSUMED AND LOST while the module is not yet Init'd — the same class as `efc4782` / L516. Extract the decision so a law can execute it, exactly as `AgendaNeedsRebuild` does:

```csharp
        /// <summary>THE INFO BAR'S GATE, in L516's shape and for L516's reason: the module's LIVENESS is
        /// asked BEFORE RepaintNeeded's memory is touched, never after. A key recorded against a bar that
        /// was not yet Init'd is a key BURNED — the first refresh after the bar comes up compares EQUAL
        /// and skips the repaint it owed. `&amp;&amp;` and not `&amp;`: short-circuit is what keeps
        /// RepaintNeeded from being evaluated at all for a dead bar.</summary>
        internal static bool InfoBarNeedsRefresh(bool barLive, string key) =>
            barLive && RepaintNeeded("infobar", key);
```

and rewrite the head of `RefreshInfoBar` (`:511`) so the bails come first and the gate is asked once:

```csharp
        private static void RefreshInfoBar(GeoLevelController geo, GeoscapeModulesData mods)
        {
            var bar = mods == null ? null : mods.InfoBar;
            var context = bar == null ? null : InfoBarContext.GetValue(bar);
            if (!InfoBarNeedsRefresh(bar != null && context != null,
                                     ScopeKey("UIModuleInfoBar"))) return;
            // …the existing body from the point after the old bails, unchanged…
        }
```

Read the real body first and keep every statement after the bails byte-for-byte; only the order of the two questions changes. If `InfoBarContext` is a `FieldInfo` (it is, `:88`), keep using it exactly as the current code does.

- [ ] **Step 5: Write L543 (signature arms only).** Create `tools/RailCheck/L543_NoHandRolledSignatureSurvives.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L543 — NO HAND-ROLLED READ-SET SURVIVES BESIDE A DECLARED PREFIX SET.
    ///
    /// The five signature builders — AgendaSignature, InfoBarKey, CrewSignature, RosterSignature,
    /// CrewSlotKey — are read-sets written BACKWARDS: a human enumerating, in a string, everything a strip
    /// draws. That is the v1 per-widget sync this project abandoned, and InfoBarKey admitted its own
    /// incompleteness by adding a 1-second Time.realtimeSinceStartup floor. Leaving them beside a declared
    /// prefix set is the two-ordering-systems mistake repeated in the UI layer (§B.8).
    ///
    /// RepaintNeeded ITSELF IS KEPT — it is the fallback primitive and L492/L516 both execute it. This law
    /// forbids the KEYS, not the memory.
    ///
    /// ARMS:
    ///   (a) signature-builder-survives — none of the five methods is declared on OpenUiRepaint.
    ///   (b) repaint-needed-gone — POSITIVE CONTROL: RepaintNeeded and AgendaNeedsRebuild MUST still
    ///       exist. Without this the law is satisfied by deleting the whole gate, which would restore the
    ///       L492 flicker (a full row teardown ~10 times a second) and re-break L516.
    ///   (c) infobar-burns-the-key — EXECUTED, the reported ordering bug: ask the gate about key X while
    ///       the bar is NOT live, then ask about the SAME X while it IS live. The second call MUST
    ///       refresh. This fails the moment the liveness test moves after RepaintNeeded instead of before.
    ///   (d) infobar-live-changed-skipped / infobar-live-unchanged-refreshes — both of L492's directions
    ///       on the live bar, so the gate cannot be a constant.
    ///
    /// ROLES SEPARATED (§C.3): every arm is a pure execution or an assembly-shape statement, identical
    /// from either role.
    ///
    /// Falsify (compile-valid src mutations, each named): re-add `private static string InfoBarKey(...)`
    /// → (a); change InfoBarNeedsRefresh's `&amp;&amp;` to `&amp;` → (c); `=> barLive;` → (d).
    /// </summary>
    internal static class L543_NoHandRolledSignatureSurvives
    {
        private static readonly string[] Builders =
            { "AgendaSignature", "InfoBarKey", "CrewSignature", "RosterSignature", "CrewSlotKey" };

        internal static IEnumerable<string> Check()
        {
            var repaint = typeof(OpenUiRepaint);
            const BindingFlags Any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.DeclaredOnly;

            var gate = repaint.GetMethod("InfoBarNeedsRefresh", Any);
            if (gate == null)
            {
                yield return "L543 premise-changed: OpenUiRepaint.InfoBarNeedsRefresh did not resolve, so " +
                             "arms (c) and (d) cannot execute the ordering they exist to pin.";
                yield break;
            }

            var surviving = Builders.Where(b => repaint.GetMethod(b, Any) != null)
                                    .OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (surviving.Count > 0)
                yield return "L543 signature-builder-survives: " + string.Join(", ", surviving) + " still " +
                             "exist(s) on OpenUiRepaint. A hand-rolled signature is a read-set written " +
                             "backwards — the v1 per-widget sync this project abandoned — and beside a " +
                             "declared prefix set it is two competing read-set mechanisms, which is the " +
                             "two-ordering-systems mistake repeated in the UI layer.";

            // (b) POSITIVE CONTROL: the primitive and L492/L516's gate must SURVIVE.
            if (repaint.GetMethod("RepaintNeeded", Any) == null ||
                repaint.GetMethod("AgendaNeedsRebuild", Any) == null)
                yield return "L543 repaint-needed-gone: RepaintNeeded and/or AgendaNeedsRebuild were " +
                             "deleted. §B.8 KEEPS RepaintNeeded as the fallback primitive; deleting the " +
                             "gate satisfies arm (a) while restoring the L492 flicker — a full agenda row " +
                             "teardown ~10 times a second — and re-breaking L516's ordering.";

            // (c) THE REPORTED ORDERING BUG, reproduced end to end.
            OpenUiRepaint.Reset();
            if (OpenUiRepaint.InfoBarNeedsRefresh(false, "UIModuleInfoBar#7"))
                yield return "L543 infobar-refreshes-while-dead: a bar that is not yet Init'd claimed a " +
                             "refresh. There is nothing on screen to refresh, and the body would " +
                             "dereference a null module.";
            if (!OpenUiRepaint.InfoBarNeedsRefresh(true, "UIModuleInfoBar#7"))
                yield return "L543 infobar-burns-the-key: the gate was asked about a key while the bar was " +
                             "NOT live, and that question was REMEMBERED. The first refresh after the bar " +
                             "came up compared EQUAL and skipped the repaint it owed — the same class as " +
                             "efc4782 / L516. The liveness test must be asked BEFORE RepaintNeeded, never " +
                             "after it.";

            // (d) BOTH DIRECTIONS on the live bar.
            if (!OpenUiRepaint.InfoBarNeedsRefresh(true, "UIModuleInfoBar#8"))
                yield return "L543 infobar-live-changed-skipped: a LIVE bar did not refresh on a CHANGED " +
                             "key. A stale strip is a defect in this repo, not a cosmetic issue.";
            if (OpenUiRepaint.InfoBarNeedsRefresh(true, "UIModuleInfoBar#8"))
                yield return "L543 infobar-live-unchanged-refreshes: a LIVE bar refreshed on an UNCHANGED " +
                             "key. The gate must ADD a condition, never remove one — every postfix another " +
                             "mod has hung on the info bar is paid on each refresh (TFTV's TopInforBar:127 " +
                             "does string Transform.Find lookups, a LINQ walk of the alien bases and a " +
                             "sprite load).";
            OpenUiRepaint.Reset();
        }
    }
}
```

- [ ] **Step 6: Register, run, kill the mutation, commit.** Add `Add(laws, () => L543_NoHandRolledSignatureSurvives.Check());`, bump `ExpectedLawRegistrations` 339 → 340, `tools/law-count.txt` to `files=280`, the `L193` literals to `(340, 340,` / `(340, 339,` / `(340, 340, "wrong")`, refresh both digests. Run:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
.\deploy.ps1 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'
dotnet run -c Debug --project tools/RailCheck
pwsh -NoProfile -File tools/law-integrity.ps1
dotnet run -c Debug --project tools/RailSim -- --seed 1
```

Expect `RAILCHECK GREEN — laws-run=340/340 law-violations=0` — **L492 and L516 must still be green**; if either goes RED, the fix is to restore whatever their arms execute, never to change those laws. Expect `laws: 280 file(s) + 60 inline = 340`, `law-integrity: OK`, `RAILSIM GREEN — scenarios=2/2 failures=0 seed=1`. Mutation: change `InfoBarNeedsRefresh`'s `&&` to `&`. Run RailCheck, confirm `L543 infobar-burns-the-key: …` and RED; revert; confirm GREEN 340/340. Record:

```
| L543 | NO HAND-ROLLED READ-SET SURVIVES BESIDE A DECLARED PREFIX SET: `AgendaSignature`, `InfoBarKey`, `CrewSignature`, `RosterSignature` and `CrewSlotKey` are gone; `RepaintNeeded` and `AgendaNeedsRebuild` still exist (positive control — deleting the gate would restore the L492 flicker and re-break L516); and `InfoBarNeedsRefresh` asks the bar's LIVENESS before touching `RepaintNeeded`'s memory, in both of L492's directions | P11 | incident | §B.8 — a hand-rolled signature is a read-set written backwards, i.e. the v1 per-widget sync this project abandoned, and `InfoBarKey` admitted its own incompleteness with a 1-second `Time.realtimeSinceStartup` floor. `RefreshInfoBar` consulted `RepaintNeeded` BEFORE its `bar == null` / `_context == null` bails, consuming and losing a key change while the module was not yet Init'd — the same class as `efc4782` / L516. MUTATION KILL: `InfoBarNeedsRefresh`'s `&&` → `&` → `L543 infobar-burns-the-key`, RED; reverted → GREEN 340/340 | premise-changed + POSITIVE CONTROL |
```

```powershell
git add src/Rail/OpenUiRepaint.cs src/Rail/UiEventMap.cs tools/RailCheck/L543_NoHandRolledSignatureSurvives.cs tools/RailCheck/Program.cs tools/RailCheck/L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs tools/law-count.txt tools/law-integrity.ps1 docs/laws.md
git commit -m "refactor(uirepaint): replace the hand-rolled strip signatures with declared prefixes"
```

Field verification: with a peer manufacturing, confirm the top-right activity strip still follows research AND manufacturing on every peer — that is L516's reported defect and it must not come back.

---

## Task 8: Collapse the duplicate `MapStates` to the NAME form (§B.7)

Two spellings of one set exist: `DurableWindowRegistry.MapStates` (`HashSet<Type>`, compared by Type in `MayPresent`) and `WindowOrder.MapStates` (`HashSet<string>`, compared by NAME in `HoldsForOpenScreen`). Two spellings of one set is the two-ordering-systems mistake in miniature. **The NAME form survives.**

**Files:**
- Modify: `src/Rail/DurableWindowRegistry.cs:334-335`, `:368-369` — **NO-OP if the companion window-journal plan already deleted this file**
- Modify: `tools/RailCheck/L543_NoHandRolledSignatureSurvives.cs` (one added arm — no new registration, so no digest change)
- Modify: `docs/laws.md`

- [ ] **Step 1: Add the arm and see it RED.** Append to `L543_NoHandRolledSignatureSurvives.Check()`, before the closing `OpenUiRepaint.Reset();`:

```csharp
            // (e) ONE MapStates, AND IT IS THE NAME FORM (§B.7). Two spellings of one set is the
            // two-ordering-systems mistake in miniature. The NAME form survives for two reasons written
            // into the code itself: WindowOrder.cs:243-244 records that the set is "DECLARED AS THE MAP
            // AND NOT AS THE SCREENS, so an unknown state HOLDS rather than interrupts", and a name set
            // can also name a state belonging to a mod whose Type this assembly cannot reference — which
            // is required now that TFTV's windows are in scope for the journal.
            var mapStatesHolders = typeof(OpenUiRepaint).Assembly.GetTypes()
                .SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Instance |
                                             BindingFlags.Public | BindingFlags.NonPublic |
                                             BindingFlags.DeclaredOnly)
                                  .Where(f => f.Name == "MapStates")
                                  .Select(f => new { Type = t.Name, f.FieldType }))
                .OrderBy(x => x.Type, StringComparer.Ordinal).ToList();
            if (mapStatesHolders.Count != 1)
                yield return "L543 two-mapstates: " + mapStatesHolders.Count + " type(s) declare a " +
                             "MapStates set (" +
                             string.Join(", ", mapStatesHolders.Select(h => h.Type).ToArray()) +
                             "). Exactly one may. Two spellings of one set — one by Type, one by NAME — " +
                             "is the two-ordering-systems mistake in miniature, and the by-Type copy is " +
                             "the one that goes.";
            else if (mapStatesHolders[0].FieldType != typeof(HashSet<string>))
                yield return "L543 mapstates-is-type-keyed: the surviving MapStates on " +
                             mapStatesHolders[0].Type + " is a " + mapStatesHolders[0].FieldType.Name +
                             ", not a HashSet<string>. The NAME form is the one that survives: an unknown " +
                             "state must HOLD rather than interrupt, and a name set can name a state from " +
                             "a mod whose Type this assembly cannot reference.";
```

Run RailCheck and — if `DurableWindowRegistry.cs` still exists — expect RED with `L543 two-mapstates: 2 type(s) declare a MapStates set (DurableWindowRegistry, WindowOrder).` If the companion plan already deleted the file, the arm is GREEN immediately; that is correct and the rest of this task is a no-op. **Say which case you are in, in the commit body.**

- [ ] **Step 2: Delete the by-Type copy.** In `src/Rail/DurableWindowRegistry.cs` delete the `MapStates` `HashSet<Type>` (`:334-335`) and change `MayPresent(bool, Type)`'s comparison (`:368-369`) from `MapStates.Contains(currentViewState)` to the name form:

```csharp
            // ONE MapStates, in WindowOrder, by NAME (§B.7). Declared as the MAP and not as the SCREENS,
            // so an unknown state HOLDS rather than interrupts — and a name set can also name a state
            // belonging to a mod whose Type this assembly cannot reference.
            currentViewState != null && WindowOrder.IsMapState(currentViewState.Name)
```

and in `src/Rail/WindowOrder.cs` expose the existing private set through one accessor rather than making the field public:

```csharp
        /// <summary>The single MapStates question, so the set itself stays private and there is exactly
        /// one copy in the assembly (§B.7, L543 arm e).</summary>
        internal static bool IsMapState(string viewStateName) =>
            viewStateName != null && MapStates.Contains(viewStateName);
```

Leave `HoldsForOpenScreen` (`:355-359`) calling the set directly or route it through `IsMapState` — either is fine, but do not change its behaviour. `HeldTransitionStates` (`:226-229`) is already name-based and stays exactly as it is.

- [ ] **Step 3: Build, run, kill the mutation, commit.** Run:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
.\deploy.ps1 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'
dotnet run -c Debug --project tools/RailCheck
pwsh -NoProfile -File tools/law-integrity.ps1
dotnet run -c Debug --project tools/RailSim -- --seed 1
```

Expect `RAILCHECK GREEN — laws-run=340/340 law-violations=0`, `laws: 280 file(s) + 60 inline = 340`, `law-integrity: OK` (**no digest change** — L543's body moved, its registration did not), `RAILSIM GREEN — scenarios=2/2 failures=0 seed=1`. Mutation: re-add

```csharp
        private static readonly HashSet<Type> MapStates = new HashSet<Type>
        { typeof(UIStateNothingSelected), typeof(UIStateVehicleSelected), typeof(UIStateInitial) };
```

to `DurableWindowRegistry`. Run RailCheck, confirm `L543 two-mapstates: 2 type(s) declare a MapStates set (DurableWindowRegistry, WindowOrder).` and RED; revert; confirm GREEN 340/340. Append to the L543 row in `docs/laws.md`:

```
 ARM (e) ADDED: exactly ONE `MapStates` set exists in the assembly and it is the `HashSet<string>` NAME form (`src/Rail/WindowOrder.cs:245-248`); the by-Type copy in `DurableWindowRegistry` (`:334-335`, compared at `:368-369`) is gone. SECOND MUTATION KILL: re-adding the `HashSet<Type>` copy → `L543 two-mapstates`, RED; reverted → GREEN 340/340
```

```powershell
git add src/Rail/DurableWindowRegistry.cs src/Rail/WindowOrder.cs tools/RailCheck/L543_NoHandRolledSignatureSurvives.cs docs/laws.md
git commit -m "refactor(uirepaint): collapse the duplicate mapstates set to the name form"
```

Drop `src/Rail/DurableWindowRegistry.cs` from the `git add` if the companion plan already deleted it.

---

## Task 9: No rail path segment is produced from a loop index (§B.3)

Prefix subscriptions are only safe because no path segment is positional. That is enforced today — the keyless arm aborts loudly rather than falling back to an index — and scoping now depends on it, so it gets a law.

**Files:**
- Create: `tools/RailCheck/L544_NoPathSegmentIsAnIndex.cs`
- Modify: `tools/RailCheck/Program.cs`, `L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs`, `tools/law-count.txt`, `tools/law-integrity.ps1`, `docs/laws.md`
- Read only: `src/Rail/DiffEngine.cs:1229-1311`, `src/Rail/IdentityResolver.cs:38-103`

- [ ] **Step 1: Write L544.** Create `tools/RailCheck/L544_NoPathSegmentIsAnIndex.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L544 — NO RAIL PATH SEGMENT IS PRODUCED FROM A LOOP INDEX, AND THE KEYLESS ARM HAS NO FALLBACK.
    ///
    /// Prefix subscriptions (§B.3) are only safe because element addressing is by STABLE ID: an index path
    /// churns on insert and silently mis-scopes every declaration under it (RFC 6902, §2.5). The rule is
    /// already stated in the file — DiffEngine.cs:1231 says "law 2 forbids element indices in the path" —
    /// and already ENFORCED: IdentityResolver.KeyOf derives a key from the ID-probe table
    /// { SiteId, VehicleID, ResearchID, FacilityId, Id, Def } (:38) via FormatKeyValue (:97-103), which
    /// returns a BaseDef's Guid, a non-empty string, or a non-negative int — and NULL otherwise. A null
    /// key sets keyless = true (DiffEngine.cs:1243) and the whole field is then ABORTED with a loud
    /// Incident (:1299-1302). Duplicate keys abort it too (:1279-1281). There is no index fallback
    /// anywhere: the failure mode is a VISIBLE REFUSAL, never a positional path.
    ///
    /// This law pins that, because scoping now DEPENDS on it. Retiring R4 was a decision about today's
    /// code; without a law it is a decision about today only.
    ///
    /// ARMS, all EXECUTED against the real IdentityResolver with no game:
    ///   (a) unkeyable-yields-a-key — an object with none of the six probe members yields NULL, not an
    ///       index and not a synthesised name.
    ///   (b) keyed-yields-nothing — POSITIVE CONTROL: an object WITH a probe member yields a key. Without
    ///       this, arm (a) passes against a KeyOf that always returns null, which would abort every keyed
    ///       collection in the game.
    ///   (c) negative-int-accepted — a negative int is refused (FormatKeyValue's own rule): a negative id
    ///       is the game's "not assigned yet", and accepting it would make two unassigned elements share
    ///       a path.
    ///   (d) index-fallback-present — the keyless arm in DiffEngine still reaches the Incident reporter.
    ///
    /// ROLES SEPARATED (§C.3): path construction is identical on both roles by design — that symmetry is
    /// what makes a client resolve the host's paths over its own graph — so there is no role-dependent
    /// behaviour for one role to hide.
    ///
    /// Falsify (compile-valid src mutations, each named): make FormatKeyValue return `index.ToString()`
    /// for an unkeyable element → (a); make KeyOf always return null → (b); accept negative ints → (c);
    /// replace the keyless Incident with a silent `continue` → (d).
    /// </summary>
    internal static class L544_NoPathSegmentIsAnIndex
    {
        private sealed class Unkeyable { public int NotAnId; public string AlsoNot; }
        private sealed class Keyed { public int SiteId = 76; }
        private sealed class NegativelyKeyed { public int SiteId = -1; }

        internal static IEnumerable<string> Check()
        {
            var resolver = typeof(IdentityResolver);
            var keyOf = resolver.GetMethod("KeyOf", BindingFlags.Static | BindingFlags.Public |
                                                    BindingFlags.NonPublic);
            if (keyOf == null)
            {
                yield return "L544 premise-changed: IdentityResolver.KeyOf did not resolve, so this law " +
                             "cannot execute the key derivation that every prefix declaration depends on.";
                yield break;
            }

            var unkeyable = keyOf.Invoke(null, new object[] { new Unkeyable() }) as string;
            if (unkeyable != null)
                yield return "L544 unkeyable-yields-a-key: an object with none of the six ID probe " +
                             "members produced the key '" + unkeyable + "'. It must produce NULL, so the " +
                             "field is ABORTED with a loud Incident rather than addressed positionally. " +
                             "An index path churns on insert and silently mis-scopes every declared " +
                             "prefix beneath it — the failure mode must be a visible refusal.";

            var keyed = keyOf.Invoke(null, new object[] { new Keyed() }) as string;
            if (string.IsNullOrEmpty(keyed))
                yield return "L544 positive-control: an object WITH a SiteId produced no key, so arm (a) " +
                             "passed against a derivation that returns null for everything — which would " +
                             "abort every keyed collection in the game and make the whole rail silent.";

            var negative = keyOf.Invoke(null, new object[] { new NegativelyKeyed() }) as string;
            if (negative != null)
                yield return "L544 negative-int-accepted: a negative id produced the key '" + negative +
                             "'. A negative id is the game's 'not assigned yet'; accepting it would make " +
                             "two unassigned elements share one path, which is an index collision under " +
                             "another name.";

            // (d) THE KEYLESS ARM STILL REACHES THE INCIDENT REPORTER.
            var diff = typeof(OpenUiRepaint).Assembly.GetTypes().FirstOrDefault(t => t.Name == "DiffEngine");
            var incident = diff == null ? null : diff.GetMethods(BindingFlags.Static | BindingFlags.Instance |
                                                                 BindingFlags.Public | BindingFlags.NonPublic |
                                                                 BindingFlags.DeclaredOnly)
                                                     .FirstOrDefault(m => m.Name == "Incident");
            var visit = diff == null ? null : diff.GetMethods(BindingFlags.Static | BindingFlags.Instance |
                                                              BindingFlags.Public | BindingFlags.NonPublic |
                                                              BindingFlags.DeclaredOnly)
                                                  .FirstOrDefault(m => m.Name == "VisitEntity");
            if (diff == null || incident == null || visit == null)
                yield return "L544 premise-changed: DiffEngine.VisitEntity or DiffEngine.Incident did not " +
                             "resolve, so arm (d) cannot see the abort it exists to pin.";
            else if (!Il.References(visit, incident))
                yield return "L544 index-fallback-present: DiffEngine.VisitEntity no longer reaches " +
                             "Incident. The keyless arm's ONLY correct outcome is a loud abort — " +
                             "'unkeyable/duplicate element keys — blob rebuild would husk the elements'. " +
                             "A silent skip or an index fallback would produce positional paths that no " +
                             "declared prefix can safely match.";
        }
    }
}
```

Read `IdentityResolver.KeyOf`'s real accessibility and parameter list at `src/Rail/IdentityResolver.cs:42` before finalising the `Invoke`; if it is `internal static string KeyOf(object o)` call it directly instead of reflectively and delete the `Invoke` wrappers. Read `DiffEngine`'s real incident-reporting method name at `:1299-1302` and use it — `Incident` is the name in the spec's citation, confirm it at the symbol.

- [ ] **Step 2: Register, run, kill the mutation, commit.** Add `Add(laws, () => L544_NoPathSegmentIsAnIndex.Check());`, bump `ExpectedLawRegistrations` 340 → 341, `tools/law-count.txt` to `files=281`, the `L193` literals to `(341, 341,` / `(341, 340,` / `(341, 341, "wrong")`, refresh both digests. Run:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
.\deploy.ps1 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'
dotnet run -c Debug --project tools/RailCheck
pwsh -NoProfile -File tools/law-integrity.ps1
```

Expect `RAILCHECK GREEN — laws-run=341/341 law-violations=0`, `laws: 281 file(s) + 60 inline = 341`, `law-integrity: OK`. Mutation: in `src/Rail/IdentityResolver.cs`'s `FormatKeyValue` (`:97-103`) make the negative-int branch return `value.ToString()` instead of null. Run RailCheck, confirm `L544 negative-int-accepted: a negative id produced the key '-1'.` and RED; revert; confirm GREEN 341/341. Record:

```
| L544 | NO RAIL PATH SEGMENT IS PRODUCED FROM A LOOP INDEX: `IdentityResolver.KeyOf` is EXECUTED and yields NULL for an object with none of the six ID probe members, a key for one that has one (positive control), and NULL for a negative id; and `DiffEngine.VisitEntity` still reaches the `Incident` abort, so the keyless arm has no index or blob fallback | P2 P6 P11 | principle | §B.3 / Q5 — prefix subscriptions are safe only because element addressing is by STABLE ID; an index path churns on insert and silently mis-scopes every declaration beneath it (RFC 6902). `DiffEngine.cs:1231` states the rule in the file and `:1283-1302` enforces it; R4 was RETIRED on that basis, which is a statement about today's code and needs a law to stay true tomorrow. MUTATION KILL: `FormatKeyValue` returning `value.ToString()` for a negative int → `L544 negative-int-accepted`, RED; reverted → GREEN 341/341 | premise-changed + POSITIVE CONTROL |
```

```powershell
git add tools/RailCheck/L544_NoPathSegmentIsAnIndex.cs tools/RailCheck/Program.cs tools/RailCheck/L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs tools/law-count.txt tools/law-integrity.ps1 docs/laws.md
git commit -m "test(railcheck): assert no rail path segment is produced from an index"
```

---

## Task 10: Patch, don't rebuild (§B.9) — SEPARATE AND MANDATORY

**Scoping alone does NOT fix the animation reset.** A repaint that legitimately fires must still not destroy the model. This is a distinct work item and is not "done because scoping landed".

**Files:**
- Create: `tools/RailCheck/L545_PatchDoNotRebuild.cs`
- Modify: `src/Rail/UiEventMap.cs:325` (`ReseedIdentityDisplay`), and a new non-destructive repaint for the edit screens modelled on `RepaintAugmentScreen` (`:939`)
- Modify: `src/Rail/OpenUiRepaint.cs:518-535` (the Exit+Enter fallback gains a refusal for model/animation surfaces)
- Modify: `tools/RailCheck/Program.cs`, `L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs`, `tools/law-count.txt`, `tools/law-integrity.ps1`, `docs/laws.md`

- [ ] **Step 1: Read the five destructive paths before writing anything.** In `E:\DEV\PhoenixPoint\decompiled\AssemblyCSharp` read and record `file:line` for: `UIStateEditSoldier.DisplaySoldier` (`UIStateEditSoldier.cs:584`) → `UIModuleActorCycle.DisplaySoldier(c, resetAnimation: true)`; `CommonCharacterUtils.ResetCharacterAnimation` (`CommonCharacterUtils.cs:66-73`, `Animator.Play(0,-1,0f)`); `UIModuleActorCycle.RebuildCharacter` + `CharacterLoadingIndicator.SetActive(true)` (`UIModuleActorCycle.cs:638-654`); `UIStateEditVehicle.DisplaySoldier` → `DisplayVehicle(c, resetAnimation: true)` (`UIStateEditVehicle.cs:348`). Then read the KNOWN-GOOD pattern already in the mod: `RepaintAugmentScreen` (`src/Rail/UiEventMap.cs:939`), which reaches `UIModuleActorCycle.DisplaySoldier(c, resetAnimation: false, …)` DIRECTLY instead of going through the state's private method. **Copy that shape.** Every one of these `file:line` values goes in the commit body.

- [ ] **Step 2: Add the non-destructive repaints.** In `src/Rail/UiEventMap.cs`, beside `RepaintAugmentScreen` (`:939`), add two methods with the same shape — reaching `UIModuleActorCycle.DisplaySoldier(c, resetAnimation: false, …)` and the vehicle equivalent directly, never the state's private `DisplaySoldier`. Use `RepaintAugmentScreen`'s exact reflection/binding style; it is the pattern that already works in this file, and it is the ponytail rung (reuse what lives here) rather than a new mechanism. Register both in `UiNativeRepaint.Table` for `UIStateEditSoldier` and `UIStateEditVehicle`, so `TryRepaint` reaches them and the Exit+Enter fallback is never consulted for those states.

- [ ] **Step 3: Stop `ReseedIdentityDisplay` being reached by an unrelated change.** `UiEventMap.ReseedIdentityDisplay` clears `AddonsCharacterBuilder.Addons` (`src/Rail/UiEventMap.cs:325`), which FORCES the `RebuildCharacter` branch (`UIModuleActorCycle.cs:638-654`) and its loading indicator. Gate it on the identity actually having changed — it already runs from the `CharacterIdentity` arm, so add the same value-inequality question Task 2 introduced:

```csharp
            // §B.9: clearing Addons forces the RebuildCharacter branch, which recreates GameObjects and
            // shows the loading indicator. That is correct for an identity that REALLY changed and is
            // gratuitous destruction for anything else, so it is reached only on a real change — the same
            // rule as GenericApplier.LeafChanged, applied at the one seam that can force a rebuild.
            if (!GenericApplier.LeafChanged(previousIdentityKey, currentIdentityKey)) return;
```

Read the method first and derive `previousIdentityKey`/`currentIdentityKey` from what it already has in hand; do not add a new cache if the values are already there.

- [ ] **Step 4: Refuse Exit+Enter for model/animation surfaces.** In `src/Rail/OpenUiRepaint.cs:518-535` (the Exit+Enter fallback), add before the fallback runs:

```csharp
            // §B.9: the Exit+Enter fallback destroys and rebuilds every widget on the screen — documented
            // to have restarted a cutscene 7 times — and for a screen with model/animation state it is
            // the animation reset itself. It stays as the last resort for UNDECLARED surfaces only.
            if (UiNativeRepaint.ModelAnimationSurfaces.Contains(current.GetType().Name))
            {
                if (_loggedFallback.Add(current.GetType().Name))
                    MpLog.LogWarning("[Multiplayer][uirepaint] refused the Exit+Enter fallback on " +
                                     current.GetType().Name + " — it carries model/animation state and " +
                                     "must be patched, not rebuilt. Its entry in UiNativeRepaint.Table is " +
                                     "missing or threw (logged once per screen)");
                RefreshPersistentHud();
                return;
            }
```

and declare the set in `src/Rail/UiEventMap.cs` beside `DeclaredPrefixes`:

```csharp
        /// <summary>Surfaces whose repaint MUST be a patch and never a rebuild (§B.9). Declared by NAME
        /// for the same reason DeclaredPrefixes is.</summary>
        internal static readonly System.Collections.Generic.HashSet<string> ModelAnimationSurfaces =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
            { "UIStateEditSoldier", "UIStateEditVehicle", "UIStateAugmentationScreen" };
```

Confirm the third name against the decompile before writing it; if the augmentation screen's real state name differs, use the real one and cite the `file:line`.

- [ ] **Step 5: Write L545.** Create `tools/RailCheck/L545_PatchDoNotRebuild.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L545 — A SURFACE WITH MODEL OR ANIMATION STATE IS PATCHED, NEVER REBUILT.
    ///
    /// SCOPING ALONE DOES NOT FIX THIS, which is why it is a separate law and a separate work item. A
    /// repaint that legitimately fires must still not destroy the model: UIStateEditSoldier.DisplaySoldier
    /// (UIStateEditSoldier.cs:584) reaches UIModuleActorCycle.DisplaySoldier(c, resetAnimation: true) →
    /// CommonCharacterUtils.ResetCharacterAnimation = Animator.Play(0,-1,0f)
    /// (CommonCharacterUtils.cs:66-73), or the RebuildCharacter branch with its loading indicator
    /// (UIModuleActorCycle.cs:638-654). UIStateEditVehicle.DisplaySoldier does the same via
    /// DisplayVehicle(c, resetAnimation: true) (UIStateEditVehicle.cs:348).
    ///
    /// THE KNOWN-GOOD PATTERN ALREADY EXISTS IN THIS MOD: UiEventMap.RepaintAugmentScreen
    /// (src/Rail/UiEventMap.cs:939) reaches UIModuleActorCycle.DisplaySoldier(c, resetAnimation: false, …)
    /// DIRECTLY instead of going through the state's private method. This law requires that shape for
    /// every model/animation surface.
    ///
    /// ARMS:
    ///   (a) surface-has-no-table-entry — every name in UiNativeRepaint.ModelAnimationSurfaces has an
    ///       entry in UiNativeRepaint.Table, so TryRepaint reaches a real patch and the Exit+Enter
    ///       fallback is never consulted for it.
    ///   (b) repaint-resets-animation — no method registered for such a surface reaches
    ///       CommonCharacterUtils.ResetCharacterAnimation or UIModuleActorCycle.RebuildCharacter.
    ///   (c) fallback-reachable — the Exit+Enter fallback refuses these surfaces: OpenUiRepaint.Repaint's
    ///       IL reads ModelAnimationSurfaces.
    ///   (d) set-is-empty — POSITIVE CONTROL: the set is non-empty and contains the two edit screens.
    ///       Every arm above is quantified over the set, so an empty set makes all of them vacuous — and
    ///       the empty-set version is exactly the state the code was in when the defect was reported.
    ///
    /// ROLES SEPARATED (§C.3): a repaint is a LOCAL act on whichever peer received the change, so these
    /// arms are role-free — and that is precisely the point: the reported reset happened on the peer that
    /// did NOTHING, driven by another peer's manufacturing tick.
    ///
    /// Falsify (compile-valid src mutations, each named): empty ModelAnimationSurfaces → (d); remove the
    /// UIStateEditSoldier entry from UiNativeRepaint.Table → (a); point that entry at the state's private
    /// DisplaySoldier → (b); delete the fallback refusal → (c).
    /// </summary>
    internal static class L545_PatchDoNotRebuild
    {
        internal static IEnumerable<string> Check()
        {
            var surfaces = UiNativeRepaint.ModelAnimationSurfaces;
            if (surfaces == null || surfaces.Count == 0)
            {
                yield return "L545 positive-control: UiNativeRepaint.ModelAnimationSurfaces is empty, so " +
                             "every arm of this law is quantified over nothing. That empty state is " +
                             "exactly the state the code was in when the soldier-model reset was " +
                             "reported.";
                yield break;
            }
            foreach (var required in new[] { "UIStateEditSoldier", "UIStateEditVehicle" })
                if (!surfaces.Contains(required))
                    yield return "L545 positive-control: " + required + " is not in " +
                                 "ModelAnimationSurfaces. It is one of the two screens whose repaint " +
                                 "reaches resetAnimation: true, and it is the screen the reported defect " +
                                 "was observed on.";

            var table = UiNativeRepaint.Table;
            if (table == null)
            {
                yield return "L545 premise-changed: UiNativeRepaint.Table did not resolve, so arms (a) " +
                             "and (b) cannot see what a repaint of these surfaces reaches.";
                yield break;
            }

            var reset = AccessTools.Method(
                Type.GetType("PhoenixPoint.Common.View.CommonCharacterUtils, Assembly-CSharp"),
                "ResetCharacterAnimation");
            var rebuild = AccessTools.Method(
                Type.GetType("PhoenixPoint.Common.View.ViewModules.UIModuleActorCycle, Assembly-CSharp"),
                "RebuildCharacter");

            foreach (var name in surfaces.OrderBy(x => x, StringComparer.Ordinal))
            {
                var entry = table.Keys.FirstOrDefault(k => k.Name == name);
                if (entry == null)
                {
                    yield return "L545 surface-has-no-table-entry: " + name + " has no entry in " +
                                 "UiNativeRepaint.Table, so TryRepaint finds nothing and the repaint " +
                                 "falls through to Exit+Enter — the fallback that destroys and rebuilds " +
                                 "every widget on the screen and is documented to have restarted a " +
                                 "cutscene seven times.";
                    continue;
                }
                var painter = table[entry];
                if (painter == null) continue;
                if ((reset != null && Il.References(painter, reset)) ||
                    (rebuild != null && Il.References(painter, rebuild)))
                    yield return "L545 repaint-resets-animation: the registered repaint for " + name +
                                 " reaches CommonCharacterUtils.ResetCharacterAnimation " +
                                 "(Animator.Play(0,-1,0f)) or UIModuleActorCycle.RebuildCharacter. It " +
                                 "must reach DisplaySoldier(c, resetAnimation: false, …) directly, the " +
                                 "shape UiEventMap.RepaintAugmentScreen already uses.";
            }

            // (c) THE FALLBACK REFUSES THESE SURFACES.
            var repaintMethod = typeof(OpenUiRepaint).GetMethod("Repaint",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var contains = typeof(HashSet<string>).GetMethod("Contains");
            if (repaintMethod == null)
                yield return "L545 premise-changed: OpenUiRepaint.Repaint did not resolve, so arm (c) " +
                             "cannot see whether the fallback refuses a model/animation surface.";
            else if (contains != null && !Il.References(repaintMethod, contains))
                yield return "L545 fallback-reachable: OpenUiRepaint.Repaint never consults " +
                             "ModelAnimationSurfaces, so the Exit+Enter fallback is still reachable for a " +
                             "screen carrying model and animation state. For those screens the fallback " +
                             "IS the animation reset.";
        }
    }
}
```

Add `using HarmonyLib;` for `AccessTools`. Verify `UiNativeRepaint.Table`'s real declared type and accessibility at `src/Rail/OpenUiRepaint.cs:~985` / `src/Rail/UiEventMap.cs` before finalising the iteration — if it is not a `Dictionary<Type, MethodInfo>`, adapt the loop to whatever it really is and cite the `file:line`.

- [ ] **Step 6: Register, run, kill the mutation, commit.** Add `Add(laws, () => L545_PatchDoNotRebuild.Check());`, bump `ExpectedLawRegistrations` 341 → 342, `tools/law-count.txt` to `files=282`, the `L193` literals to `(342, 342,` / `(342, 341,` / `(342, 342, "wrong")`, refresh both digests. Run:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
.\deploy.ps1 -GameDir 'D:\Steam\steamapps\common\Phoenix Point'
dotnet run -c Debug --project tools/RailCheck
pwsh -NoProfile -File tools/law-integrity.ps1
dotnet run -c Debug --project tools/RailSim -- --seed 1
```

Expect `RAILCHECK GREEN — laws-run=342/342 law-violations=0`, `laws: 282 file(s) + 60 inline = 342`, `law-integrity: OK`, `RAILSIM GREEN — scenarios=2/2 failures=0 seed=1`. Mutation: empty `UiNativeRepaint.ModelAnimationSurfaces` (leave the field, remove its initialiser entries). Run RailCheck, confirm:

```
L545 positive-control: UiNativeRepaint.ModelAnimationSurfaces is empty, so every arm of this law is quantified over nothing. …
RAILCHECK RED — 1 executable law violation(s); --update cannot baseline them.
```

Revert, re-run, confirm GREEN 342/342. Record:

```
| L545 | A SURFACE WITH MODEL OR ANIMATION STATE IS PATCHED, NEVER REBUILT: every name in `UiNativeRepaint.ModelAnimationSurfaces` has a `UiNativeRepaint.Table` entry, none of those entries reaches `CommonCharacterUtils.ResetCharacterAnimation` or `UIModuleActorCycle.RebuildCharacter`, `OpenUiRepaint.Repaint` refuses the Exit+Enter fallback for them, and the set is non-empty and contains both edit screens (positive control — the empty set is the state the code was in when the defect was reported) | P11 P4c | incident | reported soldier-model/animation reset: `UIStateEditSoldier.DisplaySoldier` (`UIStateEditSoldier.cs:584`) → `UIModuleActorCycle.DisplaySoldier(c, resetAnimation: true)` → `CommonCharacterUtils.ResetCharacterAnimation` = `Animator.Play(0,-1,0f)` (`CommonCharacterUtils.cs:66-73`), or `RebuildCharacter` + `CharacterLoadingIndicator.SetActive(true)` (`UIModuleActorCycle.cs:638-654`); same via `UIStateEditVehicle.cs:348`. SCOPING ALONE DOES NOT FIX THIS — a repaint that legitimately fires must still not destroy the model. Known-good shape copied from `UiEventMap.RepaintAugmentScreen` (`src/Rail/UiEventMap.cs:939`). MUTATION KILL: emptying `ModelAnimationSurfaces` → `L545 positive-control`, RED; reverted → GREEN 342/342 | premise-changed + POSITIVE CONTROL |
```

```powershell
git add src/Rail/UiEventMap.cs src/Rail/OpenUiRepaint.cs tools/RailCheck/L545_PatchDoNotRebuild.cs tools/RailCheck/Program.cs tools/RailCheck/L193_TheHarnessCannotReportAVerdictItDidNotEarn.cs tools/law-count.txt tools/law-integrity.ps1 docs/laws.md
git commit -m "fix(uirepaint): patch the soldier and vehicle screens instead of rebuilding them"
```

**Field verification, and this task is not complete without it:** run a 3-instance session, stand on the soldier-edit screen on one peer while another peer runs a manufacturing tick, and confirm the local soldier model and animation are NOT reset. Report the observation and the relevant log lines.

---

## Spec coverage — every section to its task

| Spec section | Decision | Task |
|---|---|---|
| §B.1 carry the path through | `TouchedLeaf` in `GenericApplier`; `Fire` forwards it; `MarkDirty(kind, geo, path)` | 3 |
| §B.2 mark dirty ONLY on value inequality | `GenericApplier.LeafChanged`, compared by value/bytes, never by reference; L540 | 2 |
| §B.3 static path prefixes, opt-in only; no declaration ⇒ repaint everything | `UiNativeRepaint.DeclaredPrefixes`, `OpenUiRepaint.SurfaceRepaints`; L541 | 4 |
| §B.3 the `EntityList` trap and the ORDER-at-the-field-path rule | written into `DeclaredPrefixes`'s doc comment as the rule an author must check | 4 |
| §B.3 law: no path segment from a loop index | L544 executes `IdentityResolver.KeyOf` and pins the keyless `Incident` abort | 9 |
| §B.4 the global bool STAYS; the ~63 kindless sites keep the unconditional arm | L542 (floor of 40 callers, `_dirty` positive control) | 6 |
| §B.5 mark during the batch, repaint once at batch end | the evaluate happens inside the existing `FlushIfDirty`; coalescing and the drag/typing defer are kept | 4 |
| §B.6 presentation reads, never writes | no task adds a write to replicated state; every change here is a decision not to repaint or which overload to reach | 2–10 |
| §B.7 the surface is `view.CurrentViewState.GetType()`, local-only | `FlushIfDirty` derives it locally via `GenericApplier.GeoLevel()?.View?.CurrentViewState`; nothing is replicated and no packet type is added | 4 |
| §B.7 the duplicate `MapStates` pair collapses to the NAME form | `DurableWindowRegistry.MapStates` deleted; `WindowOrder.IsMapState`; L543 arm (e) | 8 |
| §B.8 delete the hand-rolled signatures; KEEP `RepaintNeeded` | five builders deleted, `ScopeKey`/`BumpScopeGenerations` replace them; L543 arms (a) and (b) | 7 |
| §B.8 fix `RefreshInfoBar`'s ordering | `InfoBarNeedsRefresh(barLive, key)` in L516's shape; L543 arms (c) and (d) | 7 |
| §B.9 patch, don't rebuild | non-destructive repaints modelled on `RepaintAugmentScreen`, `ReseedIdentityDisplay` gated, Exit+Enter refused; L545 | 10 |
| §C.1 harness, injectable clock and transport, seeded | `tools/RailSim` (shared with the companion plan; duplicated here if it does not exist) | 1 |
| §C.1 property 3 — an untouched declared surface did NOT repaint | `an-untouched-surface-does-not-repaint`, asserting an observable history over five batches | 5 |
| §C.2 what the harness cannot prove | stated verbatim in `tools/RailSim/Program.cs`'s header | 1 |
| §C.3 roles separated in every new law | each law's header states why its arms are role-free or names its per-role arms | 2–10 |
| §8 item 11 extend `L38` or add a law that covers the kindless sites | decided in Task 6 Step 1 — prefer leaving `L38` alone and adding `L542`; the decision is recorded in the commit body | 6 |
| R3 a too-shallow prefix is safe but invisible | L541 arm (e) executes a declared surface in BOTH directions, so a useless declaration shows up | 4 |
| R4 index-based path segments | RETIRED by Q5, and pinned by L544 so it stays retired | 9 |
| R7 no law legitimising a duplicate | every law here says "there is one mechanism": one `MapStates`, one read-set mechanism, one mark predicate | 6, 7, 8 |

**§C.1 properties 1, 2, 4 and 5** (identical presentation order, no permanent gap, local dismissal, global dismissal) belong to the window journal and are implemented by `docs/superpowers/plans/2026-08-15-window-journal.md`. They are deliberately NOT in this plan.

**Not in scope, by §4:** rewriting the rail, per-widget opt-in sync, replicating a peer's surface, touching the ~63 kindless `MarkDirty()` sites, and any broad refactor "while we are in there".

