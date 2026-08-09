using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Multiplayer.Network.Sync
{
    internal static class DurableInboxCodec
    {
        private const byte Schema = 1;
        private const int MaxStringBytes = 4096;
        private const int MaxCollection = 1024;
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        internal static byte[] Encode(InboxMessage message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, StrictUtf8))
            {
                writer.Write(Schema);
                writer.Write((byte)message.Kind);
                WriteOccurrence(writer, message.Occurrence);
                WriteOccurrence(writer, message.ResultId.Occurrence);
                WriteString(writer, message.ResultId.Value);
                WriteCount(writer, message.RewardIds.Count);
                foreach (var reward in message.RewardIds)
                {
                    WriteOccurrence(writer, reward.Occurrence);
                    WriteString(writer, reward.SubjectId);
                    WriteString(writer, reward.Value);
                }
                WriteString(writer, message.Membership.PlayerGuid);
                writer.Write(message.Membership.Epoch);
                writer.Write(message.Order.CampaignOrdinal);
                WriteString(writer, message.Order.TriggerId);
                writer.Write(message.LifecycleRevision);
                writer.Write(message.TombstoneRevision);
                writer.Write((byte)message.Lifecycle);
                writer.Write(message.ChoiceId.Value != null);
                if (message.ChoiceId.Value != null)
                {
                    WriteOccurrence(writer, message.ChoiceId.Occurrence);
                    WriteString(writer, message.ChoiceId.Value);
                }
                return stream.ToArray();
            }
        }

        internal static bool TryDecode(byte[] payload, out InboxMessage message, out string refusal)
        {
            message = null;
            refusal = null;
            if (payload == null) { refusal = "payload is null"; return false; }
            try
            {
                using (var stream = new MemoryStream(payload, false))
                using (var reader = new BinaryReader(stream, StrictUtf8))
                {
                    if (reader.ReadByte() != Schema) throw new InvalidDataException("unknown schema");
                    var kind = (InboxMessageKind)reader.ReadByte();
                    if (!Enum.IsDefined(typeof(InboxMessageKind), kind)) throw new InvalidDataException("unknown kind");
                    var occurrence = ReadOccurrence(reader);
                    var result = new CanonicalResultId(ReadOccurrence(reader), ReadString(reader));
                    var rewards = new CanonicalRewardItemId[ReadCount(reader)];
                    for (int i = 0; i < rewards.Length; i++)
                        rewards[i] = new CanonicalRewardItemId(ReadOccurrence(reader), ReadString(reader), ReadString(reader));
                    var membership = new MembershipId(ReadString(reader), reader.ReadUInt64());
                    var order = new HostOrderKey(reader.ReadUInt64(), ReadString(reader));
                    ulong lifecycleRevision = reader.ReadUInt64();
                    ulong tombstoneRevision = reader.ReadUInt64();
                    var lifecycle = (InboxLifecycle)reader.ReadByte();
                    if (!Enum.IsDefined(typeof(InboxLifecycle), lifecycle)) throw new InvalidDataException("unknown lifecycle");
                    var choice = reader.ReadBoolean()
                        ? new CanonicalChoiceId(ReadOccurrence(reader), ReadString(reader))
                        : default(CanonicalChoiceId);
                    if (stream.Position != stream.Length) throw new InvalidDataException("trailing bytes");
                    message = new InboxMessage(kind, occurrence, result, rewards, membership, order,
                        lifecycleRevision, tombstoneRevision, lifecycle, choice);
                    return true;
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidDataException ||
                                       ex is EndOfStreamException || ex is IOException || ex is OverflowException)
            {
                refusal = ex.Message;
                return false;
            }
        }

        internal static byte[] EncodeLedger(IEnumerable<InboxEntry> entries)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, StrictUtf8))
            {
                var ordered = entries.OrderBy(e => e.Occurrence).ThenBy(e => e.Membership).ToArray();
                WriteCount(writer, ordered.Length);
                foreach (var entry in ordered)
                {
                    WriteOccurrence(writer, entry.Occurrence);
                    WriteString(writer, entry.Membership.PlayerGuid);
                    writer.Write(entry.Membership.Epoch);
                    writer.Write((byte)entry.Lifecycle);
                    writer.Write(entry.LifecycleRevision);
                    writer.Write(entry.TombstoneRevision);
                    writer.Write(entry.Choice.Value != null);
                    if (entry.Choice.Value != null)
                    {
                        WriteOccurrence(writer, entry.Choice.Occurrence);
                        WriteString(writer, entry.Choice.Value);
                    }
                }
                return stream.ToArray();
            }
        }

        private static void WriteOccurrence(BinaryWriter writer, OccurrenceId occurrence)
        {
            WriteString(writer, occurrence.EventId);
            WriteString(writer, occurrence.TriggerId);
            WriteCount(writer, occurrence.SubjectIds.Count);
            foreach (var subject in occurrence.SubjectIds) WriteString(writer, subject);
        }

        private static OccurrenceId ReadOccurrence(BinaryReader reader)
        {
            string eventId = ReadString(reader);
            string triggerId = ReadString(reader);
            var subjects = new string[ReadCount(reader)];
            for (int i = 0; i < subjects.Length; i++) subjects[i] = ReadString(reader);
            return new OccurrenceId(eventId, triggerId, subjects);
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            var bytes = StrictUtf8.GetBytes(InboxIdentity.Required(value, nameof(value)));
            if (bytes.Length > MaxStringBytes) throw new ArgumentOutOfRangeException(nameof(value), "string exceeds codec bound");
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadString(BinaryReader reader)
        {
            int length = reader.ReadUInt16();
            if (length == 0 || length > MaxStringBytes) throw new InvalidDataException("invalid string length");
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            var value = new UTF8Encoding(false, true).GetString(bytes);
            return InboxIdentity.Required(value, "wire string");
        }

        private static void WriteCount(BinaryWriter writer, int count)
        {
            if (count < 0 || count > MaxCollection) throw new ArgumentOutOfRangeException(nameof(count));
            writer.Write((ushort)count);
        }

        private static int ReadCount(BinaryReader reader)
        {
            int count = reader.ReadUInt16();
            if (count > MaxCollection) throw new InvalidDataException("collection exceeds codec bound");
            return count;
        }
    }
}
