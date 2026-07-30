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
        private static readonly HashSet<string> _loggedMisses = new HashSet<string>(StringComparer.Ordinal);
        private static uint _lastSeq;
        private static float _nextResyncReqAt;
        private const float CrcInterval = 1f; // one root subtree per second (see ClientCrcTick)
        private static float _crcNextAt;
        private static int _crcRoot;          // rotation cursor over IdentityResolver.Roots

        public static void Reset()
        {
            ResetForReloadBoundary();
            Seq.Reset();
            _kinds.Clear();
            _brokenKinds.Clear();
            _lastSeq = 0;
        }

        public static void ResetForReloadBoundary()
        {
            _pathCache = new Dictionary<string, object>(StringComparer.Ordinal);
            _loggedMisses.Clear();
            _fragBuf.Clear(); _fragGot.Clear(); // the transferred save replaced the state these halves belonged to
            // (No EventPopup reset here anymore: event windows are live 0xB6 raises, so there is no
            // record-derived latch to re-seed. Its raise-seq stream is a host monotonic counter and MUST
            // survive a reload boundary — rca-3 contract — so it resets only at full teardown.)
            // The transferred save just replaced this client's clock — re-seed the anchor scratch from it,
            // or the next partial anchor would layer onto pre-reload values.
            TimeAnchor.Reset();
            DefOwnership.Invalidate(); // the loaded save can mint runtime defs — rebuild the ownership set
            // seq + kind registry persist (rca-3 contract: host counters keep increasing across reloads)
        }

        private static GeoLevelController GeoLevel()
        {
            var level = GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<GeoLevelController>();
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
                        DiffEngine.RequestFullResend();
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
                            DiffEngine.HandleCrcReport(senderPeerId, r.ReadString(), r.ReadUInt32(), r.ReadUInt32());
                        }
                    return true;
                }
                if (engine == null || engine.IsHost) return true; // host never applies its own surface
                if (payload[0] == DiffEngine.MsgDelta) ApplyDelta(engine, payload);
                else if (payload[0] == DiffEngine.MsgStructural) ApplyStructural(engine, payload);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][rail] GenericApplier inbound failed: " + ex); }
            return true;
        }

        private static void ApplyDelta(NetworkEngine engine, byte[] payload)
        {
            var geo = GeoLevel();
            if (geo == null) return; // mid-load: the reload boundary + save transfer own this window

            using (var ms = new MemoryStream(payload))
            using (var r = new BinaryReader(ms, Encoding.UTF8))
            {
                r.ReadByte(); // MsgDelta
                uint seq = r.ReadUInt32();
                if (!Seq.ShouldApply(SurfaceIds.GeoRail, seq)) return; // stale
                if (_lastSeq != 0 && seq > _lastSeq + 1)
                    RequestResync(engine, "seq gap (" + _lastSeq + "→" + seq + ")");

                var touched = new HashSet<object>();
                try
                {
                    int defCount = r.ReadByte();
                    for (int i = 0; i < defCount; i++) RegisterKind(r.ReadByte(), r.ReadString(), r.ReadUInt16());

                    int n = r.ReadUInt16();
                    _pathCache.Clear(); // batch-local: a new instance under the same key (re-queued research) must re-resolve
                    using (SyncApplyScope.Enter())
                    {
                        for (int i = 0; i < n; i++)
                        {
                            byte kindId = r.ReadByte();
                            string path = r.ReadString();
                            ushort fieldIdx = r.ReadUInt16();
                            string subKey = r.ReadString();
                            var value = Reassemble(path, fieldIdx, subKey, r.ReadBytes(r.ReadUInt16()));
                            if (value == null) continue; // fragment stashed — the entry applies once it is whole
                            ApplyEntry(engine, geo, kindId, path, fieldIdx, subKey, value, touched);
                        }
                    }
                    // The anchor is a DTO, so the leaf applies above only filled it in — this is where it becomes
                    // the clock. Post-batch, and outside SyncApplyScope because ProcessInstanceData fires nothing.
                    TimeAnchor.ApplyIfTouched(geo, touched);
                    // Same post-batch rung, same reason: an ORDER may arrive as several leaves of one batch
                    // (Travelling + DestinationSites + CurrentSite), and re-deriving from a half-applied order
                    // would seed the wrong route. Outside SyncApplyScope on purpose — this only STARTS a
                    // coroutine, whose arrival callback must meet VehicleArrivalGate with no apply exemption.
                    FlushOrderReseed();
                }
                catch (Exception ex)
                {
                    // Reader-level failure mid-batch: leave the seq UNMARKED (SurfaceSeq contract — a failed
                    // apply must not consume the seq) and recover the lost entries via the throttled resync.
                    // Per-entry failures never land here (ApplyEntry catches its own); this is a torn packet.
                    Debug.LogError("[Multiplayer][rail] GenericApplier: batch failed at seq " + seq + ": " + ex);
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
                    UiEventMap.Fire(touched, geo);
            }
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
            var geo = GeoLevel();
            if (geo == null) return; // mid-load: reload boundary + save transfer own this window

            using (var ms = new MemoryStream(payload))
            using (var r = new BinaryReader(ms, Encoding.UTF8))
            {
                r.ReadByte(); // MsgStructural
                uint seq = r.ReadUInt32();
                if (!Seq.ShouldApply(SurfaceIds.GeoRail, seq)) return; // stale/duplicate
                if (_lastSeq != 0 && seq > _lastSeq + 1)
                    RequestResync(engine, "seq gap (" + _lastSeq + "→" + seq + ")");
                byte op = r.ReadByte();
                string rootKey = r.ReadString();
                var blob = r.ReadBytes(r.ReadInt32());

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
                                { Debug.LogError("[Multiplayer][rail] structural create '" + rootKey + "': " + (unit == null ? "blob deserialize failed" : "no _tacUnits registry")); return; }
                                reg[unit.Id] = unit; // the game's own load registration (ProcessInstanceData:609)
                                created = true;
                                touched.Add(unit);   // UiEventMap GeoCharacter arm: native derived-stat refresh + repaint
                                Debug.Log("[Multiplayer][rail] structural create '" + rootKey + "' applied (" + blob.Length + "B)");
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
                                Debug.Log("[Multiplayer][rail] structural destroy '" + rootKey + "' applied");
                            }
                            else LogMissOnce("structural destroy for '" + rootKey + "' not enabled — skipped");
                        }
                        // op==1 with existing != null / op==2 with null = redelivery or already-converged: no-op (law 7)
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError("[Multiplayer][rail] structural apply '" + rootKey + "' failed: " + ex);
                    return; // seq unmarked — the throttled resync path recovers
                }
                _lastSeq = seq;
                Seq.Mark(SurfaceIds.GeoRail, seq);
                _pathCache.Clear(); // a root appeared/vanished — cached resolutions are void
                if (touched.Count > 0)
                    using (SyncApplyScope.Enter())
                        UiEventMap.Fire(touched, geo); // law 11: open roster/equip screens repaint NOW
                if (created)
                    RequestResync(engine, "structural create backfill"); // ref-lists shipped pre-create reconverge
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
                    SiteRegisterMission.Invoke(site, new object[] { mission }); // GeoSite.cs:1629
                }
            }
            else LogMissOnce("descend create at " + rootKey + ": no native wiring for " +
                             owner.GetType().Name + "." + field.Name + " — field assigned RAW");
            touched.Add(owner);
            OpenUiRepaint.MarkDirty(); // law 11: the open geoscape shows the new marker NOW
            Debug.Log("[Multiplayer][rail] structural create '" + rootKey + "' applied (" + t.Name + ")");
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
            OpenUiRepaint.MarkDirty();
            Debug.Log("[Multiplayer][rail] structural destroy '" + rootKey + "' applied");
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
            Debug.Log("[Multiplayer][rail] structural create '" + rootKey + "' applied (" + t.Name + " from " + setDef.name + ")");
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
            vehicle.OnExitPlay();                                  // GeoVehicle.cs:602
            UnityEngine.Object.Destroy(vehicle.gameObject);        // :603
            OpenUiRepaint.MarkDirty();
            Debug.Log("[Multiplayer][rail] structural destroy '" + rootKey + "' applied (vehicle " + vehicle.VehicleID + ")");
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
            if (fac == null) { Debug.LogError("[Multiplayer][rail] structural create '" + rootKey + "': facility blob deserialize failed"); return false; }
            if (!(FacListField.GetValue(layout) is IList list)) return false;
            list.Add(fac);
            fac.OnFacilityStateUpdated += (GeoPhoenixFacility.FacilityStateEventHandler)Delegate.CreateDelegate(
                typeof(GeoPhoenixFacility.FacilityStateEventHandler), layout, FacStateHandler);
            FacUpdateCache.Invoke(layout, null);
            FacInit.Invoke(pxBase, new object[] { fac });
            OpenUiRepaint.MarkDirty(); // open base screen rebuilds via the UIStatePhoenixBaseLayout table entry
            Debug.Log("[Multiplayer][rail] structural create '" + rootKey + "' applied (facility " + fac.Def?.name + ", " + blob.Length + "B)");
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
            Debug.Log("[Multiplayer][rail] structural destroy '" + rootKey + "' applied (facility " + fac.Def?.name + ")");
        }

        private static void RegisterKind(byte kindId, string typeName, ushort fieldCount)
        {
            if (_kinds.ContainsKey(kindId) || _brokenKinds.Contains(kindId)) return;
            var t = AccessTools.TypeByName(typeName);
            var rt = t == null ? null : RailType.Get(t);
            if (rt == null || rt.Fields.Count != fieldCount)
            {
                _brokenKinds.Add(kindId);
                Debug.LogError("[Multiplayer][rail] GenericApplier: kind " + kindId + " (" + typeName + ") " +
                               (rt == null ? "unresolvable" : "field count mismatch " + rt.Fields.Count + "≠" + fieldCount) +
                               " — entries of this kind will be skipped (mod parity?)");
                return;
            }
            _kinds[kindId] = rt;
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
            if (entity == null) { LogMissOnce("entity not found: " + path); return; }
            if (!rt.Type.IsInstanceOfType(entity))
            {
                // The resolver returned the LIVE TWIN of a recorded *InstanceData DTO (writes into the
                // getter-minted DTO are void — see IdentityResolver). Same member source + ordinal sort ⇒
                // the wire fieldIdx addresses the same-named member in the bridged table.
                var bt = RailType.GetBridged(entity.GetType(), rt.Type);
                var bf = bt != null && bt.Fields.Count == rt.Fields.Count ? bt.Fields[fieldIdx] : null;
                if (bf == null || !string.Equals(bf.Name, field.Name, StringComparison.Ordinal))
                { LogMissOnce("type mismatch at " + path + ": " + entity.GetType().Name + " vs " + rt.Type.Name); return; }
                if (bf.Class == FieldClass.Excluded)
                { LogMissOnce("dto-twin gap: " + rt.Type.Name + "." + bf.Name + " has no live counterpart on " + entity.GetType().Name + " (" + bf.Exclude + ") — not mirrored"); return; }
                field = bf;
            }
            // Walk-time ownership law BACKSTOP (belt = host's DiffEngine refusal; this guards version
            // skew and per-peer def-graph differences): never write into an instance the def graph
            // owns, and never mutate a def-owned container reached through a live entity's field —
            // that write lands in shared def state. Leaves are exempt below by construction: a leaf
            // apply REPLACES the entity's reference, it never mutates the shared instance (and the
            // entity itself was just checked).
            if (DefOwnership.IsDefOwned(entity))
            { LogMissOnce("def-owned instance at " + path + " — write refused (ownership law)"); return; }
            if (field.Class != FieldClass.Leaf && field.CanRead)
            {
                object cur;
                try { cur = field.GetValue(entity); } catch { cur = null; }
                if (cur != null && DefOwnership.IsDefOwned(cur))
                { LogMissOnce("def-owned container at " + path + "." + field.Name + " — write refused (ownership law)"); return; }
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
                // TEMP diag (reassign retest): a TacUnits delta that arrived but matched local bytes.
                if (field.Name == "TacUnits") Debug.Log("[MP][diag] TacUnits APPLY-SKIP (unchanged) " + path);
                return; // no-op entry: not applied, not touched, no repaint
            }

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
                        if (ReferenceEquals(v, RailMeta.Unresolved)) return;
                        // FactionRef twin: the wire carries the faction's DEF; the live member holds the
                        // GeoFaction. Unknown def → keep the live value (same L-C shape as Unresolved).
                        if (field.FactionRef && v != null && (v = RailMeta.FactionByDef(geo, v)) == null) return;
                        // TEMP diag (power retest 2026-07-29): the host→client direction of the facility
                        // power leaf has never been observed live. One line at the write, at delta rate
                        // for this ONE field (Unchanged already filtered no-ops above).
                        if (field.Name == "_isPowered")
                            Debug.Log("[MP][diag] facility power APPLY " + path + "." + field.Name + " " +
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
                        // TEMP diag (reassign retest): every TacUnits list apply. Pull after the retest.
                        if (field.Name == "TacUnits")
                        {
                            var ids = new StringBuilder();
                            if (items != null) foreach (var u in items) ids.Append(IdentityResolver.RootRef(u) ?? "?").Append(' ');
                            Debug.Log("[MP][diag] TacUnits APPLY " + path + " count=" + (isNull || items == null ? -1 : items.Count) +
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
                        var items = RailMeta.DecodeEntityList(value, field, geo);
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
                                if (inst == null) LogMissOnce("order-vector member '" + k + "' unresolved at " + p + "." + fn);
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
                            LogMissOnce("GeoItemDict field not a live non-generic IDictionary at " + path + "." + field.Name +
                                        " (" + (target == null ? "null" : target.GetType().Name) + ") — entry dropped");
                            return;
                        }
                        var def = GeoItemCodec.ResolveDef(subKey); // key IS the ItemDef
                        if (def == null) { LogMissOnce("GeoItemDict unknown item def " + subKey + " at " + path); return; }
                        // DIRECT dict write / remove — NOT AddItem/RemoveItem (those fire StorageChanged/ItemAdded
                        // events + faction ammo-unload = gameplay side-effects a projector client must not run).
                        if (value.Length == 1 && value[0] == RailMeta.DictTombstone) { dict.Remove(def); break; }
                        dict[def] = GeoItemCodec.Decode(value, def);
                        break;
                    }
                    default:
                        return; // Descend never carries values
                }
                touched.Add(entity);
                MarkOrderChange(entity, field.Name);
            }
            catch (Exception ex)
            {
                LogMissOnce("apply failed " + path + "." + field.Name + ": " + ex.Message);
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
        private static readonly Dictionary<string, GeoSite[]> _seededRoute = new Dictionary<string, GeoSite[]>(StringComparer.Ordinal);

        /// <summary>The ORDER leaves — a mirrored INPUT to native navigation, as opposed to its derived
        /// output. Named, not type-dispatched: the rule is about which values constitute an order.</summary>
        private static bool IsOrderLeaf(string name) =>
            name == "DestinationSites" || name == "Travelling" || name == "CurrentSite";

        private static void MarkOrderChange(object entity, string fieldName)
        {
            if (IsOrderLeaf(fieldName) && entity is GeoVehicle v) _reseed.Add(v);
        }

        private static void FlushOrderReseed()
        {
            if (_reseed.Count == 0) return;
            foreach (var v in _reseed)
            {
                try { ReseedNavigation(v); }
                catch (Exception ex)
                { LogMissOnce("nav re-seed failed for " + (IdentityResolver.RootRef(v) ?? "?") + ": " + ex.Message); }
            }
            _reseed.Clear();
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
        private static void ReseedNavigation(GeoVehicle v)
        {
            if (v == null || v.Navigation == null) return;
            var root = IdentityResolver.RootRef(v);
            var dest = v.DestinationSites;

            if (v.Travelling && dest != null && dest.Count > 0)
            {
                var route = new GeoSite[dest.Count];
                for (int i = 0; i < dest.Count; i++)
                {
                    if (dest[i] == null) return; // an unresolved destination: wait for the resend, never fly a null leg
                    route[i] = dest[i];
                }
                if (root != null && _seededRoute.TryGetValue(root, out var last) && IsSuffixOf(last, route))
                    return;                      // same route, a waypoint consumed — the client is already flying it
                var path = new List<Vector3>(route.Length);
                foreach (var d in route) path.Add(d.WorldPosition);
                v.Navigation.Navigate(path);     // GeoVehicle.OnLevelStart:388
                if (root != null) _seededRoute[root] = route;
                Debug.Log("[Multiplayer][rail] nav re-seed " + (root ?? "?") + " → " + route.Length + " leg(s)");
                return;
            }

            if (root != null) _seededRoute.Remove(root);
            if (!v.Travelling && v.CurrentSite != null)
            {
                v.PivotTransform.localRotation = v.CurrentSite.PivotTransform.localRotation; // TeleportToSite:510
                if (v.Animator != null) v.Animator.SetInteger("State", 0);                   // :513 — the landing
                                                                                             // half of the pose the
                                                                                             // client's own nav set
                                                                                             // via InitiateTravelling
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
            foreach (var k in dict.Keys)
            {
                var sub = field.Class == FieldClass.GeoItemDict ? GeoItemCodec.SubKey(k) : RailMeta.EncodeDictKey(k);
                if (present.Contains(sub)) continue;
                if (extras == null) extras = new List<object>();
                extras.Add(k);
            }
            if (extras == null) return;
            foreach (var k in extras) dict.Remove(k);
            touched.Add(entity);
            Debug.Log("[Multiplayer][rail] GenericApplier: census pruned " + extras.Count + " phantom dict key(s) at " +
                      path + "." + field.Name);
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static void LogMissOnce(string msg)
        {
            if (_loggedMisses.Count < 500 && _loggedMisses.Add(msg))
                Debug.LogWarning("[Multiplayer][rail] GenericApplier: " + msg);
        }

        /// <summary>Law-7 drift backstop, client half — the ONE thing in the rail that ever compares host and
        /// client state. Once a second, CRC exactly ONE root subtree of our own mirror with the SAME canonical
        /// walk the host emits from (<see cref="DiffEngine.RootCrc"/>) and report it with the seq we have
        /// applied, so the host can tell divergence from lag. Rotating one root per second: a full sweep costs
        /// one host walk spread over ~N seconds instead of a graph hash per tick (the host walk cost is what
        /// caused the rhythmic freezes) — a backstop's job is to notice within a minute, not within a frame.
        /// Why it must exist: the host diff compares host-NOW to host-BEFORE, so nothing the host DELETES ever
        /// reaches us — a vanished path emits no entry and no tombstone. Only a subtree compare can see it.</summary>
        public static void ClientCrcTick(NetworkEngine engine)
        {
            if (engine == null || engine.IsHost || !engine.IsActiveSession) return;
            if (Time.realtimeSinceStartup < _crcNextAt) return;
            _crcNextAt = Time.realtimeSinceStartup + CrcInterval;
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
                uint crc = DiffEngine.RootCrc(key, obj);
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
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][rail] CRC report for '" + key + "' failed: " + ex.Message); }
        }

        /// <summary>ONE throttled gate onto the law-7 resync path, whatever noticed the divergence (seq gap,
        /// unknown kind def, torn batch). The throttle is global on purpose: a systematic miss hits every
        /// entry of every packet, and one full resend answers all of them.</summary>
        private static void RequestResync(NetworkEngine engine, string reason)
        {
            if (engine == null || Time.realtimeSinceStartup < _nextResyncReqAt) return;
            _nextResyncReqAt = Time.realtimeSinceStartup + 5f;
            Debug.LogWarning("[Multiplayer][rail] GenericApplier: " + reason + " — requesting full resend");
            try
            {
                var env = SyncProtocol.EncodeEnvelope(SurfaceIds.GeoRail, SyncKind.ActionRequest,
                                                      new[] { DiffEngine.MsgResyncRequest });
                engine.SendToHost(new NetworkMessage(PacketType.SyncEnvelope, env));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][rail] GenericApplier resync request failed: " + ex.Message); }
        }
    }
}
