using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multipleer.Network.Sync.State
{
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
    /// </summary>
    public readonly struct GeoSiteState : IEquatable<GeoSiteState>
    {
        public readonly int SiteId;
        public readonly string OwnerFactionDefGuid;
        public readonly byte SiteType;     // raw GeoSiteType enum value
        public readonly byte State;        // raw GeoSiteState enum value
        public readonly string SiteName;   // LocalizedTextBind.LocalizationKey
        public readonly string EncounterID;

        public GeoSiteState(int siteId, string ownerFactionDefGuid, byte siteType, byte state, string siteName, string encounterID)
        {
            SiteId = siteId;
            // Normalize null → "" so equality + the wire are stable (the codec also coalesces, this keeps
            // an in-memory DTO comparable to its decoded twin).
            OwnerFactionDefGuid = ownerFactionDefGuid ?? "";
            SiteType = siteType;
            State = state;
            SiteName = siteName ?? "";
            EncounterID = encounterID ?? "";
        }

        public bool Equals(GeoSiteState other)
            => SiteId == other.SiteId
               && OwnerFactionDefGuid == other.OwnerFactionDefGuid
               && SiteType == other.SiteType
               && State == other.State
               && SiteName == other.SiteName
               && EncounterID == other.EncounterID;

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
                return h;
            }
        }

        public override string ToString()
            => $"Site({SiteId} owner={OwnerFactionDefGuid} type={SiteType} state={State} name={SiteName} enc={EncounterID})";
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
    ///   [u16 count]{[i32 SiteId][u16 ownerLen][ownerGuid utf8][u8 SiteType][u8 State][u16 nameLen][siteName utf8][u16 encLen][EncounterID utf8]}*
    ///
    /// Case A only: this mirrors EXISTING client sites (resolved by SiteId). Vanilla never creates sites
    /// in-play, so a snapshot id absent on the client is logged + skipped (Case B / site creation deferred).
    /// </summary>
    public sealed class GeoSiteSnapshot
    {
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
                        snap.Sites.Add(new GeoSiteState(siteId, ownerGuid, siteType, state, siteName, encounterId));
                    }
                    return snap;
                }
            }
            // Pure/Unity-free (unit-testable): swallow malformed payloads and return null. Callers
            // (GeoSiteChannel.Apply) treat null as "no-op". No UnityEngine.Debug dependency here.
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
            // BinaryReader.ReadBytes silently returns FEWER bytes at end-of-stream (no throw); verify the
            // full length was read, else throw → caught by Decode → null (rejected, not garbage).
            var bytes = r.ReadBytes(len);
            if (bytes.Length != len)
                throw new EndOfStreamException("GeoSiteSnapshot: truncated string (wanted " + len + ", got " + bytes.Length + ")");
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
