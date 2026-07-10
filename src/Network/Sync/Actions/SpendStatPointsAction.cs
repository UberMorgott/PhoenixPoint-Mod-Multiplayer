using System;
using System.IO;
using Multiplayer.Network.Sync.State;

namespace Multiplayer.Network.Sync.Actions
{
    /// <summary>
    /// Progression client intent: spend/refund skill points on one base stat (Strength/Will/Speed — the
    /// soldier-edit stat +/- buttons). Relayed PER CLICK at the native chokepoint
    /// <c>UIModuleCharacterProgression.ChangeCharacterStat</c> (the +/- button handlers, decompile
    /// UIModuleCharacterProgression.cs:848/857/866 → :875) so each click is instantly reactive on every
    /// peer's open panel — not deferred to the CommitStatChanges soldier-switch seam. The wire carries only
    /// the SIGNED per-click delta (+1 = a plus click, −1 = a minus/refund click), never the client's SP
    /// arithmetic: the host re-derives each point's native cost (<c>GetBaseStatCost</c>) step by step, gates
    /// on <c>CanModifyBaseStat</c> + the combined soldier/faction pool (<see cref="ProgressionSpend"/>) for a
    /// spend, and on the per-session net-applied ledger (<c>StatRefundTracker</c> — no farming free SP) for a
    /// refund, then applies via native ModifyBaseStat (± symmetric price). Result mirrors on the #9 live-state
    /// blob (progression _baseStats + SkillPoints ride the GeoCharacter snapshot). <see cref="IHostOnlyApply"/>.
    /// Category ControlSoldiers → per-soldier ownership gate (PS4 policy). Wire: <c>i64 unitId,
    /// u8 statId (CharacterBaseAttribute), i32 delta (signed)</c> — the i32 was already signed on the wire, so
    /// allowing a negative delta is codec back-compatible (no format bump).
    /// </summary>
    public sealed class SpendStatPointsAction : ISyncedAction, IHostOnlyApply
    {
        /// <summary>Sanity cap on a single intent's delta (native stat maxima are ~35; a runaway value
        /// would spin the host apply loop).</summary>
        public const int MaxDelta = 100;

        private readonly long _unitId;
        private readonly byte _statId;
        private readonly int _delta;

        public SpendStatPointsAction(long unitId, byte statId, int delta)
        {
            _unitId = unitId;
            _statId = statId;
            _delta = delta;
        }

        public long UnitId => _unitId;
        public byte StatId => _statId;
        public int Delta => _delta;

        public ushort ActionId => SyncedActionIds.SpendStatPoints;
        public ActionCategory Category => ActionCategory.ControlSoldiers;

        public void Write(BinaryWriter w)
        {
            w.Write(_unitId);
            w.Write(_statId);
            w.Write(_delta);
        }

        public static ISyncedAction Read(BinaryReader r)
        {
            long unitId = r.ReadInt64();
            byte statId = r.ReadByte();
            int delta = r.ReadInt32();
            return new SpendStatPointsAction(unitId, statId, delta);
        }

        public bool Validate(GeoRuntime rt, Guid actor)
            => rt != null && rt.IsGeoscapeActive
               && _statId <= 2                         // CharacterBaseAttribute: Strength/Will/Speed
               && _delta != 0 && _delta >= -MaxDelta && _delta <= MaxDelta   // signed: +spend / −refund (host re-prices + ledger-bounds)
               && PersonnelEditReflection.OwnsSoldier(actor, _unitId);

        public void Apply(GeoRuntime rt)
            => PersonnelEditReflection.SpendStatPoints(rt, _unitId, _statId, _delta);
    }
}
