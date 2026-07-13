using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Sync.Tactical
{
    public static partial class TacticalLiveCodec
    {
        // ─── tac.camerahint (host→all, TS7 — enemy-turn camera follow) ───────────────────────────────
        // The host broadcasts this when an ENEMY actor VISIBLE to the player faction begins a camera-tracked
        // ability during its turn (native TacticalAbility.Activate → CameraDirectorHint.AbilityActivated,
        // gated TrackWithCamera). On the frozen mirror the enemy replay coroutines bypass Activate, so the
        // native follow never fires — this hint tells the client which enemy to chase (follow=true). PRESENTATION
        // ONLY: no state. Visible-only ("no fog reveals") is enforced HOST-side; the client just follows, gated to
        // its enemy turn (ClientEnemyTurnCameraGate). Self-contained tactical seq (last-writer-wins).
        //   [seq:u32][actorNetId:i32]
        public struct CameraHint
        {
            public uint Seq;
            public int ActorNetId;
            public CameraHint(uint seq, int actorNetId) { Seq = seq; ActorNetId = actorNetId; }
        }

        public static byte[] EncodeCameraHint(uint seq, int actorNetId)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq); w.Write(actorNetId);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeCameraHint(byte[] data, out CameraHint hint)
        {
            hint = default(CameraHint);
            if (data == null || data.Length < 4 + 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    int netId = r.ReadInt32();
                    hint = new CameraHint(seq, netId);
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── tac.vfx (host→all, TS7 — AoE / explosion presentation VFX) ───────────────────────────────
        // The host broadcasts this at the native prefab-VFX spawn chokepoint (ExplosionEffect / VolumeEffect
        // SpawnObject — grenades, rockets, explosive deaths), so the client replays the SAME blast visual it
        // otherwise never sees (damage rides tac.damage 0x88, but the ExplosionEffect that draws the blast runs
        // host-only). PRESENTATION ONLY: the client resolves the VolumeEffectDef by guid and instantiates its
        // ObjectToSpawn prefab at the mirrored position — a particle/FX object that applies NO damage. actorNetId
        // is a best-effort source-actor tag (-1 when unresolved); pos is the anchor. Fire/goo/acid voxel volumes
        // ride TS3 (0x94), not this surface. Self-contained tactical seq (last-writer-wins).
        //
        // srcDefGuid (rca-grenade-vfx, OPTIONAL TAIL): the def guid of the DAMAGE-PAYLOAD SOURCE (the weapon)
        // behind the blast. Grenades/rockets draw their blast prefab from DamageEffect.Params
        // .ObjectToSpawnOnExplosion, fed from the WEAPON's DamagePayload (decompile DamageTypeBaseEffect.cs:87);
        // the effect def's ObjectToSpawn is only a FALLBACK (ExplosionEffect.cs:54) and is NULL on the shared
        // ExplosionEffectDef, so the effect-def guid alone can never replay a grenade blast. "" when unresolved.
        // Appended as a TAIL field: an OLD decoder reads the fixed prefix and ignores trailing bytes; a NEW
        // decoder treats a missing tail as "" (fallback path) — both directions degrade to pre-fix behavior.
        //   [seq:u32][vfxDefGuid:string][x:f32][y:f32][z:f32][actorNetId:i32]([srcDefGuid:string])
        public struct VfxEvent
        {
            public uint Seq;
            public string VfxDefGuid;
            public float X, Y, Z;
            public int ActorNetId;   // -1 sentinel when the source actor is unresolved
            public string SrcDefGuid; // "" when the damage-payload source (weapon) def is unresolved / old peer
            public VfxEvent(uint seq, string vfxDefGuid, float x, float y, float z, int actorNetId,
                string srcDefGuid = "")
            {
                Seq = seq; VfxDefGuid = vfxDefGuid ?? ""; X = x; Y = y; Z = z; ActorNetId = actorNetId;
                SrcDefGuid = srcDefGuid ?? "";
            }
        }

        public static byte[] EncodeVfx(uint seq, string vfxDefGuid, float x, float y, float z, int actorNetId,
            string srcDefGuid = "")
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq);
                w.Write(vfxDefGuid ?? "");
                w.Write(x); w.Write(y); w.Write(z);
                w.Write(actorNetId);
                w.Write(srcDefGuid ?? "");   // optional tail — old decoders never read past actorNetId
                return ms.ToArray();
            }
        }

        public static bool TryDecodeVfx(byte[] data, out VfxEvent evt)
        {
            evt = default(VfxEvent);
            // Minimum: u32 seq + at least a 1-byte length-prefixed string + 3*f32 + i32.
            if (data == null || data.Length < 4 + 1 + 12 + 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    string guid = r.ReadString();
                    float x = r.ReadSingle(); float y = r.ReadSingle(); float z = r.ReadSingle();
                    int netId = r.ReadInt32();
                    string srcGuid = "";
                    if (ms.Position < ms.Length)          // optional tail — absent on an old-peer payload
                    {
                        try { srcGuid = r.ReadString(); } catch { srcGuid = ""; }
                    }
                    evt = new VfxEvent(seq, guid, x, y, z, netId, srcGuid);
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
