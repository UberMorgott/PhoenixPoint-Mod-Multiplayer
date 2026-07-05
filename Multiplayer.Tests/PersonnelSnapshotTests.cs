using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Multiplayer.Network.Sync.State;
using Xunit;

// PS1 personnel channel (#9) — pure tests for the wire codec (PersonnelSnapshot: per-site ordered
// GeoUnitId membership + the recLen-skipped PS2 live-state block) and the value-only membership
// reconcile core (RosterReconcile: add / remove / reorder / transfer / swap / unresolvable-id skip).
// The engine glue (PersonnelChannel, PersonnelReflection, membership Harmony seams) is game-bound and
// in-game-gated; these lock the pure contracts per the 2026-07-05 personnel-sync spec §2.2/§4.
public class PersonnelSnapshotTests
{
    // ─── wire codec ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_PreservesSiteIdsAndOrderedUnits()
    {
        var snap = new PersonnelSnapshot();
        snap.Sites.Add(new PersonnelSiteRoster(101, new long[] { 7, 3, 42 }));
        snap.Sites.Add(new PersonnelSiteRoster(-5, new long[] { 1000000007L }));

        var outSnap = PersonnelSnapshot.Decode(PersonnelSnapshot.Encode(snap));

        Assert.NotNull(outSnap);
        Assert.Equal(snap.Sites.Count, outSnap.Sites.Count);
        for (int i = 0; i < snap.Sites.Count; i++)
            Assert.Equal(snap.Sites[i], outSnap.Sites[i]);   // structural equality: id + ids + ORDER
    }

    [Fact]
    public void RoundTrip_EmptyRoster_IsHonestNotSkipped()
    {
        // Empty list = "site holds no soldiers" (honest, not a tombstone-skip): must survive the wire.
        var snap = new PersonnelSnapshot();
        snap.Sites.Add(new PersonnelSiteRoster(9, new long[0]));

        var outSnap = PersonnelSnapshot.Decode(PersonnelSnapshot.Encode(snap));

        Assert.NotNull(outSnap);
        Assert.Single(outSnap.Sites);
        Assert.Equal(9, outSnap.Sites[0].SiteId);
        Assert.Empty(outSnap.Sites[0].UnitIds);
    }

    [Fact]
    public void RoundTrip_EmptySnapshot()
    {
        var outSnap = PersonnelSnapshot.Decode(PersonnelSnapshot.Encode(new PersonnelSnapshot()));
        Assert.NotNull(outSnap);
        Assert.Empty(outSnap.Sites);
    }

    [Fact]
    public void Decode_SkipsUnknownStateRecords_ByRecLen()
    {
        // PS2 forward-compat: a state record with unknown tail bits is skipped whole via recLen
        // (parse-known-then-skip) — the membership block still decodes.
        byte[] wire;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write((ushort)1);           // siteCount
            w.Write(5);                   // siteId
            w.Write((ushort)1);           // nUnits
            w.Write(42L);                 // GeoUnitId
            w.Write((ushort)1);           // stateCount (a future PS2 record)
            w.Write(42L);                 // state GeoUnitId
            w.Write((ushort)4);           // recLen
            w.Write(new byte[] { 0x80, 1, 2, 3 });   // unknown high tail bit + payload
            wire = ms.ToArray();
        }

        var outSnap = PersonnelSnapshot.Decode(wire);

        Assert.NotNull(outSnap);
        Assert.Single(outSnap.Sites);
        Assert.Equal(new long[] { 42 }, outSnap.Sites[0].UnitIds);
    }

    [Fact]
    public void Decode_TruncatedStateRecord_ReturnsNull()
    {
        // recLen promises more bytes than remain → all-or-nothing reject (null), never garbage.
        byte[] wire;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write((ushort)0);           // siteCount
            w.Write((ushort)1);           // stateCount
            w.Write(42L);
            w.Write((ushort)10);          // recLen = 10 …
            w.Write(new byte[] { 1, 2 }); // … but only 2 bytes present
            wire = ms.ToArray();
        }
        Assert.Null(PersonnelSnapshot.Decode(wire));
    }

    [Fact]
    public void Decode_TruncatedSiteBlock_ReturnsNull()
    {
        var snap = new PersonnelSnapshot();
        snap.Sites.Add(new PersonnelSiteRoster(5, new long[] { 1, 2, 3 }));
        byte[] wire = PersonnelSnapshot.Encode(snap);
        byte[] truncated = wire.Take(wire.Length - 6).ToArray();   // clip into the id array + state block
        Assert.Null(PersonnelSnapshot.Decode(truncated));
    }

    [Fact]
    public void Decode_Garbage_ReturnsNull()
    {
        // Counts far beyond the byte budget force an EndOfStream inside → rejected, not garbage.
        Assert.Null(PersonnelSnapshot.Decode(new byte[] { 0xFF, 0xFF, 0xAA }));
    }

    [Fact]
    public void Decode_Null_ReturnsNull() => Assert.Null(PersonnelSnapshot.Decode(null));

    // ─── PS2 live-state block (whole-GeoCharacter blob tail, bit0) ──────────────────────────────────────────

    [Fact]
    public void RoundTrip_StateRecords_PreserveIdAndBlobBytes()
    {
        var snap = new PersonnelSnapshot();
        snap.Sites.Add(new PersonnelSiteRoster(3, new long[] { 7 }));
        snap.States.Add(new PersonnelSoldierState(7, new byte[] { 1, 2, 3, 4, 5 }));
        snap.States.Add(new PersonnelSoldierState(9, new byte[0] ));   // zero-length blob is framable
        snap.States.Add(new PersonnelSoldierState(11, new byte[] { 0xFF }));

        var outSnap = PersonnelSnapshot.Decode(PersonnelSnapshot.Encode(snap));

        Assert.NotNull(outSnap);
        Assert.Single(outSnap.Sites);
        Assert.Equal(3, outSnap.States.Count);
        Assert.Equal(7, outSnap.States[0].UnitId);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, outSnap.States[0].Blob);
        Assert.Equal(9, outSnap.States[1].UnitId);
        Assert.Empty(outSnap.States[1].Blob);
        Assert.Equal(11, outSnap.States[2].UnitId);
        Assert.Equal(new byte[] { 0xFF }, outSnap.States[2].Blob);
    }

    [Fact]
    public void RoundTrip_StateOnlyFlush_EmptySitesIsHonest()
    {
        // PS2-only flush: siteCount=0, states present (spec §2.2 no-tail invariant, both blocks fixed).
        var snap = new PersonnelSnapshot();
        snap.States.Add(new PersonnelSoldierState(42, new byte[] { 9, 9 }));

        var outSnap = PersonnelSnapshot.Decode(PersonnelSnapshot.Encode(snap));

        Assert.NotNull(outSnap);
        Assert.Empty(outSnap.Sites);
        Assert.Single(outSnap.States);
        Assert.Equal(42, outSnap.States[0].UnitId);
    }

    [Fact]
    public void Encode_NoStates_EndsWithZeroStateCount_LegacyPin()
    {
        // PS1-shape payload stays byte-stable: a membership-only flush still trails [u16 stateCount=0],
        // exactly what the pre-PS2 encoder wrote (legacy decoders keep working).
        var snap = new PersonnelSnapshot();
        snap.Sites.Add(new PersonnelSiteRoster(1, new long[] { 5 }));
        byte[] wire = PersonnelSnapshot.Encode(snap);
        Assert.Equal(0, wire[wire.Length - 2]);
        Assert.Equal(0, wire[wire.Length - 1]);
    }

    [Fact]
    public void Decode_KnownBlobPlusUnknownHigherBits_ParsesBlobSkipsExtras()
    {
        // Forward compat: bit0 blob + a future bit1 payload appended after it. The decoder takes the
        // known blob and recLen-skips the extra bytes (parse-known-then-skip contract).
        byte[] wire;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write((ushort)0);           // siteCount
            w.Write((ushort)1);           // stateCount
            w.Write(7L);                  // GeoUnitId
            w.Write((ushort)(1 + 4 + 3 + 2));   // recLen: flags + blobLen + blob(3) + future payload(2)
            w.Write((byte)0x03);          // bit0 (blob) + unknown bit1
            w.Write(3);                   // blobLen
            w.Write(new byte[] { 10, 20, 30 });
            w.Write(new byte[] { 0xAA, 0xBB });   // future bit1 payload — must be skipped
            wire = ms.ToArray();
        }

        var outSnap = PersonnelSnapshot.Decode(wire);

        Assert.NotNull(outSnap);
        Assert.Single(outSnap.States);
        Assert.Equal(7, outSnap.States[0].UnitId);
        Assert.Equal(new byte[] { 10, 20, 30 }, outSnap.States[0].Blob);
    }

    [Fact]
    public void Decode_BlobLenExceedsRecord_ReturnsNull()
    {
        // A KNOWN payload that lies about its length is corruption → all-or-nothing reject.
        byte[] wire;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write((ushort)0);           // siteCount
            w.Write((ushort)1);           // stateCount
            w.Write(7L);
            w.Write((ushort)8);           // recLen = flags + blobLen + 3 blob bytes
            w.Write((byte)0x01);
            w.Write(20);                  // blobLen 20 > recLen budget
            w.Write(new byte[] { 1, 2, 3 });
            wire = ms.ToArray();
        }
        Assert.Null(PersonnelSnapshot.Decode(wire));
    }

    [Fact]
    public void Encode_OversizeBlob_DroppedNotCorrupted()
    {
        // recLen is u16: a blob that cannot be framed is dropped by the encoder (defense-in-depth —
        // the flush core's MaxBlobWireBytes gate rejects it long before). The rest still encodes.
        var snap = new PersonnelSnapshot();
        snap.Sites.Add(new PersonnelSiteRoster(1, new long[] { 5 }));
        snap.States.Add(new PersonnelSoldierState(5, new byte[70000]));
        snap.States.Add(new PersonnelSoldierState(6, new byte[] { 1 }));

        var outSnap = PersonnelSnapshot.Decode(PersonnelSnapshot.Encode(snap));

        Assert.NotNull(outSnap);
        Assert.Single(outSnap.Sites);
        Assert.Single(outSnap.States);
        Assert.Equal(6, outSnap.States[0].UnitId);
    }

    // ─── RosterReconcile (value-only membership reconcile core) ─────────────────────────────────────────────

    private sealed class Soldier
    {
        public readonly long Id;
        public Soldier(long id) { Id = id; }
        public override string ToString() => "S" + Id;
    }

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

        public object Resolve(long id) => ById.TryGetValue(id, out var s) ? s : null;

        public IList ContainerOf(object soldier)
        {
            foreach (var c in Containers)
                if (c.Contains(soldier)) return c;
            return null;
        }
    }

    private static RosterReconcile.Outcome Reconcile(World w, IList target, params long[] ids)
        => RosterReconcile.Apply(target, ids, w.Resolve, w.ContainerOf);

    [Fact]
    public void Reconcile_AddsMissingMembersInOrder()
    {
        var w = new World();
        var site = new List<object>(); w.Containers.Add(site);
        var a = w.Add(1, null);
        var b = w.Add(2, null);

        var outcome = Reconcile(w, site, 1, 2);

        Assert.True(outcome.Changed);
        Assert.Equal(2, outcome.Added);
        Assert.Equal(new object[] { a, b }, site.Cast<object>().ToArray());
    }

    [Fact]
    public void Reconcile_RemovesAbsentMembers()
    {
        var w = new World();
        var site = new List<object>(); w.Containers.Add(site);
        var a = w.Add(1, site);
        w.Add(2, site);

        var outcome = Reconcile(w, site, 1);

        Assert.True(outcome.Changed);
        Assert.Equal(1, outcome.Removed);
        Assert.Equal(new object[] { a }, site.Cast<object>().ToArray());
    }

    [Fact]
    public void Reconcile_ReordersToMirroredOrder()
    {
        var w = new World();
        var site = new List<object>(); w.Containers.Add(site);
        var a = w.Add(1, site);
        var b = w.Add(2, site);
        var c = w.Add(3, site);

        var outcome = Reconcile(w, site, 3, 1, 2);

        Assert.True(outcome.Changed);
        Assert.True(outcome.Reordered);
        Assert.Equal(0, outcome.Added);
        Assert.Equal(0, outcome.Removed);
        Assert.Equal(new object[] { c, a, b }, site.Cast<object>().ToArray());
    }

    [Fact]
    public void Reconcile_Transfer_RemovesFromOldBeforeAdd()
    {
        // The PS1 risk pin: a transferred soldier must NEVER sit in two containers.
        var w = new World();
        var baseRoster = new List<object>(); w.Containers.Add(baseRoster);
        var craft = new List<object>(); w.Containers.Add(craft);
        var x = w.Add(7, baseRoster);

        var outcome = Reconcile(w, craft, 7);

        Assert.True(outcome.Changed);
        Assert.DoesNotContain(x, baseRoster);
        Assert.Equal(new object[] { x }, craft.Cast<object>().ToArray());
    }

    [Fact]
    public void Reconcile_SwapBetweenContainers_EndsWithExactlyOneHomeEach()
    {
        var w = new World();
        var craftA = new List<object>(); w.Containers.Add(craftA);
        var craftB = new List<object>(); w.Containers.Add(craftB);
        var x = w.Add(1, craftA);
        var y = w.Add(2, craftB);

        Reconcile(w, craftA, 2);   // host truth: A holds y …
        Reconcile(w, craftB, 1);   // … and B holds x

        Assert.Equal(new object[] { y }, craftA.Cast<object>().ToArray());
        Assert.Equal(new object[] { x }, craftB.Cast<object>().ToArray());
    }

    [Fact]
    public void Reconcile_UnresolvableId_SkippedAndReported_NeverThrows()
    {
        // Degrade-to-notify: a soldier this client never had is reported + skipped; the rest applies.
        var w = new World();
        var site = new List<object>(); w.Containers.Add(site);
        var a = w.Add(1, site);

        var outcome = Reconcile(w, site, 1, 999);

        Assert.Equal(new List<long> { 999 }, outcome.Unresolved);
        Assert.Equal(new object[] { a }, site.Cast<object>().ToArray());
        Assert.False(outcome.Changed);   // resolved part already matched → idempotent no-op
    }

    [Fact]
    public void Reconcile_AlreadyMatching_IsIdempotentNoOp()
    {
        var w = new World();
        var site = new List<object>(); w.Containers.Add(site);
        w.Add(1, site);
        w.Add(2, site);

        var outcome = Reconcile(w, site, 1, 2);

        Assert.False(outcome.Changed);
        Assert.Equal(0, outcome.Added);
        Assert.Equal(0, outcome.Removed);
    }

    [Fact]
    public void Reconcile_EmptyMirroredSet_ClearsContainer()
    {
        var w = new World();
        var site = new List<object>(); w.Containers.Add(site);
        w.Add(1, site);

        var outcome = Reconcile(w, site /* no ids */);

        Assert.True(outcome.Changed);
        Assert.Equal(1, outcome.Removed);
        Assert.Empty(site);
    }
}
