namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// State channel #2 — Phoenix faction <c>Research</c> (fixes host cancel/switch not reaching the
    /// frozen client). Host snapshots completed research ids + the ordered (id, progress) queue + the
    /// Revealed/Unlocked Available-list state on the faction research start/complete events AND every
    /// in-game hour (progress heartbeat); client reconciles its research to match exactly (idempotent,
    /// version-gated). Mirrors <see cref="InventoryChannel"/>. The wire codec + <see cref="ResearchSnapshot"/>
    /// live in their own pure file for unit testability.
    ///
    /// FIX#1 (progress heartbeat): the faction start/complete events are DISCRETE — between them the client
    /// progress bar would freeze and only jump to 100% on completion. Host research accrues per in-game hour
    /// (<c>LevelHourlyUpdateCrt</c> → <c>UpdateResearch()</c>), so we ALSO re-mark ch2 dirty on
    /// <c>GeoLevelController.HourTicked</c>, re-sending the queue snapshot (which already carries progress)
    /// every hour. The client bar then tracks the host at its real granularity, and a cancel's last
    /// authoritative progress is never left stale.
    /// </summary>
    public sealed class ResearchChannel : IStateChannel
    {
        public byte ChannelId => 2;

        // ─── host subscription state (mirrors WalletWatcher / InventoryChannel) ───
        private object _token;       // opaque faction-event token (Start/Complete) from ResearchStateReflection
        private object _hourToken;   // opaque hourly-tick (HourTicked) token — FIX#1 progress heartbeat
        private object _research;    // the bound Research instance (rebind guard)
        // Host: FNV-1a signature of the last snapshot Snapshot() built. Snapshot is the SOLE payload builder
        // and runs only at broadcast time (SyncEngine.FlushChannel), so it equals what clients last received —
        // PollHostDrift compares the live signature to this and never re-fires what was just sent
        // (InventoryChannel._lastBroadcastSig idiom). Null = nothing broadcast yet.
        private ulong? _lastBroadcastSig;

        public byte[] Snapshot(GeoRuntime rt)
        {
            var snap = ResearchStateReflection.Snapshot(rt);
            if (snap == null) return null;
            var bytes = ResearchSnapshot.Encode(snap);
            _lastBroadcastSig = ResearchSnapshot.Fnv1a(bytes);   // poll baseline = exactly what we send
            return bytes;
        }

        /// <summary>
        /// Host poll backstop (throttled by <see cref="SyncEngine.Tick"/>): hash the live research snapshot and
        /// mark ch#2 dirty when it drifts from the last broadcast — catching ANY mutation path that never fires
        /// the faction start/complete events (direct field writes, exotic mods), current and future. The known
        /// paths (the 5 ResearchAcquisitionDirtyPatches + faction events + hourly heartbeat) still converge
        /// instantly; this only shrinks the worst-case UNKNOWN-path lag from ~1 in-game hour to the poll cadence.
        /// Marks only — the per-channel flush stays the sole sender. No-op off-geoscape (null snapshot). We hash
        /// the ENCODED snapshot (not hand-picked fields): it is the exact wire truth, so any mutation the client
        /// must mirror changes it, and any mutation that doesn't isn't in the snapshot anyway (nothing to sync).
        /// </summary>
        public void PollHostDrift(GeoRuntime rt, SyncEngine eng)
        {
            if (eng == null) return;
            var snap = ResearchStateReflection.Snapshot(rt);
            if (snap == null) return;                              // not in geoscape yet / mid-load
            ulong sig = ResearchSnapshot.Fnv1a(ResearchSnapshot.Encode(snap));
            if (_lastBroadcastSig.HasValue && sig == _lastBroadcastSig.Value) return;  // clients already hold this
            eng.MarkChannelDirty(ChannelId);
        }

        public void Apply(GeoRuntime rt, byte[] data)
        {
            var snap = ResearchSnapshot.Decode(data);
            if (snap == null) return;
            // RATE sync: record the host's effective hourly research rate BEFORE the reconcile + the
            // research-screen refresh that follows in OnStateSync, so the rebuilt ETA rows already read
            // the synced value via the ClientResearchRatePatch postfix. Apply only runs on the client
            // (OnStateSync is host-guarded), so this never touches the host's authoritative rate.
            ClientResearchRate.OnSnapshotApplied(snap.HourlyRate);
            ResearchStateReflection.Apply(rt, snap);
        }

        public void AttachHost(SyncEngine eng)
        {
            // NO hard "already bound" gate — rebind when the LIVE Research instance changes (geoscape reload
            // builds a fresh one; the old `if (_bound) return;` left this channel on the dead instance forever —
            // research sync silently stopped after a tactical round-trip. WalletWatcher lesson, WalletWatcher.cs:20-28).
            if (eng == null) return;
            var research = ResearchStateReflection.GetResearch(GeoRuntime.Instance);
            if (research == null) return;                        // not in geoscape yet / mid-load
            if (ReferenceEquals(research, _research)) return;    // already bound to this instance

            DetachHost();                                        // drop any stale binding
            _research = research;
            byte id = ChannelId;
            _token = ResearchStateReflection.SubscribeFactionResearchEvents(
                GeoRuntime.Instance, () => NetworkEngine.Instance?.Sync?.MarkChannelDirty(id));
            // FIX#1: hourly progress heartbeat — re-snapshot the queue (with progress) every in-game hour so
            // the client bar tracks the host between the discrete start/complete events. Same dirty-mark sink.
            _hourToken = ResearchStateReflection.SubscribeHourlyTick(
                GeoRuntime.Instance, () => NetworkEngine.Instance?.Sync?.MarkChannelDirty(id));
            // Seed clients with the authoritative research the moment we bind.
            eng.MarkChannelDirty(id);
        }

        public void DetachHost()
        {
            if (_token != null) ResearchStateReflection.Unsubscribe(_token);
            if (_hourToken != null) ResearchStateReflection.Unsubscribe(_hourToken);
            _token = null;
            _hourToken = null;
            _research = null;
        }
    }
}
