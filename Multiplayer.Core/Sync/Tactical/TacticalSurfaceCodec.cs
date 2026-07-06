using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// PURE (engine-free) wire codec for the GROUND-SURFACE / VOLUME mirror surface <c>tac.surface</c>
    /// (0x94, host→all — spec TS3). Mirrors the ground hazard voxels the frozen client cannot see today —
    /// fire / goo / acid / MIST volumes (on-ACTOR fire/goo already ride the 0x8F status delta; TS3 owns the
    /// GROUND volume). Establishes the voxel-effect-mirror pattern TS6 reuses.
    ///
    /// CAPTURE MODEL — the single native funnel. Every fire/goo/mist SPAWN and REMOVAL in the game funnels
    /// through the one leaf mutation <c>TacticalVoxel.SetVoxelType(TacticalVoxelType, visualsDelay, voxelValue)</c>
    /// (SpawnTacticalVoxelEffect / RemoveTacticalVoxelEffect / SpawnMistAbility / Fire+Goo self-extinguish all
    /// call it — verified in the decompile; structural destruction uses a DIFFERENT system, so TS3 and TS6 stay
    /// disjoint concerns). The host postfixes that leaf, COALESCES the changed cells per flush heartbeat, and
    /// broadcasts them here; the client re-applies the SAME native leaf at the mirrored cells → its display + LoS
    /// are naturally correct. DAMAGE the volume deals to actors stays host-authoritative (rides tac.damage 0x88 /
    /// 0x8F) — the client volume is PRESENTATION + LoS only (inert-damage guards on the frozen client).
    ///
    /// WIRE (host→all, carries LiveSeq):
    ///   [seq:u32][opCount:u16]  then per op:
    ///     [op:u8 spawn=0/remove=1][voxelType:u8 Empty=0/Mist=1/Fire=2/Goo=4][cellCount:u16]{[x:f32][y:f32][z:f32]}*
    ///
    /// WIRE NOTE (deviation from the spec's provisional <c>[effectDefGuid:str]</c>): the native leaf funnel carries
    /// only the resulting <c>TacticalVoxelType</c>, not an effect def — the TYPE is exactly what the client needs to
    /// replay the same voxel, and carrying the enum is TFTV-tolerant BY CONSTRUCTION (no guid resolution that could
    /// fail/skip a custom TFTV surface def; a custom def still resolves to a base voxel-type). Explicit CELLS are
    /// carried (the spec's stated determinism preference) rather than a center+radius. <c>op</c> is explicit
    /// (spawn/remove) per the spec.
    ///
    /// BACKWARD-TOLERANT: <c>opCount</c> + per-op <c>cellCount</c> are self-describing length prefixes; an unknown
    /// future op/type still frames its cells so a decoder skips exactly its bytes. Truncation / a corrupt count →
    /// clean <c>false</c> (no partial accept), exactly like <see cref="TacticalDeployCodec"/> /
    /// <see cref="TacticalActorLifecycleCodec"/> — the reliable transport guarantees full delivery.
    /// </summary>
    public static class TacticalSurfaceCodec
    {
        /// <summary>Op discriminator (u8).</summary>
        public const byte OpSpawn = 0;   // set the cells to VoxelType (Mist/Fire/Goo)
        public const byte OpRemove = 1;   // clear the cells (VoxelType == VoxelEmpty)

        /// <summary>Voxel-type values (mirror <c>PhoenixPoint.Tactical.Levels.Mist.TacticalVoxelType</c>).</summary>
        public const byte VoxelEmpty = 0;
        public const byte VoxelMist = 1;
        public const byte VoxelFire = 2;
        public const byte VoxelGoo = 4;

        /// <summary>Cap on cells carried in one op — a corrupt/huge count is guarded against, and a very large
        /// volume is split into multiple ops (R1: throttle/cap). One full op of the cap fits well under the u16
        /// envelope cap (2048 * 12 B = 24576 B).</summary>
        public const int MaxCellsPerOp = 2048;

        /// <summary>One mirrored ground cell (world position, wire units are floats).</summary>
        public struct SurfaceCell
        {
            public float X, Y, Z;
            public SurfaceCell(float x, float y, float z) { X = x; Y = y; Z = z; }
        }

        /// <summary>One op: set/clear a list of cells to a single voxel type.</summary>
        public sealed class SurfaceOp
        {
            public byte Op;
            public byte VoxelType;
            public List<SurfaceCell> Cells = new List<SurfaceCell>();

            public SurfaceOp() { }
            public SurfaceOp(byte op, byte voxelType, List<SurfaceCell> cells)
            {
                Op = op; VoxelType = voxelType; Cells = cells ?? new List<SurfaceCell>();
            }
        }

        /// <summary>A batch of ops sharing one LiveSeq (one flush heartbeat).</summary>
        public sealed class SurfaceBatch
        {
            public uint Seq;
            public List<SurfaceOp> Ops = new List<SurfaceOp>();

            public SurfaceBatch() { }
            public SurfaceBatch(uint seq, List<SurfaceOp> ops)
            {
                Seq = seq; Ops = ops ?? new List<SurfaceOp>();
            }
        }

        // ─── Encode / Decode ─────────────────────────────────────────────────

        public static byte[] EncodeSurface(SurfaceBatch batch)
        {
            var ops = batch?.Ops ?? new List<SurfaceOp>();
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(batch != null ? batch.Seq : 0u);
                w.Write((ushort)ops.Count);
                foreach (var op in ops)
                {
                    var cells = op.Cells ?? new List<SurfaceCell>();
                    w.Write(op.Op);
                    w.Write(op.VoxelType);
                    w.Write((ushort)cells.Count);
                    foreach (var c in cells)
                    {
                        w.Write(c.X);
                        w.Write(c.Y);
                        w.Write(c.Z);
                    }
                }
                return ms.ToArray();
            }
        }

        /// <summary>Decode a 0x94 surface batch. Returns false (no partial accept) on any truncation or a cell
        /// count exceeding the remaining buffer (guards a corrupt huge count from a wild allocation).</summary>
        public static bool TryDecodeSurface(byte[] data, out SurfaceBatch batch)
        {
            batch = null;
            // Minimum: u32 seq + u16 opCount.
            if (data == null || data.Length < 6) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    int opCount = r.ReadUInt16();
                    var ops = new List<SurfaceOp>(opCount);
                    for (int i = 0; i < opCount; i++)
                    {
                        if (ms.Length - ms.Position < 4) return false;   // op + type + cellCount
                        byte op = r.ReadByte();
                        byte voxelType = r.ReadByte();
                        int cellCount = r.ReadUInt16();
                        // Each cell is fixed 12 bytes; guard the count against the remaining buffer.
                        if ((long)cellCount * 12 > ms.Length - ms.Position) return false;
                        var cells = new List<SurfaceCell>(cellCount);
                        for (int c = 0; c < cellCount; c++)
                        {
                            float x = r.ReadSingle();
                            float y = r.ReadSingle();
                            float z = r.ReadSingle();
                            cells.Add(new SurfaceCell(x, y, z));
                        }
                        ops.Add(new SurfaceOp(op, voxelType, cells));
                    }
                    batch = new SurfaceBatch(seq, ops);
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── PURE coalesce + group (host flush build) ────────────────────────

        /// <summary>One captured voxel change from the host <c>SetVoxelType</c> funnel: the grid-index dedup key
        /// (host↔client-stable) + the world position (wire) + the RESULTING voxel type.</summary>
        public struct CapturedCell
        {
            public int Kx, Ky, Kz;      // voxel grid indices — dedup key (last-write-wins per cell per flush)
            public float X, Y, Z;       // world position — the wire cell
            public byte VoxelType;      // resulting type after the SetVoxelType

            public CapturedCell(int kx, int ky, int kz, float x, float y, float z, byte voxelType)
            {
                Kx = kx; Ky = ky; Kz = kz; X = x; Y = y; Z = z; VoxelType = voxelType;
            }
        }

        /// <summary>PURE: coalesce a flush window of captured cell changes into wire ops. Dedups per cell
        /// (LAST write wins — a cell that flickered Fire→Empty within one window ships only its net final type),
        /// groups by resulting voxel type, derives <c>op</c> (Empty → remove, else spawn), and splits any group
        /// larger than <paramref name="maxCellsPerOp"/> into multiple same-type ops. Engine-free → unit-tested.</summary>
        public static List<SurfaceOp> CoalesceAndGroup(IReadOnlyList<CapturedCell> captures, int maxCellsPerOp)
        {
            var ops = new List<SurfaceOp>();
            if (captures == null || captures.Count == 0) return ops;
            if (maxCellsPerOp < 1) maxCellsPerOp = 1;

            // Dedup per cell (last write wins), preserving first-seen order for a stable output.
            var index = new Dictionary<CellKey, int>();
            var order = new List<CellKey>();
            var latest = new Dictionary<CellKey, CapturedCell>();
            foreach (var cap in captures)
            {
                var key = new CellKey(cap.Kx, cap.Ky, cap.Kz);
                if (!index.ContainsKey(key)) { index[key] = order.Count; order.Add(key); }
                latest[key] = cap;
            }

            // Group by resulting voxel type (stable first-seen order within the group).
            var byType = new Dictionary<byte, List<SurfaceCell>>();
            var typeOrder = new List<byte>();
            foreach (var key in order)
            {
                var cap = latest[key];
                if (!byType.TryGetValue(cap.VoxelType, out var list))
                {
                    list = new List<SurfaceCell>();
                    byType[cap.VoxelType] = list;
                    typeOrder.Add(cap.VoxelType);
                }
                list.Add(new SurfaceCell(cap.X, cap.Y, cap.Z));
            }

            foreach (var voxelType in typeOrder)
            {
                byte op = voxelType == VoxelEmpty ? OpRemove : OpSpawn;
                var cells = byType[voxelType];
                for (int off = 0; off < cells.Count; off += maxCellsPerOp)
                {
                    int len = System.Math.Min(maxCellsPerOp, cells.Count - off);
                    var chunk = cells.GetRange(off, len);
                    ops.Add(new SurfaceOp(op, voxelType, chunk));
                }
            }
            return ops;
        }

        /// <summary>Value key over the 3 grid indices (no ValueTuple dependency; stable equality/hash).</summary>
        private struct CellKey : System.IEquatable<CellKey>
        {
            public readonly int X, Y, Z;
            public CellKey(int x, int y, int z) { X = x; Y = y; Z = z; }
            public bool Equals(CellKey o) => X == o.X && Y == o.Y && Z == o.Z;
            public override bool Equals(object o) => o is CellKey k && Equals(k);
            public override int GetHashCode() { unchecked { return ((X * 397) ^ Y) * 397 ^ Z; } }
        }
    }
}
