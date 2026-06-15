using System;
using System.IO;

namespace Multipleer.Network.Sync.Actions
{
    /// <summary>
    /// Repairs a facility in a base. Wire payload:
    /// <c>string baseId, string facilityId, i32 gridX, i32 gridY</c>.
    /// facilityId = <c>GeoPhoenixFacility.FacilityId</c> (uint as string); grid position is the
    /// deterministic fallback when the instance id can't be matched on the remote peer.
    /// </summary>
    public sealed class RepairFacilityAction : ISyncedAction
    {
        private readonly string _baseId;
        private readonly string _facilityId;
        private readonly int _gridX;
        private readonly int _gridY;

        public RepairFacilityAction(string baseId, string facilityId, int gridX, int gridY)
        {
            _baseId = baseId;
            _facilityId = facilityId;
            _gridX = gridX;
            _gridY = gridY;
        }

        public ushort ActionId => SyncedActionIds.RepairFacility;
        public ActionCategory Category => ActionCategory.BaseRepair;

        public void Write(BinaryWriter w)
        {
            w.Write(_baseId ?? "");
            w.Write(_facilityId ?? "");
            w.Write(_gridX);
            w.Write(_gridY);
        }

        public static ISyncedAction Read(BinaryReader r)
            => new RepairFacilityAction(r.ReadString(), r.ReadString(), r.ReadInt32(), r.ReadInt32());

        public bool Validate(GeoRuntime rt, Guid actor)
            => !string.IsNullOrEmpty(_baseId) && rt != null && rt.IsGeoscapeActive;

        public void Apply(GeoRuntime rt)
            => BaseReflection.Repair(rt, _baseId, _facilityId, _gridX, _gridY);
    }
}
