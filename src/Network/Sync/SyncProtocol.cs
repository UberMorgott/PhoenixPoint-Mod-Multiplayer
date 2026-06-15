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
    }
}
