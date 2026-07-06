using System;
using System.IO;
using Multiplayer.Network.Sync.State;

namespace Multiplayer.Network.Sync.Actions
{
    /// <summary>
    /// PS4 client-edit intent: TRANSFER a soldier between Phoenix containers (assign to / recall from a craft,
    /// move between bases). There is no native transfer method — the host runs the authoritative
    /// <c>RemoveCharacter</c>(current)+<c>AddCharacter</c>(dest) pair; the result mirrors on #9 (site roster) +
    /// #6 (vehicle crew). <see cref="IHostOnlyApply"/>. Category ControlSoldiers → the actor must own this
    /// soldier (Validate). Wire: <c>i64 unitId, i32 destKind (0=site/base,1=vehicle), i32 destId
    /// (SiteId | VehicleID)</c>; the source is resolved host-side as the soldier's current container.
    /// </summary>
    public sealed class TransferSoldierAction : ISyncedAction, IHostOnlyApply
    {
        private readonly long _unitId;
        private readonly int _destKind;
        private readonly int _destId;

        public TransferSoldierAction(long unitId, int destKind, int destId)
        {
            _unitId = unitId;
            _destKind = destKind;
            _destId = destId;
        }

        public long UnitId => _unitId;
        public int DestKind => _destKind;
        public int DestId => _destId;

        public ushort ActionId => SyncedActionIds.TransferSoldier;
        public ActionCategory Category => ActionCategory.ControlSoldiers;

        public void Write(BinaryWriter w)
        {
            w.Write(_unitId);
            w.Write(_destKind);
            w.Write(_destId);
        }

        public static ISyncedAction Read(BinaryReader r)
        {
            long unitId = r.ReadInt64();
            int destKind = r.ReadInt32();
            int destId = r.ReadInt32();
            return new TransferSoldierAction(unitId, destKind, destId);
        }

        public bool Validate(GeoRuntime rt, Guid actor)
            => rt != null && rt.IsGeoscapeActive && PersonnelEditReflection.OwnsSoldier(actor, _unitId);

        public void Apply(GeoRuntime rt)
            => PersonnelEditReflection.Transfer(rt, _unitId, _destKind, _destId);
    }
}
