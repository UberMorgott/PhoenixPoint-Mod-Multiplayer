using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.View;

namespace RailCheck
{
    /// <summary>
    /// L511 — A WINDOW EVERY PEER GETS IS QUEUED FROM INSIDE THE APPLY THAT CARRIED IT.
    ///
    /// THE REPORT (owner, 2026-08-14, still live 2026-08-15): the host saw a research-completed window and
    /// dismissed it; an ally saw the SAME window a minute and a half later, and in a different position
    /// relative to the geoscape event that arrived beside it. Contents identical on every peer, ORDER not.
    ///
    /// WHY NO EXISTING LAW SAW IT. L124 established the cross-surface key and L507 proved the host's own
    /// provisional key is back-filled by the envelope that carries its cause. Both are about the KEY. This
    /// is about the ROUTE: a receiver that queues a mirrored window from somewhere OTHER than the apply
    /// runs with <c>RailOrdinal.Current == 0</c> and therefore keys it off its OWN encoder counter — a
    /// value the sender never used for that cause — no matter how correct the back-fill machinery is.
    /// Measured: both clients logged `ResearchSync CLIENT deferred 2 completed research window(s)` at
    /// 13:24:45.450 from inside <c>UIStateGeoscapeEvent</c>, i.e. the completion was in the mirror and the
    /// window was NOT queued yet; it was queued later, from the sync tick, outside every apply.
    ///
    /// THE RULE. The presentation of a mirrored family runs inside <c>UiEventMap.Fire</c>, which the apply
    /// calls, and the apply publishes the inbound ordinal for the whole synchronous dispatch
    /// (<c>SurfaceRouter.OnInbound</c> → <c>RailOrdinal.Applying</c>). Then <c>WindowOrder.StampAt</c>
    /// INHERITS it and every peer keys the window with the ordinal of the one message that caused it, by
    /// construction rather than by coincidence.
    ///
    /// NOT A QUORUM (P13): every value here is a counter on this peer's own rail. Nothing waits on a human.
    ///
    /// THE ARMS:
    ///   (a) <c>window-queued-outside-the-apply</c> — IL: <c>UiEventMap.Fire</c> reaches the mirrored
    ///       research-completion presenter. If the only route to it is the sync tick, every client keys the
    ///       window off its own counter.
    ///   (b) <c>apply-publishes-no-ordinal</c> — IL: the inbound dispatch opens a
    ///       <c>RailOrdinal.Applying</c> scope. Without it arm (a) buys nothing: <c>Current</c> is 0 inside
    ///       the apply too, and the inheritance in <c>ForNewWindow</c> never fires.
    ///   (c) <c>route-does-not-decide-the-key</c>, EXECUTED POSITIVE CONTROL — drive the production
    ///       <c>WindowOrder.StampAt</c> down BOTH routes for one cause: inside the apply it must carry the
    ///       applying ordinal, outside it must NOT. Arms (a) and (b) are IL claims about a route; this is
    ///       the proof that taking the wrong route actually changes the key, i.e. that they are worth
    ///       asserting at all.
    ///   (d) <c>order-does-not-follow-the-key</c>, EXECUTED — the REAL <c>WindowOrder.Reorder</c> over a
    ///       REAL queue puts the apply-keyed window ahead of the tick-keyed one. A key that differs but
    ///       does not move the sort would not have produced the report.
    ///
    /// WHAT THIS LAW DOES NOT PROVE, stated so nobody reads it as more than it is:
    ///   • NOT that the tick drain is unreachable. It is deliberately reachable (L412
    ///     deferred-window-has-no-drain) for the case where the peer had no geoscape to raise off at all.
    ///     This law asserts the NORMAL route exists and is the apply, not that the fallback is dead.
    ///   • NOT that <c>RailOrdinal.Provisional</c> is unreachable on a receiver: a client's OWN gesture
    ///     legitimately raises a local window outside any apply, and that is what the provisional path is
    ///     for (L507).
    ///   • NOT anything about window CONTENT, only about the order key it is stamped with.
    ///
    /// Falsify:
    ///   • delete the <c>ResearchSync.PumpDeferredCompletions</c> call from <c>UiEventMap.Fire</c> → (a)
    ///     [VERIFIED RED 2026-08-15, restored GREEN]
    ///   • make <c>RailOrdinal.ForNewWindow</c> ignore <c>Current</c> → (c)+(d)
    ///     [VERIFIED RED 2026-08-15, restored GREEN]
    ///   • drop the <c>using (RailOrdinal.Applying(ordinal))</c> scope in <c>SurfaceRouter.OnInbound</c> → (b)
    ///     [not executed as a mutation: removing the scope also removes the only publisher of the ordinal
    ///      and turns L124/L507 red, so it is the same kill the two of them already own]
    /// </summary>
    internal static class L511_AMirroredWindowIsKeyedByTheApplyThatCarriedIt
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var mod = typeof(ResearchSync).Assembly;
            var fire = mod.GetType("Multiplayer.Network.Sync.UiEventMap")?.GetMethod("Fire", All);
            var pump = typeof(ResearchSync).GetMethod("PumpDeferredCompletions", All);
            var inbound = mod.GetType("Multiplayer.Network.Sync.SurfaceRouter")?.GetMethod("OnInbound", All);
            var applying = typeof(RailOrdinal).GetMethod("Applying", All);
            if (fire == null || pump == null || inbound == null || applying == null)
            {
                yield return "L511 premise-changed: UiEventMap.Fire / ResearchSync.PumpDeferredCompletions / " +
                             "SurfaceRouter.OnInbound / RailOrdinal.Applying did not resolve, so nothing checks " +
                             "that a window every peer gets is queued from inside the message that caused it. " +
                             "Re-point this law — the failure it guards is silent and cosmetic-looking: right " +
                             "contents, wrong order, on one peer only.";
                yield break;
            }

            // ── (a) the mirrored presenter is reachable from the apply's own post-batch fire ────────────
            if (!Program.Callees(fire, mod).Any(c => Same(c, pump)))
                yield return "L511 window-queued-outside-the-apply: UiEventMap.Fire no longer queues the " +
                             "research completion, so the only thing that can is the sync tick — which runs " +
                             "with RailOrdinal.Current == 0 and keys the window off THIS peer's encoder " +
                             "counter, a value the sender never used for that cause. Every peer then sorts " +
                             "the same window by a different number: the 2026-08-14 order report exactly.";

            // ── (b) and the apply actually publishes an ordinal to inherit ─────────────────────────────
            if (!Program.Callees(inbound, mod).Any(c => Same(c, applying)))
                yield return "L511 apply-publishes-no-ordinal: SurfaceRouter.OnInbound no longer opens a " +
                             "RailOrdinal.Applying scope, so RailOrdinal.Current is 0 during the dispatch too " +
                             "and ForNewWindow inherits nothing. Arm (a) would stay green while every mirrored " +
                             "window silently fell back to this peer's own counter.";

            // ── (c) POSITIVE CONTROL, EXECUTED: the route is what decides the key ───────────────────────
            RailOrdinal.Reset();
            const uint carried = 900u;
            RailOrdinal.Observe(carried);               // this peer has been told about everything up to here
            var mirrored = new GeoscapeViewStateSwitchRequest(null, 0);
            using (RailOrdinal.Applying(carried)) WindowOrder.StampAt(mirrored, 0f);
            var strayed = new GeoscapeViewStateSwitchRequest(null, 0);
            WindowOrder.StampAt(strayed, 0f);           // the tick route: no apply in scope
            uint inherited = WindowOrder.OrdinalOf(mirrored);
            uint local = WindowOrder.OrdinalOf(strayed);
            if (inherited != carried)
                yield return "L511 route-does-not-decide-the-key: a window stamped INSIDE the apply of " +
                             "ordinal " + carried + " carries " + inherited + ". The whole point of queueing " +
                             "it from the apply is that the key is the sender's, not this peer's.";
            if (local == carried)
                yield return "L511 route-does-not-decide-the-key: a window stamped OUTSIDE any apply carries " +
                             carried + " too, so the two routes are indistinguishable here and arms (a) and " +
                             "(b) prove nothing. Re-ground this control before believing them.";

            // ── (d) EXECUTED: and the real sort follows the key ────────────────────────────────────────
            var queue = new List<GeoscapeViewStateSwitchRequest> { strayed, mirrored };
            WindowOrder.Reorder(queue, WindowOrder.OrdinalOf);
            if (!ReferenceEquals(queue[0], mirrored))
                yield return "L511 order-does-not-follow-the-key: the real queue still opens the tick-keyed " +
                             "window (ordinal " + local + ") before the apply-keyed one (ordinal " +
                             inherited + "). The key is what every peer compares; a difference that does not " +
                             "move the sort is not the defect the owner saw, and one that does is.";
            RailOrdinal.Reset();
        }

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;
    }
}
