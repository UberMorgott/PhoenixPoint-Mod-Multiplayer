using System;

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
}
