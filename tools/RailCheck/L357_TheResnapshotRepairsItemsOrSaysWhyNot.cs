using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;
using PhoenixPoint.Tactical.Entities.Equipments;

namespace RailCheck
{
    /// <summary>
    /// L357 — THE RESNAPSHOT EITHER REPAIRS A DAMAGED ITEM OR SAYS WHY IT COULD NOT.
    ///
    /// 0x84 is a stream of discrete events with no re-emit, so the resnapshot is the ONLY repair a lost hit
    /// has. Its item half was dead in both directions at once and nothing anywhere said so:
    ///
    ///   • THE WRITE named nothing. <c>HandleResnapRequest</c> wrote <c>it.GetSlotName()</c>, and
    ///     <c>TacticalItem.GetSlotName</c>:634-637 is a hardcoded <c>return "";</c> (see L356) — so every
    ///     damaged item was shipped under the empty slot name, which is the wire's word for THE WHOLE ACTOR.
    ///   • THE READ swallowed it. <c>ApplyResnap</c> answered a failed <c>ResolveItem</c> with a bare
    ///     <c>if (item == null) continue;</c> — no counter, no log line, nothing. So a resnapshot that repaired
    ///     not one weapon reported "N actor(s) reconciled" and looked healthy.
    ///
    /// Two silences that cover for each other is the shape this repo keeps paying for: the broken write was
    /// undetectable BECAUSE the read was mute. This law closes both ends — the address must be minted by the
    /// one rule (L356's <c>SlotOf</c>), and a resolve that fails must be audible.
    ///
    /// Falsify (each verified RED, then restored): write <c>it.GetSlotName()</c> again in the resnapshot body
    /// → (a); delete the counter and the log from the item branch of <c>ApplyResnap</c> → (b) mute-continue;
    /// make <c>TacticalDamageSync.ResolveItem</c> return null without setting <c>why</c> → (c).
    /// </summary>
    internal static class L357_TheResnapshotRepairsItemsOrSaysWhyNot
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var sync = typeof(TacticalDamageSync);
            var asm = sync.Assembly;
            var resnapped = sync.GetMethod("ApplyResnap", All);
            var resolveItem = sync.GetMethod("ResolveItem", All);
            var damageable = sync.GetMethod("DamageableItems", All);
            if (resnapped == null || resolveItem == null || damageable == null)
            {
                yield return "L357 premise-changed: TacticalDamageSync.ApplyResnap / ResolveItem / DamageableItems " +
                             "no longer all resolve. The resnapshot is the only repair a lost 0x84 record has and " +
                             "these three ARE its item half — without them every arm below passes vacuously while a " +
                             "weapon damaged on the host stays whole on every other screen forever.";
                yield break;
            }

            // ── (a) THE WRITE MINTS THE ADDRESS BY THE ONE RULE ──────────────────────
            // The body is handed to Send as a lambda, so the call lives in a compiler-generated closure — whose
            // method name still carries the enclosing method's, which is how it is found.
            var body = Closure(sync, "HandleResnapRequest").ToList();
            if (body.Count == 0)
                yield return "L357 resnap-body-gone: no method of TacticalDamageSync (closures included) belongs to " +
                             "HandleResnapRequest, so what the host actually writes into a resnapshot is unprovable " +
                             "from here.";
            else
            {
                // CalleeSequence for the second arm: TacticalItem.GetSlotName is in the GAME assembly, which
                // the one-assembly Callees walker cannot see.
                var calls = body.SelectMany(m => Program.CalleeSequence(m)).ToList();
                if (!calls.Any(c => c.Name == "SlotOf"))
                    yield return "L357 resnap-address-hand-rolled: the resnapshot body does not mint its item " +
                                 "addresses through TacticalActorKey.SlotOf. Anything else asks the item for its own " +
                                 "slot name — which is \"\" for every TacticalItem — and ships every damaged weapon " +
                                 "under the whole-actor address, where the reader resolves none of them. That is the " +
                                 "defect verbatim, and the read half below is what made it invisible.";
                if (calls.Any(c => c.Name == "GetSlotName" && c.DeclaringType == typeof(TacticalItem)))
                    yield return "L357 resnap-asks-the-item: the resnapshot body calls TacticalItem.GetSlotName() " +
                                 "directly. That method is `return \"\";` (TacticalItem.cs:634-637) — it is not a " +
                                 "worse address than SlotOf's, it is NO address, and every item record built on it " +
                                 "is discarded by the receiver.";
            }

            // ── (b) A FAILED RESOLVE IS AUDIBLE ──────────────────────────────────────
            if (!Program.Callees(resnapped, asm).Any(c => c.Name == "ResolveItem"))
                yield return "L357 resnap-applies-no-items: ApplyResnap never calls ResolveItem, so the recovery " +
                             "path reads the host's item health and does nothing with it.";
            var said = Program.StringRefs(resnapped).ToList();
            if (!said.Any(s => s.IndexOf("could not resolve", StringComparison.Ordinal) >= 0))
                yield return "L357 mute-continue: ApplyResnap carries no sentence about item records it could not " +
                             "resolve. That branch was a bare `continue` for the whole life of this arc — it is why " +
                             "a resnapshot repairing zero weapons still reported success, and it is the exact " +
                             "silent-swallow class this repo counts its bugs in.";
            if (!said.Any(s => s.IndexOf("unresolvable", StringComparison.Ordinal) >= 0))
                yield return "L357 summary-lost: ApplyResnap no longer reports what it could NOT reconcile. The " +
                             "summary line is the only thing that ever says a recovery came back half-empty.";

            // ── (c) EXECUTED: THE RESOLVER NEVER REFUSES WITHOUT A REASON ────────────
            var args = new object[] { null, "RANGED_ITEM_SLOT", L356_ACarriedItemKnowsWhichSlotItHangsIn.ItemGuid, null };
            object answer = null; string threw = null;
            try { answer = resolveItem.Invoke(null, args); }
            catch (Exception ex) { threw = (ex.InnerException ?? ex).GetType().Name; }
            if (threw != null)
                yield return "L357 resolver-throws: ResolveItem threw (" + threw + ") on an actor it cannot use, " +
                             "rather than refusing with a sentence. ApplyResnap calls it once per damaged item per " +
                             "actor inside one try — one throw abandons the whole recovery.";
            else if (answer != null)
                yield return "L357 resolver-answers-anyway: ResolveItem returned something for an actor that has no " +
                             "BodyState. A fallback here would land a damaged rifle's hit points on whatever it " +
                             "found instead, which is worse than not repairing it.";
            else if (string.IsNullOrEmpty(args[3] as string))
                yield return "L357 resolver-mute: ResolveItem refused and gave no reason, so ApplyResnap's new " +
                             "counter would report N unresolvable items and be unable to name one of them. A " +
                             "refusal nobody can read is the same swallow one level up.";
        }

        /// <summary>A method and every compiler-generated lambda it owns. A C# closure keeps the enclosing
        /// method's name inside its own (<c>&lt;HandleResnapRequest&gt;b__3</c>), whether it lands on a display
        /// class or on the cached <c>&lt;&gt;c</c> — so the name is the link, and it survives both shapes.</summary>
        internal static IEnumerable<MethodBase> Closure(Type owner, string method)
        {
            foreach (var t in new[] { owner }.Concat(owner.GetNestedTypes(All)))
                foreach (var m in t.GetMethods(All | BindingFlags.DeclaredOnly).Cast<MethodBase>())
                    if (m.Name == method || m.Name.IndexOf("<" + method + ">", StringComparison.Ordinal) >= 0)
                        yield return m;
        }
    }
}
