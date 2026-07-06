using System;
using System.IO;
using Multiplayer.Network.Sync.State;

namespace Multiplayer.Network.Sync.Actions
{
    /// <summary>
    /// PS4 client-edit intent: AUGMENT a soldier (body-part item swap). An augmentation rides the SAME
    /// <c>GeoCharacter.SetItems</c> as equip but touches only the armour/bodypart list (taxonomy §4), so the
    /// host applies <c>SetItems(armour: bodyparts, equipment: null, inventory: null)</c> — the null lists are
    /// left unchanged (GeoCharacter.cs:848/860). Frozen-client relay + authoritative host apply + #9 mirror,
    /// exactly like <see cref="EquipSoldierAction"/>; <see cref="IHostOnlyApply"/>. Same ManageEquipment gate
    /// (augment has no distinct permission bit); the separate id keeps augment intents distinguishable from
    /// weapon equips on the wire. Wire: <c>i64 unitId</c>, then one guid list (bodyparts).
    /// </summary>
    public sealed class AugmentSoldierAction : ISyncedAction, IHostOnlyApply
    {
        private readonly long _unitId;
        private readonly string[] _bodyparts;

        public AugmentSoldierAction(long unitId, string[] bodyparts)
        {
            _unitId = unitId;
            _bodyparts = bodyparts ?? Array.Empty<string>();
        }

        public long UnitId => _unitId;
        public string[] Bodyparts => _bodyparts;

        public ushort ActionId => SyncedActionIds.AugmentSoldier;
        public ActionCategory Category => ActionCategory.Equip;

        public void Write(BinaryWriter w)
        {
            w.Write(_unitId);
            PersonnelActionWire.WriteGuids(w, _bodyparts);
        }

        public static ISyncedAction Read(BinaryReader r)
        {
            long unitId = r.ReadInt64();
            var bodyparts = PersonnelActionWire.ReadGuids(r);
            return new AugmentSoldierAction(unitId, bodyparts);
        }

        public bool Validate(GeoRuntime rt, Guid actor)
            => rt != null && rt.IsGeoscapeActive && PersonnelEditReflection.OwnsSoldier(actor, _unitId);

        public void Apply(GeoRuntime rt)
            => PersonnelEditReflection.Augment(rt, _unitId, _bodyparts);
    }
}
