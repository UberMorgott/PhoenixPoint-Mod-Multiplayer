using System;
using System.IO;
using System.Text;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Wire codec for the unified sync envelope. The header is handled by <c>NetworkMessage</c>;
    /// this encodes/decodes only the payload bytes, in the <c>MessageSerializer</c> BinaryWriter/Reader
    /// idiom. Legacy per-channel codecs (WalletSync / StateSync / EventDismiss / ReportModalHide /
    /// GeoLogNotice) were deleted 2026-07-16, and the action-relay codecs (EncodeActionRequest/Apply/
    /// Reject + decoders, for the reserved 0xA2-0xA4 surfaces) 2026-07-22 — zero callers; rail surfaces
    /// carry their own inner payloads (see the SurfaceIds 0xA0-0xBF comments). History in git.
    /// </summary>
    public static class SyncProtocol
    {
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
