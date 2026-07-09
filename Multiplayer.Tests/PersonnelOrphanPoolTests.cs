using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Network.Sync.State;
using Xunit;

// Orphan pool for the #9 site ↔ #6 vehicle-crew cross-channel race (RCA 2026-07-09): a reconcile
// that removes a soldier no other container claims PARKS the instance (RosterReconcile now reports
// RemovedInstances); the next reconcile that references its id resolves it via the pool merge and
// ADOPTS it (entry dropped); unknown ids never adopt; session/reload reset drops everything and
// reports the never-reclaimed ids for the glue's one-shot log. The game glue (PersonnelReflection
// park/merge/adopt seams) is game-bound; these lock the pure contracts. NOTE: PersonnelOrphanPool
// is static shared state — all its tests live in THIS class only (xUnit runs a class sequentially),
// and every test starts from Reset().
public class PersonnelOrphanPoolTests
{
    private sealed class Soldier
    {
        public readonly long Id;
        public Soldier(long id) { Id = id; }
        public override string ToString() => "S" + Id;
    }

    // Mimics the PersonnelReflection.ReconcileInto glue: resolve container-first then pool
    // (the BuildCharacterIndex merge), park removed instances, evict adopted ids.
    private sealed class World
    {
        public readonly Dictionary<long, Soldier> ById = new Dictionary<long, Soldier>();
        public readonly List<IList> Containers = new List<IList>();

        public Soldier Add(long id, IList container)
        {
            var s = new Soldier(id);
            ById[id] = s;
            container?.Add(s);
            return s;
        }

        private object Resolve(long id)
        {
            foreach (var c in Containers)
                foreach (var o in c)
                    if (o is Soldier s && s.Id == id) return s;
            // The pool merge: an id no live container claims resolves to its parked instance.
            foreach (var kv in PersonnelOrphanPool.SnapshotEntries())
                if (kv.Key == id) return kv.Value;
            return null;
        }

        private IList ContainerOf(object soldier)
        {
            foreach (var c in Containers)
                if (c.Contains(soldier)) return c;
            return null;
        }

        public RosterReconcile.Outcome Reconcile(IList target, params long[] ids)
        {
            var outcome = RosterReconcile.Apply(target, ids, Resolve, ContainerOf);
            foreach (var inst in outcome.RemovedInstances)
                PersonnelOrphanPool.Park(((Soldier)inst).Id, inst);   // park on unclaimed remove
            foreach (var id in ids)
                PersonnelOrphanPool.Evict(id);                        // adopt on (re-)add
            return outcome;
        }
    }

    // ─── RosterReconcile.RemovedInstances (the park feed) ───────────────────────────────────────────────────

    [Fact]
    public void Reconcile_ReportsRemovedInstances()
    {
        PersonnelOrphanPool.Reset();
        var w = new World();
        var site = new List<object>(); w.Containers.Add(site);
        var a = w.Add(1, site);
        var b = w.Add(2, site);

        var outcome = w.Reconcile(site, 1);

        Assert.Equal(new object[] { b }, outcome.RemovedInstances.ToArray());
        Assert.Equal(new object[] { a }, site.Cast<object>().ToArray());
    }

    [Fact]
    public void Reconcile_SameApplyTransfer_NotReportedAsRemoved()
    {
        // Remove-from-old-before-add: a same-apply transfer must never park (it was MOVED, not removed).
        PersonnelOrphanPool.Reset();
        var w = new World();
        var site = new List<object>(); w.Containers.Add(site);
        var craft = new List<object>(); w.Containers.Add(craft);
        var x = w.Add(7, site);

        var outcome = w.Reconcile(craft, 7);

        Assert.Empty(outcome.RemovedInstances);
        Assert.Equal(0, PersonnelOrphanPool.Count);
        Assert.Equal(new object[] { x }, craft.Cast<object>().ToArray());
    }

    // ─── park → adopt (the cross-channel race scenario) ─────────────────────────────────────────────────────

    [Fact]
    public void CrossChannelTransfer_ParkedThenAdoptedByLaterCrewApply()
    {
        // THE bug scenario: #9 site apply removes the soldier first; the #6 crew apply lands later
        // with a fresh index that only sees live containers — the pool bridges the gap.
        PersonnelOrphanPool.Reset();
        var w = new World();
        var site = new List<object>(); w.Containers.Add(site);
        var craft = new List<object>(); w.Containers.Add(craft);
        var x = w.Add(11, site);

        w.Reconcile(site /* site roster no longer lists 11 */);
        Assert.Empty(site);
        Assert.Equal(1, PersonnelOrphanPool.Count);   // parked, not lost

        w.Reconcile(craft, 11);                       // the later #6 crew apply references him
        Assert.Equal(new object[] { x }, craft.Cast<object>().ToArray());
        Assert.Equal(0, PersonnelOrphanPool.Count);   // adopted → evicted
    }

    [Fact]
    public void ParkedSoldier_NotReferencedAgain_StaysParkedNotResurrected()
    {
        // Dismiss/death: the host's rosters never reference the id again — the parked instance must
        // never re-enter any container on its own (drops only at the session reset).
        PersonnelOrphanPool.Reset();
        var w = new World();
        var site = new List<object>(); w.Containers.Add(site);
        var other = new List<object>(); w.Containers.Add(other);
        w.Add(5, site);
        var keep = w.Add(6, other);

        w.Reconcile(site /* 5 dismissed */);
        w.Reconcile(other, 6);   // unrelated later apply

        Assert.Empty(site);
        Assert.Equal(new object[] { keep }, other.Cast<object>().ToArray());
        Assert.Equal(1, PersonnelOrphanPool.Count);   // still parked, nowhere resurrected
    }

    [Fact]
    public void UnknownId_NoAdopt_ReportedUnresolved()
    {
        PersonnelOrphanPool.Reset();
        var w = new World();
        var site = new List<object>(); w.Containers.Add(site);
        var a = w.Add(1, site);

        var outcome = w.Reconcile(site, 1, 999);      // 999 neither live nor pooled

        Assert.Equal(new List<long> { 999 }, outcome.Unresolved);
        Assert.Equal(new object[] { a }, site.Cast<object>().ToArray());
        Assert.Equal(0, PersonnelOrphanPool.Count);
    }

    // ─── pool primitive semantics ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Park_IgnoresIdZeroAndNullInstance()
    {
        PersonnelOrphanPool.Reset();
        PersonnelOrphanPool.Park(0, new Soldier(0));
        PersonnelOrphanPool.Park(3, null);
        Assert.Equal(0, PersonnelOrphanPool.Count);
    }

    [Fact]
    public void Evict_UnknownId_ReturnsFalse()
    {
        PersonnelOrphanPool.Reset();
        Assert.False(PersonnelOrphanPool.Evict(42));
    }

    [Fact]
    public void Reset_ClearsAndReportsDroppedIds()
    {
        PersonnelOrphanPool.Reset();
        PersonnelOrphanPool.Park(7, new Soldier(7));
        PersonnelOrphanPool.Park(8, new Soldier(8));

        var dropped = PersonnelOrphanPool.Reset();

        Assert.Equal(new[] { 7L, 8L }, dropped.OrderBy(i => i).ToArray());
        Assert.Equal(0, PersonnelOrphanPool.Count);
        Assert.Empty(PersonnelOrphanPool.Reset());   // idempotent
    }
}
