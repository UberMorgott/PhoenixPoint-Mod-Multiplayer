using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Harmony.Tactical;
using Multiplayer.Network;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// STRUCTURAL-DESTRUCTION mirror (spec TS6, surface <c>tac.structdamage</c> 0x96, host→all). Closes the
    /// destructibles blind spot: the frozen client's walls / floors / props stay solid, so cover / line-of-fire /
    /// navigation diverge from the host. TS6 mirrors destruction EVENTS by re-applying the SAME native damage to the
    /// SAME destructible on the client → the native destruction cascade runs identically (cover removed, LoS opened,
    /// nav mesh updated NATIVELY) — a state copy would risk desync; the causal replay converges by construction.
    ///
    /// ONE COMBAT-DAMAGE FUNNEL. Every shot / burst / grenade / explosion / overwatch hit to a wall / floor / prop
    /// funnels through the leaf <c>DestructableDamageReceiver.ApplyDamage(DamageResult)</c> (each affected TILE of a
    /// <c>Destructable</c> wall/floor + the single receiver of a <c>Breakable</c> prop). The host postfixes that leaf
    /// (<see cref="Multiplayer.Harmony.Tactical.StructDamageCapturePatch"/>), buffering each hit; the flush heartbeat
    /// drains + broadcasts them here (<see cref="HostFlushStructDamage"/>). DISJOINT from TS3's ground-hazard voxels
    /// (fire/goo/mist ride <c>TacticalVoxel.SetVoxelType</c>, a DIFFERENT system) — 0x94 and 0x96 never touch the same leaf.
    ///
    /// DETERMINISTIC CROSS-SIDE IDENTITY (R2). A destructible is keyed by <c>DestructableBase.GuidInScene</c>
    /// (<c>SceneObjectId.GuidString</c>) — the game's OWN save/restore key (native <c>FindDestructableObject</c>
    /// resolves by exactly it via <c>SceneObjectIdsComponent.GetObjectById</c>); baked into the scene, so for a
    /// SHARED map a given wall's guid is IDENTICAL host↔client. The receiver aim-point WORLD position selects the
    /// exact TILE (fed back through the native <c>GetDamageReceiverForHit</c>, which re-derives the same tile from
    /// the same mesh/transform → no independent grid math to drift; a Breakable's single receiver is point-agnostic).
    ///
    /// CLIENT apply (<see cref="HandleStructDamage"/>): resolve the destructible by guid → its receiver-for-hit →
    /// re-apply the native damage under the remote-apply scope. Idempotent: an already-destroyed tile/prop no-ops
    /// (native health-already-zero guard / <c>Breakable._broken</c> guard). FROZEN-MIRROR SAFE: the cascade is a
    /// Map/geometry op (sim-free) for walls/floors; for an EXPLOSIVE <c>Breakable</c> the chain effect (which would
    /// double-apply actor damage the host already broadcast via 0x88, and chain-destroy props already carried by
    /// their own 0x96 hits) is NEUTERED for the replay (<see cref="ClientStructDamageInertGate"/>). All broadcasts
    /// host→ALL (3+ player safe), carry LiveSeq (last-writer-wins), per-item try/catch, degrade-to-notify (skip an
    /// unresolvable guid). Pure wire is the engine-free, unit-tested <see cref="TacticalStructDamageCodec"/>; this
    /// class is the only reflection boundary.
    /// </summary>
    public static class TacticalStructDamageSync
    {
        // Host pending buffer: captured hits since the last flush (drained on the heartbeat). Single-threaded (the
        // Unity game thread) — no locking. Struct damage is ADDITIVE, so hits are NOT coalesced (unlike TS3's
        // last-write-wins voxel type) — each is a distinct damage application replayed in capture order.
        private static readonly List<TacticalStructDamageCodec.StructHit> _pending =
            new List<TacticalStructDamageCodec.StructHit>();

        // Re-entrancy scope: true while the CLIENT is inside HandleStructDamage's native re-apply (defense-in-depth
        // marker; the primary chain-neuter is the ExplodeEffectDef null-out below).
        [ThreadStatic] private static bool _applyingStructDamage;
        public static bool IsApplyingStructDamage => _applyingStructDamage;

        /// <summary>Clear the host pending buffer (mission exit / re-deploy). Idempotent.</summary>
        public static void Reset() { _pending.Clear(); }

        // ─── HOST: capture (called from the DestructableDamageReceiver.ApplyDamage postfix) ────────────────

        /// <summary>HOST postfix hook on <c>DestructableDamageReceiver.ApplyDamage(DamageResult)</c>: record the
        /// destructible's guid + the receiver aim-point + the health damage into the pending flush buffer. Gates
        /// (mirror TS3): host + active session + not client-mirroring + deploy already captured (pre-deploy map-gen
        /// excluded) + not inside a remote apply (our own client replay must never re-capture). Skips a zero/negative
        /// hit and an unresolvable guid. Never throws (fail-quiet — a capture miss only drops a geometry mirror).</summary>
        public static void HostCaptureStructDamage(object receiver, object damageResultBoxed)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (TacticalDeploySync.IsClientMirroring) return;
            if (!TacticalDeploySync.HostHasBroadcastDeploy) return;   // exclude pre-deploy map generation
            if (TacticalActorStateSync.IsApplyingRemote) return;      // defense-in-depth (host self-apply → no re-capture)
            if (receiver == null || damageResultBoxed == null) return;
            try
            {
                if (!ReadHealthDamage(damageResultBoxed, out float hp)) return;
                if (hp <= 1E-05f) return;                             // no-op damage → nothing to mirror
                object destructable = ReadDestructable(receiver);
                if (destructable == null) return;
                string guid = ReadGuid(destructable);
                if (string.IsNullOrEmpty(guid)) return;               // dynamic/id-less destructible → degrade (skip)
                if (!ReadAimPoint(receiver, out Vector3 p)) return;
                _pending.Add(new TacticalStructDamageCodec.StructHit(guid, p.x, p.y, p.z, hp));
            }
            catch { /* fail-quiet: a missed capture only drops a geometry mirror, never desyncs */ }
        }

        /// <summary>HOST (folded into the 0x8F flush heartbeat): drain the pending hits, pack them into 0x96 messages
        /// (capped so no single message exceeds the envelope cap), and broadcast. Idle window = 0 pending = no-op.</summary>
        public static void HostFlushStructDamage(NetworkEngine engine)
        {
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            if (_pending.Count == 0) return;
            try
            {
                // Snapshot + clear first so a mid-send capture (re-entrancy) can't lose or double a hit.
                var all = new List<TacticalStructDamageCodec.StructHit>(_pending);
                _pending.Clear();

                for (int off = 0; off < all.Count; off += TacticalStructDamageCodec.MaxHitsPerMessage)
                {
                    int len = Math.Min(TacticalStructDamageCodec.MaxHitsPerMessage, all.Count - off);
                    var chunk = all.GetRange(off, len);
                    SendBatch(engine, chunk);
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostFlushStructDamage failed: " + ex); }
        }

        private static void SendBatch(NetworkEngine engine, List<TacticalStructDamageCodec.StructHit> hits)
        {
            uint seq = TacticalDeploySync.LiveSeq.Next(TacticalSurfaceIds.TacStructDamage);
            byte[] payload = TacticalStructDamageCodec.EncodeStructDamage(
                new TacticalStructDamageCodec.StructBatch(seq, hits));
            TacticalMoveSync.BroadcastToAll(engine, TacticalSurfaceIds.TacStructDamage, payload);
            Debug.Log("[Multiplayer][tac] HOST broadcast tac.structdamage seq=" + seq + " hits=" + hits.Count);
        }

        // ─── CLIENT: apply ──────────────────────────────────────────────────────────────────────────────

        /// <summary>CLIENT inbound (<c>tac.structdamage</c> 0x96): seq-guard, then re-apply each hit's native damage
        /// to the SAME destructible (resolved by guid) at the SAME receiver (resolved by aim-point) under the
        /// remote-apply scope → the client runs the REAL native destruction (correct cover / LoS / nav). An explosive
        /// Breakable's chain effect is neutered for the replay (host owns collapse/chain actor damage via 0x88 / more
        /// 0x96). Idempotent + per-hit try/catch. No-op on host.</summary>
        public static void HandleStructDamage(byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return;
            if (!TacticalStructDamageCodec.TryDecodeStructDamage(payload, out var batch))
            { Debug.LogError("[Multiplayer][tac] tac.structdamage decode failed"); return; }
            if (!TacticalDeploySync.LiveSeq.ShouldApply(TacticalSurfaceIds.TacStructDamage, batch.Seq)) return;

            try
            {
                object idsComponent = ResolveSceneObjectIds();
                if (idsComponent == null)
                {
                    Debug.LogError("[Multiplayer][tac] HandleStructDamage: no SceneObjectIdsComponent on the current level — skip");
                    return;   // do NOT mark seq — a later flush re-sends once the level is ready
                }

                bool neuterChain = ClientStructDamageInertGate.ShouldNeuterExplosionChain(TacticalDeploySync.IsClientMirroring);

                int applied = 0;
                var prev = _applyingStructDamage;
                _applyingStructDamage = true;
                using (Network.Sync.SyncApplyScope.Enter())
                using (TacticalActorStateSync.EnterApplyScope())
                {
                    foreach (var hit in batch.Hits)
                    {
                        try
                        {
                            if (hit.TargetKind != TacticalStructDamageCodec.KindDestructible) continue;   // future kind → skip
                            object destructable = ResolveDestructable(idsComponent, hit.Guid);
                            if (destructable == null) continue;   // deterministic map → normally resolves; skip-on-null
                            var point = new Vector3(hit.Px, hit.Py, hit.Pz);
                            object receiver = InvokeGetDamageReceiverForHit(destructable, point);
                            if (receiver == null) continue;
                            object dmg = BuildDamageResult(hit.HealthDamage, point);
                            if (dmg == null) continue;
                            ApplyReceiverDamage(destructable, receiver, dmg, neuterChain);
                            applied++;
                        }
                        catch (Exception ex)
                        { Debug.LogError("[Multiplayer][tac] tac.structdamage per-hit apply failed: " + ex); }
                    }
                }
                _applyingStructDamage = prev;
                TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacStructDamage, batch.Seq);
                Debug.Log("[Multiplayer][tac] CLIENT applied tac.structdamage seq=" + batch.Seq +
                          " hits=" + batch.Hits.Count + " applied=" + applied);
            }
            catch (Exception ex)
            {
                _applyingStructDamage = false;
                Debug.LogError("[Multiplayer][tac] HandleStructDamage failed: " + ex);
            }
        }

        /// <summary>Re-apply the captured health damage to the resolved receiver via the SAME native
        /// <c>ApplyDamage(DamageResult)</c>. For an EXPLOSIVE <c>Breakable</c> on a mirroring client, the
        /// <c>ExplodeEffectDef</c> is nulled ONLY across this call (restored in finally) so its native
        /// <c>Explode()</c> skips the chain effect → no double actor damage / no double chain-destroy, while the
        /// visual break + native geometry/nav update still run.</summary>
        private static void ApplyReceiverDamage(object destructable, object receiver, object dmg, bool neuterChain)
        {
            FieldInfo explodeField = null;
            object savedExplode = null;
            bool neutered = false;
            try
            {
                if (neuterChain && IsBreakable(destructable))
                {
                    explodeField = ExplodeEffectField();
                    if (explodeField != null)
                    {
                        savedExplode = explodeField.GetValue(destructable);
                        if (savedExplode != null) { explodeField.SetValue(destructable, null); neutered = true; }
                    }
                }
                InvokeReceiverApplyDamage(receiver, dmg);
            }
            finally
            {
                if (neutered && explodeField != null)
                {
                    try { explodeField.SetValue(destructable, savedExplode); } catch { /* object being destroyed anyway */ }
                }
            }
        }

        // ─── Reflection boundary ─────────────────────────────────────────────────────────────────────────

        private static Type _ddrType;            // PhoenixPoint.Tactical.Levels.Destruction.DestructableDamageReceiver
        private static Type _destructableBaseType; // PhoenixPoint.Tactical.Levels.Destruction.DestructableBase
        private static Type _breakableType;      // PhoenixPoint.Tactical.Levels.Destruction.Breakable
        private static Type _sceneIdsType;       // Base.Levels.SceneObjectIds.SceneObjectIdsComponent
        private static Type _sceneIdType;        // Base.Levels.SceneObjectIds.SceneObjectId
        private static Type _drType;             // PhoenixPoint.Tactical.Entities.DamageResult
        private static Type _castHitType;        // Base.Levels.CastHit

        private static FieldInfo _fDestructable;     // DestructableDamageReceiver._destructable
        private static PropertyInfo _pGuidInScene;   // DestructableBase.GuidInScene
        private static FieldInfo _fGuidString;       // SceneObjectId.GuidString
        private static MethodInfo _mGetAimPoint;     // DestructableDamageReceiver.GetAimPoint()
        private static FieldInfo _fHealthDamage;     // DamageResult.HealthDamage
        private static FieldInfo _fDamageOrigin;     // DamageResult.DamageOrigin
        private static FieldInfo _fImpactForce;      // DamageResult.ImpactForce
        private static FieldInfo _fImpactHit;        // DamageResult.ImpactHit
        private static FieldInfo _fCastPoint;        // CastHit.Point
        private static FieldInfo _fExplodeEffectDef; // Breakable.ExplodeEffectDef
        private static MethodInfo _mGetReceiverForHit; // DestructableBase.GetDamageReceiverForHit(Vector3,Vector3)
        private static MethodInfo _mReceiverApplyDamage; // DestructableDamageReceiver.ApplyDamage(DamageResult)
        private static MethodInfo _mGetForScene;     // SceneObjectIdsComponent.GetForScene(Scene,bool)
        private static MethodInfo _mGetObjectById;   // SceneObjectIdsComponent.GetObjectById(SceneObjectId,bool)

        private static Type DdrType()
            => _ddrType ?? (_ddrType = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.Destruction.DestructableDamageReceiver"));
        private static Type DestructableBaseType()
            => _destructableBaseType ?? (_destructableBaseType = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.Destruction.DestructableBase"));
        private static Type BreakableType()
            => _breakableType ?? (_breakableType = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.Destruction.Breakable"));
        private static Type SceneIdsType()
            => _sceneIdsType ?? (_sceneIdsType = AccessTools.TypeByName("Base.Levels.SceneObjectIds.SceneObjectIdsComponent"));
        private static Type SceneIdType()
            => _sceneIdType ?? (_sceneIdType = AccessTools.TypeByName("Base.Levels.SceneObjectIds.SceneObjectId"));
        private static Type DamageResultType()
            => _drType ?? (_drType = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.DamageResult"));
        private static Type CastHitType()
            => _castHitType ?? (_castHitType = AccessTools.TypeByName("Base.Levels.CastHit"));

        // ---- host capture reads ----

        private static bool ReadHealthDamage(object damageResultBoxed, out float hp)
        {
            hp = 0f;
            if (_fHealthDamage == null) _fHealthDamage = AccessTools.Field(DamageResultType(), "HealthDamage");
            if (_fHealthDamage == null) return false;
            object v = _fHealthDamage.GetValue(damageResultBoxed);
            if (v == null) return false;
            hp = Convert.ToSingle(v);
            return true;
        }

        private static object ReadDestructable(object receiver)
        {
            if (_fDestructable == null) _fDestructable = AccessTools.Field(DdrType(), "_destructable");
            return _fDestructable?.GetValue(receiver);
        }

        private static string ReadGuid(object destructable)
        {
            if (_pGuidInScene == null) _pGuidInScene = AccessTools.Property(DestructableBaseType(), "GuidInScene");
            object idBox = _pGuidInScene?.GetValue(destructable, null);
            if (idBox == null) return null;
            if (_fGuidString == null) _fGuidString = AccessTools.Field(SceneIdType(), "GuidString");
            return _fGuidString?.GetValue(idBox) as string;
        }

        private static bool ReadAimPoint(object receiver, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (_mGetAimPoint == null) _mGetAimPoint = AccessTools.Method(DdrType(), "GetAimPoint");
            var t = _mGetAimPoint?.Invoke(receiver, null) as Transform;
            if (t == null) return false;
            pos = t.position;
            return true;
        }

        // ---- client apply builders/invokes ----

        /// <summary>Resolve the current tactical level's <c>SceneObjectIdsComponent</c> (host↔client-shared scene
        /// object registry) — from the live level's scene (mirrors native <c>FindDestructableObject</c>, which uses
        /// the active scene). Null if the level isn't tactical / has no registry.</summary>
        private static object ResolveSceneObjectIds()
        {
            var st = SceneIdsType();
            if (st == null) return null;
            try
            {
                Scene scene;
                var level = Network.Sync.GeoRuntime.Instance.CurrentLevel();
                if (level is Component comp && comp.gameObject.scene.IsValid()) scene = comp.gameObject.scene;
                else scene = SceneManager.GetActiveScene();

                if (_mGetForScene == null)
                    _mGetForScene = AccessTools.Method(st, "GetForScene", new[] { typeof(Scene), typeof(bool) });
                return _mGetForScene?.Invoke(null, new object[] { scene, true });   // canBeMissing:true → null, no throw
            }
            catch { return null; }
        }

        /// <summary>Resolve the <c>DestructableBase</c> for a guid via the scene object registry (the game's OWN
        /// deterministic destructible key). Null if missing (degrade-to-notify).</summary>
        private static object ResolveDestructable(object idsComponent, string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            object idBox = BuildSceneObjectId(guid);
            if (idBox == null) return null;
            if (_mGetObjectById == null)
                _mGetObjectById = AccessTools.Method(SceneIdsType(), "GetObjectById", new[] { SceneIdType(), typeof(bool) });
            var go = _mGetObjectById?.Invoke(idsComponent, new object[] { idBox, true }) as GameObject;   // canBeMissing:true
            if (go == null) return null;
            var dbt = DestructableBaseType();
            return dbt == null ? null : go.GetComponent(dbt);
        }

        private static object BuildSceneObjectId(string guid)
        {
            var t = SceneIdType();
            if (t == null) return null;
            object box = Activator.CreateInstance(t);
            if (_fGuidString == null) _fGuidString = AccessTools.Field(t, "GuidString");
            _fGuidString?.SetValue(box, guid);
            return box;
        }

        private static object InvokeGetDamageReceiverForHit(object destructable, Vector3 point)
        {
            if (_mGetReceiverForHit == null)
                _mGetReceiverForHit = AccessTools.Method(DestructableBaseType(), "GetDamageReceiverForHit",
                    new[] { typeof(Vector3), typeof(Vector3) });
            // Virtual → dispatches to Destructable (per-tile) / Breakable (single receiver). Direction unused by both.
            return _mGetReceiverForHit?.Invoke(destructable, new object[] { point, Vector3.zero });
        }

        /// <summary>Build the minimal <c>DamageResult</c> the native destructible ApplyDamage reads: HealthDamage
        /// (drives the tile/prop destroy) + the impact fields the destroy cascade uses (origin/force = cosmetic
        /// debris; ImpactHit.Point = the tile selector inside <c>Destructable.DestroyHandler</c>). Source stays null
        /// → chainedExplosion=false. Returns null if the game types can't be resolved (degrade).</summary>
        private static object BuildDamageResult(float hp, Vector3 point)
        {
            var drt = DamageResultType();
            if (drt == null) return null;
            object dr = Activator.CreateInstance(drt);

            if (_fHealthDamage == null) _fHealthDamage = AccessTools.Field(drt, "HealthDamage");
            if (_fDamageOrigin == null) _fDamageOrigin = AccessTools.Field(drt, "DamageOrigin");
            if (_fImpactForce == null) _fImpactForce = AccessTools.Field(drt, "ImpactForce");
            if (_fImpactHit == null) _fImpactHit = AccessTools.Field(drt, "ImpactHit");

            _fHealthDamage?.SetValue(dr, hp);
            _fDamageOrigin?.SetValue(dr, point);
            _fImpactForce?.SetValue(dr, Vector3.zero);

            object castHit = BuildCastHit(point);
            if (castHit != null) _fImpactHit?.SetValue(dr, castHit);
            return dr;
        }

        private static object BuildCastHit(Vector3 point)
        {
            var cht = CastHitType();
            if (cht == null) return null;
            object ch = Activator.CreateInstance(cht);
            if (_fCastPoint == null) _fCastPoint = AccessTools.Field(cht, "Point");
            _fCastPoint?.SetValue(ch, point);
            return ch;
        }

        private static void InvokeReceiverApplyDamage(object receiver, object dmg)
        {
            if (_mReceiverApplyDamage == null)
                _mReceiverApplyDamage = AccessTools.Method(receiver.GetType(), "ApplyDamage", new[] { DamageResultType() });
            _mReceiverApplyDamage?.Invoke(receiver, new object[] { dmg });
        }

        private static bool IsBreakable(object destructable)
        {
            var bt = BreakableType();
            return bt != null && bt.IsInstanceOfType(destructable);
        }

        private static FieldInfo ExplodeEffectField()
        {
            if (_fExplodeEffectDef == null)
            {
                var bt = BreakableType();
                if (bt != null) _fExplodeEffectDef = AccessTools.Field(bt, "ExplodeEffectDef");
            }
            return _fExplodeEffectDef;
        }
    }
}
