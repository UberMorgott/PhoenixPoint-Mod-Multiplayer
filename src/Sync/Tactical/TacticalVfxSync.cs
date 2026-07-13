using System;
using HarmonyLib;
using Multiplayer.Network;
using UnityEngine;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// TS7 — AoE / explosion presentation VFX replay (<c>tac.vfx</c> 0x98). Damage from a grenade / rocket /
    /// explosive death mirrors via tac.damage (0x88), but the BLAST VISUAL is drawn by the native
    /// <c>ExplosionEffect</c>/<c>VolumeEffect.SpawnObject</c> — which instantiates <c>VolumeEffectDef.ObjectToSpawn</c>
    /// (a particle/FX prefab) — and that runs HOST-ONLY (the frozen client applies the flattened damage, it never
    /// runs the effect). So the client sees numbers but no blast. This module closes that cosmetic gap.
    ///
    /// HOST broadcast (<see cref="HostBroadcastVfx"/>): from the postfix on <c>ExplosionEffect.SpawnObject</c> /
    /// <c>VolumeEffect.SpawnObject</c> (see <c>VfxBroadcastPatch</c>). Reads {effect def guid, world pos} and
    /// broadcasts <c>tac.vfx</c> — but ONLY for a REAL application, never a damage-PREDICTION simulation pass
    /// (<see cref="TacticalVfxGate.ShouldBroadcastVfx"/>; SpawnObject is entered during simulation too but
    /// early-returns without drawing). Fail-open (logs + swallows), so the native host effect always proceeds.
    ///
    /// CLIENT apply (<see cref="HandleVfx"/>): resolve the <c>VolumeEffectDef</c> by guid, read its
    /// <c>ObjectToSpawn</c> prefab, and <c>Instantiate</c> it at the mirrored position — EXACTLY the native
    /// SpawnObject visual, and PRESENTATION ONLY: a VFX prefab applies no damage (the effect's damage logic is
    /// never run on the client). Degrade silently: unresolvable def / null prefab → skip (no notify spam). The
    /// self-destructing particle prefab manages its own lifetime, mirroring native (no further tracking).
    /// </summary>
    public static class TacticalVfxSync
    {
        // ─── HOST: a blast VFX is spawning → broadcast tac.vfx so clients replay the same prefab ─────
        /// <summary>HOST: at the native <c>SpawnObject</c> chokepoint, read {defGuid, pos} off the effect +
        /// EffectTarget and broadcast <c>tac.vfx</c>. Skipped for a simulation/prediction pass (no draw), off-host,
        /// or when the def guid / position is unreadable. Fail-open.</summary>
        public static void HostBroadcastVfx(object effect, object effectTarget)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActive || !engine.IsHost) return;
                if (TacticalDeploySync.IsClientMirroring) return;
                if (effect == null || effectTarget == null) return;

                // Real application only — SpawnObject early-returns on a simulation pass, so a phantom broadcast for
                // every damage prediction must be suppressed.
                bool isSimulation = InvokeBool(effect, "IsSimulation", effectTarget);
                if (!TacticalVfxGate.ShouldBroadcastVfx(isSimulation)) return;

                string defGuid = Network.Sync.DefReflection.GetGuid(GetProp(effect, "BaseDef"));
                if (string.IsNullOrEmpty(defGuid)) return;

                if (!(GetField(effectTarget, "Position") is Vector3 pos)) return;

                // Source actor is a best-effort forward-compat tag; the client anchors on pos, so -1 is fine.
                int sourceNetId = ResolveSourceNetId(effect);

                // rca-grenade-vfx: the blast prefab a grenade/rocket actually draws is the WEAPON payload's
                // ObjectToSpawnOnExplosion (native precedence, ExplosionEffect.cs:54 — the shared
                // ExplosionEffectDef's own ObjectToSpawn is null), so carry the weapon def guid too.
                string srcDefGuid = ResolveSourceWeaponDefGuid(effect);

                uint seq = TacticalDeploySync.LiveSeq.Next(TacticalSurfaceIds.TacVfx);
                byte[] payload = TacticalLiveCodec.EncodeVfx(seq, defGuid, pos.x, pos.y, pos.z, sourceNetId, srcDefGuid);
                TacticalMoveSync.BroadcastToAll(engine, TacticalSurfaceIds.TacVfx, payload);
                Debug.Log("[Multiplayer][tac] HOST broadcast tac.vfx seq=" + seq + " def=" + defGuid +
                          " srcDef=" + (string.IsNullOrEmpty(srcDefGuid) ? "<none>" : srcDefGuid) +
                          " pos=" + pos + " src=" + sourceNetId + " effect=" + effect.GetType().Name);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HostBroadcastVfx failed: " + ex); }
        }

        // ─── CLIENT: replay the blast prefab at the mirrored position (presentation only) ────────────
        /// <summary>CLIENT inbound (<c>tac.vfx</c>): resolve the VolumeEffectDef by guid, instantiate its
        /// <c>ObjectToSpawn</c> prefab at the mirrored position. No damage (a VFX prefab is inert). No-op off-client
        /// / off-session / stale seq. Degrade silently on a missing def / prefab.</summary>
        public static void HandleVfx(byte[] payload)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return;
            if (!TacticalLiveCodec.TryDecodeVfx(payload, out var evt)) { Debug.LogError("[Multiplayer][tac] tac.vfx decode failed"); return; }
            if (!TacticalDeploySync.LiveSeq.ShouldApply(TacticalSurfaceIds.TacVfx, evt.Seq)) return;

            try
            {
                object def = Network.Sync.DefReflection.GetDefByGuid(evt.VfxDefGuid);
                if (def == null) { Debug.Log("[Multiplayer][tac] tac.vfx: def " + evt.VfxDefGuid + " unresolved — skip"); TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacVfx, evt.Seq); return; }

                // rca-grenade-vfx: prefer the weapon payload's blast prefab — the SAME precedence native
                // ExplosionEffect.SpawnObject applies (param.ObjectToSpawnOnExplosion first, def.ObjectToSpawn
                // fallback). Old-peer payload / unresolved weapon → SrcDefGuid "" → fallback only (old behavior).
                var prefab = ResolvePayloadBlastPrefab(evt.SrcDefGuid) ?? GetProp(def, "ObjectToSpawn") as GameObject;
                if (prefab == null) { Debug.Log("[Multiplayer][tac] tac.vfx: def " + evt.VfxDefGuid + " srcDef=" + (string.IsNullOrEmpty(evt.SrcDefGuid) ? "<none>" : evt.SrcDefGuid) + " has no blast prefab — skip"); TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacVfx, evt.Seq); return; }

                var pos = new Vector3(evt.X, evt.Y, evt.Z);
                UnityEngine.Object.Instantiate(prefab, pos, Quaternion.identity);   // self-destructing FX prefab, like native SpawnObject
                TacticalDeploySync.LiveSeq.Mark(TacticalSurfaceIds.TacVfx, evt.Seq);
                Debug.Log("[Multiplayer][tac] CLIENT played tac.vfx seq=" + evt.Seq + " def=" + evt.VfxDefGuid + " pos=" + pos);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] HandleVfx failed: " + ex); }
        }

        /// <summary>HOST: walk the effect's native Source chain to the WEAPON behind the blast and return its
        /// def guid ("" when unresolved — the client then falls back to the effect def's ObjectToSpawn). Hop
        /// rule mirrors the native <c>TacUtil.GetSourceOfType</c> chain (TacUtil.cs:31-42): an Effect/Status/
        /// TacticalItem/TacticalAbility hops via <c>Source</c>, a Projectile/ProjectileLogic via <c>Weapon</c>;
        /// the first node exposing a readable <c>WeaponDef</c> IS the weapon. Bounded + NRE-guarded.</summary>
        private static string ResolveSourceWeaponDefGuid(object effect)
        {
            try
            {
                object node = GetProp(effect, "Source");
                for (int hop = 0; node != null && hop < 8; hop++)
                {
                    object weaponDef = GetProp(node, "WeaponDef");
                    if (weaponDef != null)
                    {
                        string guid = Network.Sync.DefReflection.GetGuid(weaponDef);
                        return string.IsNullOrEmpty(guid) ? "" : guid;
                    }
                    node = GetProp(node, "Weapon") ?? GetProp(node, "Source");
                }
            }
            catch { /* best-effort — "" degrades to the def.ObjectToSpawn fallback */ }
            return "";
        }

        /// <summary>CLIENT: resolve the weapon def by guid and read its <c>DamagePayload.ObjectToSpawnOnExplosion</c>
        /// blast prefab (WeaponDef.cs:20 / DamagePayload.cs:91) — the prefab the native host explosion actually
        /// spawned. Null on "" / unresolvable guid / payload without a blast prefab (caller falls back).</summary>
        private static GameObject ResolvePayloadBlastPrefab(string srcDefGuid)
        {
            try
            {
                if (string.IsNullOrEmpty(srcDefGuid)) return null;
                object weaponDef = Network.Sync.DefReflection.GetDefByGuid(srcDefGuid);
                object damagePayload = weaponDef != null ? GetField(weaponDef, "DamagePayload") : null;
                return damagePayload != null ? GetField(damagePayload, "ObjectToSpawnOnExplosion") as GameObject : null;
            }
            catch { return null; }
        }

        /// <summary>Best-effort source-actor netId (the thrower/shooter behind the blast). The effect's
        /// <c>Source</c> is often an <c>IDamageDealer</c> ability whose <c>TacticalActorBase</c> is the caster;
        /// returns -1 when it can't be resolved (the client anchors on pos, so this is a forward-compat tag).</summary>
        private static int ResolveSourceNetId(object effect)
        {
            try
            {
                object source = GetProp(effect, "Source");
                object actor = source != null ? GetProp(source, "TacticalActorBase") : null;
                if (actor == null) return TacticalLiveCodec.TargetNetIdNone;
                int netId = TacticalDeploySync.NetIdForLiveActor(actor);
                return netId >= 0 ? netId : TacticalLiveCodec.TargetNetIdNone;
            }
            catch { return TacticalLiveCodec.TargetNetIdNone; }
        }

        private static bool InvokeBool(object obj, string method, object arg)
        {
            if (obj == null) return false;
            var m = AccessTools.Method(obj.GetType(), method);
            if (m == null) return false;
            object r = m.Invoke(obj, new[] { arg });
            return r is bool b && b;
        }

        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            var p = AccessTools.Property(obj.GetType(), name);
            if (p != null) return p.GetValue(obj, null);
            var f = AccessTools.Field(obj.GetType(), name);
            return f?.GetValue(obj);
        }

        private static object GetField(object obj, string name)
        {
            if (obj == null) return null;
            var f = AccessTools.Field(obj.GetType(), name);
            if (f != null) return f.GetValue(obj);
            var p = AccessTools.Property(obj.GetType(), name);
            return p != null ? p.GetValue(obj, null) : null;
        }
    }
}
