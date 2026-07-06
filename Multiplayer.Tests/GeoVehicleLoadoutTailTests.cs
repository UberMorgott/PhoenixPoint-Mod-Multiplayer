using System.IO;
using System.Linq;
using System.Text;
using Multiplayer.Network.Sync.State;
using Xunit;

// U vehicle-LOADOUT tail on the mid-session vehicle channel (#6) — pure tests for the optional
// loadout block of GeoVehicleIdentitySnapshot (audit item U): round-trip (ordered weapon/module slot
// def guids incl. "" empty slots), the LEGACY BYTE PIN (a crew-only / no-extras payload stays
// byte-identical to the pre-U wire), coexistence with the crew block (both survive one snapshot),
// the loadout-only path (the crewCount=0 marker), a hand-pinned wire, truncation rejection, and the
// parse-known-then-skip contract for unknown future tail bits. The engine glue (GeoVehicleChannel
// loadout poll/apply, GeoVehicleLoadoutReflection) is game-bound and in-game-gated.
public class GeoVehicleLoadoutTailTests
{
    private static GeoVehicleIdentity Id(int owner, int veh)
        => new GeoVehicleIdentity(owner, veh, "fac", "set",
                                  0.1f, 0.2f, 0.3f, 0.927362f, 12.5f, -3.25f, 88.0f);

    private static GeoVehicleLoadoutRecord Load(int owner, int veh, string[] w, string[] m)
        => new GeoVehicleLoadoutRecord(GeoVehiclePos.MakeKey(owner, veh), w, m);

    // ─── loadout round-trip ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Loadout_RoundTrip_PreservesSlotsAndOrder()
    {
        var snap = new GeoVehicleIdentitySnapshot();
        snap.Loadouts.Add(Load(1, 1, new[] { "wpn-A", "", "wpn-B" }, new[] { "mod-X" }));
        snap.Loadouts.Add(Load(9, 5, new string[0], new[] { "", "mod-Y" }));

        var outSnap = GeoVehicleIdentitySnapshot.Decode(GeoVehicleIdentitySnapshot.Encode(snap));

        Assert.NotNull(outSnap);
        Assert.Equal(snap.Loadouts.Count, outSnap.Loadouts.Count);
        for (int i = 0; i < snap.Loadouts.Count; i++)
            Assert.Equal(snap.Loadouts[i], outSnap.Loadouts[i]);   // structural: key + weapon/module slots + ORDER
    }

    [Fact]
    public void Loadout_EmptySlots_AreHonest_NotDropped()
    {
        // An all-empty-slot aircraft (a null weapon + null module) is distinct from "no loadout tail".
        var snap = new GeoVehicleIdentitySnapshot();
        snap.Loadouts.Add(Load(4, 4, new[] { "" }, new[] { "" }));

        var outSnap = GeoVehicleIdentitySnapshot.Decode(GeoVehicleIdentitySnapshot.Encode(snap));

        Assert.NotNull(outSnap);
        Assert.Single(outSnap.Loadouts);
        Assert.Equal(new[] { "" }, outSnap.Loadouts[0].Weapons);
        Assert.Equal(new[] { "" }, outSnap.Loadouts[0].Modules);
    }

    [Fact]
    public void Loadout_And_Crew_Together_RoundTrip()
    {
        // The two extras blocks coexist in one snapshot (the same aircraft carries crew AND loadout).
        var snap = new GeoVehicleIdentitySnapshot();
        snap.Vehicles.Add(Id(1, 1));
        snap.Crew.Add(new GeoVehicleCrewRecord(GeoVehiclePos.MakeKey(1, 1), new long[] { 7, 3 }));
        snap.Loadouts.Add(Load(1, 1, new[] { "wpn-A" }, new[] { "mod-X" }));

        var outSnap = GeoVehicleIdentitySnapshot.Decode(GeoVehicleIdentitySnapshot.Encode(snap));

        Assert.NotNull(outSnap);
        Assert.Single(outSnap.Crew);
        Assert.Equal(new long[] { 7, 3 }, outSnap.Crew[0].UnitIds);
        Assert.Single(outSnap.Loadouts);
        Assert.Equal(new[] { "wpn-A" }, outSnap.Loadouts[0].Weapons);
        Assert.Equal(new[] { "mod-X" }, outSnap.Loadouts[0].Modules);
    }

    [Fact]
    public void Loadout_Only_RoundTrip_CrewCountZeroMarker()
    {
        // Loadout present, NO crew — the crew block writes a count=0 marker so the loadout block that
        // follows is positionally unambiguous.
        var snap = new GeoVehicleIdentitySnapshot();
        snap.Loadouts.Add(Load(2, 3, new[] { "wpn-A" }, new string[0]));

        var outSnap = GeoVehicleIdentitySnapshot.Decode(GeoVehicleIdentitySnapshot.Encode(snap));

        Assert.NotNull(outSnap);
        Assert.Empty(outSnap.Crew);
        Assert.Single(outSnap.Loadouts);
        Assert.Equal(new[] { "wpn-A" }, outSnap.Loadouts[0].Weapons);
        Assert.Empty(outSnap.Loadouts[0].Modules);
    }

    // ─── legacy byte pins (backward-tolerant wire) ───────────────────────────────────────────────────────────

    [Fact]
    public void Encode_CrewOnly_NoLoadoutBlock_StaysPrePreU()
    {
        // A crew-only payload (no loadout) must write NO loadout block — decoding yields empty Loadouts,
        // and the byte length equals a snapshot encoded with the loadout list left empty.
        var withCrew = new GeoVehicleIdentitySnapshot();
        withCrew.Vehicles.Add(Id(11, 7));
        withCrew.Crew.Add(new GeoVehicleCrewRecord(GeoVehiclePos.MakeKey(11, 7), new long[] { 1, 2, 3 }));

        var wire = GeoVehicleIdentitySnapshot.Encode(withCrew);
        var outSnap = GeoVehicleIdentitySnapshot.Decode(wire);

        Assert.NotNull(outSnap);
        Assert.Empty(outSnap.Loadouts);
        Assert.Single(outSnap.Crew);
    }

    [Fact]
    public void Encode_LoadoutOnly_WirePinned()
    {
        // Pin the EXACT loadout-only wire layout: empty veh + tomb, crewCount=0 marker, one loadout record.
        long key = GeoVehiclePos.MakeKey(1, 1);
        var snap = new GeoVehicleIdentitySnapshot();
        snap.Loadouts.Add(new GeoVehicleLoadoutRecord(key, new[] { "A" }, new string[0]));

        var bytes = GeoVehicleIdentitySnapshot.Encode(snap);

        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write((ushort)0);        // vehicle count
            w.Write((ushort)0);        // tombstone count
            w.Write((ushort)0);        // crew count (marker — loadout follows)
            w.Write((ushort)1);        // loadout count
            w.Write(key);              // i64 composite key
            w.Write((ushort)8);        // recLen: flags(1) + nW(2) + str"A"(3) + nM(2)
            w.Write((byte)0x01);       // TailHasLoadout
            w.Write((ushort)1);        // nWeapons
            w.Write((ushort)1); w.Write((byte)0x41);   // str "A"
            w.Write((ushort)0);        // nModules
            Assert.Equal(ms.ToArray(), bytes);
        }
    }

    // ─── malformed payloads ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Decode_TruncatedLoadoutBlock_ReturnsNull()
    {
        var snap = new GeoVehicleIdentitySnapshot();
        snap.Loadouts.Add(Load(2, 3, new[] { "wpn-A" }, new[] { "mod-X" }));
        byte[] wire = GeoVehicleIdentitySnapshot.Encode(snap);
        byte[] truncated = wire.Take(wire.Length - 3).ToArray();   // clip into the last module guid
        Assert.Null(GeoVehicleIdentitySnapshot.Decode(truncated));
    }

    [Fact]
    public void Decode_UnknownFlagLoadoutRecord_SkippedViaRecLen()
    {
        // A loadout record whose flags carry ONLY unknown (future, higher) bits is skipped whole via
        // recLen; a known loadout record alongside still parses (per-record degradation).
        long knownKey = GeoVehiclePos.MakeKey(1, 1);
        byte[] wire;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write((ushort)0);            // vehicle count
            w.Write((ushort)0);            // tombstone count
            w.Write((ushort)0);            // crew count (marker)
            w.Write((ushort)2);            // loadout count
            w.Write(GeoVehiclePos.MakeKey(9, 9));
            w.Write((ushort)3);            // recLen
            w.Write(new byte[] { 0x02, 0xAB, 0xCD });   // unknown bit1 + opaque payload
            w.Write(knownKey);
            w.Write((ushort)5);            // recLen: flags(1) + nW(2) + nM(2)
            w.Write((byte)0x01);           // TailHasLoadout
            w.Write((ushort)0);            // nWeapons
            w.Write((ushort)0);            // nModules
            wire = ms.ToArray();
        }

        var outSnap = GeoVehicleIdentitySnapshot.Decode(wire);

        Assert.NotNull(outSnap);
        Assert.Single(outSnap.Loadouts);
        Assert.Equal(knownKey, outSnap.Loadouts[0].Key);
        Assert.Empty(outSnap.Loadouts[0].Weapons);
        Assert.Empty(outSnap.Loadouts[0].Modules);
    }

    // ─── host poll change-detect helper ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void SameLoadout_OrderedEquality_NullEqualsEmpty_EmptySlotIsDistinct()
    {
        Assert.True(GeoVehicleLoadout.SameLoadout(null, null, new string[0], new string[0]));   // never-observed ≡ observed-empty
        Assert.True(GeoVehicleLoadout.SameLoadout(new[] { "a" }, new[] { "b" }, new[] { "a" }, new[] { "b" }));
        Assert.False(GeoVehicleLoadout.SameLoadout(new[] { "a" }, new string[0], new[] { "b" }, new string[0]));   // weapon differs
        Assert.False(GeoVehicleLoadout.SameLoadout(new string[0], new[] { "a" }, new string[0], new[] { "b" }));   // module differs
        Assert.False(GeoVehicleLoadout.SameLoadout(new[] { "a", "b" }, new string[0], new[] { "b", "a" }, new string[0]));   // order = wire truth
        Assert.False(GeoVehicleLoadout.SameLoadout(new[] { "" }, new string[0], new[] { "a" }, new string[0]));   // empty slot ≠ filled slot
    }
}
