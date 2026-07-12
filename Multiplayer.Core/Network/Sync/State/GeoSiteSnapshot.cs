using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Network.Sync.State
{
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
    ///   tailFlags: bit0 = haven tail       → payload [i32 population][u8 stockCount]{[i32 resType][i32 amount]}*
    ///              (stock = GeoHaven.StockedResources rounded-value mirror); bit4 = Infested VALUE (no payload).
    ///              bit1 = alien-base tail  → payload [str typeGuid][u8 nAddons]{[str addonGuid]}*.
    ///              bit2 = excavation tail  → payload [i64 endDateTicks]; bit5 = Excavating VALUE (no payload).
    ///              bit3 = attack-schedule tail → payload [u8 n]{[str attackerGuid][i64 atTicks][i64 forTicks]}*
    ///                     (gap 6b pre-attack countdown; n=0 = honest clear).
    ///              bit6 = weather tail        → payload [u8 weather] (gap 6f; carried only when non-Clear —
    ///                     a snapshotted site WITHOUT this bit means host weather is Clear → client resets).
    ///              bit7 = expiring-timer tail → payload [i64 expiringTicks] (carried only when armed — a
    ///                     snapshotted site WITHOUT this bit means host timer is Zero → client clears).
    ///   An extras record for a siteId absent from the record array is skipped (join-by-id, never a throw).
    ///
    /// W1 FACILITY SECTION (a SECOND optional block after the extras block; the tailFlags byte is full — all 8
    /// bits assigned — so this per-base-site facility working-state tail rides its own siteId-keyed section):
    ///   [u16 facCount] { [i32 siteId] [u16 recLen] [u8 nFac] {[u32 facId][i32 gx][i32 gy][u8 state][u8 powered]}* }*
    ///   Written ONLY when ≥1 base carries facilities; the extras block above is then ALWAYS emitted first
    ///   (extrasCount possibly 0) so the two blocks read strictly in order. A no-facility payload adds nothing
    ///   (empty/extras wire stays byte-identical), and an older decoder stops after the extras block, ignoring it.
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
        private const byte TailHasWeather = 1 << 6;     // payload: [u8 weather] (gap 6f; carried only when non-Clear)
        private const byte TailHasExpiringTimer = 1 << 7; // payload: [i64 expiringTicks] (carried only when armed)

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
                // WA-2 extras block — written when ≥1 site carries a per-record tail OR any site carries the W1
                // facility section (so the — possibly zero — extrasCount ALWAYS precedes the facility section:
                // the decoder reads the two blocks strictly in order, never ambiguously). A payload with neither
                // stays byte-identical to the pre-WA-2 wire (existing pins hold; older decoders ignore the block).
                int extras = 0, facExtras = 0;
                foreach (var s in snap.Sites) { if (HasTail(s)) extras++; if (s.Facility != null) facExtras++; }
                if (extras > 0 || facExtras > 0)
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
                // W1 FACILITY SECTION — a SEPARATE siteId-keyed optional block AFTER the extras block: the
                // per-record tailFlags byte is fully assigned (all 8 bits), so the facility working-state tail
                // rides its own section instead of a flag bit. Written ONLY when ≥1 base carries facilities, so a
                // no-facility payload adds nothing (the extras/empty wire stays byte-identical); an older decoder
                // stops after the extras block and ignores these trailing bytes.
                //   [u16 facCount] { [i32 siteId] [u16 recLen] [u8 nFac] {[u32 facId][i32 gx][i32 gy][u8 state][u8 powered]}* }*
                //   recLen lets a decoder skip a record whose future fields trail the known entries (parse-known-then-skip).
                if (facExtras > 0)
                {
                    w.Write((ushort)facExtras);
                    foreach (var s in snap.Sites)
                    {
                        if (s.Facility == null) continue;
                        w.Write(s.SiteId);
                        var rec = EncodeFacilityRecord(s.Facility);
                        w.Write((ushort)rec.Length);
                        w.Write(rec);
                    }
                }
                return ms.ToArray();
            }
        }

        private static bool HasTail(GeoSiteState s)
            => s.Haven != null || s.AlienBase != null || s.Excavation != null || s.Attack != null
               || s.Weather != null || s.ExpiringTimer != null;

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
                if (s.Weather != null) flags |= TailHasWeather;
                if (s.ExpiringTimer != null) flags |= TailHasExpiringTimer;
                w.Write(flags);
                if (s.Haven != null)
                {
                    w.Write(s.Haven.Population);
                    // Stock rides the haven presence bit (bit0), appended after Population — before the higher-bit
                    // payloads (ascending bit order preserved). u8 count (≤13 resource types); each = raw type + rounded amount.
                    int ns = s.Haven.Stock.Length > byte.MaxValue ? byte.MaxValue : s.Haven.Stock.Length;
                    w.Write((byte)ns);
                    for (int i = 0; i < ns; i++)
                    {
                        w.Write(s.Haven.Stock[i].ResourceType);
                        w.Write(s.Haven.Stock[i].Amount);
                    }
                }
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
                // bits 6/7 payloads trail the lower-bit ones (ascending bit order = parse-known-then-skip).
                if (s.Weather != null) w.Write(s.Weather.Weather);
                if (s.ExpiringTimer != null) w.Write(s.ExpiringTimer.ExpiringTicks);
                return ms.ToArray();
            }
        }

        /// <summary>[u8 nFac]{[u32 facId][i32 gx][i32 gy][u8 state][u8 powered]}* for one base site's facilities.</summary>
        private static byte[] EncodeFacilityRecord(GeoFacilityTail tail)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                int n = tail.Entries.Length > byte.MaxValue ? byte.MaxValue : tail.Entries.Length;
                w.Write((byte)n);
                for (int i = 0; i < n; i++)
                {
                    var e = tail.Entries[i];
                    w.Write(e.FacilityId);                       // u32
                    w.Write(e.GridX);                           // i32
                    w.Write(e.GridY);                           // i32
                    w.Write(e.State);                           // u8 (raw FacilityState enum value)
                    w.Write((byte)(e.IsPowered ? 1 : 0));       // u8
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
                                    s.Mission, set.Haven, set.AlienBase, set.Excavation, set.Attack,
                                    set.Weather, set.ExpiringTimer);
                            }
                    }
                    // W1 FACILITY SECTION — read ONLY if bytes remain after the extras block (an older payload
                    // without it decodes with all facility tails null). Join-by-SiteId like the extras block;
                    // a truncated record throws → whole payload rejected (null), preserving all-or-nothing.
                    if (ms.Position < ms.Length)
                    {
                        int nf = r.ReadUInt16();
                        Dictionary<int, GeoFacilityTail> facs = null;
                        for (int i = 0; i < nf; i++)
                        {
                            int siteId = r.ReadInt32();
                            int recLen = r.ReadUInt16();
                            var rec = r.ReadBytes(recLen);
                            if (rec.Length != recLen)
                                throw new EndOfStreamException("GeoSiteSnapshot: truncated facility record");
                            var tail = DecodeFacilityRecord(rec);
                            if (tail != null)
                                (facs ?? (facs = new Dictionary<int, GeoFacilityTail>()))[siteId] = tail;
                        }
                        if (facs != null)
                            for (int i = 0; i < snap.Sites.Count; i++)
                            {
                                var s = snap.Sites[i];
                                if (!facs.TryGetValue(s.SiteId, out var tail)) continue;
                                snap.Sites[i] = new GeoSiteState(s.SiteId, s.OwnerFactionDefGuid, s.SiteType,
                                    s.State, s.SiteName, s.EncounterID, s.Inspected, s.Visible, s.Visited,
                                    s.Mission, s.Haven, s.AlienBase, s.Excavation, s.Attack, s.Weather,
                                    s.ExpiringTimer, tail);
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
            public GeoWeatherTail Weather;
            public GeoExpiringTimerTail ExpiringTimer;
            public bool Any => Haven != null || AlienBase != null || Excavation != null || Attack != null
                               || Weather != null || ExpiringTimer != null;
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
                {
                    int population = r.ReadInt32();
                    int ns = r.ReadByte();
                    var stock = new HavenStockUnit[ns];
                    for (int i = 0; i < ns; i++) stock[i] = new HavenStockUnit(r.ReadInt32(), r.ReadInt32());
                    set.Haven = new GeoHavenTail(population, (flags & TailInfested) != 0, stock);
                }
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
                if ((flags & TailHasWeather) != 0)
                    set.Weather = new GeoWeatherTail(r.ReadByte());
                if ((flags & TailHasExpiringTimer) != 0)
                    set.ExpiringTimer = new GeoExpiringTimerTail(r.ReadInt64());
                return set.Any ? set : null;
            }
        }

        /// <summary>Parse one facility record ([u8 nFac]{[u32 facId][i32 gx][i32 gy][u8 state][u8 powered]}*).
        /// Future per-record fields trailing the known entries are left unread (the record slice is
        /// length-prefixed) — parse-known-then-skip, exactly like the extras records.</summary>
        private static GeoFacilityTail DecodeFacilityRecord(byte[] rec)
        {
            using (var ms = new MemoryStream(rec))
            using (var r = new BinaryReader(ms, Encoding.UTF8))
            {
                int n = r.ReadByte();
                var entries = new GeoFacilityEntry[n];
                for (int i = 0; i < n; i++)
                {
                    uint facId = r.ReadUInt32();
                    int gx = r.ReadInt32();
                    int gy = r.ReadInt32();
                    byte state = r.ReadByte();
                    bool powered = r.ReadByte() != 0;
                    entries[i] = new GeoFacilityEntry(facId, gx, gy, state, powered);
                }
                return new GeoFacilityTail(entries);
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
