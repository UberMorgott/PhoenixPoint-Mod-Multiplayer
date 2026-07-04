using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Multipleer.Network.Sync.State
{
    /// <summary>
    /// One vehicle's mirrored WORLD PLACEMENT: the exact pair the native
    /// <c>GeoVehicle.RecordInstanceData</c>/<c>ProcessInstanceData</c> round-trips to persist/restore a
    /// vehicle — its <c>Surface.position</c> (world Vector3, carried as x/y/z) and <c>Surface.rotation</c>
    /// (world Quaternion, carried as qx/qy/qz/qw). Keyed by the stable, save-persisted
    /// <c>GeoVehicle.VehicleID</c> so the client resolves the SAME vehicle host-side. A pure value type
    /// (float-only, no UnityEngine dependency) with structural equality so the codec round-trip is directly
    /// assertable in unit tests (mirrors <see cref="GeoSiteState"/>).
    /// </summary>
    public readonly struct GeoVehiclePos : IEquatable<GeoVehiclePos>
    {
        public readonly int VehicleId;
        public readonly float X, Y, Z;        // Surface.position
        public readonly float QX, QY, QZ, QW; // Surface.rotation

        public GeoVehiclePos(int vehicleId, float x, float y, float z, float qx, float qy, float qz, float qw)
        {
            VehicleId = vehicleId;
            X = x; Y = y; Z = z;
            QX = qx; QY = qy; QZ = qz; QW = qw;
        }

        public bool Equals(GeoVehiclePos o)
            => VehicleId == o.VehicleId
               && X == o.X && Y == o.Y && Z == o.Z
               && QX == o.QX && QY == o.QY && QZ == o.QZ && QW == o.QW;

        public override bool Equals(object obj) => obj is GeoVehiclePos o && Equals(o);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = VehicleId;
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

        /// <summary>Order-stable change-detection signature (F2 position, F3 rotation): the HOST skips a
        /// vehicle whose signature is unchanged since the last flush, so a PARKED vehicle produces ZERO bytes
        /// and only genuinely-moving vehicles are broadcast (mirrors the tactical actor-state pos signature).
        /// Rounding dedups sub-0.01 render jitter while re-broadcasting any real travel step.</summary>
        public static string Signature(GeoVehiclePos v)
        {
            var c = CultureInfo.InvariantCulture;
            return v.X.ToString("F2", c) + "," + v.Y.ToString("F2", c) + "," + v.Z.ToString("F2", c) + "|"
                 + v.QX.ToString("F3", c) + "," + v.QY.ToString("F3", c) + ","
                 + v.QZ.ToString("F3", c) + "," + v.QW.ToString("F3", c);
        }
    }

    /// <summary>
    /// Decoded GeoVehicle position-mirror batch (Inc4 S2 host-driven travel mirror): the world placement of
    /// each CHANGED vehicle, pushed host→all so a client whose geoscape sim CLOCK is frozen (S1) still sees
    /// vehicles travel — the client applies the host's ABSOLUTE position/rotation and never simulates or
    /// integrates its own vehicle motion (canon: client = pure mirror). Drift-free by construction (absolute
    /// values + last-writer-wins seq), path/speed/TFTV-agnostic.
    ///
    /// Pure data + wire codec — free of any <c>SyncEngine</c>/Unity dependency so it is directly unit-testable
    /// (mirrors <see cref="GeoSiteSnapshot"/>). The engine glue (host read + client apply) lives in
    /// <c>GeoVehicleMirror</c> (game-bound reflection, NOT linked into the test project).
    ///
    /// Wire payload (inside the 0x67 envelope, surface <c>GeoVehiclePos</c>):
    ///   [u32 seq][u16 count]{[i32 VehicleId][f32 x][f32 y][f32 z][f32 qx][f32 qy][f32 qz][f32 qw]}*
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
                    // Each row is fixed 4 + 7*4 = 32 bytes; guard the count against the remaining buffer so a
                    // corrupt count can't allocate wildly.
                    if ((long)n * 32 > ms.Length - ms.Position) return false;
                    var list = new List<GeoVehiclePos>(n);
                    for (int i = 0; i < n; i++)
                    {
                        int id = r.ReadInt32();
                        float x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle();
                        float qx = r.ReadSingle(), qy = r.ReadSingle(), qz = r.ReadSingle(), qw = r.ReadSingle();
                        list.Add(new GeoVehiclePos(id, x, y, z, qx, qy, qz, qw));
                    }
                    vehicles = list;
                    return true;
                }
            }
            catch (Exception) { return false; }
        }
    }
}
