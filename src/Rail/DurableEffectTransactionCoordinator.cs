using System;

namespace Multiplayer.Network.Sync
{
    internal sealed class DurableEffectPreCheckpointException : Exception
    { internal DurableEffectPreCheckpointException(Exception inner) : base("durable PRE checkpoint was not verified", inner) { } }
    internal enum DurableEffectCheckpointKind : byte
    {
        Pending,
        Committed
    }

    /// <summary>
    /// Identity passed to the campaign-checkpoint backend.  The coordinator creates the identity
    /// before asking the backend to write so that even a failed write has an unambiguous reload
    /// target and can never be confused with another attempt's checkpoint.
    /// </summary>
    internal sealed class DurableEffectCheckpoint : IEquatable<DurableEffectCheckpoint>
    {
        internal DurableEffectCheckpoint(string checkpointId, DurableEffectCheckpointKind kind,
            OccurrenceId occurrence, EffectToken token, string pendingCheckpointId = null)
        {
            CheckpointId = InboxIdentity.Required(checkpointId, nameof(checkpointId));
            if (!Enum.IsDefined(typeof(DurableEffectCheckpointKind), kind))
                throw new ArgumentOutOfRangeException(nameof(kind));
            if (!token.Occurrence.Equals(occurrence))
                throw new ArgumentException("effect token belongs to another occurrence", nameof(token));
            if (kind == DurableEffectCheckpointKind.Pending && pendingCheckpointId != null)
                throw new ArgumentException("a pending checkpoint cannot name a predecessor", nameof(pendingCheckpointId));
            if (kind == DurableEffectCheckpointKind.Committed)
                PendingCheckpointId = InboxIdentity.Required(pendingCheckpointId, nameof(pendingCheckpointId));

            Kind = kind;
            Occurrence = occurrence;
            Token = token;
        }

        internal string CheckpointId { get; }
        internal DurableEffectCheckpointKind Kind { get; }
        internal OccurrenceId Occurrence { get; }
        internal EffectToken Token { get; }
        internal string PendingCheckpointId { get; }
        internal string PredecessorSaveLocator { get; private set; }
        internal void BindPredecessor(string locator)
        {
            locator = InboxIdentity.Required(locator, nameof(locator));
            if (PredecessorSaveLocator != null && !string.Equals(PredecessorSaveLocator, locator, StringComparison.Ordinal))
                throw new InvalidOperationException("checkpoint predecessor is immutable");
            PredecessorSaveLocator = locator;
        }

        public bool Equals(DurableEffectCheckpoint other) => other != null &&
            string.Equals(CheckpointId, other.CheckpointId, StringComparison.Ordinal) && Kind == other.Kind &&
            Occurrence.Equals(other.Occurrence) && Token.Equals(other.Token) &&
            string.Equals(PendingCheckpointId, other.PendingCheckpointId, StringComparison.Ordinal);
        public override bool Equals(object obj) => Equals(obj as DurableEffectCheckpoint);
        public override int GetHashCode() => InboxIdentity.Hash(CheckpointId, Kind, Occurrence.GetHashCode(),
            Token.GetHashCode(), PendingCheckpointId);
    }

    /// <summary>
    /// Synchronous campaign primitives.  Implementations must include the durable inbox journal in
    /// both checkpoints and must not return from WriteCheckpoint until Phoenix's save coroutine reports
    /// completion. VerifyCheckpoint is a read-back verification, not an in-memory receipt. This contract
    /// deliberately makes no filesystem-fsync claim the platform API cannot prove.
    /// </summary>
    internal interface IDurableEffectCheckpointBackend
    {
        bool ReloadCompletesBeforeReturn { get; }
        void WaitForActiveSavesToDrain();
        void Freeze();
        void WriteCheckpoint(DurableEffectCheckpoint checkpoint);
        bool VerifyCheckpoint(DurableEffectCheckpoint checkpoint);
        bool TryFindCheckpoint(OccurrenceId occurrence, EffectToken token,
            DurableEffectCheckpointKind kind, out DurableEffectCheckpoint checkpoint);
        void ForceReload(DurableEffectCheckpoint checkpoint);
        void Unfreeze();
    }

    internal enum DurableEffectRecovery : byte
    {
        ReloadingPending,
        RestoredCommitted
    }

    /// <summary>
    /// Owns the failure boundary around an otherwise opaque synchronous native campaign effect.
    /// It deliberately has no game or Harmony dependencies so the ordering and crash paths can be
    /// exercised by RailCheck with an in-memory backend.
    /// </summary>
    internal sealed class DurableEffectTransactionCoordinator
    {
        private static readonly object TransactionGate = new object();
        private readonly IDurableEffectCheckpointBackend _backend;
        private readonly Func<string> _newCheckpointId;

        internal DurableEffectTransactionCoordinator(IDurableEffectCheckpointBackend backend,
            Func<string> newCheckpointId = null)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _newCheckpointId = newCheckpointId ?? (() => Guid.NewGuid().ToString("N"));
        }

        internal DurableEffectCheckpoint Execute(OccurrenceId occurrence, EffectToken token,
            Action executeNativeOnce, Action<DurableEffectCheckpoint> broadcastCommitted)
            => Execute(occurrence, token, executeNativeOnce, () => { }, broadcastCommitted);

        internal DurableEffectCheckpoint Execute(OccurrenceId occurrence, EffectToken token,
            Action executeNativeOnce, Action commitAuthoritativeState,
            Action<DurableEffectCheckpoint> broadcastCommitted)
        {
            Validate(occurrence, token, executeNativeOnce, broadcastCommitted);
            if (commitAuthoritativeState == null) throw new ArgumentNullException(nameof(commitAuthoritativeState));
            // Allocate PRE before entering the failure boundary.  Once Freeze has even been attempted,
            // every exit path therefore has a concrete authoritative reload target.
            var pending = NewCheckpoint(DurableEffectCheckpointKind.Pending, occurrence, token, null);
            lock (TransactionGate)
            {
                bool pendingVerified = false;
                bool committedVerified = false;
                try
                {
                    _backend.WaitForActiveSavesToDrain();
                    _backend.Freeze();
                    WriteAndVerify(pending);
                    pendingVerified = true;
                    executeNativeOnce();
                    // Nothing observed while opaque native code was midway is a valid replicated state.
                    // POST-facing reward/decision messages are regenerated explicitly after verification.
                    DurableEffectTransactionBarrier.DiscardUncommittedOutbound();
                    // The POST snapshot must contain ChoiceLocked, never EffectPending.  This callback
                    // mutates only the durable authority journal after native campaign completion.
                    commitAuthoritativeState();

                    var committed = NewCheckpoint(DurableEffectCheckpointKind.Committed, occurrence, token,
                        pending.CheckpointId);
                    if (string.Equals(pending.CheckpointId, committed.CheckpointId, StringComparison.Ordinal))
                        throw new InvalidOperationException("pending and committed checkpoints must be unique");
                    WriteAndVerify(committed);
                    committedVerified = true;

                    broadcastCommitted(committed);
                    _backend.Unfreeze();
                    return committed;
                }
                catch (Exception ex)
                {
                    if (committedVerified)
                    {
                        // POST is now authority. Delivery is idempotently retried by the durable decision
                        // rebroadcast; rolling the world back after any peer saw POST would fork history.
                        _backend.Unfreeze();
                        throw;
                    }
                    DurableEffectTransactionBarrier.DiscardUncommittedOutbound();
                    // Never unfreeze a possibly partial world.  ForceReload is required even when the
                    // PRE write itself failed: its unique identity is the authoritative recovery target.
                    // Before PRE verifies, native has not run and there is no partial campaign world to
                    // reload. Remain frozen and retry the same PRE identity; never pretend an unwritten
                    // checkpoint is a rollback target. Once PRE verified, every later failure reloads it.
                    if (pendingVerified)
                    {
                        if (!_backend.ReloadCompletesBeforeReturn) DurableEffectTransactionBarrier.MarkRecoveryLoad(token);
                        _backend.ForceReload(pending); throw;
                    }
                    _backend.Unfreeze();
                    throw new DurableEffectPreCheckpointException(ex);
                }
            }
        }

        internal DurableEffectRecovery Recover(OccurrenceId occurrence, EffectToken token,
            Action<DurableEffectCheckpoint> broadcastCommitted)
        {
            if (!token.Occurrence.Equals(occurrence))
                throw new ArgumentException("effect token belongs to another occurrence", nameof(token));
            if (broadcastCommitted == null) throw new ArgumentNullException(nameof(broadcastCommitted));

            lock (TransactionGate)
            {
                _backend.WaitForActiveSavesToDrain();
                _backend.Freeze();

                DurableEffectCheckpoint committed;
                if (_backend.TryFindCheckpoint(occurrence, token, DurableEffectCheckpointKind.Committed,
                        out committed) && IsExactVerified(committed, DurableEffectCheckpointKind.Committed,
                            occurrence, token))
                {
                    try
                    {
                        _backend.ForceReload(committed);
                        if (!_backend.ReloadCompletesBeforeReturn)
                        {
                            DurableEffectTransactionBarrier.MarkRecoveryLoad(token);
                            return DurableEffectRecovery.RestoredCommitted;
                        }
                        broadcastCommitted(committed);
                        _backend.Unfreeze();
                        return DurableEffectRecovery.RestoredCommitted;
                    }
                    catch
                    {
                        // A failed POST recovery is not allowed to expose the current in-memory world.
                        // Leave the backend frozen for the next authoritative recovery attempt.
                        throw;
                    }
                }

                DurableEffectCheckpoint pending;
                if (!_backend.TryFindCheckpoint(occurrence, token, DurableEffectCheckpointKind.Pending,
                        out pending) || !IsExactVerified(pending, DurableEffectCheckpointKind.Pending,
                            occurrence, token))
                    throw new InvalidOperationException("no verified checkpoint exists for the durable effect");

                if (!_backend.ReloadCompletesBeforeReturn)
                    DurableEffectTransactionBarrier.MarkRecoveryLoad(token);
                _backend.ForceReload(pending);
                // PRE is a retry boundary, not a completed transaction.  The reload remains frozen and
                // no broadcast is legal until a later Execute produces and verifies POST.
                return DurableEffectRecovery.ReloadingPending;
            }
        }

        private DurableEffectCheckpoint NewCheckpoint(DurableEffectCheckpointKind kind,
            OccurrenceId occurrence, EffectToken token, string pendingCheckpointId)
        {
            string id = InboxIdentity.Required(_newCheckpointId(), "checkpointId");
            return new DurableEffectCheckpoint(id, kind, occurrence, token, pendingCheckpointId);
        }

        private void WriteAndVerify(DurableEffectCheckpoint checkpoint)
        {
            _backend.WriteCheckpoint(checkpoint);
            if (!_backend.VerifyCheckpoint(checkpoint))
                throw new InvalidOperationException("campaign checkpoint read-back verification failed: " +
                    checkpoint.Kind);
        }

        private bool IsExactVerified(DurableEffectCheckpoint checkpoint, DurableEffectCheckpointKind kind,
            OccurrenceId occurrence, EffectToken token) => checkpoint != null && checkpoint.Kind == kind &&
            checkpoint.Occurrence.Equals(occurrence) && checkpoint.Token.Equals(token) &&
            _backend.VerifyCheckpoint(checkpoint);

        private static void Validate(OccurrenceId occurrence, EffectToken token, Action executeNativeOnce,
            Action<DurableEffectCheckpoint> broadcastCommitted)
        {
            if (!token.Occurrence.Equals(occurrence))
                throw new ArgumentException("effect token belongs to another occurrence", nameof(token));
            if (executeNativeOnce == null) throw new ArgumentNullException(nameof(executeNativeOnce));
            if (broadcastCommitted == null) throw new ArgumentNullException(nameof(broadcastCommitted));
        }
    }
}
