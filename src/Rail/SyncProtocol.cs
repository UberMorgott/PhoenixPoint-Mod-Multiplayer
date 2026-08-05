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
        // Wire: [surfaceId:u8][kind:u8][ordinal:u32][len:u16][payload:N]. surfaceId selects a registered
        // surface; kind (SyncKind) selects request/apply/snapshot/delta. The inner payload is the surface's
        // own bytes (e.g. an action's Write output) — unchanged from the legacy per-packet format.
        //
        // ORDINAL, added 2026-08-05. The header grew because the ONE thing every surface has in common is
        // that it goes through this encoder: a CROSS-SURFACE order key (see RailOrdinal) minted here is
        // carried by every message that exists, present and future, without a single family opting in.
        // Both peers ship in the same DLL (mod/build parity is a join gate, L114/L108), so there is no
        // mixed-version wire to stay compatible with — only this codec has to agree with itself, and its
        // round trip is executed by the harness.

        /// <summary>The fixed header ahead of the payload — one name so the encoder, the decoder's minimum
        /// length and any future size accounting cannot drift apart.</summary>
        internal const int HeaderBytes = 1 + 1 + 4 + 2;

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
                w.Write(RailOrdinal.Mint());
                w.Write((ushort)payload.Length);
                w.Write(payload);
                return ms.ToArray();
            }
        }

        /// <summary>Overload kept for the callers that do not care about ordering (they are the majority);
        /// the router takes the four-out form because it publishes the ordinal for the whole dispatch.</summary>
        public static bool TryDecodeEnvelope(byte[] data, out byte surfaceId, out SyncKind kind, out byte[] payload)
        {
            uint ignored;
            return TryDecodeEnvelope(data, out surfaceId, out kind, out ignored, out payload);
        }

        public static bool TryDecodeEnvelope(byte[] data, out byte surfaceId, out SyncKind kind,
                                             out uint ordinal, out byte[] payload)
        {
            surfaceId = 0; kind = SyncKind.ActionRequest; ordinal = 0; payload = null;
            // Require the full header before reading anything.
            if (data == null || data.Length < HeaderBytes) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    byte sid = r.ReadByte();
                    byte kindByte = r.ReadByte();
                    // Forward-compat: an undefined kind byte is a graceful drop, never a crash.
                    if (!Enum.IsDefined(typeof(SyncKind), kindByte)) return false;
                    uint ord = r.ReadUInt32();
                    ushort len = r.ReadUInt16();
                    // No partial accept: the declared payload length must actually be present.
                    if (ms.Length - ms.Position < len) return false;
                    surfaceId = sid;
                    kind = (SyncKind)kindByte;
                    ordinal = ord;
                    payload = r.ReadBytes(len);
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
