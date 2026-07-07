namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Pure skill-point spend splitter for the progression intents (LevelUpAbility / SpendStatPoints).
    /// Mirrors the native purchase pool EXACTLY (<c>UIModuleCharacterProgression.ConsumeAbilityCost</c>,
    /// decompile :428-442, and the stat-increase branch :892-903): the soldier's own
    /// <c>CharacterProgression.SkillPoints</c> pays first; a shortfall spills into the shared
    /// <c>GeoPhoenixFaction.Skillpoints</c> pool; the purchase is affordable iff the COMBINED pool covers
    /// the cost (the native button-interactability gate :793). All-or-nothing: an unaffordable cost
    /// mutates nothing (the host rejects the intent as a logged no-op).
    /// </summary>
    public static class ProgressionSpend
    {
        /// <summary>Split <paramref name="cost"/> across (soldier SP, faction SP). False = combined pool
        /// cannot cover it (or cost is negative) — outputs then echo the inputs unchanged.</summary>
        public static bool TrySplit(int cost, int soldierSp, int factionSp,
                                    out int newSoldierSp, out int newFactionSp)
        {
            newSoldierSp = soldierSp;
            newFactionSp = factionSp;
            if (cost < 0 || soldierSp + factionSp < cost) return false;
            if (soldierSp >= cost)
            {
                newSoldierSp = soldierSp - cost;
                return true;
            }
            newSoldierSp = 0;
            newFactionSp = factionSp - (cost - soldierSp);
            return true;
        }
    }
}
