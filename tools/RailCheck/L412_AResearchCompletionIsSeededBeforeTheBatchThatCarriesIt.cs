using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L412 — THE RESEARCH-COMPLETE LATCH IS SEEDED FROM THE MIRROR *BEFORE* THE BATCH, AND A WINDOW IT
    /// CANNOT SHOW YET IS DEFERRED, NEVER DROPPED.
    ///
    /// THE FAILURE (live 2026-08-11, host + Instance2 + Instance3). <c>ModalType.GeoResearchComplete</c> is
    /// deliberately LocalOnly (GeoWindowCoverage.cs:379-398): the window is NOT mirrored, every peer raises
    /// its own off its own mirrored state through <c>ResearchSync.PresentFromMirror</c>. That bargain has a
    /// second half nothing checked — the latch that tells a real transition from join/reload BACKLOG was
    /// seeded LAZILY, inside the first <c>PresentFromMirror</c> after a reset, which runs AFTER the batch
    /// applied. So the first post-boundary batch was both the seed and the transition, and the transition
    /// was discarded by design.
    ///
    /// That is not a narrow race. The geoscape clock is PAUSED across a tactical mission, so the first
    /// research delta after a mission return is whatever completes when somebody presses play. Measured:
    /// both clients crossed the reload boundary at t≈478.7 ("raise of 'PROG_SY0_WIN' HELD — this peer has
    /// no geoscape ready to carry a window yet"), peer 1 resumed the clock at host t=543.067, the research
    /// completed 240 ms later — host log: "Queuerd state switch UIStateGeoModal with priority 99", which is
    /// GeoResearchComplete and nothing else (GeoscapeView.cs:1987-1990) — and NEITHER client ever entered
    /// <c>UIStateGeoModal</c> at any point in the session. The window every peer was owed appeared on the
    /// host alone, and no line in any log said so.
    ///
    /// TWO THINGS THEREFORE HAVE TO STAY TRUE, and this law asserts exactly those two:
    ///   (a) <c>GenericApplier.ApplyDelta</c> seeds the latch BEFORE it fires <c>UiEventMap.Fire</c>, so the
    ///       baseline is the state the peer LOADED and every later delta is a real transition;
    ///   (b) the completion is DEFERRED, not dropped, when this peer is not on a geoscape map surface
    ///       (<c>DurableWindowRegistry.MayPresent</c>) — and something OUTSIDE the apply drains it, or
    ///       "deferred" means "lost" for a peer no further research delta ever reaches. This half became
    ///       load-bearing the day geoscape time stopped pausing for a peer's own tab (L413/TimeSync): a
    ///       research can now complete while this peer is deep in Manufacturing, and P13 forbids yanking it
    ///       out of an unrelated screen.
    /// NOT A QUORUM (P13): the gate reads THIS peer's own view state only; nothing waits on another human,
    /// and a peer that never returns to the map merely keeps its own window pending.
    ///
    /// L49's ordering arm is the neighbour, not a substitute: it asserts the surviving producer is driven
    /// from the apply. It cannot see whether the latch in front of that producer swallowed the transition.
    ///
    /// GUARDED TWICE: every member must resolve, and the IL walk over <c>ApplyDelta</c> must reach
    /// <c>UiEventMap.Fire</c> — the landmark at the very bottom of that method — so a reader that died on an
    /// opcode reports itself instead of accusing the applier.
    ///
    /// Falsify: delete the <c>ResearchSync.SeedLatchFromMirror</c> call from <c>GenericApplier.ApplyDelta</c>
    /// → seed-after-the-batch; delete the <c>ResearchSync.ClientTick</c> call from the sync tick → deferred
    /// -window-has-no-drain; drop the <c>MayPresent</c> gate from <c>PumpDeferredCompletions</c> → yanks.
    /// </summary>
    internal static class L412_AResearchCompletionIsSeededBeforeTheBatchThatCarriesIt
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var mod = typeof(ResearchSync).Assembly;
            var applier = mod.GetType("Multiplayer.Network.Sync.GenericApplier");
            var applyDelta = applier?.GetMethod("ApplyDelta", All);
            var uiEventMap = mod.GetType("Multiplayer.Network.Sync.UiEventMap");
            var fire = uiEventMap?.GetMethod("Fire", All);
            var seed = typeof(ResearchSync).GetMethod("SeedLatchFromMirror", All);
            var pump = typeof(ResearchSync).GetMethod("PumpDeferredCompletions", All);
            var clientTick = typeof(ResearchSync).GetMethod("ClientTick", All);
            var stub = mod.GetType("Multiplayer.Network.Sync.SyncEngine");   // SyncEngineStub.cs declares it
            var tick = stub?.GetMethod("Tick", All);
            var mayPresent = typeof(DurableWindowRegistry).GetMethods(All)
                             .FirstOrDefault(m => m.Name == "MayPresent" && m.GetParameters().Length == 2);

            if (applyDelta == null || fire == null || seed == null || pump == null || clientTick == null ||
                tick == null || mayPresent == null)
            {
                yield return "L412 premise-changed: one of GenericApplier.ApplyDelta / UiEventMap.Fire / " +
                             "ResearchSync.SeedLatchFromMirror / PumpDeferredCompletions / ClientTick / " +
                             "SyncEngineStub.Tick / DurableWindowRegistry.MayPresent no longer resolves, so " +
                             "nothing checks that a research completion survives the first batch after a " +
                             "reload boundary. Re-point this law — do not delete it: what it guards is the " +
                             "window every peer is owed appearing on the host alone, silently.";
                yield break;
            }

            var applyCallees = Program.Callees(applyDelta, mod).ToList();
            int fireAt = applyCallees.FindIndex(c => c.MetadataToken == fire.MetadataToken &&
                                                     c.Module == fire.Module);
            if (fireAt < 0)
            {
                // ANTI-VACUITY: Fire is the last thing ApplyDelta does, so not seeing it means the IL walk
                // stopped early and every verdict below would be about what the reader missed.
                yield return "L412 premise-changed: the IL walk over GenericApplier.ApplyDelta never reached " +
                             "UiEventMap.Fire, the call that closes that method — the scan stopped early, so " +
                             "the verdicts below would accuse the applier of something nobody looked at. Fix " +
                             "the reader, or pick a new landmark at the end of the batch.";
                yield break;
            }

            int seedAt = applyCallees.FindIndex(c => c.MetadataToken == seed.MetadataToken &&
                                                     c.Module == seed.Module);
            if (seedAt < 0 || seedAt > fireAt)
                yield return "L412 seed-after-the-batch: GenericApplier.ApplyDelta does not seed the research " +
                             "presentation latch before it fires UiEventMap.Fire" +
                             (seedAt < 0 ? " (it never calls ResearchSync.SeedLatchFromMirror at all)" :
                                           " (the seed call comes AFTER Fire)") +
                             ". Seeded from inside the post-batch Fire, the first batch after a reload " +
                             "boundary is BOTH the baseline and the transition, and the transition loses: the " +
                             "research that completes on the first clock motion after a mission return is " +
                             "latched as backlog and its window opens on the host alone (live 2026-08-11).";

            if (!Program.Callees(pump, mod).Any(c => c.MetadataToken == mayPresent.MetadataToken &&
                                                     c.Module == mayPresent.Module))
                yield return "L412 completion-yanks-the-player: ResearchSync.PumpDeferredCompletions no longer " +
                             "asks DurableWindowRegistry.MayPresent before raising the native window. Geoscape " +
                             "time keeps running while a peer is in its own tab (L413), so a research can now " +
                             "complete while this peer is inside Manufacturing — raising the modal there tears " +
                             "it out of a screen it chose to be in (P13). The gate reads THIS peer's own view " +
                             "state and waits on nobody.";

            if (!Program.Callees(tick, mod).Any(c => c.MetadataToken == clientTick.MetadataToken &&
                                                     c.Module == clientTick.Module))
                yield return "L412 deferred-window-has-no-drain: SyncEngineStub.Tick no longer calls " +
                             "ResearchSync.ClientTick, so the only thing that can present a DEFERRED " +
                             "completion is the next research delta — and after the last research in the " +
                             "queue completes there may never be another. 'Deferred' then means 'lost', which " +
                             "is the exact bug class this file exists for. This is the same undrained-queue " +
                             "shape as DurableInboxEngine.TryPresentNext having no caller (fixed 1aca76d).";
        }
    }
}
