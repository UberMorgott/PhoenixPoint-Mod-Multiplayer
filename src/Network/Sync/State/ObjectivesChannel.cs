namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// State channel #7 — faction OBJECTIVES + event-system VARIABLES (spec §P7: TFTV quest lines /
    /// DLC5 / critical path ride <c>GeoFaction.Objectives</c> + <c>GeoscapeEventSystem</c> custom
    /// variables, and neither was mirrored — the client's objectives panel and TFTV quest state froze
    /// at the save-transfer baseline). Host snapshots the player faction's carried objectives as pure
    /// value records + the full variable table; the client reconciles through the NATIVE
    /// <c>AddObjective</c>/<c>RemoveObjective</c> (their <c>ObjectivesChanged</c> raise repaints the
    /// panel natively — zero new UI) and value-stamps the variable dict (no <c>VariableSet</c>
    /// cascade). Codec + <see cref="ObjectivesSnapshot"/> live in their own pure file for unit
    /// testability; <see cref="ObjectivesReflection"/> is the bridge.
    ///
    /// Dirty triggers: the faction's <c>ObjectivesChanged</c> + <c>ObjectiveCompleted</c> (add /
    /// remove / update / complete funnel) PLUS <c>GeoscapeEventSystem.VariableSet</c> (every
    /// <c>SetVariable</c> — the TFTV quest-step writer) PLUS the hourly tick heartbeat (re-converges
    /// anything a private write skipped — e.g. an event objective's <c>_completed</c> flip, which
    /// raises no faction event). All triggers only MARK dirty; the engine coalesces and snapshots the
    /// WHOLE set once per flush tick, so a TFTV quest step writing several variables + an objective in
    /// one call stack lands in ONE atomic snapshot (spec §BATCH-4 tear risk).
    /// </summary>
    public sealed class ObjectivesChannel : IStateChannel
    {
        public byte ChannelId => 7;

        private object _objToken;   // opaque faction ObjectivesChanged/ObjectiveCompleted token
        private object _varToken;   // opaque GeoscapeEventSystem.VariableSet token
        private object _hourToken;  // opaque hourly-tick token (objectives/variables heartbeat)
        private object _faction;    // bound faction instance (rebind guard)
        // Host: FNV-1a of the last snapshot Snapshot() broadcast (poll baseline; ResearchChannel idiom) —
        // Snapshot is the sole payload builder + runs only at flush, so it equals what clients last received.
        private ulong? _lastBroadcastSig;

        public byte[] Snapshot(GeoRuntime rt)
        {
            var snap = ObjectivesReflection.Snapshot(rt);
            if (snap == null) return null;
            var bytes = ObjectivesSnapshot.Encode(snap);
            _lastBroadcastSig = ResearchSnapshot.Fnv1a(bytes);   // poll baseline = exactly what we send
            return bytes;
        }

        /// <summary>
        /// Host poll backstop (throttled by <see cref="SyncEngine.Tick"/>): hash the live objectives+variables
        /// snapshot and mark ch#7 dirty when it drifts from the last broadcast — catching any mutation that
        /// bypasses the objective/VariableSet events + the hourly heartbeat (other mods, future game patches).
        /// Marks only — the per-channel flush stays the sole sender. No-op off-geoscape (null snapshot).
        /// </summary>
        public void PollHostDrift(GeoRuntime rt, SyncEngine eng)
        {
            if (eng == null) return;
            var snap = ObjectivesReflection.Snapshot(rt);
            if (snap == null) return;                          // not in geoscape yet / mid-load
            ulong sig = ResearchSnapshot.Fnv1a(ObjectivesSnapshot.Encode(snap));
            if (_lastBroadcastSig.HasValue && sig == _lastBroadcastSig.Value) return;  // clients already hold this
            eng.MarkChannelDirty(ChannelId);
        }

        public void Apply(GeoRuntime rt, byte[] data)
        {
            var snap = ObjectivesSnapshot.Decode(data);
            if (snap == null) return;
            ObjectivesReflection.Apply(rt, snap);
        }

        public void AttachHost(SyncEngine eng)
        {
            // Rebind when the LIVE faction instance changes (geoscape reload builds a fresh
            // GeoPhoenixFaction with no mid-session Detach) — the WalletWatcher lesson applied to every
            // event channel (see DiplomacyChannel.AttachHost). The event system reloads with the same
            // level instance, so the faction guard covers the VariableSet binding too.
            if (eng == null) return;
            var fac = GeoRuntime.Instance.PhoenixFaction();
            if (fac == null) return;                          // not in geoscape yet / mid-load
            if (ReferenceEquals(fac, _faction)) return;       // already bound to this instance

            DetachHost();
            _faction = fac;
            byte id = ChannelId;
            _objToken = ObjectivesReflection.SubscribeObjectiveEvents(
                GeoRuntime.Instance, () => NetworkEngine.Instance?.Sync?.MarkChannelDirty(id));
            _varToken = ObjectivesReflection.SubscribeVariableSet(
                GeoRuntime.Instance, () => NetworkEngine.Instance?.Sync?.MarkChannelDirty(id));
            _hourToken = ResearchStateReflection.SubscribeHourlyTick(
                GeoRuntime.Instance, () => NetworkEngine.Instance?.Sync?.MarkChannelDirty(id));
            // Seed clients with the authoritative objective/variable state the moment we bind.
            eng.MarkChannelDirty(id);
        }

        public void DetachHost()
        {
            if (_objToken != null) ObjectivesReflection.Unsubscribe(_objToken);
            if (_varToken != null) ObjectivesReflection.Unsubscribe(_varToken);
            if (_hourToken != null) ResearchStateReflection.Unsubscribe(_hourToken);
            _objToken = null;
            _varToken = null;
            _hourToken = null;
            _faction = null;
        }
    }
}
