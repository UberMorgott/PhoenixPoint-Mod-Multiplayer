using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Base.Serialization.General;
using Base.Levels;
using Base.Core;
using ByRefBytes = Base.Utils.ByRef<byte[]>;
using ByRefObjects = Base.Utils.ByRef<System.Collections.Generic.IEnumerable<object>>;
using TimeSlice = Base.Utils.TimeSlice;
using HarmonyLib;
using Multiplayer.Util;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Levels;

namespace Multiplayer.Network.Sync
{
    internal interface IDurableInboxStableResolver
    {
        bool SubjectExists(string subjectId);
        bool ChoiceExists(OccurrenceId occurrence, string choiceId);
        bool ResultExists(OccurrenceId occurrence, string resultId);
        bool RewardExists(OccurrenceId occurrence, string subjectId, string rewardId);
    }

    internal sealed class DurableInboxCanonicalState
    {
        internal static DurableInboxCanonicalState Empty { get; } = new DurableInboxCanonicalState(
            Array.Empty<CanonicalChoiceId>(), Array.Empty<CanonicalResultId>(), Array.Empty<CanonicalRewardItemId>());
        internal DurableInboxCanonicalState(IEnumerable<CanonicalChoiceId> choices,
            IEnumerable<CanonicalResultId> results, IEnumerable<CanonicalRewardItemId> rewards,
            IEnumerable<SharedChoiceDecision> decisions = null)
        {
            Choices = new ReadOnlyCollection<CanonicalChoiceId>((choices ?? throw new ArgumentNullException(nameof(choices))).Distinct().OrderBy(x => x).ToArray());
            Results = new ReadOnlyCollection<CanonicalResultId>((results ?? throw new ArgumentNullException(nameof(results))).Distinct().OrderBy(x => x).ToArray());
            Rewards = new ReadOnlyCollection<CanonicalRewardItemId>((rewards ?? throw new ArgumentNullException(nameof(rewards))).Distinct().OrderBy(x => x).ToArray());
            var decisionCopy = (decisions ?? Enumerable.Empty<SharedChoiceDecision>()).ToArray();
            if (decisionCopy.Any(x => x == null) || decisionCopy.GroupBy(x => x.Occurrence).Any(x => x.Count() != 1))
                throw new ArgumentException("duplicate shared-choice decision", nameof(decisions));
            Decisions = new ReadOnlyCollection<SharedChoiceDecision>(decisionCopy.OrderBy(x => x.Occurrence).ToArray());
            if (decisionCopy.Any(d => !Choices.Contains(d.Choice) || !Results.Contains(d.Result) ||
                d.Rewards.Any(r => !Rewards.Contains(r))))
                throw new ArgumentException("shared-choice decision identities are not canonical", nameof(decisions));
        }
        internal IReadOnlyList<CanonicalChoiceId> Choices { get; }
        internal IReadOnlyList<CanonicalResultId> Results { get; }
        internal IReadOnlyList<CanonicalRewardItemId> Rewards { get; }
        internal IReadOnlyList<SharedChoiceDecision> Decisions { get; }
        internal DurableInboxCanonicalState With(OccurrenceId occurrence, CanonicalChoiceId choice,
            CanonicalResultId result, IEnumerable<CanonicalRewardItemId> rewards)
        {
            if (!choice.Occurrence.Equals(occurrence) || !result.Occurrence.Equals(occurrence))
                throw new ArgumentException("foreign canonical namespace");
            var rewardArray = (rewards ?? throw new ArgumentNullException(nameof(rewards))).ToArray();
            if (rewardArray.Any(x => !x.Occurrence.Equals(occurrence))) throw new ArgumentException("foreign reward namespace");
            return new DurableInboxCanonicalState(
                Choices.Where(x => !x.Occurrence.Equals(occurrence)).Concat(new[] { choice }),
                Results.Where(x => !x.Occurrence.Equals(occurrence)).Concat(new[] { result }),
                Rewards.Where(x => !x.Occurrence.Equals(occurrence)).Concat(rewardArray), Decisions);
        }
        internal DurableInboxCanonicalState WithDecision(SharedChoiceDecision decision)
        {
            if (decision == null) throw new ArgumentNullException(nameof(decision));
            return new DurableInboxCanonicalState(
                Choices.Where(x => !x.Occurrence.Equals(decision.Occurrence)).Concat(new[] { decision.Choice }),
                Results.Where(x => !x.Occurrence.Equals(decision.Occurrence)).Concat(new[] { decision.Result }),
                Rewards.Where(x => !x.Occurrence.Equals(decision.Occurrence)).Concat(decision.Rewards),
                Decisions.Where(x => !x.Occurrence.Equals(decision.Occurrence)).Concat(new[] { decision }));
        }
        internal DurableInboxCanonicalState Without(ISet<OccurrenceId> occurrences) => new DurableInboxCanonicalState(
            Choices.Where(x => !occurrences.Contains(x.Occurrence)), Results.Where(x => !occurrences.Contains(x.Occurrence)),
            Rewards.Where(x => !occurrences.Contains(x.Occurrence)), Decisions.Where(x => !occurrences.Contains(x.Occurrence)));
        internal DurableInboxCanonicalState WithoutDecision(OccurrenceId occurrence) => new DurableInboxCanonicalState(
            Choices.Where(x => !x.Occurrence.Equals(occurrence)), Results.Where(x => !x.Occurrence.Equals(occurrence)),
            Rewards.Where(x => !x.Occurrence.Equals(occurrence)), Decisions.Where(x => !x.Occurrence.Equals(occurrence)));
    }

    [SerializeType(SerializeMembersByDefault = SerializeMembersType.SerializeAll)]
    public sealed class DurableInboxSaveRoot
    {
        public string Magic = DurableInboxSaveCodec.Magic;
        public int Schema = DurableInboxSaveCodec.Schema;
        public byte[] Snapshot = Array.Empty<byte>();
        public byte[][] Journal = Array.Empty<byte[]>();
        public ulong SnapshotRevision;
        public uint Crc;
        // Schema 6: the checkpoint identity is part of the Phoenix object graph.  It is deliberately
        // not kept in PlatformData or a companion file: copying/deleting the save cannot detach its
        // transaction proof.
        public byte TransactionKind = byte.MaxValue;
        public string TransactionCheckpointId;
        public string TransactionPendingCheckpointId;
        public string TransactionEventId;
        public string TransactionTriggerId;
        public string[] TransactionSubjectIds = Array.Empty<string>();
        public string TransactionEffectToken;
        public ulong TransactionSharedRevision;
        public bool TransactionWasIronman;
        public string TransactionPredecessorSaveLocator;
        public string[] RetiredEffectTokens = Array.Empty<string>();
        public string RetiredCatalogGameId;
    }

    internal sealed class DurableInboxRestore
    {
        internal DurableInboxRestore(HostLedger ledger, HostLedger snapshotLedger, DurableInboxCanonicalState canonical,
            DurableInboxCanonicalState snapshotCanonical,
            IEnumerable<DurableInboxJournalRecord> journal, IEnumerable<OccurrenceId> quarantine)
        {
            Ledger = ledger;
            SnapshotLedger = snapshotLedger;
            Canonical = canonical;
            SnapshotCanonical = snapshotCanonical;
            Journal = new ReadOnlyCollection<DurableInboxJournalRecord>(journal.ToArray());
            Quarantine = new ReadOnlyCollection<OccurrenceId>(quarantine.Distinct().OrderBy(x => x).ToArray());
        }
        internal HostLedger Ledger { get; }
        internal HostLedger SnapshotLedger { get; }
        internal DurableInboxCanonicalState Canonical { get; }
        internal DurableInboxCanonicalState SnapshotCanonical { get; }
        internal IReadOnlyList<DurableInboxJournalRecord> Journal { get; }
        internal IReadOnlyList<OccurrenceId> Quarantine { get; }
        internal bool IsServable(OccurrenceId occurrence) => !Quarantine.Contains(occurrence);
    }

    internal static class DurableInboxSaveCodec
    {
        internal const string Magic = "Multiplayer.DurableInbox/v1";
        internal const int Schema = 9;
        private const int MaxCount = 65535;
        private const int MaxString = 4096;

        internal static DurableInboxSaveRoot Create(HostLedger ledger,
            IEnumerable<DurableInboxJournalRecord> journal, DurableInboxCanonicalState canonical)
        {
            if (ledger == null) throw new ArgumentNullException(nameof(ledger));
            var root = new DurableInboxSaveRoot
            {
                Snapshot = EncodeSnapshot(ledger, canonical?.Choices, canonical?.Results, canonical?.Rewards, canonical?.Decisions),
                Journal = (journal ?? Enumerable.Empty<DurableInboxJournalRecord>()).Select(EncodeJournal).ToArray(),
                SnapshotRevision = ledger.CommittedRevision
            };
            DurableEffectTransactionBarrier.StampOwnerRoot(root);
            DurableEffectSaveCatalog.CaptureNormalRoot(root, canonical?.Decisions);
            root.Crc = ComputeRootCrc(root);
            return root;
        }

        internal static DurableInboxSaveRoot CreateSchema1ForMigrationTest(HostLedger ledger)
        {
            if (ledger == null) throw new ArgumentNullException(nameof(ledger));
            byte[] snapshot;
            using (var ms = new MemoryStream()) using (var w = new BinaryWriter(ms))
            {
                w.Write(ledger.CommittedRevision); WriteCount(w, ledger.Members.Count);
                foreach (var pair in ledger.Members.OrderBy(x => x.Key)) { WriteMembership(w, pair.Key); w.Write((byte)pair.Value); }
                WriteCount(w, ledger.AllEntries.Count);
                foreach (var e in ledger.AllEntries.OrderBy(x => x.HostOrderKey).ThenBy(x => x.Occurrence))
                {
                    WriteOccurrence(w, e.Occurrence); WriteMembership(w, e.Membership); w.Write((byte)e.Lifecycle);
                    WriteOptionalChoice(w, e.Choice); w.Write(e.LifecycleRevision); w.Write(e.TombstoneRevision);
                    w.Write(e.HostOrderKey.CampaignOrdinal); WriteString(w, e.HostOrderKey.TriggerId);
                }
                WriteCount(w, 0); WriteCount(w, 0); WriteCount(w, 0); snapshot = ms.ToArray();
            }
            var root = new DurableInboxSaveRoot { Schema = 1, Snapshot = snapshot, Journal = Array.Empty<byte[]>(), SnapshotRevision = ledger.CommittedRevision };
            root.Crc = ComputeRootCrc(root); return root;
        }

        internal static DurableInboxSaveRoot CreateSchema6ForMigrationTest(HostLedger ledger)
        {
            var root = new DurableInboxSaveRoot { Schema = 6,
                Snapshot = EncodeSnapshot(ledger, Array.Empty<CanonicalChoiceId>(), Array.Empty<CanonicalResultId>(),
                    Array.Empty<CanonicalRewardItemId>(), Array.Empty<SharedChoiceDecision>(), false, false),
                Journal = Array.Empty<byte[]>(), SnapshotRevision = ledger.CommittedRevision };
            root.Crc = ComputeRootCrc(root); return root;
        }
        internal static DurableInboxSaveRoot CreateSchema7ForMigrationTest(HostLedger ledger)
        {
            var root = new DurableInboxSaveRoot { Schema = 7,
                Snapshot = EncodeSnapshot(ledger, Array.Empty<CanonicalChoiceId>(), Array.Empty<CanonicalResultId>(),
                    Array.Empty<CanonicalRewardItemId>(), Array.Empty<SharedChoiceDecision>(), false, false),
                Journal = Array.Empty<byte[]>(), SnapshotRevision = ledger.CommittedRevision };
            root.Crc = ComputeRootCrc(root); return root;
        }

        internal static bool TryRestore(DurableInboxSaveRoot root, IDurableInboxStableResolver resolver,
            out DurableInboxRestore restore, out string refusal)
        {
            restore = null; refusal = null;
            try
            {
                if (root == null || root.Magic != Magic ||
                    (root.Schema < 1 || root.Schema > Schema))
                    throw new InvalidDataException("unknown durable inbox save root");
                if (root.Snapshot == null || root.Journal == null || root.Crc != ComputeRootCrc(root)) throw new InvalidDataException("durable inbox CRC mismatch");
                if (root.Schema >= 6) ValidateTransactionMarker(root);
                if (root.Schema >= 7)
                {
                    var retired = root.RetiredEffectTokens ?? Array.Empty<string>();
                    if (retired.Length > MaxCount || retired.Distinct(StringComparer.Ordinal).Count() != retired.Length)
                        throw new InvalidDataException("invalid retired effect-token catalog");
                    foreach (var encoded in retired)
                    { EffectToken ignored; if (!DurableEffectSaveCatalog.TryDecodeToken(encoded, out ignored))
                        throw new InvalidDataException("malformed retired effect token"); }
                }
                HostLedger ledger; List<CanonicalChoiceId> choices; List<CanonicalResultId> results; List<CanonicalRewardItemId> rewards;
                List<SharedChoiceDecision> decisions;
                DecodeSnapshot(root.Snapshot, root.Schema, out ledger, out choices, out results, out rewards, out decisions);
                if (ledger.CommittedRevision != root.SnapshotRevision) throw new InvalidDataException("snapshot revision mismatch");
                var snapshotLedger = ledger;
                var snapshotCanonical = new DurableInboxCanonicalState(choices, results, rewards, decisions);
                var records = root.Journal.Select(bytes => DecodeJournal(bytes, root.Schema)).OrderBy(r => r.Revision).ToList();
                ulong previous = root.SnapshotRevision;
                foreach (var record in records)
                {
                    HostLedger replayed; List<CanonicalChoiceId> replayChoices; List<CanonicalResultId> replayResults;
                    List<CanonicalRewardItemId> replayRewards; List<SharedChoiceDecision> replayDecisions; string replayRefusal = null;
                    if (previous == ulong.MaxValue || record.Revision != previous + 1 ||
                        record.Crc != Crc32.Compute(record.Payload) ||
                        !TryDecodeJournalCandidate(record.Payload, root.Schema, out replayed, out replayChoices, out replayResults,
                            out replayRewards, out replayDecisions, out replayRefusal) ||
                        replayed.CommittedRevision != record.Revision)
                        throw new InvalidDataException("invalid journal sequence: " + replayRefusal);
                    ledger = replayed;
                    choices = replayChoices; results = replayResults; rewards = replayRewards; decisions = replayDecisions;
                    previous = record.Revision;
                }
                var quarantine = new HashSet<OccurrenceId>();
                if (resolver != null)
                {
                    var allOccurrences = ledger.AllEntries.Select(x => x.Occurrence)
                        .Concat(choices.Select(x => x.Occurrence)).Concat(results.Select(x => x.Occurrence))
                        .Concat(rewards.Select(x => x.Occurrence)).Distinct().OrderBy(x => x).ToArray();
                    foreach (var occurrence in allOccurrences)
                    {
                        bool inLedger = ledger.Contains(occurrence);
                        bool valid = inLedger && occurrence.SubjectIds.All(resolver.SubjectExists) &&
                            ledger.AllEntries.Where(x => x.Occurrence.Equals(occurrence) && x.Choice.Value != null)
                                .All(x => resolver.ChoiceExists(occurrence, x.Choice.Value)) &&
                            choices.Where(x => x.Occurrence.Equals(occurrence)).All(x => resolver.ChoiceExists(occurrence, x.Value)) &&
                            results.Where(x => x.Occurrence.Equals(occurrence)).All(x => resolver.ResultExists(occurrence, x.Value)) &&
                            rewards.Where(x => x.Occurrence.Equals(occurrence)).All(x =>
                                resolver.SubjectExists(x.SubjectId) && resolver.RewardExists(occurrence, x.SubjectId, x.Value));
                        if (!valid) quarantine.Add(occurrence);
                    }
                }
                // Quarantine is a presentation/reconciliation filter only. Durable truth stays byte-for-byte
                // available for a later resolver pass and the next campaign save.
                var canonical = new DurableInboxCanonicalState(choices, results, rewards, decisions);
                restore = new DurableInboxRestore(ledger, snapshotLedger, canonical, snapshotCanonical, records, quarantine);
                return true;
            }
            catch (Exception ex) { refusal = ex.Message; return false; }
        }

        private static byte[] EncodeSnapshot(HostLedger ledger, IEnumerable<CanonicalChoiceId> choices,
            IEnumerable<CanonicalResultId> results, IEnumerable<CanonicalRewardItemId> rewards,
            IEnumerable<SharedChoiceDecision> decisions, bool includeLinks = true,
            bool includePreparationRevision = true)
        {
            using (var ms = new MemoryStream()) using (var w = new BinaryWriter(ms))
            {
                w.Write(ledger.CommittedRevision);
                WriteCount(w, ledger.Members.Count);
                foreach (var pair in ledger.Members.OrderBy(x => x.Key)) { WriteMembership(w, pair.Key); w.Write((byte)pair.Value); }
                WriteCount(w, ledger.AllEntries.Count);
                foreach (var e in ledger.AllEntries.OrderBy(x => x.HostOrderKey).ThenBy(x => x.Occurrence))
                {
                    WriteOccurrence(w, e.Occurrence); WriteMembership(w, e.Membership); w.Write((byte)e.Lifecycle);
                    WriteOptionalChoice(w, e.Choice); w.Write(e.LifecycleRevision); w.Write(e.TombstoneRevision);
                    w.Write(e.HostOrderKey.CampaignOrdinal); WriteString(w, e.HostOrderKey.TriggerId);
                    if (e.TombstoneRevision != 0)
                    {
                        w.Write(e.TerminalReason.HasValue);
                        if (e.TerminalReason.HasValue) w.Write((byte)e.TerminalReason.Value);
                    }
                    w.Write((byte)e.SuspensionReason); WriteOptionalCheckpoint(w, e.Checkpoint);
                    if (includeLinks)
                    {
                        w.Write(e.SupersededBy.HasValue); if (e.SupersededBy.HasValue) WriteOccurrence(w, e.SupersededBy.Value);
                        w.Write(e.Predecessor.HasValue); if (e.Predecessor.HasValue) WriteOccurrence(w, e.Predecessor.Value);
                    }
                    if (includePreparationRevision) { w.Write(e.PreparationRevision); w.Write(e.PreparationAuthorityRevision); }
                }
                WriteIdentities(w, choices ?? Enumerable.Empty<CanonicalChoiceId>(), (bw, x) => { WriteOccurrence(bw, x.Occurrence); WriteString(bw, x.Value); });
                WriteIdentities(w, results ?? Enumerable.Empty<CanonicalResultId>(), (bw, x) => { WriteOccurrence(bw, x.Occurrence); WriteString(bw, x.Value); });
                WriteIdentities(w, rewards ?? Enumerable.Empty<CanonicalRewardItemId>(), (bw, x) => { WriteOccurrence(bw, x.Occurrence); WriteString(bw, x.SubjectId); WriteString(bw, x.Value); });
                WriteIdentities(w, decisions ?? Enumerable.Empty<SharedChoiceDecision>(), WriteDecision);
                return ms.ToArray();
            }
        }

        internal static byte[] EncodeJournalCandidate(HostLedger ledger, DurableInboxCanonicalState canonical) =>
            EncodeSnapshot(ledger, canonical.Choices, canonical.Results, canonical.Rewards, canonical.Decisions);

        private static bool TryDecodeJournalCandidate(byte[] payload, int schema, out HostLedger ledger,
            out List<CanonicalChoiceId> choices, out List<CanonicalResultId> results,
            out List<CanonicalRewardItemId> rewards, out List<SharedChoiceDecision> decisions, out string refusal)
        {
            ledger = null; choices = null; results = null; rewards = null; decisions = null; refusal = null;
            try { DecodeSnapshot(payload, schema, out ledger, out choices, out results, out rewards, out decisions); return true; }
            catch (Exception ex) { refusal = ex.Message; return false; }
        }

        private static void DecodeSnapshot(byte[] bytes, int schema, out HostLedger ledger, out List<CanonicalChoiceId> choices,
            out List<CanonicalResultId> results, out List<CanonicalRewardItemId> rewards,
            out List<SharedChoiceDecision> decisions)
        {
            using (var ms = new MemoryStream(bytes, false)) using (var r = new BinaryReader(ms))
            {
                ulong revision = r.ReadUInt64();
                var members = new List<KeyValuePair<MembershipId, MemberPresence>>();
                for (int i = 0, n = ReadCount(r); i < n; i++) members.Add(new KeyValuePair<MembershipId, MemberPresence>(ReadMembership(r), ReadEnum<MemberPresence>(r)));
                var entries = new List<InboxEntry>();
                for (int i = 0, n = ReadCount(r); i < n; i++)
                {
                    var occurrence = ReadOccurrence(r); var member = ReadMembership(r); var lifecycle = ReadEnum<InboxLifecycle>(r);
                    var choice = ReadOptionalChoice(r, occurrence); ulong lifecycleRevision = r.ReadUInt64(); ulong tombstone = r.ReadUInt64();
                    var order = new HostOrderKey(r.ReadUInt64(), ReadString(r));
                    TerminalReason? terminalReason = null;
                    if (schema >= 4 && tombstone != 0 && r.ReadBoolean()) terminalReason = ReadEnum<TerminalReason>(r);
                    var reason = schema >= 2 ? ReadEnum<InboxSuspensionReason>(r) : InboxSuspensionReason.None;
                    var checkpoint = schema >= 2 ? ReadOptionalCheckpoint(r, schema) : null;
                    OccurrenceId? supersededBy = null, predecessor = null;
                    if (schema >= 8)
                    {
                        if (r.ReadBoolean()) supersededBy = ReadOccurrence(r);
                        if (r.ReadBoolean()) predecessor = ReadOccurrence(r);
                    }
                    ulong preparationRevision = schema >= 9 ? r.ReadUInt64() : 0;
                    ulong preparationAuthorityRevision = schema >= 9 ? r.ReadUInt64() : 0;
                    entries.Add(new InboxEntry(occurrence, member, lifecycle, choice, lifecycleRevision, tombstone, order,
                        reason, checkpoint, terminalReason, supersededBy, predecessor, preparationRevision,
                        preparationAuthorityRevision));
                }
                choices = ReadIdentities(r, br => { var o = ReadOccurrence(br); return new CanonicalChoiceId(o, ReadString(br)); });
                results = ReadIdentities(r, br => { var o = ReadOccurrence(br); return new CanonicalResultId(o, ReadString(br)); });
                rewards = ReadIdentities(r, br => { var o = ReadOccurrence(br); return new CanonicalRewardItemId(o, ReadString(br), ReadString(br)); });
                decisions = schema >= 5 ? ReadIdentities(r, br => ReadDecision(br, schema)) : new List<SharedChoiceDecision>();
                if (ms.Position != ms.Length) throw new InvalidDataException("trailing snapshot bytes");
                ledger = schema < 4
                    ? LegacyMembershipMigration.EndDisconnected(entries, revision, members)
                    : new HostLedger(entries, revision, members);
            }
        }

        private static byte[] EncodeJournal(DurableInboxJournalRecord record)
        { using (var ms = new MemoryStream()) using (var w = new BinaryWriter(ms)) { w.Write(record.Revision); var p = record.Payload; w.Write(p.Length); w.Write(p); w.Write(record.Crc); return ms.ToArray(); } }
        private static DurableInboxJournalRecord DecodeJournal(byte[] bytes, int schema)
        {
            using (var ms = new MemoryStream(bytes ?? Array.Empty<byte>(), false)) using (var r = new BinaryReader(ms))
            {
                ulong rev = r.ReadUInt64(); int n = r.ReadInt32();
                if (n < 0 || n > ms.Length - ms.Position - 4) throw new InvalidDataException("journal length");
                var p = r.ReadBytes(n); uint crc = r.ReadUInt32();
                if (ms.Position != ms.Length || crc != Crc32.Compute(p)) throw new InvalidDataException("journal CRC");
                HostLedger decoded; List<CanonicalChoiceId> c; List<CanonicalResultId> result;
                List<CanonicalRewardItemId> reward; List<SharedChoiceDecision> decisions; string refusal;
                if (!TryDecodeJournalCandidate(p, schema, out decoded, out c, out result, out reward, out decisions, out refusal) || decoded.CommittedRevision != rev)
                    throw new InvalidDataException("journal ledger: " + refusal);
                return new DurableInboxJournalRecord(rev, p, decoded.AllEntries.Select(x => x.Occurrence));
            }
        }
        private static uint ComputeRootCrc(DurableInboxSaveRoot root)
        { using (var ms = new MemoryStream()) using (var w = new BinaryWriter(ms)) { WriteString(w, root.Magic); w.Write(root.Schema); w.Write(root.SnapshotRevision); WriteBytes(w, root.Snapshot); WriteCount(w, root.Journal.Length); foreach (var p in root.Journal) WriteBytes(w, p); if (root.Schema >= 6) { w.Write(root.TransactionKind); WriteNullableString(w, root.TransactionCheckpointId); WriteNullableString(w, root.TransactionPendingCheckpointId); WriteNullableString(w, root.TransactionEventId); WriteNullableString(w, root.TransactionTriggerId); var subjects = root.TransactionSubjectIds ?? Array.Empty<string>(); WriteCount(w, subjects.Length); foreach (var subject in subjects) WriteNullableString(w, subject); WriteNullableString(w, root.TransactionEffectToken); w.Write(root.TransactionSharedRevision); w.Write(root.TransactionWasIronman); if (root.Schema >= 7) { WriteNullableString(w, root.TransactionPredecessorSaveLocator); WriteNullableString(w, root.RetiredCatalogGameId); var retired = root.RetiredEffectTokens ?? Array.Empty<string>(); WriteCount(w, retired.Length); foreach (var item in retired) WriteNullableString(w, item); } } return Crc32.Compute(ms.ToArray()); } }
        private static void ValidateTransactionMarker(DurableInboxSaveRoot root)
        {
            bool absent = root.TransactionKind == byte.MaxValue && string.IsNullOrEmpty(root.TransactionCheckpointId) &&
                string.IsNullOrEmpty(root.TransactionPendingCheckpointId) && string.IsNullOrEmpty(root.TransactionEventId) &&
                string.IsNullOrEmpty(root.TransactionTriggerId) && string.IsNullOrEmpty(root.TransactionEffectToken) &&
                root.TransactionSharedRevision == 0 && !root.TransactionWasIronman &&
                string.IsNullOrEmpty(root.TransactionPredecessorSaveLocator) &&
                (root.TransactionSubjectIds == null || root.TransactionSubjectIds.Length == 0);
            if (absent) return;
            if (root.TransactionKind > (byte)DurableEffectCheckpointKind.Committed)
                throw new InvalidDataException("invalid durable transaction checkpoint kind");
            if (root.Schema >= 7 && string.IsNullOrEmpty(root.TransactionPredecessorSaveLocator))
                throw new InvalidDataException("durable transaction omitted exact predecessor save locator");
            try
            {
                var occurrence = new OccurrenceId(root.TransactionEventId, root.TransactionTriggerId,
                    root.TransactionSubjectIds ?? Array.Empty<string>());
                var token = new EffectToken(occurrence, root.TransactionEffectToken);
                var checkpoint = new DurableEffectCheckpoint(root.TransactionCheckpointId,
                    (DurableEffectCheckpointKind)root.TransactionKind, occurrence, token,
                    root.TransactionPendingCheckpointId);
                if (root.Schema >= 7 && !string.IsNullOrEmpty(root.TransactionPredecessorSaveLocator))
                    checkpoint.BindPredecessor(root.TransactionPredecessorSaveLocator);
                if (root.TransactionKind == (byte)DurableEffectCheckpointKind.Pending && root.TransactionSharedRevision != 0)
                    throw new InvalidDataException("PRE checkpoint cannot contain a locked shared revision");
                if (root.TransactionKind == (byte)DurableEffectCheckpointKind.Committed && root.TransactionSharedRevision == 0)
                    throw new InvalidDataException("POST checkpoint omitted its locked shared revision");
            }
            catch (Exception ex) when (!(ex is InvalidDataException))
            { throw new InvalidDataException("invalid durable transaction checkpoint marker", ex); }
        }
        private static void WriteBytes(BinaryWriter w, byte[] p) { p = p ?? Array.Empty<byte>(); w.Write(p.Length); w.Write(p); }
        private static byte[] ReadBytes(BinaryReader r) { int n = r.ReadInt32(); if (n < 0 || n > SharedChoiceDecision.MaxRewardPayloadBytes) throw new InvalidDataException("byte payload length"); var p = r.ReadBytes(n); if (p.Length != n) throw new EndOfStreamException(); return p; }
        private static void WriteCount(BinaryWriter w, int n) { if (n < 0 || n > MaxCount) throw new InvalidDataException("count out of range"); w.Write(n); }
        private static int ReadCount(BinaryReader r) { int n = r.ReadInt32(); if (n < 0 || n > MaxCount) throw new InvalidDataException("count out of range"); return n; }
        private static void WriteString(BinaryWriter w, string s) { s = InboxIdentity.Required(s, nameof(s)); var b = System.Text.Encoding.UTF8.GetBytes(s); if (b.Length > MaxString) throw new InvalidDataException("string too long"); w.Write((ushort)b.Length); w.Write(b); }
        private static string ReadString(BinaryReader r) { int n = r.ReadUInt16(); if (n == 0 || n > MaxString) throw new InvalidDataException("invalid string"); var b = r.ReadBytes(n); if (b.Length != n) throw new EndOfStreamException(); return System.Text.Encoding.UTF8.GetString(b); }
        private static void WriteOccurrence(BinaryWriter w, OccurrenceId o) { WriteString(w, o.EventId); WriteString(w, o.TriggerId); WriteCount(w, o.SubjectIds.Count); foreach (var s in o.SubjectIds) WriteString(w, s); }
        private static OccurrenceId ReadOccurrence(BinaryReader r) { string e = ReadString(r), t = ReadString(r); int n = ReadCount(r); var s = new string[n]; for (int i = 0; i < n; i++) s[i] = ReadString(r); return new OccurrenceId(e, t, s); }
        private static void WriteMembership(BinaryWriter w, MembershipId m) { WriteString(w, m.PlayerGuid); w.Write(m.Epoch); }
        private static MembershipId ReadMembership(BinaryReader r) => new MembershipId(ReadString(r), r.ReadUInt64());
        private static void WriteOptionalChoice(BinaryWriter w, CanonicalChoiceId c) { bool has = c.Value != null; w.Write(has); if (has) WriteString(w, c.Value); }
        private static CanonicalChoiceId ReadOptionalChoice(BinaryReader r, OccurrenceId o) => r.ReadBoolean() ? new CanonicalChoiceId(o, ReadString(r)) : default(CanonicalChoiceId);
        private static void WriteDecision(BinaryWriter w, SharedChoiceDecision d)
        {
            WriteOccurrence(w, d.Occurrence); WriteString(w, d.EffectToken.Value); WriteString(w, d.Choice.Value);
            WriteString(w, d.Result.Value); WriteMembership(w, d.Winner); w.Write((byte)d.Phase);
            WriteIdentities(w, d.EffectSteps, (bw, step) => { WriteString(bw, step.Key); WriteString(bw, step.Operation);
                WriteString(bw, step.BeforeFact); WriteString(bw, step.AfterFact); bw.Write((byte)step.State); });
            w.Write(d.SharedRevision);
            WriteIdentities(w, d.Rewards, (bw, x) => { WriteString(bw, x.SubjectId); WriteString(bw, x.Value); });
            WriteBytes(w, d.RewardPayload);
        }
        private static SharedChoiceDecision ReadDecision(BinaryReader r, int schema)
        {
            var o = ReadOccurrence(r); var token = new EffectToken(o, ReadString(r));
            var choice = new CanonicalChoiceId(o, ReadString(r)); var result = new CanonicalResultId(o, ReadString(r));
            var winner = ReadMembership(r); var phase = ReadEnum<SharedChoicePhase>(r);
            var steps = ReadIdentities(r, br => new DurableEffectStep(ReadString(br), ReadString(br),
                ReadString(br), ReadString(br), ReadEnum<DurableEffectStepState>(br)));
            ulong sharedRevision = r.ReadUInt64();
            var rewards = ReadIdentities(r, br => new CanonicalRewardItemId(o, ReadString(br), ReadString(br)));
            var payload = schema >= 6 ? ReadBytes(r) : Array.Empty<byte>();
            return new SharedChoiceDecision(o, token, choice, result, rewards, winner, phase, steps, sharedRevision, payload);
        }
        private static void WriteOptionalCheckpoint(BinaryWriter w, InboxWindowCheckpoint checkpoint)
        {
            w.Write(checkpoint != null);
            if (checkpoint == null) return;
            WriteString(w, checkpoint.ContentPhase); WriteNullableString(w, checkpoint.Selection);
            WriteNullableString(w, checkpoint.ReadCheckpoint);
            WriteNullableString(w, checkpoint.EventId); WriteNullableString(w, checkpoint.Title);
            WriteNullableString(w, checkpoint.Narrative); w.Write(checkpoint.NativePriority);
            WriteNullableString(w, checkpoint.NativeDataIdentity);
        }
        private static InboxWindowCheckpoint ReadOptionalCheckpoint(BinaryReader r, int schema)
        {
            if (!r.ReadBoolean()) return null;
            string phase = ReadString(r), selection = ReadNullableString(r), read = ReadNullableString(r);
            return schema >= 3 ? new InboxWindowCheckpoint(phase, selection, read, ReadNullableString(r),
                ReadNullableString(r), ReadNullableString(r), r.ReadInt32(), ReadNullableString(r)) :
                new InboxWindowCheckpoint(phase, selection, read);
        }
        private static void WriteNullableString(BinaryWriter w, string value)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value ?? "");
            if (bytes.Length > MaxString) throw new InvalidDataException("string too long");
            w.Write((ushort)bytes.Length); w.Write(bytes);
        }
        private static string ReadNullableString(BinaryReader r)
        {
            int count = r.ReadUInt16(); if (count > MaxString) throw new InvalidDataException("string too long");
            return System.Text.Encoding.UTF8.GetString(r.ReadBytes(count));
        }
        private static T ReadEnum<T>(BinaryReader r) where T : struct
        {
            object value = Convert.ChangeType(r.ReadByte(), Enum.GetUnderlyingType(typeof(T)));
            if (!Enum.IsDefined(typeof(T), value)) throw new InvalidDataException("invalid enum");
            return (T)Enum.ToObject(typeof(T), value);
        }
        private static void WriteIdentities<T>(BinaryWriter w, IEnumerable<T> source, Action<BinaryWriter, T> write) { var a = source.ToArray(); WriteCount(w, a.Length); foreach (var x in a) write(w, x); }
        private static List<T> ReadIdentities<T>(BinaryReader r, Func<BinaryReader, T> read) { int n = ReadCount(r); var a = new List<T>(n); for (int i = 0; i < n; i++) a.Add(read(r)); if (a.Distinct().Count() != a.Count) throw new InvalidDataException("duplicate identity"); return a; }
    }

    internal static class DurableInboxSaveBridge
    {
        private static readonly object Gate = new object();
        private static DurableInboxStore _activeStore;
        private static DurableInboxRestore _pendingRestore;
        internal static DurableInboxStore ActiveStore
        {
            get { lock (Gate) return _activeStore; }
            set
            {
                bool changed; DurableInboxStore old;
                lock (Gate) { old = _activeStore; changed = !ReferenceEquals(old, value); _activeStore = value; }
                if (changed) { WindowQueueSync.ClearDurableRuntimeCarriers(); old?.Carriers.AbandonStore(); }
            }
        }
        internal static DurableInboxRestore PendingRestore { get { lock (Gate) return _pendingRestore; } }
        private static DurableInboxSaveRoot _pendingRoot;

        internal static IEnumerable<object> Append(IEnumerable<object> native)
        {
            bool installed = false; IEnumerable<object> result;
            lock (Gate)
            {
            var values = (native ?? Enumerable.Empty<object>()).Where(x => !(x is DurableInboxSaveRoot)).ToList();
            // A save requested between native SetReadObjects and Task 15's started-Geoscape reconciliation
            // must preserve the validated root byte-for-byte, never replace an unread inbox with empty state.
            if (_activeStore == null && _pendingRoot != null)
            {
                DurableEffectSaveCatalog.CaptureNormalRoot(_pendingRoot, _pendingRestore?.Canonical.Decisions);
                values.Add(_pendingRoot);
                result = values;
            }
            else
            {
                if (_activeStore == null)
                { _activeStore = new DurableInboxStore(new HostLedger(Array.Empty<InboxEntry>())); installed = true; }
                values.Add(_activeStore.CreateSaveRoot()); result = values;
            }
            }
            if (installed) WindowQueueSync.ClearDurableRuntimeCarriers();
            return result;
        }

        internal static IEnumerable<object> Extract(IEnumerable<object> serialized)
        {
            // SetReadObjects runs before GeoLevelController.OnLevelStart/LevelCrt constructs stable subjects.
            // Never install here: Task 15 owns the started-Geoscape reconciliation edge.
            object[] result; DurableInboxStore old;
            lock (Gate)
            {
            old = _activeStore;
            _activeStore = null;
            _pendingRestore = null;
            _pendingRoot = null;
            var values = (serialized ?? Enumerable.Empty<object>()).ToList();
            var roots = values.OfType<DurableInboxSaveRoot>().ToArray();
            if (roots.Length > 1) throw new InvalidDataException("multiple durable inbox save roots");
            if (roots.Length == 1)
            {
                if (!DurableEffectSaveCatalog.AcceptLoadedRoot(roots[0]))
                    throw new InvalidDataException("loaded durable checkpoint changed after validation; fallback scheduled");
                DurableInboxRestore restored; string refusal;
                if (!DurableInboxSaveCodec.TryRestore(roots[0], null, out restored, out refusal))
                    throw new InvalidDataException(refusal);
                DurableEffectSaveCatalog.RestoreRetiredTokens(roots[0]);
                _pendingRestore = restored;
                _pendingRoot = roots[0];
                if (roots[0].TransactionWasIronman)
                {
                    var manager = GameUtl.GameComponent<PhoenixPoint.Common.Game.PhoenixGame>()?.SaveManager;
                    if (manager != null) AccessTools.Field(typeof(PhoenixPoint.Common.Saves.PhoenixSaveManager),
                        "<IsIronmanMode>k__BackingField")?.SetValue(manager, true);
                }
            }
            else _pendingRestore = new DurableInboxRestore(new HostLedger(Array.Empty<InboxEntry>()),
                new HostLedger(Array.Empty<InboxEntry>()), DurableInboxCanonicalState.Empty, DurableInboxCanonicalState.Empty,
                Array.Empty<DurableInboxJournalRecord>(), Array.Empty<OccurrenceId>());
            result = values.Where(x => !(x is DurableInboxSaveRoot)).ToArray();
            }
            WindowQueueSync.ClearDurableRuntimeCarriers(); old?.Carriers.AbandonStore();
            return result;
        }

        // Task 15 calls this only after the fully-started Geoscape graph can answer stable-ID lookups.
        internal static bool ReconcileAndInstall(IDurableInboxStableResolver resolver, out string refusal)
        {
            DurableInboxStore installed, old;
            lock (Gate)
            {
            refusal = null;
            if (resolver == null) { refusal = "stable resolver is required"; return false; }
            DurableInboxRestore resolved = _pendingRestore;
            if (_pendingRoot != null && !DurableInboxSaveCodec.TryRestore(_pendingRoot, resolver, out resolved, out refusal))
                return false;
            if (resolved == null) { refusal = "no pending durable inbox load"; return false; }
            installed = new DurableInboxStore(resolved.Ledger, resolved.Canonical, resolved.Journal,
                resolved.SnapshotLedger, resolved.SnapshotCanonical);
            installed.SetUnservable(resolved.Quarantine);
            old = _activeStore;
            _activeStore = installed;
            _pendingRestore = resolved;
            // Retain the validated durable material: an unavailable def/subject can become resolvable after
            // late mod/level initialization, and Task 15 may call this again to clear quarantine losslessly.
            }
            WindowQueueSync.ClearDurableRuntimeCarriers();
            old?.Carriers.AbandonStore();
            DurableEffectSaveCatalog.ClearPinnedBlobs();
            var pendingDecision = installed.Canonical.Decisions.FirstOrDefault(x => x.Phase != SharedChoicePhase.ChoiceLocked);
            if (pendingDecision != null)
            {
                if (!DurableEffectTransactionBarrier.TryEnter(pendingDecision.EffectToken, "<restored-durable-effect>"))
                    throw new InvalidOperationException("another durable transaction owns restored EffectPending");
                var timing = GameUtl.CurrentLevel()?.GetComponent<GeoLevelController>()?.Timing;
                if (timing != null)
                {
                    bool wasPaused = timing.Paused;
                    timing.Paused = true;
                    DurableEffectTransactionBarrier.RegisterPauseRestore(() => timing.Paused = wasPaused, wasPaused);
                }
                DurableEffectTransactionBarrier.CompletePendingReload(pendingDecision.EffectToken);
            }
            return true;
        }
    }

    internal static class DurableInboxNativeSerializer
    {
        // Diagnostic parity seam: this is the project's configured Phoenix serializer + Timing pump,
        // never a hand-built Serializer. Campaign persistence itself travels through Level.WriteSavegame.
        internal static DurableInboxSaveRoot RoundTripConfigured(DurableInboxSaveRoot root,
            Serializer serializer, out byte[] bytes, bool headless = false)
        {
            if (serializer == null) throw new ArgumentNullException(nameof(serializer));
            var output = new ByRefBytes();
            Pump(serializer.Write(new object[] { root }, ".b", output, new TimeSlice(3600f)), headless);
            bytes = output.Value;
            return DeserializeConfigured(bytes, serializer, headless);
        }

        internal static DurableInboxSaveRoot DeserializeConfigured(byte[] bytes, Serializer serializer, bool headless = false)
        {
            if (bytes == null || serializer == null) return null;
            var graph = new ByRefObjects();
            Pump(serializer.Read(graph, new TimeSlice(3600f), ".b", bytes), headless);
            return graph.Value?.OfType<DurableInboxSaveRoot>().SingleOrDefault();
        }

        private static void Pump(IEnumerator<NextUpdate> coroutine, bool headless)
        {
            if (!headless) { Timing.RunUntilComplete(coroutine); return; }
            var timing = new Timing();
            typeof(Timing).GetField("_paused", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic).SetValue(timing, true);
            var scheduler = new TimingScheduler(timing);
            timing.Scheduler = scheduler;
            Exception failure = null;
            var updateable = timing.Start(coroutine, NextUpdate.Now, setCaller: false,
                (u, ex) => { failure = ex; return UpdateableExceptionAction.CatchAndStop; });
            for (int i = 0; !updateable.Stopped && i < 4096; i++)
            {
                scheduler.Update(TimingScheduler.UpdatePhase.Regular);
                scheduler.Update(TimingScheduler.UpdatePhase.Fixed);
            }
            if (failure != null) throw new InvalidOperationException("paused Phoenix serializer pump failed", failure);
            if (!updateable.Stopped) throw new InvalidOperationException("paused Phoenix serializer pump exceeded 4096 updates");
        }
    }

    internal static class DurableInboxSaveBlobTransit
    {
        internal static BinaryDataLevelSerializedDataSource OpenExactReadSavegameBinaryBlob(byte[] blob, string extension)
        {
            return new BinaryDataLevelSerializedDataSource(blob ?? throw new ArgumentNullException(nameof(blob)),
                InboxIdentity.Required(extension, nameof(extension)));
        }
    }

    [HarmonyPatch(typeof(GeoLevelSavegame), nameof(GeoLevelSavegame.GetObjectsToWrite))]
    internal static class DurableInboxGetObjectsToWritePatch
    { private static void Postfix(ref IEnumerable<object> __result) => __result = DurableInboxSaveBridge.Append(__result); }

    [HarmonyPatch(typeof(GeoLevelSavegame), nameof(GeoLevelSavegame.SetReadObjects))]
    internal static class DurableInboxSetReadObjectsPatch
    {
        private static void Prefix(ref IEnumerable<object> deserialized) => deserialized = DurableInboxSaveBridge.Extract(deserialized);
    }
}
