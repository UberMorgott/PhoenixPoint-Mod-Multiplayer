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
}
