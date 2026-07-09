using System;
using System.IO;
using Multiplayer.Network.Sync.State;

namespace Multiplayer.Network.Sync.Actions
{
    /// <summary>
    /// PS4 client-edit intent: AUGMENT a soldier (mutation/bionic install). The payload is the pure INTENT —
    /// the CHOSEN augment's stable def guid + the target soldier — never a model-derived bodypart list (the
    /// 3D-preview <c>OnAugmentClicked</c> writes SetItems into the client model BEFORE the commit, so a
    /// model-read payload carried stale/preview contamination). The host re-derives everything natively:
    /// <see cref="PersonnelEditReflection.Augment"/> runs the full <c>OnAugmentApplied</c>-equivalent chain
    /// (gates + CanSwapItem + SetItems + displaced-to-storage + Wallet.Take + statistics + SaveLoadout +
    /// TFTV parity), then the #9 blob + 0xA0 wallet snapshot mirror the authoritative result back.
    /// <see cref="IHostOnlyApply"/>; same ManageEquipment gate as equip (augment has no distinct permission
    /// bit); the separate id keeps augment intents distinguishable on the wire.
    /// Wire: <c>i64 unitId</c>, then one def guid (string). Both peers run the same DLL → the payload
    /// change from the old guid-list format needs no versioning.
    /// </summary>
    public sealed class AugmentSoldierAction : ISyncedAction, IHostOnlyApply
    {
        private readonly long _unitId;
        private readonly string _augmentGuid;

        public AugmentSoldierAction(long unitId, string augmentGuid)
        {
            _unitId = unitId;
            _augmentGuid = augmentGuid ?? string.Empty;
        }

        public long UnitId => _unitId;
        public string AugmentGuid => _augmentGuid;

        public ushort ActionId => SyncedActionIds.AugmentSoldier;
        public ActionCategory Category => ActionCategory.Equip;

        public void Write(BinaryWriter w)
        {
            w.Write(_unitId);
            w.Write(_augmentGuid);
        }

        public static ISyncedAction Read(BinaryReader r)
        {
            long unitId = r.ReadInt64();
            string augmentGuid = r.ReadString();
            return new AugmentSoldierAction(unitId, augmentGuid);
        }

        public bool Validate(GeoRuntime rt, Guid actor)
            => rt != null && rt.IsGeoscapeActive && PersonnelEditReflection.OwnsSoldier(actor, _unitId);

        public void Apply(GeoRuntime rt)
            => PersonnelEditReflection.Augment(rt, _unitId, _augmentGuid);
    }
}
