namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Pure kernel of the thin-client stat-edit HOST gate: the EFFECTIVE base-stat value the native stat editor
    /// prices + caps against. Native <c>UIModuleCharacterProgression.ChangeCharacterStat</c> (:875-933) and
    /// <c>SetStatButtonInteractabilty</c> (:774-810) never read <c>CharacterProgression.GetBaseStat</c> (the raw
    /// <c>_baseStats</c> ALLOCATION array) — they read the buffer seeded in <c>RefreshStats</c> (decompile
    /// UIModuleCharacterProgression.cs:516-518):
    ///   <c>_current*Stat = (int)(GetProgressionBaseStats().&lt;attr&gt; + character.Bonus&lt;attr&gt;)</c>
    /// = bodypart contributions + allocated points + item/mutation bonus (GeoCharacter.cs:1167-1183) — far larger
    /// than the raw allocation alone. Pricing/capping the host apply on the raw allocation (the pre-fix bug) fed
    /// <c>GetBaseStatCost</c>/<c>CanModifyBaseStat</c> a value ~10-30x too small → near-zero SP cost (wrong amount
    /// charged) and a cap that never triggered (infinite upgrade). The host must feed THIS value to the native
    /// GetBaseStatCost/CanModifyBaseStat calls. The <c>(int)</c> is a TRUNCATION (matches the native cast), never
    /// a round.
    /// </summary>
    public static class StatEditGate
    {
        /// <summary>Native effective base stat = <c>(int)(baseAttr + bonus)</c> — the exact RefreshStats:516-518
        /// frame. <paramref name="baseAttr"/> = <c>GetProgressionBaseStats().&lt;Endurance|Willpower|Speed&gt;</c>,
        /// <paramref name="bonus"/> = <c>GeoCharacter.&lt;BonusStrength|BonusWillpower|BonusSpeed&gt;</c>.</summary>
        public static int EffectiveStat(float baseAttr, float bonus) => (int)(baseAttr + bonus);
    }
}
