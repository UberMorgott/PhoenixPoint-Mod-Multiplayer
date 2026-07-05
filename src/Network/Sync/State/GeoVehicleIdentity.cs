using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// One vehicle's mirrored CREATION IDENTITY — enough for a sim-frozen client to spawn an INERT mirror of a
    /// vehicle it never created (an aircraft acquired mid-session: manufactured, story-gift, or stolen). The
    /// ongoing <see cref="GeoVehiclePos"/> (0xA5) / travel (0xA6) / explore (0xA7) mirrors then keep it placed —
    /// but they SILENTLY SKIP an unknown composite key, so without a creation channel a mid-session craft is
    /// invisible on the client forever. This carries the create-time facts (mirrors <see cref="GeoSiteState"/>):
    ///   * <see cref="OwnerId"/> = <see cref="GeoVehiclePos.StableOwnerKey"/> of the owner faction's def asset
    ///     name — the SAME owner half the position/travel/explore mirrors key on, so the spawned mirror's
    ///     composite key <see cref="Key"/> == the key those channels resolve (else it would be spawned yet still
    ///     never move). Carried explicitly (not recomputed) so host and client keys are bit-identical.
    ///   * <see cref="VehicleId"/> = <c>GeoVehicle.VehicleID</c> (per-faction; the composite key disambiguates it).
    ///   * <see cref="OwnerFactionDefGuid"/> = the owner <c>GeoFaction.Def.Guid</c> — resolves the LIVE owning
    ///     faction on the client (needed to set the spawned vehicle's Owner). Mirrors the GeoSite owner-by-guid.
    ///   * <see cref="VehicleSetDefGuid"/> = the vehicle's spawn <c>ComponentSet.SetDef.Guid</c> (the same
    ///     <c>ComponentSetDef</c> the native <c>GeoFaction.CreateVehicle</c> instantiates) — resolves the prefab
    ///     template on the client for an inert <c>ActorSpawner.SpawnActor&lt;GeoVehicle&gt;(setDef, null, false)</c>.
    ///   * <see cref="QX"/>..<see cref="QW"/> / <see cref="X"/>..<see cref="Z"/> = the initial
    ///     <c>PivotTransform.localRotation</c> + <c>Surface.localEulerAngles</c> (identical encoding to
    ///     <see cref="GeoVehiclePos"/>) so the mirror is placed correctly the instant it spawns, before the next
    ///     0xA5 poll arrives.
    /// A pure value type (no UnityEngine dependency) with structural equality so the codec round-trip is directly
    /// unit-testable (mirrors <see cref="GeoSiteState"/> / <see cref="GeoVehiclePos"/>).
    /// </summary>
    public readonly struct GeoVehicleIdentity : IEquatable<GeoVehicleIdentity>
    {
        public readonly int OwnerId;                 // StableOwnerKey(owner faction def name) — composite-key owner half
        public readonly int VehicleId;               // GeoVehicle.VehicleID (per-faction)
        public readonly string OwnerFactionDefGuid;  // GeoFaction.Def.Guid — resolve the live owning faction on the client
        public readonly string VehicleSetDefGuid;    // ComponentSet.SetDef.Guid — spawn template (ComponentSetDef) guid
        public readonly float QX, QY, QZ, QW;        // PivotTransform.localRotation (initial globe placement)
        public readonly float X, Y, Z;               // Surface.localEulerAngles (initial heading)

        public GeoVehicleIdentity(int ownerId, int vehicleId, string ownerFactionDefGuid, string vehicleSetDefGuid,
                                  float qx, float qy, float qz, float qw, float x, float y, float z)
        {
            OwnerId = ownerId;
            VehicleId = vehicleId;
            // Normalize null → "" so equality + the wire are stable (Decode also coalesces).
            OwnerFactionDefGuid = ownerFactionDefGuid ?? "";
            VehicleSetDefGuid = vehicleSetDefGuid ?? "";
            QX = qx; QY = qy; QZ = qz; QW = qw;
            X = x; Y = y; Z = z;
        }

        /// <summary>The composite mirror key — MUST match the position/travel/explore mirrors' key so the spawned
        /// vehicle is resolvable by 0xA5/0xA6/0xA7. Reuses <see cref="GeoVehiclePos.MakeKey"/>.</summary>
        public long Key => GeoVehiclePos.MakeKey(OwnerId, VehicleId);

        public bool Equals(GeoVehicleIdentity o)
            => OwnerId == o.OwnerId && VehicleId == o.VehicleId
               && OwnerFactionDefGuid == o.OwnerFactionDefGuid && VehicleSetDefGuid == o.VehicleSetDefGuid
               && QX == o.QX && QY == o.QY && QZ == o.QZ && QW == o.QW
               && X == o.X && Y == o.Y && Z == o.Z;

        public override bool Equals(object obj) => obj is GeoVehicleIdentity o && Equals(o);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = OwnerId;
                h = (h * 397) ^ VehicleId;
                h = (h * 397) ^ (OwnerFactionDefGuid?.GetHashCode() ?? 0);
                h = (h * 397) ^ (VehicleSetDefGuid?.GetHashCode() ?? 0);
                h = (h * 397) ^ QX.GetHashCode();
                h = (h * 397) ^ QY.GetHashCode();
                h = (h * 397) ^ QZ.GetHashCode();
                h = (h * 397) ^ QW.GetHashCode();
                h = (h * 397) ^ X.GetHashCode();
                h = (h * 397) ^ Y.GetHashCode();
                h = (h * 397) ^ Z.GetHashCode();
                return h;
            }
        }

        public override string ToString()
            => $"VehIdentity(owner={OwnerId:X8} id={VehicleId} facGuid={OwnerFactionDefGuid} setGuid={VehicleSetDefGuid})";
    }

    /// <summary>
    /// Decoded GeoVehicle CREATION/DESTRUCTION batch (mid-session vehicle-creation channel #6): the FULL resident
    /// identity set (every post-bind creation, re-emitted each flush — the unacked transport heals lost flushes /
    /// failed applies through the client's key-idempotent apply) plus the FULL tombstone key set (vehicles the
    /// host destroyed/lost — the client despawns its mirror). Pure data + wire codec — free of any
    /// <c>IStateChannel</c>/<c>SyncEngine</c>/Unity dependency so it is directly unit-testable (mirrors
    /// <see cref="GeoSiteSnapshot"/>). The engine glue (host detect + client spawn/despawn) lives in
    /// <c>GeoVehicleChannel</c> / <c>GeoVehicleIdentityReflection</c>.
    ///
    /// Wire payload (inside the 0x67 envelope, GeoState 0xA1 surface, EncodeStateSync(channelId=6, ver, payload)):
    ///   [u16 count]{[i32 OwnerId][i32 VehicleId][str OwnerFactionDefGuid][str VehicleSetDefGuid]
    ///               [f32 QX][f32 QY][f32 QZ][f32 QW][f32 X][f32 Y][f32 Z]}*
    ///   [u16 tombCount]{[i64 compositeKey]}*
    /// Strings are [u16 len][utf8] (mirrors <see cref="GeoSiteSnapshot"/>). Both peers run the same DLL, so the
    /// tombstone section is unconditional (no legacy-format branch).
    /// </summary>
    public sealed class GeoVehicleIdentitySnapshot
    {
        public readonly List<GeoVehicleIdentity> Vehicles = new List<GeoVehicleIdentity>();

        /// <summary>Composite keys (see <see cref="GeoVehicleIdentity.Key"/>) of vehicles no longer live on the
        /// host — the client despawns any live mirror with that key (idempotent when absent).</summary>
        public readonly List<long> Tombstones = new List<long>();

        public static byte[] Encode(GeoVehicleIdentitySnapshot snap)
        {
            if (snap == null) return null;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write((ushort)snap.Vehicles.Count);
                foreach (var v in snap.Vehicles)
                {
                    w.Write(v.OwnerId);
                    w.Write(v.VehicleId);
                    WriteStr(w, v.OwnerFactionDefGuid);
                    WriteStr(w, v.VehicleSetDefGuid);
                    w.Write(v.QX); w.Write(v.QY); w.Write(v.QZ); w.Write(v.QW);
                    w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
                }
                w.Write((ushort)snap.Tombstones.Count);
                foreach (var key in snap.Tombstones) w.Write(key);
                return ms.ToArray();
            }
        }

        public static GeoVehicleIdentitySnapshot Decode(byte[] data)
        {
            if (data == null) return null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    var snap = new GeoVehicleIdentitySnapshot();
                    int n = r.ReadUInt16();
                    for (int i = 0; i < n; i++)
                    {
                        int ownerId = r.ReadInt32();
                        int vehicleId = r.ReadInt32();
                        string facGuid = ReadStr(r);
                        string setGuid = ReadStr(r);
                        float qx = r.ReadSingle(), qy = r.ReadSingle(), qz = r.ReadSingle(), qw = r.ReadSingle();
                        float x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle();
                        snap.Vehicles.Add(new GeoVehicleIdentity(ownerId, vehicleId, facGuid, setGuid, qx, qy, qz, qw, x, y, z));
                    }
                    int tombs = r.ReadUInt16();
                    for (int i = 0; i < tombs; i++)
                        snap.Tombstones.Add(r.ReadInt64());
                    return snap;
                }
            }
            // Pure/Unity-free (unit-testable): swallow malformed payloads and return null; the caller
            // (GeoVehicleChannel.Apply) treats null as "no-op". No UnityEngine.Debug dependency here.
            catch (Exception) { return null; }
        }

        private static void WriteStr(BinaryWriter w, string s)
        {
            var b = Encoding.UTF8.GetBytes(s ?? "");
            w.Write((ushort)b.Length);
            w.Write(b);
        }

        private static string ReadStr(BinaryReader r)
        {
            int len = r.ReadUInt16();
            // BinaryReader.ReadBytes silently returns FEWER bytes at end-of-stream (no throw); verify the full
            // length was read, else throw → caught by Decode → null (rejected, not garbage).
            var bytes = r.ReadBytes(len);
            if (bytes.Length != len)
                throw new EndOfStreamException("GeoVehicleIdentitySnapshot: truncated string (wanted " + len + ", got " + bytes.Length + ")");
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
