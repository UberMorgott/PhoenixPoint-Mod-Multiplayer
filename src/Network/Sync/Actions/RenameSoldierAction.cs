using System;
using System.IO;
using Multiplayer.Network.Sync.State;

namespace Multiplayer.Network.Sync.Actions
{
    /// <summary>
    /// PS4 client-edit intent: RENAME a soldier. The host runs the authoritative
    /// <c>GeoCharacter.Rename(newName)</c> (GeoCharacter.cs:826, writes Identity.Name); the new name mirrors on
    /// #9 (the soldier's live-state blob carries Identity). <see cref="IHostOnlyApply"/>. Category
    /// ControlSoldiers → the actor must own this soldier (Validate). Wire: <c>i64 unitId, string newName</c>.
    /// </summary>
    public sealed class RenameSoldierAction : ISyncedAction, IHostOnlyApply
    {
        private readonly long _unitId;
        private readonly string _newName;

        public RenameSoldierAction(long unitId, string newName)
        {
            _unitId = unitId;
            _newName = newName ?? string.Empty;
        }

        public long UnitId => _unitId;
        public string NewName => _newName;

        public ushort ActionId => SyncedActionIds.RenameSoldier;
        public ActionCategory Category => ActionCategory.ControlSoldiers;

        public void Write(BinaryWriter w)
        {
            w.Write(_unitId);
            w.Write(_newName);
        }

        public static ISyncedAction Read(BinaryReader r)
        {
            long unitId = r.ReadInt64();
            string newName = r.ReadString();
            return new RenameSoldierAction(unitId, newName);
        }

        public bool Validate(GeoRuntime rt, Guid actor)
            => rt != null && rt.IsGeoscapeActive && PersonnelEditReflection.OwnsSoldier(actor, _unitId);

        public void Apply(GeoRuntime rt)
            => PersonnelEditReflection.RenameSoldier(rt, _unitId, _newName);
    }
}
