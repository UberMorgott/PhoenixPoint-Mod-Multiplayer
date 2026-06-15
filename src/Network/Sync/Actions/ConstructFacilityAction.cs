using System;
using System.IO;

namespace Multipleer.Network.Sync.Actions
{
    /// <summary>
    /// Constructs a facility in a base. Wire payload:
    /// <c>string baseId, string facilityDefId, i32 gridX, i32 gridY, i32 rotation</c>.
    /// baseId = <c>GeoSite.SiteId</c>; facilityDefId = <c>PhoenixFacilityDef.Guid</c>;
    /// rotation = <c>PhoenixBaseLayoutRotation</c> int (Rot0..Rot270 = 0..3).
    /// </summary>
    public sealed class ConstructFacilityAction : ISyncedAction
    {
        private readonly string _baseId;
        private readonly string _facilityDefId;
        private readonly int _gridX;
        private readonly int _gridY;
        private readonly int _rotation;

        public ConstructFacilityAction(string baseId, string facilityDefId, int gridX, int gridY, int rotation)
        {
            _baseId = baseId;
            _facilityDefId = facilityDefId;
            _gridX = gridX;
            _gridY = gridY;
            _rotation = rotation;
        }

        public ushort ActionId => SyncedActionIds.ConstructFacility;
        public ActionCategory Category => ActionCategory.BaseConstruction;

        public void Write(BinaryWriter w)
        {
            w.Write(_baseId ?? "");
            w.Write(_facilityDefId ?? "");
            w.Write(_gridX);
            w.Write(_gridY);
            w.Write(_rotation);
        }

        public static ISyncedAction Read(BinaryReader r)
            => new ConstructFacilityAction(r.ReadString(), r.ReadString(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());

        public bool Validate(GeoRuntime rt, Guid actor)
            => !string.IsNullOrEmpty(_baseId) && !string.IsNullOrEmpty(_facilityDefId)
               && rt != null && rt.IsGeoscapeActive;

        public void Apply(GeoRuntime rt)
            => BaseReflection.Construct(rt, _baseId, _facilityDefId, _gridX, _gridY, _rotation);
    }
}
