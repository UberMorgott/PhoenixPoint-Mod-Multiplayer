using System;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// PURE optional-tail record of one HAVEN site's display state — WA-2 of the 2026-07-05 popup-mirror spec
    /// §5 (audit gap 4d, GeoHaven.cs:172/213). Carried on the versioned EXTRAS BLOCK of
    /// <see cref="GeoSiteSnapshot"/> (null = not carried: non-haven site, older payload, or host read miss —
    /// NEVER a clear; a haven never stops being a haven).
    ///   • <c>Population</c> — <c>GeoHaven._population</c> (GeoHaven.cs:144, public <c>Population</c> prop
    ///     :172). Host reads the property; the client writes the BACKING FIELD directly: the native setter
    ///     cascades (OnPopulationChanged → ZonesStats.UpdateZonesStats/UpdateRange, and 0 → Site.DestroySite —
    ///     SIM on the frozen client).
    ///   • <c>Infested</c> — <c>GeoHaven.IsInfested</c> (GeoHaven.cs:213) is DERIVED
    ///     (<c>Site.Owner.IsAlienFaction</c>), so the identity mirror's Owner write already flips it
    ///     client-side; the flag is carried as the host-authoritative record (divergence diagnostic +
    ///     wire pin), not stamped.
    /// Zone attrition (HavenPopulationZoneAttrition, GeoMap.cs:281) is a DIRTY TRIGGER only: per-zone health
    /// is NOT carried (spec tail omits it — accepted cosmetic); the re-snapshot refreshes population/state.
    /// </summary>
    public sealed class GeoHavenTail : IEquatable<GeoHavenTail>
    {
        public readonly int Population;
        public readonly bool Infested;

        public GeoHavenTail(int population, bool infested)
        {
            Population = population;
            Infested = infested;
        }

        public bool Equals(GeoHavenTail other)
            => other != null && Population == other.Population && Infested == other.Infested;

        public override bool Equals(object obj) => obj is GeoHavenTail o && Equals(o);

        public override int GetHashCode()
        {
            unchecked { return (Population * 397) ^ (Infested ? 1 : 0); }
        }

        public override string ToString() => $"Haven(pop={Population} infested={Infested})";
    }

    /// <summary>
    /// PURE optional-tail record of one ALIEN-BASE site's display state — WA-2 (audit gap 4b,
    /// GeoAlienBase.cs:154/479). Null = not carried (non-alien-base site / older payload / read miss).
    ///   • <c>TypeDefGuid</c> — <c>GeoAlienBase.AlienBaseTypeDef</c> guid (auto-prop, private set —
    ///     GeoAlienBase.cs:62; promoted by <c>ChangeAlienBaseType</c> :167-197 which fires
    ///     <c>SiteAlienBaseTypeChanged</c>). Client stamps via the compiler-generated setter — value-only,
    ///     no <c>ChangeAlienBaseType</c> cascade (ActivateBase/CreateAlienBaseMission/SpawnMonster = SIM).
    ///     "" = host couldn't read the def → client skips (never nulls a live type).
    ///   • <c>AddonDefGuids</c> — <c>GeoSite._addons</c> (HashSet&lt;GeoSiteAddonDef&gt;, GeoSite.cs:63,
    ///     AddonsChanged :452/:463 → GeoMap.SiteAddonsChanged :257). Always carried with the tail (empty =
    ///     honest clear, last-wins); client rewrites the set value-only (no AddonsChanged re-raise).
    /// </summary>
    public sealed class GeoAlienBaseTail : IEquatable<GeoAlienBaseTail>
    {
        public readonly string TypeDefGuid;
        public readonly string[] AddonDefGuids;   // never null (normalized to empty)

        public GeoAlienBaseTail(string typeDefGuid, string[] addonDefGuids = null)
        {
            TypeDefGuid = typeDefGuid ?? "";
            AddonDefGuids = addonDefGuids ?? new string[0];
        }

        public bool Equals(GeoAlienBaseTail other)
        {
            if (other == null || TypeDefGuid != other.TypeDefGuid
                || AddonDefGuids.Length != other.AddonDefGuids.Length) return false;
            for (int i = 0; i < AddonDefGuids.Length; i++)
                if (AddonDefGuids[i] != other.AddonDefGuids[i]) return false;
            return true;
        }

        public override bool Equals(object obj) => obj is GeoAlienBaseTail o && Equals(o);

        public override int GetHashCode()
        {
            unchecked { return ((TypeDefGuid?.GetHashCode() ?? 0) * 397) ^ AddonDefGuids.Length; }
        }

        public override string ToString() => $"AlienBase(type={TypeDefGuid} addons={AddonDefGuids.Length})";
    }

    /// <summary>
    /// PURE optional-tail record of one site's EXCAVATION display state — WA-2 (audit gap 3c,
    /// SiteExcavationState.cs:44/62; GeoPhoenixFaction.cs:108/280-282). Null = not carried (the phoenix
    /// faction has NO excavation record for this site — never a clear; records persist once created).
    ///   • <c>Excavating</c> — true while digging (native record: <c>IsExcavated</c>=false,
    ///     <c>ExcavationEndDate</c> set); false once completed (<c>IsExcavated</c>=true, dates reset to Zero,
    ///     SiteExcavationState.cs:70-89). Client stamps <c>IsExcavated</c> = !Excavating.
    ///   • <c>EndDateTicks</c> — <c>ExcavationEndDate.TimeSpan.Ticks</c> (TimeUnit wraps a TimeSpan); 0 when
    ///     not excavating. Client stamps the date value-only — NEVER <c>Init</c>/<c>StartExcavation</c>
    ///     (Timing.Start updateable + CompleteExcavation → CreateAncientSiteMission = SIM on the mirror).
    ///     <c>GeoSite.IsExcavated()</c> (GeoSite.cs:472) then feeds the native ancient-site art via
    ///     RefreshVisuals (GeoSiteVisualsController.cs:348); rewards already ride the wallet rail.
    /// </summary>
    public sealed class GeoExcavationTail : IEquatable<GeoExcavationTail>
    {
        public readonly bool Excavating;
        public readonly long EndDateTicks;

        public GeoExcavationTail(bool excavating, long endDateTicks)
        {
            Excavating = excavating;
            EndDateTicks = endDateTicks;
        }

        public bool Equals(GeoExcavationTail other)
            => other != null && Excavating == other.Excavating && EndDateTicks == other.EndDateTicks;

        public override bool Equals(object obj) => obj is GeoExcavationTail o && Equals(o);

        public override int GetHashCode()
        {
            unchecked { return (EndDateTicks.GetHashCode() * 397) ^ (Excavating ? 1 : 0); }
        }

        public override string ToString() => $"Excavation(active={Excavating} end={EndDateTicks})";
    }

    /// <summary>
    /// PURE optional-tail record of one site's PRE-ATTACK SCHEDULE state — audit gap 6b
    /// (SiteAttackSchedule.cs:13; GeoFaction.cs:105-107/1926-1938). One entry per ARMED
    /// (<c>HasAttackScheduled</c>) schedule targeting this site, across ALL factions' PhoenixBase +
    /// AncientSite schedule lists. The client stamps the same schedule state onto its own faction lists
    /// (ScheduledAt + NextUpdateAttack — VALUE-ONLY, never RescheduleAttack: no Timing producer, mission
    /// creation stays host-only and arrives via the ch#5 mission record) and re-raises the native
    /// <c>SiteAttackScheduled</c> event so GeoscapeLog renders the vanilla warning toast + status-bar
    /// countdown (GeoscapeLog.cs:446-476) — or TFTV's prefix suppresses it, exactly as on the host.
    /// Carriage: null = not carried (host has NO schedule ENTRY for this site — nothing ever armed);
    /// an EMPTY Entries array = honest clear (entries exist but none armed — fired/expired attack).
    /// AttackerFactionDefGuid is the owning <c>GeoFaction.Def</c> guid (same resolve as the Owner mirror).
    /// </summary>
    public sealed class GeoAttackEntry : IEquatable<GeoAttackEntry>
    {
        public readonly string AttackerFactionDefGuid;
        public readonly long ScheduledAtTicks;    // SiteAttackSchedule.ScheduledAt (TimeUnit.TimeSpan.Ticks)
        public readonly long ScheduledForTicks;   // SiteAttackSchedule.ScheduledFor (NextUpdateAttack.NextTime)

        public GeoAttackEntry(string attackerFactionDefGuid, long scheduledAtTicks, long scheduledForTicks)
        {
            AttackerFactionDefGuid = attackerFactionDefGuid ?? "";
            ScheduledAtTicks = scheduledAtTicks;
            ScheduledForTicks = scheduledForTicks;
        }

        public bool Equals(GeoAttackEntry other)
            => other != null && AttackerFactionDefGuid == other.AttackerFactionDefGuid
               && ScheduledAtTicks == other.ScheduledAtTicks && ScheduledForTicks == other.ScheduledForTicks;

        public override bool Equals(object obj) => obj is GeoAttackEntry o && Equals(o);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = AttackerFactionDefGuid?.GetHashCode() ?? 0;
                h = (h * 397) ^ ScheduledAtTicks.GetHashCode();
                h = (h * 397) ^ ScheduledForTicks.GetHashCode();
                return h;
            }
        }

        public override string ToString()
            => $"AttackBy({AttackerFactionDefGuid} at={ScheduledAtTicks} for={ScheduledForTicks})";
    }

    /// <summary>See <see cref="GeoAttackEntry"/> — the per-site tail wrapper (Entries never null).</summary>
    public sealed class GeoAttackTail : IEquatable<GeoAttackTail>
    {
        public readonly GeoAttackEntry[] Entries;   // never null (empty = honest clear, last-wins)

        public GeoAttackTail(GeoAttackEntry[] entries = null)
        {
            Entries = entries ?? new GeoAttackEntry[0];
        }

        public bool Equals(GeoAttackTail other)
        {
            if (other == null || Entries.Length != other.Entries.Length) return false;
            for (int i = 0; i < Entries.Length; i++)
                if (!Entries[i].Equals(other.Entries[i])) return false;
            return true;
        }

        public override bool Equals(object obj) => obj is GeoAttackTail o && Equals(o);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = Entries.Length;
                for (int i = 0; i < Entries.Length; i++) h = (h * 397) ^ Entries[i].GetHashCode();
                return h;
            }
        }

        public override string ToString() => $"Attack(n={Entries.Length})";
    }

    /// <summary>
    /// PURE optional-tail record of one site's WEATHER (audit gap 6f; <c>GeoSite._weather</c>, enum
    /// <c>GeoSiteWeather</c> {None=0, Clear=1, Mist=2, Overcast=3, Storm=4}, GeoSite.cs:61/201/869). The host
    /// re-rolls per-site weather once an in-game hour (<c>GeoLevelController</c>:894 <c>DetermineWeather</c>
    /// over <c>_updateSites</c>); the sim-frozen client never re-rolls, so its weather DRIFTS from the host
    /// after join. Carried ONLY when weather is non-Clear, so a null tail on a snapshotted site means "host
    /// weather is Clear" and the client RESETS to Clear — this keeps the no-tail wire byte-identical for the
    /// common Clear case (the WA-2 byte-identical pins hold). Client stamps the <c>GeoSite._weather</c>
    /// backing field value-only (no PropertyChanged cascade) — pure display mirror.
    /// </summary>
    public sealed class GeoWeatherTail : IEquatable<GeoWeatherTail>
    {
        public readonly byte Weather;   // raw GeoSiteWeather enum value (never Clear=1 on a live record)

        public GeoWeatherTail(byte weather) { Weather = weather; }

        public bool Equals(GeoWeatherTail other) => other != null && Weather == other.Weather;
        public override bool Equals(object obj) => obj is GeoWeatherTail o && Equals(o);
        public override int GetHashCode() => Weather;
        public override string ToString() => $"Weather({Weather})";
    }

    /// <summary>
    /// PURE optional-tail record of one site's EXPIRING-TIMER countdown (<c>GeoSite._expiringTimerAt</c>,
    /// TimeUnit; GeoSite.cs:121/1498). Set host-side by an expiring-encounter site or TFTV's pandoran
    /// base-attack countdown (TFTVBaseDefenseGeoscape) and read by <c>GeoSiteVisualsController</c> for the
    /// ticking status-bar timer; the sim-frozen client never derives it. Carried ONLY when the timer is armed
    /// (ticks != 0), so a null tail on a snapshotted site means "host timer is Zero" and the client CLEARS it —
    /// the no-tail wire stays byte-identical for the common no-timer case. Client stamps the
    /// <c>GeoSite._expiringTimerAt</c> backing field value-only; TFTV-absent → the field is never armed
    /// host-side → the tail is never carried (no-op).
    /// </summary>
    public sealed class GeoExpiringTimerTail : IEquatable<GeoExpiringTimerTail>
    {
        public readonly long ExpiringTicks;   // ExpiringTimerAt.TimeSpan.Ticks (0 = no timer; never 0 on a live record)

        public GeoExpiringTimerTail(long expiringTicks) { ExpiringTicks = expiringTicks; }

        public bool Equals(GeoExpiringTimerTail other) => other != null && ExpiringTicks == other.ExpiringTicks;
        public override bool Equals(object obj) => obj is GeoExpiringTimerTail o && Equals(o);
        public override int GetHashCode() => ExpiringTicks.GetHashCode();
        public override string ToString() => $"ExpiringTimer({ExpiringTicks})";
    }
}
