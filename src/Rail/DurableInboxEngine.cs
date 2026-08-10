using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Multiplayer.Network.Sync
{
    internal readonly struct DurablePreparationEditContext
    {
        private const byte Marker = 0xD6;
        internal DurablePreparationEditContext(OccurrenceId occurrence, ulong expectedRevision)
        { Occurrence = occurrence; ExpectedRevision = expectedRevision; }
        internal OccurrenceId Occurrence { get; }
        internal ulong ExpectedRevision { get; }

        internal static void Write(BinaryWriter writer, DurablePreparationEditContext context)
        {
            writer.Write(Marker); WriteString(writer, context.Occurrence.EventId);
            WriteString(writer, context.Occurrence.TriggerId);
            if (context.Occurrence.SubjectIds.Count > 1024) throw new InvalidDataException("too many preparation subjects");
            writer.Write((ushort)context.Occurrence.SubjectIds.Count);
            foreach (var subject in context.Occurrence.SubjectIds) WriteString(writer, subject);
            writer.Write(context.ExpectedRevision);
        }

        internal static bool TryReadTrailing(BinaryReader reader, out DurablePreparationEditContext context)
        {
            context = default(DurablePreparationEditContext);
            if (reader.BaseStream.Position == reader.BaseStream.Length) return false;
            if (reader.ReadByte() != Marker) throw new InvalidDataException("invalid preparation context marker");
            string eventId = ReadString(reader), triggerId = ReadString(reader);
            int count = reader.ReadUInt16(); if (count <= 0 || count > 1024) throw new InvalidDataException("invalid preparation subject count");
            var subjects = new string[count]; for (int i = 0; i < count; i++) subjects[i] = ReadString(reader);
            context = new DurablePreparationEditContext(new OccurrenceId(eventId, triggerId, subjects), reader.ReadUInt64());
            if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("trailing preparation edit bytes");
            return true;
        }

        private static void WriteString(BinaryWriter writer, string value)
        { var bytes = new UTF8Encoding(false, true).GetBytes(InboxIdentity.Required(value, nameof(value)));
          if (bytes.Length > 4096) throw new InvalidDataException("preparation identity exceeds bound");
          writer.Write((ushort)bytes.Length); writer.Write(bytes); }
        private static string ReadString(BinaryReader reader)
        { int length = reader.ReadUInt16(); if (length <= 0 || length > 4096) throw new InvalidDataException("invalid preparation identity length");
          var bytes = reader.ReadBytes(length); if (bytes.Length != length) throw new EndOfStreamException();
          return new UTF8Encoding(false, true).GetString(bytes); }
    }

    /// <summary>Host-serialized material revision layered over the existing equipment/personnel command
    /// funnels.  It validates before native apply and advances every entitled copy in one ledger commit;
    /// it never introduces a second edit opcode or a readiness gate.</summary>
    internal sealed class DurablePreparationEditEngine
    {
        private readonly DurableInboxStore _store;
        private readonly Action<PreparationEditDelta> _repaint;
        internal DurablePreparationEditEngine(DurableInboxStore store, Action<PreparationEditDelta> repaint)
        { _store = store ?? throw new ArgumentNullException(nameof(store)); _repaint = repaint ?? throw new ArgumentNullException(nameof(repaint)); }

        internal bool TryApply(DurablePreparationEditContext context, Func<bool> validateAuthority,
            Func<bool> applyNative, Action rollbackNative, IEnumerable<string> touchedStableIdentities,
            out string refusal)
        {
            PreparationEditDelta delta = null; string local = null;
            bool result = _store.WithTransitionGate(context.Occurrence, () => _store.CommitPreparationNative(
                context.Occurrence, context.ExpectedRevision, validateAuthority, applyNative, rollbackNative,
                touchedStableIdentities, out delta, out local));
            if (result) _repaint(delta);
            refusal = local; return result;
        }
    }

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
        LevelTeardown,
        SourceInvalidated
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

        internal bool CancelMissionOfferLocal(OccurrenceId occurrence)
        {
            if (!DurableWindowRegistry.IsMissionOfferOccurrence(occurrence)) return false;
            for (int attempt = 0; attempt < 32; attempt++)
            {
                var expected = _store.Ledger; InboxEntry entry;
                try { entry = expected.Get(occurrence, _member); } catch { return false; }
                if (entry.Lifecycle == InboxLifecycle.Dismissed) return true;
                if (entry.Lifecycle == InboxLifecycle.Removed) return false;
                var dismissed = entry.WithLifecycle(InboxLifecycle.Dismissed,
                    checked(entry.LifecycleRevision + 1));
                var next = expected.Replace(dismissed).WithAuthority(
                    checked(expected.CommittedRevision + 1), expected.Members);
                if (_store.Commit(expected, next)) return true;
            }
            return false;
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

        /// <summary>Commits this peer's nonterminal Back before native navigation is allowed to run.</summary>
        internal bool TryDeferPreparation(OccurrenceId occurrence)
        {
            if (!string.Equals(occurrence.EventId, "DeploymentPreparing", StringComparison.Ordinal)) return false;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                var expected = _store.Ledger; InboxEntry current;
                try { current = expected.Get(occurrence, _member); } catch (InvalidOperationException) { return false; }
                if (current.Lifecycle == InboxLifecycle.Deferred) return true;
                if (current.Lifecycle != InboxLifecycle.Open) return false;
                var deferred = current.Defer(checked(current.LifecycleRevision + 1));
                var next = expected.Replace(deferred)
                    .WithAuthority(checked(expected.CommittedRevision + 1), expected.Members);
                try { if (_store.Commit(expected, next)) return true; } catch { return false; }
            }
            return false;
        }

        /// <summary>Explicit local re-entry is the only gesture, apart from a newer material revision,
        /// that makes a deferred preparation eligible for the display gate again.</summary>
        internal bool TryReenterDeferredPreparation(OccurrenceId occurrence)
        {
            if (!string.Equals(occurrence.EventId, "DeploymentPreparing", StringComparison.Ordinal)) return false;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                var expected = _store.Ledger; InboxEntry current;
                try { current = expected.Get(occurrence, _member); } catch (InvalidOperationException) { return false; }
                if (current.Lifecycle != InboxLifecycle.Deferred) return false;
                var queued = current.WithLifecycle(InboxLifecycle.Queued, checked(current.LifecycleRevision + 1));
                var next = expected.Replace(queued)
                    .WithAuthority(checked(expected.CommittedRevision + 1), expected.Members);
                try { if (_store.Commit(expected, next)) return true; } catch { return false; }
            }
            return false;
        }

        internal bool TryRestoreDeferredAfterFailedBack(OccurrenceId occurrence, ulong deferredLifecycleRevision)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                var expected = _store.Ledger; InboxEntry current;
                try { current = expected.Get(occurrence, _member); } catch (InvalidOperationException) { return false; }
                if (current.Lifecycle != InboxLifecycle.Deferred ||
                    current.LifecycleRevision != deferredLifecycleRevision) return false;
                var open = current.RestoreDeferred(checked(current.LifecycleRevision + 1));
                var next = expected.Replace(open)
                    .WithAuthority(checked(expected.CommittedRevision + 1), expected.Members);
                try { if (_store.Commit(expected, next)) return true; } catch { return false; }
            }
            return false;
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
                    terminalReason: TerminalReason.Invalidated, predecessor: current.Predecessor);
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

    /// <summary>The authoritative live deployment containers at one revalidation boundary.  Occupants are
    /// retained with their source so removal of an aircraft cannot leave its soldiers addressable through
    /// a queued/suspended/deferred preparation copy.</summary>
    internal sealed class DeploymentSourceSnapshot
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _sources;
        internal DeploymentSourceSnapshot(IDictionary<string, IEnumerable<string>> sources)
        {
            if (sources == null) throw new ArgumentNullException(nameof(sources));
            var copy = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var pair in sources)
            {
                var key = InboxIdentity.Required(pair.Key, nameof(sources));
                var occupants = (pair.Value ?? Enumerable.Empty<string>())
                    .Select(x => InboxIdentity.Required(x, nameof(sources))).Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal).ToArray();
                copy.Add(key, occupants);
            }
            _sources = copy;
        }
        internal int SourceCount => _sources.Count;
        internal int OccupantCount => _sources.Values.Sum(x => x.Count);
        internal bool ContainsSource(string source) => source != null && _sources.ContainsKey(source);
        internal IReadOnlyDictionary<string, IReadOnlyList<string>> Sources => _sources;
    }

    /// <summary>One reducer adapter for the host's post-StartTravel edge and the client's post-rail edge.
    /// It never invents mission cancellation: partial loss versions all copies and repaints each open
    /// entitlement; terminal loss commits one Invalidated tombstone and only then enters Task 7 teardown.</summary>
    internal sealed class DurableSourceRevalidationEngine
    {
        private sealed class NoCarrier : IDurableWindowCarrierAdapter
        {
            public InboxWindowCheckpoint Capture(OccurrenceId occurrence) => null;
            public bool Present(OccurrenceId occurrence) => false;
            public bool Restore(OccurrenceId occurrence, InboxWindowCheckpoint checkpoint) => false;
            public void Abandon(OccurrenceId occurrence) { }
            public void FinalizeRestore(OccurrenceId occurrence) { }
        }
        private static readonly IDurableWindowCarrierAdapter NullCarrier = new NoCarrier();
        private readonly DurableInboxStore _store;
        private readonly Action<OccurrenceId, MembershipId> _repaintOpen;
        internal DurableSourceRevalidationEngine(DurableInboxStore store,
            Action<OccurrenceId, MembershipId> repaintOpen)
        { _store = store ?? throw new ArgumentNullException(nameof(store));
          _repaintOpen = repaintOpen ?? throw new ArgumentNullException(nameof(repaintOpen)); }

        internal bool TryRevalidate(OccurrenceId occurrence, string departedSource,
            DeploymentSourceSnapshot before, DeploymentSourceSnapshot after, bool uniquelyBound,
            out ulong terminalRevision, out string refusal)
        {
            SourceRevalidationBatchDelta delta;
            var plan = new SourceRevalidationPlan(occurrence, departedSource, before, after, uniquelyBound);
            bool committed = TryRevalidateBatch(new[] { plan }, out delta, out refusal);
            terminalRevision = committed && delta.Items[0].Terminal ? delta.AuthorityRevision : 0;
            return committed;
        }

        internal bool TryRevalidateBatch(IEnumerable<SourceRevalidationPlan> sourcePlans,
            out SourceRevalidationBatchDelta delta, out string refusal)
        {
            delta = null; refusal = null;
            var plans = (sourcePlans ?? Enumerable.Empty<SourceRevalidationPlan>()).ToArray();
            if (plans.Length == 0 || plans.Length > 1024 || plans.Select(x => x.Occurrence).Distinct().Count() != plans.Length)
            { refusal = "source revalidation batch is empty, duplicate, or over bound"; return false; }
            foreach (var plan in plans)
            {
                if (plan.Before == null || plan.After == null || string.IsNullOrEmpty(plan.DepartedSource) ||
                    !plan.Before.ContainsSource(plan.DepartedSource)) return false;
                if (plan.After.ContainsSource(plan.DepartedSource))
                { refusal = "departed source is still present after the host write"; return false; }
                if (plan.After.Sources.Keys.Any(x => !plan.Before.ContainsSource(x)))
                { refusal = "source revalidation cannot add an unrelated deployment source"; return false; }
            }
            if (!_store.CommitSourceRevalidationBatch(plans, out delta, out refusal)) return false;
            foreach (var item in delta.Items.Where(x => !x.Terminal))
                foreach (var open in _store.Ledger.AllEntries.Where(x => x.Occurrence.Equals(item.Occurrence) &&
                    x.Lifecycle == InboxLifecycle.Open).ToArray()) _repaintOpen(item.Occurrence, open.Membership);
            RetryTerminalTeardown(_store, delta.Items.Where(x => x.Terminal).Select(x => x.Occurrence));
            return true; // authority remains committed even when a carrier needs a later retry
        }

        internal static int RetryTerminalTeardown(DurableInboxStore store, IEnumerable<OccurrenceId> occurrences = null)
        {
            if (store == null) return 0;
            var filter = occurrences == null ? null : new HashSet<OccurrenceId>(occurrences);
            int completed = 0;
            foreach (var group in store.Ledger.AllEntries.Where(x => x.Lifecycle == InboxLifecycle.Removed &&
                x.TerminalReason == TerminalReason.SourceInvalidated && (filter == null || filter.Contains(x.Occurrence)))
                .GroupBy(x => x.Occurrence).ToArray())
            {
                var first = group.First(); string why;
                if (new DurableInboxEngine(store, first.Membership, NullCarrier).RemoveAllCarriers(
                    group.Key, TerminalReason.SourceInvalidated, first.TombstoneRevision, out why)) completed++;
            }
            return completed;
        }
    }

    internal sealed class SourceRevalidationPlan
    {
        internal SourceRevalidationPlan(OccurrenceId occurrence, string departedSource,
            DeploymentSourceSnapshot before, DeploymentSourceSnapshot after, bool uniquelyBound,
            ulong departureWatermark = 1)
        { Occurrence = occurrence; DepartedSource = departedSource; Before = before; After = after;
          UniquelyBound = uniquelyBound; DepartureWatermark = departureWatermark; }
        internal OccurrenceId Occurrence { get; } internal string DepartedSource { get; }
        internal DeploymentSourceSnapshot Before { get; } internal DeploymentSourceSnapshot After { get; }
        internal bool UniquelyBound { get; }
        internal ulong DepartureWatermark { get; }
        internal bool Terminal => UniquelyBound || After.SourceCount == 0 || After.OccupantCount == 0;
    }

    internal sealed class SourceRevalidationItem
    {
        internal SourceRevalidationItem(OccurrenceId occurrence, bool terminal, ulong preparationRevision,
            ulong expectedAuthorityRevision)
        { Occurrence = occurrence; Terminal = terminal; PreparationRevision = preparationRevision;
          ExpectedAuthorityRevision = expectedAuthorityRevision; }
        internal OccurrenceId Occurrence { get; } internal bool Terminal { get; }
        internal ulong PreparationRevision { get; }
        internal ulong ExpectedAuthorityRevision { get; }
    }

    internal sealed class SourceRevalidationBatchDelta
    {
        internal SourceRevalidationBatchDelta(ulong authorityRevision, string departedSource, ulong departureWatermark,
            IEnumerable<SourceRevalidationItem> items)
        { AuthorityRevision = authorityRevision; DepartedSource = InboxIdentity.Required(departedSource, nameof(departedSource));
          DepartureWatermark = departureWatermark;
          Items = (items ?? throw new ArgumentNullException(nameof(items))).ToArray();
          if (authorityRevision == 0 || departureWatermark == 0 || Items.Count == 0 || Items.Count > 1024 ||
              Items.Select(x => x.Occurrence).Distinct().Count() != Items.Count) throw new ArgumentException("invalid source delta"); }
        internal ulong AuthorityRevision { get; } internal string DepartedSource { get; }
        internal ulong DepartureWatermark { get; }
        internal IReadOnlyList<SourceRevalidationItem> Items { get; }
    }
    internal enum SourceDeltaReadiness : byte { Ready, Future, Installed, Conflict }

    internal sealed class DeploymentTransitionDelta
    {
        internal DeploymentTransitionDelta(OccurrenceId offer, OccurrenceId preparation, HostOrderKey order,
            ulong tombstoneRevision, TerminalReason reason)
        { Offer = offer; Preparation = preparation; Order = order; TombstoneRevision = tombstoneRevision; Reason = reason; }
        internal OccurrenceId Offer { get; } internal OccurrenceId Preparation { get; }
        internal HostOrderKey Order { get; } internal ulong TombstoneRevision { get; } internal TerminalReason Reason { get; }
    }

    internal sealed class PreparationEditDelta
    {
        internal PreparationEditDelta(OccurrenceId occurrence, ulong preparationRevision,
            ulong authoritativeLedgerRevision, IEnumerable<string> touchedStableIdentities)
        {
            Occurrence = occurrence; PreparationRevision = preparationRevision;
            AuthoritativeLedgerRevision = authoritativeLedgerRevision;
            TouchedStableIdentities = (touchedStableIdentities ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        }
        internal OccurrenceId Occurrence { get; }
        internal ulong PreparationRevision { get; }
        internal ulong AuthoritativeLedgerRevision { get; }
        internal IReadOnlyList<string> TouchedStableIdentities { get; }
    }

    internal sealed class DurableMissionOfferEngine
    {
        private readonly DurableInboxStore _store;
        private readonly Action<DeploymentTransitionDelta> _terminalDelta;
        internal DurableMissionOfferEngine(DurableInboxStore store, Action<DeploymentTransitionDelta> terminalDelta)
        { _store = store ?? throw new ArgumentNullException(nameof(store));
          _terminalDelta = terminalDelta ?? throw new ArgumentNullException(nameof(terminalDelta)); }

        internal bool TryStart(OccurrenceId offer, OccurrenceId preparation,
            Func<HostLedger, bool> validate, out string refusal)
        {
            string local = null;
            bool result = _store.WithTransitionGate(offer, () =>
            {
                ulong terminalRevision;
                if (!_store.TryStartDeployment(offer, preparation, validate, out terminalRevision))
                {
                    var authoritative = _store.Ledger.AllEntries.FirstOrDefault(x => x.Predecessor.HasValue &&
                        x.Predecessor.Value.Equals(offer));
                    local = authoritative == null ? "deployment preparation could not be committed" :
                        "offer already transitioned to " + authoritative.Occurrence.EventId + "/" + authoritative.Occurrence.TriggerId;
                    return false;
                }
                var successor = _store.Ledger.AllEntries.First(x => x.Occurrence.Equals(preparation));
                if (!_store.HasTransitionDelta(offer))
                {
                    try { _terminalDelta(new DeploymentTransitionDelta(offer, preparation,
                        successor.HostOrderKey, terminalRevision, TerminalReason.Superseded)); }
                    catch { local = "authoritative terminal delta failed"; return false; }
                    _store.MarkTransitionDelta(offer);
                }
                if (_store.IsTransitionTeardownComplete(offer)) return true;
                string teardown;
                bool authorized = _store.AuthorizeCarrierRemoval(offer, TerminalReason.Superseded,
                    terminalRevision, out teardown);
                bool removed = authorized && _store.Carriers.RemoveAll(offer, TerminalReason.Superseded, out teardown);
                if (!removed) { local = teardown; return false; }
                _store.MarkTransitionTeardownComplete(offer); return true;
            });
            refusal = local; return result;
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
