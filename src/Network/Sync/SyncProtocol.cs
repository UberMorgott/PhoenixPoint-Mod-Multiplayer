using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Multipleer.Network.Sync.State;

namespace Multipleer.Network.Sync
{
    /// <summary>
    /// Wire codecs for the ActionSync packet group. The header/envelope is handled by
    /// <c>NetworkMessage</c>; these encode/decode only the payload bytes.
    /// Mirrors the existing <c>MessageSerializer</c> BinaryWriter/Reader idiom.
    /// </summary>
    public static class SyncProtocol
    {
        public static byte[] EncodeActionRequest(ushort actionId, uint nonce, byte[] payload)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(actionId);
                w.Write(nonce);
                w.Write((ushort)payload.Length);
                w.Write(payload);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeActionRequest(byte[] data, out ushort actionId, out uint nonce, out byte[] payload)
        {
            actionId = 0; nonce = 0; payload = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    actionId = r.ReadUInt16();
                    nonce = r.ReadUInt32();
                    payload = r.ReadBytes(r.ReadUInt16());
                    return true;
                }
            }
            catch { return false; }
        }

        public static byte[] EncodeActionApply(ushort actionId, ulong sequence, byte[] payload)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(actionId);
                w.Write(sequence);
                w.Write((ushort)payload.Length);
                w.Write(payload);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeActionApply(byte[] data, out ushort actionId, out ulong sequence, out byte[] payload)
        {
            actionId = 0; sequence = 0; payload = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    actionId = r.ReadUInt16();
                    sequence = r.ReadUInt64();
                    payload = r.ReadBytes(r.ReadUInt16());
                    return true;
                }
            }
            catch { return false; }
        }

        public static byte[] EncodeActionReject(uint nonce, byte reasonCode, string reason)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(nonce);
                w.Write(reasonCode);
                w.Write(reason ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryDecodeActionReject(byte[] data, out uint nonce, out byte reasonCode, out string reason)
        {
            nonce = 0; reasonCode = 0; reason = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    nonce = r.ReadUInt32();
                    reasonCode = r.ReadByte();
                    reason = r.ReadString();
                    return true;
                }
            }
            catch { return false; }
        }

        public static byte[] EncodeWalletSync(ulong version, List<(int type, float value)> slots)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(version);
                w.Write((byte)slots.Count);
                foreach (var (t, v) in slots)
                {
                    w.Write(t);
                    w.Write(v);
                }
                return ms.ToArray();
            }
        }

        public static bool TryDecodeWalletSync(byte[] data, out ulong version, out List<(int type, float value)> slots)
        {
            version = 0; slots = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    version = r.ReadUInt64();
                    int n = r.ReadByte();
                    slots = new List<(int, float)>(n);
                    for (int i = 0; i < n; i++)
                        slots.Add((r.ReadInt32(), r.ReadSingle()));
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── Generic per-channel state echo (StateChannel infra) ───────────
        // Wire: [channelId:u8][version:u64][len:u16][payload:N]. The channel id selects an
        // IStateChannel; version is host-monotonic per channel (client drops anything not newer).

        public static byte[] EncodeStateSync(byte channelId, ulong version, byte[] payload)
        {
            payload = payload ?? new byte[0];
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(channelId);
                w.Write(version);
                w.Write((ushort)payload.Length);
                w.Write(payload);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeStateSync(byte[] data, out byte channelId, out ulong version, out byte[] payload)
        {
            channelId = 0; version = 0; payload = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    channelId = r.ReadByte();
                    version = r.ReadUInt64();
                    payload = r.ReadBytes(r.ReadUInt16());
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── Geoscape event display (separate from channels) ───────────────
        // Both raise and dismiss lead with a host-synthesized per-OCCURRENCE id (u16, see EventOccurrenceIds):
        // the reusable GeoscapeEvent.EventID def-name collides when two occurrences of the same def fire, so the
        // occurrence id is the real correlation key (def-name is carried only for the native rebuild + logging).
        // Both host and client run the SAME build, so the occurrence id is a clean REQUIRED leading field (no
        // cross-version optionality); the trailing fields below keep their in-build optionality.
        //
        // EventRaised: [occId:u16][eventId:string][siteId:i32]([vehicleId:i32]?) — host tells clients to SHOW a
        // dialog. siteId is GeoSite.SiteId (-1 = none → client falls back to StartingBase context); vehicleId is
        // GeoVehicle.VehicleID (-1 = none → client resolves a vehicle at the site, else null context). The
        // trailing vehicleId is OPTIONAL: a 3-field [occId][eventId][siteId] payload decodes with vehicleId = -1
        // (so the decoder never throws on a short buffer).
        // EventDismiss: [occId:u16][eventId:string][choiceIndex:i32]?([u16 rewardLen][rewardBlob:N]?)([siteId:i32]?)
        // — host tells clients the answer was applied. choiceIndex is the index of the picked choice within
        // EventData.Choices: >= 0 means the choice produced a follow-up RESULT/OUTCOME page (clients rebuild +
        // show it natively); -1 means close-only (pure-INFO host-OK / decline). ALL trailing groups are OPTIONAL:
        //   • a 2-field [occId][eventId] payload decodes with choiceIndex = -1 (close-only);
        //   • a 3-field [occId][eventId][choiceIndex] payload decodes with an EMPTY reward blob and siteId = -1;
        // so the decoder never throws on a short buffer. The reward blob is a RewardDisplaySnapshot (the native
        // ShowReward delta lines) carried so the client mirrors the reward card; it is appended ONLY when
        // non-empty, keeping the no-reward 3-field wire byte-stable. siteId is GeoSite.SiteId (-1 = none → the
        // client result card falls back to StartingBase) — the SAME id the raise resolves (EventRaised's siteId);
        // it is appended ONLY when >= 0 (the no-site wire stays byte-stable). When a siteId follows a missing
        // reward, the u16 rewardLen is still written as 0 so the trailing siteId is unambiguous to the decoder.

        // EventRaised now optionally carries a trailing site-IDENTITY block for an absent-site event:
        // [occId:u16][eventId:string][siteId:i32][vehicleId:i32]([hasIdentity:u8=1][GeoSiteState])?. The
        // identity is a GeoSiteState (reused from the GeoSite channel): SiteId / ownerGuid / type / state /
        // siteName-locKey / encounterID. The flag byte + block are appended ONLY when an identity is present,
        // so the NO-identity wire is byte-IDENTICAL to the legacy 4-field raise (keeps EventRaised_WireBytes
        // pinned). A LEGACY 4-field payload (no trailing byte) decodes hasIdentity=false — the optional reads
        // guard on remaining length, never throw.

        public static byte[] EncodeEventRaised(ushort occurrenceId, string eventId, int siteId, int vehicleId = -1)
            => EncodeEventRaised(occurrenceId, eventId, siteId, vehicleId, null);

        public static byte[] EncodeEventRaised(ushort occurrenceId, string eventId, int siteId, int vehicleId, GeoSiteState? identity)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(occurrenceId);
                w.Write(eventId ?? "");
                w.Write(siteId);
                w.Write(vehicleId);
                // Append the flag byte + block ONLY when an identity is present; the no-identity wire stays
                // byte-identical to the legacy raise (decode treats "no trailing byte" == "flag 0" == no identity).
                if (identity.HasValue)
                {
                    w.Write((byte)1);
                    WriteSiteIdentity(w, identity.Value);
                }
                return ms.ToArray();
            }
        }

        // 4-out shim: existing callers/tests that ignore the identity block.
        public static bool TryDecodeEventRaised(byte[] data, out ushort occurrenceId, out string eventId, out int siteId, out int vehicleId)
            => TryDecodeEventRaised(data, out occurrenceId, out eventId, out siteId, out vehicleId, out _, out _);

        public static bool TryDecodeEventRaised(byte[] data, out ushort occurrenceId, out string eventId, out int siteId, out int vehicleId, out bool hasIdentity, out GeoSiteState identity)
        {
            occurrenceId = 0; eventId = null; siteId = -1; vehicleId = -1; hasIdentity = false;
            identity = default(GeoSiteState);
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    occurrenceId = r.ReadUInt16();
                    eventId = r.ReadString();
                    siteId = r.ReadInt32();
                    // Optional trailing field: absent in a 3-field payload → leave vehicleId = -1.
                    if (ms.Length - ms.Position >= sizeof(int)) vehicleId = r.ReadInt32();
                    // Optional identity flag byte: absent in a LEGACY 4-field payload → no identity.
                    if (ms.Length - ms.Position >= sizeof(byte))
                    {
                        byte flag = r.ReadByte();
                        if (flag == 1)
                        {
                            identity = ReadSiteIdentity(r);
                            hasIdentity = true;
                        }
                    }
                    return true;
                }
            }
            catch { return false; }
        }

        // One GeoSiteState record on the wire (same field order as GeoSiteSnapshot's per-site block).
        private static void WriteSiteIdentity(BinaryWriter w, GeoSiteState s)
        {
            w.Write(s.SiteId);
            WriteWireStr(w, s.OwnerFactionDefGuid);
            w.Write(s.SiteType);
            w.Write(s.State);
            WriteWireStr(w, s.SiteName);
            WriteWireStr(w, s.EncounterID);
        }

        private static GeoSiteState ReadSiteIdentity(BinaryReader r)
        {
            int siteId = r.ReadInt32();
            string owner = ReadWireStr(r);
            byte type = r.ReadByte();
            byte state = r.ReadByte();
            string name = ReadWireStr(r);
            string enc = ReadWireStr(r);
            return new GeoSiteState(siteId, owner, type, state, name, enc);
        }

        private static void WriteWireStr(BinaryWriter w, string s)
        {
            var b = Encoding.UTF8.GetBytes(s ?? "");
            w.Write((ushort)b.Length);
            w.Write(b);
        }

        private static string ReadWireStr(BinaryReader r)
        {
            int len = r.ReadUInt16();
            var bytes = r.ReadBytes(len);
            if (bytes.Length != len)
                throw new EndOfStreamException("EventRaised identity: truncated string (wanted " + len + ", got " + bytes.Length + ")");
            return Encoding.UTF8.GetString(bytes);
        }

        public static byte[] EncodeEventDismiss(ushort occurrenceId, string eventId, int choiceIndex = -1)
            => EncodeEventDismiss(occurrenceId, eventId, choiceIndex, null);

        // Reward+site overload: appends [u16 rewardLen][rewardBlob] ONLY when the blob is non-empty and
        // [i32 siteId] ONLY when siteId >= 0, so a null/empty reward with no site yields the EXACT 3-field bytes
        // (no trailing length) — keeps the no-reward/no-site wire stable. When a siteId follows a MISSING reward,
        // the u16 rewardLen is still written as 0 so the trailing siteId is unambiguous on decode. rewardBlob is
        // a RewardDisplaySnapshot-encoded payload; siteId is GeoSite.SiteId (-1 = none, the SAME id the raise uses).
        public static byte[] EncodeEventDismiss(ushort occurrenceId, string eventId, int choiceIndex, byte[] rewardBlob, int siteId = -1)
        {
            bool hasReward = rewardBlob != null && rewardBlob.Length > 0;
            bool hasSite = siteId >= 0;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(occurrenceId);
                w.Write(eventId ?? "");
                w.Write(choiceIndex);
                // Write the reward-length field when there is a reward OR a trailing siteId (so the siteId is
                // never mistaken for a reward length on decode); length is 0 when no reward is present.
                if (hasReward || hasSite)
                {
                    if (hasReward)
                    {
                        // The wire length field is u16; refuse to silently truncate an oversized reward blob.
                        if (rewardBlob.Length > ushort.MaxValue)
                            throw new ArgumentOutOfRangeException(nameof(rewardBlob),
                                "Reward blob exceeds the u16 length field (" + rewardBlob.Length + " > " + ushort.MaxValue + ").");
                        w.Write((ushort)rewardBlob.Length);
                        w.Write(rewardBlob);
                    }
                    else
                    {
                        w.Write((ushort)0);   // no reward, but a siteId follows → empty-length marker
                    }
                }
                if (hasSite) w.Write(siteId);
                return ms.ToArray();
            }
        }

        // 3-out overload (occId, eventId, choiceIndex) — kept for callers/tests that ignore the reward blob + site.
        public static bool TryDecodeEventDismiss(byte[] data, out ushort occurrenceId, out string eventId, out int choiceIndex)
            => TryDecodeEventDismiss(data, out occurrenceId, out eventId, out choiceIndex, out _, out _);

        // 4-out overload (… rewardBlob) — kept for callers/tests that ignore the trailing siteId.
        public static bool TryDecodeEventDismiss(byte[] data, out ushort occurrenceId, out string eventId, out int choiceIndex, out byte[] rewardBlob)
            => TryDecodeEventDismiss(data, out occurrenceId, out eventId, out choiceIndex, out rewardBlob, out _);

        public static bool TryDecodeEventDismiss(byte[] data, out ushort occurrenceId, out string eventId, out int choiceIndex, out byte[] rewardBlob, out int siteId)
        {
            occurrenceId = 0; eventId = null; choiceIndex = -1; rewardBlob = new byte[0]; siteId = -1;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    occurrenceId = r.ReadUInt16();
                    eventId = r.ReadString();
                    // Optional trailing field: absent in a 2-field payload → leave choiceIndex = -1
                    // (close-only), so a close-only dismiss still decodes on a short buffer.
                    if (ms.Length - ms.Position >= sizeof(int)) choiceIndex = r.ReadInt32();
                    // Optional trailing reward blob: [u16 len][len bytes]. Absent in a 3-field payload
                    // → leave an empty blob (reward-less result card). Only accept it when the FULL declared
                    // length is present (no partial accept). A len of 0 (empty-length marker that precedes a
                    // trailing siteId) consumes the u16 only, leaving the siteId for the read below.
                    if (ms.Length - ms.Position >= sizeof(ushort))
                    {
                        int len = r.ReadUInt16();
                        if (len > 0 && ms.Length - ms.Position >= len) rewardBlob = r.ReadBytes(len);
                    }
                    // Optional trailing siteId: absent in an old payload → leave siteId = -1 (no site → the
                    // result card falls back to StartingBase). Mirrors EventRaised's trailing-optional fields.
                    if (ms.Length - ms.Position >= sizeof(int)) siteId = r.ReadInt32();
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── Geoscape event CHOICE CLAIM (client -> host, first-click-wins) ─
        // A client click captures the chosen choice index and routes it to the host arbiter. Wire:
        // [occId:u16][choiceIndex:i32]. occId is the host-synthesized per-occurrence id carried on the raise;
        // choiceIndex is the index into EventData.Choices (-1 = null/decline). The host accepts the FIRST
        // claim per occId (ChoiceArbiter), runs the authoritative CompleteEvent, and broadcasts the OUTCOME
        // via the existing EventDismiss; later claims for a resolved occId are ignored.

        public static byte[] EncodeChoiceClaim(ushort occurrenceId, int choiceIndex)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(occurrenceId);
                w.Write(choiceIndex);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeChoiceClaim(byte[] data, out ushort occurrenceId, out int choiceIndex)
        {
            occurrenceId = 0; choiceIndex = -1;
            // Require the full fixed [occId:u16][choiceIndex:i32] = 6 bytes; a short buffer is a clean drop.
            if (data == null || data.Length < sizeof(ushort) + sizeof(int)) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    occurrenceId = r.ReadUInt16();
                    choiceIndex = r.ReadInt32();
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── Unified surface envelope (SurfaceRouter chokepoint) ───────────
        // Wire: [surfaceId:u8][kind:u8][len:u16][payload:N]. surfaceId selects a registered surface;
        // kind (SyncKind) selects request/apply/snapshot/delta. The inner payload is the surface's
        // own bytes (e.g. an action's Write output) — unchanged from the legacy per-packet format.

        public static byte[] EncodeEnvelope(byte surfaceId, SyncKind kind, byte[] payload)
        {
            payload = payload ?? new byte[0];
            // The wire length field is u16; refuse to silently truncate an oversized payload.
            if (payload.Length > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(payload),
                    "Envelope payload exceeds the u16 length field (" + payload.Length + " > " + ushort.MaxValue + ").");
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(surfaceId);
                w.Write((byte)kind);
                w.Write((ushort)payload.Length);
                w.Write(payload);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeEnvelope(byte[] data, out byte surfaceId, out SyncKind kind, out byte[] payload)
        {
            surfaceId = 0; kind = SyncKind.ActionRequest; payload = null;
            // Require the full 4-byte header [surfaceId:u8][kind:u8][len:u16] before reading anything.
            if (data == null || data.Length < 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    byte sid = r.ReadByte();
                    byte kindByte = r.ReadByte();
                    // Forward-compat: an undefined kind byte is a graceful drop, never a crash.
                    if (!Enum.IsDefined(typeof(SyncKind), kindByte)) return false;
                    ushort len = r.ReadUInt16();
                    // No partial accept: the declared payload length must actually be present.
                    if (ms.Length - ms.Position < len) return false;
                    surfaceId = sid;
                    kind = (SyncKind)kindByte;
                    payload = r.ReadBytes(len);
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
