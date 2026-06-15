using System;
using System.IO;

namespace Multipleer.Network.Sync.Actions
{
    /// <summary>
    /// Host-driven facility completion (construction/repair finished). Wire payload:
    /// <c>string baseId, string facilityId, i32 gridX, i32 gridY</c>. Same identity scheme as
    /// <see cref="RepairFacilityAction"/>: FacilityId primary, grid position fallback.
    /// </summary>
    public sealed class FacilityCompletedAction : ISyncedAction
    {
        private readonly string _baseId;
        private readonly string _facilityId;
        private readonly int _gridX;
        private readonly int _gridY;

        public FacilityCompletedAction(string baseId, string facilityId, int gridX, int gridY)
        {
            _baseId = baseId;
            _facilityId = facilityId;
            _gridX = gridX;
            _gridY = gridY;
        }

        public ushort ActionId => SyncedActionIds.FacilityCompleted;
        public ActionCategory Category => ActionCategory.BaseConstruction;

        public void Write(BinaryWriter w)
        {
            w.Write(_baseId ?? "");
            w.Write(_facilityId ?? "");
            w.Write(_gridX);
            w.Write(_gridY);
        }

        public static ISyncedAction Read(BinaryReader r)
            => new FacilityCompletedAction(r.ReadString(), r.ReadString(), r.ReadInt32(), r.ReadInt32());

        public bool Validate(GeoRuntime rt, Guid actor)
            => !string.IsNullOrEmpty(_baseId) && rt != null && rt.IsGeoscapeActive;

        public void Apply(GeoRuntime rt)
            => BaseReflection.Complete(rt, _baseId, _facilityId, _gridX, _gridY);
    }
}
