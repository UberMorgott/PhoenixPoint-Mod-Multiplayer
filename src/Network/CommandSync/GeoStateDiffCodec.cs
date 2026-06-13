using System.Collections.Generic;
using System.IO;

namespace Multipleer.Network.CommandSync
{
    // SD-AIDR INC-3: changedMask bit flags for a 0x35 GeoStateDiff record of scope Vehicle. Bit values are
    // serialized on the wire — never renumber. Only set bits carry a value in the envelope (mask-order),
    // so the codec is self-delimited and a partial diff stays compact. Bits split into two channels:
    // CONTINUOUS (SurfacePos/SurfaceRot/RangeRemaining) ride the UNRELIABLE pos stream; DISCRETE
    // (Travelling/CurrentSite/DestinationSites/HitPoints) ride the RELIABLE transition stream.
    public static class GeoStateMask
    {
        public const int SurfacePos = 1 << 0;        // bit0 = 1
        public const int SurfaceRot = 1 << 1;        // bit1 = 2
        public const int RangeRemaining = 1 << 2;    // bit2 = 4
        public const int Travelling = 1 << 3;        // bit3 = 8
        public const int CurrentSite = 1 << 4;       // bit4 = 16
        public const int DestinationSites = 1 << 5;  // bit5 = 32
        public const int HitPoints = 1 << 6;         // bit6 = 64
    }

    // Pure, Unity-free. One vehicle's authoritative state snapshot for the 0x35 GeoStateDiff mirror,
    // keyed by the stable cross-instance identity (FactionGuid = GeoFaction.Def.Guid, VehicleID =
    // GeoVehicle.VehicleID). Engine types never cross the wire — primitives only, resolved back to the
    // live GeoVehicle at apply time (same contract as GeoEntityOp). Seq is the host-monotonic per-identity
    // sequence (client drops Seq <= last applied). ChangedMask selects which fields below are valid/sent.
    public struct GeoVehicleStateRecord
    {
        public string FactionGuid;        // GeoFaction.Def.Guid of the owner (identity)
        public int VehicleID;             // GeoVehicle.VehicleID (per-faction counter; identity)
        public ulong Seq;                 // host-monotonic per-(FactionGuid,VehicleID) sequence
        public int ChangedMask;           // GeoStateMask bit set: which fields below are valid/sent

        public float PosX;                // SurfacePos (GeoVehicleInstanceData.SurfacePos)
        public float PosY;
        public float PosZ;

        public float RotX;                // SurfaceRot (GeoVehicleInstanceData.SurfaceRot)
        public float RotY;
        public float RotZ;
        public float RotW;

        public float RangeRemaining;      // GeoVehicleInstanceData.RangeRemaining (EarthUnits value)
        public bool Travelling;           // GeoVehicleInstanceData.Travelling
        public int CurrentSiteId;         // GeoSite.SiteId of CurrentSite; -1 = none
        public int[] DestinationSiteIds;  // ordered GeoSite.SiteId of DestinationSites
        public float HitPoints;           // GeoVehicleInstanceData.HitPoints
    }

    // Pure, Unity-free envelope: a batch of state records for one 0x35 GeoStateDiff broadcast. Many vehicle
    // records (and, in later INC-3 slices, other scopes) are packed into ONE envelope per tick/channel.
    public struct GeoStateDiff
    {
        public List<GeoVehicleStateRecord> Records;
    }

    // Pure, Unity-free wire codec for the 0x35 GeoStateDiff envelope. Same MemoryStream+BinaryWriter layout
    // conventions as GeoEntityOpCodec. The envelope is SELF-DELIMITED: a per-record [scope][seq][guid][id][mask]
    // header lets a reader recover record boundaries, and only mask-set fields are written (in stable bit order)
    // so a partial diff stays compact. INC-3a only emits/decodes scope Vehicle(1); other scopes are reserved
    // (forward-compat for INC-3b/c/CRC) and have no body reader yet — see Decode's guard.
    public static class GeoStateDiffCodec
    {
        // Stable wire format version. Bump only on an incompatible layout change.
        public const byte FormatVersion = 1;

        public static byte[] Encode(GeoStateDiff diff)
        {
            var records = diff.Records;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(FormatVersion);
                int count = records?.Count ?? 0;
                bw.Write(count);
                for (int i = 0; i < count; i++)
                {
                    WriteRecord(bw, records[i]);
                }
                return ms.ToArray();
            }
        }

        public static GeoStateDiff Decode(byte[] data)
        {
            var result = new List<GeoVehicleStateRecord>();
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                br.ReadByte(); // FormatVersion — read past it (only one version today).
                int count = br.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    var scope = (GeoStateScope)br.ReadByte();
                    ulong seq = br.ReadUInt64();
                    string factionGuid = br.ReadString();
                    int vehicleID = br.ReadInt32();
                    int mask = br.ReadInt32();
                    if (scope == GeoStateScope.Vehicle)
                    {
                        result.Add(ReadVehicleBody(br, seq, factionGuid, vehicleID, mask));
                    }
                    // INC-3a: no body reader for other scopes yet. They are not produced in this slice, so we
                    // cannot length-skip an unknown body here; reaching one means a forward-version envelope —
                    // stop reading rather than mis-aligning the stream. (INC-3b/c add per-scope bodies.)
                    else
                    {
                        break;
                    }
                }
            }
            return new GeoStateDiff { Records = result };
        }

        // Per record: [byte scope][ulong Seq][string FactionGuid][int VehicleID][int ChangedMask] then ONLY
        // mask-set fields in stable bit order (pos xyz / rot xyzw / range / travelling / currentSiteId /
        // destCount+ids / hitpoints).
        private static void WriteRecord(BinaryWriter bw, GeoVehicleStateRecord r)
        {
            bw.Write((byte)GeoStateScope.Vehicle);
            bw.Write(r.Seq);
            bw.Write(r.FactionGuid ?? "");
            bw.Write(r.VehicleID);
            bw.Write(r.ChangedMask);

            if ((r.ChangedMask & GeoStateMask.SurfacePos) != 0)
            {
                bw.Write(r.PosX);
                bw.Write(r.PosY);
                bw.Write(r.PosZ);
            }
            if ((r.ChangedMask & GeoStateMask.SurfaceRot) != 0)
            {
                bw.Write(r.RotX);
                bw.Write(r.RotY);
                bw.Write(r.RotZ);
                bw.Write(r.RotW);
            }
            if ((r.ChangedMask & GeoStateMask.RangeRemaining) != 0)
            {
                bw.Write(r.RangeRemaining);
            }
            if ((r.ChangedMask & GeoStateMask.Travelling) != 0)
            {
                bw.Write(r.Travelling);
            }
            if ((r.ChangedMask & GeoStateMask.CurrentSite) != 0)
            {
                bw.Write(r.CurrentSiteId);
            }
            if ((r.ChangedMask & GeoStateMask.DestinationSites) != 0)
            {
                var dest = r.DestinationSiteIds;
                int destCount = dest?.Length ?? 0;
                bw.Write(destCount);
                for (int i = 0; i < destCount; i++)
                {
                    bw.Write(dest[i]);
                }
            }
            if ((r.ChangedMask & GeoStateMask.HitPoints) != 0)
            {
                bw.Write(r.HitPoints);
            }
        }

        private static GeoVehicleStateRecord ReadVehicleBody(BinaryReader br, ulong seq, string factionGuid, int vehicleID, int mask)
        {
            var r = new GeoVehicleStateRecord
            {
                Seq = seq,
                FactionGuid = factionGuid,
                VehicleID = vehicleID,
                ChangedMask = mask
            };

            if ((mask & GeoStateMask.SurfacePos) != 0)
            {
                r.PosX = br.ReadSingle();
                r.PosY = br.ReadSingle();
                r.PosZ = br.ReadSingle();
            }
            if ((mask & GeoStateMask.SurfaceRot) != 0)
            {
                r.RotX = br.ReadSingle();
                r.RotY = br.ReadSingle();
                r.RotZ = br.ReadSingle();
                r.RotW = br.ReadSingle();
            }
            if ((mask & GeoStateMask.RangeRemaining) != 0)
            {
                r.RangeRemaining = br.ReadSingle();
            }
            if ((mask & GeoStateMask.Travelling) != 0)
            {
                r.Travelling = br.ReadBoolean();
            }
            if ((mask & GeoStateMask.CurrentSite) != 0)
            {
                r.CurrentSiteId = br.ReadInt32();
            }
            if ((mask & GeoStateMask.DestinationSites) != 0)
            {
                int destCount = br.ReadInt32();
                var dest = new int[destCount];
                for (int i = 0; i < destCount; i++)
                {
                    dest[i] = br.ReadInt32();
                }
                r.DestinationSiteIds = dest;
            }
            if ((mask & GeoStateMask.HitPoints) != 0)
            {
                r.HitPoints = br.ReadSingle();
            }
            return r;
        }
    }
}
