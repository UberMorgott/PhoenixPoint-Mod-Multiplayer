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
            IEnumerable<CanonicalResultId> results, IEnumerable<CanonicalRewardItemId> rewards)
        {
            Choices = new ReadOnlyCollection<CanonicalChoiceId>((choices ?? throw new ArgumentNullException(nameof(choices))).Distinct().OrderBy(x => x).ToArray());
            Results = new ReadOnlyCollection<CanonicalResultId>((results ?? throw new ArgumentNullException(nameof(results))).Distinct().OrderBy(x => x).ToArray());
            Rewards = new ReadOnlyCollection<CanonicalRewardItemId>((rewards ?? throw new ArgumentNullException(nameof(rewards))).Distinct().OrderBy(x => x).ToArray());
        }
        internal IReadOnlyList<CanonicalChoiceId> Choices { get; }
        internal IReadOnlyList<CanonicalResultId> Results { get; }
        internal IReadOnlyList<CanonicalRewardItemId> Rewards { get; }
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
                Rewards.Where(x => !x.Occurrence.Equals(occurrence)).Concat(rewardArray));
        }
        internal DurableInboxCanonicalState Without(ISet<OccurrenceId> occurrences) => new DurableInboxCanonicalState(
            Choices.Where(x => !occurrences.Contains(x.Occurrence)), Results.Where(x => !occurrences.Contains(x.Occurrence)),
            Rewards.Where(x => !occurrences.Contains(x.Occurrence)));
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
        internal const int Schema = 1;
        private const int MaxCount = 65535;
        private const int MaxString = 4096;

        internal static DurableInboxSaveRoot Create(HostLedger ledger,
            IEnumerable<DurableInboxJournalRecord> journal, DurableInboxCanonicalState canonical)
        {
            if (ledger == null) throw new ArgumentNullException(nameof(ledger));
            var root = new DurableInboxSaveRoot
            {
                Snapshot = EncodeSnapshot(ledger, canonical?.Choices, canonical?.Results, canonical?.Rewards),
                Journal = (journal ?? Enumerable.Empty<DurableInboxJournalRecord>()).Select(EncodeJournal).ToArray(),
                SnapshotRevision = ledger.CommittedRevision
            };
            root.Crc = ComputeRootCrc(root);
            return root;
        }

        internal static bool TryRestore(DurableInboxSaveRoot root, IDurableInboxStableResolver resolver,
            out DurableInboxRestore restore, out string refusal)
        {
            restore = null; refusal = null;
            try
            {
                if (root == null || root.Magic != Magic || root.Schema != Schema) throw new InvalidDataException("unknown durable inbox save root");
                if (root.Snapshot == null || root.Journal == null || root.Crc != ComputeRootCrc(root)) throw new InvalidDataException("durable inbox CRC mismatch");
                HostLedger ledger; List<CanonicalChoiceId> choices; List<CanonicalResultId> results; List<CanonicalRewardItemId> rewards;
                DecodeSnapshot(root.Snapshot, out ledger, out choices, out results, out rewards);
                if (ledger.CommittedRevision != root.SnapshotRevision) throw new InvalidDataException("snapshot revision mismatch");
                var snapshotLedger = ledger;
                var snapshotCanonical = new DurableInboxCanonicalState(choices, results, rewards);
                var records = root.Journal.Select(DecodeJournal).OrderBy(r => r.Revision).ToList();
                ulong previous = root.SnapshotRevision;
                foreach (var record in records)
                {
                    HostLedger replayed; List<CanonicalChoiceId> replayChoices; List<CanonicalResultId> replayResults;
                    List<CanonicalRewardItemId> replayRewards; string replayRefusal = null;
                    if (previous == ulong.MaxValue || record.Revision != previous + 1 ||
                        record.Crc != Crc32.Compute(record.Payload) ||
                        !TryDecodeJournalCandidate(record.Payload, out replayed, out replayChoices, out replayResults,
                            out replayRewards, out replayRefusal) ||
                        replayed.CommittedRevision != record.Revision)
                        throw new InvalidDataException("invalid journal sequence: " + replayRefusal);
                    ledger = replayed;
                    choices = replayChoices; results = replayResults; rewards = replayRewards;
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
                var canonical = new DurableInboxCanonicalState(choices, results, rewards);
                restore = new DurableInboxRestore(ledger, snapshotLedger, canonical, snapshotCanonical, records, quarantine);
                return true;
            }
            catch (Exception ex) { refusal = ex.Message; return false; }
        }

        private static byte[] EncodeSnapshot(HostLedger ledger, IEnumerable<CanonicalChoiceId> choices,
            IEnumerable<CanonicalResultId> results, IEnumerable<CanonicalRewardItemId> rewards)
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
                }
                WriteIdentities(w, choices ?? Enumerable.Empty<CanonicalChoiceId>(), (bw, x) => { WriteOccurrence(bw, x.Occurrence); WriteString(bw, x.Value); });
                WriteIdentities(w, results ?? Enumerable.Empty<CanonicalResultId>(), (bw, x) => { WriteOccurrence(bw, x.Occurrence); WriteString(bw, x.Value); });
                WriteIdentities(w, rewards ?? Enumerable.Empty<CanonicalRewardItemId>(), (bw, x) => { WriteOccurrence(bw, x.Occurrence); WriteString(bw, x.SubjectId); WriteString(bw, x.Value); });
                return ms.ToArray();
            }
        }

        internal static byte[] EncodeJournalCandidate(HostLedger ledger, DurableInboxCanonicalState canonical) =>
            EncodeSnapshot(ledger, canonical.Choices, canonical.Results, canonical.Rewards);

        private static bool TryDecodeJournalCandidate(byte[] payload, out HostLedger ledger,
            out List<CanonicalChoiceId> choices, out List<CanonicalResultId> results,
            out List<CanonicalRewardItemId> rewards, out string refusal)
        {
            ledger = null; choices = null; results = null; rewards = null; refusal = null;
            try { DecodeSnapshot(payload, out ledger, out choices, out results, out rewards); return true; }
            catch (Exception ex) { refusal = ex.Message; return false; }
        }

        private static void DecodeSnapshot(byte[] bytes, out HostLedger ledger, out List<CanonicalChoiceId> choices,
            out List<CanonicalResultId> results, out List<CanonicalRewardItemId> rewards)
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
                    entries.Add(new InboxEntry(occurrence, member, lifecycle, choice, lifecycleRevision, tombstone, order));
                }
                choices = ReadIdentities(r, br => { var o = ReadOccurrence(br); return new CanonicalChoiceId(o, ReadString(br)); });
                results = ReadIdentities(r, br => { var o = ReadOccurrence(br); return new CanonicalResultId(o, ReadString(br)); });
                rewards = ReadIdentities(r, br => { var o = ReadOccurrence(br); return new CanonicalRewardItemId(o, ReadString(br), ReadString(br)); });
                if (ms.Position != ms.Length) throw new InvalidDataException("trailing snapshot bytes");
                ledger = new HostLedger(entries, revision, members);
            }
        }

        private static byte[] EncodeJournal(DurableInboxJournalRecord record)
        { using (var ms = new MemoryStream()) using (var w = new BinaryWriter(ms)) { w.Write(record.Revision); var p = record.Payload; w.Write(p.Length); w.Write(p); w.Write(record.Crc); return ms.ToArray(); } }
        private static DurableInboxJournalRecord DecodeJournal(byte[] bytes)
        {
            using (var ms = new MemoryStream(bytes ?? Array.Empty<byte>(), false)) using (var r = new BinaryReader(ms))
            {
                ulong rev = r.ReadUInt64(); int n = r.ReadInt32();
                if (n < 0 || n > ms.Length - ms.Position - 4) throw new InvalidDataException("journal length");
                var p = r.ReadBytes(n); uint crc = r.ReadUInt32();
                if (ms.Position != ms.Length || crc != Crc32.Compute(p)) throw new InvalidDataException("journal CRC");
                HostLedger decoded; List<CanonicalChoiceId> c; List<CanonicalResultId> result;
                List<CanonicalRewardItemId> reward; string refusal;
                if (!TryDecodeJournalCandidate(p, out decoded, out c, out result, out reward, out refusal) || decoded.CommittedRevision != rev)
                    throw new InvalidDataException("journal ledger: " + refusal);
                return new DurableInboxJournalRecord(rev, p, decoded.AllEntries.Select(x => x.Occurrence));
            }
        }
        private static uint ComputeRootCrc(DurableInboxSaveRoot root)
        { using (var ms = new MemoryStream()) using (var w = new BinaryWriter(ms)) { WriteString(w, root.Magic); w.Write(root.Schema); w.Write(root.SnapshotRevision); WriteBytes(w, root.Snapshot); WriteCount(w, root.Journal.Length); foreach (var p in root.Journal) WriteBytes(w, p); return Crc32.Compute(ms.ToArray()); } }
        private static void WriteBytes(BinaryWriter w, byte[] p) { p = p ?? Array.Empty<byte>(); w.Write(p.Length); w.Write(p); }
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
        internal static DurableInboxStore ActiveStore { get { lock (Gate) return _activeStore; } set { lock (Gate) _activeStore = value; } }
        internal static DurableInboxRestore PendingRestore { get { lock (Gate) return _pendingRestore; } }
        private static DurableInboxSaveRoot _pendingRoot;

        internal static IEnumerable<object> Append(IEnumerable<object> native)
        {
            lock (Gate)
            {
            var values = (native ?? Enumerable.Empty<object>()).Where(x => !(x is DurableInboxSaveRoot)).ToList();
            // A save requested between native SetReadObjects and Task 15's started-Geoscape reconciliation
            // must preserve the validated root byte-for-byte, never replace an unread inbox with empty state.
            if (_activeStore == null && _pendingRoot != null)
            {
                values.Add(_pendingRoot);
                return values;
            }
            if (_activeStore == null) _activeStore = new DurableInboxStore(new HostLedger(Array.Empty<InboxEntry>()));
            values.Add(_activeStore.CreateSaveRoot());
            return values;
            }
        }

        internal static IEnumerable<object> Extract(IEnumerable<object> serialized)
        {
            // SetReadObjects runs before GeoLevelController.OnLevelStart/LevelCrt constructs stable subjects.
            // Never install here: Task 15 owns the started-Geoscape reconciliation edge.
            lock (Gate)
            {
            _activeStore = null;
            _pendingRestore = null;
            _pendingRoot = null;
            var values = (serialized ?? Enumerable.Empty<object>()).ToList();
            var roots = values.OfType<DurableInboxSaveRoot>().ToArray();
            if (roots.Length > 1) throw new InvalidDataException("multiple durable inbox save roots");
            if (roots.Length == 1)
            {
                DurableInboxRestore restored; string refusal;
                if (!DurableInboxSaveCodec.TryRestore(roots[0], null, out restored, out refusal))
                    throw new InvalidDataException(refusal);
                _pendingRestore = restored;
                _pendingRoot = roots[0];
            }
            else _pendingRestore = new DurableInboxRestore(new HostLedger(Array.Empty<InboxEntry>()),
                new HostLedger(Array.Empty<InboxEntry>()), DurableInboxCanonicalState.Empty, DurableInboxCanonicalState.Empty,
                Array.Empty<DurableInboxJournalRecord>(), Array.Empty<OccurrenceId>());
            return values.Where(x => !(x is DurableInboxSaveRoot)).ToArray();
            }
        }

        // Task 15 calls this only after the fully-started Geoscape graph can answer stable-ID lookups.
        internal static bool ReconcileAndInstall(IDurableInboxStableResolver resolver, out string refusal)
        {
            lock (Gate)
            {
            refusal = null;
            if (resolver == null) { refusal = "stable resolver is required"; return false; }
            DurableInboxRestore resolved = _pendingRestore;
            if (_pendingRoot != null && !DurableInboxSaveCodec.TryRestore(_pendingRoot, resolver, out resolved, out refusal))
                return false;
            if (resolved == null) { refusal = "no pending durable inbox load"; return false; }
            _activeStore = new DurableInboxStore(resolved.Ledger, resolved.Canonical, resolved.Journal,
                resolved.SnapshotLedger, resolved.SnapshotCanonical);
            _activeStore.SetUnservable(resolved.Quarantine);
            _pendingRestore = resolved;
            // Retain the validated durable material: an unavailable def/subject can become resolvable after
            // late mod/level initialization, and Task 15 may call this again to clear quarantine losslessly.
            return true;
            }
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
