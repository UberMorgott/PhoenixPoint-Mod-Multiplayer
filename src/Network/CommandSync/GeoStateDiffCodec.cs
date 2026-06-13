using System.Collections.Generic;

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
}
