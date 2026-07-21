using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Base.Core;
using HarmonyLib;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Rail;
using PhoenixPoint.Geoscape.Entities.Research;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View.ViewModules;
using PhoenixPoint.Geoscape.View.ViewStates;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Migration step 1 — RESEARCH subsystem on the rail (grew out of Spike A; in-game verified core).
    ///
    /// HOST → ALL (surface GeoResearch 0xAA, observe seams = plain native event subscriptions + the
    /// ≤2 Hz poll in <see cref="HostTick"/>, zero Harmony):
    ///   • MsgStart    — Research.OnResearchStarted → native-serializer ResearchElement blob
    ///                   (value-only fallback when the blob exceeds the u16 envelope).
    ///   • MsgQueue    — poll: full queue ORDER (ResearchIDs) changed → snapshot delta. KEPT off the
    ///                   generic rail because a List's ORDER cannot be expressed as value fields (the
    ///                   rail addresses collection elements by stable key, never by index — law 2);
    ///                   also the one catch-all for cancel/reorder/queue-add (no native event covers a
    ///                   non-current cancel) and the convergence belt for a start lost to the seq guard.
    ///   • MsgComplete — Research.OnResearchCompleted → id delta.
    /// RETIRED (2026-07-17): MsgProgress + the 2 Hz progress poll — ResearchElement.ResearchProgress is
    /// a plain [SerializeMember] value field, so it now rides the generic value rail (DiffEngine 0xAC).
    ///
    /// CLIENT (projector, law 3): applies mutate the LIVE Research/ResearchElement instances by
    /// field/value copy only, inside <see cref="SyncApplyScope"/> (law 8), then repaint via the native
    /// paths (open UIStateResearch SetupQueue rebuild, agenda-tracker nudge, native completed modal/log
    /// handlers). The client NEVER runs ResearchElement.Complete() — State is stamped through the
    /// backing field so the reward chain (ApplyRewards/RewardReputation/Wallet.Give) stays host-only;
    /// reward side-effects reach the client through their own subsystems (wallet, diplomacy, …).
    ///
    /// CLIENT → HOST (surface GeoResearchIntent 0xAB): the research screen's entry points
    /// (Research.AddResearchToQueue/Cancel/PutInFromOfQueue/PutUpInQueue/PutDownInQueue/
    /// InsertAtPosition — UIModuleResearch.cs:414-480 all route here) are intent-capture Harmony
    /// prefixes (law 4a): on the client they BLOCK the native call and send an intent; on the host
    /// (and inside SyncApplyScope) the native code runs untouched. Host: IntentDedup → validate →
    /// execute the SAME native method → the observe seams above broadcast the outcome. Invalid
    /// intents are rejected silently (logged).
    ///
    /// SIM GATING (law 4b): Research.Update is skipped on the client (its clock is not frozen — the
    /// local hourly tick would double-apply progress and locally complete research). Host deltas are
    /// the only research progression source on a client.
    /// </summary>
    public static class ResearchSync
    {
        // Inner payload discriminator (the SurfaceRouter hands us surfaceId + payload, not the SyncKind).
        // 2 (MsgProgress) retired 2026-07-17 — progress rides the generic value rail (0xAC). Never reuse.
        private const byte MsgStart = 1;
        private const byte MsgQueue = 3;
        private const byte MsgComplete = 4;

        // Intent ops (GeoResearchIntent inner payload) — each maps 1:1 onto the native Research method.
        private const byte OpStart = 1;      // AddResearchToQueue
        private const byte OpCancel = 2;     // Cancel
        private const byte OpFront = 3;      // PutInFromOfQueue
        private const byte OpUp = 4;         // PutUpInQueue
        private const byte OpDown = 5;       // PutDownInQueue
        private const byte OpInsertAt = 6;   // InsertAtPosition(pos)

        // Blob bigger than this → value-only start (u16 envelope length field, minus header/strings headroom).
        private const int MaxStartBlob = ushort.MaxValue - 512;

        private static readonly SurfaceSeq Seq = new SurfaceSeq();
        private static readonly IntentDedup Intents = new IntentDedup();
        private static uint _nextIntentNonce;

        // ─── Host observe state ────────────────────────────────────────────
        private static Research _hooked;                 // faction Research we subscribed to (unhook on level change)
        private static float _nextTickAt;                // realtime throttle (0.5 s → max 2 Hz)
        private static string _lastSentQueue;            // joined queue ResearchIDs (order-sensitive)

        // ─── Reflection (private members only; everything else is typed) ──
        private static readonly System.Reflection.MethodInfo IsInProgressSetter =
            AccessTools.PropertySetter(typeof(ResearchElement), "IsInProgress");
        private static readonly System.Reflection.FieldInfo StateBackingField =
            AccessTools.Field(typeof(ResearchElement), "_state");
        private static readonly System.Reflection.FieldInfo OnResearchStartedField =
            AccessTools.Field(typeof(Research), "OnResearchStarted");
        private static readonly System.Reflection.FieldInfo TrackerNeedsRefreshField =
            AccessTools.Field(typeof(UIModuleFactionAgendaTracker), "_needsRefresh");
        private static readonly System.Reflection.MethodInfo SetupQueueMethod =
            AccessTools.Method(typeof(UIModuleResearch), "SetupQueue");
        // SetupQueue rebuilds ONLY the queue panel (right side). The AVAILABLE list (left) is a separate
        // pull-model snapshot rebuilt only by ShowAvailable — which the native Cancel/StartResearch handlers
        // ALSO call. Mirror that: a host research delta changes availability (a cancelled research returns to
        // the list, a queued one leaves it), so law 11 requires the list to repaint too, not just the queue.
        private static readonly System.Reflection.MethodInfo ShowAvailableMethod =
            AccessTools.Method(typeof(UIModuleResearch), "ShowAvailable");
        // Native research-complete PRESENTATION handlers (private), invoked directly on the client so the
        // completed modal + log line appear WITHOUT raising GeoFaction.ResearchCompletedEventHandler
        // (whose other subscribers — GeoscapeEventSystem, stats, pedia — are host-side logic/state).
        private static readonly System.Reflection.MethodInfo ViewResearchCompletedMethod =
            AccessTools.Method(typeof(PhoenixPoint.Geoscape.View.GeoscapeView), "OnFactionResearchCompleted");
        private static readonly System.Reflection.MethodInfo LogResearchCompletedMethod =
            AccessTools.Method(typeof(GeoscapeLog), "Faction_ResearchCompleted");

        // ─── Lifecycle (driven by SyncEngine) ──────────────────────────────

        /// <summary>Full session teardown: everything resets, including the seq streams.</summary>
        public static void Reset()
        {
            ResetForReloadBoundary();
            Seq.Reset();
            Intents.Reset();
        }

        /// <summary>
        /// Mid-session reload boundary (rca-3 contract): drop every reference into the dying geoscape
        /// (host event hooks) and the last-sent coalescing marks (so the post-reload state is re-emitted),
        /// but KEEP the SurfaceSeq streams + intent nonce — version/seq counters persist symmetrically on
        /// both sides so post-reload deltas keep applying (host numbers keep increasing).
        /// </summary>
        public static void ResetForReloadBoundary()
        {
            Unhook();
            _lastSentQueue = null;
        }

        /// <summary>Rejoin (rca-3 audit b): the returning peer's fresh engine restarts its nonce at 1.</summary>
        public static void ResetIntentDedupForPeer(ulong peerId) => Intents.ResetPeer(peerId);

        private static void Unhook()
        {
            if (_hooked == null) return;
            try
            {
                _hooked.OnResearchStarted -= HostOnResearchStarted;
                _hooked.OnResearchCompleted -= HostOnResearchCompleted;
            }
            catch { }
            _hooked = null;
        }

        private static GeoLevelController GeoLevel()
        {
            var level = GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<GeoLevelController>();
        }

        // ─── HOST: hook management + ≤2 Hz value/queue poll ────────────────

        public static void HostTick(NetworkEngine engine)
        {
            if (engine == null || !engine.IsHost || !engine.IsActiveSession) return;
            if (Time.realtimeSinceStartup < _nextTickAt) return;
            _nextTickAt = Time.realtimeSinceStartup + 0.5f;

            var geo = GeoLevel();
            var research = geo == null ? null : geo.PhoenixFaction?.Research;
            if (research == null) { Unhook(); return; }
            if (!ReferenceEquals(research, _hooked))
            {
                Unhook();
                research.OnResearchStarted += HostOnResearchStarted;
                research.OnResearchCompleted += HostOnResearchCompleted;
                _hooked = research;
                Debug.Log("[Multiplayer][rail] ResearchSync: hooked OnResearchStarted/OnResearchCompleted (host)");
            }

            // Queue ORDER snapshot. This poll is now only the DRIFT BACKSTOP (<=2 Hz): the known apply
            // points push immediately (HandleIntent success, host start/complete), so on localhost a
            // change no longer waits up to 500 ms for the next tick. _lastSentQueue coalesces the two
            // (an immediate push already advanced it -> the following poll tick is a no-op).
            PushQueueSnapshot(engine);
            // Progress value deltas retired 2026-07-17: ResearchProgress rides the generic rail (0xAC).
        }

        /// <summary>
        /// Send the full queue-order snapshot NOW if it changed since the last send. Shared primitive
        /// behind both the &lt;=2 Hz poll (drift backstop) and the immediate pushes at the known apply
        /// points. The _lastSentQueue compare doubles as the double-send guard: an immediate push
        /// updates it, so a poll tick (or a second push) landing right after is a no-op.
        /// </summary>
        private static void PushQueueSnapshot(NetworkEngine engine)
        {
            if (engine == null || !engine.IsHost || !engine.IsActiveSession) return;
            var research = GeoLevel()?.PhoenixFaction?.Research;
            if (research == null) return;
            var queueIds = research.ResearchQueue.Select(r => r.ResearchID).ToList();
            string queueKey = string.Join("", queueIds.ToArray());
            if (queueKey == _lastSentQueue) return;
            _lastSentQueue = queueKey;
            Send(engine, EncodeQueue(Seq.Next(SurfaceIds.GeoResearch), research.Faction.Def.Guid, queueIds));
        }

        private static void HostOnResearchStarted(ResearchElement research)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsHost || !engine.IsActiveSession || research == null) return;
            // The serializer coroutine needs a running IUpdateable — a UI-click start has no
            // Timing.Current, so defer onto the geoscape level Timing (GeoLevelController.Timing).
            SerializerRoundtrip.RunOnTiming(GeoLevel(), () => SendStartCrt(engine, research));
            // Host-initiated start also reorders the queue (new current at front) — push it NOW so
            // clients' queue panel isn't left waiting on the poll. MsgStart still rides SendStartCrt.
            PushQueueSnapshot(engine);
        }

        private static void HostOnResearchCompleted(ResearchElement research)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsHost || !engine.IsActiveSession || research == null) return;
            Send(engine, EncodeComplete(Seq.Next(SurfaceIds.GeoResearch),
                research.Faction.Def.Guid, research.ResearchID));
            // Completion drops the item from the queue (next becomes current) — push the new order NOW.
            PushQueueSnapshot(engine);
            Debug.Log("[Multiplayer][rail] ResearchSync HOST complete " + research.ResearchID);
        }

        private static IEnumerator<NextUpdate> SendStartCrt(NetworkEngine engine, ResearchElement research)
        {
            // quiet:false = the ARCHITECTURE.md risk-1 probe: logs input object count, output byte count
            // and a host self-roundtrip (how many objects the blob actually dragged in).
            byte[] blob = SerializerRoundtrip.SerializeGraph(new object[] { research }, quiet: false);
            if (blob != null && blob.Length > MaxStartBlob)
            {
                // Graph-chase blowup (risk 1): the blob cannot ride the u16 envelope — degrade LOUDLY to a
                // value-only start (queue/progress/state still sync; the blob's requirement detail is lost).
                Debug.LogError("[Multiplayer][rail] ResearchSync: start blob " + blob.Length +
                               " bytes exceeds envelope u16 — sending value-only start for " + research.ResearchID);
                blob = null;
            }
            Debug.Log("[Multiplayer][rail] ResearchSync HOST start " + research.ResearchID +
                      (blob == null ? " (value-only)" : " blob=" + blob.Length + " bytes"));
            Send(engine, EncodeStart(Seq.Next(SurfaceIds.GeoResearch),
                research.Faction.Def.Guid, research.ResearchID, research.ResearchProgress, blob));
            yield break;
        }

        private static void Send(NetworkEngine engine, byte[] inner)
        {
            try
            {
                var env = SyncProtocol.EncodeEnvelope(SurfaceIds.GeoResearch, SyncKind.StateDelta, inner);
                engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope, env));
            }
            catch (ArgumentOutOfRangeException ex)
            {
                // Belt for any non-start message ever outgrowing the envelope: loud, never silent.
                Debug.LogError("[Multiplayer][rail] ResearchSync: payload exceeds envelope u16 — " + ex.Message);
            }
        }

        // ─── CLIENT: intent capture (fed by the Harmony prefixes below) ────

        /// <summary>
        /// The one intent-capture decision (law 4a + law 8). Returns TRUE = run the native method
        /// (host, no session, apply scope, non-viewer faction), FALSE = blocked on the client and an
        /// intent was sent instead.
        /// </summary>
        private static bool CaptureIntent(Research research, ResearchElement element, byte op, int pos)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return true;
            if (SyncApplyScope.Active) return true;              // law 8: applying a delta never echoes an intent
            if (research?.Faction == null || element == null) return false; // law 3: client never runs native logic — block, nothing to send
            if (!research.Faction.IsViewerFaction) return true;  // NPC sim paths stay native (and un-synced)

            try
            {
                var inner = EncodeIntent(++_nextIntentNonce, op, research.Faction.Def.Guid, element.ResearchID, pos);
                var env = SyncProtocol.EncodeEnvelope(SurfaceIds.GeoResearchIntent, SyncKind.ActionRequest, inner);
                engine.SendToHost(new NetworkMessage(PacketType.SyncEnvelope, env));
                Debug.Log("[Multiplayer][rail] ResearchSync CLIENT intent op=" + op + " research=" +
                          element.ResearchID + " nonce=" + _nextIntentNonce);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][rail] ResearchSync: intent send failed: " + ex); }
            return false; // client is a projector: local execution blocked, outcome arrives as host deltas
        }

        // ─── Inbound (armed as SurfaceRouter.GeoscapeInbound) ──────────────

        /// <summary>Returns true when the surface was consumed (GeoResearch / GeoResearchIntent).</summary>
        public static bool HandleInbound(NetworkEngine engine, ulong senderPeerId, byte surfaceId, byte[] payload)
        {
            if (surfaceId == SurfaceIds.GeoResearchIntent) return HandleIntent(engine, senderPeerId, payload);
            if (surfaceId != SurfaceIds.GeoResearch) return false;
            if (engine == null || engine.IsHost) return true; // host never applies its own surface
            try
            {
                using (var ms = new MemoryStream(payload))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    byte msg = r.ReadByte();
                    uint seq = r.ReadUInt32();
                    string factionGuid = r.ReadString();
                    if (!Seq.ShouldApply(SurfaceIds.GeoResearch, seq)) return true; // stale re-send
                    switch (msg)
                    {
                        case MsgStart:
                            string startId = r.ReadString();
                            float startProgress = r.ReadSingle();
                            byte[] blob = r.ReadBytes(r.ReadInt32()); // length 0 = value-only start
                            // Deserialize needs a running IUpdateable — network callbacks have none.
                            SerializerRoundtrip.RunOnTiming(GeoLevel(),
                                () => ApplyStartCrt(factionGuid, startId, startProgress, blob, seq));
                            break;
                        case MsgQueue:
                            int n = r.ReadByte();
                            var ids = new List<string>(n);
                            for (int i = 0; i < n; i++) ids.Add(r.ReadString());
                            ApplyQueue(factionGuid, ids, seq);
                            break;
                        case MsgComplete:
                            ApplyComplete(factionGuid, r.ReadString(), seq);
                            break;
                    }
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][rail] ResearchSync inbound failed: " + ex); }
            return true;
        }

        // ─── HOST: intent apply (dedup → validate → execute NATIVELY) ──────

        private static bool HandleIntent(NetworkEngine engine, ulong senderPeerId, byte[] payload)
        {
            if (engine == null || !engine.IsHost) return true; // intents are host-only inbound
            try
            {
                uint nonce; byte op; string factionGuid, researchId; int pos;
                using (var ms = new MemoryStream(payload))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    nonce = r.ReadUInt32();
                    op = r.ReadByte();
                    factionGuid = r.ReadString();
                    researchId = r.ReadString();
                    pos = r.ReadInt32();
                }
                if (!Intents.IsNew(senderPeerId, SurfaceIds.GeoResearchIntent, nonce)) return true; // double-send

                var live = LocateLive(factionGuid, researchId, out var research);
                if (live == null || research == null)
                {
                    Debug.LogWarning("[Multiplayer][rail] ResearchSync HOST intent REJECT (unknown research " +
                                     researchId + ") op=" + op + " peer=" + senderPeerId);
                    return true;
                }
                // Ownership check: LocateLive resolves ANY factionGuid — never let a client intent drive
                // NPC-faction (Anu/NJ/Synedrion/alien) research.
                if (!research.Faction.IsViewerFaction)
                {
                    Debug.LogWarning("[Multiplayer][rail] ResearchSync HOST intent REJECT (non-player faction " +
                                     factionGuid + ") op=" + op + " peer=" + senderPeerId);
                    return true;
                }

                // Validate, then execute the SAME native code the host UI would run — the observe seams
                // (OnResearchStarted / OnResearchCompleted / the queue poll) broadcast the outcome.
                bool ok;
                switch (op)
                {
                    case OpStart:
                        ok = live.State == ResearchState.Unlocked && research.CanAddToQueue(live);
                        if (ok) research.AddResearchToQueue(live);
                        break;
                    case OpCancel:
                        ok = research.ResearchQueue.Contains(live);
                        if (ok) research.Cancel(live);
                        break;
                    case OpFront:
                        ok = research.ResearchQueue.Contains(live);
                        if (ok) research.PutInFromOfQueue(live);
                        break;
                    case OpUp:
                        // Native guard is only element != Current (Research.cs:424-433): an up-click at
                        // index 1 legally displaces the current head. IndexOf > 0 matches it (-1 = absent).
                        ok = research.ResearchQueue.IndexOf(live) > 0;
                        if (ok) research.PutUpInQueue(live);
                        break;
                    case OpDown:
                        ok = research.ResearchQueue.Contains(live) && live != research.Last;
                        if (ok) research.PutDownInQueue(live);
                        break;
                    case OpInsertAt:
                        ok = research.ResearchQueue.Contains(live) && pos > 0; // never displace the current
                        if (ok) research.InsertAtPosition(live, pos);
                        break;
                    default:
                        ok = false;
                        break;
                }
                if (ok)
                {
                    // Law 11 (host side): the host's own UIModuleResearch is PULL-MODEL. We executed the
                    // native method DIRECTLY here — not through a UIModuleResearch button handler — so none
                    // of the native SetupQueue()/ShowAvailable() rebuilds ran, and the host's open research
                    // screen stays stale (this is why a client's research action shows on every OTHER peer —
                    // they mirror it through their own client-apply repaint — but NOT on the host itself).
                    // Repaint the host's screen exactly like the client-apply paths do.
                    RepaintResearchUi();
                    // Immediate push (localhost lag fix): the queue changed by this intent reaches every
                    // OTHER peer NOW instead of at the next <=2 Hz poll tick. Guarded/coalesced by _lastSentQueue.
                    PushQueueSnapshot(engine);
                    Debug.Log("[Multiplayer][rail] ResearchSync HOST intent APPLIED op=" + op + " research=" +
                              researchId + " peer=" + senderPeerId);
                }
                else
                    Debug.LogWarning("[Multiplayer][rail] ResearchSync HOST intent REJECT (invalid state) op=" +
                                     op + " research=" + researchId + " peer=" + senderPeerId);
            }
            catch (Exception ex)
            {
                // Native validation throws (AddResearchToQueue) double as the reject path — silent + logged.
                Debug.LogWarning("[Multiplayer][rail] ResearchSync HOST intent REJECT (native throw): " + ex.Message);
            }
            return true;
        }

        // ─── CLIENT: delta appliers (all inside SyncApplyScope, law 8) ─────

        private static ResearchElement LocateLive(string factionGuid, string researchId, out Research research)
        {
            research = null;
            var geo = GeoLevel();
            if (geo == null) { Debug.LogWarning("[Multiplayer][rail] ResearchSync: no geoscape level — dropping apply"); return null; }
            var faction = geo.Factions.FirstOrDefault(f => f.Def != null && f.Def.Guid == factionGuid);
            research = faction?.Research;
            var live = research?.AllResearchesArray?.FirstOrDefault(r => r.ResearchID == researchId);
            if (live == null)
                Debug.LogWarning("[Multiplayer][rail] ResearchSync: live element not found faction=" + factionGuid + " research=" + researchId);
            return live;
        }

        private static IEnumerator<NextUpdate> ApplyStartCrt(string factionGuid, string researchId, float progress, byte[] blob, uint seq)
        {
            // Deferred one frame by RunOnTiming — a newer delta may have applied in between; re-check
            // before mutating (out-of-order, law 7). A start dropped here self-heals via the MsgQueue
            // snapshot that always follows a host-side start.
            if (!Seq.ShouldApply(SurfaceIds.GeoResearch, seq)) yield break;
            var live = LocateLive(factionGuid, researchId, out var research);
            if (live == null) yield break;

            using (SyncApplyScope.Enter())
            {
                ResearchElement incoming = null;
                if (blob != null && blob.Length > 0)
                {
                    // Roundtrip probe (client side): logs incoming bytesHash + deserialized graph object count.
                    incoming = SerializerRoundtrip.DeserializeGraph(blob, typeof(ResearchElement)) as ResearchElement;
                    if (incoming == null)
                        Debug.LogError("[Multiplayer][rail] ResearchSync: DeserializeGraph yielded no ResearchElement for "
                                       + researchId + " — falling back to value-only start");
                }

                // Field-copy onto the LIVE instance (law 3) — never replace it. State via the native property
                // setter so the native Reveal/Unlock cascade + its events fire; progress verbatim;
                // IsInProgress through its private setter (BeginResearching would execute cost logic — the
                // client is a projector and must not run game logic).
                var incomingState = incoming != null ? incoming.State : ResearchState.Unlocked;
                if (live.State < ResearchState.Unlocked && incomingState >= ResearchState.Unlocked)
                    live.State = ResearchState.Unlocked;
                live.ResearchProgress = incoming != null ? incoming.ResearchProgress : progress;
                IsInProgressSetter?.Invoke(live, new object[] { true });

                // Mirror the queue membership (value-level list op on the live queue, no game logic).
                var queue = research.ResearchQueue;
                if (!queue.Contains(live)) queue.Add(live);

                // Law 11: fire the NATIVE event the push-model UI already listens to (GeoFaction relays it to
                // ResearchStartedEventHandler → GeoscapeLog). Backing delegate raised via reflection — the
                // event is only invokable from inside Research.
                if (research.Current == live)
                {
                    try { (OnResearchStartedField?.GetValue(research) as Delegate)?.DynamicInvoke(live); }
                    catch (Exception ex) { Debug.LogError("[Multiplayer][rail] ResearchSync: OnResearchStarted raise failed: " + ex); }
                }
                if (!RebuildOpenResearchScreen()) NudgeAgendaTracker();

                Seq.Mark(SurfaceIds.GeoResearch, seq);
                Debug.Log("[Multiplayer][rail] ResearchSync CLIENT applied start " + researchId +
                          " progress=" + live.ResearchProgress + " state=" + live.State +
                          " queuePos=" + queue.IndexOf(live));
            }
        }

        private static void ApplyQueue(string factionGuid, List<string> ids, uint seq)
        {
            var geo = GeoLevel();
            var faction = geo == null ? null : geo.Factions.FirstOrDefault(f => f.Def != null && f.Def.Guid == factionGuid);
            var research = faction?.Research;
            if (research == null) return;
            using (SyncApplyScope.Enter())
            {
                var queue = research.ResearchQueue;
                var desired = ids.Select(research.GetResearchById).Where(r => r != null).ToList();

                // Cancel-dropped elements keep IsInProgress=true — native Cancel never writes false, and the
                // host keeps it true; mirror host state exactly (un-marking here diverged CanResearch checks).

                // Rebuild the live list IN PLACE to the host's exact order (same List instance — the game
                // exposes it by reference via Research.ResearchQueue).
                queue.Clear();
                queue.AddRange(desired);

                // Newly queued (a non-current add fires no native start event): mirror the started markers.
                foreach (var q in desired)
                {
                    if (q.State < ResearchState.Unlocked) q.State = ResearchState.Unlocked;
                    if (!q.IsInProgress) IsInProgressSetter?.Invoke(q, new object[] { true });
                }

                if (!RebuildOpenResearchScreen()) NudgeAgendaTracker();
                Seq.Mark(SurfaceIds.GeoResearch, seq);
                Debug.Log("[Multiplayer][rail] ResearchSync CLIENT applied queue [" + string.Join(",", ids.ToArray()) + "]");
            }
        }

        private static void ApplyComplete(string factionGuid, string researchId, uint seq)
        {
            var live = LocateLive(factionGuid, researchId, out var research);
            if (live == null) return;
            using (SyncApplyScope.Enter())
            {
                // Law 3 boundary: stamp Completed through the BACKING FIELD — the native State setter would
                // run ResearchElement.Complete() = the reward chain (ApplyRewards, RewardReputation,
                // Wallet.Give), which is host-only logic. Reward side-effects (resources, diplomacy, pedia,
                // dependent reveals/unlocks) reach the client via their own subsystems / the start blobs.
                StateBackingField?.SetValue(live, ResearchState.Completed);
                live.ResearchProgress = live.ResearchCost;
                research.ResearchQueue.Remove(live);

                // Presentation (law 11): the native completed modal + geoscape log line, invoked directly on
                // their private handlers (raising the faction event would also hit GeoscapeEventSystem/stats).
                var geo = GeoLevel();
                try
                {
                    if (geo != null && geo.View != null && live.Faction != null)
                        ViewResearchCompletedMethod?.Invoke(geo.View, new object[] { live.Faction, live });
                }
                catch (Exception ex) { Debug.LogWarning("[Multiplayer][rail] ResearchSync: completed modal failed: " + ex.Message); }
                try
                {
                    if (geo != null && geo.Log != null && live.Faction != null)
                        LogResearchCompletedMethod?.Invoke(geo.Log, new object[] { live.Faction, live });
                }
                catch (Exception ex) { Debug.LogWarning("[Multiplayer][rail] ResearchSync: completed log failed: " + ex.Message); }

                if (!RebuildOpenResearchScreen()) NudgeAgendaTracker();
                Seq.Mark(SurfaceIds.GeoResearch, seq);
                Debug.Log("[Multiplayer][rail] ResearchSync CLIENT applied complete " + researchId);
            }
        }

        /// <summary>Law 11 repaint entry for the generic rail (UiEventMap): research values changed.</summary>
        internal static void RepaintResearchUi()
        {
            if (!RebuildOpenResearchScreen()) NudgeAgendaTracker();
        }

        // Law 11 (RCA 2026-07-16): UIModuleResearch is a pull-model snapshot — SetupQueue() runs only
        // at Init + its own button callbacks, ResearchQueueItem sets ProgressBar.value only in Init.
        // A delta landing while the research screen is OPEN must rebuild it in place; SetupQueue is
        // idempotent (pure re-read of Research.Current/ResearchQueue). Returns true when the screen
        // was open (tracker nudge then unnecessary: it is Uninit'd during UIStateResearch, and every
        // geoscape state re-entry re-Inits it from Research.Current — decompile UIStateVehicleSelected
        // EnterState / UIStateNothingSelected EnterState — so the return path self-heals natively).
        private static bool RebuildOpenResearchScreen()
        {
            try
            {
                var geo = GeoLevel();
                var view = geo == null ? null : geo.View;
                if (view == null || !(view.CurrentViewState is UIStateResearch)) return false;
                var module = view.GeoscapeModules == null ? null : view.GeoscapeModules.ResearchModule;
                if (module != null)
                {
                    SetupQueueMethod?.Invoke(module, null);   // queue panel (right)
                    ShowAvailableMethod?.Invoke(module, null); // available list (left) — a cancelled research
                                                               // returns here, a queued one leaves; the queue
                                                               // panel stays visible under any list tab, so
                                                               // this is safe for the reorder-observe path too.
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Multiplayer][rail] ResearchSync: research screen rebuild failed: " + ex.Message);
                return true; // screen was open — a tracker nudge would not help here
            }
        }

        // The agenda tracker (top-right research row) has NO started-event subscription — natively it
        // rebuilds on view-state Init and repolls once per second (UpdateModuleDataCrt). Setting its
        // _needsRefresh makes that native 1 s loop rebuild the rows, so the new research row appears
        // without a view-state change. ponytail: 1 s worst-case latency via the native poll; a bespoke
        // instant-rebuild call is the upgrade path if the gate finds it too slow.
        private static void NudgeAgendaTracker()
        {
            try
            {
                var tracker = UnityEngine.Object.FindObjectOfType<UIModuleFactionAgendaTracker>();
                if (tracker != null) TrackerNeedsRefreshField?.SetValue(tracker, true);
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer][rail] ResearchSync: tracker nudge failed: " + ex.Message); }
        }

        // ─── Wire codecs (inner payloads) ───────────────────────────────────

        private static byte[] EncodeStart(uint seq, string factionGuid, string researchId, float progress, byte[] blob)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(MsgStart);
                w.Write(seq);
                w.Write(factionGuid ?? "");
                w.Write(researchId ?? "");
                w.Write(progress);
                w.Write(blob == null ? 0 : blob.Length);
                if (blob != null) w.Write(blob);
                return ms.ToArray();
            }
        }

        private static byte[] EncodeQueue(uint seq, string factionGuid, List<string> researchIds)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(MsgQueue);
                w.Write(seq);
                w.Write(factionGuid ?? "");
                w.Write((byte)Math.Min(researchIds.Count, byte.MaxValue));
                for (int i = 0; i < researchIds.Count && i < byte.MaxValue; i++)
                    w.Write(researchIds[i] ?? "");
                return ms.ToArray();
            }
        }

        private static byte[] EncodeComplete(uint seq, string factionGuid, string researchId)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(MsgComplete);
                w.Write(seq);
                w.Write(factionGuid ?? "");
                w.Write(researchId ?? "");
                return ms.ToArray();
            }
        }

        private static byte[] EncodeIntent(uint nonce, byte op, string factionGuid, string researchId, int pos)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(nonce);
                w.Write(op);
                w.Write(factionGuid ?? "");
                w.Write(researchId ?? "");
                w.Write(pos);
                return ms.ToArray();
            }
        }

        // ─── Harmony seams (the ONLY patches this subsystem owns, law 4) ───

        /// <summary>Intent capture (law 4a): every research-screen entry point on Research.</summary>
        [HarmonyPatch(typeof(Research))]
        internal static class IntentCapturePatches
        {
            [HarmonyPrefix, HarmonyPatch(nameof(Research.AddResearchToQueue))]
            private static bool StartPrefix(Research __instance, ResearchElement research)
                => CaptureIntent(__instance, research, OpStart, 0);

            [HarmonyPrefix, HarmonyPatch(nameof(Research.Cancel))]
            private static bool CancelPrefix(Research __instance, ResearchElement research)
                => CaptureIntent(__instance, research, OpCancel, 0);

            [HarmonyPrefix, HarmonyPatch(nameof(Research.PutInFromOfQueue))]
            private static bool FrontPrefix(Research __instance, ResearchElement element)
                => CaptureIntent(__instance, element, OpFront, 0);

            [HarmonyPrefix, HarmonyPatch(nameof(Research.PutUpInQueue))]
            private static bool UpPrefix(Research __instance, ResearchElement element)
                => CaptureIntent(__instance, element, OpUp, 0);

            [HarmonyPrefix, HarmonyPatch(nameof(Research.PutDownInQueue))]
            private static bool DownPrefix(Research __instance, ResearchElement element)
                => CaptureIntent(__instance, element, OpDown, 0);

            [HarmonyPrefix, HarmonyPatch(nameof(Research.InsertAtPosition))]
            private static bool InsertAtPrefix(Research __instance, ResearchElement element, int position)
                => CaptureIntent(__instance, element, OpInsertAt, position);
        }

        /// <summary>
        /// Sim gating (law 4b): the client's clock is NOT frozen, so its own hourly tick would add local
        /// research progress (and locally complete research = run the reward chain twice). Skip the whole
        /// native progression tick on a client in an active session — host deltas are the only research
        /// progression source. Gates ALL factions: NPC research is frozen on the client until its
        /// subsystem migrates (host-authoritative copies arrive with diplomacy/faction sync).
        /// </summary>
        [HarmonyPatch(typeof(Research), nameof(Research.Update))]
        internal static class SimGatePatch
        {
            private static bool Prefix()
            {
                var engine = NetworkEngine.Instance;
                return engine == null || !engine.IsActiveSession || engine.IsHost;
            }
        }
    }
}
