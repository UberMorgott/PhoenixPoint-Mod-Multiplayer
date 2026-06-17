using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

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
        // EventDismiss: [occId:u16][eventId:string][choiceIndex:i32]?([u16 rewardLen][rewardBlob:N]?) — host
        // tells clients the answer was applied. choiceIndex is the index of the picked choice within
        // EventData.Choices: >= 0 means the choice produced a follow-up RESULT/OUTCOME page (clients rebuild +
        // show it natively); -1 means close-only (pure-INFO host-OK / decline). BOTH trailing groups are OPTIONAL:
        //   • a 2-field [occId][eventId] payload decodes with choiceIndex = -1 (close-only);
        //   • a 3-field [occId][eventId][choiceIndex] payload decodes with an EMPTY reward blob;
        // so the decoder never throws on a short buffer. The reward blob is a RewardDisplaySnapshot (the native
        // ShowReward delta lines) carried so the client mirrors the reward card; it is appended ONLY when
        // non-empty, keeping the no-reward 3-field wire byte-stable.

        public static byte[] EncodeEventRaised(ushort occurrenceId, string eventId, int siteId, int vehicleId = -1)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(occurrenceId);
                w.Write(eventId ?? "");
                w.Write(siteId);
                w.Write(vehicleId);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeEventRaised(byte[] data, out ushort occurrenceId, out string eventId, out int siteId, out int vehicleId)
        {
            occurrenceId = 0; eventId = null; siteId = -1; vehicleId = -1;
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
                    return true;
                }
            }
            catch { return false; }
        }

        public static byte[] EncodeEventDismiss(ushort occurrenceId, string eventId, int choiceIndex = -1)
            => EncodeEventDismiss(occurrenceId, eventId, choiceIndex, null);

        // Reward-carrying overload: appends [u16 rewardLen][rewardBlob] ONLY when the blob is non-empty, so a
        // null/empty reward yields the EXACT 3-field bytes (no trailing length) — keeps the no-reward wire
        // stable. rewardBlob is a RewardDisplaySnapshot-encoded payload.
        public static byte[] EncodeEventDismiss(ushort occurrenceId, string eventId, int choiceIndex, byte[] rewardBlob)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(occurrenceId);
                w.Write(eventId ?? "");
                w.Write(choiceIndex);
                if (rewardBlob != null && rewardBlob.Length > 0)
                {
                    // The wire length field is u16; refuse to silently truncate an oversized reward blob.
                    if (rewardBlob.Length > ushort.MaxValue)
                        throw new ArgumentOutOfRangeException(nameof(rewardBlob),
                            "Reward blob exceeds the u16 length field (" + rewardBlob.Length + " > " + ushort.MaxValue + ").");
                    w.Write((ushort)rewardBlob.Length);
                    w.Write(rewardBlob);
                }
                return ms.ToArray();
            }
        }

        // 3-out overload (occId, eventId, choiceIndex) — kept for callers/tests that ignore the reward blob.
        public static bool TryDecodeEventDismiss(byte[] data, out ushort occurrenceId, out string eventId, out int choiceIndex)
            => TryDecodeEventDismiss(data, out occurrenceId, out eventId, out choiceIndex, out _);

        public static bool TryDecodeEventDismiss(byte[] data, out ushort occurrenceId, out string eventId, out int choiceIndex, out byte[] rewardBlob)
        {
            occurrenceId = 0; eventId = null; choiceIndex = -1; rewardBlob = new byte[0];
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
                    // length is present (no partial accept).
                    if (ms.Length - ms.Position >= sizeof(ushort))
                    {
                        int len = r.ReadUInt16();
                        if (len > 0 && ms.Length - ms.Position >= len) rewardBlob = r.ReadBytes(len);
                    }
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
