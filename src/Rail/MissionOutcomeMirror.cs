using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Base.Core;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Geoscape.Core;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Levels;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// THE POST-MISSION SCREENS, ON EVERY PEER (user report 2026-08-01 items 3a+3b: "the panel showing what
    /// was gained — materials, technology, reputation — appeared on the HOST only, and so did the end-of-
    /// mission resupply panel").
    ///
    /// ROOT CAUSE, and it is ONE gate for BOTH panels — not two bugs. Everything a peer sees on arriving back
    /// from a battle hangs off a single branch, <c>UIStateInitial.EnterState</c>:101:
    /// <code>_params.LastMission != null &amp;&amp; (LastMission.IsCompleted || GetMissionOutcomeState() != Playing)</code>
    /// and inside it, in this order: the outcome modal (:105-112 <c>GetMissionOutcomeModal</c> →
    /// <c>OpenModalPersistent</c>), the Pandoran reveal (:114-123) and the RESUPPLY screen (:124-127
    /// <c>QueueReplenishState</c>). On a client that branch was ALWAYS FALSE, because
    /// <see cref="Multiplayer.Tactical.ClientMissionResultGate"/> blocks <c>GeoMission.Complete</c> whole —
    /// and <c>Complete</c>:267-276 is what sets BOTH of the things the branch tests (<c>Result</c> on its
    /// first line, <c>IsCompleted</c> at :275). Correct block, wrong blast radius: the campaign WRITE had to
    /// go, the two bookkeeping flags did not, and taking them with it silently removed every post-mission
    /// screen a client would ever see. The window-coverage table recorded the opposite premise — "arrival UI
    /// that fires natively on every peer that played the mission" — for both entries; it was never true.
    ///
    /// HALF ONE, THE GATE, IS THE GAME'S OWN METHOD: <c>GeoMission.CompleteSilently</c>:284-287, whose entire
    /// body is <c>IsCompleted = true</c>. It exists in the shipped game for exactly this case — record the
    /// completion, apply nothing — so the client takes it instead of the blocked <c>Complete</c>. That one
    /// call restores the resupply screen COMPLETELY (item 3b): <c>QueueReplenishState</c> is gated on this
    /// peer's own <c>GeoPhoenixFaction.GetMissingItems()</c> over its own aircraft and its own storage, all
    /// of it already mirrored, so nothing about it needs the wire. <c>Result</c> is deliberately NOT stamped:
    /// it is the host's authoritative mission result (law 3) and the branch's <c>||</c> does not need it.
    ///
    /// HALF TWO, THE CONTENT, IS THIS SURFACE. With the gate open the outcome modal now OPENS on every peer,
    /// but it renders exactly one thing — every one of the eleven outcome data binds is the same six lines
    /// ending in <c>RewardsController.SetReward(mission.Reward)</c> (ScavengeOutcomeDataBind:42,
    /// AmbushOutcomeDataBind:42, …), and <c>SetReward</c>:97-117 reads <c>reward.ApplyResult</c>, the object
    /// <c>GeoFactionReward.Apply</c>:110-112 mints. That is host-computed and NOTHING else on the rail
    /// carries it: the resources and items themselves arrive as ordinary wallet/storage state, but "what this
    /// battle gave you" is a DELTA that exists only in that one object, and the mission it hangs off leaves
    /// the graph moments later. So 0xBB ships the two lists the panel actually draws and each peer's own
    /// native modal renders them — the same posture as 0xBA cutscenes and the elite-intro rebuild: replay
    /// natively per peer, never ship rendered content.
    ///
    /// SCOPE, STATED: Resources and Items only. <c>SetReward</c> also draws stolen aircraft, stolen research,
    /// new units and captured aliens (:104-114); those are rarer, each needs its own identity resolution, and
    /// an empty list renders as nothing rather than as something wrong. They are the declared remainder.
    /// </summary>
    internal static class MissionOutcomeMirror
    {
        private static readonly SurfaceSeq Seq = new SurfaceSeq();

        /// <summary>The last outcome the host announced, waiting for this peer's own <c>Complete</c> to be
        /// gated. ONE slot, not a queue keyed by mission: <c>GeoLevelController</c>:694-711 completes exactly
        /// one <c>_missionToComplete</c> per geoscape load and stamps the screen five lines later, so a
        /// second outcome cannot be in flight — and a mission REF would be the wrong key anyway, since the
        /// client's <c>GeoMission</c> is a structural mirror the host's ref does not name.</summary>
        private static GeoFactionReward _incoming;

        internal static void Reset() { Seq.Reset(); _incoming = null; }

        // ─── HOST ───────────────────────────────────────────────────────────

        /// <summary>Broadcast what the mission actually granted. Captured at
        /// <c>GeoFaction.OnMissionRewardApplied</c> (GeoFaction.cs:910), whose ONE caller is the
        /// <c>OnMissionCompleted</c> delegate <c>GeoSite</c>:798-805 installs on every mission — it runs one
        /// line after <c>reward.Apply(...)</c>:801, so the <c>ApplyResult</c> is fully built and is the
        /// GRANTED amount, not the requested one. Mission-specific by construction, which is why the seam is
        /// here and not on <c>GeoFactionReward.Apply</c> (that one also fires for every encounter reward).</summary>
        internal static void HostBroadcast(GeoFactionRewardApplyResult result)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            var coord = engine.SaveTransfer;
            if (coord == null || !coord.SessionStarted) return;
            if (result == null) return;

            try
            {
                uint seq = Seq.Next(SurfaceIds.GeoMissionOutcome);
                byte[] inner;
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms, Encoding.UTF8))
                {
                    w.Write(seq);
                    Encode(w, result.Resources, result.Items);
                    inner = ms.ToArray();
                }
                engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope,
                    SyncProtocol.EncodeEnvelope(SurfaceIds.GeoMissionOutcome, SyncKind.StateDelta, inner)));
                Debug.Log("[MP][outcome] HOST mission reward seq=" + seq + " res=" + Count(result.Resources) +
                          " items=" + Count(result.Items));
            }
            catch (Exception ex)
            {
                // A dropped payload is a post-mission panel that opens EMPTY on every other peer — the exact
                // "model fresh, view stale" shape this repo treats as its dominant bug. Never silent.
                Debug.LogError("[MP][outcome] HOST mission reward broadcast FAILED — every other peer's " +
                               "outcome panel will show nothing gained: " + ex);
            }
        }

        // ─── WIRE (pure both ways, so RailCheck L83 round-trips it headless) ──

        /// <summary>[resCount:u16]([resourceType:i32][value:f32])* [itemCount:u16]([ItemRec])*. Resources ride
        /// as the game's own <c>ResourceType</c> enum (a def-order-free stable id) and items reuse
        /// <see cref="GeoItemCodec"/>'s record — def guid + count + charges + malfunction, law 2 addressing,
        /// no instance identity, because the panel draws a LIST and not the peer's storage.</summary>
        internal static void Encode(BinaryWriter w, ResourcePack resources, ItemStorage items)
        {
            var res = resources == null ? new List<ResourceUnit>() : resources.Values;
            w.Write((ushort)res.Count);
            foreach (var u in res) { w.Write((int)u.Type); w.Write(u.Value); }

            var list = items == null ? new List<GeoItem>() : items.ToList();
            w.Write((ushort)list.Count);
            foreach (var it in list) GeoItemCodec.WriteRec(w, GeoItemCodec.RecOf(it));
        }

        /// <summary>The inverse. Items whose def does not resolve are DROPPED by
        /// <c>GeoItemCodec.FromRec</c>, loudly — mod parity (law 10) says that cannot happen, and a null in
        /// the list would NRE inside the native renderer's own <c>foreach</c>.</summary>
        internal static void Decode(BinaryReader r, out ResourcePack resources, out ItemStorage items)
        {
            resources = new ResourcePack();
            int n = r.ReadUInt16();
            for (int i = 0; i < n; i++)
            {
                var type = (ResourceType)r.ReadInt32();
                float value = r.ReadSingle();
                resources.Add(new ResourceUnit(type, value));
            }
            items = new ItemStorage();
            int m = r.ReadUInt16();
            for (int i = 0; i < m; i++)
            {
                var item = GeoItemCodec.FromRec(GeoItemCodec.ReadRec(r)) as GeoItem;
                if (item != null) items.AddItem(item);
            }
        }

        // ─── CLIENT ─────────────────────────────────────────────────────────

        /// <summary>Returns true when the surface was consumed. Client-only: the host already drew its own.</summary>
        internal static bool HandleInbound(NetworkEngine engine, ulong senderPeerId, byte surfaceId, byte[] payload)
        {
            if (surfaceId != SurfaceIds.GeoMissionOutcome) return false;
            if (engine == null || engine.IsHost) return true;
            try
            {
                using (var ms = new MemoryStream(payload ?? new byte[0]))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    if (!Seq.ShouldApply(SurfaceIds.GeoMissionOutcome, seq)) return true; // stale (law 7)
                    Decode(r, out var resources, out var items);
                    // BOTH halves, and they are read by DIFFERENT methods: HasRewards():96-108 gates on the
                    // reward's OWN lists and SetReward():102-103 draws the ApplyResult's. Filling one leaves
                    // either a panel that refuses to draw or a panel that draws nothing.
                    _incoming = new GeoFactionReward
                    {
                        Reason = "Mission",
                        Resources = resources,
                        Items = items,
                        ApplyResult = new GeoFactionRewardApplyResult { Resources = resources, Items = items },
                    };
                    Seq.Mark(SurfaceIds.GeoMissionOutcome, seq);
                    Debug.Log("[MP][outcome] CLIENT mission reward seq=" + seq + " res=" + Count(resources) +
                              " items=" + Count(items) + " — held for this peer's own mission completion");
                }
            }
            catch (Exception ex) { Debug.LogError("[MP][outcome] inbound failed: " + ex); }
            return true;
        }

        // GeoMission.Reward has a private setter (GeoMission.cs:145) — the one reason reflection appears here.
        private static readonly System.Reflection.MethodInfo RewardSetter =
            AccessTools.PropertySetter(typeof(GeoMission), "Reward");

        /// <summary>The client's stand-in for the blocked <c>Complete</c>: the game's own bookkeeping flag,
        /// plus the host's reward so the native panel has something to draw. Called from
        /// <see cref="Multiplayer.Tactical.ClientMissionResultGate"/>, i.e. from
        /// <c>GeoLevelController</c>:703 — FIVE LINES before :708 hands the same mission to
        /// <c>UIStateInitial</c>, in the same coroutine step, which is what makes a stash safe here and why
        /// no repaint seam is needed: nothing has read the mission yet.</summary>
        internal static void StampMirroredOutcome(GeoMission mission)
        {
            if (mission == null) return;
            mission.CompleteSilently();   // the game's own "record it, apply nothing" (GeoMission.cs:284)

            var reward = _incoming;
            _incoming = null;
            if (reward == null)
            {
                // The panel still OPENS (the gate above is what decides that) and reads "nothing gained".
                // Loud, because the alternative reading — "this mission really granted nothing" — is one a
                // player cannot tell apart from a lost message.
                Debug.LogWarning("[MP][outcome] no host reward payload had arrived when this peer completed its " +
                                 "mission — the outcome panel will open EMPTY. The 0xBB raise is sent as the host " +
                                 "applies the reward, so this means the host had not got there yet.");
                reward = new GeoFactionReward { Reason = "Mission" };
            }
            if (RewardSetter == null)
            {
                Debug.LogError("[MP][outcome] GeoMission.Reward setter did not resolve — the outcome panel " +
                               "renders whatever this peer's mission already held, which is nothing.");
                return;
            }
            RewardSetter.Invoke(mission, new object[] { reward });
            Debug.Log("[MP][outcome] CLIENT stamped mission outcome — CompleteSilently + the host's reward " +
                      "(res=" + Count(reward.Resources) + " items=" + Count(reward.Items) + "); the native " +
                      "UIStateInitial:101 branch now runs the outcome modal and the resupply screen.");
        }

        private static int Count(ResourcePack p) => p?.Values?.Count ?? 0;
        private static int Count(ItemStorage s) => s == null ? 0 : s.ToList().Count;
    }

    /// <summary>The host-side capture. POSTFIX so the reward is already applied and the result is the GRANTED
    /// amount; host-gated inside <see cref="MissionOutcomeMirror.HostBroadcast"/>, so a client reaching this
    /// (it cannot — its <c>Complete</c> is gated one level up) would still send nothing.</summary>
    [HarmonyPatch(typeof(GeoFaction), nameof(GeoFaction.OnMissionRewardApplied))]
    internal static class MissionRewardBroadcast
    {
        private static void Postfix(GeoFactionRewardApplyResult result) =>
            MissionOutcomeMirror.HostBroadcast(result);
    }
}
