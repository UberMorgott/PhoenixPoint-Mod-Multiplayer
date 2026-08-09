using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Multiplayer.Util;

namespace Multiplayer.Network.Sync
{
    internal static class DurableInboxCodec
    {
        private const byte Schema = 3;
        private const uint LedgerMagic = 0x33495744; // DWI3, cannot alias the first four bytes of a framed ledger
        private const uint LedgerTail = 0xCCA6A8BB;
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
                    byte schema = reader.ReadByte();
                    if (schema != 1 && schema != 2 && schema != Schema) throw new InvalidDataException("unknown schema");
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

        internal static byte[] EncodeLedger(IEnumerable<InboxEntry> entries, ulong committedRevision,
            IEnumerable<KeyValuePair<MembershipId, MemberPresence>> members)
        {
            byte[] body;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, StrictUtf8))
            {
                writer.Write(committedRevision);
                var orderedMembers = members.OrderBy(pair => pair.Key).ToArray();
                WriteCount(writer, orderedMembers.Length);
                foreach (var pair in orderedMembers)
                {
                    WriteString(writer, pair.Key.PlayerGuid);
                    writer.Write(pair.Key.Epoch);
                    writer.Write((byte)pair.Value);
                }
                var ordered = entries.OrderBy(e => e.HostOrderKey).ThenBy(e => e.Occurrence).ThenBy(e => e.Membership).ToArray();
                WriteCount(writer, ordered.Length);
                foreach (var entry in ordered)
                {
                    writer.Write(entry.HostOrderKey.CampaignOrdinal);
                    WriteString(writer, entry.HostOrderKey.TriggerId);
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
                    writer.Write((byte)entry.SuspensionReason);
                    writer.Write(entry.Checkpoint != null);
                    if (entry.Checkpoint != null)
                    {
                        WriteString(writer, entry.Checkpoint.ContentPhase);
                        WriteLooseString(writer, entry.Checkpoint.Selection);
                        WriteLooseString(writer, entry.Checkpoint.ReadCheckpoint);
                        WriteLooseString(writer, entry.Checkpoint.EventId);
                        WriteLooseString(writer, entry.Checkpoint.Title);
                        WriteLooseString(writer, entry.Checkpoint.Narrative);
                        writer.Write(entry.Checkpoint.NativePriority);
                        WriteLooseString(writer, entry.Checkpoint.NativeDataIdentity);
                    }
                }
                body = stream.ToArray();
            }
            using (var framed = new MemoryStream()) using (var writer = new BinaryWriter(framed, StrictUtf8))
            {
                writer.Write(LedgerMagic); writer.Write(Schema); writer.Write(body.Length); writer.Write(body);
                writer.Write(Crc32.Compute(body)); writer.Write(LedgerTail); return framed.ToArray();
            }
        }

        internal static bool TryDecodeLedger(byte[] payload, out HostLedger ledger, out string refusal)
        {
            ledger = null; refusal = null;
            try
            {
                payload = payload ?? Array.Empty<byte>(); int ledgerSchema = 1; byte[] bodyPayload = payload;
                if (payload.Length >= 17 && BitConverter.ToUInt32(payload, 0) == LedgerMagic)
                {
                    int length = BitConverter.ToInt32(payload, 5);
                    bool framed = length >= 0 && length <= payload.Length - 17 && payload.Length == length + 17 &&
                        BitConverter.ToUInt32(payload, payload.Length - 4) == LedgerTail;
                    if (framed)
                    {
                        ledgerSchema = payload[4];
                        if (ledgerSchema != Schema) throw new InvalidDataException("unknown ledger schema");
                        bodyPayload = new byte[length]; Buffer.BlockCopy(payload, 9, bodyPayload, 0, length);
                        if (BitConverter.ToUInt32(payload, 9 + length) != Crc32.Compute(bodyPayload))
                            throw new InvalidDataException("ledger CRC mismatch");
                    }
                }
                using (var stream = new MemoryStream(bodyPayload, false))
                using (var reader = new BinaryReader(stream, StrictUtf8))
                {
                    bool extended = ledgerSchema >= 2;
                    ulong revision = reader.ReadUInt64();
                    var members = new List<KeyValuePair<MembershipId, MemberPresence>>();
                    for (int i = 0, count = ReadCount(reader); i < count; i++)
                    {
                        var member = new MembershipId(ReadString(reader), reader.ReadUInt64());
                        byte raw = reader.ReadByte();
                        if (!Enum.IsDefined(typeof(MemberPresence), (int)raw)) throw new InvalidDataException("invalid member presence");
                        members.Add(new KeyValuePair<MembershipId, MemberPresence>(member, (MemberPresence)raw));
                    }
                    var entries = new List<InboxEntry>();
                    for (int i = 0, count = ReadCount(reader); i < count; i++)
                    {
                        ulong ordinal = reader.ReadUInt64(); string orderTrigger = ReadString(reader);
                        var occurrence = ReadOccurrence(reader);
                        var membership = new MembershipId(ReadString(reader), reader.ReadUInt64());
                        byte rawLifecycle = reader.ReadByte();
                        if (!Enum.IsDefined(typeof(InboxLifecycle), rawLifecycle)) throw new InvalidDataException("invalid lifecycle");
                        ulong lifecycleRevision = reader.ReadUInt64(), tombstoneRevision = reader.ReadUInt64();
                        CanonicalChoiceId choice = default(CanonicalChoiceId);
                        if (reader.ReadBoolean())
                        {
                            var choiceOccurrence = ReadOccurrence(reader);
                            choice = new CanonicalChoiceId(choiceOccurrence, ReadString(reader));
                            if (!choiceOccurrence.Equals(occurrence)) throw new InvalidDataException("foreign choice namespace");
                        }
                        var reason = extended ? (InboxSuspensionReason)reader.ReadByte() : InboxSuspensionReason.None;
                        if (!Enum.IsDefined(typeof(InboxSuspensionReason), reason)) throw new InvalidDataException("invalid suspension reason");
                        InboxWindowCheckpoint checkpoint = null;
                        if (extended && reader.ReadBoolean())
                        {
                            string phase = ReadString(reader), selection = ReadLooseString(reader), read = ReadLooseString(reader);
                            checkpoint = ledgerSchema >= 3
                                ? new InboxWindowCheckpoint(phase, selection, read, ReadLooseString(reader),
                                    ReadLooseString(reader), ReadLooseString(reader), reader.ReadInt32(), ReadLooseString(reader))
                                : new InboxWindowCheckpoint(phase, selection, read);
                        }
                        entries.Add(new InboxEntry(occurrence, membership, (InboxLifecycle)rawLifecycle, choice,
                            lifecycleRevision, tombstoneRevision, new HostOrderKey(ordinal, orderTrigger), reason, checkpoint));
                    }
                    if (stream.Position != stream.Length) throw new InvalidDataException("trailing ledger bytes");
                    ledger = new HostLedger(entries, revision, members);
                    return true;
                }
            }
            catch (Exception ex) { refusal = ex.Message; return false; }
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

        private static void WriteLooseString(BinaryWriter writer, string value)
        {
            var bytes = StrictUtf8.GetBytes(value ?? "");
            if (bytes.Length > MaxStringBytes) throw new ArgumentOutOfRangeException(nameof(value), "string exceeds codec bound");
            writer.Write((ushort)bytes.Length); writer.Write(bytes);
        }

        private static string ReadLooseString(BinaryReader reader)
        {
            int length = reader.ReadUInt16();
            if (length > MaxStringBytes) throw new InvalidDataException("invalid string length");
            var bytes = reader.ReadBytes(length); if (bytes.Length != length) throw new EndOfStreamException();
            return new UTF8Encoding(false, true).GetString(bytes);
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
