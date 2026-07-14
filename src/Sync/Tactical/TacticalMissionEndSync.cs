using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using UnityEngine;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// MISSION-CONCLUSION mirror (spec TS4, surface <c>tac.missionend</c> 0x95, host→all, RELIABLE). Closes the
    /// audit gap "the battle can't END in sync": the client leaves the battle when the host does, with the same
    /// result/loot, and the client's in-battle objective HUD mirrors the host state.
    ///
    /// HOST authority (<see cref="HostOnGameOver"/>, postfix on <c>TacticalLevelController.GameOver()</c> — the one
    /// method that fires GameWrappingUpEvent then GameOverEvent). Emits, in order:
    ///   • phase=wrappingup (0): a lightweight pre-close notify (no heavy blob) so the client can order its close.
    ///   • phase=gameover (1): the full conclusion — the native <c>GetMissionResult()</c> graph blob (via the game
    ///     Serializer, never a hand-mapped DTO), the player-faction outcome, evac-zone state, and per-objective state.
    ///
    /// CLIENT apply (<see cref="HandleMissionEnd"/>, display-only under <see cref="SyncApplyScope"/> + the tactical
    /// remote-apply guard): repaint objective state value-only, then on the terminal gameover phase RIDE THE NATIVE
    /// end-of-mission flow — flip the native <c>IsGameOver</c> flag that the tactical View state machine
    /// (UIStateWaiting / UIStateInitial → GetLevelFinishedViewState) AND the mirror turn-loop already watch, so the
    /// client transitions to the native level-finished state and returns to geoscape. NO custom teardown.
    ///
    /// NO DOUBLE-OUTCOME: the post-mission GEOSCAPE result modal is owned by the geoscape popup-mirror rail
    /// (MissionOutcome 0x69, deferred + non-occupying in the display sequencer). TS4 shows NO modal of its own
    /// (<see cref="TacticalMissionEndGate.ShouldDisplayOutcome"/> is constant-false) — it only closes the tactical
    /// scene, so the client sees the outcome exactly once via 0x69 once it is back in geoscape.
    ///
    /// EVAC-ZONE STATE: the host emits an EMPTY evac list BY DESIGN (gap-evac closure) — the client evac-zone
    /// LOCK state is owned live by the 0x99 ZONE_UNLOCK records (<see cref="TacticalObjectiveSync.HostOnZonesUnlocked"/>
    /// mirrors both native unlock chokepoints in-battle; audit D20), so filling this list too would add a second
    /// writer for the same field. The wire + codec keep supporting evac records (unit-tested, no wire break) purely
    /// for compatibility. The evac OUTCOME that matters for parity — which soldiers got out / survived — rides the
    /// <c>TacMissionResult</c> blob + the 0x8F <c>EvacuatedStatus</c> mirror, not this list.
    ///
    /// All broadcasts host→ALL (3+ player safe), carry LiveSeq (last-writer-wins), per-item try/catch,
    /// degrade-to-notify, TFTV-tolerant (resolve-by-member, skip-on-null). Pure wire + decisions live in the
    /// engine-free, unit-tested <see cref="TacticalMissionEndCodec"/> / <see cref="TacticalMissionEndGate"/>; this
    /// class is the only reflection boundary.
    /// </summary>
    public static class TacticalMissionEndSync
    {
        private static readonly List<TacticalMissionEndCodec.EvacRec> EmptyEvac = new List<TacticalMissionEndCodec.EvacRec>();
        private static readonly List<TacticalMissionEndCodec.ObjectiveRec> EmptyObj = new List<TacticalMissionEndCodec.ObjectiveRec>();

        // ─── HOST: broadcast at the GameOver() chokepoint ────────────────────────────────────────────────

        /// <summary>HOST postfix on <c>TacticalLevelController.GameOver()</c>: broadcast the conclusion. Gates: host +
        /// active session + not client-mirroring + deploy already captured (a non-co-op / pre-deploy game-over is a
        /// clean no-op). Emits wrappingup then gameover so the client applies them in order. No-op off-host /
        /// single-player.</summary>
        public static void HostOnGameOver(object tlc)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (TacticalDeploySync.IsClientMirroring) return;
            if (!TacticalDeploySync.HostHasBroadcastDeploy) return;   // exclude non-deployed / pre-deploy game-over
            if (tlc == null) return;
            try
            {
                int outcome = ReadPlayerOutcome(tlc);

                // wrappingup: lightweight pre-close notify (no heavy blob) — lets the client order its close.
                BroadcastPhase(engine, TacticalMissionEndCodec.PhaseWrappingUp, outcome, new byte[0], EmptyEvac, EmptyObj);

                // gameover: conclusion — objective state only; the client closes the scene on this phase.
                // Result blob OMITTED: the un-chunked GetMissionResult() graph (~93 KB) overflows the u16 envelope
                // length field (SyncProtocol.EncodeEnvelope, >65535 → ArgumentOutOfRangeException), so the phase=1
                // send threw and the client NEVER flipped IsGameOver (stuck in tactical). The client never reads
                // ResultBlob (HandleMissionEnd) — the outcome DISPLAY is owned by the geoscape popup-mirror 0x69.
                // If the blob is ever needed, chunk it via TacticalDeployChunkCodec (as the deploy snapshot does).
                byte[] resultBlob = new byte[0];
                var evac = ReadEvacZones(tlc);          // empty this cut (documented deviation)
                var obj = ReadObjectives(tlc);
                BroadcastPhase(engine, TacticalMissionEndCodec.PhaseGameOver, outcome, resultBlob, evac, obj);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostOnGameOver failed: " + ex); }
        }

        private static void BroadcastPhase(NetworkEngine engine, byte phase, int outcome, byte[] resultBlob,
            List<TacticalMissionEndCodec.EvacRec> evac, List<TacticalMissionEndCodec.ObjectiveRec> obj)
        {
            uint seq = TacticalDeploySync.LiveSeq.Next(TacticalSurfaceIds.TacMissionEnd);
            byte[] payload = TacticalMissionEndCodec.Encode(new TacticalMissionEndCodec.MissionEndPayload(
                seq, phase, outcome, resultBlob, evac, obj));
            TacticalMoveSync.BroadcastToAll(engine, TacticalSurfaceIds.TacMissionEnd, payload);
            Debug.Log("[Multiplayer][tac] HOST broadcast tac.missionend seq=" + seq + " phase=" + phase +
                      " outcome=" + outcome + " resultLen=" + (resultBlob != null ? resultBlob.Length : 0) +
                      " evac=" + (evac != null ? evac.Count : 0) + " obj=" + (obj != null ? obj.Count : 0));
        }

        // ─── CLIENT: apply ────────────────────────────────────────────────────────────────────────────

        /// <summary>CLIENT inbound (<c>tac.missionend</c> 0x95): seq-guard, then under the remote-apply scope repaint
        /// objective state value-only and, on the terminal gameover phase, ride the native end-of-mission flow (flip
        /// <c>IsGameOver</c> → native View machine returns to geoscape). Idempotent (the seq guard + the not-already-
        /// over check make a re-send a no-op). NO outcome modal (0x69 owns it). No-op on host.</summary>
        public static void HandleMissionEnd(byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return;
            if (!TacticalMissionEndCodec.TryDecode(payload, out var p))
            { Debug.LogError("[Multiplayer][tac] tac.missionend decode failed"); return; }
            if (!TacticalDeploySync.LiveSeq.ShouldApply(TacticalSurfaceIds.TacMissionEnd, p.Seq)) return;

            try
            {
                object tlc = ResolveClientTlc();
                if (tlc == null)
                {
                    // Not in a tactical level yet — do NOT mark the seq; a reliable re-send applies once the level is live.
                    Debug.LogError("[Multiplayer][tac] HandleMissionEnd: no client TacticalLevelController — skip (unmarked)");
                    return;
                }

                using (SyncApplyScope.Enter())
                using (TacticalActorStateSync.EnterApplyScope())
                {
                    ApplyObjectives(tlc, p.Objectives);
                    // evac apply: no-op this cut (host emits empty; see class summary).

                    bool alreadyOver = ReadIsGameOver(tlc);
                    if (TacticalMissionEndGate.ShouldEndClientMission(p.Phase, alreadyOver))
                    {
                        EndClientMission(tlc);
                        Debug.Log("[Multiplayer][tac] CLIENT ended tactical mission — rode native game-over → geoscape " +
                                  "(outcome modal owned by geoscape popup-mirror 0x69)");
                    }
                }

                TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacMissionEnd, p.Seq);
                Debug.Log("[Multiplayer][tac] CLIENT applied tac.missionend seq=" + p.Seq + " phase=" + p.Phase +
                          " objApplied=" + (p.Objectives != null ? p.Objectives.Count : 0));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HandleMissionEnd failed: " + ex); }
        }

        // ─── HOST reads ──────────────────────────────────────────────────────────────────────────────

        /// <summary>Serialize the native <c>GetMissionResult()</c> graph via the ONE game Serializer (spec R2 — never
        /// a hand-mapped DTO). Degrade-to-notify: a null result / failed serialize → empty blob (the client still
        /// closes; the outcome DISPLAY rides 0x69 regardless).</summary>
        private static byte[] SerializeMissionResult(object tlc)
        {
            try
            {
                object result = InvokeGetMissionResult(tlc);
                if (result == null) { Debug.LogError("[Multiplayer][tac] SerializeMissionResult: null GetMissionResult() — empty blob"); return new byte[0]; }
                byte[] blob = TacticalDeploySync.SerializeGraph(new[] { result }, quiet: true);
                return blob ?? new byte[0];
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] SerializeMissionResult failed: " + ex); return new byte[0]; }
        }

        /// <summary>Player faction's terminal <c>TacFactionState</c> as an int (diagnostic / HUD hint). Best-effort;
        /// <see cref="TacticalMissionEndCodec.OutcomeUnknown"/> on any failure.</summary>
        private static int ReadPlayerOutcome(object tlc)
        {
            try
            {
                object pf = ResolvePlayerFaction(tlc);
                if (pf != null)
                {
                    object st = GetProp(pf, "State");
                    if (st != null) return Convert.ToInt32(st);
                }
            }
            catch { }
            return TacticalMissionEndCodec.OutcomeUnknown;
        }

        /// <summary>Per-objective state of the player faction, keyed by ORDINAL index (stable host↔client via the
        /// shared mission def). Best-effort; a read miss drops just that objective.</summary>
        private static List<TacticalMissionEndCodec.ObjectiveRec> ReadObjectives(object tlc)
        {
            var list = new List<TacticalMissionEndCodec.ObjectiveRec>();
            try
            {
                object pf = ResolvePlayerFaction(tlc);
                if (pf == null) return list;
                if (GetProp(pf, "Objectives") is IEnumerable en)
                {
                    int i = 0;
                    foreach (var o in en)
                    {
                        try
                        {
                            object st = o != null ? GetProp(o, "State") : null;
                            byte state = st != null ? (byte)Convert.ToInt32(st) : (byte)0;
                            list.Add(new TacticalMissionEndCodec.ObjectiveRec(i.ToString(), state));
                        }
                        catch { }
                        i++;
                    }
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ReadObjectives failed: " + ex); }
            return list;
        }

        /// <summary>Evac-zone unlock state: EMPTY BY DESIGN (gap-evac closure). The client zone-lock state is owned
        /// live by the 0x99 ZONE_UNLOCK records (<see cref="TacticalObjectiveSync.HostOnZonesUnlocked"/>, audit D20) —
        /// one writer per field, so this list must stay empty (codec/wire keep supporting evac records, no wire
        /// break). The evac OUTCOME rides the result blob + the 0x8F EvacuatedStatus mirror.</summary>
        private static List<TacticalMissionEndCodec.EvacRec> ReadEvacZones(object tlc) => EmptyEvac;

        // ─── CLIENT applies ──────────────────────────────────────────────────────────────────────────

        /// <summary>Repaint the client player-faction objectives to the host states (value-only, private setter).
        /// Keyed by ORDINAL index via the pure <see cref="TacticalMissionEndGate.ResolveObjectiveApplies"/>;
        /// per-item try/catch (degrade-to-notify).</summary>
        private static void ApplyObjectives(object tlc, List<TacticalMissionEndCodec.ObjectiveRec> objectives)
        {
            if (objectives == null || objectives.Count == 0) return;
            try
            {
                object pf = ResolvePlayerFaction(tlc);
                if (pf == null) return;
                if (!(GetProp(pf, "Objectives") is IEnumerable en)) return;
                var objList = new List<object>();
                foreach (var o in en) objList.Add(o);

                var applies = TacticalMissionEndGate.ResolveObjectiveApplies(objectives, objList.Count);
                foreach (var kv in applies)
                {
                    try { SetObjectiveState(objList[kv.Key], kv.Value); }
                    catch (Exception ex) { Debug.LogError("[Multiplayer][tac] objective apply failed idx=" + kv.Key + ": " + ex); }
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] ApplyObjectives failed: " + ex); }
        }

        /// <summary>End the client mission by riding the NATIVE end-of-mission flow: flip the native
        /// <c>IsGameOver</c> flag that the tactical View state machine (UIStateWaiting / UIStateInitial →
        /// GetLevelFinishedViewState) and the mirror turn-loop already watch → the client transitions to the native
        /// level-finished state and returns to geoscape. NO custom teardown. Idempotent (caller guards on
        /// not-already-over).</summary>
        private static void EndClientMission(object tlc)
        {
            if (_isGameOverSetter == null) _isGameOverSetter = AccessTools.PropertySetter(TlcType(), "IsGameOver");
            if (_isGameOverSetter == null)
            {
                Debug.LogError("[Multiplayer][tac] IsGameOver setter not found — cannot close client mission");
                return;
            }
            _isGameOverSetter.Invoke(tlc, new object[] { true });
        }

        // ─── Reflection boundary ─────────────────────────────────────────────────────────────────────

        private static Type _tlcType;                 // PhoenixPoint.Tactical.Levels.TacticalLevelController
        private static MethodInfo _getMissionResult;   // TacticalLevelController.GetMissionResult()
        private static MethodInfo _isGameOverSetter;    // TacticalLevelController.IsGameOver (private set)
        // FactionObjective.State (private set) + FactionObjectiveState live in the SHARED
        // FactionObjectiveReflect boundary (also used by the live 0x99 objective mirror).

        private static Type TlcType()
            => _tlcType ?? (_tlcType = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.TacticalLevelController"));

        private static object InvokeGetMissionResult(object tlc)
        {
            if (_getMissionResult == null) _getMissionResult = AccessTools.Method(TlcType(), "GetMissionResult");
            return _getMissionResult?.Invoke(tlc, null);
        }

        private static bool ReadIsGameOver(object tlc)
        {
            try { return GetProp(tlc, "IsGameOver") is bool b && b; }
            catch { return false; }
        }

        /// <summary>First player-controlled faction in the level's Factions list (the shared Phoenix faction).</summary>
        private static object ResolvePlayerFaction(object tlc)
        {
            try
            {
                if (GetProp(tlc, "Factions") is IEnumerable en)
                    foreach (var f in en)
                        if (f != null && GetProp(f, "IsControlledByPlayer") is bool b && b) return f;
            }
            catch { }
            return null;
        }

        private static void SetObjectiveState(object objective, byte state)
            => FactionObjectiveReflect.SetState(objective, state);   // shared boundary (also drives the 0x99 mirror)

        /// <summary>Resolve the live client <c>TacticalLevelController</c> — the same
        /// <c>GeoRuntime.CurrentLevel() → GetComponent</c> path the TS3 surface mirror uses. Null if the current
        /// level is not tactical.</summary>
        private static object ResolveClientTlc()
        {
            var t = TlcType();
            if (t == null) return null;
            try
            {
                var level = GeoRuntime.Instance.CurrentLevel();
                if (level is Component comp) return comp.GetComponent(t);
                return null;
            }
            catch { return null; }
        }

        private static readonly Dictionary<string, PropertyInfo> _propCache = new Dictionary<string, PropertyInfo>();
        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            string key = obj.GetType().FullName + "::" + name;
            if (!_propCache.TryGetValue(key, out var pi))
            {
                pi = AccessTools.Property(obj.GetType(), name);
                _propCache[key] = pi;
            }
            return pi?.GetValue(obj, null);
        }
    }
}
