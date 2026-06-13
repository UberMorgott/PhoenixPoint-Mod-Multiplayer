using Multipleer.Network.CommandSync;
using Xunit;

// Task 7 (INC-3a) — pure host diff+seq core for the 0x35 GeoStateDiff mirror.
// The differ holds last-sent + per-identity seq and emits a record whose ChangedMask flags ONLY the
// fields that moved (epsilon on continuous pos/rot/range, exact on discrete travelling/site/dest/hp),
// with a monotonic per-(FactionGuid,VehicleID) Seq. mask==0 => emit nothing (no packet, no seq burn).
public class GeoVehicleStateDifferTests
{
    private static readonly int FullMask =
        GeoStateMask.SurfacePos | GeoStateMask.SurfaceRot | GeoStateMask.RangeRemaining
        | GeoStateMask.Travelling | GeoStateMask.CurrentSite | GeoStateMask.DestinationSites
        | GeoStateMask.HitPoints;

    private static GeoVehicleStateRecord Veh(string guid, int id)
    {
        return new GeoVehicleStateRecord
        {
            FactionGuid = guid,
            VehicleID = id,
            PosX = 1f, PosY = 2f, PosZ = 3f,
            RotX = 0f, RotY = 0f, RotZ = 0f, RotW = 1f,
            RangeRemaining = 1000f,
            Travelling = false,
            CurrentSiteId = 5,
            DestinationSiteIds = new[] { 5 },
            HitPoints = 100f
        };
    }

    // (a) first record for an identity → full changedMask, seq=1
    [Fact]
    public void FirstRecord_GetsFullMask_AndSeqOne()
    {
        var d = new GeoVehicleStateDiffer();
        var r = d.Diff(Veh("NJ", 7));
        Assert.Equal(FullMask, r.ChangedMask);
        Assert.Equal(1UL, r.Seq);
    }

    // (b) unchanged re-submit → mask==0 (emit nothing)
    [Fact]
    public void UnchangedResubmit_GivesZeroMask()
    {
        var d = new GeoVehicleStateDiffer();
        d.Diff(Veh("NJ", 7));            // first (consumes seq 1)
        var r = d.Diff(Veh("NJ", 7));    // identical snapshot
        Assert.Equal(0, r.ChangedMask);
    }

    // (b'/e) an unchanged re-submit must NOT burn a seq number (no packet sent).
    [Fact]
    public void UnchangedResubmit_DoesNotIncrementSeq()
    {
        var d = new GeoVehicleStateDiffer();
        d.Diff(Veh("NJ", 7));            // seq 1
        d.Diff(Veh("NJ", 7));            // mask 0 -> no seq burn
        var moved = Veh("NJ", 7);
        moved.PosX = 50f;                 // now a real change
        var r = d.Diff(moved);
        Assert.Equal(2UL, r.Seq);        // still the SECOND sent packet, not 3rd
    }

    // (c) pos moved beyond epsilon → bit0 set + seq increments
    [Fact]
    public void PosMovedBeyondEpsilon_SetsSurfacePosBit_AndIncrementsSeq()
    {
        var d = new GeoVehicleStateDiffer();
        d.Diff(Veh("NJ", 7));            // seq 1
        var moved = Veh("NJ", 7);
        moved.PosX = 1f + 1.0f;          // well beyond epsilon
        var r = d.Diff(moved);
        Assert.Equal(GeoStateMask.SurfacePos, r.ChangedMask & GeoStateMask.SurfacePos);
        Assert.Equal(2UL, r.Seq);
    }

    // (c) pos moved within epsilon → bit0 NOT set (sub-epsilon float churn ignored)
    [Fact]
    public void PosMovedWithinEpsilon_DoesNotSetSurfacePosBit()
    {
        var d = new GeoVehicleStateDiffer();
        d.Diff(Veh("NJ", 7));            // seq 1
        var nudged = Veh("NJ", 7);
        nudged.PosX = 1f + (GeoVehicleStateDiffer.Epsilon / 2f); // below threshold
        var r = d.Diff(nudged);
        Assert.Equal(0, r.ChangedMask & GeoStateMask.SurfacePos);
    }

    // (c') sub-epsilon drift is compared against the LAST SENT value, not last seen, so accumulated
    //      drift eventually crosses the threshold and fires.
    [Fact]
    public void SubEpsilonDrift_Accumulates_AgainstLastSent()
    {
        var d = new GeoVehicleStateDiffer();
        d.Diff(Veh("NJ", 7));            // last sent PosX = 1f
        var step = GeoVehicleStateDiffer.Epsilon * 0.6f; // each step below epsilon individually
        var s1 = Veh("NJ", 7); s1.PosX = 1f + step;
        Assert.Equal(0, d.Diff(s1).ChangedMask & GeoStateMask.SurfacePos); // still 0 vs last sent
        var s2 = Veh("NJ", 7); s2.PosX = 1f + 2f * step;                    // now > epsilon vs last sent
        Assert.Equal(GeoStateMask.SurfacePos, d.Diff(s2).ChangedMask & GeoStateMask.SurfacePos);
    }

    // (d) Travelling flip → bit3, exact (no epsilon)
    [Fact]
    public void TravellingFlip_SetsTravellingBit_Exact()
    {
        var d = new GeoVehicleStateDiffer();
        d.Diff(Veh("NJ", 7));
        var t = Veh("NJ", 7);
        t.Travelling = true;
        var r = d.Diff(t);
        Assert.Equal(GeoStateMask.Travelling, r.ChangedMask & GeoStateMask.Travelling);
    }

    // (d) CurrentSite change → bit4, exact
    [Fact]
    public void CurrentSiteChange_SetsCurrentSiteBit_Exact()
    {
        var d = new GeoVehicleStateDiffer();
        d.Diff(Veh("NJ", 7));            // CurrentSiteId = 5
        var s = Veh("NJ", 7);
        s.CurrentSiteId = 6;
        var r = d.Diff(s);
        Assert.Equal(GeoStateMask.CurrentSite, r.ChangedMask & GeoStateMask.CurrentSite);
    }

    // (d) DestinationSites change (ordered, exact) → bit5
    [Fact]
    public void DestinationSitesChange_SetsDestinationBit_Exact()
    {
        var d = new GeoVehicleStateDiffer();
        d.Diff(Veh("NJ", 7));            // dest = {5}
        var s = Veh("NJ", 7);
        s.DestinationSiteIds = new[] { 5, 9 };
        var r = d.Diff(s);
        Assert.Equal(GeoStateMask.DestinationSites, r.ChangedMask & GeoStateMask.DestinationSites);
    }

    // (d) DestinationSites unchanged but reordered counts as a change (ordered comparison).
    [Fact]
    public void DestinationSitesReorder_IsAChange()
    {
        var d = new GeoVehicleStateDiffer();
        var a = Veh("NJ", 7); a.DestinationSiteIds = new[] { 1, 2 };
        d.Diff(a);
        var b = Veh("NJ", 7); b.DestinationSiteIds = new[] { 2, 1 };
        var r = d.Diff(b);
        Assert.Equal(GeoStateMask.DestinationSites, r.ChangedMask & GeoStateMask.DestinationSites);
    }

    // (d) DestinationSites null vs empty are treated equal (no false change).
    [Fact]
    public void DestinationSites_NullVsEmpty_NotAChange()
    {
        var d = new GeoVehicleStateDiffer();
        var a = Veh("NJ", 7); a.DestinationSiteIds = null;
        d.Diff(a);
        var b = Veh("NJ", 7); b.DestinationSiteIds = new int[0];
        var r = d.Diff(b);
        Assert.Equal(0, r.ChangedMask & GeoStateMask.DestinationSites);
    }

    // (e) per-(factionGuid,VehicleID) seq independent + monotonic
    [Fact]
    public void Seq_IsIndependentPerIdentity_AndMonotonic()
    {
        var d = new GeoVehicleStateDiffer();
        // Two distinct identities (different faction, different id).
        Assert.Equal(1UL, d.Diff(Veh("NJ", 7)).Seq);
        Assert.Equal(1UL, d.Diff(Veh("PHX", 7)).Seq); // same id, different faction -> own seq line
        Assert.Equal(1UL, d.Diff(Veh("NJ", 8)).Seq);  // same faction, different id -> own seq line

        var m1 = Veh("NJ", 7); m1.PosX = 100f;
        Assert.Equal(2UL, d.Diff(m1).Seq);             // NJ#7 advances independently
        var m2 = Veh("PHX", 7); m2.PosX = 100f;
        Assert.Equal(2UL, d.Diff(m2).Seq);             // PHX#7 advances independently
    }

    // (e) collision proof: same VehicleID under different factions must NOT share a seq line.
    [Fact]
    public void SameVehicleId_DifferentFaction_DoesNotCollide()
    {
        var d = new GeoVehicleStateDiffer();
        d.Diff(Veh("A", 1));             // A#1 seq 1
        d.Diff(Veh("B", 1));             // B#1 seq 1 (independent)
        var mA = Veh("A", 1); mA.PosX = 99f;
        Assert.Equal(2UL, d.Diff(mA).Seq); // A#1 -> 2, unaffected by B#1
    }

    // (f) classify CONTINUOUS bits (pos/rot/range) vs DISCRETE bits (travelling/site/dest/hp) for channel split.
    [Fact]
    public void ChannelSplit_PartitionsMask_IntoContinuousAndDiscrete()
    {
        int mask = FullMask;
        int cont = GeoVehicleStateDiffer.ContinuousBits(mask);
        int disc = GeoVehicleStateDiffer.DiscreteBits(mask);

        // Continuous = pos|rot|range only.
        Assert.Equal(GeoStateMask.SurfacePos | GeoStateMask.SurfaceRot | GeoStateMask.RangeRemaining, cont);
        // Discrete = travelling|currentsite|dest|hp only.
        Assert.Equal(
            GeoStateMask.Travelling | GeoStateMask.CurrentSite | GeoStateMask.DestinationSites | GeoStateMask.HitPoints,
            disc);
        // Disjoint and complete.
        Assert.Equal(0, cont & disc);
        Assert.Equal(mask, cont | disc);
    }

    // (f) split on a partial mask carries only the relevant bits into each channel.
    [Fact]
    public void ChannelSplit_OnPartialMask_KeepsOnlyRelevantBits()
    {
        int mask = GeoStateMask.SurfacePos | GeoStateMask.Travelling; // one cont, one disc
        Assert.Equal(GeoStateMask.SurfacePos, GeoVehicleStateDiffer.ContinuousBits(mask));
        Assert.Equal(GeoStateMask.Travelling, GeoVehicleStateDiffer.DiscreteBits(mask));
    }

    // HitPoints classified DISCRETE (bit6), exact compare.
    [Fact]
    public void HitPointsChange_SetsHitPointsBit_Exact_AndIsDiscrete()
    {
        var d = new GeoVehicleStateDiffer();
        d.Diff(Veh("NJ", 7));            // HitPoints = 100
        var s = Veh("NJ", 7);
        s.HitPoints = 90f;
        var r = d.Diff(s);
        Assert.Equal(GeoStateMask.HitPoints, r.ChangedMask & GeoStateMask.HitPoints);
        Assert.Equal(GeoStateMask.HitPoints, GeoVehicleStateDiffer.DiscreteBits(r.ChangedMask) & GeoStateMask.HitPoints);
    }
}
