using System;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// State channel #10 — off-roster recruit-pool mirror (2026-07-05 personnel-sync spec, PS3). The
    /// frozen client never refreshes its own pools, so after join every haven recruit / base naked
    /// recruit / captured Pandoran is stale forever. Three pools, one writer each (taxonomy §6):
    /// • HAVEN slots (<c>GeoHaven.AvailableRecruit</c>) — diffed PER SiteId: only havens whose slot
    ///   content changed ship (FNV-hash skip + per-flush byte budget with defer, the
    ///   <see cref="PersonnelStateFlush"/> discipline). TFTV-safe: TFTV's overlay reads this same
    ///   vanilla slot, and its refreshes drive the vanilla SpawnNewRecruit/RemoveRecruit our seams hook.
    /// • NAKED pool (<c>_nakedRecruits</c> descriptor→cost) and CAPTURED pool (<c>_capturedUnits</c>) —
    ///   FULL-SET blocks: when carried they hold the whole pool (client clear+refills — one delivered
    ///   flush heals any earlier drop); an UNCHANGED pool is hash-skipped and its block simply absent
    ///   (present-empty stays an honest clear — see <see cref="RecruitPoolSnapshot"/>).
    /// Membership is NOT handled here: hiring lands the soldier via the #9/#6 membership seams; this
    /// channel only mirrors the pool CONTENT (one writer per field).
    ///
    /// Dirty seams (<c>RecruitPoolPatches</c>): GeoHaven.SpawnNewRecruit/RemoveRecruit/KillRecruit
    /// (per-haven), RegenerateNakedRecruits/HireNakedRecruit (naked), CaptureUnit/KillCapturedUnit/
    /// TrimCapturedUnitsToCapacity (captured). Marks coalesce per SyncEngine tick; budget-deferred havens
    /// drain next tick via the AttachHost re-mark (the MistChannel pump pattern). Pool blobs are
    /// deliberately NOT seeded at (re)bind: the join save-blob already carries all pools
    /// (ExtendedInstanceData + haven InstanceData), so the first post-seam emit doubles as drift heal —
    /// the <see cref="PersonnelChannel"/> _lastSentBlobHash discipline.
    /// </summary>
    public sealed class RecruitPoolChannel : IStateChannel
    {
        public byte ChannelId => SurfaceIds.RecruitPoolChannel; // 10

        // Host-attached live instance (set in AttachHost, cleared in DetachHost) so the static Harmony
        // seams can mark dirty without threading the channel through the patches (GeoSiteChannel._live).
        private static RecruitPoolChannel _live;

        private readonly HashSet<int> _dirtyHavens = new HashSet<int>();   // SiteIds with a changed slot
        private bool _nakedDirty;
        private bool _capturedDirty;
        private readonly object _dirtyLock = new object();

        // Host-only change-detect: per-haven slot hash (flush core stamps on emit) + whole-block hashes
        // for the two full-set pools (null = never emitted this session — first dirty always ships).
        private readonly Dictionary<int, ulong> _lastHavenHash = new Dictionary<int, ulong>();
        private ulong? _lastNakedHash;
        private ulong? _lastCapturedHash;

        // Per-flush byte budget for haven records (the PersonnelChannel 24 KB bound) and the hard safety
        // ceiling for the WHOLE payload: EncodeStateSync's u16 len caps at 65535 — beyond it the length
        // WRAPS and corrupts the wire, so blocks that don't fit defer to the next tick instead.
        private const int HavenBytesPerFlush = 24 * 1024;
        private const int PayloadSafeBytes = 58000;

        private object _faction;   // bound GeoPhoenixFaction instance (rebind-by-instance guard)

        /// <summary>Out-of-band host dirty-mark (haven seams): this haven's slot must (re-)emit.
        /// No-op when not host-attached; siteId &lt; 0 (unresolvable haven) can't be keyed → dropped.</summary>
        public static void MarkHavenDirtyExternal(int siteId)
        {
            var ch = _live;
            if (ch == null || siteId < 0) return;
            lock (ch._dirtyLock) { ch._dirtyHavens.Add(siteId); }
            NetworkEngine.Instance?.Sync?.MarkChannelDirty(SurfaceIds.RecruitPoolChannel);
        }

        /// <summary>Out-of-band host dirty-mark (naked-pool seams): the base recruitment roster changed.</summary>
        public static void MarkNakedDirtyExternal()
        {
            var ch = _live;
            if (ch == null) return;
            lock (ch._dirtyLock) { ch._nakedDirty = true; }
            NetworkEngine.Instance?.Sync?.MarkChannelDirty(SurfaceIds.RecruitPoolChannel);
        }

        /// <summary>Out-of-band host dirty-mark (containment seams): the captured-unit pool changed.</summary>
        public static void MarkCapturedDirtyExternal()
        {
            var ch = _live;
            if (ch == null) return;
            lock (ch._dirtyLock) { ch._capturedDirty = true; }
            NetworkEngine.Instance?.Sync?.MarkChannelDirty(SurfaceIds.RecruitPoolChannel);
        }

        public byte[] Snapshot(GeoRuntime rt)
        {
            List<int> havenIds;
            bool naked, captured;
            lock (_dirtyLock)
            {
                if (_dirtyHavens.Count == 0 && !_nakedDirty && !_capturedDirty) return null;
                havenIds = new List<int>(_dirtyHavens);
                _dirtyHavens.Clear();
                naked = _nakedDirty; _nakedDirty = false;
                captured = _capturedDirty; _capturedDirty = false;
            }
            // Mid-load (no live faction): restore the drained marks untouched — pool marks are sparse
            // (unlike #9's full-set membership) and a lost one stays stale until the NEXT pool event,
            // which can be hours of game time away.
            if (rt?.PhoenixFaction() == null)
            {
                lock (_dirtyLock)
                {
                    foreach (var id in havenIds) _dirtyHavens.Add(id);
                    _nakedDirty |= naked;
                    _capturedDirty |= captured;
                }
                return null;
            }

            var snap = new RecruitPoolSnapshot();
            int deferredHavens = 0, skippedHavens = 0, failedHavens = 0;
            int budgetLeft = PayloadSafeBytes;

            try
            {
                // FULL-SET pools first (they cannot split): naked, then captured. A block whose content
                // hash equals the last emitted one is skipped WITHOUT stamping anything; a block that no
                // longer fits the payload ceiling re-marks itself and ships alone next tick (drain pump).
                if (naked)
                {
                    var records = RecruitPoolReflection.SnapshotNakedRecruits(rt);
                    if (records == null)
                        Debug.LogError("[Multiplayer] RecruitPoolChannel: naked pool snapshot failed — flush dropped (next seam re-arms)");
                    else
                    {
                        var block = RecruitPoolSnapshot.EncodeNakedBlock(records);
                        ulong h = PersonnelStateFlush.Hash(block);
                        if (h == _lastNakedHash) { /* unchanged — zero bytes */ }
                        else if (block.Length + 8 > budgetLeft)
                        {
                            lock (_dirtyLock) { _nakedDirty = true; }
                            Debug.Log("[Multiplayer] RecruitPoolChannel: naked block deferred (" + block.Length + " B over ceiling)");
                        }
                        else
                        {
                            snap.HasNaked = true;
                            snap.Naked.AddRange(records);
                            _lastNakedHash = h;
                            budgetLeft -= block.Length + 8;
                        }
                    }
                }
                if (captured)
                {
                    var blobs = RecruitPoolReflection.SnapshotCapturedUnits(rt);
                    if (blobs == null)
                        Debug.LogError("[Multiplayer] RecruitPoolChannel: containment snapshot failed — flush dropped (next seam re-arms)");
                    else
                    {
                        var block = RecruitPoolSnapshot.EncodeCapturedBlock(blobs);
                        ulong h = PersonnelStateFlush.Hash(block);
                        if (h == _lastCapturedHash) { /* unchanged — zero bytes */ }
                        else if (block.Length + 8 > budgetLeft)
                        {
                            lock (_dirtyLock) { _capturedDirty = true; }
                            Debug.Log("[Multiplayer] RecruitPoolChannel: captured block deferred (" + block.Length + " B over ceiling)");
                        }
                        else
                        {
                            snap.HasCaptured = true;
                            snap.Captured.AddRange(blobs);
                            _lastCapturedHash = h;
                            budgetLeft -= block.Length + 8;
                        }
                    }
                }
                if (havenIds.Count > 0)
                {
                    int havenBudget = Math.Min(HavenBytesPerFlush, budgetLeft);
                    var flush = RecruitPoolFlush.Run(havenIds,
                        id => RecruitPoolReflection.ReadHavenRecruit(rt, id),
                        _lastHavenHash, havenBudget);
                    snap.Havens.AddRange(flush.Emit);
                    if (flush.Deferred.Count > 0)
                        lock (_dirtyLock) { foreach (var d in flush.Deferred) _dirtyHavens.Add(d); }
                    deferredHavens = flush.Deferred.Count;
                    skippedHavens = flush.SkippedUnchanged;
                    failedHavens = flush.Failed;
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] RecruitPoolChannel flush failed: " + ex.Message); }

            if (snap.Havens.Count == 0 && !snap.HasNaked && !snap.HasCaptured) return null;   // all skipped/deferred
            Debug.Log("[Multiplayer] RecruitPoolChannel flush havens=" + snap.Havens.Count
                      + (skippedHavens > 0 ? " skipUnchanged=" + skippedHavens : "")
                      + (deferredHavens > 0 ? " deferred=" + deferredHavens : "")
                      + (failedHavens > 0 ? " failed=" + failedHavens : "")
                      + " naked=" + (snap.HasNaked ? snap.Naked.Count.ToString() : "-")
                      + " captured=" + (snap.HasCaptured ? snap.Captured.Count.ToString() : "-"));
            return RecruitPoolSnapshot.Encode(snap);
        }

        public void Apply(GeoRuntime rt, byte[] data)
        {
            var snap = RecruitPoolSnapshot.Decode(data);
            if (snap == null || (snap.Havens.Count == 0 && !snap.HasNaked && !snap.HasCaptured)) return;
            Debug.Log("[Multiplayer] RecruitPoolChannel apply havens=" + snap.Havens.Count
                      + " naked=" + (snap.HasNaked ? snap.Naked.Count.ToString() : "-")
                      + " captured=" + (snap.HasCaptured ? snap.Captured.Count.ToString() : "-"));
            // Value-only stamps; every record degrades to notify on its own (log + keep previous state).
            // The native recruitment/containment screens read these pools lazily on next open; the
            // SyncEngine channel-id ≥ 3 fan-out drives the generic RefreshNeedsKick repaint.
            foreach (var rec in snap.Havens)
                RecruitPoolReflection.ApplyHavenRecruit(rt, rec.SiteId, rec.Blob);
            if (snap.HasNaked)
                RecruitPoolReflection.ApplyNakedRecruits(rt, snap.Naked);
            if (snap.HasCaptured)
                RecruitPoolReflection.ApplyCapturedUnits(rt, snap.Captured);
        }

        public void AttachHost(SyncEngine eng)
        {
            if (eng == null) return;
            // Drain pump: deferred haven marks / re-marked full-set blocks must keep the channel dirty so
            // the flush loop (after this AttachHost pass each host Tick) ships the next slice this tick.
            lock (_dirtyLock) { if (_dirtyHavens.Count > 0 || _nakedDirty || _capturedDirty) eng.MarkChannelDirty(ChannelId); }
            var fac = GeoRuntime.Instance.PhoenixFaction();
            if (fac == null) return;                       // not in geoscape yet / mid-load
            if (ReferenceEquals(fac, _faction)) return;    // already bound to this instance

            _faction = fac;
            _live = this;
            // NO baseline emission on (re)bind: pools ride the join blob / reloaded save, and hashes are
            // content-keyed so surviving entries stay valid across a reload of the same state (an equal-
            // content skip is correct; a genuinely changed pool re-arms via its seam).
        }

        public void DetachHost()
        {
            _faction = null;
            if (_live == this) _live = null;
            lock (_dirtyLock) { _dirtyHavens.Clear(); _nakedDirty = false; _capturedDirty = false; }
            _lastHavenHash.Clear();
            _lastNakedHash = null;
            _lastCapturedHash = null;
        }
    }
}
