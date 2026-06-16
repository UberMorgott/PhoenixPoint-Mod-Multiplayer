using System;
using System.IO;

namespace Multipleer.Network.Sync.Actions
{
    /// <summary>
    /// Host-driven facility completion (construction/repair finished). Wire payload:
    /// <c>string baseId, string facilityId, i32 gridX, i32 gridY</c>. Same identity scheme as
    /// <see cref="RepairFacilityAction"/>: FacilityId primary, grid position fallback.
    ///
    /// Deliberately NOT IHostOnlyApply, and NOT reward-suppressed — the client MUST replay this.
    /// <c>GeoPhoenixFacility.CompleteFacility()</c> (decompile GeoPhoenixFacility.cs:347) is
    /// <c>_health = 100; SetFacilityFunctioning()</c> → <c>State = Functioning</c>: a PURELY STRUCTURAL
    /// flip to operational, with NO reward side-effect (no wallet, no item grant, no resource). There is
    /// NO facility state channel, so nothing else converges the client's facility to "built" — suppressing
    /// the replay would leave the client's facility stuck under-construction. Since there is nothing to
    /// double-apply, replaying CompleteFacility on the client is both safe and necessary. (Verified
    /// reward-free 2026-06-16; if a future game/TFTV change adds a reward to CompleteFacility this must be
    /// revisited — suppress only the reward part, keep the structural flip.)
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
