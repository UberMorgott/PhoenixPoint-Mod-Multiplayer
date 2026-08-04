using System;
using System.IO;
using System.Linq;
using System.Text;
using Base.Core;
using Base.Defs;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Network.Sync;
using Multiplayer.Rail;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Common.Levels.MapGeneration;
using PhoenixPoint.Common.Levels.Params;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Tactical.Levels.Missions;
using UnityEngine;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// EXPERIMENT (law L103) — TACTICAL ENTRY WITHOUT THE SAVE TRANSFER: every peer BUILDS its own battle
    /// natively from the host's generation RESULT, the way the tactical→geoscape return already builds its
    /// own geoscape. Off by default (<see cref="Enabled"/>); the save transfer stays the shipping path and
    /// every one of its members stays reachable.
    ///
    /// WHAT IS SHIPPED, AND WHY IT IS THE RESULT AND NOT A SEED. <c>GeoMission.PrepareTacticalGame</c>
    /// (GeoMission.cs:305, sole caller <c>Launch</c>:239, fully synchronous) turns the geoscape into ONE
    /// <c>[SerializeType]</c> object — <c>TacticalGameParams</c> (TacticalGameParams.cs:22-24) carrying the
    /// whole <c>TacMissionData</c> (participants, every <c>ActorDeployData</c>, crates, reinforcement counts,
    /// the mission id) — wrapped in a <c>LevelSceneBinding</c> off the chosen plot (<c>PrepareLevel</c>:764,
    /// :770 <c>plotDef.LevelSceneDef.Binding.CloneAndAddLevelParams</c>). Kilobytes against a megabyte-scale
    /// save. Shipping that RESULT is strictly stronger than shipping the seed it was rolled from: a seed
    /// only reproduces the roll if every input matches on the client — <c>geoLevel.AvailablePlots</c>, the
    /// squad's <c>GenerateActorDeployData</c>, difficulty, deployment points, the whole def graph — while the
    /// result needs none of that to be true. It also carries <c>MissionData.MissionId</c>
    /// (<c>MissionId.CreateNew()</c> = <c>Guid.NewGuid()</c>, GeoMission.cs:769), which no seed can reach.
    ///
    /// TWO FIELDS DO NOT RIDE THE GAME'S OWN SERIALIZER and are shipped/rebuilt beside it, verified against
    /// the type: <c>MapPlotInstanceData</c> is <c>[NonSerialized]</c> and <c>IsInMist</c> is
    /// <c>SerializeMode.DoNotSerialize</c>. The parcel layout is not re-rolled blindly — the client prefers
    /// its OWN mirrored <c>GeoSite.MapPlotInstanceData</c> (already replicated, docs/rail-baseline.txt:698)
    /// and only falls back to the host's own recipe <c>plotDef.GenerateInstanceData(gen, new
    /// System.Random(randomSeed))</c> (GeoMission.cs:762-764) when the site carries none.
    ///
    /// THE CLIENT RE-ENTERS THE GAME'S OWN LAUNCH, not a bespoke loader: <c>GeoLevelController
    /// .LaunchTacticalGame</c> (:1387) → <c>LaunchTacticalGameCrt</c> (:1412) gives it asset validation,
    /// the curtain, <c>AutosaveGame</c> and <c>FinishLevel</c> for free. That autosave is not incidental —
    /// it is what fills <c>PhoenixSaveManager._currentGeoscapeSection</c> (PhoenixSaveManager.cs:227, the
    /// <c>!IsTactical</c> branch), which <c>LoadCurrentGeoscape</c>:382 dereferences on the post-mission
    /// return. On the save-transfer path that section has to be lifted out of the host's blob by hand
    /// (<c>SaveTransferCoordinator.PrepareEntryFromBlobCrt</c>, the <c>IsTacticalSave</c> branch); here every
    /// peer stashes its OWN geoscape and returns to it.
    ///
    /// THE SEED BRACKET COVERS THE GENERATION WINDOW ONLY, AND IT IS THE SPAWN WINDOW — NOT
    /// <c>PrepareTacticalGame</c>. The client never runs <c>PrepareTacticalGame</c> at all (it consumes the
    /// host's params), so a bracket there would seed a method only one peer executes. What both peers DO run
    /// is the level build, and its two unseeded points are plain synchronous voids:
    ///   • <c>TacMission.RunMissionActivators</c> (TacMission.cs:183, called from <c>Init</c>:75, itself
    ///     called from <c>TacticalLevelController.BeforePlaying</c>:533) — <c>SharedData.Random.Next() %
    ///     list2.Count</c> picks the DEPLOYMENT SET.
    ///   • <c>TacMission.OnLevelStart</c> (TacMission.cs:275, called from
    ///     <c>TacticalLevelController.OnLevelStart</c>:642 with no yield between) — <c>InitDeployZones</c> +
    ///     every participant's <c>Initialize</c> + <c>DeployForTurn(0)</c>, i.e. the whole initial
    ///     deployment: <c>TacticalDeployZone.GetOrderedSpawnPositions</c>:277 <c>Shuffle(SharedData.Random)</c>
    ///     and <c>TacParticipantSpawn</c>'s <c>GetRandomElement(SharedData.Random)</c> placement draws.
    /// Neither yields, so nothing interleaves and the bracket cannot leak. <c>UnityEngine.Random.state</c> is
    /// ordinary IL (only <c>InitState</c>/<c>value</c>/<c>Range</c> are InternalCall), so the stream is SAVED
    /// and RESTORED around each; <c>SharedData.Random</c> is a public field (SharedData.cs:70) swapped for a
    /// freshly seeded <c>System.Random</c> and put back. A <c>Finalizer</c> and not a postfix, so a throw
    /// inside the window still restores.
    ///
    /// KNOWN GAPS, STATED PLAINLY — THIS IS WHY THE SWITCH DEFAULTS TO THE SAVE TRANSFER:
    ///   1. TFTV RE-SEEDS THE GLOBAL STREAM FROM THE WALL CLOCK. <c>UnityEngine.Random.InitState((int)
    ///      Stopwatch.GetTimestamp())</c> appears ~15 times in TFTV (e.g. TFTVBaseDefenseTactical.cs:1511,
    ///      1852, 2304, 2364, 2570, 2665) alongside <c>new System.Random((int)Stopwatch.GetTimestamp())</c>
    ///      (:2310, :2336, :2370) choosing DEPLOYED ENEMIES. Any one of those reached inside the bracket
    ///      makes the two peers' boards diverge and no bracket of ours can prevent it. TFTV's own deployment
    ///      patches (<c>TacParticipantSpawn.GetEligibleActorDeployments</c> /
    ///      <c>DoSpawnActor</c>, TFTVTacticalDeploymentEnemies.cs:331/369) draw nothing themselves.
    ///   2. THE INITIAL DEPLOYMENT IS NOT REPLICATED. <c>ClientDeployGate</c>
    ///      (TacticalActorLifecycle.cs:536) suppresses the client's <c>DeployForTurn</c> for EVERY turn
    ///      including turn 0, and the host's own battle-start actors ship no 0x84 spawn records (the capture
    ///      is armed only after <c>TacticalActorKey.Built</c>, because on the save path both peers already
    ///      had them). Closing this is the next step and it lives in files this pass does not own: either the
    ///      turn-0 deployment ships as ordinary spawn records, or the client's gate opens for turn 0 and
    ///      leans on the bracket — which gap 1 says it cannot, under TFTV.
    /// Until gap 2 is closed a client entering natively arrives on a board with no enemies. The fallback
    /// below is therefore not a safety net, it is the current answer.
    ///
    /// THE FALLBACK IS THE UNTOUCHED SAVE TRANSFER. Any failure on the client — params that deserialize
    /// empty, a plot def that will not resolve, no mirrored mission — sends op 2 to the host, which arms
    /// <see cref="FallbackArmed"/>; <c>TacDeployReadyCapture</c> then runs
    /// <c>HostBeginTacticalEntryTransfer</c> exactly as it does today. Nothing in the save path is deleted,
    /// gated or reordered, and flipping <see cref="Enabled"/> to false restores today's behaviour in one line.
    /// </summary>
    internal static class NativeTacticalEntry
    {
        /// <summary>THE SWITCH. false = the shipping save-transfer entry (proven). true = the experiment.
        /// Defaults to false because gap 2 above is open: nothing yet replicates the initial deployment, so
        /// a natively-built client board is EMPTY of enemies. Flip only together with that work.
        /// <para>DELIBERATELY <c>static readonly</c> AND NOT <c>const</c>: a const folds
        /// <c>if (Enabled &amp;&amp; …)</c> away at compile time, which would delete the fallback wiring
        /// (<c>TacDeployReadyCapture</c>'s <see cref="FallbackArmed"/> read) out of the IL entirely — and a
        /// retreat that is not in the assembly is not a retreat. RailCheck L103 arm (d) reads that IL.</para></summary>
        internal static readonly bool Enabled = false;

        internal const byte OpEntry = 1;     // host→all: the generation result
        internal const byte OpFallback = 2;  // client→host: "I cannot build this locally — send the save"

        /// <summary>True while THIS peer is replaying the host's entry through the game's own
        /// <c>LaunchTacticalGame</c>. The one thing that lets a client past <c>TacLaunchGate</c>'s
        /// self-launch block — a client still never GENERATES a battle, it only plays back the host's.</summary>
        internal static bool Replaying { get; private set; }

        /// <summary>Host-side: a peer asked for the save instead. Read by <c>TacDeployReadyCapture</c>.</summary>
        internal static bool FallbackArmed { get; private set; }

        private static int _spawnSeed;
        private static bool _seedArmed;

        // Bracket state. `_held` is the pairing flag: a nested Enter takes nothing and restores nothing.
        private static UnityEngine.Random.State _savedUnityState;
        private static System.Random _savedShared;
        private static SharedData _bracketShared;
        private static bool _held;

        // ─── HOST: capture the generation result and ship it ───────────────

        /// <summary>Host-side capture. A POSTFIX on the generation itself rather than on the launch, because
        /// <c>Launch</c>:239 hands the result straight to <c>OnMissionActivated</c> and the params object is
        /// mutated after that (<c>SetMissionLevelCinematic</c> writes <c>EnterMissionCutscene</c> — already
        /// done by :346, one line before the return).</summary>
        internal static void HostCapture(GeoMission mission, PlayTacticalGameLevelResult result)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            var coord = engine.SaveTransfer;
            if (coord == null || !coord.SessionStarted) return;

            FallbackArmed = false;
            var tgp = result?.LevelSceneBinding?.LevelParams as TacticalGameParams;
            var site = mission?.Site;
            var plotDef = tgp?.MapPlotInstanceData?.MapPlotDef;
            if (tgp == null || site == null || plotDef == null)
            {
                Debug.LogError("[Multiplayer][tac-native] host capture found no params/site/plot (params=" +
                               (tgp == null ? "null" : "ok") + " site=" + (site == null ? "null" : "ok") +
                               " plot=" + (plotDef == null ? "null" : "ok") + ") — falling back to the save transfer.");
                FallbackArmed = true;
                return;
            }

            _spawnSeed = site.RandomSeed;
            _seedArmed = true;

            // The native Serializer needs the engine's CONFIGURED instance plus a Timing pump, both of which
            // SerializerRoundtrip owns; this runs inside the geoscape's own update (Launch is driven from the
            // level's Timing), so no RunOnTiming deferral is needed here.
            byte[] paramBytes = SerializerRoundtrip.SerializeGraph(new object[] { tgp });
            if (paramBytes == null || paramBytes.Length == 0)
            {
                Debug.LogError("[Multiplayer][tac-native] TacticalGameParams serialized to nothing — the native " +
                               "entry cannot ship a battle it cannot encode. Falling back to the save transfer.");
                FallbackArmed = true;
                return;
            }

            try
            {
                byte[] inner;
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms, Encoding.UTF8))
                {
                    w.Write(OpEntry);
                    w.Write(site.SiteId);
                    w.Write(_spawnSeed);
                    w.Write((byte)(tgp.IsInMist ? 1 : 0));
                    w.Write(plotDef.Guid ?? "");
                    w.Write(paramBytes.Length);
                    w.Write(paramBytes);
                    inner = ms.ToArray();
                }
                engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope,
                    SyncProtocol.EncodeEnvelope(SurfaceIds.TacEntryParams, SyncKind.StateSnapshot, inner)));
                Debug.Log("[Multiplayer][tac-native] HOST shipped entry params site=" + site.SiteId +
                          " plot=" + plotDef.name + " seed=" + _spawnSeed + " bytes=" + paramBytes.Length);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac-native] entry params FAILED to reach the wire (" + ex.Message +
                               ") — falling back to the save transfer.");
                FallbackArmed = true;
            }
        }

        // ─── Inbound: client applies the entry, host takes a fallback request ──

        internal static bool HandleInbound(NetworkEngine engine, ulong senderPeerId, byte surfaceId, byte[] payload)
        {
            if (surfaceId != SurfaceIds.TacEntryParams) return false;
            if (!Enabled) return true;   // the surface is ours either way — never let it fall to the geoscape hook
            try
            {
                using (var ms = new MemoryStream(payload ?? new byte[0]))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    byte op = r.ReadByte();
                    if (op == OpFallback)
                    {
                        if (!engine.IsHost) return true;
                        string reason = r.ReadString();
                        Debug.LogWarning("[Multiplayer][tac-native] peer " + senderPeerId + " cannot build this " +
                                         "battle locally (" + reason + ") — arming the save-transfer fallback.");
                        FallbackArmed = true;
                        return true;
                    }
                    if (op != OpEntry)
                    {
                        Debug.LogError("[Multiplayer][tac-native] unknown entry op " + op + " — asking for the save.");
                        RequestFallback("unknown op " + op);
                        return true;
                    }
                    if (engine.IsHost) return true;   // the host ignores its own broadcast

                    int siteId = r.ReadInt32();
                    int seed = r.ReadInt32();
                    bool inMist = r.ReadByte() != 0;
                    string plotGuid = r.ReadString();
                    int len = r.ReadInt32();
                    if (len <= 0 || len > ms.Length) { RequestFallback("params length " + len + " out of bounds"); return true; }
                    byte[] paramBytes = r.ReadBytes(len);

                    // Network callbacks run outside any IUpdateable and the native Read coroutine reads
                    // Timing.Current — defer onto a live Timing before touching the serializer.
                    SerializerRoundtrip.RunOnTiming(GameUtl.CurrentLevel(),
                        () => ClientEnterCrt(siteId, seed, inMist, plotGuid, paramBytes));
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac-native] entry message unreadable: " + ex);
                RequestFallback("entry message unreadable");
            }
            return true;
        }

        private static System.Collections.Generic.IEnumerator<NextUpdate> ClientEnterCrt(
            int siteId, int seed, bool inMist, string plotGuid, byte[] paramBytes)
        {
            var geo = GameUtl.CurrentLevel()?.GetComponent<GeoLevelController>();
            var site = geo?.Map?.AllSites?.FirstOrDefault(s => s != null && s.SiteId == siteId);
            var mission = site?.ActiveMission;
            var repo = GameUtl.GameComponent<DefRepository>();
            var plotDef = string.IsNullOrEmpty(plotGuid) || repo == null ? null : repo.GetDef(plotGuid) as MapPlotDef;
            var tgp = SerializerRoundtrip.DeserializeGraph(paramBytes, typeof(TacticalGameParams)) as TacticalGameParams;

            if (geo == null || site == null || mission == null || plotDef == null || tgp == null ||
                plotDef.LevelSceneDef == null)
            {
                RequestFallback("geo=" + (geo == null ? "null" : "ok") + " site=" + (site == null ? "null" : "ok") +
                                " mission=" + (mission == null ? "null" : "ok") + " plot=" +
                                (plotDef == null ? "null" : "ok") + " params=" + (tgp == null ? "EMPTY" : "ok"));
                yield break;
            }

            // The two fields the game's own serializer does not carry (see the class doc).
            tgp.IsInMist = inMist;
            bool mirroredLayout = site.MapPlotInstanceData != null;
            tgp.MapPlotInstanceData = site.MapPlotInstanceData ??
                plotDef.GenerateInstanceData(tgp.MapPlotGenerationData,
                                             new System.Random(tgp.MapPlotGenerationData.RandomSeed));
            site.MapPlotInstanceData = tgp.MapPlotInstanceData;   // the host caches it on the site too (GeoMission:764)

            _spawnSeed = seed;
            _seedArmed = true;

            var binding = plotDef.LevelSceneDef.Binding.CloneAndAddLevelParams(tgp);
            Debug.Log("[Multiplayer][tac-native] CLIENT building the battle locally: site=" + siteId +
                      " plot=" + plotDef.name + " seed=" + seed + " layout=" +
                      (mirroredLayout ? "mirrored" : "re-rolled from the site seed"));

            Replaying = true;
            try
            {
                geo.LaunchTacticalGame(mission, new PlayTacticalGameLevelResult { LevelSceneBinding = binding });
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac-native] CLIENT native launch threw: " + ex);
                RequestFallback("native launch threw");
            }
            finally { Replaying = false; }
        }

        /// <summary>Client→host: "build me the save instead." Idempotent — the host only ever sets a flag,
        /// and <c>TacDeployReadyCapture</c> reads it once at deploy-ready.</summary>
        internal static void RequestFallback(string reason)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return;
            if (engine.IsHost) { FallbackArmed = true; return; }
            Debug.LogWarning("[Multiplayer][tac-native] asking the host for the save transfer instead (" + reason + ")");
            try
            {
                byte[] inner;
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms, Encoding.UTF8))
                {
                    w.Write(OpFallback);
                    w.Write(reason ?? "");
                    inner = ms.ToArray();
                }
                engine.SendToHost(new NetworkMessage(PacketType.SyncEnvelope,
                    SyncProtocol.EncodeEnvelope(SurfaceIds.TacEntryParams, SyncKind.ActionRequest, inner)));
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac-native] the fallback request itself failed to send (" + ex.Message +
                               ") — this peer will sit behind the curtain until the reveal deadline.");
            }
        }

        // ─── The seed bracket ──────────────────────────────────────────────

        /// <summary>Take the RNG for one synchronous generation window. Returns false (and takes nothing) when
        /// the experiment is off, no entry armed a seed, or a bracket is already held.</summary>
        internal static bool EnterSeed(string what)
        {
            if (!Enabled || !_seedArmed || _held) return false;
            _bracketShared = SharedData.GetSharedDataFromGame();
            _savedUnityState = UnityEngine.Random.state;
            _savedShared = _bracketShared?.Random;
            UnityEngine.Random.InitState(_spawnSeed);
            if (_bracketShared != null) _bracketShared.Random = new System.Random(_spawnSeed);
            _held = true;
            Debug.Log("[Multiplayer][tac-native] RNG bracket ENTER " + what + " seed=" + _spawnSeed);
            return true;
        }

        /// <summary>Give it back. Called from a Harmony <c>Finalizer</c>, so it runs whether the window
        /// returned or threw — a bracket that leaks is a battle whose every later roll is our seed's.</summary>
        internal static void ExitSeed(string what)
        {
            if (!_held) return;
            _held = false;
            UnityEngine.Random.state = _savedUnityState;
            if (_bracketShared != null) _bracketShared.Random = _savedShared;
            _bracketShared = null;
            _savedShared = null;
            Debug.Log("[Multiplayer][tac-native] RNG bracket EXIT " + what + " — streams restored");
        }
    }

    /// <summary>Host generation capture (see <see cref="NativeTacticalEntry"/>). Patches the BASE
    /// <c>GeoMission.PrepareTacticalGame</c>: it is not virtual and every mission subclass reaches it.</summary>
    [HarmonyPatch(typeof(GeoMission), "PrepareTacticalGame")]
    internal static class NativeEntryCapture
    {
        private static bool Prepare() => NativeTacticalEntry.Enabled;

        private static void Postfix(GeoMission __instance, PlayTacticalGameLevelResult __result)
            => NativeTacticalEntry.HostCapture(__instance, __result);
    }

    /// <summary>Bracket 1 of 2 — the DEPLOYMENT-SET pick. <c>TacMission.RunMissionActivators</c>
    /// (TacMission.cs:183) draws <c>SharedData.Random.Next() % list2.Count</c> to choose which
    /// <c>TacMissionDeploymentSetActivator</c> stays active, i.e. WHICH DEPLOY ZONES EXIST.</summary>
    [HarmonyPatch(typeof(TacMission), "RunMissionActivators")]
    internal static class DeploymentSetSeedBracket
    {
        private static bool Prepare() => NativeTacticalEntry.Enabled;

        private static void Prefix(out bool __state) => __state = NativeTacticalEntry.EnterSeed("RunMissionActivators");

        private static Exception Finalizer(bool __state, Exception __exception)
        {
            if (__state) NativeTacticalEntry.ExitSeed("RunMissionActivators");
            return __exception;
        }
    }

    /// <summary>Bracket 2 of 2 — the INITIAL DEPLOYMENT. <c>TacMission.OnLevelStart</c> (TacMission.cs:275,
    /// called from <c>TacticalLevelController.OnLevelStart</c>:642 with no yield in between) runs
    /// <c>InitDeployZones</c> plus every participant's <c>Initialize</c> + <c>DeployForTurn(0)</c>, which is
    /// every zone shuffle and every placement draw for the battle's starting board.</summary>
    [HarmonyPatch(typeof(TacMission), nameof(TacMission.OnLevelStart))]
    internal static class InitialDeploySeedBracket
    {
        private static bool Prepare() => NativeTacticalEntry.Enabled;

        private static void Prefix(out bool __state) => __state = NativeTacticalEntry.EnterSeed("TacMission.OnLevelStart");

        private static Exception Finalizer(bool __state, Exception __exception)
        {
            if (__state) NativeTacticalEntry.ExitSeed("TacMission.OnLevelStart");
            return __exception;
        }
    }
}
