using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>L377 / DWI-02 — transport receipt is bookkeeping, never lifecycle progress.</summary>
    internal static class L377_AReceiptAdvancesNoInboxLifecycle
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var occurrence = new OccurrenceId("event-a", "trigger-a", new[] { "subject-b", "subject-a" });
            var sibling = new OccurrenceId("event-a", "trigger-b", new[] { "subject-a", "subject-b" });
            var member = new MembershipId("player-a", 7);
            var choice = new CanonicalChoiceId(occurrence, "choice-a");
            var result = new CanonicalResultId(occurrence, "result-a");
            var rewards = new[] { new CanonicalRewardItemId(occurrence, "subject-a", "reward-a") };
            var ack = new InboxMessage(InboxMessageKind.TransportAck, occurrence, result, rewards, member,
                new HostOrderKey(11, "trigger-a"), 3, 1, InboxLifecycle.Open, choice);

            InboxMessage decoded;
            string refusal;
            var payload = DurableInboxCodec.Encode(ack);
            if (!DurableInboxCodec.TryDecode(payload, out decoded, out refusal))
            {
                yield return "L377 premise-changed: a production TransportAck did not decode: " + refusal;
                yield break;
            }

            var separatelyDecoded = DurableInboxCodec.Encode(decoded);
            InboxMessage decodedAgain;
            if (!DurableInboxCodec.TryDecode(separatelyDecoded, out decodedAgain, out refusal) ||
                !decoded.Occurrence.Equals(decodedAgain.Occurrence))
                yield return "L377 separately-decoded-subjects-differ: equal normalized subject arrays lost value equality";

            var mutableSubjects = new[] { "subject-b", "subject-a" };
            var copiedOccurrence = new OccurrenceId("event-a", "trigger-a", mutableSubjects);
            mutableSubjects[0] = "mutated-after-construction";
            if (!copiedOccurrence.Equals(occurrence) || copiedOccurrence.GetHashCode() != occurrence.GetHashCode() ||
                copiedOccurrence.CompareTo(occurrence) != 0)
                yield return "L377 occurrence-storage-not-immutable: caller mutation or unstable hash/order changed identity";

            var duplicateRewards = DuplicateFirstReward(payload);
            InboxMessage ignoredDuplicate;
            if (DurableInboxCodec.TryDecode(duplicateRewards, out ignoredDuplicate, out refusal))
                yield return "L377 codec-accepted-duplicate-reward";

            var otherMember = new MembershipId("player-b", 9);
            var ledger = new HostLedger(new[]
            {
                new InboxEntry(occurrence, member, InboxLifecycle.Open, choice, 3, 1),
                new InboxEntry(sibling, member, InboxLifecycle.Queued, default(CanonicalChoiceId), 1, 0),
                new InboxEntry(occurrence, otherMember, InboxLifecycle.Read, default(CanonicalChoiceId), 5, 2),
                new InboxEntry(sibling, otherMember, InboxLifecycle.Dismissed, default(CanonicalChoiceId), 6, 3)
            });
            var before = ledger.EncodeCanonical();
            var receipt = DurableInboxReducer.Apply(ledger, InboxCommand.FromMessage(decoded));
            if (receipt.Changed || !before.SequenceEqual(receipt.Ledger.EncodeCanonical()))
                yield return "L377 ack-advanced-lifecycle: receipt changed the exact queued/open/read/dismissed two-member matrix bytes";

            var lifecycle = DurableInboxReducer.Apply(ledger,
                InboxCommand.SetLifecycle(occurrence, member, InboxLifecycle.Read, 4));
            var expectedLifecycle = new HostLedger(new[]
            {
                new InboxEntry(occurrence, member, InboxLifecycle.Read, choice, 4, 1),
                new InboxEntry(sibling, member, InboxLifecycle.Queued, default(CanonicalChoiceId), 1, 0),
                new InboxEntry(occurrence, otherMember, InboxLifecycle.Read, default(CanonicalChoiceId), 5, 2),
                new InboxEntry(sibling, otherMember, InboxLifecycle.Dismissed, default(CanonicalChoiceId), 6, 3)
            });
            if (!lifecycle.Changed ||
                !expectedLifecycle.EncodeCanonical().SequenceEqual(lifecycle.Ledger.EncodeCanonical()))
                yield return "L377 lifecycle-not-narrow: explicit lifecycle did not change only that peer at its named occurrence";

            var sameState = DurableInboxReducer.Apply(ledger,
                InboxCommand.SetLifecycle(occurrence, member, InboxLifecycle.Open, 8));
            if (!sameState.Changed || sameState.Ledger.Get(occurrence, member).LifecycleRevision != 8)
                yield return "L377 same-lifecycle-revision-not-advanced";
            var delayed = DurableInboxReducer.Apply(sameState.Ledger,
                InboxCommand.SetLifecycle(occurrence, member, InboxLifecycle.Read, 7));
            if (delayed.Changed || delayed.Ledger.Get(occurrence, member).Lifecycle != InboxLifecycle.Open)
                yield return "L377 delayed-transition-beat-newer-same-state-revision";

            var dismissed = ledger.Get(sibling, otherMember);
            if (DurableInboxReducer.Apply(ledger,
                    InboxCommand.SetLifecycle(sibling, otherMember, InboxLifecycle.Open, 7)).Changed ||
                DurableInboxReducer.Apply(ledger,
                    InboxCommand.SetLifecycle(sibling, member, InboxLifecycle.Read, 2)).Changed ||
                DurableInboxReducer.Apply(ledger,
                    InboxCommand.SetLifecycle(occurrence, member, (InboxLifecycle)0x7f, 4)).Changed ||
                ledger.Get(sibling, otherMember).Lifecycle != dismissed.Lifecycle)
                yield return "L377 reducer-accepted-illegal-lifecycle-transition";

            // POSITIVE CONTROL: the pre-fix ACK-as-read mutation must be observable by the byte comparison above.
            var ackAsRead = DurableInboxReducer.Apply(ledger,
                InboxCommand.SetLifecycle(occurrence, member, InboxLifecycle.Read, 4));
            if (before.SequenceEqual(ackAsRead.Ledger.EncodeCanonical()))
                yield return "L377 control-not-red: ACK-as-read would escape the byte-for-byte lifecycle arm";

            // POSITIVE CONTROL: event-id-only addressing would hit both same-event occurrences.
            if (occurrence.Equals(sibling) || lifecycle.Ledger.Get(sibling, member).Lifecycle != InboxLifecycle.Queued)
                yield return "L377 control-not-red: event-id-only identity would escape the sibling occurrence arm";

            foreach (var violation in CodecRefusals(ack, payload)) yield return violation;
            foreach (var violation in EnvelopeContract()) yield return violation;
        }

        private static byte[] DuplicateFirstReward(byte[] payload)
        {
            // The sample carries one reward, so turn its count into two and duplicate those exact bytes.
            // Decode offsets through the same bounded primitives rather than pinning string lengths.
            using (var input = new System.IO.MemoryStream(payload, false))
            using (var reader = new System.IO.BinaryReader(input, System.Text.Encoding.UTF8))
            {
                reader.ReadBytes(2);
                SkipOccurrence(reader);
                SkipOccurrence(reader);
                SkipString(reader);
                long countOffset = input.Position;
                ushort count = reader.ReadUInt16();
                long rewardsOffset = input.Position;
                for (int i = 0; i < count; i++)
                {
                    SkipOccurrence(reader); SkipString(reader); SkipString(reader);
                }
                int rewardBytes = checked((int)(input.Position - rewardsOffset));
                var expanded = new byte[payload.Length + rewardBytes];
                Buffer.BlockCopy(payload, 0, expanded, 0, (int)countOffset);
                expanded[countOffset] = 2; expanded[countOffset + 1] = 0;
                Buffer.BlockCopy(payload, (int)rewardsOffset, expanded, (int)countOffset + 2, rewardBytes);
                Buffer.BlockCopy(payload, (int)rewardsOffset, expanded, (int)countOffset + 2 + rewardBytes, rewardBytes);
                Buffer.BlockCopy(payload, (int)rewardsOffset + rewardBytes, expanded,
                    (int)countOffset + 2 + rewardBytes * 2, payload.Length - (int)rewardsOffset - rewardBytes);
                return expanded;
            }
        }

        private static void SkipOccurrence(System.IO.BinaryReader reader)
        {
            SkipString(reader); SkipString(reader);
            int subjects = reader.ReadUInt16();
            for (int i = 0; i < subjects; i++) SkipString(reader);
        }

        private static void SkipString(System.IO.BinaryReader reader) => reader.ReadBytes(reader.ReadUInt16());

        private static IEnumerable<string> CodecRefusals(InboxMessage message, byte[] payload)
        {
            string refusal;
            InboxMessage ignored;
            var trailing = payload.Concat(new byte[] { 0 }).ToArray();
            if (DurableInboxCodec.TryDecode(trailing, out ignored, out refusal))
                yield return "L377 codec-accepted-trailing-bytes";

            var unknownVersion = (byte[])payload.Clone();
            unknownVersion[0] = 0x7f;
            if (DurableInboxCodec.TryDecode(unknownVersion, out ignored, out refusal))
                yield return "L377 codec-accepted-unknown-version";

            var unknownKind = (byte[])payload.Clone();
            unknownKind[1] = 0x7f;
            if (DurableInboxCodec.TryDecode(unknownKind, out ignored, out refusal))
                yield return "L377 codec-accepted-unknown-kind";

            bool accepted = true;
            try
            {
                DurableInboxCodec.Encode(new InboxMessage(message.Kind,
                    new OccurrenceId("event-a", "trigger-a", new[] { "subject-a", "subject-a" }),
                    message.ResultId, message.RewardIds, message.Membership, message.Order,
                    message.LifecycleRevision, message.TombstoneRevision, message.Lifecycle, message.ChoiceId));
            }
            catch (ArgumentException) { accepted = false; }
            if (accepted) yield return "L377 codec-accepted-duplicate-subject";

            accepted = true;
            try
            {
                DurableInboxCodec.Encode(new InboxMessage(message.Kind,
                    new OccurrenceId("event-a", "trigger-a", new string[0]),
                    message.ResultId, message.RewardIds, message.Membership, message.Order,
                    message.LifecycleRevision, message.TombstoneRevision, message.Lifecycle, message.ChoiceId));
            }
            catch (ArgumentException) { accepted = false; }
            if (accepted) yield return "L377 codec-accepted-empty-subjects";

            accepted = true;
            try
            {
                var foreign = new OccurrenceId("event-a", "foreign-trigger", new[] { "subject-a" });
                DurableInboxCodec.Encode(new InboxMessage(message.Kind, message.Occurrence,
                    new CanonicalResultId(foreign, "result-a"), message.RewardIds, message.Membership,
                    message.Order, message.LifecycleRevision, message.TombstoneRevision,
                    message.Lifecycle, message.ChoiceId));
            }
            catch (ArgumentException) { accepted = false; }
            if (accepted) yield return "L377 codec-accepted-foreign-canonical-namespace";

            accepted = true;
            try
            {
                DurableInboxCodec.Encode(new InboxMessage(message.Kind, message.Occurrence,
                    new CanonicalResultId(message.Occurrence, "\uD800"), message.RewardIds,
                    message.Membership, message.Order, message.LifecycleRevision,
                    message.TombstoneRevision, message.Lifecycle, message.ChoiceId));
            }
            catch (System.Text.EncoderFallbackException) { accepted = false; }
            if (accepted) yield return "L377 codec-accepted-malformed-utf16";
        }

        private static IEnumerable<string> EnvelopeContract()
        {
            var encode = typeof(SyncProtocol).GetMethods(All)
                .Where(m => m.Name == "EncodeEnvelope").ToArray();
            if (encode.Length != 1 || !encode[0].GetParameters().Select(p => p.ParameterType)
                    .SequenceEqual(new[] { typeof(byte), typeof(SyncKind), typeof(byte[]) }))
                yield return "L377 premise-changed: SyncProtocol.EncodeEnvelope is not the sole three-argument encoder";

            var mint = typeof(RailOrdinal).GetMethod("Mint", All);
            if (mint == null || mint.IsPublic || mint.ReturnType != typeof(uint) || mint.GetParameters().Length != 0)
                yield return "L377 premise-changed: internal RailOrdinal.Mint() no longer owns ordinal creation";

            RailOrdinal.Reset();
            var envelope = SyncProtocol.EncodeEnvelope(0xB9, SyncKind.StateDelta, new byte[] { 0x46, 0x55 });
            if (envelope.Length != SyncProtocol.HeaderBytes + 2 || envelope[0] != 0xB9 ||
                envelope[1] != (byte)SyncKind.StateDelta || BitConverter.ToUInt32(envelope, 2) != 1 ||
                BitConverter.ToUInt16(envelope, 6) != 2 || envelope[8] != 0x46 || envelope[9] != 0x55)
                yield return "L377 envelope-header-drift: expected surface, kind, minted ordinal, u16 length, payload";

            bool accepted = true;
            try { SyncProtocol.EncodeEnvelope(0xB9, SyncKind.StateDelta, new byte[ushort.MaxValue + 1]); }
            catch (ArgumentOutOfRangeException) { accepted = false; }
            if (accepted) yield return "L377 envelope-accepted-more-than-u16";
        }
    }
}
