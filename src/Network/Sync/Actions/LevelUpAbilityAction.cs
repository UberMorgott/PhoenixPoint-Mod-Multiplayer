using System;
using System.IO;
using Multiplayer.Network.Sync.State;

namespace Multiplayer.Network.Sync.Actions
{
    /// <summary>
    /// Progression client intent: BUY an ability on a soldier's progression track with skill points (the
    /// soldier-edit screen buy button — <c>UIModuleCharacterProgression.BuyAbility</c> →
    /// <c>CharacterProgression.LearnAbility</c>, UIModuleCharacterProgression.cs:389/416). The host
    /// re-validates natively (<c>CanLearnAbility</c> + <c>GetAbilitySlotCost</c> against the combined
    /// soldier-SP + faction-SP pool, the <see cref="ProgressionSpend"/> split) and applies the native
    /// LearnAbility; the learned ability + spent SP mirror back on the #9 live-state blob
    /// (<c>CharacterProgression</c> is a [SerializeMember] of the GeoCharacter snapshot).
    /// <see cref="IHostOnlyApply"/>. Category ControlSoldiers → per-soldier ownership gate (the PS4
    /// rename/dismiss/transfer policy). The slot is keyed (trackSource, slotIndex) with the ability-def
    /// guid as drift fingerprint (TFTV-added defs ride the same guid path); an empty PERSONAL-track slot
    /// takes the relayed def (the BuyAbility null-slot mirror, :393-396).
    /// Wire: <c>i64 unitId, u8 trackSource, i32 slotIndex, string abilityGuid</c>.
    /// </summary>
    public sealed class LevelUpAbilityAction : ISyncedAction, IHostOnlyApply
    {
        private readonly long _unitId;
        private readonly byte _trackSource;
        private readonly int _slotIndex;
        private readonly string _abilityGuid;

        public LevelUpAbilityAction(long unitId, byte trackSource, int slotIndex, string abilityGuid)
        {
            _unitId = unitId;
            _trackSource = trackSource;
            _slotIndex = slotIndex;
            _abilityGuid = abilityGuid ?? string.Empty;
        }

        public long UnitId => _unitId;
        public byte TrackSource => _trackSource;
        public int SlotIndex => _slotIndex;
        public string AbilityGuid => _abilityGuid;

        public ushort ActionId => SyncedActionIds.LevelUpAbility;
        public ActionCategory Category => ActionCategory.ControlSoldiers;

        public void Write(BinaryWriter w)
        {
            w.Write(_unitId);
            w.Write(_trackSource);
            w.Write(_slotIndex);
            w.Write(_abilityGuid);
        }

        public static ISyncedAction Read(BinaryReader r)
        {
            long unitId = r.ReadInt64();
            byte trackSource = r.ReadByte();
            int slotIndex = r.ReadInt32();
            string abilityGuid = r.ReadString();
            return new LevelUpAbilityAction(unitId, trackSource, slotIndex, abilityGuid);
        }

        public bool Validate(GeoRuntime rt, Guid actor)
            => rt != null && rt.IsGeoscapeActive
               && _slotIndex >= 0 && !string.IsNullOrEmpty(_abilityGuid)
               && PersonnelEditReflection.OwnsSoldier(actor, _unitId);

        public void Apply(GeoRuntime rt)
            => PersonnelEditReflection.LevelUpAbility(rt, _unitId, _trackSource, _slotIndex, _abilityGuid);
    }
}
