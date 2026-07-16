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
    /// SPIKE A (ARCHITECTURE.md): host starts research → client research bar moves live, no reload.
    /// Proves the load-bearing rail assumption: the native save Serializer works as a live per-entity
    /// blob mechanism AND a field-copy apply onto the LIVE client instance preserves runtime invariants.
    ///
    /// HOST (observe seam, no Harmony patch — a plain event subscription on the native
    /// Research.OnResearchStarted): serialize the ONE started ResearchElement via SerializerRoundtrip
    /// (risk-1 probe logs blob byte + object count) and broadcast (factionDefGuid, researchDefGuid, blob)
    /// on the GeoResearch surface. Then a ≤2 Hz pure value tick (defGuid + progress float) — zero traffic
    /// while nothing changes.
    ///
    /// CLIENT (projector, law 3): defer onto the level Timing (network callbacks have no Timing.Current),
    /// DeserializeGraph → locate the LIVE element by ResearchDef key (ResearchID) → field-copy progress/
    /// state onto it (NEVER replace the instance) → fire the native Research.OnResearchStarted so the
    /// push-model geoscape UI (GeoscapeLog, tutorial) repaints, and nudge the agenda tracker's native
    /// 1 s refresh loop (its progress row polls Research.GetTotalTimeLeft — the poll IS its native
    /// repaint path, law 6).
    ///
    /// Law 7: host→all deltas ride SurfaceSeq (per-surface last-writer-wins). IntentDedup is N/A — this
    /// spike has no client→host intent (host-side observe only).
    /// Risk 2 (client double-execution) is ACCEPTED for the spike: the client's own hourly research tick
    /// still adds local progress; the 2 Hz host delta overwrites it. Sim-gating lands in migration step 2.
    /// </summary>
    public static class ResearchSpike
    {
        // Inner payload discriminator (the SurfaceRouter hands us surfaceId + payload, not the SyncKind).
        private const byte MsgStart = 1;
        private const byte MsgProgress = 2;

        private static readonly SurfaceSeq Seq = new SurfaceSeq();

        // ─── Host observe state ────────────────────────────────────────────
        private static Research _hooked;                 // faction Research we subscribed to (unhook on level change)
        private static float _nextTickAt;                // realtime throttle (0.5 s → max 2 Hz)
        private static string _lastSentId;
        private static float _lastSentProgress = float.NaN;

        // ─── Reflection (private members only; everything else is typed) ──
        private static readonly System.Reflection.MethodInfo IsInProgressSetter =
            AccessTools.PropertySetter(typeof(ResearchElement), "IsInProgress");
        private static readonly System.Reflection.FieldInfo OnResearchStartedField =
            AccessTools.Field(typeof(Research), "OnResearchStarted");
        private static readonly System.Reflection.FieldInfo TrackerNeedsRefreshField =
            AccessTools.Field(typeof(UIModuleFactionAgendaTracker), "_needsRefresh");
        private static readonly System.Reflection.MethodInfo SetupQueueMethod =
            AccessTools.Method(typeof(UIModuleResearch), "SetupQueue");

        // ─── Lifecycle (driven by SyncEngine) ──────────────────────────────

        public static void Reset()
        {
            Unhook();
            Seq.Reset();
            _lastSentId = null;
            _lastSentProgress = float.NaN;
        }

        private static void Unhook()
        {
            if (_hooked == null) return;
            try { _hooked.OnResearchStarted -= HostOnResearchStarted; } catch { }
            _hooked = null;
        }

        private static GeoLevelController GeoLevel()
        {
            var level = GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<GeoLevelController>();
        }

        // ─── HOST: hook management + ≤2 Hz progress value tick ─────────────

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
                _hooked = research;
                Debug.Log("[Multiplayer][rail] ResearchSpike: hooked OnResearchStarted (host)");
            }

            // Pure value delta — sent ONLY when (id, progress) actually changed. Idle = zero traffic.
            var cur = research.Current;
            if (cur == null) { _lastSentId = null; _lastSentProgress = float.NaN; return; }
            if (cur.ResearchID == _lastSentId && cur.ResearchProgress == _lastSentProgress) return;
            _lastSentId = cur.ResearchID;
            _lastSentProgress = cur.ResearchProgress;
            Send(engine, EncodeProgress(Seq.Next(SurfaceIds.GeoResearch),
                cur.Faction.Def.Guid, cur.ResearchID, cur.ResearchProgress));
        }

        private static void HostOnResearchStarted(ResearchElement research)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsHost || !engine.IsActiveSession || research == null) return;
            // The serializer coroutine needs a running IUpdateable — a UI-click start has no
            // Timing.Current, so defer onto the geoscape level Timing (GeoLevelController.Timing).
            SerializerRoundtrip.RunOnTiming(GeoLevel(), () => SendStartCrt(engine, research));
        }

        private static IEnumerator<NextUpdate> SendStartCrt(NetworkEngine engine, ResearchElement research)
        {
            // quiet:false = the ARCHITECTURE.md risk-1 probe: logs input object count, output byte count
            // and a host self-roundtrip (how many objects the blob actually dragged in).
            byte[] blob = SerializerRoundtrip.SerializeGraph(new object[] { research }, quiet: false);
            if (blob == null)
            {
                Debug.LogError("[Multiplayer][rail] ResearchSpike: SerializeGraph returned null for " + research.ResearchID);
                yield break;
            }
            Debug.Log("[Multiplayer][rail] ResearchSpike HOST start " + research.ResearchID +
                      " blob=" + blob.Length + " bytes");
            Send(engine, EncodeStart(Seq.Next(SurfaceIds.GeoResearch),
                research.Faction.Def.Guid, research.ResearchID, blob));
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
                // Risk-1 blowup made LOUD: the blob dragged more than the u16 envelope can carry.
                Debug.LogError("[Multiplayer][rail] ResearchSpike: payload exceeds envelope u16 — graph-chase blowup? " + ex.Message);
            }
        }

        // ─── CLIENT: inbound (armed as SurfaceRouter.GeoscapeInbound) ──────

        /// <summary>Returns true when the surface was consumed (GeoResearch only).</summary>
        public static bool HandleInbound(NetworkEngine engine, byte surfaceId, byte[] payload)
        {
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
                    string researchId = r.ReadString();
                    if (!Seq.ShouldApply(SurfaceIds.GeoResearch, seq)) return true; // stale re-send
                    switch (msg)
                    {
                        case MsgStart:
                            byte[] blob = r.ReadBytes(r.ReadInt32());
                            // Deserialize needs a running IUpdateable — network callbacks have none.
                            SerializerRoundtrip.RunOnTiming(GeoLevel(),
                                () => ApplyStartCrt(factionGuid, researchId, blob, seq));
                            break;
                        case MsgProgress:
                            float progress = r.ReadSingle();
                            ApplyProgress(factionGuid, researchId, progress, seq);
                            break;
                    }
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][rail] ResearchSpike inbound failed: " + ex); }
            return true;
        }

        private static ResearchElement LocateLive(string factionGuid, string researchId, out Research research)
        {
            research = null;
            var geo = GeoLevel();
            if (geo == null) { Debug.LogWarning("[Multiplayer][rail] ResearchSpike: no geoscape level — dropping apply"); return null; }
            var faction = geo.Factions.FirstOrDefault(f => f.Def != null && f.Def.Guid == factionGuid);
            research = faction?.Research;
            var live = research?.AllResearchesArray?.FirstOrDefault(r => r.ResearchID == researchId);
            if (live == null)
                Debug.LogWarning("[Multiplayer][rail] ResearchSpike: live element not found faction=" + factionGuid + " research=" + researchId);
            return live;
        }

        private static IEnumerator<NextUpdate> ApplyStartCrt(string factionGuid, string researchId, byte[] blob, uint seq)
        {
            var live = LocateLive(factionGuid, researchId, out var research);
            if (live == null) yield break;

            // Roundtrip probe (client side): logs incoming bytesHash + deserialized graph object count.
            var incoming = SerializerRoundtrip.DeserializeGraph(blob, typeof(ResearchElement)) as ResearchElement;
            if (incoming == null)
            {
                Debug.LogError("[Multiplayer][rail] ResearchSpike: DeserializeGraph yielded no ResearchElement for " + researchId);
                yield break;
            }

            // Field-copy onto the LIVE instance (law 3) — never replace it. State via the native property
            // setter so the native Reveal/Unlock cascade + its events fire (law 6); progress verbatim;
            // IsInProgress through its private setter (BeginResearching would execute cost logic — the
            // client is a projector and must not run game logic).
            if (live.State < ResearchState.Unlocked && incoming.State >= ResearchState.Unlocked)
                live.State = ResearchState.Unlocked;
            live.ResearchProgress = incoming.ResearchProgress;
            IsInProgressSetter?.Invoke(live, new object[] { true });

            // Mirror the queue membership (value-level list op on the live queue, no game logic).
            var queue = research.ResearchQueue;
            if (!queue.Contains(live)) queue.Add(live);

            // Law 6: fire the NATIVE event the push-model UI already listens to (GeoFaction relays it to
            // ResearchStartedEventHandler → GeoscapeLog). Backing delegate raised via reflection — the
            // event is only invokable from inside Research.
            if (research.Current == live)
            {
                try { (OnResearchStartedField?.GetValue(research) as Delegate)?.DynamicInvoke(live); }
                catch (Exception ex) { Debug.LogError("[Multiplayer][rail] ResearchSpike: OnResearchStarted raise failed: " + ex); }
            }
            if (!RebuildOpenResearchScreen()) NudgeAgendaTracker();

            Seq.Mark(SurfaceIds.GeoResearch, seq);
            Debug.Log("[Multiplayer][rail] ResearchSpike CLIENT applied start " + researchId +
                      " progress=" + live.ResearchProgress + " state=" + live.State +
                      " queuePos=" + queue.IndexOf(live));
        }

        private static void ApplyProgress(string factionGuid, string researchId, float progress, uint seq)
        {
            var live = LocateLive(factionGuid, researchId, out _);
            if (live == null) return;
            live.ResearchProgress = progress; // pure value-copy
            RebuildOpenResearchScreen(); // ProgressBar.value is set only in ResearchQueueItem.Init — must rebuild
            Seq.Mark(SurfaceIds.GeoResearch, seq);
            Debug.Log("[Multiplayer][rail] ResearchSpike CLIENT progress " + researchId + " = " + progress);
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
                if (module != null) SetupQueueMethod?.Invoke(module, null);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Multiplayer][rail] ResearchSpike: research screen rebuild failed: " + ex.Message);
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
            catch (Exception ex) { Debug.LogWarning("[Multiplayer][rail] ResearchSpike: tracker nudge failed: " + ex.Message); }
        }

        // ─── Wire codecs (inner payload of the GeoResearch envelope) ───────

        private static byte[] EncodeStart(uint seq, string factionGuid, string researchId, byte[] blob)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(MsgStart);
                w.Write(seq);
                w.Write(factionGuid ?? "");
                w.Write(researchId ?? "");
                w.Write(blob.Length);
                w.Write(blob);
                return ms.ToArray();
            }
        }

        private static byte[] EncodeProgress(uint seq, string factionGuid, string researchId, float progress)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(MsgProgress);
                w.Write(seq);
                w.Write(factionGuid ?? "");
                w.Write(researchId ?? "");
                w.Write(progress);
                return ms.ToArray();
            }
        }
    }
}
