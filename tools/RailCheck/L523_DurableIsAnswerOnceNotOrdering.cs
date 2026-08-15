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
    ///   (a) host-order-key-survives — no type in the assembly exposes a HostOrderKey member, and the
    ///       HostOrderKey TYPE itself is gone, so the key cannot come back under a property named
    ///       something else (it rode as InboxMessage.Order and DeploymentTransitionDelta.Order).
    ///   (b) durable-priority-head-survives — WindowOrder exposes no DurablePriorityHead / BindDurable /
    ///       TryGetDurable, and WindowQueueSync exposes no TryDurablePriorityPreemption. STRENGTHENED
    ///       past the plan text: the request→occurrence binding was MOVED to WindowQueueSync rather than
    ///       deleted, because answer-once needs it (arm (d)), so a name check alone could be satisfied by
    ///       relocation. Arm (b2) therefore asserts the ORDERING class has no durable SURFACE at all —
    ///       no field type, parameter type or return type of WindowOrder mentions the durable subsystem.
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

            var model = asm.GetTypes().FirstOrDefault(t => t.Name == "DurableInboxModel" ||
                                                           t.Name == "InboxEntry");
            if (model == null)
            {
                yield return "L523 premise-changed: neither DurableInboxModel nor InboxEntry resolved. If " +
                             "durability was deleted outright rather than reduced to answer-once, that is a " +
                             "different change and this law must be re-pointed before its verdict means " +
                             "anything.";
                yield break;
            }

            var orderKeyHolders = asm.GetTypes()
                .SelectMany(t => t.GetFields(Any).Select(f => t.Name + "." + f.Name)
                                  .Concat(t.GetProperties(Any).Select(p => t.Name + "." + p.Name)))
                .Where(n => n.EndsWith(".HostOrderKey", StringComparison.Ordinal))
                .Concat(asm.GetTypes().Where(t => t.Name == "HostOrderKey").Select(t => "type " + t.Name))
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

            // (b2) THE ORDERING CLASS HAS NO DURABLE SURFACE. Names alone are evadable by relocation, and
            // the binding WAS relocated (to WindowQueueSync, beside the answer-once carrier bookkeeping it
            // serves). What must never come back is WindowOrder — the class that decides what is in front —
            // handling a durable value at all.
            var durableSurface = order.GetFields(Any).Select(f => f.Name + " : " + TypeNames(f.FieldType))
                .Concat(order.GetMethods(Any).Select(m => m.Name + " : " +
                    TypeNames(m.ReturnType) + "(" + string.Join(",", m.GetParameters()
                        .Select(p => TypeNames(p.ParameterType)).ToArray()) + ")"))
                .Where(IsDurable).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (durableSurface.Count > 0)
                yield return "L523 order-touches-durable: WindowOrder still has a durable surface (" +
                             string.Join("; ", durableSurface) + "). The ordering class must not know an " +
                             "occurrence exists — that is how the ledger became a second sorter.";

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

        private static string TypeNames(Type t)
        {
            if (t == null) return "?";
            var name = t.Name;
            if (t.IsGenericType)
                name += "<" + string.Join(",", t.GetGenericArguments().Select(TypeNames).ToArray()) + ">";
            if (t.IsByRef || t.IsArray) name = name.TrimEnd('&', '[', ']');
            return name;
        }

        private static bool IsDurable(string surface) =>
            surface.IndexOf("Durable", StringComparison.Ordinal) >= 0 ||
            surface.IndexOf("OccurrenceId", StringComparison.Ordinal) >= 0 ||
            surface.IndexOf("Inbox", StringComparison.Ordinal) >= 0 ||
            surface.IndexOf("HostOrderKey", StringComparison.Ordinal) >= 0;
    }
}
