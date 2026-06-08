using System;
using System.Text;

namespace Multipleer.Network.MessageLayer
{
    public class NetworkMessage
    {
        public PacketType Type { get; set; }
        public ulong SenderSteamId { get; set; }
        public Guid MessageId { get; set; }
        public byte[] Payload { get; set; }
        public long Timestamp { get; set; }

        public NetworkMessage()
        {
            MessageId = Guid.NewGuid();
            Timestamp = DateTime.UtcNow.Ticks;
        }

        public NetworkMessage(PacketType type, byte[] payload = null)
            : this()
        {
            Type = type;
            Payload = payload ?? Array.Empty<byte>();
        }

        public byte[] Serialize()
        {
            var typeByte = (byte)Type;
            var senderBytes = BitConverter.GetBytes(SenderSteamId);
            var msgIdBytes = MessageId.ToByteArray();
            var tsBytes = BitConverter.GetBytes(Timestamp);
            var payloadLenBytes = BitConverter.GetBytes(Payload?.Length ?? 0);

            var totalLen = 1 + 8 + 16 + 8 + 4 + (Payload?.Length ?? 0);
            var buffer = new byte[totalLen];
            var offset = 0;

            buffer[offset++] = typeByte;
            Array.Copy(senderBytes, 0, buffer, offset, 8); offset += 8;
            Array.Copy(msgIdBytes, 0, buffer, offset, 16); offset += 16;
            Array.Copy(tsBytes, 0, buffer, offset, 8); offset += 8;
            Array.Copy(payloadLenBytes, 0, buffer, offset, 4); offset += 4;

            if (Payload != null && Payload.Length > 0)
            {
                Array.Copy(Payload, 0, buffer, offset, Payload.Length);
            }

            return buffer;
        }

        public static NetworkMessage Deserialize(byte[] buffer)
        {
            if (buffer == null || buffer.Length < 37)
                throw new ArgumentException("Invalid message buffer");

            var msg = new NetworkMessage();
            var offset = 0;

            msg.Type = (PacketType)buffer[offset++];
            msg.SenderSteamId = BitConverter.ToUInt64(buffer, offset); offset += 8;
            var msgIdBytes = new byte[16];
            Array.Copy(buffer, offset, msgIdBytes, 0, 16);
            msg.MessageId = new Guid(msgIdBytes); offset += 16;
            msg.Timestamp = BitConverter.ToInt64(buffer, offset); offset += 8;
            var payloadLen = BitConverter.ToInt32(buffer, offset); offset += 4;

            if (payloadLen > 0)
            {
                msg.Payload = new byte[payloadLen];
                Array.Copy(buffer, offset, msg.Payload, 0, payloadLen);
            }
            else
            {
                msg.Payload = Array.Empty<byte>();
            }

            return msg;
        }

        // ─── Helper payload builders ──────────────────────────────────────

        public static byte[] BuildStringPayload(string value)
            => Encoding.UTF8.GetBytes(value ?? "");

        public static string ParseStringPayload(byte[] payload)
            => payload != null && payload.Length > 0
                ? Encoding.UTF8.GetString(payload)
                : "";

        public static byte[] BuildBoolPayload(bool value)
            => new[] { (byte)(value ? 1 : 0) };

        public static bool ParseBoolPayload(byte[] payload)
            => payload != null && payload.Length > 0 && payload[0] != 0;

        public static byte[] BuildIntPayload(int value)
            => BitConverter.GetBytes(value);

        public static int ParseIntPayload(byte[] payload)
            => payload != null && payload.Length >= 4
                ? BitConverter.ToInt32(payload, 0)
                : 0;
    }
}
