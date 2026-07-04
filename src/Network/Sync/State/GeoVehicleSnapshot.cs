using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Multipleer.Network.Sync.State
{
    /// <summary>
    /// One vehicle's mirrored GLOBE PLACEMENT — the EXACT state the native <c>GeoNavComponent.NavigateRoutine</c>
    /// writes each travel tick, so a sim-frozen client reproduces travel by replaying it:
    ///   * <c>QX/QY/QZ/QW</c> = <c>PivotTransform.localRotation</c> (the parent-pivot quaternion — the SOLE
    ///     determinant of the vehicle's position on the globe; NavigateRoutine writes ONLY this to move a
    ///     vehicle, and the on-globe GlobeMarker + 3D mesh both hang off the pivot, GeoNavComponent.cs:111).
    ///   * <c>X/Y/Z</c> = <c>Surface.localEulerAngles</c> (the vehicle's heading/facing, GeoNavComponent.cs:213).
    /// (Inc4 S2 fix 2026-07-04 — the original mirrored <c>Surface.position</c>/<c>Surface.rotation</c>, a
    /// DERIVED world value of the GlobeOffset child; writing it left the pivot untouched so the frozen client's
    /// marker never moved and every vehicle mismatched the host. Mirroring the LOCAL pivot rotation is also
    /// frame-of-reference-robust across the two instances' globe hierarchies.)
    ///
    /// KEY (Inc4 S2 fix #3 2026-07-04, DIAG-proven): <c>GeoVehicle.VehicleID</c> is per-FACTION, NOT global —
    /// each <c>GeoFaction</c> numbers its vehicles from its own <c>++_lastVehicleIndex</c> (GeoFaction.cs:2008,
    /// save-persisted :376/:598), so a 25-vehicle map has ~5 vehicles ALL with VehicleID=1 (in-game DIAG
    /// 2026-07-04: host shipped PX_Mantis/DA_Blimp/NJ_Rhino/SYN_Icarus all "id=1"; the client applied every one
    /// of them to ONE vehicle — last-writer-wins in its id-keyed lookup — which ended each batch back on its own
    /// parked rotation → nothing ever moved; the host's per-id signature slot churned the same way → every
    /// vehicle re-shipped every poll). The mirror key is therefore the COMPOSITE (<see cref="OwnerId"/> =
    /// <see cref="StableOwnerKey"/> of the owner faction's def asset name, VehicleID); the def asset name is
    /// identical across the two instances (same build, host-replicated state).
    /// A pure value type (no UnityEngine dependency) with structural equality so the
    /// codec round-trip is directly assertable in unit tests (mirrors <see cref="GeoSiteState"/>).
    /// </summary>
    public readonly struct GeoVehiclePos : IEquatable<GeoVehiclePos>
    {
        public readonly int OwnerId;          // StableOwnerKey(owner faction def name) — disambiguates per-faction VehicleIDs
        public readonly int VehicleId;
        public readonly float X, Y, Z;        // Surface.localEulerAngles (heading, degrees)
        public readonly float QX, QY, QZ, QW; // PivotTransform.localRotation (globe placement quaternion)

        public GeoVehiclePos(int ownerId, int vehicleId, float x, float y, float z, float qx, float qy, float qz, float qw)
        {
            OwnerId = ownerId;
            VehicleId = vehicleId;
            X = x; Y = y; Z = z;
            QX = qx; QY = qy; QZ = qz; QW = qw;
        }

        /// <summary>The composite mirror key — globally unique (VehicleID alone is only per-faction unique).</summary>
        public long Key => MakeKey(OwnerId, VehicleId);

        public static long MakeKey(int ownerId, int vehicleId) => ((long)ownerId << 32) | (uint)vehicleId;

        /// <summary>Deterministic 32-bit FNV-1a of the owner faction's def asset name — stable across runs,
        /// processes and machines (unlike <c>string.GetHashCode</c>, which is runtime-dependent). Null/empty → 0
        /// (symmetric fallback on both ends; a live vehicle always has an owner once in play).</summary>
        public static int StableOwnerKey(string ownerDefName)
        {
            if (string.IsNullOrEmpty(ownerDefName)) return 0;
            unchecked
            {
                uint h = 2166136261u;
                foreach (char c in ownerDefName) { h ^= c; h *= 16777619u; }
                return (int)h;
            }
        }

        public bool Equals(GeoVehiclePos o)
            => OwnerId == o.OwnerId && VehicleId == o.VehicleId
               && X == o.X && Y == o.Y && Z == o.Z
               && QX == o.QX && QY == o.QY && QZ == o.QZ && QW == o.QW;

        public override bool Equals(object obj) => obj is GeoVehiclePos o && Equals(o);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = OwnerId;
                h = (h * 397) ^ VehicleId;
                h = (h * 397) ^ X.GetHashCode();
                h = (h * 397) ^ Y.GetHashCode();
                h = (h * 397) ^ Z.GetHashCode();
                h = (h * 397) ^ QX.GetHashCode();
                h = (h * 397) ^ QY.GetHashCode();
                h = (h * 397) ^ QZ.GetHashCode();
                h = (h * 397) ^ QW.GetHashCode();
                return h;
            }
        }

        /// <summary>Order-stable change-detection signature: the HOST skips a vehicle whose signature is
        /// unchanged since the last flush, so a PARKED vehicle produces ZERO bytes and only genuinely-moving
        /// vehicles are broadcast (mirrors the tactical actor-state pos signature). The pivot quaternion is the
        /// PRIMARY travel signal (position on the globe) so it is rounded FINE (F6 ≈ 0.0001° pivot) to catch even
        /// slow craft; a parked pivot is a stored constant (bit-stable per poll) so F6 never false-triggers. The
        /// heading euler rounds at F2 (0.01°), dedupping sub-0.01° facing jitter.</summary>
        public static string Signature(GeoVehiclePos v)
        {
            var c = CultureInfo.InvariantCulture;
            return v.X.ToString("F2", c) + "," + v.Y.ToString("F2", c) + "," + v.Z.ToString("F2", c) + "|"
                 + v.QX.ToString("F6", c) + "," + v.QY.ToString("F6", c) + ","
                 + v.QZ.ToString("F6", c) + "," + v.QW.ToString("F6", c);
        }
    }

    /// <summary>
    /// Decoded GeoVehicle placement-mirror batch (Inc4 S2 host-driven travel mirror): the ABSOLUTE globe
    /// placement of each CHANGED vehicle (pivot <c>localRotation</c> + heading euler — see <see cref="GeoVehiclePos"/>),
    /// pushed host→all so a client whose geoscape sim CLOCK is frozen (S1) still sees vehicles travel — the client
    /// replays the host's pivot rotation and never simulates or integrates its own vehicle motion (canon: client =
    /// pure mirror). Drift-free by construction (absolute values + last-writer-wins seq), path/speed/TFTV-agnostic.
    ///
    /// Pure data + wire codec — free of any <c>SyncEngine</c>/Unity dependency so it is directly unit-testable
    /// (mirrors <see cref="GeoSiteSnapshot"/>). The engine glue (host read + client apply) lives in
    /// <c>GeoVehicleMirror</c> (game-bound reflection, NOT linked into the test project).
    ///
    /// Wire payload (inside the 0x67 envelope, surface <c>GeoVehiclePos</c>) — 36 bytes/vehicle:
    ///   [u32 seq][u16 count]{[i32 OwnerId][i32 VehicleId][f32 headingX][f32 headingY][f32 headingZ][f32 pivotQx][f32 pivotQy][f32 pivotQz][f32 pivotQw]}*
    /// The leading seq is the host's per-surface <see cref="SurfaceSeq"/> value (client drops a stale/dup seq).
    /// </summary>
    public static class GeoVehicleSnapshot
    {
        public static byte[] Encode(uint seq, IList<GeoVehiclePos> vehicles)
        {
            vehicles = vehicles ?? new List<GeoVehiclePos>();
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq);
                w.Write((ushort)vehicles.Count);
                foreach (var v in vehicles)
                {
                    w.Write(v.OwnerId);
                    w.Write(v.VehicleId);
                    w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
                    w.Write(v.QX); w.Write(v.QY); w.Write(v.QZ); w.Write(v.QW);
                }
                return ms.ToArray();
            }
        }

        /// <summary>Decode a vehicle position batch. Returns false (no partial accept) on any truncation —
        /// the reliable transport guarantees full delivery, so a short buffer is a clean drop.</summary>
        public static bool TryDecode(byte[] data, out uint seq, out List<GeoVehiclePos> vehicles)
        {
            seq = 0; vehicles = null;
            if (data == null) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    seq = r.ReadUInt32();
                    int n = r.ReadUInt16();
                    // Each row is fixed 4 + 4 + 7*4 = 36 bytes; guard the count against the remaining buffer so a
                    // corrupt count can't allocate wildly.
                    if ((long)n * 36 > ms.Length - ms.Position) return false;
                    var list = new List<GeoVehiclePos>(n);
                    for (int i = 0; i < n; i++)
                    {
                        int owner = r.ReadInt32();
                        int id = r.ReadInt32();
                        float x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle();
                        float qx = r.ReadSingle(), qy = r.ReadSingle(), qz = r.ReadSingle(), qw = r.ReadSingle();
                        list.Add(new GeoVehiclePos(owner, id, x, y, z, qx, qy, qz, qw));
                    }
                    vehicles = list;
                    return true;
                }
            }
            catch (Exception) { return false; }
        }
    }
}
