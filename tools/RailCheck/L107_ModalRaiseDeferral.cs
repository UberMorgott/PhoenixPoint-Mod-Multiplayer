using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Utils;

namespace RailCheck
{
    /// <summary>
    /// L107 — A RAISE THAT ARRIVES BEFORE ITS ENTITY MUST END UP SHOWN, NOT DROPPED.
    ///
    /// L106 proves an <c>EntityRef</c> payload rebuilds ONCE THE ENTITY IS THERE. It says nothing about
    /// WHEN, and the timing is not a race the transport could win: the host broadcasts 0xB7 synchronously
    /// from its <c>OpenModal</c> postfix, inside the same call stack that minted the object
    /// (HavenMissionUtil.cs:94 creates the GeoCharacter, :59 opens the modal), while the structural create
    /// for that object is a value-rail packet the diff cycle only emits on a LATER frame
    /// (<c>DiffEngine.HostTick</c>). So the raise is ALWAYS first, <c>IdentityResolver.Resolve</c> always
    /// returns null, and the pre-deferral code took L106's own refusal path — a soldier-join reward window
    /// the peer never saw, logged as a correct refusal. That is law 91: a peer left unable to answer.
    ///
    /// WHAT IS ASSERTED IS THE OUTCOME, EXECUTED on the REAL <see cref="ModalParkQueue"/> with a fake
    /// resolver — the class is pure precisely so this can be a run, not an inspection:
    ///   (a) an unresolved raise is PARKED, not shown and not dropped, for as long as its entity is absent;
    ///   (b) once the entity resolves, the parked raise is SHOWN — same seq, same payload. This is the law;
    ///   (c) FIFO: a later raise whose entity IS present must NOT jump ahead of a waiting earlier one, or
    ///       this peer's window order stops being the host's (equal priorities keep arrival order in
    ///       <c>GeoscapeViewSwitchQuery.QueryStateSwitch</c>:77-82);
    ///   (d) BOUNDED, LOUDLY: a raise whose entity never arrives EXPIRES after
    ///       <see cref="ModalParkQueue.MaxBatches"/> applied batches and reports it — never an unbounded
    ///       queue, never a silent swallow (this repo's dominant bug class); and an over-full queue evicts
    ///       with a stated reason rather than quietly;
    ///   (e) the seams, or every arm above is a pure class nobody calls: <c>HandleInbound</c> must PARK,
    ///       <c>PumpParked</c> must PUMP, <c>NeedsPark</c> must ask <c>IdentityResolver.Resolve</c> (the
    ///       peer's OWN graph, never the payload), and <c>GenericApplier.HandleInbound</c> — the one place
    ///       an applied batch can have created the entity — must drive the pump.
    /// ponytail: (e) scans raw IL for the callee's metadata token, same lenient trade as L105/L106.
    /// </summary>
    internal static class L107_ModalRaiseDeferral
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        private static GeoModalMirror.Raise Sample(string entityRef) => new GeoModalMirror.Raise
        {
            ModalType = (int)ModalType.FactionSoldierJoin,
            Shape = GeoModalMirror.DataShape.EntityRef,
            Ref = entityRef,
            Keys = new[] { typeof(PhoenixPoint.Geoscape.Entities.GeoCharacter).FullName },
            Priority = 99,
        };

        internal static IEnumerable<string> Check()
        {
            // ── (a) parked while absent, and (b) shown once it lands ───────
            var q = new ModalParkQueue();
            var shown = new List<string>();
            var expired = new List<string>();
            bool here = false;
            Action pump = () => q.Pump(p => here, (p, s) => shown.Add(p.Ref + "@" + s),
                                       (p, s, w) => expired.Add(p.Ref + "@" + s + "/" + w));

            if (q.Park(11u, Sample("U#7")) != null)
                yield return "L107 park-rejected: the FIRST parked raise was already evicted, so nothing can " +
                             "ever wait for its entity and the deferral is inert.";
            for (int i = 0; i < ModalParkQueue.MaxBatches - 1; i++) pump();
            if (shown.Count != 0 || expired.Count != 0 || q.Count != 1)
                yield return "L107 early-release: a raise whose entity is still ABSENT was released (shown=" +
                             shown.Count + " expired=" + expired.Count + " parked=" + q.Count + "). Raising it " +
                             "now hands UIStateGeoModal null data and the prefab's data-bind dereferences it " +
                             "unguarded inside EnterState — the half-built window over placeholder text.";

            here = true;
            pump();
            if (shown.Count != 1 || shown[0] != "U#7@11" || q.Count != 0)
                yield return "L107 late-entity-lost: the raise that arrived BEFORE its entity was not shown once " +
                             "the entity landed (shown=[" + string.Join(",", shown.ToArray()) + "] parked=" +
                             q.Count + "). This is the whole law: the host broadcasts 0xB7 from its OpenModal " +
                             "postfix while the structural create rides a LATER diff cycle, so every EntityRef " +
                             "window arrives early and dropping it is a reward the player never gets to answer " +
                             "(law 91).";
            if (expired.Count != 0)
                yield return "L107 expired-then-shown: the queue reported an EXPIRY for a raise it could still " +
                             "resolve — an expiry is a dropped window, so a spurious one is the loss this law " +
                             "exists to prevent, announced as if it were correct.";

            // ── (c) FIFO — a later ready raise may not overtake a waiter ───
            var fifo = new ModalParkQueue();
            var order = new List<string>();
            fifo.Park(20u, Sample("U#100"));   // never resolves
            fifo.Park(21u, Sample("U#7"));     // resolves immediately
            fifo.Pump(p => p.Ref == "U#7", (p, s) => order.Add(p.Ref), (p, s, w) => order.Add("X" + p.Ref));
            if (order.Count != 0)
                yield return "L107 order-jumped: the LATER raise (" + string.Join(",", order.ToArray()) + ") was " +
                             "released while an EARLIER one was still waiting. The game shows windows out of a " +
                             "priority queue that keeps ARRIVAL order between equal priorities, so this peer " +
                             "would see the host's windows in an order the host never showed.";

            // ── (d) bounded, and both exits speak ──────────────────────────
            var timeout = new ModalParkQueue();
            var lost = new List<int>();
            timeout.Park(30u, Sample("U#404"));
            for (int i = 0; i < ModalParkQueue.MaxBatches; i++)
                timeout.Pump(p => false, (p, s) => lost.Add(-1), (p, s, w) => lost.Add(w));
            if (lost.Count != 1 || lost[0] != ModalParkQueue.MaxBatches || timeout.Count != 0)
                yield return "L107 unbounded-park: a raise whose entity NEVER arrives did not expire loudly after " +
                             ModalParkQueue.MaxBatches + " batches (events=[" + string.Join(",", lost.ConvertAll(x => x.ToString()).ToArray()) +
                             "] parked=" + timeout.Count + "). An entry that waits forever is the silent swallow " +
                             "this repo dies of, and it holds every later window behind it.";

            var full = new ModalParkQueue();
            string evicted = null;
            for (int i = 0; i <= ModalParkQueue.MaxParked; i++) evicted = full.Park((uint)(40 + i), Sample("U#" + i));
            if (evicted == null || full.Count != ModalParkQueue.MaxParked)
                yield return "L107 queue-unbounded: parking " + (ModalParkQueue.MaxParked + 1) + " raises neither " +
                             "capped the queue (count=" + full.Count + ") nor named what it dropped. An unbounded " +
                             "queue of windows nobody can raise is the same swallow with a memory cost.";

            // ── (e) the seams: the queue must be the one that RUNS ─────────
            var mirror = typeof(GeoModalMirror);
            var inbound = mirror.GetMethod("HandleInbound", All);
            var pumpParked = mirror.GetMethod("PumpParked", All);
            var needsPark = mirror.GetMethod("NeedsPark", All);
            var parkM = typeof(ModalParkQueue).GetMethod("Park", All);
            var pumpM = typeof(ModalParkQueue).GetMethod("Pump", All);
            var resolve = typeof(IdentityResolver).GetMethod("Resolve", All);
            var applier = Type.GetType("Multiplayer.Network.Sync.GenericApplier, Multiplayer");
            var applierInbound = applier == null ? null : applier.GetMethod("HandleInbound", All);
            if (inbound == null || pumpParked == null || needsPark == null || parkM == null || pumpM == null ||
                resolve == null || applierInbound == null)
            {
                yield return "L107 seam-gone: GeoModalMirror.HandleInbound / PumpParked / NeedsPark, " +
                             "ModalParkQueue.Park / Pump, IdentityResolver.Resolve or GenericApplier.HandleInbound " +
                             "no longer exists — every arm above is testing a class that is wired to nothing.";
                yield break;
            }
            if (!References(inbound, parkM))
                yield return "L107 inbound-never-parks: GeoModalMirror.HandleInbound does not call " +
                             "ModalParkQueue.Park, so an early raise still takes the immediate refusal path and " +
                             "the queue proven above is never given anything to hold.";
            if (!References(pumpParked, pumpM))
                yield return "L107 pump-never-pumps: GeoModalMirror.PumpParked does not call ModalParkQueue.Pump — " +
                             "parked raises would neither be released nor ever expire, which is the unbounded " +
                             "silent queue in its worst form.";
            if (!References(needsPark, resolve))
                yield return "L107 park-trusts-the-wire: GeoModalMirror.NeedsPark does not call " +
                             "IdentityResolver.Resolve. Whether a raise must WAIT is a question about THIS peer's " +
                             "own mirrored graph; anything else is reading host state out of the payload.";
            if (!References(applierInbound, pumpParked))
                yield return "L107 pump-undriven: GenericApplier.HandleInbound does not call " +
                             "GeoModalMirror.PumpParked. An applied rail batch is the ONE moment this peer can " +
                             "have gained the entity a parked raise names (law 3: only a structural apply creates " +
                             "identity) — without that call every parked window waits for an event that never comes.";
        }

        /// <summary>Does <paramref name="m"/>'s IL mention <paramref name="callee"/>? Raw 4-byte metadata
        /// token scan — see the ponytail note on the class.</summary>
        private static bool References(MethodBase m, MethodBase callee)
        {
            byte[] il = null;
            try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null || callee == null) return false;
            int token = callee.MetadataToken;
            for (int i = 0; i + 4 <= il.Length; i++)
                if (BitConverter.ToInt32(il, i) == token) return true;
            return false;
        }
    }
}
