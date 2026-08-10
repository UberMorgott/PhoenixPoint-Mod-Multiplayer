using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Base.Core;
using Base.Serialization;
using Base.Utils;
using PhoenixPoint.Common.Game;
using PhoenixPoint.Common.Saves;
using PhoenixPoint.Geoscape.Levels;
using HarmonyLib;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Phoenix campaign implementation of the durable-effect checkpoint contract.  It uses the
    /// game's configured SaveGame/ReadSavegameBinary/LoadGame paths; in particular it never creates
    /// a second Serializer which would lack Phoenix's live serialization context.
    ///
    /// Checkpoints are ordinary, uniquely named ManualSave records.  Their serialized UserSetName
    /// contains the exact transaction identity, while verification reads the completed save bytes
    /// back and calculates CRC32.  PRE and POST are intentionally retained: PRE is the rollback
    /// boundary and POST is the authoritative crash-recovery result.
    /// </summary>
    internal sealed class DurableEffectPhoenixCheckpointBackend : IDurableEffectCheckpointBackend
    {
        public bool ReloadCompletesBeforeReturn => false;
        private const string SavePrefix = "mp_dfx_";
        private const string MarkerPrefix = "Multiplayer2-DurableEffect-v1|";

        private sealed class VerifiedRecord
        {
            internal PPSavegameMetaData Meta;
            internal DurableEffectCheckpoint Checkpoint;
            internal uint Crc32;
            internal int Length;
        }

        private readonly EffectToken _owner;
        private readonly Dictionary<string, VerifiedRecord> _verified =
            new Dictionary<string, VerifiedRecord>(StringComparer.Ordinal);
        private bool _frozen;
        private Base.Core.Timing _campaignTiming;
        private bool _wasPaused;
        private bool _barrierOwnsPauseRestore;

        internal DurableEffectPhoenixCheckpointBackend(EffectToken owner)
        {
            _owner = owner;
        }

        public void WaitForActiveSavesToDrain()
        {
            // This method runs on the campaign thread. Blocking it would prevent an already-running
            // save coroutine from advancing. The caller must retry on a later host tick instead.
            if (DurableEffectTransactionBarrier.ActiveNonOwnerSaves != 0)
                throw new InvalidOperationException("campaign checkpoint deferred until active saves drain");
        }

        public void Freeze()
        {
            if (_frozen) return;
            bool inheritedBarrier = DurableEffectTransactionBarrier.Active;
            if (!DurableEffectTransactionBarrier.TryEnter(_owner, "<durable-effect-no-owner-save>"))
                throw new InvalidOperationException("another durable effect transaction owns the campaign");
            _frozen = true; // from here every failure must make Unfreeze release the acquired barrier
            _campaignTiming = GameUtl.CurrentLevel()?.GetComponent<GeoLevelController>()?.Timing;
            if (_campaignTiming == null)
                throw new InvalidOperationException("geoscape timing is unavailable for durable effect freeze");
            _wasPaused = _campaignTiming.Paused;
            if (inheritedBarrier && DurableEffectTransactionBarrier.TryGetPriorPause(out var priorPause))
            { _wasPaused = priorPause; _barrierOwnsPauseRestore = true; }
            _campaignTiming.Paused = true;
        }

        public void WriteCheckpoint(DurableEffectCheckpoint checkpoint)
        {
            RequireExactOwner(checkpoint);
            PhoenixSaveManager manager = ResolveSaveManager();
            if (checkpoint.PredecessorSaveLocator == null)
            {
                if (checkpoint.Kind == DurableEffectCheckpointKind.Committed &&
                    _verified.TryGetValue(checkpoint.PendingCheckpointId, out var predecessor))
                    checkpoint.BindPredecessor(predecessor.Checkpoint.PredecessorSaveLocator);
                else checkpoint.BindPredecessor(ResolveLatestUserVisiblePredecessor(manager.LatestSave, manager.LatestLoad));
            }
            string name = BuildSaveName(checkpoint);
            ulong sharedRevision = DurableInboxSaveBridge.ActiveStore?.Canonical.Decisions
                .FirstOrDefault(x => x.EffectToken.Equals(checkpoint.Token))?.SharedRevision ?? 0;
            if (checkpoint.Kind == DurableEffectCheckpointKind.Pending && sharedRevision != 0)
                throw new InvalidOperationException("PRE checkpoint observed ChoiceLocked");
            if (checkpoint.Kind == DurableEffectCheckpointKind.Committed && sharedRevision == 0)
                throw new InvalidOperationException("POST checkpoint omitted ChoiceLocked shared revision");
            DurableEffectTransactionBarrier.SetOwnerCheckpoint(_owner, name, checkpoint, sharedRevision);
            DurableEffectTransactionBarrier.SetOwnerIronman(_owner, manager.IsIronmanMode);

            var meta = new PPSavegameMetaData(name, manager.Serializer.SavegameVersion,
                manager.CurrentGameId, BuildMarker(checkpoint), DateTime.Now, "", SaveType.ManualSave,
                manager.IsTactical, manager.CurrentDifficulty, manager.EnabledDlc);
            var written = new ByRef<bool>(false);
            // The host frame is synchronously isolated, so save the player's real pause state rather than
            // baking the transaction curtain into PRE/POST across a level reload.
            if (_campaignTiming != null) _campaignTiming.Paused = _wasPaused;
            try { Timing.RunUntilComplete(manager.SaveGame(meta, SerializationComponent.DefaultExtension,
                written, showCurtain: false)); }
            finally { if (_campaignTiming != null) _campaignTiming.Paused = true; }
            if (!written.Value)
                throw new InvalidOperationException("Phoenix did not complete durable effect checkpoint " + name);
            if (!DurableEffectTransactionBarrier.SaveCaptured(checkpoint))
                throw new InvalidOperationException("completed Phoenix save omitted its durable transaction root: " + name);

            byte[] bytes = ReadBytes(manager, meta);
            if (bytes == null || bytes.Length == 0)
                throw new InvalidOperationException("durable effect checkpoint read-back was empty: " + name);
            var record = new VerifiedRecord
            {
                Meta = meta,
                Checkpoint = checkpoint,
                Crc32 = Crc32(bytes),
                Length = bytes.Length
            };
            _verified[checkpoint.CheckpointId] = record;
            DurableEffectSaveCatalog.RegisterInternal(meta);
        }

        public bool VerifyCheckpoint(DurableEffectCheckpoint checkpoint)
        {
            if (!IsExactOwner(checkpoint)) return false;
            PhoenixSaveManager manager;
            try { manager = ResolveSaveManager(); }
            catch { return false; }

            VerifiedRecord record;
            if (!_verified.TryGetValue(checkpoint.CheckpointId, out record))
            {
                record = FindRecord(manager, checkpoint);
                if (record == null) return false;
            }
            if (!string.Equals(record.Meta.UserSetName, BuildMarker(checkpoint), StringComparison.Ordinal))
                return false;

            byte[] bytes;
            try { bytes = ReadBytes(manager, record.Meta); }
            catch { return false; }
            if (bytes == null || bytes.Length != record.Length || Crc32(bytes) != record.Crc32)
                return false;
            _verified[checkpoint.CheckpointId] = record;
            return true;
        }

        public bool TryFindCheckpoint(OccurrenceId occurrence, EffectToken token,
            DurableEffectCheckpointKind kind, out DurableEffectCheckpoint checkpoint)
        {
            checkpoint = null;
            if (!token.Equals(_owner) || !token.Occurrence.Equals(occurrence)) return false;
            PhoenixSaveManager manager = ResolveSaveManager();
            foreach (PPSavegameMetaData meta in GetSaves(manager).OfType<PPSavegameMetaData>()
                .OrderByDescending(m => m.SaveCreated))
            {
                DurableEffectCheckpoint candidate;
                if (!TryParseMarker(meta.UserSetName, occurrence, token, kind, out candidate)) continue;
                byte[] bytes;
                try { bytes = ReadBytes(manager, meta); }
                catch { continue; }
                if (bytes == null || bytes.Length == 0) continue;
                DurableInboxSaveRoot embedded;
                if (!DurableEffectSaveCatalog.TryReadEmbedded(manager, meta, candidate, out embedded)) continue;
                var record = new VerifiedRecord { Meta = meta, Checkpoint = candidate,
                    Length = bytes.Length, Crc32 = Crc32(bytes) };
                _verified[candidate.CheckpointId] = record;
                checkpoint = candidate;
                return true;
            }
            return false;
        }

        // Startup catalog: no in-memory token is required.  POST is deliberately considered before
        // PRE, then newest first.  The embedded schema-6 marker is validated again by SetReadObjects
        // before Phoenix installs the loaded graph.
        internal static bool TryFindLatestCheckpoint(out DurableEffectCheckpoint checkpoint)
        {
            checkpoint = null;
            PhoenixSaveManager manager = ResolveSaveManager();
            var candidates = new List<Tuple<DurableEffectCheckpoint, PPSavegameMetaData>>();
            foreach (PPSavegameMetaData meta in GetSaves(manager).OfType<PPSavegameMetaData>())
            {
                DurableEffectCheckpoint parsed;
                if (!TryParseAnyMarker(meta.UserSetName, out parsed)) continue;
                candidates.Add(Tuple.Create(parsed, meta));
            }
            var validated = new List<DurableEffectCatalogCandidate>();
            foreach (var candidate in candidates)
            {
                byte[] bytes;
                try { bytes = ReadBytes(manager, candidate.Item2); }
                catch { continue; }
                DurableInboxSaveRoot embedded;
                if (bytes != null && bytes.Length != 0 &&
                    DurableEffectSaveCatalog.TryReadEmbedded(manager, candidate.Item2, candidate.Item1, out embedded))
                    validated.Add(new DurableEffectCatalogCandidate(candidate.Item1, embedded,
                        candidate.Item2.SaveCreated.dateTime.Ticks));
            }
            var selected = DurableEffectSaveCatalog.SelectValidated(validated);
            checkpoint = selected?.Checkpoint;
            return checkpoint != null;
        }

        public void ForceReload(DurableEffectCheckpoint checkpoint)
        {
            RequireExactOwner(checkpoint);
            PhoenixSaveManager manager = ResolveSaveManager();
            VerifiedRecord record;
            if (!_verified.TryGetValue(checkpoint.CheckpointId, out record))
                record = FindRecord(manager, checkpoint);
            if (record == null || !VerifyCheckpoint(checkpoint))
                throw new InvalidOperationException("refusing to reload an unverified durable effect checkpoint");
            if (!DurableEffectSaveCatalog.PinExact(manager, record.Meta, checkpoint, record.Length, record.Crc32))
                throw new InvalidOperationException("checkpoint changed before exact pinned reload");
            // PrepareLoadGame normally writes the current ironman slot before any load.  During rollback
            // that would persist the possibly partial native effect and may delete its predecessor.
            // Checkpoints stay ManualSave so Phoenix cannot deduplicate the PRE/POST pair as ironman
            // slots.  The schema-6 root records TransactionWasIronman and SetReadObjects restores the
            // mode after successful graph validation; until then the transaction curtain remains raised.
            bool wasIronman = manager.IsIronmanMode;
            if (wasIronman)
                AccessTools.Field(typeof(PhoenixSaveManager), "<IsIronmanMode>k__BackingField")
                    .SetValue(manager, false);
            // LoadGame prepares metadata then schedules PhoenixGame.FinishLevelAndLoadGame. The barrier
            // remains raised until the coordinator explicitly releases it (POST only).
            try
            {
                System.Collections.Generic.IEnumerator<NextUpdate> load;
                using (DurableEffectSaveCatalog.ExactLoadScope()) load = manager.LoadGame(record.Meta);
                Timing.RunUntilComplete(load);
            }
            catch
            {
                DurableEffectSaveCatalog.ClearLoadState();
                throw;
            }
            finally
            {
                // SetReadObjects also restores from the validated root after level replacement. This
                // finally covers corruption/abort before that seam so manager semantics never leak false.
                if (wasIronman)
                    AccessTools.Field(typeof(PhoenixSaveManager), "<IsIronmanMode>k__BackingField")
                        .SetValue(manager, true);
            }
        }

        public void Unfreeze()
        {
            if (!_frozen) return;
            DurableEffectTransactionBarrier.Exit(_owner);
            if (_campaignTiming != null && !_barrierOwnsPauseRestore) _campaignTiming.Paused = _wasPaused;
            _campaignTiming = null;
            _frozen = false;
        }

        private VerifiedRecord FindRecord(PhoenixSaveManager manager, DurableEffectCheckpoint checkpoint)
        {
            string marker = BuildMarker(checkpoint);
            PPSavegameMetaData meta = GetSaves(manager).OfType<PPSavegameMetaData>()
                .FirstOrDefault(m => string.Equals(m.UserSetName, marker, StringComparison.Ordinal));
            if (meta == null) return null;
            byte[] bytes = ReadBytes(manager, meta);
            if (bytes == null || bytes.Length == 0) return null;
            DurableInboxSaveRoot embedded;
            if (!DurableEffectSaveCatalog.TryReadEmbedded(manager, meta, checkpoint, out embedded)) return null;
            var record = new VerifiedRecord { Meta = meta, Checkpoint = checkpoint,
                Length = bytes.Length, Crc32 = Crc32(bytes) };
            _verified[checkpoint.CheckpointId] = record;
            return record;
        }

        private static PhoenixSaveManager ResolveSaveManager()
        {
            PhoenixSaveManager manager = GameUtl.GameComponent<PhoenixGame>()?.SaveManager;
            if (manager?.Serializer == null)
                throw new InvalidOperationException("Phoenix SaveManager is unavailable");
            if (manager.IsTactical)
                throw new InvalidOperationException("durable campaign effects cannot checkpoint in tactical mode");
            return manager;
        }

        private static byte[] ReadBytes(PhoenixSaveManager manager, PPSavegameMetaData meta)
        {
            var bytes = new ByRef<byte[]>();
            Timing.RunUntilComplete(manager.Serializer.ReadSavegameBinary(meta, bytes));
            return bytes.Value;
        }

        private static List<SavegameMetaData> GetSaves(PhoenixSaveManager manager)
        {
            var saves = new ByRef<List<SavegameMetaData>>();
            using (DurableEffectSaveCatalog.RawScope())
                Timing.RunUntilComplete(manager.GetSavegames(saves));
            return saves.Value ?? new List<SavegameMetaData>();
        }

        private static string BuildSaveName(DurableEffectCheckpoint checkpoint)
        {
            string kind = checkpoint.Kind == DurableEffectCheckpointKind.Pending ? "pre_" : "post_";
            return SavePrefix + kind + checkpoint.CheckpointId;
        }

        private static string BuildMarker(DurableEffectCheckpoint checkpoint)
        {
            return MarkerPrefix + ((byte)checkpoint.Kind).ToString(CultureInfo.InvariantCulture) + "|" +
                Encode(checkpoint.CheckpointId) + "|" + Encode(checkpoint.PendingCheckpointId ?? "") + "|" +
                Encode(checkpoint.Occurrence.EventId) + "|" + Encode(checkpoint.Occurrence.TriggerId) + "|" +
                Encode(string.Join("\u001f", checkpoint.Occurrence.SubjectIds)) + "|" + Encode(checkpoint.Token.Value) + "|" +
                Encode(checkpoint.PredecessorSaveLocator ?? "");
        }

        private static bool TryParseMarker(string marker, OccurrenceId occurrence, EffectToken token,
            DurableEffectCheckpointKind expectedKind, out DurableEffectCheckpoint checkpoint)
        {
            checkpoint = null;
            DurableEffectCheckpoint parsed;
            if (!TryParseAnyMarker(marker, out parsed) || parsed.Kind != expectedKind ||
                !parsed.Occurrence.Equals(occurrence) || !parsed.Token.Equals(token)) return false;
            checkpoint = parsed;
            return true;
        }

        private static bool TryParseAnyMarker(string marker, out DurableEffectCheckpoint checkpoint)
        {
            checkpoint = null;
            if (marker == null || !marker.StartsWith(MarkerPrefix, StringComparison.Ordinal)) return false;
            string[] p = marker.Substring(MarkerPrefix.Length).Split('|');
            byte rawKind;
            if (p.Length != 8 || !byte.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out rawKind) ||
                rawKind > (byte)DurableEffectCheckpointKind.Committed) return false;
            try
            {
                string id = Decode(p[1]);
                string pending = Decode(p[2]);
                var parsedOccurrence = new OccurrenceId(Decode(p[3]), Decode(p[4]),
                    Decode(p[5]).Split(new[] { '\u001f' }, StringSplitOptions.RemoveEmptyEntries));
                var parsedToken = new EffectToken(parsedOccurrence, Decode(p[6]));
                var kind = (DurableEffectCheckpointKind)rawKind;
                checkpoint = new DurableEffectCheckpoint(id, kind, parsedOccurrence, parsedToken,
                    kind == DurableEffectCheckpointKind.Committed ? pending : null);
                checkpoint.BindPredecessor(Decode(p[7]));
                return true;
            }
            catch { return false; }
        }

        internal static bool IsInternal(PPSavegameMetaData meta) => meta != null &&
            meta.Name.StartsWith(SavePrefix, StringComparison.Ordinal) &&
            meta.UserSetName != null && meta.UserSetName.StartsWith(MarkerPrefix, StringComparison.Ordinal);

        internal static bool TryParseInternal(PPSavegameMetaData meta, out DurableEffectCheckpoint checkpoint)
        {
            checkpoint = null;
            return meta != null && TryParseAnyMarker(meta.UserSetName, out checkpoint);
        }

        internal static string SaveLocator(PPSavegameMetaData meta)
        {
            if (meta == null) return "<unsaved-campaign>";
            return Encode(meta.Name ?? "") + ":" + Encode(meta.Path ?? "") + ":" + Encode(meta.GameId.ToString());
        }

        internal static string ResolveUserVisiblePredecessor(PPSavegameMetaData meta)
        {
            DurableEffectCheckpoint internalCheckpoint;
            return TryParseInternal(meta, out internalCheckpoint) &&
                !string.IsNullOrEmpty(internalCheckpoint.PredecessorSaveLocator)
                ? internalCheckpoint.PredecessorSaveLocator : SaveLocator(meta);
        }
        internal static string ResolveLatestUserVisiblePredecessor(PPSavegameMetaData latestSave,
            PPSavegameMetaData latestLoad) => ResolveUserVisiblePredecessor(latestSave ?? latestLoad);

        private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        private static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));

        private bool IsExactOwner(DurableEffectCheckpoint checkpoint) => checkpoint != null &&
            checkpoint.Token.Equals(_owner) && checkpoint.Occurrence.Equals(_owner.Occurrence);
        private void RequireExactOwner(DurableEffectCheckpoint checkpoint)
        {
            if (!IsExactOwner(checkpoint))
                throw new InvalidOperationException("checkpoint belongs to another durable effect transaction");
        }

        internal static uint Crc32(byte[] bytes)
        {
            uint crc = 0xffffffffu;
            foreach (byte value in bytes)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
            }
            return ~crc;
        }
    }
}
