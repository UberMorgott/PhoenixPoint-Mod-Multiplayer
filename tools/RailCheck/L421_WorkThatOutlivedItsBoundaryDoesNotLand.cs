using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L421 — WORK THAT OUTLIVED ITS BOUNDARY MAY NOT LAND.
    ///
    /// THE FAILURE (b0656e9). <c>MistSync</c> hands the mist array to a <c>ThreadPool</c> worker to compress
    /// and base64, and the worker publishes into <c>_ready</c> for the main thread to pick up.
    /// <c>ResetForReloadBoundary</c> clears <c>_ready</c> — and cannot reach a worker that is already running.
    /// So an encode queued BEFORE a reload boundary landed AFTER one queued for a later hour and overwrote the
    /// fresh shipment with the PRE-boundary array. <c>_lastShippedHours</c> was by then the later hour, so the
    /// <c>hours == _lastShippedHours</c> gate refused to encode anything further until the campaign clock
    /// turned: up to a full in-game hour of WRONG MIST on every client. Mist is gameplay
    /// (<c>MistRendererSystem.IsInMist</c>), not decoration.
    ///
    /// THE GENERAL SHAPE, WHICH IS WHY IT IS WORTH A LAW. A cancel that clears a RESULT SLOT does not cancel
    /// the producer; the producer must carry the generation it was queued in and the consumer must check it.
    /// The two-part symptom is also general: dropping the stale result is only half a fix — the gate that the
    /// clobbered work already advanced has to be RE-ARMED, or the drop just converts wrong data into no data.
    ///
    /// WHY IL AND NOT EXECUTION. The executable form would construct a <c>Shipment</c> with a stale epoch and
    /// call <c>HostTick</c>. <c>HostTick</c> reads <c>Time.realtimeSinceStartup</c> immediately after the
    /// consumer block, and that is a Unity ECall which THROWS in a console host — this harness has been bitten
    /// by exactly that member before (see <see cref="L193_TheHarnessCannotReportAVerdictItDidNotEarn"/>'s note
    /// on <c>RailMeta.CountMiss</c>). A law whose arm depends on an ECall reports a HARNESS-CRASH instead of a
    /// verdict, so the arms below read the IL, and they read the two REFERENCE COUNTS rather than mere
    /// presence — which is what makes them fall over when the check is removed rather than when the file is.
    ///
    /// THE ARMS:
    ///   (a) <c>shipment-carries-no-epoch</c> — the handoff record declares the generation it was queued in.
    ///   (b) <c>boundary-orphans-nothing</c> — <c>ResetForReloadBoundary</c> advances <c>_epoch</c>. Clearing
    ///       <c>_ready</c> alone is the defect verbatim.
    ///   (c) <c>worker-carries-no-epoch</c> — the queued closure writes that field, so the worker carries the
    ///       generation it CAPTURED and not whatever the main thread holds when it lands.
    ///   (d) <c>consumer-does-not-check</c> — <c>HostTick</c> reads <c>Shipment.Epoch</c> (there is exactly one
    ///       reason to: the comparison) and touches <c>_epoch</c> at least TWICE (the comparison and the
    ///       capture). Deleting the comparison leaves the capture behind, so a presence test alone would stay
    ///       green through the whole bug.
    ///   (e) <c>drop-does-not-rearm</c> — <c>_lastShippedHours</c> is touched BEFORE the first
    ///       <c>Time.realtimeSinceStartup</c> read, i.e. inside the consumer block rather than only in the
    ///       encode path below the interval gate. Without the re-arm the hour gate stays shut on the clobbered
    ///       hour and the drop trades wrong mist for no mist.
    ///
    /// GUARD: <c>premise-changed</c> when the type, the nested handoff record, the epoch field, the boundary
    /// reset or the tick stop resolving — every arm is a presence/count claim about that family, and a family
    /// that has moved would let all five pass while nothing is checked.
    ///
    /// Falsify: delete <c>Epoch</c> from <c>Shipment</c> → (a); drop the <c>_epoch++</c> from
    /// <c>ResetForReloadBoundary</c> → (b); drop <c>Epoch = epoch</c> from the queued lambda → (c); delete the
    /// <c>if (ready.Epoch == _epoch)</c> and apply unconditionally → (c)+(d); delete the <c>else
    /// _lastShippedHours = -1</c> → (e).
    /// </summary>
    internal static class L421_WorkThatOutlivedItsBoundaryDoesNotLand
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var mist = typeof(MistSync);
            var shipment = mist.GetNestedType("Shipment", All);
            var epochField = mist.GetField("_epoch", All);
            var lastShipped = mist.GetField("_lastShippedHours", All);
            var reset = mist.GetMethod("ResetForReloadBoundary", All);
            var hostTick = mist.GetMethod("HostTick", All);

            if (shipment == null || epochField == null || lastShipped == null || reset == null || hostTick == null)
            {
                yield return "L421 premise-changed: MistSync's worker handoff no longer resolves (nested " +
                             "Shipment, _epoch, _lastShippedHours, ResetForReloadBoundary, HostTick). Every arm " +
                             "below is a claim about THAT family, so all five would pass while a pre-boundary " +
                             "encode is free to land on top of a post-boundary one again — which cost every " +
                             "client up to an in-game hour of wrong mist, and mist is gameplay. Re-point this " +
                             "law at wherever the off-thread encode lives now.";
                yield break;
            }

            // ── (a) THE RECORD CARRIES ITS GENERATION ───────────────────────────────
            var stamp = shipment.GetField("Epoch", All);
            if (stamp == null || stamp.FieldType != typeof(int))
            {
                yield return "L421 shipment-carries-no-epoch: MistSync.Shipment has no int Epoch. The record is " +
                             "the ONLY thing that crosses from the worker to the main thread, so it is the only " +
                             "place the generation it was queued in can be carried — ResetForReloadBoundary can " +
                             "clear the slot but cannot reach a ThreadPool item already running.";
                yield break;   // (c)/(d) are about this field
            }

            // ── (b) THE BOUNDARY ORPHANS WHAT IS IN FLIGHT ──────────────────────────
            if (!Program.ReadsField(reset, epochField))
                yield return "L421 boundary-orphans-nothing: ResetForReloadBoundary does not advance _epoch. It " +
                             "clears _ready, which is the defect verbatim: a worker queued before the boundary " +
                             "is untouched by that and re-publishes the PRE-boundary array afterwards, on top of " +
                             "a valid post-boundary shipment.";

            // ── (c) THE WORKER CARRIES THE ONE IT CAPTURED ──────────────────────────
            var queuer = Bodies(mist).FirstOrDefault(m => Program.CalleeSequence(m)
                .Any(c => c != null && c.Name == "QueueUserWorkItem"));
            if (queuer == null)
                yield return "L421 premise-changed: nothing in MistSync queues a ThreadPool work item any more. " +
                             "If the encode came back onto the main thread this law has no subject; if it moved " +
                             "to another mechanism, re-point it — the hazard is the mechanism, not the file.";
            // HostTick itself touches Shipment.Epoch (that is arm (d)'s comparison), so it cannot count here
            // or the capture and the check would prove each other.
            var writers = Bodies(mist).Where(m => m.MetadataToken != hostTick.MetadataToken && Writes(m, stamp)).ToList();
            if (writers.Count == 0)
                yield return "L421 worker-carries-no-epoch: nothing in MistSync (closures included) ever WRITES " +
                             "Shipment.Epoch, so every shipment reaches the main thread stamped 0 and the " +
                             "comparison below is against a constant. The value has to be captured at QUEUE " +
                             "time — reading _epoch inside the worker would read whatever the main thread holds " +
                             "when the worker happens to run, which is the bug with an extra field.";

            // ── (d) THE CONSUMER ACTUALLY CHECKS IT ─────────────────────────────────
            var tickFields = Program.FieldSites(hostTick);
            if (!tickFields.Any(s => Same(s.Value, stamp)))
                yield return "L421 consumer-does-not-check: HostTick never reads Shipment.Epoch. It is the main " +
                             "thread's only chance to notice that the shipment in its hand belongs to a campaign " +
                             "state that no longer exists — and applying it writes a pre-boundary mist array over " +
                             "a correct one, for every client, for as long as the hour gate stays shut.";
            int epochRefs = tickFields.Count(s => Same(s.Value, epochField));
            if (epochRefs < 2)
                yield return "L421 consumer-does-not-check: HostTick touches _epoch " + epochRefs + " time(s); it " +
                             "needs two — the CAPTURE handed to the worker, and the COMPARISON that drops a " +
                             "shipment from an older generation. One reference is the capture on its own, which " +
                             "is exactly the state this bug shipped in: the stamp was carried and nobody read it.";

            // ── (e) …AND RE-ARMS THE GATE IT JUST LEFT SHUT ─────────────────────────
            int clock = Program.CallSites(hostTick)
                               .Where(s => s.Value != null && s.Value.Name == "get_realtimeSinceStartup")
                               .Select(s => (int?)s.Key).FirstOrDefault() ?? int.MaxValue;
            if (clock == int.MaxValue)
                yield return "L421 premise-changed: HostTick no longer reads Time.realtimeSinceStartup, which is " +
                             "the landmark arm (e) uses to tell the consumer block from the encode path below the " +
                             "interval gate. Re-derive the boundary before trusting the re-arm.";
            else if (!tickFields.Any(s => Same(s.Value, lastShipped) && s.Key < clock))
                yield return "L421 drop-does-not-rearm: HostTick touches _lastShippedHours only BELOW the " +
                             "interval gate — i.e. only where it records the hour it just queued, never in the " +
                             "consumer block that drops a stale shipment. Dropping is half a fix. The clobbered " +
                             "hour was already written into _lastShippedHours by the worker that got overwritten, " +
                             "so `hours == _lastShippedHours` then refuses to re-encode it and the drop turns " +
                             "wrong mist into NO mist until the campaign clock moves on. Re-arm it to -1 on the " +
                             "drop.";
        }

        private static bool Writes(MethodBase m, FieldInfo f) =>
            Program.FieldSites(m).Any(s => Same(s.Value, f));

        private static bool Same(FieldInfo a, FieldInfo b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;

        /// <summary>A type plus its compiler-generated nests — the queued lambda's IL is in a closure class,
        /// which is the only place the capture can be seen at all.</summary>
        private static IEnumerable<MethodBase> Bodies(Type root)
        {
            foreach (var t in Nest(root))
            {
                List<MethodBase> ms;
                try { ms = t.GetMethods(All | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                            .Concat(t.GetConstructors(All)).ToList(); }
                catch { continue; }
                foreach (var m in ms) yield return m;
            }
        }

        private static IEnumerable<Type> Nest(Type root)
        {
            yield return root;
            foreach (var n in root.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                foreach (var d in Nest(n)) yield return d;
        }
    }
}
