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
    /// <para>Wire: <c>i64 unitId</c>, three guid lists (armour/bodypart, equipment, inventory) each
    /// <c>u16 count</c> + guids, then <c>bool returnRemovedToStorage</c>. Item fidelity is def-level (a fresh
    /// <c>new GeoItem(def)</c>, the game's own reconstruction GeoCharacter.cs:1620); exact ammo/charges converge
    /// via the authoritative #9 blob.</para>
    /// <para>STORAGE (v2 rebuild): the host derives the shared-storage change from the AUTHORITATIVE loadout
    /// delta (soldier's old lists → these new lists), NEVER from a client storage snapshot — client never
    /// simulates, host is the one writer. Items the soldier GAINED come out of faction storage; items it LOST go
    /// back IN, gated by <see cref="ReturnRemovedToStorage"/> — true for equip/unequip/side-button/loadout moves,
    /// FALSE for scrap (a scrapped item is destroyed, not returned; else the delta would dupe it into storage).</para>
    /// </summary>
    public sealed class EquipSoldierAction : ISyncedAction, IHostOnlyApply
    {
        private readonly long _unitId;
        private readonly string[] _armour;
        private readonly string[] _equipment;
        private readonly string[] _inventory;
        private readonly bool _returnRemovedToStorage;

        public EquipSoldierAction(long unitId, string[] armour, string[] equipment, string[] inventory,
                                  bool returnRemovedToStorage = true)
        {
            _unitId = unitId;
            _armour = armour ?? Array.Empty<string>();
            _equipment = equipment ?? Array.Empty<string>();
            _inventory = inventory ?? Array.Empty<string>();
            _returnRemovedToStorage = returnRemovedToStorage;
        }

        public long UnitId => _unitId;
        public string[] Armour => _armour;
        public string[] Equipment => _equipment;
        public string[] Inventory => _inventory;
        public bool ReturnRemovedToStorage => _returnRemovedToStorage;

        public ushort ActionId => SyncedActionIds.EquipSoldier;
        public ActionCategory Category => ActionCategory.Equip;

        public void Write(BinaryWriter w)
        {
            w.Write(_unitId);
            PersonnelActionWire.WriteGuids(w, _armour);
            PersonnelActionWire.WriteGuids(w, _equipment);
            PersonnelActionWire.WriteGuids(w, _inventory);
            w.Write(_returnRemovedToStorage);
        }

        public static ISyncedAction Read(BinaryReader r)
        {
            long unitId = r.ReadInt64();
            var armour = PersonnelActionWire.ReadGuids(r);
            var equipment = PersonnelActionWire.ReadGuids(r);
            var inventory = PersonnelActionWire.ReadGuids(r);
            bool returnRemovedToStorage = r.ReadBoolean();
            return new EquipSoldierAction(unitId, armour, equipment, inventory, returnRemovedToStorage);
        }

        public bool Validate(GeoRuntime rt, Guid actor)
            => rt != null && rt.IsGeoscapeActive && PersonnelEditReflection.OwnsSoldier(actor, _unitId);

        public void Apply(GeoRuntime rt)
            => PersonnelEditReflection.Equip(rt, _unitId, _armour, _equipment, _inventory, _returnRemovedToStorage);
    }
}
