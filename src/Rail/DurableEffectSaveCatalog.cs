using System;
using System.Collections.Generic;
using System.Linq;
using Base.Core;
using Base.Levels;
using Base.Serialization;
using Base.Utils;
using HarmonyLib;
using PhoenixPoint.Common.Saves;
using PhoenixPoint.Common.Game;
using System.Reflection;
using System.Text;
using System.IO;

namespace Multiplayer.Network.Sync
{
    internal sealed class DurableEffectCatalogCandidate
    {
        internal DurableEffectCatalogCandidate(DurableEffectCheckpoint checkpoint, DurableInboxSaveRoot root,
            long order, object carrier = null)
        { Checkpoint = checkpoint; Root = root; Order = order; Carrier = carrier; }
        internal DurableEffectCheckpoint Checkpoint { get; }
        internal DurableInboxSaveRoot Root { get; }
        internal long Order { get; }
        internal object Carrier { get; }
    }

    /// <summary>Raw internal checkpoint catalog and the user-facing save-list boundary.</summary>
    internal static class DurableEffectSaveCatalog
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, PPSavegameMetaData> Internal =
            new Dictionary<string, PPSavegameMetaData>(StringComparer.Ordinal);
        private static readonly Dictionary<string, PPSavegameMetaData> RawCatalog =
            new Dictionary<string, PPSavegameMetaData>(StringComparer.Ordinal);
        private static readonly Dictionary<string, EffectToken[]> NormalCaptures =
            new Dictionary<string, EffectToken[]>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Dictionary<string, EffectToken>> RetiredTokensByCampaign =
            new Dictionary<string, Dictionary<string, EffectToken>>(StringComparer.Ordinal);
        private static DurableEffectCheckpoint _expectedLoad;
        private static readonly Queue<PPSavegameMetaData> LoadFallbacks = new Queue<PPSavegameMetaData>();
        private static PPSavegameMetaData _scheduledFallback;
        private static readonly HashSet<string> AttemptedFallbacks = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, byte[]> PinnedBlobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Tuple<int, uint>> ValidatedFingerprints =
            new Dictionary<string, Tuple<int, uint>>(StringComparer.Ordinal);
        [ThreadStatic] private static int _rawDepth;
        [ThreadStatic] private static int _exactLoadDepth;
        [ThreadStatic] private static int _continueArmDepth;
        [ThreadStatic] private static string _continueName;
        [ThreadStatic] private static string _continuePath;
        [ThreadStatic] private static object _continueGameId;

        internal static void ArmContinue() { _continueArmDepth++; }
        internal static void DisarmContinue() { if (_continueArmDepth > 0) _continueArmDepth--; }
        internal static bool ContinueArmed => _continueArmDepth != 0;
        internal static IDisposable PermitContinueLoad(PPSavegameMetaData meta)
        {
            if (meta == null) throw new ArgumentNullException(nameof(meta));
            _continueName = meta.Name; _continuePath = meta.Path; _continueGameId = meta.GameId;
            return new ContinuePermit();
        }
        private sealed class ContinuePermit : IDisposable
        { public void Dispose() { _continueName = null; _continuePath = null; _continueGameId = null; } }
        internal static bool ConsumeContinueLoad(PPSavegameMetaData meta)
        {
            bool match = meta != null && _continueName != null &&
                string.Equals(_continueName, meta.Name, StringComparison.Ordinal) &&
                string.Equals(_continuePath ?? "", meta.Path ?? "", StringComparison.Ordinal) &&
                Equals(_continueGameId, meta.GameId);
            _continueName = null; _continuePath = null; _continueGameId = null;
            return match;
        }

        private sealed class Scope : IDisposable
        { public void Dispose() { if (_rawDepth > 0) _rawDepth--; } }
        internal static IDisposable RawScope() { _rawDepth++; return new Scope(); }
        internal static void RegisterInternal(PPSavegameMetaData meta)
        { if (meta != null) lock (Gate) Internal[meta.Name] = meta; }
        private sealed class ExactScope : IDisposable
        { public void Dispose() { if (_exactLoadDepth > 0) _exactLoadDepth--; } }
        internal static IDisposable ExactLoadScope()
        { _exactLoadDepth++; lock (Gate) { _expectedLoad = null; LoadFallbacks.Clear(); } return new ExactScope(); }
        internal static bool ExactLoadRequested => _exactLoadDepth != 0;

        internal static void CaptureNormalRoot(DurableInboxSaveRoot root, IEnumerable<SharedChoiceDecision> decisions)
        {
            string name = DurableEffectTransactionBarrier.CurrentSaveName;
            if (name == null || DurableEffectTransactionBarrier.Active) return;
            var locked = (decisions ?? Enumerable.Empty<SharedChoiceDecision>())
                .Where(x => x.Phase == SharedChoicePhase.ChoiceLocked && x.SharedRevision != 0)
                .Select(x => x.EffectToken).Distinct().ToArray();
            lock (Gate)
            {
                string campaign = CurrentCampaignKey();
                Dictionary<string, EffectToken> retired;
                if (!RetiredTokensByCampaign.TryGetValue(campaign, out retired))
                    RetiredTokensByCampaign[campaign] = retired = new Dictionary<string, EffectToken>(StringComparer.Ordinal);
                foreach (var token in locked) retired[EncodeToken(token)] = token;
                var liveTokens = Internal.Values.Where(x => CampaignKey(x) == campaign).Select(Parse)
                    .Where(x => x != null).Select(x => x.Token).ToArray();
                foreach (var key in retired.Where(x => !liveTokens.Contains(x.Value) && !locked.Contains(x.Value))
                    .Select(x => x.Key).ToArray()) retired.Remove(key);
                if (root != null) { root.RetiredCatalogGameId = campaign;
                    root.RetiredEffectTokens = retired.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray(); }
                NormalCaptures[name] = retired.Values.ToArray();
            }
        }

        internal static void RestoreRetiredTokens(DurableInboxSaveRoot root)
        {
            if (root == null) return;
            lock (Gate)
            {
                string campaign = root.RetiredCatalogGameId ?? "";
                var replacement = new Dictionary<string, EffectToken>(StringComparer.Ordinal);
                var live = Internal.Values.Where(x => CampaignKey(x) == campaign).Select(Parse)
                    .Where(x => x != null).Select(x => x.Token).ToArray();
                foreach (var encoded in root.RetiredEffectTokens ?? Array.Empty<string>())
                { EffectToken token; if (TryDecodeToken(encoded, out token) && live.Contains(token)) replacement[encoded] = token; }
                RetiredTokensByCampaign[campaign] = replacement;
            }
        }

        internal static IEnumerator<NextUpdate> FilterAfter(IEnumerator<NextUpdate> inner,
            ByRef<List<SavegameMetaData>> result)
        {
            try
            {
                while (inner != null && inner.MoveNext()) yield return inner.Current;
                var values = result?.Value ?? new List<SavegameMetaData>();
                lock (Gate)
                {
                    RawCatalog.Clear(); Internal.Clear();
                    foreach (var meta in values.OfType<PPSavegameMetaData>())
                    { RawCatalog[meta.Name] = meta; if (DurableEffectPhoenixCheckpointBackend.IsInternal(meta)) Internal[meta.Name] = meta; }
                }
                if (_rawDepth == 0 && result != null)
                    result.Value = values.Where(x => !(x is PPSavegameMetaData pp) ||
                        !DurableEffectPhoenixCheckpointBackend.IsInternal(pp)).ToList();
            }
            finally { inner?.Dispose(); }
        }

        internal static bool TrySelectRecovery(PhoenixSaveManager manager, PPSavegameMetaData requested,
            out PPSavegameMetaData selected)
        {
            selected = requested;
            if (manager?.Serializer == null || requested == null) return false;
            try
            {
                var all = new ByRef<List<SavegameMetaData>>();
                using (RawScope()) Timing.RunUntilComplete(manager.GetSavegames(all));
            }
            catch { if (DurableEffectPhoenixCheckpointBackend.IsInternal(requested)) selected = null; return false; }
            var superseded = !DurableEffectPhoenixCheckpointBackend.IsInternal(requested)
                ? NormalSaveSupersededTokens(manager, requested) : Array.Empty<EffectToken>();
            PPSavegameMetaData[] candidates;
            string requestedPredecessor = DurableEffectPhoenixCheckpointBackend.ResolveUserVisiblePredecessor(requested);
            lock (Gate) candidates = Internal.Values.Where(x => x.GameId == requested.GameId &&
                !superseded.Any(token => token.Equals(Parse(x)?.Token)) &&
                string.Equals(Parse(x)?.PredecessorSaveLocator,
                    requestedPredecessor, StringComparison.Ordinal) &&
                CompatibleCheckpoint(requested, x)).ToArray();
            var validated = new List<DurableEffectCatalogCandidate>();
            foreach (var meta in candidates.Select(x => new { Meta = x, Parsed = Parse(x) }).Where(x => x.Parsed != null))
            {
                DurableInboxSaveRoot embedded; byte[] candidateBlob;
                if (!TryReadRoot(manager, meta.Meta, out embedded, out candidateBlob) ||
                    !MatchesEmbedded(embedded, meta.Parsed)) continue;
                lock (Gate) ValidatedFingerprints[meta.Meta.Name] =
                    Tuple.Create(candidateBlob.Length, DurableEffectPhoenixCheckpointBackend.Crc32(candidateBlob));
                validated.Add(new DurableEffectCatalogCandidate(meta.Parsed, embedded,
                    meta.Meta.SaveCreated.dateTime.Ticks, meta.Meta));
            }
            var chosen = SelectValidated(validated);
            while (chosen != null)
            {
                var remaining = validated.Where(x => !ReferenceEquals(x, chosen)).ToList();
                selected = (PPSavegameMetaData)chosen.Carrier;
                Tuple<int, uint> fingerprint;
                lock (Gate) ValidatedFingerprints.TryGetValue(selected.Name, out fingerprint);
                if (fingerprint != null && PinExact(manager, selected, chosen.Checkpoint,
                    fingerprint.Item1, fingerprint.Item2))
                {
                    lock (Gate)
                    {
                        _expectedLoad = chosen.Checkpoint; LoadFallbacks.Clear();
                        DurableEffectCatalogCandidate next;
                        while ((next = SelectValidated(remaining)) != null)
                        { LoadFallbacks.Enqueue((PPSavegameMetaData)next.Carrier); remaining.Remove(next); }
                    }
                    return true;
                }
                validated.Remove(chosen); chosen = SelectValidated(validated);
            }
            if (DurableEffectPhoenixCheckpointBackend.IsInternal(requested))
            {
                PPSavegameMetaData predecessor;
                lock (Gate) predecessor = RawCatalog.Values.FirstOrDefault(x =>
                    !DurableEffectPhoenixCheckpointBackend.IsInternal(x) && x.GameId == requested.GameId &&
                    string.Equals(DurableEffectPhoenixCheckpointBackend.SaveLocator(x), requestedPredecessor,
                        StringComparison.Ordinal) && CompatibleCheckpoint(x, requested));
                if (predecessor != null) { selected = predecessor; ClearLoadState(); return true; }
            }
            ClearLoadState();
            return false;
        }

        internal static bool AcceptLoadedRoot(DurableInboxSaveRoot root)
        {
            PPSavegameMetaData fallback = null;
            lock (Gate)
            {
                if (_expectedLoad == null) return true;
                if (MatchesEmbedded(root, _expectedLoad))
                { _expectedLoad = null; LoadFallbacks.Clear(); AttemptedFallbacks.Clear(); PinnedBlobs.Clear();
                    ValidatedFingerprints.Clear(); return true; }
                if (LoadFallbacks.Count != 0) fallback = LoadFallbacks.Dequeue();
                _expectedLoad = fallback == null ? null : Parse(fallback);
                PinnedBlobs.Clear();
                if (fallback != null && AttemptedFallbacks.Add(fallback.Name)) _scheduledFallback = fallback;
            }
            return false;
        }

        internal static void PumpScheduledFallback()
        {
            var manager = GameUtl.GameComponent<PhoenixPoint.Common.Game.PhoenixGame>()?.SaveManager;
            while (true)
            {
                PPSavegameMetaData fallback;
                lock (Gate) { fallback = _scheduledFallback; _scheduledFallback = null; }
                if (fallback == null) return;
                Tuple<int, uint> fingerprint; DurableEffectCheckpoint expected = Parse(fallback);
                lock (Gate) ValidatedFingerprints.TryGetValue(fallback.Name, out fingerprint);
                if (manager != null && expected != null && fingerprint != null &&
                    PinExact(manager, fallback, expected, fingerprint.Item1, fingerprint.Item2))
                { Timing.Current.Start(manager.LoadGame(fallback)); return; }
                lock (Gate)
                {
                    _scheduledFallback = LoadFallbacks.Count == 0 ? null : LoadFallbacks.Dequeue();
                    _expectedLoad = _scheduledFallback == null ? null : Parse(_scheduledFallback);
                    if (_scheduledFallback == null) { AttemptedFallbacks.Clear(); PinnedBlobs.Clear();
                        ValidatedFingerprints.Clear(); }
                }
                // Continue inside this post-iterator lifecycle invocation. No LoadGame recursion occurs:
                // only the first freshly verified candidate is started, after the prior iterator disposed.
            }
        }
        internal static bool HasScheduledFallback { get { lock (Gate) return _scheduledFallback != null; } }
        internal static IEnumerator<NextUpdate> CompleteLoadLifecycle(IEnumerator<NextUpdate> inner)
        {
            bool completed = false;
            try
            {
                while (inner != null)
                {
                    bool moved;
                    try { moved = inner.MoveNext(); }
                    catch { if (HasScheduledFallback) break; throw; }
                    if (!moved) { completed = true; break; }
                    yield return inner.Current;
                }
            }
            finally
            {
                inner?.Dispose();
                if (HasScheduledFallback) PumpScheduledFallback();
                else if (!completed) ClearLoadState();
            }
        }

        internal static void ClearLoadState()
        {
            lock (Gate) { _expectedLoad = null; _scheduledFallback = null; LoadFallbacks.Clear();
                AttemptedFallbacks.Clear(); PinnedBlobs.Clear(); ValidatedFingerprints.Clear(); }
        }

        private static DurableEffectCheckpoint Parse(PPSavegameMetaData meta)
        { DurableEffectCheckpoint parsed; return DurableEffectPhoenixCheckpointBackend.TryParseInternal(meta, out parsed) ? parsed : null; }

        internal static bool TryReadEmbedded(PhoenixSaveManager manager, PPSavegameMetaData meta,
            DurableEffectCheckpoint expected, out DurableInboxSaveRoot root)
        {
            return TryReadRoot(manager, meta, out root) && MatchesEmbedded(root, expected);
        }

        private static bool TryReadRoot(PhoenixSaveManager manager, PPSavegameMetaData meta,
            out DurableInboxSaveRoot root)
        { byte[] ignored; return TryReadRoot(manager, meta, out root, out ignored); }

        private static bool TryReadRoot(PhoenixSaveManager manager, PPSavegameMetaData meta,
            out DurableInboxSaveRoot root, out byte[] blob)
        {
            root = null; blob = null;
            try
            {
                var bytes = new ByRef<byte[]>();
                Timing.RunUntilComplete(manager.Serializer.ReadSavegameBinary(meta, bytes));
                if (bytes.Value == null || bytes.Value.Length == 0) return false;
                blob = bytes.Value;
                var data = new ByRef<LevelSerializedData>();
                var source = DurableInboxSaveBlobTransit.OpenExactReadSavegameBinaryBlob(bytes.Value,
                    SerializationComponent.DefaultExtension);
                Timing.RunUntilComplete(source.ReadSerializedDataAsync(manager.Serializer, data));
                root = data.Value?.Objects?.OfType<DurableInboxSaveRoot>().SingleOrDefault();
                DurableInboxRestore restore; string refusal;
                return root != null && DurableInboxSaveCodec.TryRestore(root, null, out restore, out refusal);
            }
            catch { root = null; blob = null; return false; }
        }

        internal static bool TryPinnedBlob(string saveName, out byte[] blob)
        {
            lock (Gate)
            {
                byte[] stored;
                if (!PinnedBlobs.TryGetValue(saveName ?? "", out stored)) { blob = null; return false; }
                blob = (byte[])stored.Clone(); return true;
            }
        }
        internal static bool PinExact(PhoenixSaveManager manager, PPSavegameMetaData meta,
            DurableEffectCheckpoint expected, int expectedLength, uint expectedCrc32)
        {
            DurableInboxSaveRoot root; byte[] blob;
            if (!TryReadRoot(manager, meta, out root, out blob) || !MatchesEmbedded(root, expected) ||
                !BlobMatches(expectedLength, expectedCrc32, blob))
                return false;
            lock (Gate) PinnedBlobs[meta.Name] = blob;
            return true;
        }
        internal static bool BlobMatches(int expectedLength, uint expectedCrc32, byte[] blob) =>
            blob != null && blob.Length == expectedLength &&
            DurableEffectPhoenixCheckpointBackend.Crc32(blob) == expectedCrc32;

        internal static bool CompatibleCheckpoint(PPSavegameMetaData requested, PPSavegameMetaData checkpoint)
        {
            if (requested == null || checkpoint == null || requested.GameId != checkpoint.GameId ||
                requested.IsTacticalSave != checkpoint.IsTacticalSave) return false;
            string requestedDifficulty = requested.DifficultyDef?.Guid ?? "";
            string checkpointDifficulty = checkpoint.DifficultyDef?.Guid ?? "";
            if (!string.Equals(requestedDifficulty, checkpointDifficulty, StringComparison.Ordinal)) return false;
            var requestedDlc = (requested.EnabledDlc ?? Array.Empty<Base.Platforms.EntitlementDef>())
                .Select(x => x?.Guid ?? "").OrderBy(x => x, StringComparer.Ordinal);
            var checkpointDlc = (checkpoint.EnabledDlc ?? Array.Empty<Base.Platforms.EntitlementDef>())
                .Select(x => x?.Guid ?? "").OrderBy(x => x, StringComparer.Ordinal);
            return requestedDlc.SequenceEqual(checkpointDlc);
        }
        internal static void ClearPinnedBlobs() { lock (Gate) PinnedBlobs.Clear(); }

        private static EffectToken[] NormalSaveSupersededTokens(PhoenixSaveManager manager, PPSavegameMetaData normal)
        {
            DurableInboxSaveRoot root;
            if (!TryReadRoot(manager, normal, out root)) return Array.Empty<EffectToken>();
            DurableInboxRestore restore; string refusal;
            if (!DurableInboxSaveCodec.TryRestore(root, null, out restore, out refusal)) return Array.Empty<EffectToken>();
            var locked = restore.Canonical.Decisions.Where(x => x.Phase == SharedChoicePhase.ChoiceLocked &&
                x.SharedRevision != 0).Select(x => x.EffectToken).ToArray();
            var retired = new List<EffectToken>(locked);
            if (string.Equals(root.RetiredCatalogGameId ?? "", CampaignKey(normal), StringComparison.Ordinal))
                foreach (var encoded in root.RetiredEffectTokens ?? Array.Empty<string>())
                { EffectToken token; if (TryDecodeToken(encoded, out token) && !retired.Contains(token)) retired.Add(token); }
            locked = retired.ToArray();
            PPSavegameMetaData[] obsolete;
            string normalCampaign = CampaignKey(normal);
            lock (Gate) obsolete = Internal.Values.Where(meta =>
            { var parsed = Parse(meta); return CampaignKey(meta) == normalCampaign && parsed != null && locked.Contains(parsed.Token); }).ToArray();
            foreach (var meta in obsolete)
            {
                try
                {
                    Timing.RunUntilComplete(manager.DeleteSaveGame(meta));
                    lock (Gate) Internal.Remove(meta.Name);
                }
                catch { /* normal root remains the authority; retry deletion on the next selection */ }
            }
            return locked;
        }

        internal static bool MatchesEmbedded(DurableInboxSaveRoot root, DurableEffectCheckpoint expected) =>
            root != null && expected != null && root.TransactionKind == (byte)expected.Kind &&
                    string.Equals(root.TransactionCheckpointId, expected.CheckpointId, StringComparison.Ordinal) &&
                    string.Equals(root.TransactionPendingCheckpointId, expected.PendingCheckpointId, StringComparison.Ordinal) &&
                    string.Equals(root.TransactionEventId, expected.Occurrence.EventId, StringComparison.Ordinal) &&
                    string.Equals(root.TransactionTriggerId, expected.Occurrence.TriggerId, StringComparison.Ordinal) &&
                    (root.TransactionSubjectIds ?? Array.Empty<string>()).SequenceEqual(expected.Occurrence.SubjectIds) &&
                    string.Equals(root.TransactionEffectToken, expected.Token.Value, StringComparison.Ordinal) &&
                    (root.Schema < 7 || string.Equals(root.TransactionPredecessorSaveLocator,
                        expected.PredecessorSaveLocator, StringComparison.Ordinal)) &&
                    (expected.Kind != DurableEffectCheckpointKind.Committed || root.TransactionSharedRevision != 0);

        internal static DurableEffectCatalogCandidate SelectValidated(IEnumerable<DurableEffectCatalogCandidate> source)
        {
            var valid = (source ?? Enumerable.Empty<DurableEffectCatalogCandidate>())
                .Where(x => x != null && MatchesEmbedded(x.Root, x.Checkpoint)).ToArray();
            foreach (var transaction in valid.GroupBy(x => x.Checkpoint.Token)
                .OrderByDescending(g => g.Max(x => x.Order)))
            {
                var pre = transaction.Where(x => x.Checkpoint.Kind == DurableEffectCheckpointKind.Pending)
                    .OrderByDescending(x => x.Order).FirstOrDefault();
                var post = transaction.Where(x => x.Checkpoint.Kind == DurableEffectCheckpointKind.Committed &&
                    pre != null && pre.Checkpoint.CheckpointId == x.Checkpoint.PendingCheckpointId &&
                    pre.Checkpoint.Occurrence.Equals(x.Checkpoint.Occurrence))
                    .OrderByDescending(x => x.Order).FirstOrDefault();
                if (post != null) return post;
                if (pre != null) return pre;
            }
            return null;
        }

        internal static string[] PlanCleanup(IEnumerable<DurableEffectCheckpoint> checkpoints,
            IEnumerable<EffectToken> capturedLocked) => (checkpoints ?? Enumerable.Empty<DurableEffectCheckpoint>())
            .Where(x => x != null && (capturedLocked ?? Enumerable.Empty<EffectToken>()).Contains(x.Token))
            .Select(x => x.CheckpointId).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        internal static IEnumerator<NextUpdate> CleanupAfterNormalSave(PhoenixSaveManager manager,
            PPSavegameMetaData completed, bool success)
        {
            if (manager == null || completed == null ||
                DurableEffectPhoenixCheckpointBackend.IsInternal(completed)) yield break;
            EffectToken[] decisions;
            bool captured;
            lock (Gate)
            {
                captured = NormalCaptures.TryGetValue(completed.Name, out decisions);
                if (captured) NormalCaptures.Remove(completed.Name);
            }
            if (!success || !captured) yield break;
            PPSavegameMetaData[] obsolete;
            string completedCampaign = CampaignKey(completed);
            lock (Gate) obsolete = Internal.Values.Where(meta =>
            { var parsed = Parse(meta); return CampaignKey(meta) == completedCampaign && parsed != null && decisions.Contains(parsed.Token); }).ToArray();
            foreach (var meta in obsolete)
            {
                bool deleted = false;
                var deletion = manager.DeleteSaveGame(meta);
                try
                {
                    while (true)
                    {
                        bool moved;
                        try { moved = deletion.MoveNext(); }
                        catch { break; } // cleanup is retryable; a verified normal save remains successful
                        if (!moved) { deleted = true; break; }
                        yield return deletion.Current;
                    }
                }
                finally { deletion.Dispose(); }
                if (deleted) lock (Gate) Internal.Remove(meta.Name);
            }
            lock (Gate)
            {
                string campaign = CampaignKey(completed);
                var live = Internal.Values.Where(x => CampaignKey(x) == campaign).Select(Parse)
                    .Where(x => x != null).Select(x => x.Token).ToArray();
                Dictionary<string, EffectToken> retired;
                if (RetiredTokensByCampaign.TryGetValue(campaign, out retired))
                    foreach (var key in retired.Where(x => !live.Contains(x.Value)).Select(x => x.Key).ToArray()) retired.Remove(key);
            }
        }

        private static string CurrentCampaignKey()
        {
            var manager = GameUtl.GameComponent<PhoenixGame>()?.SaveManager;
            return manager == null ? "" : manager.CurrentGameId.ToString();
        }
        private static string CampaignKey(PPSavegameMetaData meta) => meta == null ? "" : meta.GameId.ToString();

        private static string EncodeToken(EffectToken token)
        {
            var parts = new[] { token.Occurrence.EventId, token.Occurrence.TriggerId,
                string.Join("\u001f", token.Occurrence.SubjectIds), token.Value };
            return string.Join(".", parts.Select(x => Convert.ToBase64String(Encoding.UTF8.GetBytes(x))));
        }

        internal static bool TryDecodeToken(string encoded, out EffectToken token)
        {
            token = default(EffectToken);
            try
            {
                var p = encoded.Split('.'); if (p.Length != 4) return false;
                Func<string, string> decode = x => Encoding.UTF8.GetString(Convert.FromBase64String(x));
                var occurrence = new OccurrenceId(decode(p[0]), decode(p[1]),
                    decode(p[2]).Split(new[] { '\u001f' }, StringSplitOptions.RemoveEmptyEntries));
                token = new EffectToken(occurrence, decode(p[3])); return true;
            }
            catch { return false; }
        }
    }

    [HarmonyPatch(typeof(PhoenixSaveManager), nameof(PhoenixSaveManager.GetSavegames))]
    internal static class DurableEffectSaveListFilterPatch
    {
        private static void Postfix(ByRef<List<SavegameMetaData>> saveGames,
            ref IEnumerator<NextUpdate> __result) =>
            __result = DurableEffectSaveCatalog.FilterAfter(__result, saveGames);
    }

    [HarmonyPatch(typeof(PhoenixSaveManager), nameof(PhoenixSaveManager.LoadGame))]
    internal static class DurableEffectLoadSelectionPatch
    {
        private static bool Prefix(PhoenixSaveManager __instance, ref PPSavegameMetaData metaData,
            ref IEnumerator<NextUpdate> __result)
        {
            if (DurableEffectSaveCatalog.ExactLoadRequested) return true;
            if (!DurableEffectSaveCatalog.ConsumeContinueLoad(metaData)) return true;
            PPSavegameMetaData selected;
            bool requestedInternal = DurableEffectPhoenixCheckpointBackend.IsInternal(metaData);
            if (DurableEffectSaveCatalog.TrySelectRecovery(__instance, metaData, out selected))
            { metaData = selected; return true; }
            if (requestedInternal)
                throw new InvalidDataException("refusing corrupt or orphan durable checkpoint; no validated predecessor exists");
            return true;
        }

        private static void Postfix(ref IEnumerator<NextUpdate> __result) =>
            __result = DurableEffectSaveCatalog.CompleteLoadLifecycle(__result);
    }

    [HarmonyPatch]
    internal static class DurableEffectContinueArmPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(
            AccessTools.TypeByName("PhoenixPoint.Home.View.ViewModules.UIModuleMainMenuButtons"),
            "OnContinueGameButtonClicked");
        private static void Prefix() => DurableEffectSaveCatalog.ArmContinue();
        private static Exception Finalizer(Exception __exception)
        { DurableEffectSaveCatalog.DisarmContinue(); return __exception; }
    }

    [HarmonyPatch]
    internal static class DurableEffectContinueDelegatePatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(
            AccessTools.TypeByName("PhoenixPoint.Home.View.ViewModules.UIModuleMainMenuButtons"), "TryLoadSave");
        private static void Prefix(ref Action<PPSavegameMetaData> loadConfirmation)
        {
            if (!DurableEffectSaveCatalog.ContinueArmed) return;
            loadConfirmation = saveData =>
            {
                var game = GameUtl.GameComponent<PhoenixGame>();
                game.SaveManager.StartCheckAssetsAndPromptForSave(saveData, true, () =>
                {
                    using (DurableEffectSaveCatalog.PermitContinueLoad(saveData))
                        GameUtl.GameComponent<TimeSource>().Timing.Start(game.SaveManager.LoadGame(saveData));
                });
            };
        }
    }

    /// <summary>The selected checkpoint is loaded from the exact blob already root/CRC validated, so a
    /// file replacement between catalog selection and level construction cannot pair that root with a
    /// different campaign graph.</summary>
    [HarmonyPatch(typeof(SavegameMetaData), nameof(SavegameMetaData.GetLevelSerializedDataSource))]
    internal static class DurableEffectPinnedLevelDataPatch
    {
        private static void Postfix(SavegameMetaData __instance, ref ILevelSerializedDataSource __result)
        {
            byte[] blob;
            if (DurableEffectSaveCatalog.TryPinnedBlob(__instance?.Name, out blob))
                __result = new BinaryDataLevelSerializedDataSource(blob, SerializationComponent.DefaultExtension);
        }
    }

    [HarmonyPatch(typeof(SavegameMetaData), nameof(SavegameMetaData.GetLevelParamsDataSource))]
    internal static class DurableEffectPinnedLevelParamsPatch
    {
        private static void Postfix(SavegameMetaData __instance, ref ILevelParamsSource __result)
        {
            byte[] blob;
            if (DurableEffectSaveCatalog.TryPinnedBlob(__instance?.Name, out blob))
                __result = new BinaryDataLevelParamsSource(blob, SerializationComponent.DefaultExtension);
        }
    }
}
