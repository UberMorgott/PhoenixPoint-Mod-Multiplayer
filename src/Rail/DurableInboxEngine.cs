using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Network.Sync
{
    internal interface IDurableWindowCarrierAdapter
    {
        InboxWindowCheckpoint Capture(OccurrenceId occurrence);
        bool Present(OccurrenceId occurrence);
        bool Restore(OccurrenceId occurrence, InboxWindowCheckpoint checkpoint);
        void Abandon(OccurrenceId occurrence);
        void FinalizeRestore(OccurrenceId occurrence);
    }

    internal enum TerminalReason : byte
    {
        Invalidated,
        Superseded,
        MissionCompleted,
        Launched,
        MembershipEnded,
        LevelTeardown
    }

    internal enum DurableCarrierClass : byte
    {
        NativeCurrent,
        NativePending,
        ModQueued,
        ModSuspended,
        ModDeferred,
        WireReplay,
        TacticalHeld,
        Deployment
    }

    internal interface IDurableOccurrenceCarrier
    {
        void RemoveWithoutCallback(TerminalReason reason);
    }

    /// <summary>A family-owned carrier registration. Dispose is normal consumption; terminal removal is silent.</summary>
    internal sealed class DurableCarrierLease : IDurableOccurrenceCarrier, IDisposable
    {
        private readonly DurableInboxStore _store; private readonly OccurrenceId _occurrence;
        private Action<TerminalReason> _silentRemove; private int _state; // 0 ready, 1 running, 2 success

        private DurableCarrierLease(DurableInboxStore store, OccurrenceId occurrence,
            DurableCarrierClass carrierClass, Action<TerminalReason> silentRemove)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _occurrence = occurrence; _silentRemove = silentRemove ?? throw new ArgumentNullException(nameof(silentRemove));
            store.Carriers.Register(occurrence, carrierClass, this);
        }

        internal static DurableCarrierLease Bind(DurableInboxStore store, OccurrenceId occurrence,
            DurableCarrierClass carrierClass, Action<TerminalReason> silentRemove)
        {
            if (store == null || !store.Ledger.Contains(occurrence))
                throw new InvalidOperationException("authoritative occurrence must exist before carrier binding");
            return new DurableCarrierLease(store, occurrence, carrierClass, silentRemove);
        }
        internal bool IsFinished => System.Threading.Volatile.Read(ref _state) == 2;

        public void Dispose()
        {
            var spin = new System.Threading.SpinWait();
            while (true)
            {
                int state = System.Threading.Volatile.Read(ref _state);
                if (state == 2) return;
                if (state == 1) { spin.SpinOnce(); continue; }
                if (System.Threading.Interlocked.CompareExchange(ref _state, 1, 0) != 0) continue;
                _store.Carriers.Unregister(_occurrence, this); _silentRemove = null;
                System.Threading.Volatile.Write(ref _state, 2); return;
            }
        }

        public void RemoveWithoutCallback(TerminalReason reason)
        {
            var spin = new System.Threading.SpinWait();
            while (true)
            {
                int state = System.Threading.Volatile.Read(ref _state);
                if (state == 2) return;
                if (state == 1) { spin.SpinOnce(); continue; }
                if (System.Threading.Interlocked.CompareExchange(ref _state, 1, 0) != 0) continue;
                try
                {
                    _silentRemove(reason);
                    _silentRemove = null;
                    System.Threading.Volatile.Write(ref _state, 2); return;
                }
                catch
                {
                    System.Threading.Volatile.Write(ref _state, 0);
                    throw;
                }
            }
        }
    }

    /// <summary>Live presentation objects indexed by durable identity, never by their native queue position.</summary>
    internal sealed class DurableCarrierRegistry
    {
        private sealed class Binding
        {
            internal DurableCarrierClass Class;
            internal IDurableOccurrenceCarrier Carrier;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<OccurrenceId, List<Binding>> _byOccurrence =
            new Dictionary<OccurrenceId, List<Binding>>();
        private readonly Dictionary<OccurrenceId, TerminalReason> _sealed =
            new Dictionary<OccurrenceId, TerminalReason>();
        private readonly HashSet<OccurrenceId> _removing = new HashSet<OccurrenceId>();
        private readonly Dictionary<OccurrenceId, int> _inFlight = new Dictionary<OccurrenceId, int>();
        private bool _abandoned, _abandoning;
        private int _activeCallbacks;

        internal void Register(OccurrenceId occurrence, DurableCarrierClass carrierClass,
            IDurableOccurrenceCarrier carrier)
        {
            if (!Enum.IsDefined(typeof(DurableCarrierClass), carrierClass))
                throw new ArgumentOutOfRangeException(nameof(carrierClass));
            if (carrier == null) throw new ArgumentNullException(nameof(carrier));
            TerminalReason terminalReason;
            bool abandonedRegistration = false;
            lock (_gate)
            {
                if (_abandoned)
                {
                    terminalReason = TerminalReason.LevelTeardown; abandonedRegistration = true;
                    _activeCallbacks = checked(_activeCallbacks + 1); goto Terminal;
                }
                if (_sealed.TryGetValue(occurrence, out terminalReason))
                {
                    int count; _inFlight.TryGetValue(occurrence, out count);
                    _inFlight[occurrence] = checked(count + 1);
                    _activeCallbacks = checked(_activeCallbacks + 1); goto Terminal;
                }
                List<Binding> bindings;
                if (!_byOccurrence.TryGetValue(occurrence, out bindings))
                    _byOccurrence.Add(occurrence, bindings = new List<Binding>());
                var existing = bindings.FirstOrDefault(x => ReferenceEquals(x.Carrier, carrier));
                if (existing == null) bindings.Add(new Binding { Class = carrierClass, Carrier = carrier });
                else existing.Class = carrierClass;
                return;
            }
        Terminal:
            // Never publish a carrier after terminal removal started. Callback runs outside the registry lock,
            // so a carrier may safely register another carrier during its own silent teardown.
            bool lateFailed = false;
            try { carrier.RemoveWithoutCallback(terminalReason); }
            catch { lateFailed = true; }
            finally
            {
                lock (_gate)
                {
                    if (lateFailed && !abandonedRegistration && !_abandoned)
                    {
                        List<Binding> failed;
                        if (!_byOccurrence.TryGetValue(occurrence, out failed))
                            _byOccurrence.Add(occurrence, failed = new List<Binding>());
                        if (!failed.Any(x => ReferenceEquals(x.Carrier, carrier)))
                            failed.Add(new Binding { Class = carrierClass, Carrier = carrier });
                    }
                    _activeCallbacks--;
                    if (!abandonedRegistration)
                    {
                        int count = _inFlight[occurrence] - 1;
                        if (count == 0) _inFlight.Remove(occurrence); else _inFlight[occurrence] = count;
                    }
                    System.Threading.Monitor.PulseAll(_gate);
                }
            }
        }

        internal void Unregister(OccurrenceId occurrence, IDurableOccurrenceCarrier carrier)
        {
            if (carrier == null) return;
            lock (_gate)
            {
                List<Binding> bindings;
                if (!_byOccurrence.TryGetValue(occurrence, out bindings)) return;
                bindings.RemoveAll(x => ReferenceEquals(x.Carrier, carrier));
                if (bindings.Count == 0) _byOccurrence.Remove(occurrence);
            }
        }

        internal int Count(OccurrenceId occurrence)
        { lock (_gate) { List<Binding> bindings; return _byOccurrence.TryGetValue(occurrence, out bindings) ? bindings.Count : 0; } }

        internal void AbandonStore()
        {
            Binding[] carriers;
            lock (_gate)
            {
                while (_abandoning) System.Threading.Monitor.Wait(_gate);
                if (_abandoned) return;
                _abandoning = true; _abandoned = true;
                carriers = _byOccurrence.Values.SelectMany(x => x).ToArray(); _byOccurrence.Clear();
                _activeCallbacks = checked(_activeCallbacks + carriers.Length);
            }
            foreach (var binding in carriers)
            {
                try { binding.Carrier.RemoveWithoutCallback(TerminalReason.LevelTeardown); } catch { }
                finally { lock (_gate) { _activeCallbacks--; System.Threading.Monitor.PulseAll(_gate); } }
            }
            lock (_gate)
            {
                while (_activeCallbacks != 0 || _removing.Count != 0)
                    System.Threading.Monitor.Wait(_gate);
                _byOccurrence.Clear(); _abandoning = false; System.Threading.Monitor.PulseAll(_gate);
            }
        }

        internal bool RemoveAll(OccurrenceId occurrence, TerminalReason reason, out string refusal)
        {
            if (!Enum.IsDefined(typeof(TerminalReason), reason))
            { refusal = "unknown terminal reason"; return false; }
            Binding[] snapshot;
            lock (_gate)
            {
                if (_abandoned) { refusal = "carrier store is abandoned"; return false; }
                TerminalReason sealedReason;
                if (_sealed.TryGetValue(occurrence, out sealedReason) && sealedReason != reason)
                { refusal = "terminal reason does not match the occurrence seal"; return false; }
                _sealed[occurrence] = reason;
                if (_removing.Contains(occurrence))
                { refusal = "terminal carrier removal is already in progress"; return false; }
                List<Binding> bindings;
                int activeLate;
                if (!_byOccurrence.TryGetValue(occurrence, out bindings) &&
                    (!_inFlight.TryGetValue(occurrence, out activeLate) || activeLate == 0))
                { refusal = null; return true; }
                _removing.Add(occurrence);
                snapshot = bindings == null ? Array.Empty<Binding>() : bindings.ToArray();
                _byOccurrence.Remove(occurrence);
                _activeCallbacks = checked(_activeCallbacks + snapshot.Length);
            }
            var failed = new List<Binding>();
            foreach (var binding in snapshot)
            {
                try { binding.Carrier.RemoveWithoutCallback(reason); }
                catch { failed.Add(binding); }
                finally { lock (_gate) { _activeCallbacks--; System.Threading.Monitor.PulseAll(_gate); } }
            }
            int remaining;
            lock (_gate)
            {
                int inFlight;
                while (_inFlight.TryGetValue(occurrence, out inFlight) && inFlight != 0)
                    System.Threading.Monitor.Wait(_gate);
                List<Binding> current;
                if (!_byOccurrence.TryGetValue(occurrence, out current) && failed.Count != 0)
                    _byOccurrence.Add(occurrence, current = new List<Binding>());
                if (current != null)
                    foreach (var binding in failed.Where(x => !current.Any(y => ReferenceEquals(y.Carrier, x.Carrier))))
                        current.Add(binding);
                remaining = current == null ? 0 : current.Count;
                _removing.Remove(occurrence);
                System.Threading.Monitor.PulseAll(_gate);
            }
            refusal = remaining == 0 ? null : "one or more carriers refused silent removal";
            return remaining == 0;
        }

    }

    /// <summary>
    /// Per-player durable scheduler.  The native request is only a carrier: lifecycle and the captured
    /// read position are committed before a priority carrier is allowed to replace an ordinary one.
    /// No method consults another player's readiness, so an AFK peer cannot veto progress.
    /// </summary>
    internal sealed class DurableInboxEngine
    {
        private readonly DurableInboxStore _store;
        private readonly MembershipId _member;
        private readonly IDurableWindowCarrierAdapter _carrier;
        private readonly DurableCarrierRegistry _registry;

        internal DurableInboxEngine(DurableInboxStore store, MembershipId member,
            IDurableWindowCarrierAdapter carrier, DurableCarrierRegistry registry = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _member = member;
            _carrier = carrier ?? throw new ArgumentNullException(nameof(carrier));
            _registry = registry ?? store.Carriers;
        }

        internal bool RemoveAllCarriers(OccurrenceId occurrence, TerminalReason reason,
            ulong committedRevision, out string refusal)
        {
            if (!_store.AuthorizeCarrierRemoval(occurrence, reason, committedRevision, out refusal)) return false;
            return _registry.RemoveAll(occurrence, reason, out refusal);
        }

        internal bool TryPresentNext(bool geoscapeStarted, Type currentViewState)
        {
            if (!DurableWindowRegistry.MayPresent(geoscapeStarted, currentViewState)) return false;
            var entries = _store.Ledger.EntriesFor(_member);
            if (entries.Count(x => x.Lifecycle == InboxLifecycle.Open) > 1) return false;
            var next = entries.Where(x => x.Lifecycle == InboxLifecycle.Queued)
                .OrderByDescending(x => DurableWindowRegistry.PriorityOf(x.Occurrence)).ThenBy(x => x.HostOrderKey).FirstOrDefault();
            if (next == null) return TryResumeSuspended(geoscapeStarted, currentViewState);
            var open = entries.FirstOrDefault(x => x.Lifecycle == InboxLifecycle.Open);
            if (open != null)
                return DurableWindowRegistry.PriorityOf(next.Occurrence) > DurableWindowRegistry.PriorityOf(open.Occurrence) &&
                    TryPreempt(open.Occurrence, next.Occurrence, geoscapeStarted, currentViewState);
            return CommitOpenAndPresent(next);
        }

        internal bool ConfirmNativePresented(OccurrenceId occurrence)
        {
            var expected = _store.Ledger; InboxEntry entry;
            try { entry = expected.Get(occurrence, _member); } catch (InvalidOperationException) { return false; }
            if (entry.Lifecycle == InboxLifecycle.Open) return true;
            if (entry.Lifecycle != InboxLifecycle.Queued) return false;
            var next = expected.Replace(entry.WithLifecycle(InboxLifecycle.Open, checked(entry.LifecycleRevision + 1)))
                .WithAuthority(checked(expected.CommittedRevision + 1), expected.Members);
            try { return _store.Commit(expected, next); } catch { return false; }
        }

        internal bool TryPreempt(OccurrenceId ordinary, OccurrenceId priority,
            bool geoscapeStarted, Type currentViewState)
        {
            if (!DurableWindowRegistry.MayPresent(geoscapeStarted, currentViewState) ||
                DurableWindowRegistry.PriorityOf(ordinary) != DurableWindowPriority.Ordinary ||
                DurableWindowRegistry.PriorityOf(priority) == DurableWindowPriority.Ordinary) return false;
            var expected = _store.Ledger;
            InboxEntry ordinaryEntry, priorityEntry;
            try { ordinaryEntry = expected.Get(ordinary, _member); priorityEntry = expected.Get(priority, _member); }
            catch (InvalidOperationException) { return false; }
            if (ordinaryEntry.Lifecycle != InboxLifecycle.Open || priorityEntry.Lifecycle != InboxLifecycle.Queued)
                return false;
            InboxWindowCheckpoint checkpoint;
            try { checkpoint = _carrier.Capture(ordinary); } catch { checkpoint = null; }
            if (checkpoint == null) return false; // capture failure: ordinary remains open, priority remains queued
            ulong revision = checked(expected.CommittedRevision + 1);
            var suspended = ordinaryEntry.Suspend(InboxSuspensionReason.PriorityPreemption, checkpoint,
                checked(ordinaryEntry.LifecycleRevision + 1));
            var next = expected.Replace(suspended).WithAuthority(revision, expected.Members);
            if (!_store.Commit(expected, next)) return false;
            bool presented;
            try { presented = _carrier.Present(priority); } catch { presented = false; }
            if (!presented)
            {
                try { _carrier.Abandon(priority); } catch { }
                RestoreAfterFailedPresent(ordinary, checkpoint); return false;
            }
            var presentedLedger = _store.Ledger; InboxEntry stillQueued, stillSuspended;
            try
            {
                stillQueued = presentedLedger.Get(priority, _member);
                stillSuspended = presentedLedger.Get(ordinary, _member);
            }
            catch
            {
                try { _carrier.Abandon(priority); } catch { }
                return false;
            }
            if (stillQueued.Lifecycle != InboxLifecycle.Queued ||
                stillSuspended.Lifecycle != InboxLifecycle.Suspended ||
                stillSuspended.SuspensionReason != InboxSuspensionReason.PriorityPreemption ||
                !checkpoint.Equals(stillSuspended.Checkpoint))
            {
                try { _carrier.Abandon(priority); } catch { }
                if (stillSuspended.Lifecycle == InboxLifecycle.Suspended && checkpoint.Equals(stillSuspended.Checkpoint))
                    RestoreAfterFailedPresent(ordinary, checkpoint);
                return false;
            }
            bool openedCommitted = false;
            try
            {
                var opened = stillQueued.WithLifecycle(InboxLifecycle.Open, checked(stillQueued.LifecycleRevision + 1));
                var committed = presentedLedger.Replace(opened)
                    .WithAuthority(checked(presentedLedger.CommittedRevision + 1), presentedLedger.Members);
                openedCommitted = _store.Commit(presentedLedger, committed);
            }
            catch { openedCommitted = false; }
            if (openedCommitted) return true;
            try { _carrier.Abandon(priority); } catch { }
            RestoreAfterFailedPresent(ordinary, checkpoint);
            return false;
        }

        internal bool TryResumeSuspended(bool geoscapeStarted, Type currentViewState)
        {
            if (!DurableWindowRegistry.MayPresent(geoscapeStarted, currentViewState)) return false;
            var expected = _store.Ledger;
            var entries = expected.EntriesFor(_member);
            if (entries.Any(x => DurableWindowRegistry.PriorityOf(x.Occurrence) != DurableWindowPriority.Ordinary &&
                (x.Lifecycle == InboxLifecycle.Queued || x.Lifecycle == InboxLifecycle.Open))) return false;
            if (entries.Any(x => x.Lifecycle == InboxLifecycle.Open)) return false;
            var suspended = entries.Where(x => x.Lifecycle == InboxLifecycle.Suspended &&
                x.SuspensionReason == InboxSuspensionReason.PriorityPreemption)
                .OrderBy(x => x.HostOrderKey).FirstOrDefault();
            if (suspended == null) return false;
            if (!_store.IsServable(suspended.Occurrence)) return RemoveInvalidatedSuspended(suspended);
            var checkpoint = suspended.Checkpoint;
            ulong revision = checked(expected.CommittedRevision + 1);
            bool restored;
            try { restored = _carrier.Restore(suspended.Occurrence, checkpoint); } catch { restored = false; }
            if (!restored) return false;
            var next = expected.Replace(suspended.WithLifecycle(InboxLifecycle.Open,
                checked(suspended.LifecycleRevision + 1))).WithAuthority(revision, expected.Members);
            bool committed;
            try { committed = _store.Commit(expected, next); } catch { committed = false; }
            if (!committed)
            {
                try { _carrier.Abandon(suspended.Occurrence); } catch { }
                return false;
            }
            try { _carrier.FinalizeRestore(suspended.Occurrence); } catch { }
            return true;
        }

        private bool CommitOpenAndPresent(InboxEntry entry)
        {
            var expected = _store.Ledger;
            InboxEntry current;
            try { current = expected.Get(entry.Occurrence, _member); }
            catch (InvalidOperationException) { return false; }
            if (current.Lifecycle != InboxLifecycle.Queued) return false;
            ulong revision = checked(expected.CommittedRevision + 1);
            bool presented;
            try { presented = _carrier.Present(entry.Occurrence); } catch { presented = false; }
            if (!presented) return false;
            var next = expected.Replace(current.WithLifecycle(InboxLifecycle.Open,
                checked(current.LifecycleRevision + 1))).WithAuthority(revision, expected.Members);
            bool committed;
            try { committed = _store.Commit(expected, next); } catch { committed = false; }
            if (!committed) { try { _carrier.Abandon(entry.Occurrence); } catch { } }
            return committed;
        }

        private bool RemoveInvalidatedSuspended(InboxEntry suspended)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                var expected = _store.Ledger; InboxEntry current;
                try { current = expected.Get(suspended.Occurrence, _member); } catch (InvalidOperationException) { return false; }
                if (current.Lifecycle == InboxLifecycle.Removed || current.Lifecycle == InboxLifecycle.Dismissed) return false;
                if (current.Lifecycle != InboxLifecycle.Suspended || current.LifecycleRevision < suspended.LifecycleRevision) return false;
                ulong lifecycle = checked(current.LifecycleRevision + 1);
                var removed = new InboxEntry(current.Occurrence, current.Membership, InboxLifecycle.Removed,
                    current.Choice, lifecycle, Math.Max(current.TombstoneRevision, lifecycle), current.HostOrderKey,
                    terminalReason: TerminalReason.Invalidated);
                var next = expected.Replace(removed).WithAuthority(checked(expected.CommittedRevision + 1), expected.Members);
                try { if (_store.Commit(expected, next)) return false; } catch { }
            }
            return false;
        }

        private void RestoreAfterFailedPresent(OccurrenceId occurrence, InboxWindowCheckpoint checkpoint)
        {
            bool restored;
            try { restored = _carrier.Restore(occurrence, checkpoint); } catch { restored = false; }
            if (!restored) return;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                var expected = _store.Ledger; InboxEntry current;
                try { current = expected.Get(occurrence, _member); } catch { break; }
                if (current.Lifecycle != InboxLifecycle.Suspended || !current.Checkpoint.Equals(checkpoint)) break;
                var open = current.WithLifecycle(InboxLifecycle.Open, checked(current.LifecycleRevision + 1));
                var next = expected.Replace(open).WithAuthority(checked(expected.CommittedRevision + 1), expected.Members);
                bool committed; try { committed = _store.Commit(expected, next); } catch { committed = false; }
                if (!committed) continue;
                try { _carrier.FinalizeRestore(occurrence); } catch { }
                return;
            }
            try { _carrier.Abandon(occurrence); } catch { }
        }

    }

    internal interface IDurableSharedChoiceEffect
    {
        IReadOnlyList<DurableEffectStep> Prepare(EffectToken token);
        DurableEffectObservation Observe(SharedChoiceDecision decision, DurableEffectStep step);
        void Apply(SharedChoiceDecision decision, DurableEffectStep step);
    }

    internal enum DurableEffectObservation { Before, After, Diverged }

    internal sealed class DelegateDurableChoiceEffect : IDurableSharedChoiceEffect
    {
        private readonly Func<EffectToken, IReadOnlyList<DurableEffectStep>> _prepare;
        private readonly Action<SharedChoiceDecision, DurableEffectStep> _apply;
        private readonly Func<SharedChoiceDecision, DurableEffectStep, DurableEffectObservation> _observe;
        internal DelegateDurableChoiceEffect(Func<EffectToken, IReadOnlyList<DurableEffectStep>> prepare,
            Action<SharedChoiceDecision, DurableEffectStep> apply,
            Func<SharedChoiceDecision, DurableEffectStep, DurableEffectObservation> observe)
        { _prepare = prepare ?? throw new ArgumentNullException(nameof(prepare));
          _apply = apply ?? throw new ArgumentNullException(nameof(apply));
          _observe = observe ?? throw new ArgumentNullException(nameof(observe)); }
        internal DelegateDurableChoiceEffect(Action<SharedChoiceDecision> apply,
            Func<SharedChoiceDecision, DurableEffectObservation> observe,
            string beforeFact = "unanswered", string afterFact = "applied")
            : this(token => new[] { new DurableEffectStep("effect", "delegate", beforeFact, afterFact) },
                (decision, step) => apply(decision), (decision, step) => observe(decision)) { }
        internal DelegateDurableChoiceEffect(Action<SharedChoiceDecision, Action<SharedEffectReceipt>> apply,
            Func<SharedChoiceDecision, DurableEffectObservation> observe,
            string beforeFact, string afterFact)
            : this(token => new[] { new DurableEffectStep("effect", "delegate", beforeFact, afterFact) },
                (decision, step) => apply(decision, _ => { }), (decision, step) => observe(decision)) { }
        public IReadOnlyList<DurableEffectStep> Prepare(EffectToken token) => _prepare(token);
        public void Apply(SharedChoiceDecision decision, DurableEffectStep step) => _apply(decision, step);
        public DurableEffectObservation Observe(SharedChoiceDecision decision, DurableEffectStep step) => _observe(decision, step);
    }

    internal enum SharedChoiceCrashPoint
    {
        PendingCommitted,
        NativeEffectReturned,
        EffectAppliedCommitted,
        ChoiceLockedBeforeResponse
    }

    /// <summary>
    /// Host-only durable transaction for an ordinary shared event choice. No peer readiness is consulted:
    /// the first CAS that journals EffectPending wins. Every entitled copy keeps its own lifecycle while its
    /// canonical choice/result is frozen, so AFK, tactical and suspended peers can read and dismiss later.
    /// </summary>
    internal sealed class DurableSharedChoiceEngine
    {
        private readonly DurableInboxStore _store;
        private readonly IDurableSharedChoiceEffect _effect;
        private readonly Action<OccurrenceId> _repaint;
        internal Action<SharedChoiceCrashPoint> CrashProbe { get; set; }

        internal DurableSharedChoiceEngine(DurableInboxStore store, IDurableSharedChoiceEffect effect,
            Action<OccurrenceId> repaint)
        { _store = store ?? throw new ArgumentNullException(nameof(store)); _effect = effect ?? throw new ArgumentNullException(nameof(effect));
          _repaint = repaint ?? throw new ArgumentNullException(nameof(repaint)); }

        internal bool TryAnswer(OccurrenceId occurrence, MembershipId winner, CanonicalChoiceId choice,
            CanonicalResultId result, IEnumerable<CanonicalRewardItemId> rewards, Func<bool> validate,
            out SharedChoiceDecision decision)
        {
            decision = null;
            if (validate == null) throw new ArgumentNullException(nameof(validate));
            if (!choice.Occurrence.Equals(occurrence) || !result.Occurrence.Equals(occurrence)) return false;
            var rewardArray = (rewards ?? throw new ArgumentNullException(nameof(rewards))).ToArray();
            if (rewardArray.Any(x => !x.Occurrence.Equals(occurrence))) return false;

            for (int attempt = 0; attempt < 64; attempt++)
            {
                var canonical = _store.Canonical;
                var existing = canonical.Decisions.SingleOrDefault(x => x.Occurrence.Equals(occurrence));
                if (existing != null) return Recover(existing, out decision);
                var ledger = _store.Ledger;
                MemberPresence presence;
                if (!ledger.Members.TryGetValue(winner, out presence) || !MemberPresenceRules.IsEnrolled(presence) ||
                    !ledger.AllEntries.Any(x => x.Occurrence.Equals(occurrence) && x.Membership.Equals(winner))) return false;
                bool valid;
                try { valid = validate(); } catch { return false; }
                if (!valid) return false; // remains Unanswered: no journal, charge, grant or lock
                var token = new EffectToken(occurrence, "effect:" + occurrence.TriggerId);
                IReadOnlyList<DurableEffectStep> prepared;
                try { prepared = _effect.Prepare(token); } catch { return false; }
                if (prepared == null || prepared.Count == 0 || prepared.Any(x => x.State != DurableEffectStepState.Prepared)) return false;
                var pending = new SharedChoiceDecision(occurrence, token, choice, result, rewardArray,
                    winner, SharedChoicePhase.EffectPending, prepared);
                var entries = ledger.AllEntries.Select(x => x.Occurrence.Equals(occurrence)
                    ? new InboxEntry(x.Occurrence, x.Membership, x.Lifecycle, choice,
                        x.LifecycleRevision, x.TombstoneRevision, x.HostOrderKey,
                        x.SuspensionReason, x.Checkpoint, x.TerminalReason)
                    : x).ToArray();
                var next = new HostLedger(entries, checked(ledger.CommittedRevision + 1), ledger.Members);
                if (!_store.CommitWithCanonical(ledger, next, canonical.WithDecision(pending))) continue;
                Probe(SharedChoiceCrashPoint.PendingCommitted);
                return Recover(pending, out decision);
            }
            return false;
        }

        /// <summary>Production opaque-campaign path. PRE is written after EffectPending is committed;
        /// native CompleteEvent runs once between PRE and POST; ChoiceLocked is committed before POST is
        /// snapshotted. Any failure is owned by the coordinator's mandatory PRE reload boundary.</summary>
        internal bool TryAnswerCheckpointed(OccurrenceId occurrence, MembershipId winner,
            CanonicalChoiceId choice, CanonicalResultId result, IEnumerable<CanonicalRewardItemId> rewards,
            Func<bool> validate, Func<EffectToken, DurableEffectTransactionCoordinator> coordinatorFactory,
            Action executeNativeOnce, Action<SharedChoiceDecision> broadcast, out SharedChoiceDecision decision)
        {
            decision = null;
            if (validate == null || coordinatorFactory == null || executeNativeOnce == null || broadcast == null)
                throw new ArgumentNullException("checkpointed choice dependency");
            if (!choice.Occurrence.Equals(occurrence) || !result.Occurrence.Equals(occurrence)) return false;
            var rewardArray = (rewards ?? throw new ArgumentNullException(nameof(rewards))).ToArray();
            if (rewardArray.Any(x => !x.Occurrence.Equals(occurrence))) return false;
            SharedChoiceDecision pending = null;
            for (int attempt = 0; attempt < 64 && pending == null; attempt++)
            {
                var canonical = _store.Canonical;
                var existing = canonical.Decisions.SingleOrDefault(x => x.Occurrence.Equals(occurrence));
                if (existing != null) { decision = existing; return existing.Phase == SharedChoicePhase.ChoiceLocked; }
                var ledger = _store.Ledger; MemberPresence presence;
                if (!ledger.Members.TryGetValue(winner, out presence) || !MemberPresenceRules.IsEnrolled(presence) ||
                    !ledger.AllEntries.Any(x => x.Occurrence.Equals(occurrence) && x.Membership.Equals(winner))) return false;
                bool valid; try { valid = validate(); } catch { return false; } if (!valid) return false;
                var token = new EffectToken(occurrence, "effect:" + occurrence.TriggerId);
                // Canonical choice/result/reward/RNG-derived facts are all in the pending journal before PRE.
                var barrierStep = new DurableEffectStep("campaign-checkpoint", "native-complete-event",
                    "checkpoint:PRE", "checkpoint:POST");
                var candidate = new SharedChoiceDecision(occurrence, token, choice, result, rewardArray, winner,
                    SharedChoicePhase.EffectPending, new[] { barrierStep });
                var entries = ledger.AllEntries.Select(x => x.Occurrence.Equals(occurrence)
                    ? new InboxEntry(x.Occurrence, x.Membership, x.Lifecycle, choice, x.LifecycleRevision,
                        x.TombstoneRevision, x.HostOrderKey, x.SuspensionReason, x.Checkpoint, x.TerminalReason) : x).ToArray();
                var next = new HostLedger(entries, checked(ledger.CommittedRevision + 1), ledger.Members);
                if (_store.CommitWithCanonical(ledger, next, canonical.WithDecision(candidate))) pending = candidate;
            }
            if (pending == null) return false;
            SharedChoiceDecision locked = null;
            try
            {
                coordinatorFactory(pending.EffectToken).Execute(occurrence, pending.EffectToken, executeNativeOnce, () =>
                {
                if (!PersistAppliedStep(occurrence, "campaign-checkpoint"))
                    throw new InvalidOperationException("could not commit durable campaign effect receipt");
                var live = _store.Canonical.Decisions.Single(x => x.Occurrence.Equals(occurrence));
                if (!Advance(live, SharedChoicePhase.EffectApplied))
                    throw new InvalidOperationException("could not commit EffectApplied");
                live = _store.Canonical.Decisions.Single(x => x.Occurrence.Equals(occurrence));
                if (!Advance(live, SharedChoicePhase.ChoiceLocked))
                    throw new InvalidOperationException("could not commit ChoiceLocked");
                locked = _store.Canonical.Decisions.Single(x => x.Occurrence.Equals(occurrence));
                }, _ =>
                {
                _repaint(occurrence);
                broadcast(locked ?? throw new InvalidOperationException("POST verified without ChoiceLocked"));
                });
            }
            catch (DurableEffectPreCheckpointException)
            {
                if (!_store.RollbackUncheckpointedDecision(pending))
                    throw new InvalidOperationException("PRE failed and pending authority could not roll back");
                return false;
            }
            decision = locked;
            return locked != null;
        }

        internal int RecoverPending()
        {
            int recovered = 0;
            foreach (var pending in _store.Canonical.Decisions
                .Where(x => x.Phase != SharedChoicePhase.ChoiceLocked).ToArray())
            { SharedChoiceDecision ignored; if (Recover(pending, out ignored)) recovered++; }
            return recovered;
        }

        internal bool ResumeCheckpointed(SharedChoiceDecision pending,
            DurableEffectTransactionCoordinator coordinator, Action executeNativeOnce,
            Action<SharedChoiceDecision> broadcast, out SharedChoiceDecision locked)
        {
            locked = null;
            if (pending == null || pending.Phase != SharedChoicePhase.EffectPending) return false;
            SharedChoiceDecision completed = null;
            coordinator.Execute(pending.Occurrence, pending.EffectToken, executeNativeOnce, () =>
            {
                if (!PersistAppliedStep(pending.Occurrence, "campaign-checkpoint"))
                    throw new InvalidOperationException("could not resume campaign checkpoint receipt");
                var live = _store.Canonical.Decisions.Single(x => x.Occurrence.Equals(pending.Occurrence));
                if (!Advance(live, SharedChoicePhase.EffectApplied)) throw new InvalidOperationException("resume EffectApplied failed");
                live = _store.Canonical.Decisions.Single(x => x.Occurrence.Equals(pending.Occurrence));
                if (!Advance(live, SharedChoicePhase.ChoiceLocked)) throw new InvalidOperationException("resume ChoiceLocked failed");
                completed = _store.Canonical.Decisions.Single(x => x.Occurrence.Equals(pending.Occurrence));
            }, _ => { _repaint(pending.Occurrence); broadcast(completed); });
            locked = completed; return completed != null;
        }

        private bool Recover(SharedChoiceDecision known, out SharedChoiceDecision decision)
        {
            SharedChoiceDecision local = null;
            bool result = _store.WithEffectGate(known.Occurrence, () => RecoverLocked(known, out local));
            decision = local; return result;
        }

        private bool RecoverLocked(SharedChoiceDecision known, out SharedChoiceDecision decision)
        {
            decision = null;
            for (int attempt = 0; attempt < 64; attempt++)
            {
                var current = _store.Canonical.Decisions.SingleOrDefault(x => x.Occurrence.Equals(known.Occurrence));
                if (current == null || !SameAnswer(current, known)) return false;
                if (current.Phase == SharedChoicePhase.ChoiceLocked) { decision = current; return true; }
                if (current.Phase == SharedChoicePhase.EffectPending)
                {
                    foreach (var step in current.EffectSteps.Where(x => x.State == DurableEffectStepState.Prepared).ToArray())
                    {
                        DurableEffectObservation observation;
                        try { observation = _effect.Observe(current, step); } catch { return false; }
                        if (observation == DurableEffectObservation.Diverged) return false;
                        if (observation == DurableEffectObservation.Before)
                        {
                            try { _effect.Apply(current, step); } catch { return false; }
                            Probe(SharedChoiceCrashPoint.NativeEffectReturned);
                            try { observation = _effect.Observe(current, step); } catch { return false; }
                            if (observation != DurableEffectObservation.After) return false;
                        }
                        if (!PersistAppliedStep(current.Occurrence, step.Key)) return false;
                        current = _store.Canonical.Decisions.Single(x => x.Occurrence.Equals(current.Occurrence));
                    }
                    if (!Advance(current, SharedChoicePhase.EffectApplied)) continue;
                    Probe(SharedChoiceCrashPoint.EffectAppliedCommitted);
                    continue;
                }
                if (current.Phase == SharedChoicePhase.EffectApplied)
                {
                    if (!Advance(current, SharedChoicePhase.ChoiceLocked)) continue;
                    var locked = _store.Canonical.Decisions.Single(x => x.Occurrence.Equals(current.Occurrence));
                    _repaint(locked.Occurrence); // queued/suspended state was committed with pending first
                    Probe(SharedChoiceCrashPoint.ChoiceLockedBeforeResponse);
                    decision = locked; return true;
                }
            }
            return false;
        }

        private bool Advance(SharedChoiceDecision expected, SharedChoicePhase phase)
        {
            var ledger = _store.Ledger; var canonical = _store.Canonical;
            var live = canonical.Decisions.SingleOrDefault(x => x.Occurrence.Equals(expected.Occurrence));
            if (live == null || live.Phase != expected.Phase || !SameAnswer(live, expected)) return false;
            var next = ledger.WithAuthority(checked(ledger.CommittedRevision + 1), ledger.Members);
            var advanced = phase == SharedChoicePhase.ChoiceLocked
                ? live.WithPhase(phase, next.CommittedRevision) : live.WithPhase(phase);
            return _store.CommitWithCanonical(ledger, next, canonical.WithDecision(advanced));
        }

        private bool PersistAppliedStep(OccurrenceId occurrence, string key)
        {
            for (int attempt = 0; attempt < 32; attempt++)
            {
                var ledger = _store.Ledger; var canonical = _store.Canonical;
                var live = canonical.Decisions.Single(x => x.Occurrence.Equals(occurrence));
                var step = live.EffectSteps.Single(x => x.Key == key);
                if (step.State == DurableEffectStepState.Applied) return true;
                var next = ledger.WithAuthority(checked(ledger.CommittedRevision + 1), ledger.Members);
                if (_store.CommitWithCanonical(ledger, next, canonical.WithDecision(live.WithAppliedStep(key)))) return true;
            }
            return false;
        }

        private static bool SameAnswer(SharedChoiceDecision a, SharedChoiceDecision b) =>
            a.EffectToken.Equals(b.EffectToken) && a.Choice.Equals(b.Choice) && a.Result.Equals(b.Result) &&
            a.Rewards.SequenceEqual(b.Rewards) && a.Winner.Equals(b.Winner) && a.EffectSteps.Count == b.EffectSteps.Count &&
            a.EffectSteps.Zip(b.EffectSteps, (x, y) => x.Key == y.Key && x.Operation == y.Operation &&
                x.BeforeFact == y.BeforeFact && x.AfterFact == y.AfterFact).All(x => x);
        private void Probe(SharedChoiceCrashPoint point) { var probe = CrashProbe; if (probe != null) probe(point); }
    }
}
