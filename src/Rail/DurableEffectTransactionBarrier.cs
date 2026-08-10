using System;
using System.Collections.Generic;
using System.Linq;
using Base.Utils;
using Base.Core;
using Base.Levels;
using HarmonyLib;
using PhoenixPoint.Common.Saves;
using PhoenixPoint.Geoscape.Entities;
using Base.Serialization;

namespace Multiplayer.Network.Sync
{
    /// <summary>Process-wide curtain for the short PRE/native/POST campaign transaction. It is not the
    /// durability mechanism; it prevents a second save/intent/delta from observing the in-memory middle.</summary>
    internal static class DurableEffectTransactionBarrier
    {
        private static readonly object Gate = new object();
        private static EffectToken _owner;
        private static string _ownerSaveName;
        private static bool _active;
        private static int _activeNonOwnerSaves;
        private static readonly List<Action> DeferredOutbound = new List<Action>();
        private static bool _flushingOutbound;
        private static EffectToken _recoveryLoad;
        private static bool _recoveryLoadInFlight;
        private static Action _restorePause;
        private static bool _priorPause;
        private static bool _priorPauseKnown;
        [ThreadStatic] private static string _currentSaveName;
        private static DurableEffectCheckpoint _ownerCheckpoint;
        private static string _capturedCheckpointId;
        private static ulong _ownerSharedRevision;
        private static bool _ownerIronman;

        internal static bool Active { get { lock (Gate) return _active; } }
        internal static int ActiveNonOwnerSaves { get { lock (Gate) return _activeNonOwnerSaves; } }
        internal static bool TryEnter(EffectToken owner, string ownerSaveName)
        {
            lock (Gate)
            {
                if (_active) return _owner.Equals(owner);
                // A verified POST batch outranks new campaign work. Mixing owners would let a later
                // pre-POST failure discard the older authoritative delivery retry.
                if (DeferredOutbound.Count != 0) return false;
                _owner = owner; _ownerSaveName = InboxIdentity.Required(ownerSaveName, nameof(ownerSaveName));
                _active = true; return true;
            }
        }
        internal static bool IsOwnerSave(string name)
        { lock (Gate) return _active && string.Equals(_ownerSaveName, name, StringComparison.Ordinal); }
        internal static void SetOwnerSave(EffectToken owner, string ownerSaveName)
        {
            lock (Gate)
            {
                if (!_active || !_owner.Equals(owner))
                    throw new InvalidOperationException("effect barrier owner mismatch");
                _ownerSaveName = InboxIdentity.Required(ownerSaveName, nameof(ownerSaveName));
            }
        }
        internal static void SetOwnerCheckpoint(EffectToken owner, string ownerSaveName, DurableEffectCheckpoint checkpoint,
            ulong sharedRevision)
        {
            SetOwnerSave(owner, ownerSaveName);
            lock (Gate) { _ownerCheckpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint));
                _ownerSharedRevision = sharedRevision; _capturedCheckpointId = null; }
        }
        internal static void SetOwnerIronman(EffectToken owner, bool ironman)
        {
            lock (Gate) { if (!_active || !_owner.Equals(owner)) throw new InvalidOperationException("effect barrier owner mismatch");
                _ownerIronman = ironman; }
        }
        internal static void StampOwnerRoot(DurableInboxSaveRoot root)
        {
            lock (Gate)
            {
                if (!_active || _ownerCheckpoint == null || !string.Equals(_ownerSaveName, _currentSaveName, StringComparison.Ordinal)) return;
                var c = _ownerCheckpoint;
                root.TransactionKind = (byte)c.Kind;
                root.TransactionCheckpointId = c.CheckpointId;
                root.TransactionPendingCheckpointId = c.PendingCheckpointId;
                root.TransactionEventId = c.Occurrence.EventId;
                root.TransactionTriggerId = c.Occurrence.TriggerId;
                root.TransactionSubjectIds = c.Occurrence.SubjectIds.ToArray();
                root.TransactionEffectToken = c.Token.Value;
                root.TransactionSharedRevision = _ownerSharedRevision;
                root.TransactionWasIronman = _ownerIronman;
                root.TransactionPredecessorSaveLocator = c.PredecessorSaveLocator;
                _capturedCheckpointId = c.CheckpointId;
            }
        }
        internal static bool SaveCaptured(DurableEffectCheckpoint checkpoint)
        { lock (Gate) return checkpoint != null && string.Equals(_capturedCheckpointId, checkpoint.CheckpointId, StringComparison.Ordinal); }
        internal static bool BlocksHostWork => Active;
        internal static void MarkRecoveryLoad(EffectToken token)
        { lock (Gate) { _recoveryLoad = token; _recoveryLoadInFlight = true; } }
        internal static bool RecoveryLoadInFlight(EffectToken token)
        { lock (Gate) return _recoveryLoadInFlight && _recoveryLoad.Equals(token); }
        internal static void CompletePendingReload(EffectToken token)
        { lock (Gate) if (_recoveryLoadInFlight && _recoveryLoad.Equals(token)) { _recoveryLoadInFlight = false; _recoveryLoad = default(EffectToken); } }
        internal static void RegisterPauseRestore(Action restore, bool priorPause)
        { lock (Gate) { _restorePause = restore; _priorPause = priorPause; _priorPauseKnown = true; } }
        internal static bool TryGetPriorPause(out bool paused)
        { lock (Gate) { paused = _priorPause; return _priorPauseKnown; } }
        internal static bool TryDeferOutbound(Action send)
        {
            lock (Gate)
            {
                if (!_active || _flushingOutbound) return false;
                DeferredOutbound.Add(send ?? throw new ArgumentNullException(nameof(send)));
                return true;
            }
        }
        internal static void FlushCommittedOutbound()
        {
            Action[] sends;
            lock (Gate) { _flushingOutbound = true; sends = DeferredOutbound.ToArray(); DeferredOutbound.Clear(); }
            try { foreach (var send in sends) send(); }
            catch
            {
                // Delivery is retryable after POST. Requeue the complete idempotent batch because the
                // transport API cannot tell which peers observed the throwing send.
                lock (Gate) DeferredOutbound.InsertRange(0, sends);
                throw;
            }
            finally { lock (Gate) _flushingOutbound = false; }
        }
        internal static bool RetryCommittedOutbound()
        {
            lock (Gate) if (_active || DeferredOutbound.Count == 0) return true;
            try { FlushCommittedOutbound(); return true; }
            catch { return false; }
        }
        internal static void DiscardUncommittedOutbound()
        { lock (Gate) DeferredOutbound.Clear(); }
        internal static bool AtOwnerSnapshot
        { get { lock (Gate) return _active && string.Equals(_ownerSaveName, _currentSaveName, StringComparison.Ordinal); } }
        internal static string CurrentSaveName => _currentSaveName;
        internal static void Exit(EffectToken owner)
        {
            Action restore;
            lock (Gate)
            { if (!_active || !_owner.Equals(owner)) throw new InvalidOperationException("effect barrier owner mismatch");
              _active = false; _ownerSaveName = null; _ownerCheckpoint = null; _capturedCheckpointId = null; _ownerSharedRevision = 0; _ownerIronman = false; _owner = default(EffectToken); restore = _restorePause; _restorePause = null; _priorPauseKnown = false;
              System.Threading.Monitor.PulseAll(Gate); }
            restore?.Invoke();
        }
        internal static bool TryExitInstalled(EffectToken owner)
        {
            Action restore;
            lock (Gate)
            {
                if (!_active || !_owner.Equals(owner)) return false;
                _active = false; _ownerSaveName = null; _ownerCheckpoint = null; _capturedCheckpointId = null; _ownerSharedRevision = 0; _ownerIronman = false; _owner = default(EffectToken);
                if (_recoveryLoadInFlight && _recoveryLoad.Equals(owner))
                { _recoveryLoadInFlight = false; _recoveryLoad = default(EffectToken); }
                DeferredOutbound.Clear(); restore = _restorePause; _restorePause = null; _priorPauseKnown = false;
                System.Threading.Monitor.PulseAll(Gate);
            }
            restore?.Invoke(); return true;
        }
        internal static IEnumerator<NextUpdate> WrapSave(IEnumerator<NextUpdate> inner, string saveName,
            PhoenixSaveManager manager, PPSavegameMetaData meta, ByRef<bool> success)
        {
            if (inner == null) yield break;
            bool started = false, countedForeign = false;
            try
            {
                while (true)
                {
                    lock (Gate)
                    {
                        bool owner = _active && string.Equals(_ownerSaveName, saveName, StringComparison.Ordinal);
                        if (_active && !owner && !started) { }
                        else if (!started)
                        {
                            started = true;
                            if (!owner) { _activeNonOwnerSaves++; countedForeign = true; }
                        }
                    }
                    if (!started) { yield return default(NextUpdate); continue; }
                    bool moved;
                    _currentSaveName = saveName;
                    try { moved = inner.MoveNext(); }
                    finally { _currentSaveName = null; }
                    if (!moved) break;
                    yield return inner.Current;
                }
                var cleanup = DurableEffectSaveCatalog.CleanupAfterNormalSave(manager, meta,
                    success != null && success.Value);
                try { while (cleanup.MoveNext()) yield return cleanup.Current; }
                finally { cleanup.Dispose(); }
            }
            finally
            {
                if (countedForeign) lock (Gate) { _activeNonOwnerSaves--; System.Threading.Monitor.PulseAll(Gate); }
                inner.Dispose();
            }
        }
    }

    /// <summary>SaveGame is an iterator factory.  Replacing its result is what brackets every MoveNext
    /// and Dispose; a conventional postfix would run before Phoenix writes a byte.</summary>
    [HarmonyPatch(typeof(PhoenixSaveManager), nameof(PhoenixSaveManager.SaveGame))]
    internal static class DurableEffectSaveLifetimePatch
    {
        private static void Postfix(PhoenixSaveManager __instance, PPSavegameMetaData metaData,
            ByRef<bool> success, ref IEnumerator<NextUpdate> __result)
            => __result = DurableEffectTransactionBarrier.WrapSave(__result, metaData?.Name ?? "<unnamed-save>",
                __instance, metaData, success);
    }

    [HarmonyPatch(typeof(GeoLevelSavegame), nameof(GeoLevelSavegame.GetObjectsToWrite))]
    internal static class DurableEffectSnapshotOwnershipPatch
    {
        private static void Prefix()
        {
            if (DurableEffectTransactionBarrier.Active && !DurableEffectTransactionBarrier.AtOwnerSnapshot)
                throw new InvalidOperationException("non-owner save reached campaign snapshot during durable effect transaction");
        }
    }
}
