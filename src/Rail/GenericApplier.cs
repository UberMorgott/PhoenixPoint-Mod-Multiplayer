using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Base.Core;
using HarmonyLib;
using Multiplayer.Network.MessageLayer;
using PhoenixPoint.Geoscape.Entities.PhoenixBases;
using PhoenixPoint.Geoscape.Entities.Sites;
using PhoenixPoint.Geoscape.Levels;
using UnityEngine;
using EarthUnits = PhoenixPoint.Common.Core.EarthUnits;
using CharacterIdentity = PhoenixPoint.Common.Entities.Characters.CharacterIdentity;
using GeoCharacter = PhoenixPoint.Geoscape.Entities.GeoCharacter;
using GeoSite = PhoenixPoint.Geoscape.Entities.GeoSite;
using GeoVehicle = PhoenixPoint.Geoscape.Entities.GeoVehicle;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// THE RAIL, client side (law 3 projector): decodes <see cref="SurfaceIds.GeoRail"/> delta packets,
    /// locates the live entity by walking the SAME <see cref="IdentityResolver"/> keys over the client's
    /// own graph (key resolution is symmetric — the client mirrors the host structure), and sets the
    /// field through the cached metadata accessor. The whole batch runs inside
    /// <see cref="SyncApplyScope"/> (law 8). After the batch, every touched entity fires its native
    /// repaint through <see cref="UiEventMap"/> (law 11 — open UI repaints instantly).
    ///
    /// Unknown entity path (not spawned on the client yet) → log once + skip: creating identity is the
    /// STRUCTURAL layer's job (law 3), never the value rail's. Seq gap → request a full resend from the
    /// host (law 7 resync-on-gap), while still applying the current packet (values are idempotent).
    /// </summary>
    public static class GenericApplier
    {
        private static readonly SurfaceSeq Seq = new SurfaceSeq();
        private static readonly Dictionary<byte, RailType> _kinds = new Dictionary<byte, RailType>();
        private static readonly HashSet<byte> _brokenKinds = new HashSet<byte>();
        private static Dictionary<string, object> _pathCache = new Dictionary<string, object>(StringComparer.Ordinal);
        private static uint _lastSeq;
        // A FULL resend and a SCOPED backfill are not interchangeable, so they never share a window
        // (see RequestResync). The scoped window is PER ROOT: two different roots losing their
        // ref-lists in the same second are two different losses.
        private static float _nextFullResyncAt;
        private static readonly Dictionary<string, float> _nextScopedAt = new Dictionary<string, float>(StringComparer.Ordinal);
        /// <summary>Scoped requests this peer has sent and not yet seen an answer to (root → deadline).
        /// Without it "the resend never came" and "it came and was applied silently" produce the same
        /// log — four requests in the 2026-08-07 session had ZERO acknowledgement of either kind.</summary>
        private static readonly Dictionary<string, float> _pendingScoped = new Dictionary<string, float>(StringComparer.Ordinal);
        /// <summary>The FULL resend's counterpart of the deadline above (&lt; 0 = none in flight). It had
        /// none, so the one request that covers the WORST losses — a seq gap, a torn batch, an unknown kind,
        /// i.e. everything where this peer cannot even name what it lost — was the one request nothing ever
        /// said had gone unanswered. A scoped backfill got a warning after 10 s and a full one got silence.</summary>
        private static float _pendingFullAt = -1f;
        /// <summary>Referrer PATHS whose entry dropped a reference because the referent did not exist on
        /// this peer yet — an <c>Unresolved</c> leaf, an entity-list element, an order-vector member. The
        /// host will NEVER re-ship them (its snapshot is unchanged, so no diff ever fires for them), which
        /// makes the structural create that finally mints the referent the ONE moment they can be
        /// recovered — and the scope that recovers them is the REFERRER's path, never the created root's.
        /// See <see cref="BackfillScopes"/>.</summary>
        private static readonly HashSet<string> _refDropPaths = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Evidence for a pending scoped request: how many entries ARRIVED under that root and
        /// were discarded anyway, and why the first of them was. Without it the 10 s give-up can only say
        /// the mirror stayed as it was — never whether the answer came and died on this side.</summary>
        private static readonly Dictionary<string, int> _scopedDropN = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> _scopedDropWhy = new Dictionary<string, string>(StringComparer.Ordinal);
        internal const float ResyncThrottleSec = 5f;
        private const float ScopedAnswerDeadlineSec = 10f;
        private const float CrcInterval = 1f; // one root subtree per second (see ClientCrcTick)
        private static float _crcNextAt;
        private static int _crcRoot;          // rotation cursor over IdentityResolver.Roots
        // THE ROOT THIS PEER LAST REPORTED A CRC FOR, and when — the only two facts needed to NAME the
        // field a divergence report cannot (see the ApplyEntry seam). Window is generous against the
        // report cadence (CrcInterval * roster) and the host's answer, which lands in ~150 ms.
        private static string _crcReportedRoot;
        private static float _crcReportedAt;
        private const float CrcNameWindowSec = 3f;

        public static void Reset()
        {
            ResetForReloadBoundary();
            Seq.Reset();
            _kinds.Clear();
            _brokenKinds.Clear();
            _lastSeq = 0;
            _missedNoLevel = false;   // full teardown only: a reload boundary is exactly when this is PENDING
        }

        public static void ResetForReloadBoundary()
        {
            _pathCache = new Dictionary<string, object>(StringComparer.Ordinal);
            // Flush BEFORE clearing: a boundary is the one moment the whole session's mirror-gap volume
            // is knowable, and the counts are what a once-per-message logger throws away (13 "dto-twin
            // gap" families all reported "1" in the 2026-08-07 log).
            RailMeta.FlushMissDigest("reload boundary");
            RailMeta.ResetMissTally();
            _lastSitePaint.Clear();
            _nextScopedAt.Clear();
            // The transferred save replaced the state a pending answer belonged to — it can never be
            // answered now, and reporting it unanswered later would name a root that no longer means
            // what it meant when the request went out.
            _pendingScoped.Clear();
            _pendingFullAt = -1f;
            _scopedDropN.Clear(); _scopedDropWhy.Clear();
            _refDropPaths.Clear(); // those referrers belonged to the replaced state; the save carries the refs
            _fragBuf.Clear(); _fragGot.Clear(); // the transferred save replaced the state these halves belonged to
            _pendingWire.Clear(); // those objects belonged to the replaced graph; the save wires its own on load
            // (No EventPopup reset here anymore: event windows are live 0xB6 raises, so there is no
            // record-derived latch to re-seed. Its raise-seq stream is a host monotonic counter and MUST
            // survive a reload boundary — rca-3 contract — so it resets only at full teardown.)
            // The transferred save just replaced this client's clock — re-seed the anchor scratch from it,
            // or the next partial anchor would layer onto pre-reload values.
            TimeAnchor.Reset();
            DefOwnership.Invalidate(); // the loaded save can mint runtime defs — rebuild the ownership set
            // seq + kind registry persist (rca-3 contract: host counters keep increasing across reloads)
        }

        /// <summary>The last GeoRail seq this peer actually APPLIED. Read by ReplenishSync to tell "nothing
        /// is missing" from "nothing has arrived yet" — the two are indistinguishable on a returning peer,
        /// whose squad is still the host's pre-battle save until a batch lands.</summary>
        internal static uint LastSeq => _lastSeq;

        /// <summary>The live <c>GeoLevelController</c>, or null off the geoscape / mid-load. THE one copy:
        /// twenty-two identical private ones used to exist and three of them had drifted to
        /// <c>CurrentLevel()?.GetComponent&lt;…&gt;()</c>, which is NOT the same question. <c>level == null</c>
        /// runs Unity's <c>op_Equality</c> and answers "the native half is gone" for a destroyed Level; <c>?.</c>
        /// is a plain reference test, sees the managed husk as alive and calls <c>GetComponent</c> on a dead
        /// object. That is the exact hazard RailCheck's <c>L113_UnityIdentityEquality</c> exists for, so the
        /// comparison lives in one place where it cannot drift again. Internal, like the
        /// <see cref="StartedGeoLevel"/> next to it.</summary>
        internal static GeoLevelController GeoLevel()
        {
            Base.Levels.Level level;
            try { level = GameUtl.CurrentLevel(); }
            catch (System.Security.SecurityException) { return null; } // headless/pre-player host
            return level == null ? null : level.GetComponent<GeoLevelController>();
        }

        /// <summary>The geoscape level whose own INITIALIZATION has finished. Latched by
        /// <see cref="GeoscapeStartedPatch"/>; see it for why "the level object exists" is a different and
        /// insufficient question. Latched as the LEVEL INSTANCE rather than a bool, so a teardown needs no
        /// reset: a new geoscape is a new <see cref="GeoLevelController"/> and cannot match a stale latch,
        /// and a destroyed one is never reference-equal to what <see cref="GeoLevel"/> returns.</summary>
        private static GeoLevelController _startedLevel;

        internal static void MarkGeoscapeStarted() => _startedLevel = GeoLevel();

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static bool SameLevelInstance(object current, object started) => ReferenceEquals(current, started);

        internal static GeoLevelController StartedGeoLevel()
        {
            var geo = GeoLevel();
            return geo != null && SameLevelInstance(geo, _startedLevel) ? geo : null;
        }

        /// <summary>Returns true when the surface was consumed (GeoRail, either direction).</summary>
        public static bool HandleInbound(NetworkEngine engine, ulong senderPeerId, byte surfaceId, byte[] payload)
        {
            if (surfaceId != SurfaceIds.GeoRail) return false;
            if (payload == null || payload.Length == 0) return true;
            try
            {
                if (payload[0] == DiffEngine.MsgResyncRequest)
                {
                    // Membership gate: a full resend is host-side work fanned out to ALL peers, so
                    // only a peer actually on the roster may trigger it — a rejected/unjoined
                    // sender's socket can still be up and would otherwise drive it unthrottled.
                    if (engine != null && engine.IsHost && engine.Session != null
                        && engine.Session.Clients.ContainsKey(senderPeerId))
                    {
                        // SCOPE FIRST. The request optionally names the root the client lost (payload
                        // byte 1..: a length-prefixed string); a named root is answered with the scoped
                        // re-emit that already exists rather than with the whole graph. Only a request
                        // that knows no scope falls back to the full resend — which is itself globally
                        // coalesced host-side (DiffEngine.FullResendCooldownSec).
                        // ANSWERED, OUT LOUD. The requesting peer cannot tell a resend that never came
                        // from one that arrived and applied silently, and neither could the host log:
                        // four scoped requests in the 2026-08-07 session produced no host line at all,
                        // so the RCA had to guess from DiffEngine tick shapes.
                        string root = ReadResyncRoot(payload);
                        if (!string.IsNullOrEmpty(root))
                        {
                            MpLog.Log("[Multiplayer][rail] resync request from peer " + senderPeerId +
                                      " — answering with a SCOPED re-emit of root '" + root + "'");
                            DiffEngine.ForceReemit(root);
                        }
                        else
                        {
                            MpLog.Log("[Multiplayer][rail] resync request from peer " + senderPeerId +
                                      " — answering with a FULL resend (the request named no scope)");
                            DiffEngine.RequestFullResend();
                        }
                    }
                    else
                        LogMissOnce("resync request from peer " + senderPeerId + " REFUSED — " +
                                    (engine == null || !engine.IsHost
                                        ? "this peer is not the host"
                                        : "that sender is not on the roster") +
                                    ". Its mirror stays as it is and it will ask again.");
                    return true;
                }
                if (payload[0] == DiffEngine.MsgCrcReport)
                {
                    // Same membership gate: a report can cost the host a scoped re-emit (drift backstop).
                    if (engine != null && engine.IsHost && engine.Session != null
                        && engine.Session.Clients.ContainsKey(senderPeerId))
                        using (var ms = new MemoryStream(payload))
                        using (var r = new BinaryReader(ms, Encoding.UTF8))
                        {
                            r.ReadByte(); // MsgCrcReport
                            // Bounded: this one is CLIENT→HOST, so the length prefix is a sender's word.
                            DiffEngine.HandleCrcReport(senderPeerId, MessageSerializer.ReadBoundedString(r),
                                                       r.ReadUInt32(), r.ReadUInt32());
                        }
                    return true;
                }
                if (engine == null || engine.IsHost) return true; // host never applies its own surface
                // Charged by RailCost: this runs on the NETWORK DRAIN, not inside SyncEngine.Tick, so the
                // tick total cannot see it — and "the client hitches, the host does not" points here first.
                long t = RailCost.Now();
                if (payload[0] == DiffEngine.MsgDelta) { ApplyDelta(engine, payload); RailCost.Charge("apply", t); }
                else if (payload[0] == DiffEngine.MsgStructural) { ApplyStructural(engine, payload); RailCost.Charge("structural", t); }
                // A 0xB7 modal raise is broadcast SYNCHRONOUSLY from the host's OpenModal postfix while the
                // entity it names only crosses on this surface a frame later — so an applied batch is the one
                // moment a parked window can become raisable (law 3: only a structural apply creates
                // identity). Pumped for delta batches too: the pump is also what advances the bounded expiry.
                GeoModalMirror.PumpParked();
            }
            catch (Exception ex) { MpLog.LogError("[Multiplayer][rail] GenericApplier inbound failed: " + ex); }
            return true;
        }

        private static void ApplyDelta(NetworkEngine engine, byte[] payload)
        {
            // Receipt stamp for the clock: everything from here to TimeAnchor.ApplyIfTouched at the end of
            // this batch is delay THIS peer can measure, and it is the only part of the anchor's flight
            // anybody can (the host's own estimate of the rest was the permanent 1440 game-s lead).
            TimeAnchor.NoteBatchReceived();
            var geo = StartedGeoLevel();
            if (geo == null) { MissedNoLevel(); return; } // mid-load — see MissedNoLevel for what that costs

            using (var ms = new MemoryStream(payload))
            using (var r = new BinaryReader(ms, Encoding.UTF8))
            {
                r.ReadByte(); // MsgDelta
                uint seq = r.ReadUInt32();
                if (!Seq.ShouldApply(SurfaceIds.GeoRail, seq)) return; // stale
                if (_lastSeq != 0 && seq > _lastSeq + 1)
                    RequestResync(engine, "seq gap (" + _lastSeq + "→" + seq + ")");

                // The presentation latches must be seeded from the mirror AS IT WAS BEFORE THIS BATCH.
                // Seeded lazily from inside the post-batch Fire, the first batch after a reload boundary is
                // both the seed and the transition — and the transition loses (live 2026-08-11: the geo
                // clock was paused from the mission return until peer 1 resumed it, so the FIRST research
                // delta the clients saw after the boundary was the completion itself; it seeded them
                // silently and the research-complete window opened on the host alone).
                ResearchSync.SeedLatchFromMirror(geo);
                var touched = new HashSet<object>();
                try
                {
                    int defCount = r.ReadByte();
                    for (int i = 0; i < defCount; i++)
                        RegisterKind(r.ReadByte(), MessageSerializer.ReadBoundedString(r), r.ReadUInt16());

                    int n = r.ReadUInt16();
                    _pathCache.Clear(); // batch-local: a new instance under the same key (re-queued research) must re-resolve
                    using (SyncApplyScope.Enter())
                    {
                        for (int i = 0; i < n; i++)
                        {
                            byte kindId = r.ReadByte();
                            string path = MessageSerializer.ReadBoundedString(r);
                            ushort fieldIdx = r.ReadUInt16();
                            string subKey = MessageSerializer.ReadBoundedString(r);
                            var value = Reassemble(path, fieldIdx, subKey, r.ReadBytes(r.ReadUInt16()));
                            if (value == null) continue; // fragment stashed — the entry applies once it is whole
                            ApplyEntry(engine, geo, kindId, path, fieldIdx, subKey, value, touched);
                        }
                    }
                    ApplyDepartureGenerationTail(r);
                    // The anchor is a DTO, so the leaf applies above only filled it in — this is where it becomes
                    // the clock. Post-batch, and outside SyncApplyScope because ProcessInstanceData fires nothing.
                    TimeAnchor.ApplyIfTouched(geo, touched);
                    // Same post-batch rung, same reason: an ORDER may arrive as several leaves of one batch
                    // (Travelling + DestinationSites + CurrentSite), and re-deriving from a half-applied order
                    // would seed the wrong route. Outside SyncApplyScope on purpose — this only STARTS a
                    // coroutine, whose arrival callback must meet VehicleArrivalGate with no apply exemption.
                    FlushOrderReseed();
                    MarkDepartureWatermarksSettled();
                    FlushDepartureRevalidation();
                }
                catch (Exception ex)
                {
                    // Reader-level failure mid-batch: leave the seq UNMARKED (SurfaceSeq contract — a failed
                    // apply must not consume the seq) and recover the lost entries via the throttled resync.
                    // Per-entry failures never land here (ApplyEntry catches its own); this is a torn packet.
                    MpLog.LogError("[Multiplayer][rail] GenericApplier: batch failed at seq " + seq + ": " + ex);
                    RequestResync(engine, "batch failed at seq " + seq);
                    return;
                }
                // Mark ONLY after the whole batch applied (SurfaceSeq.cs contract). Native events raised
                // during the apply run synchronously inside SyncApplyScope and never pump the network, so
                // nothing re-enters the seq logic while it is still unmarked.
                _lastSeq = seq;
                Seq.Mark(SurfaceIds.GeoRail, seq);
                // Law 11 lives ENTIRELY in UiEventMap.Fire: mapped kinds repaint through their own native
                // events, unmapped kinds + ItemStorage mark the open screen dirty there. No unconditional
                // MarkDirty here — a batch of only mapped kinds (host clock ticking Timing/Wallet/Research
                // every geo hour) must NOT Exit+Enter the client's open screen.
                // Fire runs INSIDE SyncApplyScope (law 8): its native repaints (SetupQueue rebuild,
                // ResourcesChanged/StorageChanged subscribers) reach the intent-capture seams and
                // EquipStorageGate, which all key on Active — an apply-driven repaint must stay suppressed
                // exactly like the apply itself. The same rebuild reached from ResearchSync's own appliers
                // already runs inside their scopes; the Wallet/Storage raisers keep their inner scopes
                // (harmless nesting).
                using (SyncApplyScope.Enter())
                {
                    // BEFORE Fire, and only here: this batch is what filled the objects an earlier structural
                    // create left empty, so it is the first moment their owner's native load wiring can run
                    // without dereferencing a leaf that has not landed (see FlushPendingWire).
                    FlushPendingWire(geo, seq);
                    UiEventMap.Fire(touched, geo);
                }
                RedeemRefDrops(engine);
            }
        }

        /// <summary>THE VALUE-BATCH HALF OF THE BACKFILL. A dropped reference leaves the referrer SHORT of
        /// the host's value — an EntityList element removed as <c>Unresolved</c> (:1073-1076), an
        /// order-vector member that resolved to nothing (:1106) — and the host, whose OWN value did not
        /// change, never re-ships it. Until now the only thing that ever redeemed <c>_refDropPaths</c> was
        /// a structural CREATE arriving behind it (:486): a drop with no create behind it stood until the
        /// CRC backstop happened to sample that root, whole minutes later. Measured 2026-08-13: roots
        /// <c>U#4</c>/<c>U#5</c> went silently stale on <c>_equipmentItems</c> right after an equip intent
        /// popped items out of storage, and the host only named them at quiescent seq 1008/1009 — 21 s and
        /// 7 s after the fact — with an emergency full-subtree re-emit.
        ///
        /// So ask for the REFERRER here, on the same rung the create backfill uses and with the same scope
        /// rule (never the missing referent — it has no entry of its own to restate). Bounded by
        /// construction: <see cref="RequestResync"/> is per-root throttled to <see cref="ResyncThrottleSec"/>,
        /// and a ref that is STILL unresolvable simply re-records itself on the next entry that carries it.
        /// Skipped while a FULL resend is pending — that answer restates every root anyway, and the join
        /// hydration where drops arrive in bulk is exactly when one is in flight.</summary>
        internal static bool ShouldRedeemRefDrops(int dropCount, float pendingFullAt) =>
            dropCount > 0 && pendingFullAt < 0f;

        private static void RedeemRefDrops(NetworkEngine engine)
        {
            if (!ShouldRedeemRefDrops(_refDropPaths.Count, _pendingFullAt)) return;
            foreach (var scope in new List<string>(_refDropPaths))
                RequestResync(engine, "dropped reference under '" + scope + "'", scope);
            _refDropPaths.Clear();
        }

        // ─── Oversized-value reassembly (the envelope layer's own split — DiffEngine.FragmentForWire) ───
        // Fragments of one entry ride CONSECUTIVE entries on the ONE ordered seq stream (possibly across
        // packets, since each packet caps at MaxPacketBytes), so ordering is the delivery contract's job and
        // nothing here needs to sort. A seq gap already drives the throttled resync, which re-ships the whole
        // value; a partial buffer left behind by one is simply overwritten by the resend's first fragment.
        private static readonly Dictionary<string, byte[]> _fragBuf = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> _fragGot = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>An ordinary value passes through untouched (one byte compared); a FRAGMENT is copied into
        /// its entry's buffer and returns null until the last byte lands. Internal because RailCheck L40
        /// drives this exact function against <see cref="DiffEngine.FragmentForWire"/> — a reassembler the
        /// harness re-implements could agree with itself while disagreeing with the wire.</summary>
        internal static byte[] Reassemble(string path, ushort fieldIdx, string subKey, byte[] value)
        {
            if (!RailMeta.TryDecodeFragment(value, out int total, out int offset, out var chunk)) return value;
            string key = path + "" + fieldIdx + "" + subKey;
            if (!_fragBuf.TryGetValue(key, out var buf) || buf.Length != total || offset == 0)
            { buf = new byte[total]; _fragBuf[key] = buf; _fragGot[key] = 0; }
            Buffer.BlockCopy(chunk, 0, buf, offset, chunk.Length);
            int got = _fragGot[key] + chunk.Length;
            _fragGot[key] = got;
            if (got < total) return null;
            _fragBuf.Remove(key); _fragGot.Remove(key);
            return buf;
        }

        /// <summary>Structural create/destroy (law 3, host→client): the entity arrives as a native-
        /// Serializer blob and is reconstructed through the game's OWN deserialization (PostRead
        /// callbacks = the load path, SerializerRoundtrip), then registered exactly the way the game's
        /// load registers it. Idempotent: a redelivered create finds the root already resolvable and
        /// skips; a destroy of an unknown root skips. Rides the ONE ordered GeoRail seq stream, so a
        /// stale packet is dropped by SurfaceSeq like any delta. Enabled kinds mirror the host table:
        ///   "U#" GeoCharacter — register geo._tacUnits[unit.Id] (GeoLevelController.cs:607-610);
        ///        destroy = native GeoLevelController.DestroyTacUnit (:1560-1563). Container membership
        ///        (site/vehicle TacUnits ref-lists) rides the value rail; a create additionally requests
        ///        the throttled resync so ref-list deltas applied BEFORE the root existed reconverge.
        /// Anything else → logged once (the visible opt-out).</summary>
        private static void ApplyStructural(NetworkEngine engine, byte[] payload)
        {
            var geo = StartedGeoLevel();
            if (geo == null) { MissedNoLevel(); return; } // mid-load — see MissedNoLevel for what that costs

            using (var ms = new MemoryStream(payload))
            using (var r = new BinaryReader(ms, Encoding.UTF8))
            {
                r.ReadByte(); // MsgStructural
                uint seq = r.ReadUInt32();
                if (!Seq.ShouldApply(SurfaceIds.GeoRail, seq)) return; // stale/duplicate
                if (_lastSeq != 0 && seq > _lastSeq + 1)
                    RequestResync(engine, "seq gap (" + _lastSeq + "→" + seq + ")");
                byte op = r.ReadByte();
                bool fragmented = (op & DiffEngine.FragmentedBlobFlag) != 0;
                op &= unchecked((byte)~DiffEngine.FragmentedBlobFlag);
                string rootKey = MessageSerializer.ReadBoundedString(r);
                // The blob length is a byte count, so the "one byte per entry" floor is the exact bound: a
                // length the stream cannot back would otherwise have ReadBytes allocate new byte[len] first.
                var blob = r.ReadBytes(MessageSerializer.ReadBoundedCount(r));
                // A blob too big for the u16 envelope arrives as fragments (DiffEngine.FragmentStructuralBlob),
                // reassembled by the SAME buffer the entry rail uses. Nothing below knows it was ever split;
                // an incomplete blob simply is not a packet to apply yet. The seq was already accepted, so a
                // seq gap on the fragment stream still drives the ordinary throttled resync.
                if (fragmented)
                {
                    blob = Reassemble(rootKey, ushort.MaxValue, "#structural", blob);
                    // A buffered fragment IS a consumed packet: mark its seq, or the NEXT fragment reads as
                    // a gap and every one of them asks the host for a full resync.
                    if (blob == null) { _lastSeq = seq; Seq.Mark(SurfaceIds.GeoRail, seq); return; }
                }

                bool created = false;
                var touched = new HashSet<object>();
                try
                {
                    using (SyncApplyScope.Enter())
                    {
                        var existing = IdentityResolver.Resolve(geo, rootKey, null);
                        if (DiffEngine.IsDescendPath(rootKey))
                        {
                            // Descend FIELD (S#<id>.SerializationData.ActiveMission): the third shape.
                            // Payload = the concrete type name; the field's values ride the next packet.
                            if (op == 1 && existing == null)
                                created = ApplyDescendCreate(geo, rootKey, blob, touched);
                            else if (op == 2 && existing != null)
                                ApplyDescendDestroy(geo, rootKey, touched);
                        }
                        else if (rootKey.IndexOf('.') >= 0)
                        {
                            // Keyed-collection ELEMENT (…Layout._facilities#<id>): same set-diff wire,
                            // element-specific native wiring below.
                            if (op == 1 && existing == null)
                                created = ApplyFacilityCreate(geo, rootKey, blob);
                            else if (op == 2 && existing is GeoPhoenixFacility fac)
                                ApplyFacilityDestroy(geo, rootKey, fac);
                        }
                        else if (op == 1 && existing == null)
                        {
                            if (rootKey.StartsWith("V#", StringComparison.Ordinal))
                                created = ApplyVehicleCreate(geo, rootKey, blob);
                            else if (rootKey.StartsWith("U#", StringComparison.Ordinal))
                            {
                                var unit = Multiplayer.Rail.SerializerRoundtrip.DeserializeGraph(blob, typeof(PhoenixPoint.Geoscape.Entities.GeoCharacter), quiet: true)
                                           as PhoenixPoint.Geoscape.Entities.GeoCharacter;
                                var reg = IdentityResolver.TacUnitsDict(geo);
                                if (unit == null || reg == null)
                                { MpLog.LogError("[Multiplayer][rail] structural create '" + rootKey + "': " + (unit == null ? "blob deserialize failed" : "no _tacUnits registry")); return; }
                                reg[unit.Id] = unit; // the game's own load registration (ProcessInstanceData:609)
                                created = true;
                                touched.Add(unit);   // UiEventMap GeoCharacter arm: native derived-stat refresh + repaint
                                MpLog.Log("[Multiplayer][rail] structural create '" + rootKey + "' applied (" + blob.Length + "B)");
                            }
                            else LogMissOnce("structural create for '" + rootKey + "' not enabled — skipped");
                        }
                        else if (op == 2 && existing != null)
                        {
                            if (existing is PhoenixPoint.Geoscape.Entities.GeoVehicle vehicle)
                                ApplyVehicleDestroy(rootKey, vehicle);
                            else if (existing is PhoenixPoint.Geoscape.Entities.Missions.IGeoTacUnit unit)
                            {
                                geo.DestroyTacUnit(unit); // native removal (GeoLevelController.cs:1560-1563)
                                // …which is a one-line dictionary erase and NOTHING else: no event, no view
                                // notification. So an edit screen open on this exact unit is still bound to
                                // it, and stays bound until something says otherwise — measured as a screen
                                // that painted a dismissed soldier and then threw out of its own UpdateState
                                // every frame. Release it through the game's own doors before we walk away.
                                OpenUiRepaint.ReleaseScreenBoundTo(geo, unit as PhoenixPoint.Geoscape.Entities.GeoCharacter);
                                // Repaint like every other structural arm does (:517, :536, :624, :652, :717).
                                // NOT via `touched` + UiEventMap.Fire: the GeoCharacter arm there runs a
                                // native derived-stat recompute and an identity reseed ON the entity, and the
                                // entity in hand has just been erased from the level.
                                OpenUiRepaint.MarkDirty();
                                MpLog.Log("[Multiplayer][rail] structural destroy '" + rootKey + "' applied");
                            }
                            else LogMissOnce("structural destroy for '" + rootKey + "' not enabled — skipped");
                        }
                        // op==1 with existing != null / op==2 with null = redelivery or already-converged: no-op (law 7)
                    }
                    // Same post-batch rung the value batch uses at :171 (and outside SyncApplyScope for the
                    // same reason): a structural packet is the ONLY carrier of a site's mission going
                    // null↔non-null, so without this the marker repaint it just marked would wait for the
                    // next value batch — or never arrive at all on a quiet geoscape.
                    FlushOrderReseed();
                }
                catch (Exception ex)
                {
                    MpLog.LogError("[Multiplayer][rail] structural apply '" + rootKey + "' failed: " + ex);
                    return; // seq unmarked — the throttled resync path recovers
                }
                _lastSeq = seq;
                Seq.Mark(SurfaceIds.GeoRail, seq);
                _pathCache.Clear(); // a root appeared/vanished — cached resolutions are void
                if (touched.Count > 0)
                    using (SyncApplyScope.Enter())
                        UiEventMap.Fire(touched, geo); // law 11: open roster/equip screens repaint NOW
                // NOT for a DESCEND field. The backfill exists for ref-lists that shipped BEFORE their root
                // existed (a U#/V# actor can be listed in a container a tick before it is registered), and a
                // descend field's subtree is reachable ONLY through the field just assigned — nothing can
                // have addressed it earlier, and a ref INTO it names the same key the create just made
                // resolvable. Its values ride the SAME batch immediately behind the create: DiffEngine:975-977
                // emits every structural packet before any value entry on the one ordered seq stream, the
                // invariant IsDescendPath's own doc states at :310-312. PROVEN, not assumed — 2026-08-08
                // client log, ONE frame (4965): `structural create 'S#602…ActiveMission' applied` 12:21:03.684
                // → `repaint S#602 … activeMission=StoryNJ0_CustomMissionTypeDef` 12:21:03.696, while the host
                // only answered the backfill at 12:21:03.698. So this asked the host to resend a root the
                // client had already applied — two round-trips at tactical entry, one for `_squad` and one for
                // `GlobalTime`, every mission. Real packet loss is still covered, by the seq-gap resync.
                if (created && !DiffEngine.IsDescendPath(rootKey))
                {
                    // The ONE resync caller that knows its scope — and the scope is the REFERRER, never the
                    // root just created (see BackfillScopes). Still a scoped re-emit, not the whole graph.
                    foreach (var scope in BackfillScopes(rootKey, _refDropPaths))
                        RequestResync(engine, "structural create backfill for '" + rootKey + "'", scope);
                    // Consumed either way: a recorded path UNDER the created root was just restated by the
                    // create blob, and one we did ask for is either answered or reported unanswered by the
                    // pending-scoped bookkeeping. A ref that is STILL unresolvable re-records itself on the
                    // next entry that carries it.
                    _refDropPaths.Clear();
                }
            }
        }

        // ─── Descend-field wiring (structural create/destroy for a field going null↔non-null) ───
        // The generic half — owner + field resolution + construction — carries no subsystem knowledge;
        // only the post-assign NATIVE wiring is per-owner, exactly like the facility pair below (the
        // game's own load path differs per type, and re-deriving it is what keeps the client a mirror).

        /// <summary>Owner path + member name of a Descend path (`S#12.SerializationData.ActiveMission`
        /// → `S#12.SerializationData`, `ActiveMission`). The owner resolves to the LIVE entity even when
        /// the trailing segments are a recorded-DTO twin (IdentityResolver keeps `cur` on the live actor
        /// and only re-keys the member lookup), so the field must be looked up in the DIRECT table first
        /// and the bridged twin table second — the same two rungs ApplyEntry uses at :373-385.</summary>
        private static bool ResolveDescendTarget(GeoLevelController geo, string rootKey, out object owner, out RailField field)
        {
            owner = null; field = null;
            int d = rootKey.LastIndexOf('.');
            if (d <= 0) return false;
            var name = rootKey.Substring(d + 1);
            owner = IdentityResolver.Resolve(geo, rootKey.Substring(0, d), null);
            if (owner == null) return false;
            field = RailType.Get(owner.GetType())?.FieldByName(name);
            if (field == null)
            {
                var dto = RailMeta.FindBridge(owner.GetType());
                field = dto == null ? null : RailType.GetBridged(owner.GetType(), dto)?.FieldByName(name);
            }
            return field != null && field.Class == FieldClass.Descend;
        }

        /// <summary>Why a path whose OWNER resolves is still unresolvable: its last segment is a Descend
        /// field that is NULL on this peer because the host WALKS INTO it and ships its values but never a
        /// structural create — the field's type is missing from
        /// <see cref="DiffEngine.StructuralDescendKinds"/>. Every entry under such a path is lost
        /// PERMANENTLY (the host's snapshot diff re-emits only on change, so nothing ever asks again), and
        /// the bare "entity not found" names none of that: `S#602.SerializationData.MapPlotInstanceData`
        /// spent the 2026-08-08 session as one counted line on both clients while the site's whole map-plot
        /// layout never crossed. Returns "" for every other miss shape, so an ordinary unresolved path
        /// still reads the way it always did.</summary>
        private static string DescendGapUnder(GeoLevelController geo, string path)
        {
            if (!DiffEngine.IsDescendPath(path) || !ResolveDescendTarget(geo, path, out var owner, out var field))
                return "";
            object cur;
            try { cur = field.GetValue(owner); } catch { return ""; }
            if (cur != null) return "";
            return " — " + owner.GetType().Name + "." + field.Name + " is a NULL Descend field here: the host " +
                   "ships this subtree's values but never a structural create for it, because " +
                   field.ValueType.Name + " is not in DiffEngine.StructuralDescendKindTable. Every entry " +
                   "under this path is lost until that row exists.";
        }

        // GeoSite.RegisterMission (private, GeoSite.cs:787) — the load path's own mission wiring:
        // subscribes OnMissionActivated/PreApplyResult/Completed/Cancel and raises SiteMissionStarted,
        // which is what repaints the site's geoscape marker natively. EXACT param type (law 5): the real
        // parameter is GeoMission, and AccessTools.Method matches Type[] exactly — a base would bind null.
        private static readonly MethodInfo SiteRegisterMission = AccessTools.Method(
            typeof(PhoenixPoint.Geoscape.Entities.GeoSite), "RegisterMission",
            new[] { typeof(PhoenixPoint.Geoscape.Entities.GeoMission) });

        /// <summary>Construct the object the host reports as newly non-null and hand it to the owner's
        /// native load wiring. Construction is the game's OWN: its `[SerializeCustomCreate]` static
        /// (GeoHavenDefenseMission.cs:156-160 `new GeoHavenDefenseMission(null, null, null)` — most
        /// mission subclasses have NO parameterless ctor, so this rung is load-bearing, not a nicety),
        /// else `Activator.CreateInstance(nonPublic)` — the identical two-rung order the blob codec
        /// already uses (RailMeta.DecodeObjectBody:1892-1911). No field is filled here: the value entries
        /// for this path ride the very next packet of the same batch, and the rail covers every
        /// classified member of every mission subclass.
        ///
        /// `Site` is assigned directly rather than waited for. It IS rail-covered (Leaf/EntityRef) and
        /// would arrive one packet later, but the game's own load path assigns it BEFORE registering
        /// (GeoSite.cs:1628) and the native subscribers raised by RegisterMission read it — so this
        /// closes the one-packet window instead of NRE-ing inside it. Same value either way.
        ///
        /// Deliberately NOT replayed from GeoSite.cs:1630: `(mission as GeoUpdateableMission)
        /// .ResumeUpdating(Timing)`. That starts the mission's own updateable on the client, i.e. local
        /// sim over state the host already ships (`SerializedNextUpdate`, `_status` are covered leaves) —
        /// law 3. The client stays a projector; the countdown it shows is the host's.</summary>
        private static bool ApplyDescendCreate(GeoLevelController geo, string rootKey, byte[] blob, HashSet<object> touched)
        {
            if (!ResolveDescendTarget(geo, rootKey, out var owner, out var field))
            { LogMissOnce("descend owner/field unresolved at " + rootKey + " — create skipped"); return false; }
            var typeName = RailMeta.DescendCreateTypeName(blob);
            var t = string.IsNullOrEmpty(typeName) ? null : AccessTools.TypeByName(typeName);
            if (t == null || !field.ValueType.IsAssignableFrom(t))
            {
                LogMissOnce("descend create at " + rootKey + ": payload type '" + (typeName ?? "<empty>") +
                            "' " + (t == null ? "unresolvable" : "is not a " + field.ValueType.Name) + " — skipped");
                return false;
            }
            object made;
            try { made = RailMeta.ConstructLikeLoad(t, RailMeta.DecodeCreateArgs(blob, t, geo)); }
            catch (Exception ex)
            { LogMissOnce("descend create at " + rootKey + ": " + t.Name + " construction threw " + ex.GetType().Name + " — skipped"); return false; }
            if (made == null) { LogMissOnce("descend create at " + rootKey + ": " + t.Name + " could not be constructed — skipped"); return false; }
            // Law 1: what the frame could NOT carry gets a line, not silence. Create params are WriteOnly
            // members, so the value rail will never fill them either — the create packet was the only
            // chance and for these it missed. Same predicate as RailCheck L29 (RailMeta.CreateInfoOf +
            // LeafKindOf), so the runtime line and the static law cannot drift.
            var cp = RailMeta.UncarriableCreateParams(t);
            if (cp.Length > 0)
                LogMissOnce("descend create at " + rootKey + ": " + t.Name + " create params (" +
                            string.Join(",", cp) + ") are not leaf-encodable — they arrive NULL");

            field.SetValue(owner, made);
            if (owner is PhoenixPoint.Geoscape.Entities.GeoSite site &&
                made is PhoenixPoint.Geoscape.Entities.GeoMission mission)
            {
                if (SiteRegisterMission == null)
                { LogMissOnce("GeoSite.RegisterMission handle unresolved — mission wired but not registered"); }
                else
                {
                    mission.Site = site;                                  // GeoSite.cs:1628
                    // NOT invoked here — PARKED. See ParkNativeWiring: the object this create just made is
                    // still EMPTY (its leaves ride the next batch), and RegisterMission raises the game's own
                    // SiteMissionStarted synchronously into subscribers that read those leaves.
                    ParkNativeWiring(rootKey, owner, field, made,
                        () => SiteRegisterMission.Invoke(site, new object[] { mission })); // GeoSite.cs:1629
                }
            }
            // A RAW assign is NOT a gap by itself, and the blanket warning that used to sit here said it was
            // — twice per mission entry, on both clients, every battle. For the plain [SerializeType] data
            // members the structural table vets, raw IS the game's own load path: the native Serializer
            // restores GeoMission.GlobalTime and _squad as ordinary serialized members (decompile
            // GeoMission.cs:139 `{ get; set; }`, :108 `private GeoSquad _squad`, both written raw at :237 and
            // :217/:230), and GeoSite.ProcessInstanceData:1621-1631 is that same restore on load. There is no
            // wiring to miss. What IS a real unwired create is a MISSION landing on an owner that is not a
            // GeoSite: that one loses RegisterMission — the native subscriptions AND the marker repaint.
            else if (made is PhoenixPoint.Geoscape.Entities.GeoMission)
                LogMissOnce("descend create at " + rootKey + ": a mission was assigned to " +
                            owner.GetType().Name + "." + field.Name + ", which is not a GeoSite — " +
                            "GeoSite.RegisterMission did NOT run, so its native subscriptions and the site's " +
                            "marker repaint are missing on this peer");
            touched.Add(owner);
            MarkOrderChange(geo, rootKey, owner, field.Name); // the mission wrapper is a MARKER, not a view state (law 11)
            OpenUiRepaint.MarkDirty(); // law 11: the open geoscape shows the new marker NOW
            MpLog.Log("[Multiplayer][rail] structural create '" + rootKey + "' applied (" + t.Name + ")");
            return true;
        }

        /// <summary>The host's field went null — clear the client's. This is the half that used to vanish
        /// with no entry, no tombstone and no log line (RCA gap B1/B2). Nothing to un-wire: the
        /// subscriptions RegisterMission created live ON the dropped object, and the client never resumed
        /// its updateable, so there is no EndUpdating to mirror (GeoSite.cs:1070).</summary>
        private static void ApplyDescendDestroy(GeoLevelController geo, string rootKey, HashSet<object> touched)
        {
            if (!ResolveDescendTarget(geo, rootKey, out var owner, out var field))
            { LogMissOnce("descend owner/field unresolved at " + rootKey + " — destroy skipped"); return; }
            field.SetValue(owner, null);
            touched.Add(owner);
            // The retired mission's blue wrapper hangs off GeoSiteVisualsController.RefreshMissionVisuals:620
            // (`site.ActiveMission as GeoUpdateableMission`), a globe MonoBehaviour that no view-state
            // re-enter touches — so MarkDirty alone leaves the wrapper on screen. Same seam as the leaf path.
            MarkOrderChange(geo, rootKey, owner, field.Name);
            OpenUiRepaint.MarkDirty();
            MpLog.Log("[Multiplayer][rail] structural destroy '" + rootKey + "' applied");
        }

        // ─── Deferred native wiring for a structural create (the load ORDER, not a per-case patch) ───
        // THE FAILURE (2026-08-14 session, 6x on the client, every haven-defence mission). A descend create
        // makes the object and hands it to the owner's native load wiring in the SAME packet — but the
        // object is still EMPTY: every one of its classified members rides the NEXT batch of the same stream
        // (see ApplyDescendCreate's own doc). GeoSite.RegisterMission raises SiteMissionStarted
        // SYNCHRONOUSLY, and GeoscapeFactionObjectiveSystem.OnSiteMissionStarted:293 reads
        // `mission.MissionDef.Description` (GeoscapeFactionObjectiveSystem.cs:188) — MissionDef is an
        // ordinary [SerializeMember] leaf (GeoMission.cs:204) that has not landed yet, so it NREs. The throw
        // unwound out of ApplyStructural, which correctly leaves the seq UNMARKED — and that turned one
        // missing objective into a seq gap ("98→100"), a FULL resend, and the divergence/resend storm behind
        // it. The game's own load path never has this problem because it fills the DTO FIRST and registers
        // AFTER (GeoSite.ProcessInstanceData:1621-1631); the mirror was doing it in the opposite order.
        //
        // So the wiring waits for the batch that carries the values, exactly like the load path waits for
        // ProcessInstanceData. Generic on purpose: nothing here knows about missions — a create hands over
        // an action and the flush rung decides WHEN. A wire that still throws is retried on later batches
        // and finally dropped with a line, never re-thrown into the seq path: the create itself already
        // landed and the mirror must not lose a whole subtree over a subscriber.
        private sealed class PendingWire
        {
            internal string Path; internal object Owner; internal RailField Field; internal object Made;
            internal Action Wire; internal uint ParkedSeq; internal int Tries;
        }

        private static readonly List<PendingWire> _pendingWire = new List<PendingWire>();

        /// <summary>How many value batches a wire may keep failing before it is dropped. Same bound-shape as
        /// every other parked thing on this rail: retried, then reported — never parked forever.</summary>
        /// (static readonly, not const: L500's bound guard must be a real runtime test, and a const one the
        /// compiler folds away is a guard that proves nothing.)
        internal static readonly int WireMaxTries = 8;

        internal enum WireVerdict { Wait, Wire, Drop }

        /// <summary>The whole decision, pure so RailCheck L500 can drive it case by case. <paramref
        /// name="stillAssigned"/> false = the field was destroyed or replaced before its values ever landed,
        /// so there is nothing to wire onto. <paramref name="appliedSeq"/> not past the create's seq = the
        /// batch carrying the values has not arrived, which is the exact state the inline invoke used to
        /// call into.</summary>
        internal static WireVerdict WireDecision(bool stillAssigned, uint parkedSeq, uint appliedSeq, int tries)
        {
            if (!stillAssigned) return WireVerdict.Drop;
            if (appliedSeq <= parkedSeq) return WireVerdict.Wait;
            return tries >= WireMaxTries ? WireVerdict.Drop : WireVerdict.Wire;
        }

        private static void ParkNativeWiring(string path, object owner, RailField field, object made, Action wire)
        {
            _pendingWire.Add(new PendingWire
            { Path = path, Owner = owner, Field = field, Made = made, Wire = wire, ParkedSeq = _lastSeq });
        }

        /// <summary>Run the wiring parked by earlier structural creates, on the value batch that filled them.
        /// Called from the post-batch rung INSIDE SyncApplyScope (law 8): these are native events raised
        /// during an apply. Marks the open screen dirty on success — the objective the wiring creates lands
        /// in the Phoenix agenda, which no leaf of this batch touched (law 11).</summary>
        private static void FlushPendingWire(GeoLevelController geo, uint appliedSeq)
        {
            for (int i = _pendingWire.Count - 1; i >= 0; i--)
            {
                var p = _pendingWire[i];
                bool stillAssigned;
                try { stillAssigned = ReferenceEquals(p.Field.GetValue(p.Owner), p.Made); }
                catch { stillAssigned = false; }
                switch (WireDecision(stillAssigned, p.ParkedSeq, appliedSeq, p.Tries))
                {
                    case WireVerdict.Wait: continue;
                    case WireVerdict.Drop:
                        _pendingWire.RemoveAt(i);
                        if (!stillAssigned)
                            MpLog.Log("[Multiplayer][rail] deferred wiring for '" + p.Path + "' dropped — the field no longer holds what the create made");
                        else
                            LogMissOnce("deferred wiring for '" + p.Path + "' gave up after " + p.Tries +
                                        " batches — the created " + p.Made.GetType().Name + " is assigned and " +
                                        "mirrored, but its owner's native registration never succeeded");
                        continue;
                }
                try
                {
                    p.Wire();
                    _pendingWire.RemoveAt(i);
                    OpenUiRepaint.MarkDirty();
                    MpLog.Log("[Multiplayer][rail] deferred native wiring for '" + p.Path + "' ran at seq " + appliedSeq);
                }
                catch (Exception ex)
                {
                    // Retried, not lost: the values it needed may simply ride a later batch than this one.
                    p.Tries++;
                    LogMissOnce("deferred wiring for '" + p.Path + "' threw (" + p.Tries + "/" + WireMaxTries +
                                "): " + ex.GetBaseException().Message);
                }
            }
        }

        // ─── Vehicle actor wiring (structural create/destroy for the "V#<id>@<ownerGuid>" root) ───
        // A MonoBehaviour-bound ACTOR, so the payload is a type name + the ComponentSetDef the game spawns
        // it from (RailMeta.EncodeActorCreate) and NEVER a native graph blob: decoding a blob would
        // re-create this actor and every actor its members reach (law 3). Everything else about the vehicle
        // rides the value rail one packet behind the create — CurrentSite, Stats.HitPoints, RangeRemaining,
        // Travelling, Weapons, Modules, TacUnits, Name. POSE is not among them any more (RailMeta
        // .DerivedPoseOptOut); the create's placement seed is the CurrentSite leaf, which lands on the very
        // next packet and drives ReseedNavigation's parked arm.

        /// <summary>Split a "V#&lt;id&gt;@&lt;ownerFactionDefGuid&gt;" root key. The qualifier is not
        /// decoration: VehicleID comes from the OWNER's counter (GeoFaction.cs:2008), so the key already
        /// carries BOTH facts the spawn needs and neither has to ride the payload. An unqualified key (owner
        /// unknown when it was minted — IdentityResolver.OwnerQualifier) cannot name a faction, so the
        /// create declines rather than guessing one.</summary>
        private static bool ParseVehicleKey(string rootKey, out int id, out string ownerGuid)
        {
            id = 0; ownerGuid = null;
            int at = rootKey.IndexOf('@');
            if (at < 0) return false;
            ownerGuid = rootKey.Substring(at + 1);
            return ownerGuid.Length > 0 &&
                   int.TryParse(rootKey.Substring(2, at - 2), out id);
        }

        /// <summary>The game's OWN runtime spawn (GeoFaction.CreateVehicle:2004-2019), replayed minus the id
        /// ALLOCATION — `:2008 VehicleID = ++_lastVehicleIndex` becomes the id read off the root key. That
        /// is the whole allocator discipline here and it is the same reason ApplyFacilityCreate does not call
        /// AddFacility: a client that allocates mints ids the host never issued, and a mirrored counter would
        /// then silently rewind and re-issue live ones. (`_lastVehicleIndex` is ALSO replicated —
        /// GeoFactionInstanceData.LastVehicleIndex rides the F# root, docs/rail-baseline.txt — but that is a
        /// belt, not the fix.)
        ///
        /// Deliberately NOT replayed from that method:
        ///   • `:2012 UseLoadout(...)` — the LOAD path does not do it either: it clears equipment and adds
        ///     what the DTO carried (GeoVehicle.ProcessInstanceData:1089-1102), and here the rail's covered
        ///     Weapons/Modules EntityLists are that DTO. A mirror follows the load model, not the runtime one.
        ///   • `:2015 TeleportToSite(site)` — not HERE, because the create frame does not name a site: the
        ///     spawn site is not on the root key and the payload is a type + ComponentSetDef. Its placement
        ///     half is replayed one packet later, from the mirrored `CurrentSite` leaf, by
        ///     <see cref="ReseedNavigation"/> — and only that half, since the method's tail
        ///     (`VehicleArrived`, `OnArrivedAtDestination`) is a host outcome (law 3). Until that leaf lands
        ///     the new aircraft sits at the spawn pivot for one packet; it is never dead-reckoned into a site.
        ///   • `:2016 VehicleAdded?.Invoke` and `:2017 OnVehicleArrived(...)` — host outcome events (law 3).
        /// `Stats` is NOT optional: the rail's HitPoints twin writes through `Stats.HitPoints`, so a null
        /// Stats would NRE on the first value packet. Both native paths build it the same way
        /// (`:2010` and ProcessInstanceData:1080).</summary>
        private static bool ApplyVehicleCreate(GeoLevelController geo, string rootKey, byte[] blob)
        {
            if (!ParseVehicleKey(rootKey, out var vehicleId, out var ownerGuid))
            { LogMissOnce("vehicle create '" + rootKey + "': key carries no id@ownerFactionGuid — skipped"); return false; }
            if (!(IdentityResolver.Resolve(geo, "F#" + ownerGuid, null) is GeoFaction owner))
            { LogMissOnce("vehicle create '" + rootKey + "': owner faction F#" + ownerGuid + " unresolved — skipped"); return false; }
            var typeName = RailMeta.DescendCreateTypeName(blob);
            var t = string.IsNullOrEmpty(typeName) ? null : AccessTools.TypeByName(typeName);
            if (t == null || !typeof(PhoenixPoint.Geoscape.Entities.GeoVehicle).IsAssignableFrom(t))
            {
                LogMissOnce("vehicle create '" + rootKey + "': payload type '" + (typeName ?? "<empty>") + "' " +
                            (t == null ? "unresolvable" : "is not a GeoVehicle") + " — skipped");
                return false;
            }
            var setDef = RailMeta.DecodeActorCreateDef(blob, geo);
            if (setDef == null)
            { LogMissOnce("vehicle create '" + rootKey + "': no spawn ComponentSetDef in the frame — skipped"); return false; }
            var repo = GameUtl.GameComponent<Base.Defs.DefRepository>();
            if (repo == null) { LogMissOnce("vehicle create '" + rootKey + "': no DefRepository — skipped"); return false; }

            var v = repo.Instantiate<PhoenixPoint.Geoscape.Entities.GeoVehicle>(setDef); // GeoFaction.cs:2006
            if (v == null)
            { LogMissOnce("vehicle create '" + rootKey + "': DefRepository.Instantiate returned null for " + setDef.name + " — skipped"); return false; }
            geo.Map.SetActorRootParent(v);                  // :2007
            v.VehicleID = vehicleId;                        // :2008 WITHOUT ++_lastVehicleIndex (see above)
            v.Owner = owner;                                // :2009 — must precede DoEnterPlay: OnEnterPlay
                                                            //         calls Owner.RegisterVehicle (GeoVehicle.cs:401)
            v.Stats = v.VehicleDef.BaseStats.Clone();       // :2010
            v.RangeRemaining = v.Stats.MaximumRange;        // :2011
            v.DoEnterPlay();                                // :2013 → OnEnterPlay → BaseMap.RegisterActor →
                                                            //   GeoMap.RegisterVehicle:517-521 = Vehicles.Add,
                                                            //   i.e. what makes this root key resolvable
            v.OnLevelStart();                               // :2014 — inert on a virgin actor (GeoVehicle.cs:385
                                                            //   needs Travelling && destinations, and a just-spawned
                                                            //   one has neither; once they mirror, the order-leaf
                                                            //   re-seed issues the same Navigate :385-388 does)
            // Law 11 via the ONE universal repaint, exactly like the facility pair: UiEventMap has no
            // GeoVehicle arm, so adding the actor to `touched` would be a no-op dressed up as wiring.
            OpenUiRepaint.MarkDirty(); // the open geoscape/roster shows the new aircraft NOW
            MpLog.Log("[Multiplayer][rail] structural create '" + rootKey + "' applied (" + t.Name + " from " + setDef.name + ")");
            return true;
        }

        /// <summary>`GeoVehicle.Destroy()`'s body (GeoVehicle.cs:593-604) minus its gameplay-outcome event —
        /// the same split ApplyFacilityDestroy makes for DestroyFacility. `OnVehicleDestroyed` is NOT
        /// presentation: GeoFaction.RegisterVehicle:2059 wires it to
        /// GeoPhoenixFaction.OnVehicleDestroyed:824-845, which subtracts every crew member's Health and
        /// Fatigue.Stamina and can zero vehicle-type characters, and to
        /// GeoAlienFaction.OnVehicleDestroyed:969+, which drives infested-rebuild and behemoth logic. All of
        /// that is host-computed and already rail-covered, so re-running it on a client would double-apply
        /// losses (law 3). `MarkedForDestruction` is skipped because it cannot be set from outside
        /// (`{ get; private set; }`, :170) and is not needed: idempotence comes from the set-diff plus the
        /// `existing != null` guard.
        ///
        /// OnExitPlay (:405-412) is what actually removes the vehicle: Navigation.CancelNavigation, then
        /// BaseMap.UnregisterActor → GeoMap.UnregisterActor:400-403 → UnRegisterVehicles:530-533
        /// (Vehicles.Remove + faction cache), then Owner.UnregisterVehicle. NAMED residual, not hidden: that
        /// last call ends with `if (_automanufactureVehicles &amp;&amp; IsPlaying) UpdateManufacturing()`
        /// (GeoFaction.cs:2095) and `_automanufactureVehicles = Def.ManufacturesVehicles` is set in
        /// OnLevelStart (:392) on BOTH peers — so destroying an auto-manufacturing faction's aircraft can
        /// enqueue a replacement locally. Not permanent (the queue is host-covered state the next delta
        /// re-asserts) but it IS a client-side authoritative write, and ClientSimGate does not cover it.</summary>
        private static void ApplyVehicleDestroy(string rootKey, PhoenixPoint.Geoscape.Entities.GeoVehicle vehicle)
        {
            RemoveDepartureGeneration(rootKey);
            vehicle.OnExitPlay();                                  // GeoVehicle.cs:602
            UnityEngine.Object.Destroy(vehicle.gameObject);        // :603
            OpenUiRepaint.MarkDirty();
            MpLog.Log("[Multiplayer][rail] structural destroy '" + rootKey + "' applied (vehicle " + vehicle.VehicleID + ")");
        }

        // ─── Facility element wiring (structural create/destroy for …Layout._facilities#<id>) ───
        // Resolve-all-first handles; a null anywhere declines the whole apply (LogMissOnce), never a
        // partial wire. All private members grounded in the decompile (see ApplyFacilityCreate doc).
        private static readonly FieldInfo FacListField = AccessTools.Field(typeof(GeoPhoenixBaseLayout), "_facilities");           // GeoPhoenixBaseLayout.cs:40
        private static readonly MethodInfo FacStateHandler = AccessTools.Method(typeof(GeoPhoenixBaseLayout), "Facility_OnFacilityStateUpdated"); // :595
        private static readonly MethodInfo FacUpdateCache = AccessTools.Method(typeof(GeoPhoenixBaseLayout), "UpdateLayoutCache"); // :590 caller
        private static readonly MethodInfo FacInit = AccessTools.Method(typeof(GeoPhoenixBase), "InitFacility");                  // GeoPhoenixBase.cs:680
        private static readonly MethodInfo FacUninit = AccessTools.Method(typeof(GeoPhoenixBase), "UninitFacility");              // :727

        private static bool ResolveFacilityOwners(GeoLevelController geo, string rootKey, out GeoPhoenixBaseLayout layout, out GeoPhoenixBase pxBase, out string fieldSeg)
        {
            layout = null; pxBase = null; fieldSeg = null;
            int h = rootKey.LastIndexOf('#');
            int d = h > 0 ? rootKey.LastIndexOf('.', h) : -1;
            if (d <= 0) return false;
            fieldSeg = rootKey.Substring(d + 1, h - d - 1);
            var parentPath = rootKey.Substring(0, d);
            layout = IdentityResolver.Resolve(geo, parentPath, null) as GeoPhoenixBaseLayout;
            int d2 = parentPath.LastIndexOf('.');
            if (d2 > 0) pxBase = IdentityResolver.Resolve(geo, parentPath.Substring(0, d2), null) as GeoPhoenixBase;
            return layout != null && pxBase != null && fieldSeg == "_facilities";
        }

        /// <summary>The game's own add + load wiring, minus the id assignment (FacilityId arrives in the
        /// blob): AddFacility's body (GeoPhoenixBaseLayout.cs:585-593 — list add, state-handler
        /// subscribe, UpdateLayoutCache; NOT called directly — it would reassign ++_lastFacilityId and
        /// is private) followed by the load path's InitFacility (GeoPhoenixBase.cs:985 → :680 —
        /// facility.Initialize(pxBase): PxBase back-ref + component Contexts, + the base's reactive
        /// event subscriptions). Blob reconstruction = native Serializer (components ride serialized,
        /// Context rewired by Initialize — the exact save-load shape).</summary>
        private static bool ApplyFacilityCreate(GeoLevelController geo, string rootKey, byte[] blob)
        {
            if (FacListField == null || FacStateHandler == null || FacUpdateCache == null || FacInit == null)
            { LogMissOnce("facility wiring handles unresolved — create skipped"); return false; }
            if (!ResolveFacilityOwners(geo, rootKey, out var layout, out var pxBase, out _))
            { LogMissOnce("facility owners unresolved at " + rootKey); return false; }
            var fac = Multiplayer.Rail.SerializerRoundtrip.DeserializeGraph(blob, typeof(GeoPhoenixFacility), quiet: true) as GeoPhoenixFacility;
            if (fac == null) { MpLog.LogError("[Multiplayer][rail] structural create '" + rootKey + "': facility blob deserialize failed"); return false; }
            if (!(FacListField.GetValue(layout) is IList list)) return false;
            list.Add(fac);
            fac.OnFacilityStateUpdated += (GeoPhoenixFacility.FacilityStateEventHandler)Delegate.CreateDelegate(
                typeof(GeoPhoenixFacility.FacilityStateEventHandler), layout, FacStateHandler);
            FacUpdateCache.Invoke(layout, null);
            FacInit.Invoke(pxBase, new object[] { fac });
            OpenUiRepaint.MarkDirty(); // open base screen rebuilds via the UIStatePhoenixBaseLayout table entry
            MpLog.Log("[Multiplayer][rail] structural create '" + rootKey + "' applied (facility " + fac.Def?.name + ", " + blob.Length + "B)");
            return true;
        }

        /// <summary>The demolish path's structural half (GeoPhoenixBase demolish: DestroyFacility →
        /// Layout.RemoveFacility → UninitFacility): native RemoveFacility (public,
        /// GeoPhoenixBaseLayout.cs:227 — list remove, handler unsubscribe, FacilityId=0, cache, event)
        /// + UninitFacility (GeoPhoenixBase.cs:727 — event unsubscribe). DestroyFacility itself is
        /// skipped — it raises gameplay outcome events, and outcomes are host-side (law 3).</summary>
        private static void ApplyFacilityDestroy(GeoLevelController geo, string rootKey, GeoPhoenixFacility fac)
        {
            if (FacUninit == null) { LogMissOnce("facility wiring handles unresolved — destroy skipped"); return; }
            if (!ResolveFacilityOwners(geo, rootKey, out var layout, out var pxBase, out _))
            { LogMissOnce("facility owners unresolved at " + rootKey); return; }
            layout.RemoveFacility(fac);
            FacUninit.Invoke(pxBase, new object[] { fac });
            OpenUiRepaint.MarkDirty();
            MpLog.Log("[Multiplayer][rail] structural destroy '" + rootKey + "' applied (facility " + fac.Def?.name + ")");
        }

        private static void RegisterKind(byte kindId, string typeName, ushort fieldCount)
        {
            if (_kinds.ContainsKey(kindId) || _brokenKinds.Contains(kindId)) return;
            var t = AccessTools.TypeByName(typeName);
            var rt = t == null ? null : RailType.Get(t);
            if (rt == null || rt.Fields.Count != fieldCount)
            {
                _brokenKinds.Add(kindId);
                MpLog.LogError("[Multiplayer][rail] GenericApplier: kind " + kindId + " (" + typeName + ") " +
                               (rt == null ? "unresolvable" : "field count mismatch " + rt.Fields.Count + "≠" + fieldCount) +
                               " — entries of this kind will be skipped (mod parity?)");
                return;
            }
            _kinds[kindId] = rt;
        }

        /// <summary>Whole-segment root test ("S#9" never matches "S#95"), the apply-side twin of
        /// <c>DiffEngine.PrefixMatchOne</c>.</summary>
        internal static bool PathUnderRoot(string path, string root) =>
            path != null && root != null && path.Length >= root.Length &&
            string.CompareOrdinal(path, 0, root, 0, root.Length) == 0 &&
            (path.Length == root.Length || path[root.Length] == '.');

        /// <summary>
        /// NAME THE FIELD THE CRC BACKSTOP CANNOT.
        ///
        /// A divergence report carries ONE hash for a WHOLE subtree, so the host's
        /// <c>CRC backstop: root 'S#95' DIVERGED</c> can say that a root differs and never WHICH entry —
        /// and no amount of log reading afterwards can recover it, because nothing per-field ever crossed
        /// the wire. Live 2026-08-13: root 'S#95' diverged identically on both clients (host B1600030 vs
        /// client BD0D5BBC), the host force-re-emitted 115 entries, both clients applied it — and the
        /// session's logs cannot say what was wrong, so the actual defect survives the report.
        ///
        /// THE ANSWER IS ALREADY ON THIS PEER'S DOORSTEP. The host answers a divergence with
        /// <c>DiffEngine.ForceReemit(rootKey)</c> — every entry of that subtree, values plus dict censuses —
        /// and it arrives here ~150 ms after our own report went out. An entry under the root we JUST
        /// reported that does NOT equal our bytes (the <see cref="Unchanged"/> gate above already answered
        /// that question, for free) is the divergence itself. So the naming costs one string compare on
        /// entries that changed, no wire byte and no new message.
        ///
        /// CANDIDATE, not verdict: an ordinary host-driven change to that root inside the window looks the
        /// same from here. The host's own line is the other half — a candidate printed with no
        /// <c>root DIVERGED</c> beside it is just a delta. Deduped by <see cref="LogMissOnce"/>.
        /// </summary>
        private static void NameTheDivergedField(string path, RailField field, string subKey)
        {
            if (_crcReportedRoot == null || field == null) return;
            if (Time.realtimeSinceStartup - _crcReportedAt >= CrcNameWindowSec) return;
            if (!PathUnderRoot(path, _crcReportedRoot)) return;
            LogMissOnce("CRC divergence candidate: " + path + "." + field.Name +
                        (string.IsNullOrEmpty(subKey) ? "" : "#" + subKey) +
                        " arrived DIFFERENT from this peer's value within " + CrcNameWindowSec +
                        "s of our CRC report for root '" + _crcReportedRoot + "' — if the host logged " +
                        "\"CRC backstop: root '" + _crcReportedRoot + "' DIVERGED\", this is the field it " +
                        "could not name (a report carries one hash for the whole subtree)");
        }

        /// <summary>
        /// DID THIS LEAF ACTUALLY CHANGE? A mark is raised by a value that DIFFERS, never by "a write
        /// happened" (§B.2, Bevy set_if_neq / DOTS chunk versions). Marking on write is the direct cause of
        /// the reported symptom: an unrelated peer's manufacturing tick rewrites leaves with the values
        /// they already hold, the applier marks, and the open soldier-edit screen rebuilds — resetting the
        /// model and restarting the animation.
        ///
        /// COMPARED BY VALUE OR BY BYTES, NEVER BY REFERENCE. The game mutates state in place, so
        /// reference memoization is useless here (reselect FAQ, §2.5) — two references being equal says
        /// nothing about whether the contents moved.
        ///
        /// PURE and internal so RailCheck executes the real one with no game.
        /// </summary>
        internal static bool LeafChanged(object before, object after)
        {
            if (before == null || after == null) return !ReferenceEquals(before, after);
            var a = before as byte[];
            var b = after as byte[];
            if (a != null && b != null)
            {
                if (a.Length != b.Length) return true;
                for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return true;
                return false;
            }
            // Equals, not ==: a boxed value type compares by value here, and a string compares by
            // content. A reference type with no Equals override degrades to reference equality, which is
            // the SAFE direction — it reports "changed" and costs a repaint, never a stale screen.
            return !before.Equals(after);
        }

        private static void ApplyEntry(NetworkEngine engine, GeoLevelController geo, byte kindId, string path, ushort fieldIdx,
                                       string subKey, byte[] value, HashSet<object> touched)
        {
            if (!_kinds.TryGetValue(kindId, out var rt))
            {
                // Broken kind already logged at register. A kindId never registered at all = this client
                // missed the def packet — the mid-session joiner shape: host sends each kind def ONCE per
                // client set, the joiner dropped the in-load packets pre-seq, and _lastSeq==0 skipped the
                // gap check, so without this every pre-join kind stays dead FOREVER. The full resend clears
                // the host's _sentKinds, so defs re-ship with it. Throttled (one request per window), and
                // the values missed during the load window ride the same resend.
                if (!_brokenKinds.Contains(kindId))
                {
                    LogMissOnce("unknown kindId " + kindId + " (def not received)");
                    RequestResync(engine, "unknown kindId " + kindId);
                }
                return;
            }
            if (fieldIdx >= rt.Fields.Count) return;
            var field = rt.Fields[fieldIdx];
            if (field.Class == FieldClass.Excluded) return;

            var entity = IdentityResolver.Resolve(geo, path, _pathCache);
            if (entity == null)
            {
                // Possibly a stale cache after a structural change — retry once uncached.
                _pathCache.Remove(path);
                entity = IdentityResolver.Resolve(geo, path, _pathCache);
            }
            if (entity == null) { LogMissDrop(path, "entity not found: " + path + DescendGapUnder(geo, path)); return; }
            if (!rt.Type.IsInstanceOfType(entity))
            {
                // The resolver returned the LIVE TWIN of a recorded *InstanceData DTO (writes into the
                // getter-minted DTO are void — see IdentityResolver). Same member source + ordinal sort ⇒
                // the wire fieldIdx addresses the same-named member in the bridged table.
                var bt = RailType.GetBridged(entity.GetType(), rt.Type);
                var bf = bt != null && bt.Fields.Count == rt.Fields.Count ? bt.Fields[fieldIdx] : null;
                if (bf == null || !string.Equals(bf.Name, field.Name, StringComparison.Ordinal))
                { LogMissDrop(path, "type mismatch at " + path + ": " + entity.GetType().Name + " vs " + rt.Type.Name); return; }
                if (bf.Class == FieldClass.Excluded)
                { LogMissDrop(path, "dto-twin gap: " + rt.Type.Name + "." + bf.Name + " has no live counterpart on " + entity.GetType().Name + " (" + bf.Exclude + ") — not mirrored"); return; }
                field = bf;
            }
            // Walk-time ownership law BACKSTOP (belt = host's DiffEngine refusal; this guards version
            // skew and per-peer def-graph differences): never write into an instance the def graph
            // owns, and never mutate a def-owned container reached through a live entity's field —
            // that write lands in shared def state. Leaves are exempt below by construction: a leaf
            // apply REPLACES the entity's reference, it never mutates the shared instance (and the
            // entity itself was just checked).
            if (DefOwnership.IsDefOwned(entity))
            { LogMissDrop(path, "def-owned instance at " + path + " — write refused (ownership law)"); return; }
            if (field.Class != FieldClass.Leaf && field.CanRead)
            {
                object cur;
                try { cur = field.GetValue(entity); } catch { cur = null; }
                if (cur != null && DefOwnership.IsDefOwned(cur))
                { LogMissDrop(path, "def-owned container at " + path + "." + field.Name + " — write refused (ownership law)"); return; }
            }
            // Resync-only dict CENSUS (SubKey "" + marker — a real dict entry always carries its key, and
            // no leaf/GeoItem value starts with the marker in that slot): prune local keys the host does not
            // list. The delete half of a forced re-emit — a client-side EXTRA key has no host-side change to
            // tombstone it, so values alone would leave it phantom forever.
            if (subKey.Length == 0 && value.Length > 0 && value[0] == RailMeta.DictCensusMarker &&
                (field.Class == FieldClass.LeafDict || field.Class == FieldClass.GeoItemDict))
            {
                try { ApplyDictCensus(entity, field, path, value, touched); }
                catch (Exception ex) { LogMissOnce("census failed " + path + "." + field.Name + ": " + ex.Message); }
                return;
            }
            if (Unchanged(entity, field, subKey, value))
            {
                // A SCOPED RESEND IS ANSWERED HERE TOO. What the request asks for is that this peer's
                // mirror hold the host's entry — and an entry whose bytes already equal the mirror
                // delivers exactly that. Keying the acknowledgement on the WRITE alone is why all four
                // backfills of the 2026-08-07 session reported UNANSWERED while the host had answered
                // every one: they asked for the root a structural create had just delivered byte for
                // byte, so every entry of the answer landed on this line.
                if (_pendingScoped.Count > 0 || _pendingFullAt >= 0f) NoteScopedAnswer(path);
                // Investigation diag (reassign retest), behind the ONE switch: a TacUnits delta that
                // arrived but matched local bytes. Ungated it emitted 431 lines in a release build, 424
                // of them inside ONE second of the first campaign load — MpDiag.On FIRST so the path
                // concatenation is not built either.
                if (MpDiag.On && field.Name == "TacUnits")
                    MpLog.Log("[MP][diag] TacUnits APPLY-SKIP (unchanged) " + path);
                return; // no-op entry: not applied, not touched, no repaint
            }
            NameTheDivergedField(path, field, subKey);

            // §B.2: SNAPSHOT BEFORE THE WRITE, so the mark below can ask whether the value actually
            // DIFFERS rather than whether a write happened. ONLY FieldClass.Leaf is snapshotable: a leaf
            // apply REPLACES the entity's reference (see the ownership note above), while every container
            // class (LeafList / EntityList / EntityCollection / LeafDict / GeoItemDict) is mutated THROUGH
            // the very reference this snapshot holds — before and after would be the same object and a
            // REAL change would read as "unchanged". Containers therefore keep marking unconditionally:
            // REACTIVITY is a hard mandate, a redundant repaint is cheap and a stale screen is a defect.
            bool comparable = field.Class == FieldClass.Leaf && field.CanRead;
            object beforeValue = null;
            if (comparable) { try { beforeValue = field.GetValue(entity); } catch { comparable = false; } }

            try
            {
                switch (field.Class)
                {
                    case FieldClass.Leaf:
                    {
                        var v = RailMeta.DecodeFieldValue(value, field, geo, out _);
                        // Unresolved referent (RailMeta.Unresolved, already warned once at decode): keep the
                        // client's live value — writing null over a valid ref would never be re-shipped
                        // (the host snapshot is unchanged) and the divergence would be silent.
                        if (ReferenceEquals(v, RailMeta.Unresolved))
                        {
                            // WHERE the ref was dropped, so the create that finally makes it resolvable can
                            // ask for THIS path back (BackfillScopes). Ref-addressable declared type only:
                            // an unknown def GUID is a mod-parity gap no create will ever fix, and recording
                            // it would make every later create re-ask for it forever.
                            if (IdentityResolver.IsRefAddressableType(field.ValueType)) NoteRefDrop(path);
                            return;
                        }
                        // REDUCED-DTO twin, the third coercion beside FactionRef below and the wrapper
                        // re-wrap in RailField.SetValue. At the DTO-twin swap above the HOST walked the
                        // game's own *InstanceData, so the wire carries the reduction the game RECORDS — a
                        // def as its string Id — while this field was retyped to the rich LIVE type
                        // (RailMeta.LiveTypeWins). Convert it the way the game's own ProcessInstanceData
                        // does. On the live-table path the wire already carries the rich value, so the test
                        // is false and nothing runs.
                        // Unresolvable → keep the client's live value (same L-C shape as Unresolved above):
                        // before this the raw reduced value reached SetValue and threw, which the per-field
                        // catch turned into ONE warning and a member that never mirrored again.
                        if (v != null && !field.ValueType.IsInstanceOfType(v))
                        {
                            var live = RailMeta.LiveFromReduced(field.ValueType, v);
                            if (live == null)
                            {
                                LogMissDrop(path, "reduced twin value '" + v + "' does not resolve onto " +
                                                  field.ValueType.Name + " at " + path + "." + field.Name +
                                                  " — live value kept");
                                return;
                            }
                            v = live;
                        }
                        // FactionRef twin: the wire carries the faction's DEF; the live member holds the
                        // GeoFaction. Unknown def → keep the live value (same L-C shape as Unresolved).
                        if (field.FactionRef && v != null && (v = RailMeta.FactionByDef(geo, v)) == null) return;
                        // Investigation diag (power retest 2026-07-29), behind the ONE switch: the
                        // host→client direction of the facility power leaf has never been observed live.
                        if (MpDiag.On && field.Name == "_isPowered")
                            MpLog.Log("[MP][diag] facility power APPLY " + path + "." + field.Name + " " +
                                      (field.CanRead ? field.GetValue(entity) : null) + "→" + v);
                        field.SetValue(entity, v);
                        break;
                    }
                    case FieldClass.LeafDict:
                    {
                        // Materialize: a dict the ctor never built is null on this instance, and writing the
                        // entry into that null used to be a SILENT return (see RailMeta.MaterializeContainer).
                        var target = RailMeta.MaterializeContainer(entity, field);
                        if (!(target is IDictionary dict))
                        {
                            LogMissOnce("dict field not a live non-generic IDictionary at " + path + "." + field.Name +
                                        " (" + (target == null ? "null" : target.GetType().Name) + ") — entry dropped");
                            return;
                        }
                        var key = RailMeta.DecodeDictKey(subKey, field.KeyType);
                        // Explicit delete carries the tombstone sentinel; LeafKind.Null is a genuine present-null value.
                        if (value.Length == 1 && value[0] == RailMeta.DictTombstone) { dict.Remove(key); break; }
                        var dv = RailMeta.DecodeFieldValue(value, field, geo, out _);
                        if (ReferenceEquals(dv, RailMeta.Unresolved)) return; // unresolved referent — keep the local entry
                        dict[key] = dv;
                        break;
                    }
                    case FieldClass.LeafList:
                    {
                        var items = RailMeta.DecodeFieldValue(value, field, geo, out var isNull) as List<object>;
                        // Investigation diag (reassign retest), behind the ONE switch: every TacUnits list apply.
                        if (MpDiag.On && field.Name == "TacUnits")
                        {
                            var ids = new StringBuilder();
                            if (items != null) foreach (var u in items) ids.Append(IdentityResolver.RootRef(u) ?? "?").Append(' ');
                            MpLog.Log("[MP][diag] TacUnits APPLY " + path + " count=" + (isNull || items == null ? -1 : items.Count) +
                                      " [" + ids.ToString().TrimEnd() + "]");
                        }
                        RailMeta.ApplyList(entity, field, isNull ? null : items);
                        break;
                    }
                    case FieldClass.EntityList:
                    {
                        // EntityList ONLY. EntityCollection is element-addressed (path.Name#key); the one
                        // field-level entry it ships is the order vector (own case below) — never a blob,
                        // because rebuilding keyed elements from a blob husks them (see DiffEngine's keyless
                        // branch). Ship side and apply side stay symmetric: what the host refuses to send,
                        // the client refuses to reconstruct.
                        if (value.Length == 0 || value[0] != RailMeta.EntityListMarker) return;
                        // Hand the decoder the client's CURRENT container: an ORDERED one lets it reconcile
                        // element i in place instead of building a twin, which is the only way an element
                        // holding an aliased reference (FactionDiplomacyState.Relation, shared with the
                        // rail-excluded PartyDiplomacy._relations) can ever receive the mirrored value.
                        var items = RailMeta.DecodeEntityList(value, field, geo, field.GetValue(entity));
                        // An element whose referent is not on this peer yet decodes to the Unresolved
                        // sentinel and ApplyListCore removes the hole (RailMeta:3117-3127) — the list
                        // applies SHORT and the host, whose own list did not change, never re-ships it.
                        if (items != null && items.Contains(RailMeta.Unresolved)) NoteRefDrop(path);
                        // Keep LIVE instances wherever the blob element is value-identical: a pure reorder
                        // then MOVES the client's existing objects (order rides inside the blob), and state
                        // the blob cannot carry (AmmoManager) survives on every unchanged element.
                        RailMeta.ReuseLiveElements(field, field.GetValue(entity), items);
                        RailMeta.ApplyList(entity, field, items);
                        break;
                    }
                    case FieldClass.EntityCollection:
                    {
                        // The keyed-collection ORDER+MEMBERSHIP channel: the only field-level entry an
                        // EntityCollection ever ships is the host's FULL live key sequence of an ordered
                        // container (DiffEngine.AddKeyOrder). For ALIAS collections (elements owned by a
                        // sibling container — research queue ⊂ catalog) the vector is authoritative for the
                        // SET too: prune unlisted local elements, adopt missing keys by resolving the LIVE
                        // instance from a sibling collection on the same owner (never rebuilt — law 3).
                        // Structurally-owned elements (facilities) keep membership on the create/destroy
                        // set-diff: vector stays order-only there, unknown keys wait for their create.
                        if (value.Length == 0 || value[0] != RailMeta.OrderVectorMarker) return;
                        var keys = RailMeta.DecodeKeyOrder(value);
                        var container = field.GetValue(entity);
                        bool changed = false;
                        if (!DiffEngine.IsStructuralElemType(field.ElemType))
                        {
                            string p = path, fn = field.Name;
                            changed = RailMeta.SyncMembersByKeys(container, keys, k =>
                            {
                                var inst = ResolveSiblingElement(entity, field, k);
                                if (inst == null)
                                {
                                    NoteRefDrop(p); // same loss as an Unresolved leaf: the member is simply absent here
                                    LogMissOnce("order-vector member '" + k + "' unresolved at " + p + "." + fn);
                                }
                                return inst;
                            });
                        }
                        if (!RailMeta.ReorderByKeys(container, keys) && !changed) return;
                        break;
                    }
                    case FieldClass.GeoItemDict:
                    {
                        var target = RailMeta.MaterializeContainer(entity, field);
                        if (!(target is IDictionary dict))
                        {
                            LogMissDrop(path, "GeoItemDict field not a live non-generic IDictionary at " + path + "." + field.Name +
                                        " (" + (target == null ? "null" : target.GetType().Name) + ") — entry dropped");
                            return;
                        }
                        var def = GeoItemCodec.ResolveDef(subKey); // key IS the ItemDef
                        if (def == null) { LogMissDrop(path, "GeoItemDict unknown item def " + subKey + " at " + path); return; }
                        // DIRECT dict write / remove — NOT AddItem/RemoveItem (those fire StorageChanged/ItemAdded
                        // events + faction ammo-unload = gameplay side-effects a projector client must not run).
                        if (value.Length == 1 && value[0] == RailMeta.DictTombstone) { dict.Remove(def); break; }
                        dict[def] = GeoItemCodec.Decode(value, def);
                        break;
                    }
                    default:
                        return; // Descend never carries values
                }
                object afterValue = null;
                if (comparable) { try { afterValue = field.GetValue(entity); } catch { comparable = false; } }
                // §B.2: only a value that DIFFERS raises a mark. A non-snapshotable class, or an
                // unreadable field (either read threw), compares as changed and marks — the safe
                // direction, because an unreadable model may cost a repaint but may never cost a
                // stale screen.
                if (!comparable || LeafChanged(beforeValue, afterValue)) touched.Add(entity);
                // NOT repaint marks and deliberately unconditional: the order channel and the scoped
                // backfill acknowledgement are owed by the ENTRY landing, not by the value moving.
                MarkOrderChange(geo, path, entity, field.Name);
                if (_pendingScoped.Count > 0 || _pendingFullAt >= 0f) NoteScopedAnswer(path);
            }
            catch (Exception ex)
            {
                LogMissDrop(path, "apply failed " + path + "." + field.Name + ": " + ex.Message);
            }
        }

        // ─── Order-change navigation re-seed ────────────────────────────────────────────────────────
        // The other half of excluding the POSE leaves (RailMeta.DerivedPoseOptOut): the client no longer
        // receives where its aircraft IS, so it must re-derive it — and the game already has the routine
        // that does exactly that, closed-form, every frame. This is the seam that hands that routine the
        // mirrored ORDER, and it is deliberately the ONLY place a mirrored actor's placement is written.
        //
        // NOT PER TICK — three independent reasons, because a per-tick re-seed would be catastrophic rather
        // than merely wasteful: NavigateRoutine opens with `yield return NextUpdate.Seconds(5f)`
        // (GeoNavComponent.cs:89), so every redundant re-seed freezes the aircraft for five seconds.
        //   1. The diff ships only CHANGED leaves, and `Unchanged` (above) drops a redelivered identical
        //      value before it ever reaches here — so a marker means the value really moved.
        //   2. The marker set is the ORDER leaves only. `RangeRemaining` is the one that matters: it is
        //      rail-covered AND changes continuously mid-flight, so keying on "the vehicle was touched"
        //      instead of on these three names would re-seed on every delta of a flight in progress.
        //   3. Consuming a waypoint is NOT an order change. `TravelTo` routes through the pathfinder
        //      (GeoVehicle.cs:553-556/:568-571), so a normal player order fills DestinationSites with MANY
        //      sites and the host trims one at every intermediate arrival. That is the same route, so a
        //      list which is a SUFFIX of what we last seeded is skipped; only a genuinely different route
        //      re-issues Navigate.
        private static readonly HashSet<GeoVehicle> _reseed = new HashSet<GeoVehicle>();
        private const int MaxDepartureWatermarks = DepartureGenerationRail.MaxVehicles;
        private static readonly Dictionary<string, ulong> _departureWatermarks =
            new Dictionary<string, ulong>(StringComparer.Ordinal);
        private static readonly Dictionary<string, GeoSite[]> _seededRoute = new Dictionary<string, GeoSite[]>(StringComparer.Ordinal);

        /// <summary>The ORDER leaves — a mirrored INPUT to a native derivation, as opposed to its derived
        /// output. Named, not type-dispatched: the rule is about which values constitute an order.
        /// <c>StartExplorationTime</c> joins the three navigation names for the SECOND derivation on this
        /// actor (see <see cref="ReseedExploration"/>); it is the aircraft's other order, and like them it
        /// moves only when the host actually issues one — never per tick.
        /// These are TRIGGERS ("re-derive now"), never GATES: what the re-derivation may ask is only what
        /// the host alone writes — see <see cref="UnderTravelOrder"/> for why <c>Travelling</c> is on this
        /// list yet must not decide anything.</summary>
        private static bool IsOrderLeaf(string name) =>
            name == "DestinationSites" || name == "Travelling" || name == "CurrentSite" ||
            name == "StartExplorationTime";

        /// <summary>The per-faction site leaf: <c>GeoSite.FactionsData</c> is the EntityList whose elements
        /// carry <c>Inspected</c>/<c>Visible</c>/<c>Raided</c> (GeoSite.cs:71/:398/:403, baseline row
        /// "EntityList FactionsData -> live _factionsData"). It is the AUTHORITATIVE-DONE leaf for the
        /// exploration derivation and the notify-gap for every site marker at once — see
        /// <see cref="MarkSiteAuthority"/>.</summary>
        private static bool IsSiteAuthorityLeaf(string name) => name == "FactionsData";

        /// <summary>WHAT A RAIL WRITE ON A GeoSite MUST TRIGGER, kept pure so RailCheck L115 can execute it
        /// case by case.
        ///
        /// THE MARKER REPAINT IS UNCONDITIONAL and the field name may not gate it. Every covered
        /// <c>GeoSiteInstaceData</c> member lands as a twin write on the LIVE site, which is exactly the
        /// path the game's own <c>SetProperty</c> notification does NOT sit on: <c>ActiveMission</c> is a
        /// plain auto-property (GeoSite.cs:101) and <c>State</c> raises <c>StateChanged</c>, not
        /// <c>PropertyChanged</c> (:189-197) — and <c>GeoSiteVisualsController</c> subscribes to
        /// <c>PropertyChanged</c> ALONE (:152). So the game repaints a retired site only from its own
        /// explicit <c>RefreshVisuals()</c> calls (:826 updateable-mission ended, :866 <c>DestroySite</c>),
        /// none of which a projector runs. Gating the repaint on ONE leaf name ("FactionsData") is what let
        /// a host-retired quest site keep its blue mission wrapper and its Functioning material on every
        /// client after a mission returned: the state arrived, the marker never asked again.
        ///
        /// THE PARKED-VEHICLE RE-SEED STAYS NAMED, and that asymmetry is the point: re-deriving a
        /// derivation is only correct off the AUTHORITATIVE-DONE leaf (see <see cref="MarkSiteAuthority"/>),
        /// while asking a MonoBehaviour to set its own <c>_refresh</c> bool costs one field write and is
        /// correct after any write at all.</summary>
        internal static void SiteWriteConsequences(string fieldName, out bool repaintMarker, out bool reseedParked)
        {
            repaintMarker = true;
            reseedParked = IsSiteAuthorityLeaf(fieldName);
        }

        /// <summary>WHAT A RAIL WRITE ON A <c>CharacterIdentity</c> MUST TRIGGER — the derived-cache half of
        /// the customization fix, and the one consequence in this file that deliberately takes NO field name.
        ///
        /// <c>GeoCharacter.GameTags</c> is NOT serialized: it is re-derived from the identity by the game's
        /// own <c>GeoCharacter.RefreshTags()</c> (GeoCharacter.cs:568-573 → <c>Identity.ApplyGameTags</c>),
        /// and the mesh/material addon builder renders off THOSE TAGS, not off the identity. So mirroring
        /// the 15 identity leaves perfectly still left every other peer's soldier wearing his old colours
        /// until the next reload — the value arrived, the derivation never re-ran.
        ///
        /// NAMELESS ON PURPOSE, unlike <see cref="IsOrderLeaf"/>: <c>CharacterIdentity.GetGameTags()</c>
        /// (CharacterIdentity.cs:104-121) reads THIRTEEN of the fifteen members, so a name list here would
        /// be a transcription of that method that silently rots the day the game adds a tag — which is
        /// exactly the per-field knowledge this fix exists to avoid. Any identity leaf ⇒ re-derive. The
        /// cost of a redundant re-derive is a list rebuild, not a five-second freeze (the reason the
        /// NAVIGATION re-seed next door must stay named).</summary>
        internal static bool IdentityWriteConsequence(object entity) => entity is CharacterIdentity;

        /// <summary>The ROOT segment of a path when that root names a SITE and the write landed DEEPER than
        /// the site itself, else null. Pure (no resolver, no level) so RailCheck L183 can execute it case by
        /// case, and cheap: a path whose first two chars are not "S#" costs two comparisons and no resolve.
        ///
        /// WHY IT EXISTS. A descend create ships the mission's TYPE and nothing else — every one of its own
        /// members rides a LATER packet (<see cref="ApplyDescendCreate"/>). So the repaint the create marks
        /// necessarily observes a mission with no values in it, and the batch that finally fills those values
        /// wrote a <c>GeoMission</c>, not a <c>GeoSite</c> — which the type test below dropped on the floor.
        /// Live proof (2026-08-07): the host answered the S#104 backfill and the client APPLIED it, and the
        /// client's last word on that site remained the create-time line saying the mission was not there.
        /// A repaint that cannot observe the batch that landed the state is not a repaint.</summary>
        internal static string SiteRootKeyOf(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length < 3 || path[0] != 'S' || path[1] != '#') return null;
            int dot = path.IndexOf('.');
            return dot <= 0 ? null : path.Substring(0, dot);
        }

        private static void MarkOrderChange(GeoLevelController geo, string path, object entity, string fieldName)
        {
            if (IdentityWriteConsequence(entity)) { MarkTagRefresh(geo, path); return; }
            if (IsOrderLeaf(fieldName) && entity is GeoVehicle v)
            {
                _reseed.Add(v);
                // THE HOST'S "EXPLORE NOW" EDGE. The leaf's VALUE is unusable on this peer (see
                // ExplorationStartInLocalEpoch); its ARRIVAL is not. StartedExplorationAt moves in exactly
                // one place, StartExploringCurrentSite:423, whose sole caller is ExploreSiteAbility.cs:14 —
                // and a client's own explore click is an INTENT (ClientSimGate.cs:341), so on a client this
                // edge is host-authored by construction.
                if (fieldName == "StartExplorationTime") _hostExplorationEdge.Add(v);
                return;
            }
            if (entity is GeoSite s)
            {
                SiteWriteConsequences(fieldName, out var repaintMarker, out var reseedParked);
                if (repaintMarker) _siteRepaint.Add(s);
                if (reseedParked) MarkSiteAuthority(s);
                return;
            }
            // DEPTH IS NOT A GATE, for the same reason the field NAME is not one (see
            // SiteWriteConsequences): the marker repaint costs one bool on a MonoBehaviour, and the state
            // it reads (ActiveMission and everything hanging off it) lands on entities that are not the
            // site. The re-seed stays behind the site's own authoritative leaf — that one is a derivation
            // and must not re-decide off a descendant's clock.
            var rootKey = SiteRootKeyOf(path);
            if (rootKey != null && IdentityResolver.Resolve(geo, rootKey, _pathCache) is GeoSite owner)
                _siteRepaint.Add(owner);
        }

        /// <summary>Task 12 client seam intentionally owns no authority. Source lifecycle changes arrive
        /// only as authenticated GeoWindowIntent StateDelta schema 3 after the ordinary vehicle rail.
        /// Kept as a named negative seam for L388 and future batch ordering instrumentation.</summary>
        internal static void FlushDepartureRevalidation()
        {
            MissionSync.ApplyScheduledSourceRevalidationDeltas();
        }

        private static void MarkDepartureWatermarksSettled()
        {
            // Exact generations are installed from the optional GeoRail tail before this post-batch rung.
        }

        internal static void InstallDepartureGeneration(string source, ulong generation)
        {
            if (string.IsNullOrEmpty(source) || generation == 0) return;
            ulong existing;
            if (_departureWatermarks.TryGetValue(source, out existing))
            { if (generation > existing) _departureWatermarks[source] = generation; return; }
            if (_departureWatermarks.Count >= MaxDepartureWatermarks) return; // never evict a pending token
            _departureWatermarks.Add(source, generation);
        }

        internal static void RemoveDepartureGeneration(string source)
        {
            if (string.IsNullOrEmpty(source) || MissionSync.HasScheduledSourceDelta(source)) return;
            _departureWatermarks.Remove(source);
        }

        private static void ApplyDepartureGenerationTail(BinaryReader reader)
        {
            if (reader.BaseStream.Position == reader.BaseStream.Length) return; // older host
            if (reader.ReadByte() != DepartureGenerationRail.TailMarker) throw new InvalidDataException("unknown GeoRail tail");
            int count = reader.ReadUInt16();
            if (count < 0 || count > MaxDepartureWatermarks) throw new InvalidDataException("departure generation tail over bound");
            for (int i = 0; i < count; i++)
                InstallDepartureGeneration(MessageSerializer.ReadBoundedString(reader), reader.ReadUInt64());
            // The departure-anchor section is optional so an older host's tail still parses (the precedent
            // ManufactureSync.HandleInbound sets): read it only while bytes remain.
            if (reader.BaseStream.Position != reader.BaseStream.Length)
            {
                if (reader.ReadByte() != DepartureAnchorRail.TailMarker) throw new InvalidDataException("unknown GeoRail tail");
                int anchors = reader.ReadUInt16();
                if (anchors > MaxDepartureWatermarks) throw new InvalidDataException("departure anchor tail over bound");
                // REBUILT, never merged: the host ships its whole map on every emit, so a wholesale replace is
                // what keeps this bounded by the host's own MaxVehicles cap instead of growing forever.
                _departureAnchors.Clear();
                for (int i = 0; i < anchors; i++)
                {
                    string key = MessageSerializer.ReadBoundedString(reader);
                    _departureAnchors[key] = new DepartureAnchorRail.Anchor
                    { LevelSeconds = reader.ReadDouble(), RangeValue = reader.ReadSingle() };
                }
            }
            if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("trailing GeoRail bytes");
        }

        private static readonly Dictionary<string, DepartureAnchorRail.Anchor> _departureAnchors =
            new Dictionary<string, DepartureAnchorRail.Anchor>(StringComparer.Ordinal);

        /// <summary>Is a mirrored order's derivation allowed to start from the LOCAL clock? Never, once the
        /// host has stamped the departure — that is the whole of the foreign-aircraft fix, kept pure so
        /// RailCheck L460 can execute it case by case. Returns the leg time already covered on the host,
        /// or -1 for "no usable anchor, fall back to local now".
        ///
        /// The three refusals are all cases where the anchor cannot describe THIS route:
        ///   • no stamp at all (a save-transfer join, or a host too old to send the section);
        ///   • a stamp in the future — an un-applied <see cref="TimeAnchor"/> on a fresh client;
        ///   • a stamp older than the whole route, which means it belongs to a journey already finished, not
        ///     to the order that just landed. Fast-forwarding by it would park the aircraft on its
        ///     destination.</summary>
        internal static double CoveredSeconds(bool haveAnchor, double departureSeconds, double sharedNow,
                                              double routeSeconds)
        {
            if (!haveAnchor || routeSeconds <= 0.0) return -1.0;
            double elapsed = sharedNow - departureSeconds;
            return elapsed <= 0.0 || elapsed > routeSeconds ? -1.0 : elapsed;
        }

        /// <summary>Fast-forward a re-seeded leg to where the HOST already is, so the remaining leg re-derives
        /// against shared time instead of this peer's own <c>Timing.Now</c>. See
        /// <see cref="DepartureAnchorRail"/> for why the pose itself is still never mirrored.
        ///
        /// The route walk mirrors <c>GeoNavComponent</c> exactly: <c>GeoPathRequest.Calculate</c> makes ONE
        /// segment per consecutive destination (no subdivision), each timed
        /// <c>distance.InMeters / (Speed.InMeters / 3600f)</c> (:94-95). Placement goes through the game's own
        /// <c>GeoActor.SetOrientedGlobeWorldPosition</c> (GeoActor.cs:66-70) rather than a hand-written
        /// <c>localRotation</c>, and the great-circle point is the same <c>Vector3.Slerp</c> the routine uses
        /// (:106) — taken about the geoscape centre, since the routine slerps in globe-local space.
        /// The 5 s pre-roll (:89) needs no correction: the host paid it too, so at the instant this peer's
        /// re-seeded routine starts moving the host has advanced by exactly those 5 s onto this same point.</summary>
        private static void AnchorToHostDeparture(GeoVehicle v, string root, List<Vector3> path)
        {
            DepartureAnchorRail.Anchor anchor = default(DepartureAnchorRail.Anchor);
            bool haveAnchor = root != null && _departureAnchors.TryGetValue(root, out anchor);
            if (!haveAnchor) return;
            var geo = StartedGeoLevel();
            if (geo == null || geo.Timing == null || geo.SceneReferences == null ||
                geo.SceneReferences.Geoscape == null || path.Count == 0) return;
            float speed = v.Speed.InMeters / 3600f;                   // GeoNavComponent.cs:95
            if (speed <= 0f) return;

            var from = v.WorldPosition;
            var legs = new EarthUnits[path.Count];
            double routeSeconds = 0.0;
            for (int i = 0; i < path.Count; i++)
            {
                legs[i] = GeoMap.Distance(i == 0 ? from : path[i - 1], path[i]);
                routeSeconds += legs[i].InMeters / speed;
            }
            double covered = CoveredSeconds(haveAnchor, anchor.LevelSeconds,
                                            geo.Timing.Now.TimeSpan.TotalSeconds, routeSeconds);
            if (covered < 0.0) return;

            var range = new EarthUnits(anchor.RangeValue);
            var centre = geo.SceneReferences.Geoscape.position;
            for (int i = 0; i < path.Count; i++)
            {
                double legSeconds = legs[i].InMeters / speed;
                if (legSeconds > 0.0 && covered < legSeconds)
                {
                    float t = (float)(covered / legSeconds);
                    v.SetOrientedGlobeWorldPosition(centre + Vector3.Slerp(from - centre, path[i] - centre, t));
                    v.RangeRemaining = range - legs[i] * t;
                    MpLog.Log("[Multiplayer][rail] nav anchor " + root + " → leg " + (i + 1) + "/" + path.Count +
                              " t=" + t.ToString("F3") + " covered=" + covered.ToString("F0") + "s");
                    return;
                }
                covered -= legSeconds; range -= legs[i]; from = path[i];
            }
        }

        internal static bool HasSettledDeparture(string source, ulong watermark)
        { ulong settled; return watermark != 0 && source != null && _departureWatermarks.TryGetValue(source, out settled) && settled >= watermark; }

        internal static void ClearDepartureWatermarks()
        { _departureWatermarks.Clear(); _departureAnchors.Clear(); }

        /// <summary>
        /// A DERIVED PRESENTATION'S "DONE" DOES NOT ALWAYS LAND ON THE ENTITY THAT IS PRESENTING, and that
        /// asymmetry is this whole seam. Navigation's completion lands on the MOVER — <c>Travelling</c> /
        /// <c>CurrentSite</c> are <see cref="IsOrderLeaf"/> names on the very <c>GeoVehicle</c> whose local
        /// <c>NavigateRoutine</c> is running — so the arrival case has always re-seeded itself. Exploration's
        /// completion lands on the SITE (<c>GeoFaction.OnVehicleSiteExplored</c> → <c>SetInspected</c>,
        /// GeoSite.cs:403) while the thing presenting is the vehicle's <c>GeoActorProgressionVisualController</c>,
        /// so NOTHING marked the vehicle and the client's spinner ran on to its own local end while the host
        /// was already done — the ~0.5 s the user measured, and it never self-corrected.
        ///
        /// So this marks BOTH consequences of one authoritative site write, and neither is exploration-specific:
        ///   • every vehicle parked at the site is re-seeded, so any derivation keyed on that site re-decides
        ///     against the state that just arrived instead of against its own clock. <c>GeoSite.Vehicles</c>:239
        ///     is the game's own accessor (<c>Map.Vehicles.Where(veh =&gt; veh.CurrentSite == this)</c>).
        ///   • the site's own marker is repainted, because the value rail writes <c>_factionsData</c> ELEMENTS
        ///     directly and therefore never runs <c>SetInspected</c>/<c>SetVisible</c> — so
        ///     <c>SetProperty</c> never fires <c>PropertyChanged</c>, and
        ///     <c>GeoSiteVisualsController</c> (subscribed at :152, refreshed only when <c>_refresh</c> is set)
        ///     kept the <c>Unknown</c> material its <c>RefreshSiteVisuals</c>:239 picks for an un-inspected
        ///     site. That is why the site still read UNEXPLORED on the clients after the event window closed.
        /// </summary>
        private static void MarkSiteAuthority(GeoSite site)
        {
            if (site == null) return;
            _siteRepaint.Add(site);
            var vehicles = site.Vehicles;   // GeoSite.cs:239 — the game's own "parked here" accessor
            if (vehicles == null) return;
            foreach (var v in vehicles) if (v != null) _reseed.Add(v);
        }

        private static readonly HashSet<GeoSite> _siteRepaint = new HashSet<GeoSite>();

        /// <summary>The last <c>[MP][site] repaint</c> text printed per site — see the print itself.</summary>
        private static readonly Dictionary<GeoSite, string> _lastSitePaint = new Dictionary<GeoSite, string>();

        /// <summary>HOW A SITE'S MISSION READS IN THE REPAINT LINE, kept pure so RailCheck L183 can execute it
        /// case by case. THREE outcomes, never two: the old line was
        /// <c>s.ActiveMission?.MissionDef?.name ?? "none"</c>, so a site that HAS a mission whose def has not
        /// landed yet printed the same word as a site with no mission at all. That collapse is what made
        /// five identical "activeMission=none" lines read as "the client never got that mission" when the
        /// client had in fact created it, registered it natively and applied the host's answer to its own
        /// backfill request. A diagnostic that cannot tell absent from unfinished manufactures its own
        /// root cause.</summary>
        internal static string MissionLabel(bool hasMission, string typeName, string defName)
        {
            if (!hasMission) return "none";
            if (!string.IsNullOrEmpty(defName)) return defName;
            return (typeName ?? "GeoMission") + "(def has not landed yet)";
        }

        /// <summary>The characters whose tag cache this batch invalidated. The identity is a DESCEND child,
        /// so the applied entity is the <c>CharacterIdentity</c> itself and carries no back-reference to its
        /// owner — but the path's ROOT segment names one ("U#&lt;charId&gt;"), and the batch-local
        /// <c>_pathCache</c> makes that resolution free after the first leaf of the same soldier.</summary>
        private static readonly HashSet<GeoCharacter> _tagRefresh = new HashSet<GeoCharacter>();

        private static void MarkTagRefresh(GeoLevelController geo, string path)
        {
            if (geo == null || string.IsNullOrEmpty(path)) return;
            int dot = path.IndexOf('.');
            var rootKey = dot < 0 ? path : path.Substring(0, dot);
            if (IdentityResolver.Resolve(geo, rootKey, _pathCache) is GeoCharacter c) _tagRefresh.Add(c);
            else LogMissOnce("identity write at " + path + " has no resolvable GeoCharacter root — " +
                             "the mirrored appearance will not re-derive on this peer");
        }

        private static void FlushOrderReseed()
        {
            // INSIDE SyncApplyScope (law 8 hygiene): this is a native call made during an apply, and
            // GeoCharacter.RefreshTags is the tail of the game's own customization funnel — the funnel
            // PersonnelSync's capture watches one level up. The capture's own apply-scope arm is what
            // actually stops the mirror echoing back (the repaint path, OpenUiRepaint.cs:474), so this
            // scope is not the guard; it is what keeps the guard true if the capture ever moves down here.
            // Safe to scope: RefreshTags only rebuilds a tag list (GeoCharacter.cs:568-573) — it pumps no
            // network and starts no coroutine, unlike the navigation re-seed below, which must stay
            // OUTSIDE so its arrival callback meets VehicleArrivalGate with no apply exemption.
            if (_tagRefresh.Count > 0)
                using (SyncApplyScope.Enter())
                    foreach (var c in _tagRefresh)
                    {
                        try { c.RefreshTags(); }
                        catch (Exception ex)
                        { LogMissOnce("tag re-derive failed for " + (IdentityResolver.RootRef(c) ?? "U#?") + ": " + ex.Message); }
                    }
            _tagRefresh.Clear();
            foreach (var v in _reseed)
            {
                try { ReseedNavigation(v); }
                catch (Exception ex)
                { LogMissOnce("nav re-seed failed for " + (IdentityResolver.RootRef(v) ?? "?") + ": " + ex.Message); }
                try { ReseedExploration(v, _hostExplorationEdge.Contains(v)); }
                catch (Exception ex)
                { LogMissOnce("exploration re-seed failed for " + (IdentityResolver.RootRef(v) ?? "?") + ": " + ex.Message); }
                // A FOREIGN FACTION'S AIRCRAFT IS NOT RAIL STATE AT ALL: GeoVehicle.IsVisible is
                // VisualsRoot.activeInHierarchy (GeoVehicle.cs:174-186), so no leaf can ever carry it, and its
                // only writer is RefreshVisibility (:606-612). The native trigger for one parked at a site the
                // viewer just inspected is GeoSite.SetInspected:403-406 -> GeoMap.cs:571 -> GeoFaction.cs:394 ->
                // GeoPhoenixFaction.OnSiteInspectedChanged:1187, whose loop :1191-1196 does exactly this call —
                // but the rail writes FactionsData by direct field write, so that event never fires here. Law L347.
                try { v.RefreshVisibility(); }
                catch (Exception ex)
                { LogMissOnce("visibility re-derive failed for " + (IdentityResolver.RootRef(v) ?? "?") + ": " + ex.Message); }
            }
            _reseed.Clear();
            _hostExplorationEdge.Clear();
            foreach (var s in _siteRepaint)
            {
                // The game's OWN refresh entry (GeoSiteVisualsController.Refresh:202 — sets _refresh, which
                // Update:690 consumes into RefreshSiteVisuals). Never a hand-rolled material swap: law 11's
                // repaint always comes from the decompile.
                try { s.GetComponent<PhoenixPoint.Geoscape.View.GeoSiteVisualsController>()?.Refresh(); }
                catch (Exception ex)
                { LogMissOnce("site visuals refresh failed for " + (IdentityResolver.RootRef(s) ?? "S#?") + ": " + ex.Message); }
                // The AFTER picture, paired with the "[MP][outcome] CLIENT stamped" BEFORE line: what this
                // peer's site now IS, at the instant a rail write changed it. The site-marker RCA had to be
                // reconstructed from the host's log alone because this moment was never named on the client —
                // and a site that never prints one of these after a mission return never received the delta.
                // Deduped on the RENDERED text, not throttled: a line repeats only when the site actually
                // reads differently, so the volume the descendant marking above adds is zero when nothing
                // visible changed, and every change still gets its line.
                var m = s.ActiveMission;
                var line = "[MP][site] repaint " + (IdentityResolver.RootRef(s) ?? "S#?") + " state=" + s.State +
                           " activeMission=" + MissionLabel(m != null, m?.GetType().Name, m?.MissionDef?.name);
                if (!_lastSitePaint.TryGetValue(s, out var prev) || !string.Equals(prev, line, StringComparison.Ordinal))
                {
                    _lastSitePaint[s] = line;
                    MpLog.Log(line);
                }
            }
            _siteRepaint.Clear();
        }

        // GeoVehicle.cs:73 / :448 / :460 — the private store and the two private halves the game's OWN
        // save-restore calls (ProcessInstanceData:1123-1126). Nothing is re-implemented here.
        private static readonly FieldInfo ExplorationStartField =
            AccessTools.Field(typeof(GeoVehicle), "StartedExplorationAt");
        private static readonly MethodInfo ExploreCurrentSiteMethod =
            AccessTools.Method(typeof(GeoVehicle), "ExploreCurrentSite", new[] { typeof(TimeUnit), typeof(TimeUnit) });
        private static readonly MethodInfo EndExploreCurrentSiteMethod =
            AccessTools.Method(typeof(GeoVehicle), "EndExploreCurrentSite");

        /// <summary>
        /// The exploration twin of <see cref="ReseedNavigation"/>, and for the identical reason: site
        /// exploration PROGRESS is CLOSED-FORM, not accumulated state.
        /// <c>GeoActorProgressionVisualController.Progression</c> is
        /// <c>(Timing.Now - Start).TotalMinutes / (End - Start).TotalMinutes</c> recomputed from scratch in
        /// <c>Update()</c> EVERY FRAME (GeoActorProgressionVisualController.cs:25-35, :53-56) — the same
        /// shape as <c>NavigateRoutine</c>'s <c>Ratio01</c>. Both of its inputs are host-authored or def-fixed:
        /// <c>Start</c> is this peer's own stamp of the instant the <c>StartExplorationTime</c> delta LANDED
        /// (<see cref="ExplorationStartInLocalEpoch"/> — the leaf's value is in the host's per-actor epoch and
        /// is never comparable here) and
        /// <c>End = Start + CurrentSite.ExplorationTime</c>, which is exactly the sum
        /// <c>StartExploringCurrentSite</c>:424 makes out of <c>GeoSite.ExplorationTime</c> —
        /// <c>TimeUnit.FromHours(geoSiteDef.ExplorationTimeHours)</c> (GeoSite.cs:490), def-fixed, no RNG,
        /// on a clock the client already tracks. So NOTHING about the fill needs to ride the wire: the
        /// client runs the game's own timer and its own <c>Update()</c> draws the bar smoothly, instead of
        /// a client that showed nothing at all until the host's counter completed.
        ///
        /// STATE-BASED, not edge-based, which is why it needs no memo and cannot restart anything: it asks
        /// what the mirrored order SAYS and only acts when the live handle disagrees. An unrelated order
        /// leaf (a flight consuming a waypoint) therefore costs one comparison, and a host that ended an
        /// exploration early — <c>StartTravel</c>:524/:538, <c>TeleportToSite</c>:506 — is obeyed the
        /// moment its <c>Travelling</c>/<c>CurrentSite</c> delta lands, without a cancellation flag on the
        /// wire.
        ///
        /// The OUTCOME stays host-only exactly like arrival does: the client's own timer will fire
        /// <c>SiteExplorationCompleted</c>:474, whose <c>SiteExplored</c> invoke reaches
        /// <c>GeoFaction.OnVehicleSiteExplored</c> (SetInspected + UpdateVehicleSite) — gated by
        /// <c>SiteExploredOutcomeGate</c>. Its other half, <c>EndExploreCurrentSite</c>, is presentation
        /// (destroy the visuals, drop the handle) and runs.
        /// </summary>
        /// <summary>
        /// THE EXPLORATION DECISION, kept pure so RailCheck L115 can execute it case by case (the same shape
        /// <c>OpenUiRepaint.ExplorationJustStarted</c> uses for L102).
        ///
        /// <paramref name="inspected"/> is the AUTHORITATIVE DONE and it OUTRANKS THE LOCAL CLOCK — the whole
        /// point. The fill is closed-form off a PER-ACTOR epoch (<c>ActorComponent.Timing</c>:85), so the
        /// peers' spinners never agree to the frame; the host finished ~0.5 s before the clients, and with
        /// the clock as the only end test (<c>now &lt; end</c>) a client that already KNEW the site was
        /// inspected still had to wait out its own timer. State that says "finished" must beat an animation
        /// still playing, so the inspected test is asked FIRST and short-circuits the clock entirely.
        /// <c>ExploreSiteAbility.GetTargetDisabledStateInternal</c>:42-49 already refuses to explore an
        /// inspected site, so this can never cancel a legitimate second exploration — there is no such thing.
        /// </summary>
        internal static bool ShouldBeExploring(bool underTravelOrder, bool inspected,
                                               TimeUnit start, TimeUnit explorationTime, TimeUnit now)
        {
            if (underTravelOrder || inspected) return false;
            return start > TimeUnit.Zero && explorationTime != TimeUnit.Zero && now < start + explorationTime;
        }

        /// <summary>The vehicles whose <c>StartExplorationTime</c> leaf CHANGED in the batch being flushed —
        /// i.e. the host issued an exploration order. Batch-scoped, cleared with <see cref="_reseed"/>.</summary>
        private static readonly HashSet<GeoVehicle> _hostExplorationEdge = new HashSet<GeoVehicle>();

        /// <summary>THIS peer's own epoch for an exploration the host ordered, stamped when the order LANDED.
        /// Dropped when the exploration ends, so a vehicle that is not exploring holds no entry.</summary>
        private static readonly Dictionary<GeoVehicle, TimeUnit> _localExplorationStart =
            new Dictionary<GeoVehicle, TimeUnit>();

        /// <summary>
        /// A MIRRORED TIMESTAMP IS ONLY MEANINGFUL IN A MIRRORED EPOCH, and this one is not in one.
        /// <c>StartedExplorationAt = base.Timing.Now</c> (GeoVehicle.cs:423) reads the PER-ACTOR clock:
        /// every <c>ActorComponent</c> gets its own <c>new Timing()</c> (ActorComponent.cs:85) whose
        /// <c>StartTime</c> is never set — only the LEVEL's is (GeoLevelController.cs:571) — so
        /// <c>Now = StartTime + OwnNow</c> (Timing.cs:55) is that actor's AGE, counted from the instant its
        /// <c>ParentTime</c> was assigned (Timing.cs:148) and rooted in the process's own
        /// <c>Time.time</c> (Timing.cs:176). The rail mirrors the LEAF (rail-baseline.txt:727/:753) and
        /// DELIBERATELY EXCLUDES the clock it is measured in (rail-baseline.txt:33 — "per-actor clock …
        /// client clocks tick locally, the level clock rides the TimeAnchor 'TA' root"). So the host's
        /// number and this peer's <c>Timing.Now</c> are two different epochs, and the skew between them is
        /// unbounded: live 2026-08-08, both clients printed a mirrored start of 01:06:09.428 against a local
        /// clock of 06:26:33.428 — five hours of nothing but epoch. That is the same class of bug the
        /// level-vs-actor comment in <see cref="ReseedExploration"/> already fixed once, one level up.
        ///
        /// So the VALUE never decides anything; only its ARRIVAL does, and the answer is stamped in the
        /// epoch this peer can actually read:
        ///   • the host's order just landed → stamp NOW. The client's timer therefore starts LATER than the
        ///     host's by the wire delay and runs the identical def-fixed duration, so it can only ever
        ///     finish AFTER the host's. A client is structurally incapable of completing an exploration the
        ///     host has not — which is the property, not the arithmetic, that keeps the host sole author.
        ///   • already stamped → keep it, or an unrelated order delta would restart the fill.
        ///   • no stamp but the handle is already running (the join case: a mid-exploration aircraft arrives
        ///     through the save transfer, law 1b, never as a delta) → ADOPT the running handle with a local
        ///     epoch rather than tear it down. Worst case its bar runs one full duration long; it still
        ///     cannot finish early, and the host's own DONE cuts it short.
        ///   • otherwise ZERO, which <see cref="ShouldBeExploring"/> reads as "not exploring". A vehicle that
        ///     explored once keeps a non-zero mirrored leaf FOREVER (nothing clears it — neither
        ///     EndExploreCurrentSite:460 nor SiteExplorationCompleted:474), so falling back to it here is
        ///     precisely how a client would mint a phantom exploration at the next site it parks at.
        /// Pure so RailCheck L290 can execute it case by case.
        /// </summary>
        internal static TimeUnit ExplorationStartInLocalEpoch(bool hostStartLanded, bool alreadyExploring,
                                                              bool haveStamp, TimeUnit stamp, TimeUnit localNow)
        {
            // THE ARRIVAL EDGE OUTRANKS THE STAMP, and the order of these two lines is the whole fix
            // (client bug 2026-08-14: the SECOND and every later exploration by the same aircraft never
            // showed a ring). The stamp is dropped in exactly ONE place — the teardown branch in
            // `ReseedExploration` — but the client's own timer normally expires by ITSELF
            // (SiteExplorationCompleted:474 -> EndExploreCurrentSite:460), leaving `IsExploringSite=false`
            // while the stamp survives; the next batch then finds `should == IsExploringSite` and returns
            // early without ever clearing it. From then on every fresh host order reused that DEAD stamp,
            // whose `end` is long past, so `ShouldBeExploring` said false and `ExploreCurrentSite` was
            // never invoked again. A landed `StartExplorationTime` arrival is a NEW order by construction
            // (GenericApplier:1258 records the edge only on a real leaf delta), so it must re-anchor.
            // (d) `fill-restarted` is untouched: an unrelated order leaf lands with hostStartLanded=false
            // and still returns the stamp.
            if (hostStartLanded) return localNow;
            if (haveStamp) return stamp;
            if (alreadyExploring) return localNow;
            return TimeUnit.Zero;
        }

        private static void ReseedExploration(GeoVehicle v, bool hostStartLanded)
        {
            if (v == null || ExplorationStartField == null ||
                ExploreCurrentSiteMethod == null || EndExploreCurrentSiteMethod == null) return;
            // THE ACTOR'S OWN CLOCK, not the level's — and this one line is why the client's fill never
            // appeared (live 2026-08-04, client Player.log): every ActorComponent gets `Timing = new Timing()`
            // parented to the level's (Base.Entities/ActorComponent.cs:85/:89), and Timing.Now is
            // `StartTime + OwnNow` (Base.Core/Timing.cs:55) — a PER-ACTOR epoch, not the campaign date.
            // `StartedExplorationAt = base.Timing.Now` (GeoVehicle.cs:423) and both consumers,
            // `ExploreCurrentSite`'s `end - base.Timing.Now`:449 and `SetProgression(start, end, base.Timing)`:456,
            // read that same actor clock. Asking GeoLevel.Timing compared two different epochs — the log read
            // `mirrored start 36.08:16:30 … the clock (747322.13:10:33) is already past`, i.e. ~36 days of actor
            // life against the level's absolute datetime — so `timing.Now < end` was false for EVERY vehicle,
            // forever, and `should` could never be true. RailCheck L77 holds the two apart by name.
            var timing = v.Timing;
            if (timing == null) return;
            var site = v.CurrentSite;
            // FORENSICS ONLY — the host's own number, in the HOST's per-actor epoch. It is printed below so a
            // log still names both timestamps, and it is passed to NOTHING that decides.
            var mirrored = (TimeUnit)ExplorationStartField.GetValue(v);
            bool haveStamp = _localExplorationStart.TryGetValue(v, out var stamped);
            var start = ExplorationStartInLocalEpoch(hostStartLanded, v.IsExploringSite, haveStamp, stamped, timing.Now);
            // OVERWRITE, never preserve: on the arrival edge `start` is a fresh local anchor and the old
            // entry is exactly the stale value that killed the re-seed. Writing unconditionally keeps the
            // map in step with the answer above.
            if (start > TimeUnit.Zero) _localExplorationStart[v] = start;
            var end = site == null ? TimeUnit.Zero : start + site.ExplorationTime;
            bool inspected = site != null && v.Owner != null && site.GetInspected(v.Owner);
            bool should = site != null && ShouldBeExploring(UnderTravelOrder(v), inspected, start,
                                                            site.ExplorationTime, timing.Now);
            if (should == v.IsExploringSite)
            {
                // SILENT AGREEMENT IS THE ONE CASE THIS SEAM COULD NOT EXPLAIN (user report 2026-08-01 item 4:
                // "the exploration progress bar advances only on the host"). The whole session's logs held NOT
                // ONE exploration line — neither the re-seed nor a failure — because both the act and the
                // refusal are silent here, and "no line" is exactly as consistent with "nobody explored" as
                // with "the order never arrived". So the INTERESTING disagreement now says which clause
                // decided it: this peer is PARKED at a site that is explorable and not yet inspected — i.e.
                // the player is looking at a spinner — and the mirrored order still says "not exploring".
                // Log-once per (vehicle, reason): exploration is a per-site event, not a per-frame one.
                // AGREEMENT IS NOT A GAP, and the zero-start arm could only ever report agreement. With no
                // stamp, no host edge and nothing running here, the mirrored order says "not exploring" and
                // so does this peer — the ordinary state of an aircraft PARKED at a site it has not been
                // told to explore. Nothing on the wire can distinguish that from a lost order either: there
                // is no is-exploring leaf (rail-baseline.txt GeoVehicle rows), only the StartExplorationTime
                // ARRIVAL edge, so the arm asserted a loss it had no evidence for. Live 2026-08-13: 8 such
                // warnings (4 per client) while BOTH of the host's real explore orders re-seeded correctly
                // in the same logs — a warning that fires on the normal case buries the two arms below,
                // which do have evidence (an order in flight, or a start this peer itself stamped).
                if (!should && site != null && site.ExplorationTime != TimeUnit.Zero && !inspected &&
                    (start > TimeUnit.Zero || UnderTravelOrder(v)))
                {
                    string why = UnderTravelOrder(v) ? "the mirrored order still holds a destination (in flight)"
                               : "the local start " + start + " this peer stamped when the host's order landed, " +
                                 "plus the site's def-fixed ExplorationTime, ends at " + end + ", which the local " +
                                 "clock (" + timing.Now + ") is already past — a STALE start, so the host's DONE " +
                                 "(SetInspected on FactionsData) never arrived";
                    LogMissOnce("exploration NOT re-seeded for " + (IdentityResolver.RootRef(v) ?? "V#?") +
                                " parked at " + (IdentityResolver.RootRef(site) ?? "S#?") + " — " + why +
                                " [host's own StartExplorationTime leaf reads " + mirrored + ", in the HOST's " +
                                "per-actor epoch — not comparable to this clock, rail-baseline.txt:33]");
                }
                return; // the live handle already matches the mirrored order
            }

            var root = IdentityResolver.RootRef(v) ?? "V#?";
            if (should)
            {
                ExploreCurrentSiteMethod.Invoke(v, new object[] { start, end }); // ProcessInstanceData:1126
                MpLog.Log("[Multiplayer][rail] exploration re-seed " + root + " → started " + start +
                          ", ends " + end + " (LOCAL epoch, stamped when the host's order landed; the host's own " +
                          "leaf reads " + mirrored + " in its own per-actor epoch. Progress derived locally, " +
                          "outcome stays host-only — this timer cannot finish before the host's)");
            }
            else
            {
                // THE GAME'S OWN COMPLETION TEARDOWN, not a hand-rolled one: EndExploreCurrentSite:460 is
                // exactly what SiteExplorationCompleted:473 calls when the timer runs out naturally, so
                // forcing it here ends the local presentation the same way the host's did — the spinner is
                // destroyed at the AUTHORITATIVE instant instead of finishing on this peer's own clock.
                EndExploreCurrentSiteMethod.Invoke(v, null);
                _localExplorationStart.Remove(v);   // the exploration is over; the next one stamps afresh
                MpLog.Log("[Multiplayer][rail] exploration re-seed " + root + " → cleared (" +
                          (inspected ? "the site is now INSPECTED on the host — authoritative DONE forced this " +
                                       "peer's in-flight spinner to completion rather than letting it run out locally"
                                     : "the mirrored order no longer says exploring") + ")");
            }
        }

        /// <summary>Replay the game's OWN two re-derivations, chosen by the mirrored order:
        /// <c>GeoVehicle.OnLevelStart</c>:385-388 when the order says "flying" (the path is rebuilt from the
        /// destination SITES and <c>CalculatePath</c>:68-84 re-seeds leg 1 from the actor's current
        /// WorldPosition, so a client that drifted converges instead of snapping), and the placement half of
        /// <c>TeleportToSite</c>:510/:513 when it says "parked". The parked arm is the SEED the structural
        /// create needs (a rail-created aircraft has no pose leaf to land on any more) and the drift
        /// corrector at journey's end, and it is exactly the two lines of that method which are NOT gameplay:
        /// :506/:507 (EndCollecting/EndExplore), :509 (_destinationSites.Clear), :511 (CurrentSite), :512
        /// (SetVisible), :514 (VehicleArrived) and :515 (OnArrivedAtDestination) are all either host outcomes
        /// (law 3) or rail-covered leaves that arrive on their own.</summary>
        /// <summary>
        /// THE mirrored "this aircraft is under an order to fly", and deliberately NOT
        /// <c>GeoVehicle.Travelling</c> — which is a LOCALLY WRITTEN flag on every peer and therefore
        /// says nothing about the host's order.
        ///
        /// <c>Travelling</c> has no gameplay writer at all: the only ones are the client's OWN
        /// <c>GeoNavComponent.NavigateRoutine</c> (GeoNavComponent.cs:88/:94 true, and :138 FALSE at the end
        /// of every leg) plus the save DTO (GeoVehicle.cs:1085). <see cref="VehicleArrivalGate"/> blocks
        /// <c>GeoVehicle.OnArrived</c>, i.e. the <c>Arrived?.Invoke</c> at :139 — it does NOT and cannot
        /// block the <c>Travelling = false</c> one line above it. So the moment a client's own re-derived
        /// leg finishes (it runs ahead of or behind the host's, by design — the whole point of the pose
        /// opt-out), that client's <c>Travelling</c> reads FALSE while the host's aircraft is still in the
        /// air. The rail cannot correct it: the diff ships CHANGE, and on the host nothing changed.
        ///
        /// A MID-FLIGHT REDIRECT then stalls, permanently and SILENTLY (live 2026-08-01, 16:05:35 —
        /// V#1 → S#550 applied on the host, no `nav re-seed` line on EITHER client, aircraft frozen for
        /// 45 s until the host's real arrival snapped it): the host re-orders a vehicle that was ALREADY
        /// travelling, so `Travelling` is not in the delta, only `DestinationSites` is; the old flying arm
        /// asked the falsified local flag, said "not flying", fell through to the parked arm, found
        /// `CurrentSite` null (the setter :212-216 nulls it on departure) and did nothing — no motion, no
        /// log, and the memo cleared on the way past.
        ///
        /// <c>DestinationSites</c> is the order and is a PURE mirror on a client: the only writers are
        /// <c>StartTravel</c>:526-527, <c>TeleportToSite</c>:509 and the pop in <c>OnArrived</c>:340 —
        /// host-side every one of them, since the client's <c>StartTravel</c> is blocked block-first
        /// (VehicleSync.CaptureTravel) and its <c>OnArrived</c> is gated. Empty = parked, non-empty = an
        /// outstanding order, which is exactly what the host's own two arms mean.
        /// </summary>
        private static bool UnderTravelOrder(GeoVehicle v)
        {
            var dest = v == null ? null : v.DestinationSites;
            return dest != null && dest.Count > 0;
        }

        private static void ReseedNavigation(GeoVehicle v)
        {
            if (v == null || v.Navigation == null) return;
            var root = IdentityResolver.RootRef(v);
            var dest = v.DestinationSites;

            if (UnderTravelOrder(v))
            {
                var route = new GeoSite[dest.Count];
                for (int i = 0; i < dest.Count; i++)
                {
                    if (dest[i] == null) return; // an unresolved destination: wait for the resend, never fly a null leg
                    route[i] = dest[i];
                }
                // The host's own two arms say this aircraft is flying (see UnderTravelOrder), so re-assert the
                // covered leaf the client's OWN NavigateRoutine falsified at :138 when its re-derived leg ended
                // early. Nothing changed on the host, so the diff can never carry the correction — this is one
                // of the leaves the CRC backstop reported permanently DIVERGED.
                if (!v.Travelling) v.Travelling = true;
                if (root != null && _seededRoute.TryGetValue(root, out var last) && IsSuffixOf(last, route))
                    return;                      // same route, a waypoint consumed — the client is already flying it
                var path = new List<Vector3>(route.Length);
                foreach (var d in route) path.Add(d.WorldPosition);
                AnchorToHostDeparture(v, root, path); // BEFORE Navigate: CalculatePath:69-72 reads WorldPosition
                v.Navigation.Navigate(path);     // GeoVehicle.OnLevelStart:388
                if (root != null) _seededRoute[root] = route;
                MpLog.Log("[Multiplayer][rail] nav re-seed " + (root ?? "?") + " → " + route.Length + " leg(s)");
                return;
            }

            // The client's ARRIVAL EDGE, and the only one it has: the mirrored order says "parked" for an
            // aircraft this client was actively flying a re-seeded route for. Once per journey by
            // construction — the route memo is written only by the flying arm above and removed here.
            bool wasFlying = root != null && _seededRoute.Remove(root);
            if (v.CurrentSite != null)
            {
                // THE ARRIVAL SNAP, and the reason the parked arm is not merely a pose write. The client
                // flies the leg itself off its OWN start time — it re-seeds when the host's order delta
                // LANDS, so it departs a rail round-trip late and stays that far behind for the whole
                // flight (game-time lag = that latency x Timing.Scale, which at geoscape speed is
                // minutes). The host therefore ARRIVES FIRST, and its arrival pauses the shared clock for
                // the site interaction (GeoscapeView.SetGamePauseState -> the "T" leaves) — at which point
                // NavigateRoutine's num = totalTime.Ratio01(startTime, Timing.Now) stops advancing
                // (GeoNavComponent.cs:104) and the client's aircraft hangs in mid-air, permanently, having
                // "never reached the site". Worse, that still-running coroutine rewrites
                // PivotTransform.localRotation from its own Slerp EVERY FRAME (:111), so the two placement
                // lines below were overwritten the very next frame and this arm did nothing at all.
                // Cancelling is the game's own move at the same point: TeleportToSite:508 cancels the
                // navigation before it writes the pose, for exactly this reason. Guarded on IsNavigating
                // (NavigationComponent.cs:35) so the ordinary parked re-seed — a create's placement seed,
                // a baseline — does not poke ActionComponent.CancelAction with a null action.
                if (v.Navigation.IsNavigating) v.Navigation.CancelNavigation();               // TeleportToSite:508
                v.PivotTransform.localRotation = v.CurrentSite.PivotTransform.localRotation; // TeleportToSite:510
                if (v.Animator != null) v.Animator.SetInteger("State", 0);                   // :513 — the landing
                                                                                             // half of the pose the
                                                                                             // client's own nav set
                                                                                             // via InitiateTravelling
                if (wasFlying) RaiseArrivedForUi(v);
            }
        }

        private static readonly FieldInfo ViewArrivedEvent =
            AccessTools.Field(typeof(PhoenixPoint.Geoscape.View.GeoscapeView), "FactionVehicleArrived");

        /// <summary>
        /// Law 11 at the arrival edge, in its ORIGINAL phrasing — "fire the native event the view already
        /// listens to" — and NOT a repaint: what the game does on arrival is OPEN a popup, which no repaint
        /// entry may ever do (it would re-open on every rail batch the player dismissed it after).
        ///
        /// <c>VehicleArrivalGate</c> (ClientSimGate.cs:222) skips <c>GeoVehicle.OnArrived</c> WHOLE on a
        /// client, correctly — its body is the authoritative arrival. But the last two lines of that body
        /// are not: <c>CurrentSite.VehicleArrived</c>:347 and <c>OnArrivedAtDestination</c>:348 are the
        /// NOTIFICATION, and losing them is why a client's site panel had no Explore button. Native, the
        /// chain <c>ArrivedAtDestinationEvent</c> → <c>VehicleFactionController.OnVehicleArrived</c>:126 →
        /// <c>GeoFaction.OnVehicleArrived</c>:1875 → <c>GeoscapeView.OnFactionVehicleArrived</c>:1629 ends
        /// at <c>UIStateVehicleSelected.OnVehicleArrived</c>:1180-1192, which calls
        /// <c>UpdateReachableSitesMarkers</c> + <c>UpdateVehicleActions</c> + <c>ShowContextualMenu</c> —
        /// the auto-opened site menu carrying "Explore (Xh)". Without it the client player falls back to
        /// the native CLICK path, and that path natively needs TWO clicks:
        /// <c>ShowBaseInfoCrt</c>:775-779 `yield break`s on the first one when the site was not already the
        /// hovered one. Exactly the symptom reported.
        ///
        /// ENTERED AT THE PRESENTATION BOUNDARY, one link down from the gameplay: the raise is on
        /// <c>GeoscapeView.FactionVehicleArrived</c> (GeoscapeView.cs:201, raised :1631), NOT on
        /// <c>GeoFaction.OnVehicleArrived</c>, whose body FIRST does SetInspected / UpdateVehicleSite /
        /// Refill / EngageEnemyAircraftOnSite (GeoFaction.cs:1877-1897) — authoritative state a projector
        /// may not mint (law 3). Its only two subscribers in the assembly are
        /// <c>UIStateVehicleSelected</c>:146 and <c>GeoscapeSound</c>:35: UI and audio, nothing else.
        /// Inside <see cref="SyncApplyScope"/> (law 8) because the handler runs native UI code.
        /// </summary>
        private static void RaiseArrivedForUi(GeoVehicle v)
        {
            var view = v.GeoLevel == null ? null : v.GeoLevel.View;
            var del = view == null || ViewArrivedEvent == null ? null : ViewArrivedEvent.GetValue(view) as Delegate;
            if (del == null) return; // nobody subscribed — no open geoscape screen to notify
            try
            {
                using (SyncApplyScope.Enter()) del.DynamicInvoke(v, false); // justPassing:false — the order is done
            }
            catch (Exception ex)
            {
                LogMissOnce("arrival notify failed for " + (IdentityResolver.RootRef(v) ?? "V#?") + ": " + ex.Message);
            }
        }

        /// <summary>Is <paramref name="cur"/> the tail of <paramref name="last"/>? Reference equality on the
        /// live sites — the rail resolves both to the client's own instances. Typed <c>object[]</c> so the
        /// rule is checkable headlessly (RailCheck L43 arm 3): array covariance keeps the GeoSite[] call
        /// sites unchanged, and reference identity is all this ever compares.</summary>
        private static bool IsSuffixOf(object[] last, object[] cur)
        {
            if (last == null || cur == null || cur.Length == 0 || cur.Length > last.Length) return false;
            int off = last.Length - cur.Length;
            for (int i = 0; i < cur.Length; i++)
                if (!ReferenceEquals(last[off + i], cur[i])) return false;
            return true;
        }

        /// <summary>Resolve a live instance for an order-vector key that is missing from an ALIAS
        /// collection: scan the OTHER keyed collections of the SAME owner entity (the alias pattern —
        /// the queue's elements live in the catalog next door). Type-gated per element so an id-keyed
        /// stranger can never be adopted into a foreign list. Full live-type table, not the wire kind's:
        /// the owner may be a subtype of the wire type.</summary>
        private static object ResolveSiblingElement(object entity, RailField field, string key)
        {
            var rt = RailType.Get(entity.GetType());
            if (rt == null) return null;
            foreach (var f in rt.Fields)
            {
                if (f.Class != FieldClass.EntityCollection || !f.CanRead ||
                    string.Equals(f.Name, field.Name, StringComparison.Ordinal)) continue;
                object v;
                try { v = f.GetValue(entity); } catch { continue; }
                if (!(v is IEnumerable src)) continue;
                foreach (var e in src)
                    if (e != null && field.ElemType.IsInstanceOfType(e) &&
                        string.Equals(IdentityResolver.KeyOf(e), key, StringComparison.Ordinal))
                        return e;
            }
            return null;
        }

        /// <summary>
        /// UNIVERSAL no-op test — the gate between "a delta arrived" and "something on this client changed".
        /// Encodes what the client ALREADY holds with the SAME canonical codec the host used to produce the
        /// wire value, then compares bytes. Identical ⇒ the entry is dropped: not written, not added to
        /// <c>touched</c>, so it drives neither <see cref="UiEventMap"/> nor <see cref="OpenUiRepaint"/>.
        /// That is what makes law 11 fire on real CHANGE instead of on traffic — a redelivered packet or a
        /// resync full-resend (host re-emits every covered pair, all of them already equal here) used to
        /// force a full Exit→Enter screen rebuild per batch.
        ///
        /// Symmetric by construction, not by a table: the host's own "changed?" question is byte inequality
        /// of these same encoders (DiffEngine snapshot), so no field/kind can be classified here that the
        /// host classifies differently, and no per-kind allowlist exists to drift.
        ///
        /// Encoder throws, or a shape this codec cannot restate ⇒ CHANGED. Applying a value that was already
        /// equal is idempotent (law 7) and costs one repaint; wrongly skipping one strands a stale screen,
        /// which law 11 forbids. The asymmetry is deliberate — the fallback is always "repaint".
        /// </summary>
        private static bool Unchanged(object entity, RailField field, string subKey, byte[] value)
        {
            try
            {
                switch (field.Class)
                {
                    case FieldClass.Leaf:
                    case FieldClass.LeafList:
                        return SameBytes(value, RailMeta.EncodeFieldValue(field, field.GetValue(entity)));
                    case FieldClass.EntityList:
                        return SameBytes(value, RailMeta.EncodeEntityList(field, field.GetValue(entity)));
                    case FieldClass.EntityCollection:
                    {
                        // Order vector: restate the local live key sequence with the host's own encoder.
                        // Unkeyable local element → null → "changed" (ReorderByKeys copes per element).
                        var mine = RailMeta.EncodeKeyOrderOf(field.GetValue(entity));
                        return mine != null && SameBytes(value, mine);
                    }
                    case FieldClass.LeafDict:
                    {
                        if (!(field.GetValue(entity) is IDictionary dict)) return false;
                        var key = RailMeta.DecodeDictKey(subKey, field.KeyType);
                        bool tomb = value.Length == 1 && value[0] == RailMeta.DictTombstone;
                        // No null-key arm: DecodeDictKey throws on a malformed key, it never returns null.
                        if (!dict.Contains(key)) return tomb; // absent: a delete is the no-op
                        return !tomb && SameBytes(value, RailMeta.EncodeFieldValue(field, dict[key]));
                    }
                    case FieldClass.GeoItemDict:
                    {
                        if (!(field.GetValue(entity) is IDictionary dict)) return false;
                        var def = GeoItemCodec.ResolveDef(subKey);
                        bool tomb = value.Length == 1 && value[0] == RailMeta.DictTombstone;
                        if (def == null || !dict.Contains(def)) return tomb;
                        return !tomb && SameBytes(value, GeoItemCodec.Encode(dict[def]));
                    }
                    default:
                        return false;
                }
            }
            catch { return false; }
        }

        /// <summary>Prune local dict keys absent from the host census. Removes EXTRAS only — keys the host
        /// holds arrive as ordinary value entries in the same forced batch, so applying a census twice is a
        /// no-op (law 7). Direct dict.Remove, same as the tombstone path (never RemoveItem — side effects).</summary>
        private static void ApplyDictCensus(object entity, RailField field, string path, byte[] value, HashSet<object> touched)
        {
            if (!(field.GetValue(entity) is IDictionary dict)) return;
            var present = new HashSet<string>(RailMeta.DecodeDictCensus(value), StringComparer.Ordinal);
            List<object> extras = null;
            List<string> extraSubs = null;
            foreach (var k in dict.Keys)
            {
                var sub = field.Class == FieldClass.GeoItemDict ? GeoItemCodec.SubKey(k) : RailMeta.EncodeDictKey(k);
                if (present.Contains(sub)) continue;
                if (extras == null) { extras = new List<object>(); extraSubs = new List<string>(); }
                extras.Add(k);
                extraSubs.Add(sub);
            }
            if (extras == null) return;
            foreach (var k in extras) dict.Remove(k);
            touched.Add(entity);
            // NAME them. This store is the SHARED Phoenix inventory as often as it is anything else
            // (F#…ItemStorage._storageItems), so "pruned 2 phantom dict keys" threw away the only evidence
            // of WHICH two items the peers disagreed about — and the prune is where that evidence dies.
            // The sub-keys are already in hand from the loop above; printing them costs nothing.
            MpLog.Log("[Multiplayer][rail] GenericApplier: census pruned " + extras.Count + " phantom dict key(s) at " +
                      path + "." + field.Name + ": " + string.Join(", ", extraSubs));
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>First sighting of a message prints; every later one is COUNTED and reported by
        /// <see cref="RailMeta.FlushMissDigest"/>. The old version kept a bare HashSet, so all thirteen
        /// "dto-twin gap" families, both "no native wiring" lines and every "entity not found" reported
        /// exactly 1 for a whole session however often they really fired — the quietness was fine, the
        /// unknowability was not.</summary>
        private static void LogMissOnce(string msg)
        {
            var line = "[Multiplayer][rail] GenericApplier: " + msg;
            if (RailMeta.CountMiss(line)) MpLog.LogWarning(line);
        }

        /// <summary>Whole-segment path-prefix match — the same rule the host's forced scope uses
        /// (DiffEngine.PrefixMatchOne), so "S#29" never claims "S#293"'s answer.</summary>
        private static bool UnderRoot(string path, string root) =>
            !string.IsNullOrEmpty(root) && path != null && path.Length >= root.Length &&
            string.CompareOrdinal(path, 0, root, 0, root.Length) == 0 &&
            (path.Length == root.Length || path[root.Length] == '.');

        /// <summary>The pending scoped root <paramref name="path"/> sits under, or null.</summary>
        private static string PendingRootOf(string path)
        {
            foreach (var k in _pendingScoped.Keys) if (UnderRoot(path, k)) return k;
            return null;
        }

        /// <summary>An entry under a root this peer asked to have re-sent that leaves the mirror holding
        /// the HOST's value IS the acknowledgement — there is no other one on the wire, and the host's
        /// answer is an ordinary delta by design (DiffEngine.ForceReemit). Both ways of holding it count:
        /// an entry that WROTE, and an entry whose bytes already equalled the mirror. The outcome asked
        /// for is the mirror's contents, not a write.</summary>
        private static void NoteScopedAnswer(string path)
        {
            // A FULL resend has no scope to match, so ANY entry that lands is its answer — that is what
            // "resend everything" asked for and there is no other acknowledgement on the wire.
            if (_pendingFullAt >= 0f)
            {
                _pendingFullAt = -1f;
                MpLog.Log("[Multiplayer][rail] GenericApplier: full resend ANSWERED — the mirror holds the " +
                          "host's entry at " + path);
            }
            var hit = PendingRootOf(path);
            if (hit == null) return;
            _pendingScoped.Remove(hit);
            _scopedDropN.Remove(hit); _scopedDropWhy.Remove(hit);
            MpLog.Log("[Multiplayer][rail] GenericApplier: scoped resend of root '" + hit +
                      "' ANSWERED — the mirror holds the host's entry at " + path);
        }

        /// <summary>Referrer path of a dropped reference — WHERE it was dropped, not what it pointed at:
        /// the recovery re-emits the referrer, while the decoder that saw the unresolvable key is three
        /// layers down and the entry that carried it is already gone.</summary>
        private static void NoteRefDrop(string path)
        {
            if (!string.IsNullOrEmpty(path)) _refDropPaths.Add(path);
        }

        /// <summary>WHICH SCOPE a structural create's backfill must ask the host to re-emit — pure, so
        /// RailCheck L335 can execute it case by case.
        ///
        /// THE CREATED ROOT IS NEVER THE ANSWER. What a create leaves broken is not the new entity: the
        /// create blob delivered its whole graph byte for byte (19288 B for `U#6`). It is every entry that
        /// ALREADY shipped a REFERENCE to it and had that reference dropped for being unresolvable — an
        /// Unresolved leaf, a hole removed from an entity list, an unresolved order-vector member — and
        /// those entries live on the REFERRER. The host re-emits only on CHANGE and its own value did not
        /// change, so nothing will ever restate them unless this asks for them by name.
        ///
        /// Measured: in the 2026-08-07 session all four backfills asked for the root just created
        /// (U#6/U#7/U#8 the rescued soldiers, U#9 the faction-base recruit), the host answered every one
        /// (host log :5030 `SCOPED re-emit of root 'U#9'`), and not one arriving entry differed from the
        /// mirror — while the leaf that had dropped `U#6` 0.3 s BEFORE the create (client log :2528) was
        /// never re-emitted at all and stayed dropped for the rest of the session.
        ///
        /// A recorded path UNDER the created root is skipped: the create blob just restated it.</summary>
        internal static List<string> BackfillScopes(string createdRoot, IEnumerable<string> refDropPaths)
        {
            var scopes = new List<string>();
            if (refDropPaths == null) return scopes;
            foreach (var p in refDropPaths)
                if (!string.IsNullOrEmpty(p) && !UnderRoot(p, createdRoot) && !scopes.Contains(p))
                    scopes.Add(p);
            return scopes;
        }

        /// <summary><see cref="LogMissOnce"/> for a discard that happened at a PATH: same counted line, plus
        /// the one fact the 10 s give-up could never state — that the answer to a scoped request did arrive
        /// and died on this side, how often, and why the first one did.</summary>
        private static void LogMissDrop(string path, string msg)
        {
            LogMissOnce(msg);
            if (_pendingScoped.Count == 0) return;
            var hit = PendingRootOf(path);
            if (hit == null) return;
            _scopedDropN.TryGetValue(hit, out int n);
            _scopedDropN[hit] = n + 1;
            if (!_scopedDropWhy.ContainsKey(hit)) _scopedDropWhy[hit] = msg;
        }

        /// <summary>The other half: a request nobody answered must say so, or "the resend never came" and
        /// "it came and applied silently" stay indistinguishable — which is exactly what four unacknowledged
        /// backfill requests looked like in the 2026-08-07 session.</summary>
        /// <summary>The give-up LINE, as a pure decision so the outcome can be asserted headless (RailCheck
        /// L368) rather than only observed in a live session. "" = still inside the deadline, say nothing.
        /// <paramref name="root"/> null = the rootless FULL request.</summary>
        internal static string GiveUpLine(string root, float now, float deadline, int dropped, string why)
        {
            if (now < deadline) return "";
            return (root == null ? "full resend" : "scoped resend of root '" + root + "'") +
                   " went UNANSWERED for " + ScopedAnswerDeadlineSec + "s. This peer's mirror " +
                   (root == null ? "stays as it was — and this is the request it sends when it cannot even " +
                                   "NAME what it lost (a seq gap, a torn batch, an unknown kind), so what is " +
                                   "missing is unknown as well. "
                                 : "of that root stays as it was. ") +
                   (dropped > 0
                       ? dropped + " entr" + (dropped == 1 ? "y" : "ies") + " under it DID arrive and were " +
                         "discarded here — first: " + why
                       : "Nothing arrived at all, so the answer never reached this peer (or the host's scope " +
                         "matched no covered path — it says so).");
        }

        private static void ExpireScopedRequests()
        {
            float now = Time.realtimeSinceStartup;
            if (_pendingFullAt >= 0f)
            {
                var full = GiveUpLine(null, now, _pendingFullAt, 0, null);
                if (full.Length > 0)
                {
                    _pendingFullAt = -1f;
                    MpLog.LogWarning("[Multiplayer][rail] GenericApplier: " + full);
                }
            }
            List<string> dead = null;
            foreach (var kv in _pendingScoped)
                if (now >= kv.Value) (dead ?? (dead = new List<string>())).Add(kv.Key);
            if (dead == null) return;
            foreach (var k in dead)
            {
                _pendingScoped.Remove(k);
                _scopedDropN.TryGetValue(k, out int dropped);
                _scopedDropWhy.TryGetValue(k, out var why);
                _scopedDropN.Remove(k); _scopedDropWhy.Remove(k);
                MpLog.LogWarning("[Multiplayer][rail] GenericApplier: " + GiveUpLine(k, now, 0f, dropped, why));
            }
        }

        /// <summary>Law-7 drift backstop, client half — the ONE thing in the rail that ever compares host and
        /// client state. Once a second, CRC exactly ONE root subtree of our own mirror with the SAME canonical
        /// walk the host emits from (<see cref="DiffEngine.RootCrc"/>) and report it with the seq we have
        /// applied, so the host can tell divergence from lag. Rotating one root per second: a full sweep costs
        /// one host walk spread over ~N seconds instead of a graph hash per tick (the host walk cost is what
        /// caused the rhythmic freezes) — a backstop's job is to notice within a minute, not within a frame.
        /// Why it must exist: the host diff compares host-NOW to host-BEFORE, so nothing the host DELETES ever
        /// reaches us — a vanished path emits no entry and no tombstone. Only a subtree compare can see it.</summary>
        /// <summary>How many peers the host last told us about (PEER_LIST), floored at 1. The ONE number the
        /// client half needs to keep the host's aggregate backstop cost independent of the roster.</summary>
        private static float RosterScale(NetworkEngine engine)
        {
            try
            {
                int n = engine?.Session?.GetLobbyRoster()?.Count ?? 0;
                return n < 1 ? 1f : n;
            }
            catch { return 1f; }
        }

        /// <summary>A batch arrived while this peer had NO STARTED GEOSCAPE LEVEL, so it was thrown away.
        /// Set at the two <see cref="StartedGeoLevel"/> returns, cleared by
        /// <see cref="ClientMissedBatchTick"/> once the level is live.
        ///
        /// WHY IT HAS TO EXIST AT ALL. Those two returns used to be a SILENT swallow: no line, no counter,
        /// and — because <c>_lastSeq</c> is marked only AFTER the guard — no seq movement either, so the loss
        /// stayed invisible until some unrelated later batch tripped the gap check and named a range nobody
        /// could explain. Both mission returns of the 2026-08-08 session measured the same shape: the host
        /// was back on the geoscape 50-77 s before the client finished loading, and EVERY batch of that
        /// window died here — <c>seq gap (200→249)</c> = 49 batches, then <c>seq gap (579→629)</c> = 50. The
        /// host's own log agrees from the other side: 13 CRC DIVERGED for that client, all clustered right
        /// after the first tac→geo return.
        ///
        /// AND NOTHING WOULD EVER HAVE ASKED FOR THEM BACK. Every other <c>RequestResync</c> call site is
        /// REACTIVE — it needs an inbound message to notice anything — and the peer's problem here is
        /// precisely that it received nothing it could keep. The level going live is the one edge that is not
        /// a message, so it is the one thing that can close this. No wire byte is added: the request is the
        /// existing law-7 one and the host answers it with the existing <c>DiffEngine.RequestFullResend</c>.
        ///
        /// One flag, not a count: the answer to one lost batch and to fifty is the same full resend.</summary>
        private static bool _missedNoLevel;

        private static void MissedNoLevel()
        {
            if (_missedNoLevel) return;   // one line per window, not one per discarded batch
            _missedNoLevel = true;
            MpLog.LogWarning("[Multiplayer][rail] GenericApplier: a host batch was DISCARDED — this peer has " +
                             "no STARTED geoscape level yet (mid-load: the level object may already exist " +
                             "while its graph is still being built). It will ask for the whole state back " +
                             "the moment the level is live; until then every further batch is dropped silently.");
        }

        /// <summary>The one non-reactive resync trigger: THE LEVEL BECAME LIVE. Driven from SyncEngine.Tick
        /// just BEFORE <c>ReplenishSync.ClientArrivalTick</c> on purpose — the resend has to be in flight
        /// before the resupply re-ask starts polling <c>GetMissingItems()</c>, or the poll spends its whole
        /// bounded window asking about state that is still sitting on the host.</summary>
        internal static void ClientMissedBatchTick(NetworkEngine engine)
        {
            if (!_missedNoLevel) return;
            if (engine == null || engine.IsHost || !engine.IsActiveSession) { _missedNoLevel = false; return; }
            if (StartedGeoLevel() == null) return; // same predicate the discard used, or the resend lands in the hole it is answering
            // Cleared even when the throttle eats the request: a full resend that went out in the last 5 s
            // carries this peer's missing state too, which is the same reason the full window is global.
            _missedNoLevel = false;
            RequestResync(engine, "state arrived while this peer had no geoscape level");
        }

        public static void ClientCrcTick(NetworkEngine engine)
        {
            if (engine == null || engine.IsHost || !engine.IsActiveSession) return;
            // BEFORE the CRC's own interval gate: an unanswered request must be reported on its OWN
            // deadline, not on whenever the roster-scaled backstop next happens to come round.
            if (_pendingScoped.Count > 0 || _pendingFullAt >= 0f) ExpireScopedRequests();
            if (Time.realtimeSinceStartup < _crcNextAt) return;
            // ROSTER-SCALED interval (N=50 mandate). Every client reports on its own clock and the HOST pays
            // one subtree walk per report, so a fixed 1 s interval makes the host's backstop cost grow
            // linearly with the roster while each client's own cost stays flat — the peer that suffers is
            // the one that asked for nothing. Stretch the interval by the roster size the host already
            // broadcast to us (PEER_LIST → SessionManager.GetLobbyRoster), so the AGGREGATE inbound rate
            // stays ≈ one report per second whatever N is. Backstop semantics survive: at N=50 a root is
            // still swept well inside the "notice within a minute, not within a frame" contract because
            // the roots are swept round-robin across all peers, not by each peer alone.
            _crcNextAt = Time.realtimeSinceStartup + CrcInterval * RosterScale(engine);
            var geo = GeoLevel();
            if (geo == null) return;
            string key = null; object obj = null;
            int i = 0;
            foreach (var kv in IdentityResolver.Roots(geo, hostWalk: false))
                if (i++ == _crcRoot) { key = kv.Key; obj = kv.Value; break; }
            if (key == null) { _crcRoot = 0; return; } // swept every root — restart next tick
            _crcRoot++;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                uint crc = DiffEngine.RootCrc(key, obj);
                double crcMs = sw.Elapsed.TotalMilliseconds;
                // BUDGET (the gap L50 left: it budgets the HOST's RunSlice only, and this is the same
                // VisitEntity walk run client-side with nothing watching it). A whole root is hashed inside
                // ONE frame and cannot be sliced — a torn hash would false-alarm the backstop — so it is
                // RATE-limited instead: charge the measured cost against the same per-frame budget the host
                // walk obeys and push the next root out proportionally. Average cost then stays ≈
                // SliceBudgetMs per CrcInterval whatever shape the graph has, and a fat root (GL/F#/ES)
                // cannot come back a second later. Backstop semantics are unharmed — it notices within a
                // minute, not within a frame (see the summary above).
                if (crcMs > DiffEngine.SliceBudgetMs)
                    _crcNextAt = Time.realtimeSinceStartup +
                                 (float)(CrcInterval * RosterScale(engine) * crcMs / DiffEngine.SliceBudgetMs);
                RailCost.Charge("crc:" + key, crcMs);
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms, Encoding.UTF8))
                {
                    w.Write(DiffEngine.MsgCrcReport);
                    w.Write(key);
                    w.Write(crc);
                    w.Write(_lastSeq);
                    var env = SyncProtocol.EncodeEnvelope(SurfaceIds.GeoRail, SyncKind.ActionRequest, ms.ToArray());
                    engine.SendToHost(new NetworkMessage(PacketType.SyncEnvelope, env));
                }
                // Remember what we just asked about — the answer names the field (see ApplyEntry).
                _crcReportedRoot = key;
                _crcReportedAt = Time.realtimeSinceStartup;
            }
            catch (Exception ex) { MpLog.LogError("[Multiplayer][rail] CRC report for '" + key + "' failed: " + ex.Message); }
        }

        /// <summary>MAY THIS REQUEST GO OUT, kept pure so RailCheck L184 can execute it case by case.
        ///
        /// A FULL RESEND AND A SCOPED BACKFILL ARE NOT INTERCHANGEABLE, so they do not share a window. The
        /// full one stays globally coalesced for the reason its old comment gave and that reason still
        /// holds — a systematic miss hits every entry of every packet and one full resend answers all of
        /// them (39 unknown-kindId warnings, one resend, converged in 1.3 s: correct). But the SAME window
        /// also swallowed the scoped <c>structural create backfill</c> requests, and the warning line sat
        /// INSIDE the throttle, so a swallowed scoped request printed NOTHING AT ALL. A backfill is about
        /// one root's ref-lists; a full resend that went out five seconds ago for an unrelated reason does
        /// not answer it, and nothing would ever have said so.
        ///
        /// The scoped window is PER ROOT for the same reason: two roots losing their ref-lists in the same
        /// second are two losses, and the second one is not noise.</summary>
        internal static bool ResyncAllowed(bool scoped, float now, float nextFullAt, float nextScopedAt)
            => scoped ? now >= nextScopedAt : now >= nextFullAt;

        /// <summary>ONE gate onto the law-7 resync path, whatever noticed the divergence (seq gap, unknown
        /// kind def, torn batch). Throttled per <see cref="ResyncAllowed"/> — and a request the throttle
        /// eats leaves a COUNTED line, never silence.</summary>
        /// <param name="rootKey">The one root this client knows it lost, when it knows one — the host then
        /// answers with a SCOPED re-emit instead of the whole covered graph (see HandleInbound). Null = no
        /// scope known (a seq gap or a torn batch can have touched anything).</param>
        private static void RequestResync(NetworkEngine engine, string reason, string rootKey = null)
        {
            if (engine == null) return;
            float now = Time.realtimeSinceStartup;
            bool scoped = !string.IsNullOrEmpty(rootKey);
            float nextScoped = scoped && _nextScopedAt.TryGetValue(rootKey, out var at) ? at : 0f;
            if (!ResyncAllowed(scoped, now, _nextFullResyncAt, nextScoped))
            {
                // The REASON is deliberately not in this text: it varies per entry (38 distinct
                // "unknown kindId N" strings in one burst) and would defeat the counted once-logger it
                // rides on. The digest reports the volume; the first line already named the family.
                LogMissOnce(scoped
                    ? "scoped resend of root '" + rootKey + "' THROTTLED — a request for that root went out " +
                      "less than " + ResyncThrottleSec + "s ago and has not been answered yet"
                    : "full resend THROTTLED — one is already in flight (" + ResyncThrottleSec + "s window)");
                return;
            }
            if (scoped)
            {
                _nextScopedAt[rootKey] = now + ResyncThrottleSec;
                _pendingScoped[rootKey] = now + ScopedAnswerDeadlineSec;
            }
            else { _nextFullResyncAt = now + ResyncThrottleSec; _pendingFullAt = now + ScopedAnswerDeadlineSec; }
            MpLog.LogWarning("[Multiplayer][rail] GenericApplier: " + reason + " — requesting resend" +
                             (scoped ? " of root '" + rootKey + "'" : " (full)"));
            try
            {
                byte[] body;
                if (string.IsNullOrEmpty(rootKey)) body = new[] { DiffEngine.MsgResyncRequest };
                else
                    using (var ms = new MemoryStream())
                    using (var w = new BinaryWriter(ms, Encoding.UTF8))
                    { w.Write(DiffEngine.MsgResyncRequest); w.Write(rootKey); body = ms.ToArray(); }
                var env = SyncProtocol.EncodeEnvelope(SurfaceIds.GeoRail, SyncKind.ActionRequest, body);
                engine.SendToHost(new NetworkMessage(PacketType.SyncEnvelope, env));
            }
            catch (Exception ex) { MpLog.LogError("[Multiplayer][rail] GenericApplier resync request failed: " + ex.Message); }
        }

        /// <summary>Host half of the optional scope above. A legacy one-byte request (or any unreadable
        /// tail) reads as "no scope" and falls back to the full resend — the pre-scope behaviour.</summary>
        private static string ReadResyncRoot(byte[] payload)
        {
            if (payload == null || payload.Length <= 1) return null;
            try
            {
                using (var ms = new MemoryStream(payload, 1, payload.Length - 1))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                    return MessageSerializer.ReadBoundedString(r); // HOST side: a client names this scope
            }
            catch { return null; }
        }
    }

    /// <summary>THE LEVEL IS BUILT, not merely present. <c>GameUtl.CurrentLevel()</c> answers a DIFFERENT
    /// question from the one the applier's discard gate has to ask, and the gap between the two answers is
    /// many frames wide: <c>GeoLevelController.LevelCrt</c> is a coroutine that keeps CONSTRUCTING the graph
    /// long after the level object exists — every faction's <c>Research</c> and <c>ManufactureQueue</c> are
    /// built inside <c>GeoFaction.OnLevelStart</c> → <c>SetupStartingComponents</c> (GeoFaction.cs:409/:566
    /// → :779 <c>InitManufacturing</c> / :798 <c>SetupResearch</c>), which that coroutine only reaches at
    /// GeoLevelController.cs:653.
    ///
    /// A batch landing inside that window is the worst shape this rail has: the ROOT resolves (the faction
    /// object is already there), so nothing discards the batch — and then every entry under a still-null
    /// sub-object is dropped one by one by "entity not found", which requests NOTHING. Measured 2026-08-09
    /// (D:/PP-Instance2/Player.log:872-944, all inside second 65): 423 drops in one batch —
    /// <c>F#….ManufactureQueue</c>, <c>F#….Research</c> and every <c>F#….Research.AllResearchesArray#…</c>
    /// under them — followed by two <c>CRC backstop: root DIVERGED</c> on that faction. The peer whose level
    /// OBJECT had not appeared yet took the geo==null path for the identical window, discarded the batches
    /// and got all of them back. Widening that same gate is the whole fix; the CRC path needed no change,
    /// it was the backstop doing its job over a hole this closes upstream.
    ///
    /// <c>ModManager.OnGeoscapeStart</c> (GeoLevelController.cs:757) is the game's OWN "the geoscape is
    /// built" callback and the LAST line of that init block — the same seam this mod already trusts to know
    /// a campaign is savable (<c>NewCampaignInterceptPatch.GeoscapeReadyPatch</c>). No frame counts, no
    /// polled private field, and it stays correct when another mod makes the init slower.
    ///
    /// NOT A QUORUM (P13): what is waited on is THIS peer's own load, which ends by itself — no peer, and no
    /// person, is asked to act.
    ///
    /// Takes no parameter on purpose: the game calls it as <c>OnGeoscapeStart(this)</c> from the level that
    /// is starting, so the current level IS the argument, and binding by parameter name is one more way to
    /// fail silently.</summary>
    [HarmonyPatch(typeof(PhoenixPoint.Modding.ModManager), "OnGeoscapeStart")]
    internal static class GeoscapeStartedPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            GenericApplier.MarkGeoscapeStarted();
            // The durable window inbox lives for one geoscape session; this is its only creation seam.
            Multiplayer.Network.Sync.DurableInboxSession.OpenSessionStore();
        }
    }
}
