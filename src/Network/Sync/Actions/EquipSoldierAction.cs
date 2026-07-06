using System;
using System.IO;
using Multiplayer.Network.Sync.State;

namespace Multiplayer.Network.Sync.Actions
{
    /// <summary>
    /// PS4 client-edit intent: EQUIP a soldier (full-loadout replace — the three GeoItem lists). A co-op
    /// client's sim is frozen; its local <c>GeoCharacter.SetItems</c> is suppressed and this intent is
    /// relayed to the host, which runs the authoritative SetItems; the resulting loadout mirrors back on the
    /// #9 personnel channel (whole-GeoCharacter blob) to ALL clients. The initiator sees its own edit only
    /// once it round-trips (canon: client = display-only). <see cref="IHostOnlyApply"/> → the client never
    /// replays the action.
    /// <para>Wire: <c>i64 unitId</c>, then three guid lists (armour/bodypart, equipment, inventory) each
    /// <c>u16 count</c> + guids. Item fidelity is def-level (a fresh <c>new GeoItem(def)</c>, the game's own
    /// reconstruction GeoCharacter.cs:1620); exact ammo/charges converge via the authoritative #9 blob.</para>
    /// </summary>
    public sealed class EquipSoldierAction : ISyncedAction, IHostOnlyApply
    {
        private readonly long _unitId;
        private readonly string[] _armour;
        private readonly string[] _equipment;
        private readonly string[] _inventory;

        public EquipSoldierAction(long unitId, string[] armour, string[] equipment, string[] inventory)
        {
            _unitId = unitId;
            _armour = armour ?? Array.Empty<string>();
            _equipment = equipment ?? Array.Empty<string>();
            _inventory = inventory ?? Array.Empty<string>();
        }

        public long UnitId => _unitId;
        public string[] Armour => _armour;
        public string[] Equipment => _equipment;
        public string[] Inventory => _inventory;

        public ushort ActionId => SyncedActionIds.EquipSoldier;
        public ActionCategory Category => ActionCategory.Equip;

        public void Write(BinaryWriter w)
        {
            w.Write(_unitId);
            PersonnelActionWire.WriteGuids(w, _armour);
            PersonnelActionWire.WriteGuids(w, _equipment);
            PersonnelActionWire.WriteGuids(w, _inventory);
        }

        public static ISyncedAction Read(BinaryReader r)
        {
            long unitId = r.ReadInt64();
            var armour = PersonnelActionWire.ReadGuids(r);
            var equipment = PersonnelActionWire.ReadGuids(r);
            var inventory = PersonnelActionWire.ReadGuids(r);
            return new EquipSoldierAction(unitId, armour, equipment, inventory);
        }

        public bool Validate(GeoRuntime rt, Guid actor)
            => rt != null && rt.IsGeoscapeActive && PersonnelEditReflection.OwnsSoldier(actor, _unitId);

        public void Apply(GeoRuntime rt)
            => PersonnelEditReflection.Equip(rt, _unitId, _armour, _equipment, _inventory);
    }
}
