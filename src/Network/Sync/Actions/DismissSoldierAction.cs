using System;
using System.IO;
using Multiplayer.Network.Sync.State;

namespace Multiplayer.Network.Sync.Actions
{
    /// <summary>
    /// PS4 client-edit intent: DISMISS a soldier. The host runs the authoritative
    /// <c>GeoFaction.KillCharacter(soldier, CharacterDeathReason.Dismissed)</c> (the exact dismiss call,
    /// UIStateEditSoldier.cs:425); the soldier's removal mirrors on #9/#6 (membership). <see cref="IHostOnlyApply"/>.
    /// Category ControlSoldiers → the actor must own this soldier (Validate). Wire: <c>i64 unitId</c>.
    /// </summary>
    public sealed class DismissSoldierAction : ISyncedAction, IHostOnlyApply
    {
        private readonly long _unitId;

        public DismissSoldierAction(long unitId) => _unitId = unitId;

        public long UnitId => _unitId;

        public ushort ActionId => SyncedActionIds.DismissSoldier;
        public ActionCategory Category => ActionCategory.ControlSoldiers;

        public void Write(BinaryWriter w) => w.Write(_unitId);

        public static ISyncedAction Read(BinaryReader r) => new DismissSoldierAction(r.ReadInt64());

        public bool Validate(GeoRuntime rt, Guid actor)
            => rt != null && rt.IsGeoscapeActive && PersonnelEditReflection.OwnsSoldier(actor, _unitId);

        public void Apply(GeoRuntime rt)
            => PersonnelEditReflection.Dismiss(rt, _unitId);
    }
}
