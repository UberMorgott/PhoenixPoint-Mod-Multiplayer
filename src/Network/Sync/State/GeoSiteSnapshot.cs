using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// PURE (Unity-free) mirror record of one site's <c>GeoSite.ActiveMission</c> — the P1 mission-state mirror
    /// of the unified popup-mirror design (2026-07-05 spec §P1). Carried as an OPTIONAL tail of each
    /// <see cref="GeoSiteState"/> record on the GeoSite channel (#5): a <c>null</c> record = TOMBSTONE (host has
    /// no active mission on that site — cleared/cancelled/completed). The client rebuilds the SAME mission
    /// subclass on its own resolved site via the pure serializer-support ctors and stamps these runtime bits, so
    /// a later mirrored LIVE→site-id brief (ReportModalVariant.ActiveMissionBrief) binds fully natively off
    /// <c>site.ActiveMission</c>.
    ///
    /// Class discriminator values are REQUIRED because ModalType alone is ambiguous (GeoAlienBaseBrief 2 covers
    /// TWO classes whose bind hard-casts — a wrong-class rebuild throws) and the wire must stay class-exact.
    /// Only the runtime bits a brief bind actually reads are carried (decompile-verified 2026-07-05):
    ///   • HavenDefense — HavenDefenceBriefDataBind reads AttackerFaction + Attacker/DefenderDeployment +
    ///     AttackedZone (GeoHavenDefenseMission.cs:51-79); ctor's live HavenAttacker is NOT reconstructable →
    ///     carry {attackerFactionGuid, deployments, attackedZoneDefGuid}.
    ///   • AlienBaseAssault — deployments + attacker faction (GeoAlienBaseAssaultMission.cs:44-49).
    ///   • PhoenixBaseDefense — attackingSites (GeoPhoenixBaseDefenseMission.cs:27-35, `_attackingSites`) +
    ///     `_enemyFaction`; carried as site ids (resolved back on the client's own map).
    ///   • Everything else rebuilds from (site, missionDef) alone — pure base-ctors.
    /// <c>Unknown</c> (255) marks a host mission class outside the mapped set (the BehemothAttackBrief-34
    /// fallback family, GeoscapeView.cs:1751): the client NEVER attaches it — the display path degrades to the
    /// notify-only text modal instead (honest fallback, spec Batch-1 risk note).
    /// </summary>
    public sealed class GeoMissionRecord : IEquatable<GeoMissionRecord>
    {
        // ── mission-class discriminator (wire byte). 0 is RESERVED for "no mission" (a null record
        //    encodes as a lone 0 byte); it is never a valid record value. ──
        public const byte HavenDefense = 1;           // GeoHavenDefenseMission         → brief 0
        public const byte AlienBase = 2;              // GeoAlienBaseMission            → brief 2
        public const byte AlienBaseAssault = 3;       // GeoAlienBaseAssaultMission     → brief 2
        public const byte PhoenixBaseDefense = 4;     // GeoPhoenixBaseDefenseMission   → brief 11
        public const byte PhoenixBaseInfestation = 5; // GeoPhoenixBaseInfestationMission → brief 20
        public const byte InfestationCleanse = 6;     // GeoInfestationCleanseMission   → brief 36
        public const byte Scavenging = 7;             // GeoScavengingMission           → brief 4
        public const byte Ambush = 8;                 // GeoAmbushMission               → brief 15
        public const byte AncientSite = 9;            // GeoAncientSiteMission          → brief 26/28
        public const byte Unknown = 255;              // unmapped class (fallback-34 family) — never rebuilt

        public readonly byte MissionClass;            // discriminator above (never 0 on a live record)
        public readonly string MissionDefGuid;        // TacMissionTypeDef guid (GeoMission.MissionDef)
        public readonly string AttackerFactionDefGuid;// PPFactionDef guid (HavenDefense/AlienBaseAssault attacker; PhoenixBaseDefense _enemyFaction)
        public readonly int AttackerDeployment;       // HavenDefense / AlienBaseAssault strength numbers
        public readonly int DefenderDeployment;
        public readonly string AttackedZoneDefGuid;   // GeoHavenZoneDef guid (HavenDefense _attackedZoneDef)
        public readonly int[] AttackingSiteIds;       // PhoenixBaseDefense/HavenDefense attacking-site ids (may be empty)

        public GeoMissionRecord(byte missionClass, string missionDefGuid,
                                string attackerFactionDefGuid = null, int attackerDeployment = 0,
                                int defenderDeployment = 0, string attackedZoneDefGuid = null,
                                int[] attackingSiteIds = null)
        {
            MissionClass = missionClass;
            MissionDefGuid = missionDefGuid ?? "";
            AttackerFactionDefGuid = attackerFactionDefGuid ?? "";
            AttackerDeployment = attackerDeployment;
            DefenderDeployment = defenderDeployment;
            AttackedZoneDefGuid = attackedZoneDefGuid ?? "";
            AttackingSiteIds = attackingSiteIds ?? new int[0];
        }

        public bool Equals(GeoMissionRecord other)
        {
            if (other == null) return false;
            if (MissionClass != other.MissionClass
                || MissionDefGuid != other.MissionDefGuid
                || AttackerFactionDefGuid != other.AttackerFactionDefGuid
                || AttackerDeployment != other.AttackerDeployment
                || DefenderDeployment != other.DefenderDeployment
                || AttackedZoneDefGuid != other.AttackedZoneDefGuid
                || AttackingSiteIds.Length != other.AttackingSiteIds.Length) return false;
            for (int i = 0; i < AttackingSiteIds.Length; i++)
                if (AttackingSiteIds[i] != other.AttackingSiteIds[i]) return false;
            return true;
        }

        public override bool Equals(object obj) => obj is GeoMissionRecord o && Equals(o);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = MissionClass;
                h = (h * 397) ^ (MissionDefGuid?.GetHashCode() ?? 0);
                h = (h * 397) ^ (AttackerFactionDefGuid?.GetHashCode() ?? 0);
                h = (h * 397) ^ AttackerDeployment;
                h = (h * 397) ^ DefenderDeployment;
                h = (h * 397) ^ (AttackedZoneDefGuid?.GetHashCode() ?? 0);
                h = (h * 397) ^ AttackingSiteIds.Length;
                return h;
            }
        }

        public override string ToString()
            => $"Mission(class={MissionClass} def={MissionDefGuid} attacker={AttackerFactionDefGuid} " +
               $"atk={AttackerDeployment} def={DefenderDeployment} zone={AttackedZoneDefGuid} sites={AttackingSiteIds.Length})";
    }

    /// <summary>What the client-side mission mirror must do with an incoming record (see
    /// <see cref="MissionMirrorDecision"/>).</summary>
    public enum MissionMirrorAction : byte
    {
        None = 0,        // no record, nothing attached → nothing to do
        Clear = 1,       // tombstone/unknown while a mirror is attached → clear ActiveMission
        KeepRefresh = 2, // same class + same def already attached → stamp runtime bits, KEEP the instance
        Rebuild = 3,     // different/absent mirror → construct class-exact mission and attach
    }

    /// <summary>
    /// PURE branch logic of <c>GeoSiteReflection.ApplyMission</c> (unit-testable without game types): a
    /// tombstone (null record) or an Unknown/0 class NEVER attaches; an already-mirrored same-class/same-def
    /// mission is refreshed IN PLACE (the queued brief may hold the instance — and the ongoing haven-defense /
    /// assault deployments tick down hourly on the host, so re-applies MUST land on the existing mirror);
    /// anything else rebuilds class-exact.
    /// </summary>
    public static class MissionMirrorDecision
    {
        public static MissionMirrorAction Decide(bool hasCurrent, byte currentClass, string currentDefGuid,
                                                 GeoMissionRecord rec)
        {
            if (rec == null || rec.MissionClass == GeoMissionRecord.Unknown || rec.MissionClass == 0)
                return hasCurrent ? MissionMirrorAction.Clear : MissionMirrorAction.None;
            if (hasCurrent && currentClass == rec.MissionClass && currentDefGuid == rec.MissionDefGuid)
                return MissionMirrorAction.KeepRefresh;
            return MissionMirrorAction.Rebuild;
        }
    }

    /// <summary>
    /// PURE decision data: the <c>GeoMap</c> aggregate events the channel-#5 dirty subscription binds
    /// (<c>GeoSiteReflection.Subscribe</c> iterates this list; unit tests pin it — the reflection binding
    /// itself needs live game types). Every event carries the changed SITE CARRIER as arg 0 (the GeoSite,
    /// or the GeoHaven for the WA-2 haven family — unwrapped via <c>GeoSiteReflection.GetOwningSiteId</c>).
    /// </summary>
    public static class GeoSiteDirtyEvents
    {
        // SiteAdded/SiteRemoved bound for symmetry; SiteFirstTimeVisited covers the Visited-only flip;
        // SiteMissionStarted/Ended/Cancelled drive the P1 ActiveMission mirror (GeoMap.cs:263-277).
        // WA-2 HAVEN family (GeoMap.cs:279-283): HavenPopulationChanged (void(GeoHaven,int,int)),
        // HavenPopulationZoneAttrition (void(GeoHaven, GeoHavenZone) — DIRTY TRIGGER only, per-zone health
        // not carried) and HavenInfestationStateChanged (Action<GeoHaven>) drive the haven tail.
        // WA-2 commit 2: SiteAddonsChanged (GeoMap.cs:257, void(GeoSite, GeoSiteAddonDef, bool)) +
        // SiteAlienBaseTypeChanged (GeoMap.cs:287, void(GeoSite, GeoAlienBaseTypeDef, GeoAlienBaseTypeDef))
        // drive the alien-base tail. Both carry the GeoSite as arg 0.
        public static readonly string[] GeoMapEventNames =
        {
            "SiteOwnerChanged", "SiteStateChanged", "SiteVisibilityChanged",
            "SiteInspectedChanged", "SiteFirstTimeVisited", "SiteAdded", "SiteRemoved",
            "SiteMissionStarted", "SiteMissionEnded", "SiteMissionCancelled",
            "HavenPopulationChanged", "HavenPopulationZoneAttrition", "HavenInfestationStateChanged",
            "SiteAddonsChanged", "SiteAlienBaseTypeChanged",
        };

        // WA-2 excavation dirty triggers (gap 3c): GeoPhoenixFaction.OnExcavationStarted/OnExcavationCompleted
        // (GeoPhoenixFaction.cs:280-282). Both are ExcavationCompletedHanlder(GeoPhoenixFaction faction,
        // SiteExcavationState excavation) — the SITE CARRIER is arg 1 (unwrapped via SiteExcavationState.Site).
        public static readonly string[] PhoenixFactionEventNames =
        {
            "OnExcavationStarted", "OnExcavationCompleted",
        };

        // Attack-schedule dirty trigger (audit gap 6b): GeoFaction.SiteAttackScheduled
        // (FactionSiteAttackHandler(GeoFaction, SiteAttackSchedule), GeoFaction.cs:319; raised by
        // ScheduleAttackOnSite :1932 and AttackAncientSite :532). Subscribed on EVERY faction (any faction —
        // alien or human — can arm a pre-attack countdown); the SITE CARRIER is arg 1 (unwrapped via
        // SiteAttackSchedule.Site). The attack FIRING needs no own trigger: mission creation dirties the
        // site via SiteMissionStarted and the re-snapshot then carries the now-disarmed (empty) tail.
        public static readonly string[] GeoFactionEventNames =
        {
            "SiteAttackScheduled",
        };
    }

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
    /// One site's mirrored IDENTITY: the fields the geoscape-event card / native art collection reads off
    /// <c>Context.Site</c> (Owner / Type / State / EncounterID, plus the site name loc-key for token text).
    /// A pure value type with structural equality so the codec round-trip is directly assertable.
    ///
    /// <c>SiteType</c> and <c>State</c> carry the RAW enum integer value (NOT an ordinal): the game enums are
    /// sparse — <c>GeoSiteType</c> { None=0, PhoenixBase=10, … Marketplace=110 } and <c>GeoSiteState</c>
    /// { None=0, Functioning=1, Destroyed=2, Abandoned=4 } — both fit in a byte, and the client converts back
    /// via <c>Enum.ToObject(enumType, byteValue)</c>. <c>OwnerFactionDefGuid</c> is the owning
    /// <c>GeoFaction.Def.Guid</c> (resolved back to a live faction on the client). <c>SiteName</c> is the
    /// <c>LocalizedTextBind.LocalizationKey</c> the card's token replacement reads.
    ///
    /// The per-faction EXPLORED-STATE FAMILY (all read for <c>ViewerFaction</c>, the shared Phoenix faction of a
    /// co-op campaign — <c>GeoSiteFactionData</c> { Visible, Inspected, Visited }, GeoSiteFactionData.cs:12-19):
    ///   • <c>Inspected</c> — <c>GetInspected</c> (GeoSite.cs:398). A site EXPLORATION completion sets it
    ///     (<c>GeoFaction.OnVehicleSiteExplored</c> → <c>SetInspected(faction, true)</c>, GeoFaction.cs:1922);
    ///     the un-inspected map icon is the "?" marker (GeoSiteVisualsController.cs:239).
    ///   • <c>Visible</c> — <c>GetVisible</c> (GeoSite.cs:387). Exploration also REVEALS sites around the POI
    ///     (<c>UpdateVehicleSite</c> → <c>RevealAroundSite</c> → <c>SetVisible(faction, true)</c>,
    ///     GeoFaction.cs:1908 / GeoSite.cs:896-910); an invisible site renders NO marker at all
    ///     (GeoSiteVisualsController.cs:195), so without carrying it the newly revealed POIs never appear
    ///     on the sim-frozen client.
    ///   • <c>Visited</c> — <c>GetVisited</c> (GeoSite.cs:370), set on first visit/exploration
    ///     (<c>UpdateVehicleSite</c> → <c>SetVisited(faction, true)</c>, GeoFaction.cs:1907); feeds the haven
    ///     visited icon (GeoSiteVisualsController.cs:327) + FindPhoenixBase objectives.
    /// All three are per-faction display state the sim-frozen client never derives — the host reads them off the
    /// live site, the client mirrors them. Optional trailing fields (default false) so DTO callers stay stable.
    /// </summary>
    public readonly struct GeoSiteState : IEquatable<GeoSiteState>
    {
        public readonly int SiteId;
        public readonly string OwnerFactionDefGuid;
        public readonly byte SiteType;     // raw GeoSiteType enum value
        public readonly byte State;        // raw GeoSiteState enum value
        public readonly string SiteName;   // LocalizedTextBind.LocalizationKey
        public readonly string EncounterID;
        public readonly bool Inspected;    // GetInspected(ViewerFaction) — per-faction site reveal (exploration outcome)
        public readonly bool Visible;      // GetVisible(ViewerFaction) — site shown on the map at all (RevealAroundSite outcome)
        public readonly bool Visited;      // GetVisited(ViewerFaction) — first-visit flag (haven visited icon / objectives)
        public readonly GeoMissionRecord Mission; // site.ActiveMission mirror (P1); null = TOMBSTONE (no active mission)
        public readonly GeoHavenTail Haven;       // WA-2 haven tail (extras block); null = not carried (never a clear)
        public readonly GeoAlienBaseTail AlienBase;     // WA-2 alien-base tail; null = not carried
        public readonly GeoExcavationTail Excavation;   // WA-2 excavation tail; null = not carried
        public readonly GeoAttackTail Attack;           // pre-attack schedule tail (gap 6b); null = not carried

        public GeoSiteState(int siteId, string ownerFactionDefGuid, byte siteType, byte state, string siteName, string encounterID,
                            bool inspected = false, bool visible = false, bool visited = false,
                            GeoMissionRecord mission = null, GeoHavenTail haven = null,
                            GeoAlienBaseTail alienBase = null, GeoExcavationTail excavation = null,
                            GeoAttackTail attack = null)
        {
            SiteId = siteId;
            // Normalize null → "" so equality + the wire are stable (the codec also coalesces, this keeps
            // an in-memory DTO comparable to its decoded twin).
            OwnerFactionDefGuid = ownerFactionDefGuid ?? "";
            SiteType = siteType;
            State = state;
            SiteName = siteName ?? "";
            EncounterID = encounterID ?? "";
            Inspected = inspected;
            Visible = visible;
            Visited = visited;
            Mission = mission;
            Haven = haven;
            AlienBase = alienBase;
            Excavation = excavation;
            Attack = attack;
        }

        public bool Equals(GeoSiteState other)
            => SiteId == other.SiteId
               && OwnerFactionDefGuid == other.OwnerFactionDefGuid
               && SiteType == other.SiteType
               && State == other.State
               && SiteName == other.SiteName
               && EncounterID == other.EncounterID
               && Inspected == other.Inspected
               && Visible == other.Visible
               && Visited == other.Visited
               && (Mission == null ? other.Mission == null : Mission.Equals(other.Mission))
               && (Haven == null ? other.Haven == null : Haven.Equals(other.Haven))
               && (AlienBase == null ? other.AlienBase == null : AlienBase.Equals(other.AlienBase))
               && (Excavation == null ? other.Excavation == null : Excavation.Equals(other.Excavation))
               && (Attack == null ? other.Attack == null : Attack.Equals(other.Attack));

        public override bool Equals(object obj) => obj is GeoSiteState o && Equals(o);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = SiteId;
                h = (h * 397) ^ (OwnerFactionDefGuid?.GetHashCode() ?? 0);
                h = (h * 397) ^ SiteType;
                h = (h * 397) ^ State;
                h = (h * 397) ^ (SiteName?.GetHashCode() ?? 0);
                h = (h * 397) ^ (EncounterID?.GetHashCode() ?? 0);
                h = (h * 397) ^ (Inspected ? 1 : 0);
                h = (h * 397) ^ (Visible ? 2 : 0);
                h = (h * 397) ^ (Visited ? 4 : 0);
                h = (h * 397) ^ (Mission?.GetHashCode() ?? 0);
                h = (h * 397) ^ (Haven?.GetHashCode() ?? 0);
                h = (h * 397) ^ (AlienBase?.GetHashCode() ?? 0);
                h = (h * 397) ^ (Excavation?.GetHashCode() ?? 0);
                h = (h * 397) ^ (Attack?.GetHashCode() ?? 0);
                return h;
            }
        }

        public override string ToString()
            => $"Site({SiteId} owner={OwnerFactionDefGuid} type={SiteType} state={State} name={SiteName} enc={EncounterID} insp={Inspected} vis={Visible} visited={Visited} mission={(Mission == null ? "none" : Mission.ToString())} haven={(Haven == null ? "none" : Haven.ToString())} alienBase={(AlienBase == null ? "none" : AlienBase.ToString())} excav={(Excavation == null ? "none" : Excavation.ToString())} attack={(Attack == null ? "none" : Attack.ToString())})";
    }

    /// <summary>
    /// Decoded GeoSite state-replication snapshot: the identity of each CHANGED site, mirrored host→client so
    /// the client's stale (sim-frozen) GeoSite is refreshed and a geoscape-event card resolves a FRESH site
    /// (correct header/backdrop, which the native art collection derives from <c>Context.Site.Owner</c>/
    /// <c>Type</c>). This is the pure data + wire codec for the GeoSite state channel (#5) — free of any
    /// <c>IStateChannel</c>/<c>SyncEngine</c>/Unity dependency so it is directly unit-testable (mirrors
    /// <see cref="DiplomacySnapshot"/> / <see cref="ResearchSnapshot"/>).
    ///
    /// Wire payload (inside StateSync):
    ///   [u16 count]{[i32 SiteId][u16 ownerLen][ownerGuid utf8][u8 SiteType][u8 State][u16 nameLen][siteName utf8][u16 encLen][EncounterID utf8][u8 exploredFlags][mission]}*
    ///   exploredFlags bit0=Inspected bit1=Visible bit2=Visited (the per-faction explored-state family).
    ///   mission (P1 ActiveMission mirror) = [u8 missionClass]; 0 = no active mission (TOMBSTONE), else
    ///   [str missionDefGuid][str attackerFactionGuid][i32 atkDeploy][i32 defDeploy][str attackedZoneGuid][u8 nSites][i32 siteId × nSites].
    ///
    /// WA-2 EXTRAS BLOCK (versioned optional tail, ResearchSnapshot v2/v3 precedent): appended AFTER the
    /// record array, and ONLY when ≥1 site carries a tail — a no-tail payload stays byte-identical to the
    /// pre-WA-2 wire. An older decoder never reads past the record array (trailing bytes ignored); a newer
    /// decoder reads the block only when bytes remain (older payload → all tails null). Layout:
    ///   [u16 extrasCount] { [i32 siteId] [u16 recLen] [u8 tailFlags] [payloads in bit order…] }*
    ///   recLen = byte length of tailFlags + payloads (lets a decoder SKIP a record whose flags carry
    ///   unknown future bits: known payloads always precede unknown ones — new fields take new HIGHER bits —
    ///   so parse-known-then-skip degrades gracefully per record instead of rejecting the payload).
    ///   tailFlags: bit0 = haven tail       → payload [i32 population]; bit4 = Infested VALUE (no payload).
    ///              bit1 = alien-base tail  → payload [str typeGuid][u8 nAddons]{[str addonGuid]}*.
    ///              bit2 = excavation tail  → payload [i64 endDateTicks]; bit5 = Excavating VALUE (no payload).
    ///              bit3 = attack-schedule tail → payload [u8 n]{[str attackerGuid][i64 atTicks][i64 forTicks]}*
    ///                     (gap 6b pre-attack countdown; n=0 = honest clear).
    ///              bits 6/7 RESERVED for future tails (higher bits, payloads appended after known ones).
    ///   An extras record for a siteId absent from the record array is skipped (join-by-id, never a throw).
    ///
    /// Case A only: this mirrors EXISTING client sites (resolved by SiteId). Vanilla never creates sites
    /// in-play, so a snapshot id absent on the client is logged + skipped (Case B / site creation deferred).
    /// </summary>
    public sealed class GeoSiteSnapshot
    {
        // WA-2 extras tailFlags bits. PRESENCE bits get a payload (written in ascending bit order);
        // VALUE bits carry no payload. New tails MUST take new HIGHER bits (parse-known-then-skip contract).
        private const byte TailHasHaven = 1 << 0;       // payload: [i32 population]
        private const byte TailHasAlienBase = 1 << 1;   // payload: [str typeGuid][u8 nAddons]{[str addonGuid]}*
        private const byte TailHasExcavation = 1 << 2;  // payload: [i64 excavationEndDateTicks]
        private const byte TailHasAttack = 1 << 3;      // payload: [u8 n]{[str attackerGuid][i64 atTicks][i64 forTicks]}*
        private const byte TailInfested = 1 << 4;       // value bit (meaningful only with TailHasHaven)
        private const byte TailExcavating = 1 << 5;     // value bit (meaningful only with TailHasExcavation)

        public readonly List<GeoSiteState> Sites = new List<GeoSiteState>();

        public static byte[] Encode(GeoSiteSnapshot snap)
        {
            if (snap == null) return null;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write((ushort)snap.Sites.Count);
                foreach (var s in snap.Sites)
                {
                    w.Write(s.SiteId);
                    WriteStr(w, s.OwnerFactionDefGuid);
                    w.Write(s.SiteType);
                    w.Write(s.State);
                    WriteStr(w, s.SiteName);
                    WriteStr(w, s.EncounterID);
                    // explored-state family as one flags byte: bit0=Inspected bit1=Visible bit2=Visited.
                    byte flags = 0;
                    if (s.Inspected) flags |= 1;
                    if (s.Visible) flags |= 2;
                    if (s.Visited) flags |= 4;
                    w.Write(flags);
                    // P1 ActiveMission mirror tail: class byte 0 = tombstone (no mission / cleared). A record
                    // whose class is 0 would alias the tombstone → treated as absent (never encoded live).
                    var m = s.Mission;
                    if (m == null || m.MissionClass == 0)
                    {
                        w.Write((byte)0);
                    }
                    else
                    {
                        w.Write(m.MissionClass);
                        WriteStr(w, m.MissionDefGuid);
                        WriteStr(w, m.AttackerFactionDefGuid);
                        w.Write(m.AttackerDeployment);
                        w.Write(m.DefenderDeployment);
                        WriteStr(w, m.AttackedZoneDefGuid);
                        int n2 = m.AttackingSiteIds.Length > byte.MaxValue ? byte.MaxValue : m.AttackingSiteIds.Length;
                        w.Write((byte)n2);
                        for (int i = 0; i < n2; i++) w.Write(m.AttackingSiteIds[i]);
                    }
                }
                // WA-2 extras block — written ONLY when ≥1 site carries a tail, so a no-tail payload stays
                // byte-identical to the pre-WA-2 wire (existing pins hold; older decoders ignore the block).
                int extras = 0;
                foreach (var s in snap.Sites) if (HasTail(s)) extras++;
                if (extras > 0)
                {
                    w.Write((ushort)extras);
                    foreach (var s in snap.Sites)
                    {
                        if (!HasTail(s)) continue;
                        w.Write(s.SiteId);
                        var rec = EncodeTailRecord(s);
                        w.Write((ushort)rec.Length);
                        w.Write(rec);
                    }
                }
                return ms.ToArray();
            }
        }

        private static bool HasTail(GeoSiteState s)
            => s.Haven != null || s.AlienBase != null || s.Excavation != null || s.Attack != null;

        /// <summary>[u8 tailFlags][payloads in ascending bit order] for one site's carried tails.</summary>
        private static byte[] EncodeTailRecord(GeoSiteState s)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                byte flags = 0;
                if (s.Haven != null)
                {
                    flags |= TailHasHaven;
                    if (s.Haven.Infested) flags |= TailInfested;
                }
                if (s.AlienBase != null) flags |= TailHasAlienBase;
                if (s.Excavation != null)
                {
                    flags |= TailHasExcavation;
                    if (s.Excavation.Excavating) flags |= TailExcavating;
                }
                if (s.Attack != null) flags |= TailHasAttack;
                w.Write(flags);
                if (s.Haven != null) w.Write(s.Haven.Population);
                if (s.AlienBase != null)
                {
                    WriteStr(w, s.AlienBase.TypeDefGuid);
                    int n = s.AlienBase.AddonDefGuids.Length > byte.MaxValue
                        ? byte.MaxValue : s.AlienBase.AddonDefGuids.Length;
                    w.Write((byte)n);
                    for (int i = 0; i < n; i++) WriteStr(w, s.AlienBase.AddonDefGuids[i]);
                }
                if (s.Excavation != null) w.Write(s.Excavation.EndDateTicks);
                if (s.Attack != null)
                {
                    int n = s.Attack.Entries.Length > byte.MaxValue ? byte.MaxValue : s.Attack.Entries.Length;
                    w.Write((byte)n);
                    for (int i = 0; i < n; i++)
                    {
                        WriteStr(w, s.Attack.Entries[i].AttackerFactionDefGuid);
                        w.Write(s.Attack.Entries[i].ScheduledAtTicks);
                        w.Write(s.Attack.Entries[i].ScheduledForTicks);
                    }
                }
                return ms.ToArray();
            }
        }

        public static GeoSiteSnapshot Decode(byte[] data)
        {
            if (data == null) return null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    var snap = new GeoSiteSnapshot();
                    int n = r.ReadUInt16();
                    for (int i = 0; i < n; i++)
                    {
                        int siteId = r.ReadInt32();
                        string ownerGuid = ReadStr(r);
                        byte siteType = r.ReadByte();
                        byte state = r.ReadByte();
                        string siteName = ReadStr(r);
                        string encounterId = ReadStr(r);
                        byte flags = r.ReadByte();
                        // P1 ActiveMission mirror tail (class byte 0 = tombstone → null record).
                        GeoMissionRecord mission = null;
                        byte missionClass = r.ReadByte();
                        if (missionClass != 0)
                        {
                            string missionDefGuid = ReadStr(r);
                            string attackerGuid = ReadStr(r);
                            int atkDeploy = r.ReadInt32();
                            int defDeploy = r.ReadInt32();
                            string zoneGuid = ReadStr(r);
                            int nSites = r.ReadByte();
                            var siteIds = new int[nSites];
                            for (int j = 0; j < nSites; j++) siteIds[j] = r.ReadInt32();
                            mission = new GeoMissionRecord(missionClass, missionDefGuid, attackerGuid,
                                atkDeploy, defDeploy, zoneGuid, siteIds);
                        }
                        snap.Sites.Add(new GeoSiteState(siteId, ownerGuid, siteType, state, siteName, encounterId,
                            inspected: (flags & 1) != 0, visible: (flags & 2) != 0, visited: (flags & 4) != 0,
                            mission: mission));
                    }
                    // WA-2 extras block — read ONLY if bytes remain (an older payload without it decodes with
                    // all tails null, ResearchSnapshot length-tolerance precedent). A truncated block throws →
                    // whole payload rejected (null), preserving the all-or-nothing contract.
                    if (ms.Position < ms.Length)
                    {
                        int ne = r.ReadUInt16();
                        Dictionary<int, TailSet> tails = null;
                        for (int i = 0; i < ne; i++)
                        {
                            int siteId = r.ReadInt32();
                            int recLen = r.ReadUInt16();
                            var rec = r.ReadBytes(recLen);
                            if (rec.Length != recLen)
                                throw new EndOfStreamException("GeoSiteSnapshot: truncated extras record");
                            var set = DecodeTailRecord(rec);
                            if (set != null)
                                (tails ?? (tails = new Dictionary<int, TailSet>()))[siteId] = set;
                        }
                        // Join by SiteId (an extras record for an id absent from the record array is ignored).
                        if (tails != null)
                            for (int i = 0; i < snap.Sites.Count; i++)
                            {
                                var s = snap.Sites[i];
                                if (!tails.TryGetValue(s.SiteId, out var set)) continue;
                                snap.Sites[i] = new GeoSiteState(s.SiteId, s.OwnerFactionDefGuid, s.SiteType,
                                    s.State, s.SiteName, s.EncounterID, s.Inspected, s.Visible, s.Visited,
                                    s.Mission, set.Haven, set.AlienBase, set.Excavation, set.Attack);
                            }
                    }
                    return snap;
                }
            }
            // Pure/Unity-free (unit-testable): swallow malformed payloads and return null. Callers
            // (GeoSiteChannel.Apply) treat null as "no-op". No UnityEngine.Debug dependency here.
            catch (Exception) { return null; }
        }

        private sealed class TailSet
        {
            public GeoHavenTail Haven;
            public GeoAlienBaseTail AlienBase;
            public GeoExcavationTail Excavation;
            public GeoAttackTail Attack;
            public bool Any => Haven != null || AlienBase != null || Excavation != null || Attack != null;
        }

        /// <summary>
        /// Parse one extras record's known tails ([u8 tailFlags][payloads in ascending bit order]). Payloads of
        /// UNKNOWN (future, higher) bits trail the known ones and are simply left unread — the record slice is
        /// length-prefixed, so an old decoder degrades to "known tails only" instead of rejecting the payload.
        /// A KNOWN bit whose payload is truncated throws → whole payload rejected by the caller.
        /// </summary>
        private static TailSet DecodeTailRecord(byte[] rec)
        {
            using (var ms = new MemoryStream(rec))
            using (var r = new BinaryReader(ms, Encoding.UTF8))
            {
                byte flags = r.ReadByte();
                var set = new TailSet();
                if ((flags & TailHasHaven) != 0)
                    set.Haven = new GeoHavenTail(r.ReadInt32(), (flags & TailInfested) != 0);
                if ((flags & TailHasAlienBase) != 0)
                {
                    string typeGuid = ReadStr(r);
                    int n = r.ReadByte();
                    var addons = new string[n];
                    for (int i = 0; i < n; i++) addons[i] = ReadStr(r);
                    set.AlienBase = new GeoAlienBaseTail(typeGuid, addons);
                }
                if ((flags & TailHasExcavation) != 0)
                    set.Excavation = new GeoExcavationTail((flags & TailExcavating) != 0, r.ReadInt64());
                if ((flags & TailHasAttack) != 0)
                {
                    int n = r.ReadByte();
                    var entries = new GeoAttackEntry[n];
                    for (int i = 0; i < n; i++)
                    {
                        string guid = ReadStr(r);
                        long at = r.ReadInt64();
                        long forT = r.ReadInt64();
                        entries[i] = new GeoAttackEntry(guid, at, forT);
                    }
                    set.Attack = new GeoAttackTail(entries);
                }
                return set.Any ? set : null;
            }
        }

        private static void WriteStr(BinaryWriter w, string s)
        {
            var b = Encoding.UTF8.GetBytes(s ?? "");
            w.Write((ushort)b.Length);
            w.Write(b);
        }

        private static string ReadStr(BinaryReader r)
        {
            int len = r.ReadUInt16();
            // BinaryReader.ReadBytes silently returns FEWER bytes at end-of-stream (no throw); verify the
            // full length was read, else throw → caught by Decode → null (rejected, not garbage).
            var bytes = r.ReadBytes(len);
            if (bytes.Length != len)
                throw new EndOfStreamException("GeoSiteSnapshot: truncated string (wanted " + len + ", got " + bytes.Length + ")");
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
