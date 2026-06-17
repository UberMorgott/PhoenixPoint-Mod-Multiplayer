using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multipleer.Network.Sync.State
{
    /// <summary>One resource delta line: native <c>ShowReward</c> renders <c>ResourceType</c> + <c>RoundedValue</c>
    /// (UIModuleSiteEncounters.cs:415-418). <c>ResourceType</c> is the raw enum integer value.</summary>
    public readonly struct RewardResourceLine : IEquatable<RewardResourceLine>
    {
        public readonly int ResourceType;   // raw ResourceType enum value
        public readonly int RoundedValue;
        public RewardResourceLine(int resourceType, int roundedValue) { ResourceType = resourceType; RoundedValue = roundedValue; }
        public bool Equals(RewardResourceLine o) => ResourceType == o.ResourceType && RoundedValue == o.RoundedValue;
        public override bool Equals(object obj) => obj is RewardResourceLine o && Equals(o);
        public override int GetHashCode() { unchecked { return (ResourceType * 397) ^ RoundedValue; } }
    }

    /// <summary>One diplomacy +/- line (UIModuleSiteEncounters.cs:369-395). Party/Target are <c>IDiplomaticParty</c>
    /// (GeoFaction or GeoHavenLeader). Kind: 0 = GeoFaction (key = <c>Def.Guid</c>), 1 = GeoHavenLeader
    /// (key = the leader's haven <c>GeoSite.SiteId</c> as string), 2 = none/unresolved (key = "").</summary>
    public readonly struct RewardDiplomacyLine : IEquatable<RewardDiplomacyLine>
    {
        public readonly byte PartyKind;
        public readonly string PartyKey;
        public readonly byte TargetKind;
        public readonly string TargetKey;
        public readonly int Value;
        public RewardDiplomacyLine(byte partyKind, string partyKey, byte targetKind, string targetKey, int value)
        {
            PartyKind = partyKind; PartyKey = partyKey ?? "";
            TargetKind = targetKind; TargetKey = targetKey ?? "";
            Value = value;
        }
        public bool Equals(RewardDiplomacyLine o) => PartyKind == o.PartyKind && PartyKey == o.PartyKey
            && TargetKind == o.TargetKind && TargetKey == o.TargetKey && Value == o.Value;
        public override bool Equals(object obj) => obj is RewardDiplomacyLine o && Equals(o);
        public override int GetHashCode()
        {
            unchecked
            {
                int h = PartyKind;
                h = (h * 397) ^ (PartyKey?.GetHashCode() ?? 0);
                h = (h * 397) ^ TargetKind;
                h = (h * 397) ^ (TargetKey?.GetHashCode() ?? 0);
                h = (h * 397) ^ Value;
                return h;
            }
        }
    }

    /// <summary>One item-reward line (UIModuleSiteEncounters.cs:397-403): the item's <c>ItemDef.Guid</c> +
    /// count. The client resolves the def by guid for its display name (no <c>GeoItem</c> reconstruction).</summary>
    public readonly struct RewardItemLine : IEquatable<RewardItemLine>
    {
        public readonly string ItemDefGuid;
        public readonly int Count;
        public RewardItemLine(string itemDefGuid, int count) { ItemDefGuid = itemDefGuid ?? ""; Count = count; }
        public bool Equals(RewardItemLine o) => ItemDefGuid == o.ItemDefGuid && Count == o.Count;
        public override bool Equals(object obj) => obj is RewardItemLine o && Equals(o);
        public override int GetHashCode() { unchecked { return ((ItemDefGuid?.GetHashCode() ?? 0) * 397) ^ Count; } }
    }

    /// <summary>One haven-population change line (UIModuleSiteEncounters.cs:480-488): the haven's
    /// <c>GeoSite.SiteId</c> + delta (the client resolves the site for its name).</summary>
    public readonly struct RewardHavenPopLine : IEquatable<RewardHavenPopLine>
    {
        public readonly int HavenSiteId;
        public readonly int Delta;
        public RewardHavenPopLine(int havenSiteId, int delta) { HavenSiteId = havenSiteId; Delta = delta; }
        public bool Equals(RewardHavenPopLine o) => HavenSiteId == o.HavenSiteId && Delta == o.Delta;
        public override bool Equals(object obj) => obj is RewardHavenPopLine o && Equals(o);
        public override int GetHashCode() { unchecked { return (HavenSiteId * 397) ^ Delta; } }
    }

    /// <summary>One haven-zone damage line (UIModuleSiteEncounters.cs:469-478): the haven's
    /// <c>GeoSite.SiteId</c>, the zone's <c>Def.ViewElementDef.Guid</c> (for the localized zone name) + amount.</summary>
    public readonly struct RewardZoneLine : IEquatable<RewardZoneLine>
    {
        public readonly int HavenSiteId;
        public readonly string ZoneViewDefGuid;
        public readonly int Damage;
        public RewardZoneLine(int havenSiteId, string zoneViewDefGuid, int damage)
        { HavenSiteId = havenSiteId; ZoneViewDefGuid = zoneViewDefGuid ?? ""; Damage = damage; }
        public bool Equals(RewardZoneLine o) => HavenSiteId == o.HavenSiteId && ZoneViewDefGuid == o.ZoneViewDefGuid && Damage == o.Damage;
        public override bool Equals(object obj) => obj is RewardZoneLine o && Equals(o);
        public override int GetHashCode()
        {
            unchecked { int h = HavenSiteId; h = (h * 397) ^ (ZoneViewDefGuid?.GetHashCode() ?? 0); h = (h * 397) ^ Damage; return h; }
        }
    }

    /// <summary>
    /// The structured reward-delta lines the native <c>UIModuleSiteEncounters.ShowReward</c> draws on the
    /// host's event RESULT card, snapshotted host→client so the client's result card mirrors them by replaying
    /// the SAME native renderer (<c>AddRewardText</c>) — NEVER by re-applying the reward (the host already
    /// applied it; the totals already converge via the wallet/research/items/diplomacy/site channels).
    ///
    /// Pure data + wire codec — free of any Unity / <c>SyncEngine</c> dependency so it is directly
    /// unit-testable (mirrors <see cref="GeoSiteSnapshot"/> / <see cref="DiplomacySnapshot"/>). The host-side
    /// READ (<c>BuildFromReward</c>) and client-side RENDER (<c>RewardDisplayRender</c>) bind live game types
    /// via reflection and live in separate (un-linked) files.
    ///
    /// Wire payload (carried as the trailing reward blob inside EventDismiss):
    ///   [u16 nResources]{[i32 resType][i32 roundedValue]}
    ///   [u16 nDiplomacy]{[u8 partyKind][str partyKey][u8 targetKind][str targetKey][i32 value]}
    ///   [u16 nItems]{[str itemDefGuid][i32 count]}
    ///   [u16 nUnits]{[str unitName]}
    ///   [u16 nRevealed]{[i32 siteId]}
    ///   [u16 nHavenPop]{[i32 havenSiteId][i32 delta]}
    ///   [u16 nZones]{[i32 havenSiteId][str zoneViewDefGuid][i32 damage]}
    ///   [u16 nMaxDiplo]{[str factionDefGuid]}
    ///   [i32 spawnedHavenDefensesCount]
    ///   [i32 damagedSoldiersSum][i32 tiredSoldiersSum][i32 allSoldiersDamage][i32 allSoldiersTiredness]
    ///   [i32 factionSkillPoints][i32 newPhoenixBaseSiteId]
    /// </summary>
    public sealed class RewardDisplaySnapshot
    {
        public readonly List<RewardResourceLine> Resources = new List<RewardResourceLine>();
        public readonly List<RewardDiplomacyLine> Diplomacy = new List<RewardDiplomacyLine>();
        public readonly List<RewardItemLine> Items = new List<RewardItemLine>();
        public readonly List<string> Units = new List<string>();
        public readonly List<int> RevealedSites = new List<int>();
        public readonly List<RewardHavenPopLine> HavenPopulation = new List<RewardHavenPopLine>();
        public readonly List<RewardZoneLine> DamageZones = new List<RewardZoneLine>();
        public readonly List<string> MaxDiplomacyFactionGuids = new List<string>();
        public int SpawnedHavenDefensesCount;
        public int DamagedSoldiersSum;
        public int TiredSoldiersSum;
        public int AllSoldiersDamage;
        public int AllSoldiersTiredness;
        public int FactionSkillPoints;
        public int NewPhoenixBaseSiteId = -1;   // -1 = none

        /// <summary>True when there is nothing to render (the render path is a clean no-op).</summary>
        public bool IsEmpty =>
            Resources.Count == 0 && Diplomacy.Count == 0 && Items.Count == 0 && Units.Count == 0
            && RevealedSites.Count == 0 && HavenPopulation.Count == 0 && DamageZones.Count == 0
            && MaxDiplomacyFactionGuids.Count == 0 && SpawnedHavenDefensesCount == 0
            && DamagedSoldiersSum == 0 && TiredSoldiersSum == 0 && AllSoldiersDamage == 0
            && AllSoldiersTiredness == 0 && FactionSkillPoints == 0 && NewPhoenixBaseSiteId < 0;

        public static byte[] Encode(RewardDisplaySnapshot snap)
        {
            if (snap == null) return null;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write((ushort)snap.Resources.Count);
                foreach (var r in snap.Resources) { w.Write(r.ResourceType); w.Write(r.RoundedValue); }

                w.Write((ushort)snap.Diplomacy.Count);
                foreach (var d in snap.Diplomacy)
                {
                    w.Write(d.PartyKind); WriteStr(w, d.PartyKey);
                    w.Write(d.TargetKind); WriteStr(w, d.TargetKey);
                    w.Write(d.Value);
                }

                w.Write((ushort)snap.Items.Count);
                foreach (var it in snap.Items) { WriteStr(w, it.ItemDefGuid); w.Write(it.Count); }

                w.Write((ushort)snap.Units.Count);
                foreach (var u in snap.Units) WriteStr(w, u);

                w.Write((ushort)snap.RevealedSites.Count);
                foreach (var id in snap.RevealedSites) w.Write(id);

                w.Write((ushort)snap.HavenPopulation.Count);
                foreach (var h in snap.HavenPopulation) { w.Write(h.HavenSiteId); w.Write(h.Delta); }

                w.Write((ushort)snap.DamageZones.Count);
                foreach (var z in snap.DamageZones) { w.Write(z.HavenSiteId); WriteStr(w, z.ZoneViewDefGuid); w.Write(z.Damage); }

                w.Write((ushort)snap.MaxDiplomacyFactionGuids.Count);
                foreach (var g in snap.MaxDiplomacyFactionGuids) WriteStr(w, g);

                w.Write(snap.SpawnedHavenDefensesCount);
                w.Write(snap.DamagedSoldiersSum);
                w.Write(snap.TiredSoldiersSum);
                w.Write(snap.AllSoldiersDamage);
                w.Write(snap.AllSoldiersTiredness);
                w.Write(snap.FactionSkillPoints);
                w.Write(snap.NewPhoenixBaseSiteId);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Decode a reward blob. <c>null</c> input → <c>null</c> (no blob present should be handled by the
        /// caller). A ZERO-LENGTH blob (absent reward on a legacy/back-compat dismiss) decodes to an EMPTY
        /// snapshot, not null, so the render path is a clean no-op. A malformed/truncated blob → <c>null</c>
        /// (rejected, never partial garbage).
        /// </summary>
        public static RewardDisplaySnapshot Decode(byte[] data)
        {
            if (data == null) return null;
            if (data.Length == 0) return new RewardDisplaySnapshot();   // absent reward → empty (not null)
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    var snap = new RewardDisplaySnapshot();

                    int nRes = r.ReadUInt16();
                    for (int i = 0; i < nRes; i++) snap.Resources.Add(new RewardResourceLine(r.ReadInt32(), r.ReadInt32()));

                    int nDip = r.ReadUInt16();
                    for (int i = 0; i < nDip; i++)
                    {
                        byte pk = r.ReadByte(); string pKey = ReadStr(r);
                        byte tk = r.ReadByte(); string tKey = ReadStr(r);
                        int val = r.ReadInt32();
                        snap.Diplomacy.Add(new RewardDiplomacyLine(pk, pKey, tk, tKey, val));
                    }

                    int nItems = r.ReadUInt16();
                    for (int i = 0; i < nItems; i++) snap.Items.Add(new RewardItemLine(ReadStr(r), r.ReadInt32()));

                    int nUnits = r.ReadUInt16();
                    for (int i = 0; i < nUnits; i++) snap.Units.Add(ReadStr(r));

                    int nRev = r.ReadUInt16();
                    for (int i = 0; i < nRev; i++) snap.RevealedSites.Add(r.ReadInt32());

                    int nHav = r.ReadUInt16();
                    for (int i = 0; i < nHav; i++) snap.HavenPopulation.Add(new RewardHavenPopLine(r.ReadInt32(), r.ReadInt32()));

                    int nZones = r.ReadUInt16();
                    for (int i = 0; i < nZones; i++) snap.DamageZones.Add(new RewardZoneLine(r.ReadInt32(), ReadStr(r), r.ReadInt32()));

                    int nMax = r.ReadUInt16();
                    for (int i = 0; i < nMax; i++) snap.MaxDiplomacyFactionGuids.Add(ReadStr(r));

                    snap.SpawnedHavenDefensesCount = r.ReadInt32();
                    snap.DamagedSoldiersSum = r.ReadInt32();
                    snap.TiredSoldiersSum = r.ReadInt32();
                    snap.AllSoldiersDamage = r.ReadInt32();
                    snap.AllSoldiersTiredness = r.ReadInt32();
                    snap.FactionSkillPoints = r.ReadInt32();
                    snap.NewPhoenixBaseSiteId = r.ReadInt32();
                    return snap;
                }
            }
            // Malformed payload → reject (null). The codec stays PURE/Unity-free (it is linked into the unit
            // tests, which don't ship UnityEngine), so the visibility log lives at the Unity-bound caller
            // (SyncEngine.OnEventDismiss logs when a NON-empty blob fails to decode) — same [Multipleer] style.
            catch (Exception) { return null; }
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
            // BinaryReader.ReadBytes silently returns fewer bytes at EOF; verify the full length so a truncated
            // blob is rejected (→ Decode returns null) rather than yielding a short/garbage string.
            var bytes = r.ReadBytes(len);
            if (bytes.Length != len)
                throw new EndOfStreamException("RewardDisplaySnapshot: truncated string (wanted " + len + ", got " + bytes.Length + ")");
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
