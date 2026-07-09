using System;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// State channel #9 — Phoenix roster mirror (2026-07-05 personnel-sync spec, PS1 + PS2).
    /// The client geoscape sim is frozen, so after join its roster never changes on its own:
    /// • PS1 (membership): a host base↔craft assignment, hire, dismissal or transfer leaves the client's
    ///   <c>_tacUnits</c> stale forever (audit §2). The host re-snapshots the FULL per-site membership
    ///   (ordered <c>GeoUnitId</c>s) whenever any membership seam fires; the client reconciles each
    ///   container VALUE-ONLY via <see cref="RosterReconcile"/> — never native AddCharacter/RemoveCharacter.
    ///   Vehicle CREW rides the #6 crew tail (one writer per field: #9 = site membership only).
    /// • PS2 (live-state): per CHANGED soldier the flush appends a whole-<c>GeoCharacter</c> game-Serializer
    ///   blob record (HP/stamina/fatigue/progression/equipment/augment/corruption/bodypart-HP — the
    ///   self-contained [SerializeType(Version=6)] snapshot unit). Host: dirty seams
    ///   (<see cref="MarkSoldierStateDirtyExternal"/>) + the hourly bulk mark
    ///   (<see cref="MarkAllSoldiersStateDirtyExternal"/>) drain through the pure
    ///   <see cref="PersonnelStateFlush"/> — FNV-1a-64 blob-hash skip (unchanged soldiers ship zero bytes,
    ///   R3) + a per-flush byte budget (EncodeStateSync u16 cap; overflow re-queues and drains next tick
    ///   via the AttachHost re-mark — the MistChannel pump pattern). Client: decode via
    ///   <see cref="PersonnelBlob"/> (game Serializer + own-Timing pump, R2) and value-copy onto the
    ///   existing instance via <see cref="PersonnelReflection.ApplySoldierState"/> (R1 fallback path —
    ///   RecreateFromAnotherCharacter is a new-instance factory, not an in-place copy). Failures degrade
    ///   to notify: log + keep the previous soldier state, never throw.
    ///
    /// Dirty triggers: PS1 membership seams (container Add/RemoveCharacter, PersonnelMembershipPatches)
    /// and PS2 state seams (ApllyTacticalResult/SetItems/Heal/SetInjured/DamageBodyPart/AddProgression +
    /// the BaseHourlyUpdate bulk driver, PersonnelStatePatches). Marks coalesce per SyncEngine tick.
    /// </summary>
    public sealed class PersonnelChannel : IStateChannel
    {
        public byte ChannelId => SurfaceIds.PersonnelChannel; // 9

        // The host-attached live instance (set in AttachHost — host branch of SyncEngine.Tick only —
        // cleared in DetachHost), so the static Harmony membership hooks can mark dirty without
        // threading the channel instance through the patches (the GeoSiteChannel._live pattern).
        private static PersonnelChannel _live;

        // GeoUnitIds changed since the last flush (0 = seed/unresolved sentinel — the flush is full-set
        // regardless; the set is the coalesced dirty TRIGGER + diagnostic). Own lock: hooks fire on the
        // host sim, Snapshot drains (swap-then-clear).
        private readonly HashSet<long> _dirty = new HashSet<long>();
        // PS2: soldiers whose live-state blob must (re-)emit — per-id records, unlike the full-set
        // membership block. Budget-deferred ids return here after the flush (drained next tick).
        private readonly HashSet<long> _stateDirty = new HashSet<long>();
        // PS2: hourly heal/stamina/train sweep — ONE bulk flag instead of enumerating the roster inside
        // the Harmony seam; expanded to all live ids at flush time, coalescing any number of hourly
        // marks in a tick into one flush (spec §3).
        private bool _stateBulk;
        private readonly object _dirtyLock = new object();

        // Host-only: per-soldier hash of the last EMITTED blob (FNV-1a 64) — the hourly whole-roster
        // sweep re-blobs dirty soldiers but only CHANGED bytes ship (R3). Deliberately NOT seeded at
        // AttachHost: the join blob already carries the whole roster, so pre-existing soldiers emit
        // nothing until a state seam actually fires; the first post-seam emit doubles as drift heal
        // (and seeding would cost a full-roster serialize hitch on every rebind boundary).
        private readonly Dictionary<long, ulong> _lastSentBlobHash = new Dictionary<long, ulong>();

        // PS2 faction-SP tail: the last shared GeoPhoenixFaction.Skillpoints value we shipped. The pool rides
        // EVERY #9 flush (value-only, last-writer-wins) but only when it CHANGED since this — an SP spend
        // (TrySpendSkillPoints, alongside ModifyBaseStat/AddAbility MarkAll) and a mission/hourly SP grant
        // (ApllyTacticalResult / BaseHourlyUpdate bulk) both already mark this channel dirty, so the current
        // value converges on the same flush the client already applies (no new rail, no new dirty hook). Null =
        // nothing shipped yet (first flush after (re)bind ships the current value). Host-only; cleared on Detach.
        private int? _lastSentFactionSp;

        // Per-flush byte budget for PS2 state records: EncodeStateSync's u16 len (65535) is the hard
        // wire cap; 24 KB mirrors the MistChannel-proven per-message bound (MistChannel.ChunkBytes).
        private const int StateBytesPerFlush = 24 * 1024;

        // CLIENT: unit ids whose soldier STATE the most recent Apply actually stamped (ApplySoldierState
        // successes + newcomers materialized from their blob). Read by SyncEngine.OnStateSync right after
        // the apply to scope the augment-screen mirror repaint to genuine same-character stamps (preview
        // regression RCA 2026-07-09) — membership-only / SP-only applies leave it empty.
        private readonly List<long> _lastStateApplyIds = new List<long>();
        public IReadOnlyList<long> LastStateApplyUnitIds => _lastStateApplyIds;

        private object _faction;   // bound GeoPhoenixFaction instance (rebind-by-instance guard)

        /// <summary>Out-of-band host dirty-mark (PS1 membership Harmony seams): mark
        /// <paramref name="geoUnitId"/> dirty on the live host-attached channel. No-op when not
        /// host-attached (client / no session) — the ONLY setter of <see cref="_live"/> is AttachHost.</summary>
        public static void MarkSoldierDirtyExternal(long geoUnitId)
        {
            var ch = _live;
            if (ch == null) return;
            lock (ch._dirtyLock) { ch._dirty.Add(geoUnitId); }
            NetworkEngine.Instance?.Sync?.MarkChannelDirty(SurfaceIds.PersonnelChannel);
        }

        /// <summary>Out-of-band host dirty-mark (PS2 state Harmony seams): this soldier's live-state blob
        /// must (re-)emit. No-op when not host-attached; id 0 (read miss) is useless for a per-id record
        /// and is dropped here (the flush core drops it too).</summary>
        public static void MarkSoldierStateDirtyExternal(long geoUnitId)
        {
            var ch = _live;
            if (ch == null || geoUnitId == 0) return;
            lock (ch._dirtyLock) { ch._stateDirty.Add(geoUnitId); }
            NetworkEngine.Instance?.Sync?.MarkChannelDirty(SurfaceIds.PersonnelChannel);
        }

        /// <summary>Out-of-band host bulk mark (hourly heal/stamina/train driver): every live Phoenix
        /// soldier becomes state-dirty at the next flush. Many marks per tick coalesce into ONE flush;
        /// the blob-hash skip then culls the unchanged majority (R3).</summary>
        public static void MarkAllSoldiersStateDirtyExternal()
        {
            var ch = _live;
            if (ch == null) return;
            lock (ch._dirtyLock) { ch._stateBulk = true; }
            NetworkEngine.Instance?.Sync?.MarkChannelDirty(SurfaceIds.PersonnelChannel);
        }

        public byte[] Snapshot(GeoRuntime rt)
        {
            int membershipDirty;
            bool bulk;
            List<long> stateIds;
            lock (_dirtyLock)
            {
                if (_dirty.Count == 0 && _stateDirty.Count == 0 && !_stateBulk) return null;
                membershipDirty = _dirty.Count;
                _dirty.Clear();
                bulk = _stateBulk;
                _stateBulk = false;
                stateIds = new List<long>(_stateDirty);
                _stateDirty.Clear();
            }

            var snap = new PersonnelSnapshot();
            if (membershipDirty > 0)
            {
                var rosters = PersonnelReflection.SnapshotSiteRosters(rt);
                if (rosters != null) snap.Sites.AddRange(rosters);
                if (snap.Sites.Count > 0)
                    Debug.Log("[Multiplayer] PersonnelChannel flush sites=" + snap.Sites.Count + " dirtyUnits=" + membershipDirty);
            }

            // PS2 state records — guarded so a state-side failure can never kill the membership flush.
            try
            {
                if (stateIds.Count > 0 || bulk)
                {
                    var index = PersonnelReflection.BuildCharacterIndex(rt);
                    if (bulk)
                    {
                        var seen = new HashSet<long>(stateIds);
                        foreach (var id in index.ById.Keys)
                            if (seen.Add(id)) stateIds.Add(id);
                    }
                    var flush = PersonnelStateFlush.Run(stateIds,
                        id => index.ById.TryGetValue(id, out var soldier) ? PersonnelBlob.Write(soldier) : null,
                        _lastSentBlobHash, StateBytesPerFlush);
                    snap.States.AddRange(flush.Emit);
                    if (flush.Deferred.Count > 0)
                        lock (_dirtyLock) { foreach (var d in flush.Deferred) _stateDirty.Add(d); }
                    if (flush.Emit.Count > 0 || flush.Deferred.Count > 0 || flush.Failed > 0 || flush.Oversized > 0)
                        Debug.Log("[Multiplayer] PersonnelChannel state flush emit=" + flush.Emit.Count
                                  + " skipUnchanged=" + flush.SkippedUnchanged + " deferred=" + flush.Deferred.Count
                                  + " failed=" + flush.Failed + " oversized=" + flush.Oversized + (bulk ? " (bulk)" : ""));
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelChannel state flush failed: " + ex.Message); }

            // PS2 faction-SP tail: ship the shared pool value only when it CHANGED since the last emit
            // (value-only mirror). This snapshot is only reached when the channel was already marked dirty
            // (soldier/membership seam OR the hourly bulk), so a standalone faction-SP grant ships within one
            // base-hour at worst and immediately alongside any progression/mission edit.
            int? curFacSp = PersonnelReflection.ReadFactionSkillpoints(rt);
            if (curFacSp.HasValue && (!_lastSentFactionSp.HasValue || _lastSentFactionSp.Value != curFacSp.Value))
                snap.FactionSkillpoints = curFacSp;

            if (snap.Sites.Count == 0 && snap.States.Count == 0 && !snap.FactionSkillpoints.HasValue)
                return null;   // mid-load / all skipped, nothing to ship → next mark re-arms
            if (snap.FactionSkillpoints.HasValue) _lastSentFactionSp = snap.FactionSkillpoints;
            return PersonnelSnapshot.Encode(snap);
        }

        public void Apply(GeoRuntime rt, byte[] data)
        {
            _lastStateApplyIds.Clear();   // cleared even on decode/no-op exits — never carries a stale stamp set
            var snap = PersonnelSnapshot.Decode(data);
            if (snap == null || (snap.Sites.Count == 0 && snap.States.Count == 0 && !snap.FactionSkillpoints.HasValue)) return;
            Debug.Log("[Multiplayer] PersonnelChannel apply sites=" + snap.Sites.Count + " states=" + snap.States.Count
                      + (snap.FactionSkillpoints.HasValue ? " factionSP=" + snap.FactionSkillpoints.Value : ""));
            // ONE index for the whole payload: every Phoenix soldier resolved across vehicles + sites.
            // RosterReconcile Contains-guards entries that a preceding record's move made stale, and the
            // host emits each soldier in at most one container per snapshot (single-writer truth).
            var index = PersonnelReflection.BuildCharacterIndex(rt);
            // Hire gap: a brand-new soldier (never on this client) is materialized from its PS2 blob into the
            // site the membership names BEFORE the reconcile — so the reconcile below finds + orders it instead
            // of skipping it as "not live". Its state is now applied (the decoded instance), so skip it below.
            var materialized = PersonnelReflection.MaterializeNewcomers(rt, snap.Sites, snap.States, index);
            _lastStateApplyIds.AddRange(materialized);   // a newcomer's state came from its blob → stamped
            foreach (var rec in snap.Sites)
                PersonnelReflection.ApplySiteRoster(rt, rec, index);
            // PS2 live-state AFTER membership (a just-transferred soldier is already in its mirrored
            // container; ById keys instances, so the reconcile above never invalidates it). Every failure
            // degrades to notify — log + keep the soldier's previous state, never throw into the engine.
            foreach (var st in snap.States)
            {
                if (materialized.Contains(st.UnitId)) continue;   // just materialized FROM this blob — already current
                if (!index.ById.TryGetValue(st.UnitId, out var existing))
                {
                    Debug.Log("[Multiplayer] PersonnelChannel: live-state for GeoUnitId " + st.UnitId
                              + " — soldier not live on this client, skipped");
                    continue;
                }
                var decoded = PersonnelBlob.Read(st.Blob);
                if (decoded == null)
                {
                    Debug.LogError("[Multiplayer] PersonnelChannel: blob decode failed for GeoUnitId " + st.UnitId
                                   + " — soldier keeps previous state");
                    continue;
                }
                long decodedId = PersonnelReflection.ReadUnitId(decoded);
                if (decodedId != 0 && decodedId != st.UnitId)
                {
                    Debug.LogError("[Multiplayer] PersonnelChannel: blob id mismatch (" + decodedId + " != "
                                   + st.UnitId + ") — record skipped");
                    continue;
                }
                if (PersonnelReflection.ApplySoldierState(existing, decoded))
                {
                    _lastStateApplyIds.Add(st.UnitId);
                    Debug.Log("[Multiplayer] PersonnelChannel: applied live-state for GeoUnitId " + st.UnitId);
                }
            }
            // PS2 faction-SP tail: value-only mirror of the shared GeoPhoenixFaction.Skillpoints pool (present
            // only when the host value changed). The open progression panel repaints on the same #9 apply via
            // the OnStateSync → RefreshNeedsKick → RefreshRosterEquip re-drive (a client with no uncommitted
            // local allocation), so the new pool total lands reactively.
            if (snap.FactionSkillpoints.HasValue)
            {
                PersonnelReflection.ApplyFactionSkillpoints(rt, snap.FactionSkillpoints.Value);
                Debug.Log("[Multiplayer] PersonnelChannel: applied shared faction SP pool = " + snap.FactionSkillpoints.Value);
            }
        }

        public void AttachHost(SyncEngine eng)
        {
            // Rebind when the LIVE faction instance changes (geoscape reload after a tactical round-trip /
            // co-op save-reload builds a fresh GeoPhoenixFaction with no mid-session Detach) — the
            // WalletWatcher lesson. No event subscription here: the dirty sources are the static Harmony
            // membership seams routed through _live, so rebinding = retargeting _live + re-seeding.
            if (eng == null) return;
            // PS2 drain pump: budget-deferred / bulk state marks must keep the channel dirty so the flush
            // loop (which runs after this AttachHost pass each host Tick) ships the next slice this very
            // tick — the MistChannel._sendQueue pattern.
            lock (_dirtyLock) { if (_stateDirty.Count > 0 || _stateBulk) eng.MarkChannelDirty(ChannelId); }
            var fac = GeoRuntime.Instance.PhoenixFaction();
            if (fac == null) return;                       // not in geoscape yet / mid-load
            if (ReferenceEquals(fac, _faction)) return;    // already bound to this instance

            _faction = fac;
            _live = this;
            // Seed a baseline flush on (re)bind: the join blob / reloaded save already matches, so the
            // client reconcile is an idempotent no-op — but a reload boundary that raced a membership
            // change re-converges here. Sentinel 0 arms the local dirty gate for the engine flush.
            // (State blobs deliberately NOT seeded — see _lastSentBlobHash.)
            lock (_dirtyLock) { _dirty.Add(0); }
            eng.MarkChannelDirty(ChannelId);
        }

        public void DetachHost()
        {
            _faction = null;
            if (_live == this) _live = null;
            lock (_dirtyLock) { _dirty.Clear(); _stateDirty.Clear(); _stateBulk = false; }
            _lastSentBlobHash.Clear();
            _lastSentFactionSp = null;   // re-seed the faction-SP tail on the next (re)bind
        }
    }
}
