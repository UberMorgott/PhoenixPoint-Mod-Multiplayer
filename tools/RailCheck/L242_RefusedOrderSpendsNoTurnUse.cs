using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Abilities;

namespace RailCheck
{
    /// <summary>
    /// L242 — AN ORDER THE HOST REFUSED SPENDS NO PER-TURN USE.
    ///
    /// THE REPORT (2026-08-08, second symptom): after switching weapon to the pistol, overwatch was refused
    /// with "cannot be used again this turn" — for a soldier who had never successfully overwatched with
    /// anything.
    ///
    /// IT WAS THE GAME'S OWN RULE, not a mod lock, and the mod simply never replicated it.
    /// <c>TacticalAbility.Activate</c>:1092 calls <c>IncrementUsesThisTurn</c>, which feeds
    /// <c>TacticalActor._abilityUsesThisTurn</c>:113 — a <c>Dictionary&lt;TacticalAbilityDef,int&gt;</c>, so
    /// EVERY weapon's copy of Overwatch shares one entry — and it is cleared only at the turn edge
    /// (<c>TacticalActor</c>:1194). <c>UsesPerTurn</c> defaults to 1. A client that played an order
    /// speculatively spent the turn's use; the host, which refused BEFORE activating, kept 0. A weapon change
    /// cannot clear a def-keyed counter, by design — the player had no way out until the turn edge. Laws
    /// L145-L147 (the per-actor action lock) are NOT implicated: this counter is the game's, and there were
    /// zero references to it anywhere in <c>src/</c>.
    ///
    /// THE REPAIR IS THE SEAM THAT ALREADY EXISTS, for the fourth time. The host's counts ride the 0x82 settle
    /// beside the status set (L131), the ability-trait set (L137) and the selection (L186): the settle closes
    /// every rider AND sweeps every keyed live actor at every turn edge, so the authoritative value is
    /// RE-ASSERTED routinely and repairs the counter whatever leaked it — not only the reject case a targeted
    /// <c>ResetUsesThisTurn</c> could reach. The game itself persists the field
    /// (<c>TacActorInstanceData.AbilityUsesThisTurn</c>:30, written at <c>TacticalActor</c>:693, restored at
    /// :606), which is this repo's own test for what belongs on the settle.
    ///
    /// WHAT THIS LAW ASSERTS.
    ///   (a) EXECUTED — <c>HostUsesFor</c>, the shipped pure lookup: an ABSENT entry means ZERO, not "leave it
    ///       alone". That single bit IS the repair, because the spurious use is precisely an entry the host
    ///       does not have; an apply that only wrote what it was sent would never clear one. Both polarities,
    ///       so the arm is its own control.
    ///   (b) the game-side premise, read from the real assembly: the counter is DEF-keyed (which is why no
    ///       weapon change can clear it) and its three public writers still exist.
    ///   (c) it crosses and it lands — <c>HostSettle</c> collects it, <c>PendingSettle.Uses</c> carries it,
    ///       <c>ApplySettle</c> reconciles it, and the reconcile drives the count through the game's own
    ///       writers rather than poking the private dictionary.
    ///   (d) the reconcile walks the ACTOR'S abilities, not the wire dictionary's keys — the only shape in
    ///       which "absent means zero" can actually fire.
    ///   (e) reactivity: a repaired counter repaints. <c>CanUseThisTurn</c> is what greys the ability bar and
    ///       nothing native repaints it for a model change this peer did not click.
    ///   (f) POSITIVE CONTROL, EXECUTED — <see cref="FakeSeam"/> treats an absent entry as "skip" and settles
    ///       without the field; (a), (c), (d) and (e) must all go red on it.
    ///
    /// Falsify: make <c>HostUsesFor</c> return -1 (or any skip sentinel) for an absent guid → (a); drop
    /// <c>WriteUses</c> from <c>HostSettle</c> or <c>ReconcileAbilityUses</c> from <c>ApplySettle</c> → (c);
    /// iterate <c>host.Keys</c> instead of the actor's abilities → (d); drop the <c>MarkDirty</c> → (e).
    /// </summary>
    internal static class L242_RefusedOrderSpendsNoTurnUse
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check()
        {
            var sync = typeof(TacticalCommandSync);
            var hostSettle = sync.GetMethod("HostSettle", AllMembers);
            var collect = sync.GetMethod("CollectAbilityUses", AllMembers);
            var applySettle = sync.GetMethod("ApplySettle", AllMembers);
            var reconcile = sync.GetMethod("ReconcileAbilityUses", AllMembers);
            var pending = sync.GetNestedType("PendingSettle", AllMembers);
            if (hostSettle == null || collect == null || applySettle == null || reconcile == null ||
                pending == null || pending.GetField("Uses", AllMembers) == null)
            {
                yield return "L242 premise-changed: one of TacticalCommandSync.{HostSettle, CollectAbilityUses, " +
                             "ApplySettle, ReconcileAbilityUses} or PendingSettle.Uses no longer resolves. The " +
                             "per-turn use counter is the field this arc moved onto the settle — re-read this " +
                             "law before assuming a use spent on a refused order still clears itself.";
                yield break;
            }

            // ── (b) THE GAME-SIDE PREMISE ────────────────────────────────────
            var instance = typeof(TacActorInstanceData).GetField("AbilityUsesThisTurn",
                               BindingFlags.Public | BindingFlags.Instance);
            if (instance == null || !instance.FieldType.IsGenericType ||
                instance.FieldType.GetGenericArguments().FirstOrDefault() != typeof(TacticalAbilityDef))
                yield return "L242 counter-premise-gone: TacActorInstanceData.AbilityUsesThisTurn is no longer a " +
                             "dictionary keyed by TacticalAbilityDef. DEF-keyed is the whole reason a weapon " +
                             "change cannot clear a spent use — if the key has become per-instance, this law's " +
                             "diagnosis no longer describes the build and the settle field may be redundant.";
            foreach (var name in new[] { "GetAbilityUsesThisTurn", "IncrementAbilityUsesThisTurn",
                                         "ResetAbilityUsesThisTurn" })
                if (typeof(TacticalActor).GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null,
                                                    new[] { typeof(TacticalAbility) }, null) == null)
                    yield return "L242 counter-premise-gone: TacticalActor." + name + "(TacticalAbility) no " +
                                 "longer exists. The reconcile drives this counter through the game's own public " +
                                 "writers on purpose; without them it would have to poke the private dictionary " +
                                 "by reflection, which no law could then keep honest.";
            if (typeof(TacticalAbility).GetProperty("CanUseThisTurn", BindingFlags.Public | BindingFlags.Instance) == null)
                yield return "L242 counter-premise-gone: TacticalAbility.CanUseThisTurn no longer exists. That is " +
                             "the getter the ability bar greys on (TacticalAbility:245-256) and the property the " +
                             "player experienced as \"cannot be used again this turn\".";

            // ── (a) ABSENT MEANS ZERO, EXECUTED ──────────────────────────────
            foreach (var v in ScanLookup(TacticalCommandSync.HostUsesFor, "HostUsesFor")) yield return v;

            // ── (c) IT CROSSES AND IT LANDS ──────────────────────────────────
            foreach (var v in ScanCross(hostSettle, applySettle, reconcile, "the settle")) yield return v;

            // ── (d) + (e) THE RECONCILE'S SHAPE ──────────────────────────────
            foreach (var v in ScanReconcile(reconcile, "ReconcileAbilityUses")) yield return v;

            // ── (f) POSITIVE CONTROL, EXECUTED ───────────────────────────────
            var fake = typeof(FakeSeam);
            var control = ScanLookup(FakeSeam.Lookup, "FakeSeam.Lookup")
                .Concat(ScanCross(fake.GetMethod("Settle", AllMembers), fake.GetMethod("Apply", AllMembers),
                                  fake.GetMethod("Reconcile", AllMembers), "FakeSeam"))
                .Concat(ScanReconcile(fake.GetMethod("Reconcile", AllMembers), "FakeSeam.Reconcile"))
                .ToList();
            foreach (var want in new[] { "absent-means-skip", "settle-carries-no-uses", "settle-applies-no-uses",
                                         "reconcile-follows-the-wire", "repaired-uses-are-invisible" })
                if (!control.Any(c => c.Contains(want)))
                    yield return "L242 control-not-red: FakeSeam commits " + want + " and the scan did not flag " +
                                 "it. That arm cannot tell a repaired counter from one that stays spent for the " +
                                 "rest of the turn.";
        }

        /// <summary>Arm (a) — the one bit that makes the repair possible, run in both polarities.</summary>
        private static IEnumerable<string> ScanLookup(Func<Dictionary<string, int>, string, int> lookup, string label)
        {
            const string guid = "overwatch-def-guid";
            if (lookup(new Dictionary<string, int>(StringComparer.Ordinal), guid) != 0)
                yield return "L242 absent-means-skip: " + label + " does not answer ZERO for a guid the host did " +
                             "not send. The spurious use a refused order left behind is PRECISELY an entry the " +
                             "host does not have, so an apply built on this would never clear one — the ability " +
                             "stays dead on every weapon until the turn edge, which is the report verbatim.";
            if (lookup(null, guid) != 0)
                yield return "L242 absent-means-skip: " + label + " does not answer ZERO for a missing dictionary. " +
                             "A settle from before the field existed, or one whose codec read nothing, would " +
                             "then write a sentinel into a live counter.";
            var one = new Dictionary<string, int>(StringComparer.Ordinal) { { guid, 2 } };
            if (lookup(one, guid) != 2)
                yield return "L242 absent-means-skip: " + label + " does not return the host's own count for an " +
                             "entry it WAS sent. Absent-means-zero is only safe while present-means-the-number; " +
                             "an ability with UsesPerTurn > 1 would otherwise be over-restored.";
        }

        /// <summary>Arm (c) — the host reads it, the apply reconciles it.</summary>
        private static IEnumerable<string> ScanCross(MethodBase hostSettle, MethodBase applySettle,
                                                     MethodBase reconcile, string label)
        {
            var mod = typeof(TacticalCommandSync).Assembly;
            var game = typeof(TacticalActor).Assembly;
            if (hostSettle == null || !Program.Callees(hostSettle, mod).Any(c => c.Name == "CollectAbilityUses"))
                yield return "L242 settle-carries-no-uses: " + label + "'s host half no longer collects the " +
                             "per-turn counts on the way out, so the field rides nothing. The counter is back to " +
                             "being replicated by NOTHING at all — zero references, which is how it got here.";
            if (applySettle == null || !Program.Callees(applySettle, mod).Any(c => c.Name == "ReconcileAbilityUses"))
                yield return "L242 settle-applies-no-uses: " + label + "'s apply half no longer reconciles the " +
                             "counts, so the host ships an answer nothing acts on and the turn-edge sweep " +
                             "re-asserts everything about an actor except what he has already spent.";
            if (reconcile != null &&
                !Program.Callees(reconcile, game).Any(c => c.Name == "ResetAbilityUsesThisTurn"))
                yield return "L242 settle-applies-no-uses: " + label + "'s reconcile never calls " +
                             "ResetAbilityUsesThisTurn. Increment alone can only raise a count, so a client that " +
                             "spent MORE than the host — the entire refused-order case — can never be brought " +
                             "back down.";
        }

        /// <summary>Arms (d) and (e) — walk the actor, and repaint.</summary>
        private static IEnumerable<string> ScanReconcile(MethodBase reconcile, string label)
        {
            if (reconcile == null)
            {
                yield return "L242 reconcile-follows-the-wire: " + label + " does not resolve.";
                yield break;
            }
            var game = typeof(TacticalActor).Assembly;
            if (!Program.Callees(reconcile, game).Any(c => c.Name == "GetAbilities" ||
                                                           c.Name == "GetAbilitiesFiltered"))
                yield return "L242 reconcile-follows-the-wire: " + label + " does not enumerate the ACTOR'S " +
                             "abilities. Iterating the wire dictionary's keys instead makes absent mean " +
                             "\"untouched\" no matter what HostUsesFor answers, so arm (a) would be green over a " +
                             "counter that is never cleared.";
            if (!Program.Callees(reconcile, typeof(TacticalCommandSync).Assembly).Any(c => c.Name == "MarkDirty"))
                yield return "L242 repaired-uses-are-invisible: " + label + " changes the model and never marks " +
                             "the tactical UI dirty. CanUseThisTurn is what greys the ability bar, and nothing " +
                             "native repaints it for a model change this peer did not click — the player would " +
                             "still see a dead Overwatch button over a counter that is already back to zero, " +
                             "which is postulate 1's defect, not a cosmetic one.";
        }

        /// <summary>THE BROKEN SHAPE, COMPILED: an absent entry is skipped, the field never rides, the
        /// reconcile follows the wire and repaints nothing.</summary>
        private sealed class FakeSeam
        {
            internal static int Lookup(Dictionary<string, int> host, string guid)
            {
                int n;
                return host != null && host.TryGetValue(guid, out n) ? n : -1;
            }

            internal static void Settle(TacticalActor actor) { }

            internal static void Apply(TacticalActor actor) { }

            internal static void Reconcile(TacticalActor actor, Dictionary<string, int> host)
            {
                foreach (var kv in host) actor.IncrementAbilityUsesThisTurn(null);
            }
        }
    }
}
