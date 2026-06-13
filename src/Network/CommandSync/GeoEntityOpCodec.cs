using System.IO;

namespace Multipleer.Network.CommandSync
{
    // SD-AIDR INC-2: the wire op-types for 0x36 GeoEntityOp. Byte values are STABLE (serialized on the
    // wire) — never renumber. SiteCreated (3) is reserved/forward-compat only; INC-2 neither broadcasts
    // nor applies it (a new site needs a full GeoSiteInstaceData blob -> INC-3 0x35 GeoStateDiff).
    public enum GeoEntityOpType : byte
    {
        VehicleCreated = 1,
        VehicleRemoved = 2,
        SiteCreated = 3, // reserved for INC-3 (needs full site InstanceData)
        SiteRemoved = 4
    }

    // Pure, Unity-free. One authoritative entity create/destroy op. Carries enough to recreate the entity
    // by running its NATIVE lifecycle on the client (def guid + owner faction guid + anchor site/position)
    // and the host's authoritative VehicleID so the client assigns the SAME id (collision-free, §9/C8).
    // No engine types cross the wire — ids/guids are resolved back to live entities at apply time.
    public struct GeoEntityOp
    {
        public GeoEntityOpType OpType;
        public string DefGuid;            // BaseDef.Guid of the vehicle def (VehicleCreated)
        public string OwnerFactionGuid;   // GeoFaction.Def.Guid of the owner (VehicleCreated)
        public int SiteId;                // anchor GeoSite.SiteId (VehicleCreated via site) OR target (SiteRemoved); -1 = none
        public float PosX;                // world position (VehicleCreated via position when SiteId == -1)
        public float PosY;
        public float PosZ;
        public int EntityId;              // authoritative VehicleID (VehicleCreated / VehicleRemoved)
    }

    public static class GeoEntityOpCodec
    {
        public static byte[] Encode(GeoEntityOp op)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((byte)op.OpType);
                bw.Write(op.DefGuid ?? "");
                bw.Write(op.OwnerFactionGuid ?? "");
                bw.Write(op.SiteId);
                bw.Write(op.PosX);
                bw.Write(op.PosY);
                bw.Write(op.PosZ);
                bw.Write(op.EntityId);
                return ms.ToArray();
            }
        }

        public static GeoEntityOp Decode(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                return new GeoEntityOp
                {
                    OpType = (GeoEntityOpType)br.ReadByte(),
                    DefGuid = br.ReadString(),
                    OwnerFactionGuid = br.ReadString(),
                    SiteId = br.ReadInt32(),
                    PosX = br.ReadSingle(),
                    PosY = br.ReadSingle(),
                    PosZ = br.ReadSingle(),
                    EntityId = br.ReadInt32()
                };
            }
        }
    }
}
