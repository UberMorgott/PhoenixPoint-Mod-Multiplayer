using System.Collections.Generic;
using Multiplayer.Harmony.Tactical;
using Multiplayer.Sync.Tactical;
using Xunit;

/// <summary>
/// PURE seam tests for the TS3 ground-surface / volume mirror (surface <c>tac.surface</c> 0x94). Covers:
///   (a) the <see cref="TacticalSurfaceCodec"/> wire round-trips (seq/pins + multi-op spawn+remove + cell lists)
///       and truncation / garbage / corrupt-count → clean drop (no partial accept),
///   (b) the pure host COALESCE+GROUP build (<see cref="TacticalSurfaceCodec.CoalesceAndGroup"/>): per-cell
///       last-write-wins dedup, group by voxel type, op derivation (Empty → remove, else spawn), and the
///       per-op cell cap split,
///   (c) the pure CLIENT inert-suppress decision (<see cref="ClientSurfaceInertGate.ShouldSuppress"/>) — the
///       "no double damage / no double status" contract (skip native surface-gameplay iff client mirror).
/// The engine glue (TacticalSurfaceSync / SurfaceEffectPatches — the SetVoxelType funnel + native replay + the
/// live Harmony guards) binds game types and is in-game verified.
/// </summary>
public class TacticalSurfaceCodecTests
{
    private static TacticalSurfaceCodec.SurfaceOp Op(byte op, byte type, params (float, float, float)[] cells)
    {
        var list = new List<TacticalSurfaceCodec.SurfaceCell>();
        foreach (var (x, y, z) in cells) list.Add(new TacticalSurfaceCodec.SurfaceCell(x, y, z));
        return new TacticalSurfaceCodec.SurfaceOp(op, type, list);
    }

    // ─── (a) codec round-trip ──────────────────────────────────────────
    [Fact]
    public void RoundTrips_MultiOp_SpawnAndRemove_WithCells()
    {
        var batch = new TacticalSurfaceCodec.SurfaceBatch(77u, new List<TacticalSurfaceCodec.SurfaceOp>
        {
            Op(TacticalSurfaceCodec.OpSpawn, TacticalSurfaceCodec.VoxelFire, (1.5f, 0f, 2.5f), (1.5f, 0f, 3.5f)),
            Op(TacticalSurfaceCodec.OpSpawn, TacticalSurfaceCodec.VoxelMist, (-4f, 2.4f, 8f)),
            Op(TacticalSurfaceCodec.OpRemove, TacticalSurfaceCodec.VoxelEmpty, (1.5f, 0f, 2.5f)),
        });

        var bytes = TacticalSurfaceCodec.EncodeSurface(batch);
        Assert.True(TacticalSurfaceCodec.TryDecodeSurface(bytes, out var d));

        Assert.Equal(77u, d.Seq);                 // pin: seq survives
        Assert.Equal(3, d.Ops.Count);

        Assert.Equal(TacticalSurfaceCodec.OpSpawn, d.Ops[0].Op);
        Assert.Equal(TacticalSurfaceCodec.VoxelFire, d.Ops[0].VoxelType);
        Assert.Equal(2, d.Ops[0].Cells.Count);
        Assert.Equal(1.5f, d.Ops[0].Cells[0].X);
        Assert.Equal(0f, d.Ops[0].Cells[0].Y);
        Assert.Equal(2.5f, d.Ops[0].Cells[0].Z);
        Assert.Equal(3.5f, d.Ops[0].Cells[1].Z);

        Assert.Equal(TacticalSurfaceCodec.VoxelMist, d.Ops[1].VoxelType);
        Assert.Equal(-4f, d.Ops[1].Cells[0].X);
        Assert.Equal(2.4f, d.Ops[1].Cells[0].Y);

        Assert.Equal(TacticalSurfaceCodec.OpRemove, d.Ops[2].Op);
        Assert.Equal(TacticalSurfaceCodec.VoxelEmpty, d.Ops[2].VoxelType);
        Assert.Single(d.Ops[2].Cells);
    }

    [Fact]
    public void RoundTrips_EmptyBatch_NoOps()
    {
        var bytes = TacticalSurfaceCodec.EncodeSurface(new TacticalSurfaceCodec.SurfaceBatch(1u, null));
        Assert.Equal(6, bytes.Length);            // exactly u32 seq + u16 opCount, no tail
        Assert.True(TacticalSurfaceCodec.TryDecodeSurface(bytes, out var d));
        Assert.Equal(1u, d.Seq);
        Assert.Empty(d.Ops);
    }

    [Fact]
    public void RoundTrips_Op_WithZeroCells()
    {
        var batch = new TacticalSurfaceCodec.SurfaceBatch(9u, new List<TacticalSurfaceCodec.SurfaceOp>
        {
            Op(TacticalSurfaceCodec.OpRemove, TacticalSurfaceCodec.VoxelEmpty),   // op present, no cells
        });
        var bytes = TacticalSurfaceCodec.EncodeSurface(batch);
        Assert.True(TacticalSurfaceCodec.TryDecodeSurface(bytes, out var d));
        Assert.Single(d.Ops);
        Assert.Empty(d.Ops[0].Cells);
    }

    [Fact]
    public void Rejects_Null_Truncated_AndGarbage()
    {
        Assert.False(TacticalSurfaceCodec.TryDecodeSurface(null, out _));
        Assert.False(TacticalSurfaceCodec.TryDecodeSurface(new byte[5], out _));   // shorter than the 6-byte header

        // A valid frame chopped mid-cell → clean reject (cellCount says more bytes than remain).
        var bytes = TacticalSurfaceCodec.EncodeSurface(new TacticalSurfaceCodec.SurfaceBatch(3u,
            new List<TacticalSurfaceCodec.SurfaceOp> { Op(TacticalSurfaceCodec.OpSpawn, TacticalSurfaceCodec.VoxelGoo, (1f, 2f, 3f), (4f, 5f, 6f)) }));
        var chopped = new byte[bytes.Length - 5];
        System.Array.Copy(bytes, chopped, chopped.Length);
        Assert.False(TacticalSurfaceCodec.TryDecodeSurface(chopped, out _));
    }

    [Fact]
    public void Rejects_CorruptCellCount()
    {
        // header (seq=1, opCount=1) then op=spawn, type=fire, a bogus huge cellCount with no cell data → guarded reject.
        using (var ms = new System.IO.MemoryStream())
        using (var w = new System.IO.BinaryWriter(ms))
        {
            w.Write(1u);                                   // seq
            w.Write((ushort)1);                            // opCount
            w.Write(TacticalSurfaceCodec.OpSpawn);         // op
            w.Write(TacticalSurfaceCodec.VoxelFire);       // voxelType
            w.Write((ushort)ushort.MaxValue);              // cellCount far exceeds the remaining buffer
            Assert.False(TacticalSurfaceCodec.TryDecodeSurface(ms.ToArray(), out _));
        }
    }

    // ─── (b) pure coalesce + group ─────────────────────────────────────
    [Fact]
    public void CoalesceAndGroup_GroupsByType_DerivesOp()
    {
        var caps = new List<TacticalSurfaceCodec.CapturedCell>
        {
            new TacticalSurfaceCodec.CapturedCell(0, 0, 0, 0f, 0f, 0f, TacticalSurfaceCodec.VoxelFire),
            new TacticalSurfaceCodec.CapturedCell(1, 0, 0, 1f, 0f, 0f, TacticalSurfaceCodec.VoxelFire),
            new TacticalSurfaceCodec.CapturedCell(2, 0, 0, 2f, 0f, 0f, TacticalSurfaceCodec.VoxelMist),
            new TacticalSurfaceCodec.CapturedCell(3, 0, 0, 3f, 0f, 0f, TacticalSurfaceCodec.VoxelEmpty),
        };
        var ops = TacticalSurfaceCodec.CoalesceAndGroup(caps, TacticalSurfaceCodec.MaxCellsPerOp);

        Assert.Equal(3, ops.Count);   // one op per distinct type (Fire, Mist, Empty)
        var fire = ops.Find(o => o.VoxelType == TacticalSurfaceCodec.VoxelFire);
        var mist = ops.Find(o => o.VoxelType == TacticalSurfaceCodec.VoxelMist);
        var empty = ops.Find(o => o.VoxelType == TacticalSurfaceCodec.VoxelEmpty);
        Assert.NotNull(fire); Assert.NotNull(mist); Assert.NotNull(empty);
        Assert.Equal(TacticalSurfaceCodec.OpSpawn, fire.Op);    // non-empty type → spawn
        Assert.Equal(TacticalSurfaceCodec.OpSpawn, mist.Op);
        Assert.Equal(TacticalSurfaceCodec.OpRemove, empty.Op);  // Empty → remove
        Assert.Equal(2, fire.Cells.Count);
        Assert.Single(mist.Cells);
        Assert.Single(empty.Cells);
    }

    [Fact]
    public void CoalesceAndGroup_LastWriteWins_PerCell()
    {
        // The SAME cell (key 5,5,5) flickers Fire → Empty within one flush window → only its NET final type (Empty)
        // ships, as a single remove cell. No stray Fire spawn for that cell.
        var caps = new List<TacticalSurfaceCodec.CapturedCell>
        {
            new TacticalSurfaceCodec.CapturedCell(5, 5, 5, 5f, 5f, 5f, TacticalSurfaceCodec.VoxelFire),
            new TacticalSurfaceCodec.CapturedCell(5, 5, 5, 5f, 5f, 5f, TacticalSurfaceCodec.VoxelEmpty),
        };
        var ops = TacticalSurfaceCodec.CoalesceAndGroup(caps, TacticalSurfaceCodec.MaxCellsPerOp);
        Assert.Single(ops);
        Assert.Equal(TacticalSurfaceCodec.OpRemove, ops[0].Op);
        Assert.Equal(TacticalSurfaceCodec.VoxelEmpty, ops[0].VoxelType);
        Assert.Single(ops[0].Cells);
    }

    [Fact]
    public void CoalesceAndGroup_SplitsAtCellCap()
    {
        var caps = new List<TacticalSurfaceCodec.CapturedCell>();
        for (int i = 0; i < 5; i++)
            caps.Add(new TacticalSurfaceCodec.CapturedCell(i, 0, 0, i, 0f, 0f, TacticalSurfaceCodec.VoxelFire));

        var ops = TacticalSurfaceCodec.CoalesceAndGroup(caps, maxCellsPerOp: 2);
        Assert.Equal(3, ops.Count);                 // 2 + 2 + 1
        Assert.All(ops, o => Assert.Equal(TacticalSurfaceCodec.VoxelFire, o.VoxelType));
        int total = 0; foreach (var o in ops) total += o.Cells.Count;
        Assert.Equal(5, total);
    }

    [Fact]
    public void CoalesceAndGroup_EmptyInput_NoOps()
    {
        Assert.Empty(TacticalSurfaceCodec.CoalesceAndGroup(null, 8));
        Assert.Empty(TacticalSurfaceCodec.CoalesceAndGroup(new List<TacticalSurfaceCodec.CapturedCell>(), 8));
    }

    // ─── (c) pure inert-suppress decision (no double damage/status) ─────
    [Fact]
    public void ShouldSuppress_OnlyOnClientMirror()
    {
        Assert.True(ClientSurfaceInertGate.ShouldSuppress(isClientMirroring: true));    // client mirror → skip native gameplay
        Assert.False(ClientSurfaceInertGate.ShouldSuppress(isClientMirroring: false));  // host / single-player → run native
    }
}
