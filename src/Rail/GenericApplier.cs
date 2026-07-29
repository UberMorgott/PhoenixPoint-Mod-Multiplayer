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
            EventPopup.Reset(); // record latch re-seeds silently from the transferred save's records
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
                            var value = r.ReadBytes(r.ReadUInt16());
                            ApplyEntry(engine, geo, kindId, path, fieldIdx, subKey, value, touched);
                        }
                    }
                    // The anchor is a DTO, so the leaf applies above only filled it in — this is where it becomes
                    // the clock. Post-batch, and outside SyncApplyScope because ProcessInstanceData fires nothing.
                    TimeAnchor.ApplyIfTouched(geo, touched);
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
                            if (rootKey.StartsWith("U#", StringComparison.Ordinal))
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
                            if (existing is PhoenixPoint.Geoscape.Entities.Missions.IGeoTacUnit unit)
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
            var typeName = blob == null || blob.Length == 0 ? null : Encoding.UTF8.GetString(blob);
            var t = string.IsNullOrEmpty(typeName) ? null : AccessTools.TypeByName(typeName);
            if (t == null || !field.ValueType.IsAssignableFrom(t))
            {
                LogMissOnce("descend create at " + rootKey + ": payload type '" + (typeName ?? "<empty>") +
                            "' " + (t == null ? "unresolvable" : "is not a " + field.ValueType.Name) + " — skipped");
                return false;
            }
            object made;
            try { made = RailMeta.ConstructLikeLoad(t); }
            catch (Exception ex)
            { LogMissOnce("descend create at " + rootKey + ": " + t.Name + " construction threw " + ex.GetType().Name + " — skipped"); return false; }
            if (made == null) { LogMissOnce("descend create at " + rootKey + ": " + t.Name + " could not be constructed — skipped"); return false; }
            // Law 1: the ONE thing this payload shape cannot carry gets a line, not silence. A custom
            // create's params are WriteOnly members — outside SerializedMembers, so the value rail will
            // never fill them either (RailCheck L29 names the same types statically).
            var cp = RailMeta.CreateParamNames(t);
            if (cp.Length > 0)
                LogMissOnce("descend create at " + rootKey + ": " + t.Name + " has custom-create params (" +
                            string.Join(",", cp) + ") that a type-name payload cannot carry — they arrive NULL");

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
            }
            catch (Exception ex)
            {
                LogMissOnce("apply failed " + path + "." + field.Name + ": " + ex.Message);
            }
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
