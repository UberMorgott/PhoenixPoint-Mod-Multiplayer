using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Sync.Tactical
{
    public static partial class TacticalLiveCodec
    {
        // ─── tac.intent.ability (client→host, Inc 3a) ─────────────────────
        // A client SHOOT intent: which actor shoots which ability (by def guid) at which target. The target
        // is carried as BOTH a netId (actor target; -1 sentinel when the shot is at a bare position) AND a
        // position (always present — the host rebuilds a TacticalAbilityTarget from whichever is valid). FIX A:
        // it ALSO carries BodyPartId — the index of the player's SELECTED body part in the target actor's snapper
        // enumeration (GetHealthSlots().GetAimPointItem() then visible Equipments.Items, the SAME order native
        // ShootAbility.GetShootTarget walks) — so the host reproduces the EXACT limb + cover-aware ShootFromPos
        // via native generation instead of snapping to center-of-mass. -1 = no selected body part (free shot).
        public struct IntentAbility
        {
            public int ShooterNetId;
            public string AbilityDefGuid;
            public int TargetNetId;          // -1 sentinel when the target is a position, not an actor
            public float TX, TY, TZ;
            public int BodyPartId;           // -1 sentinel = no player-selected body part (bare-ground / free shot)
            public uint Nonce;
            public IntentAbility(int shooterNetId, string abilityDefGuid, int targetNetId,
                float tx, float ty, float tz, int bodyPartId, uint nonce)
            {
                ShooterNetId = shooterNetId; AbilityDefGuid = abilityDefGuid ?? ""; TargetNetId = targetNetId;
                TX = tx; TY = ty; TZ = tz; BodyPartId = bodyPartId; Nonce = nonce;
            }
        }

        public const int TargetNetIdNone = -1;   // sentinel: the shot target is a position, not an actor
        public const int BodyPartIdNone = -1;    // sentinel: no player-selected body part (bare-ground / free shot)

        public static byte[] EncodeIntentAbility(int shooterNetId, string abilityDefGuid, int targetNetId,
            float tx, float ty, float tz, int bodyPartId, uint nonce)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(shooterNetId);
                w.Write(abilityDefGuid ?? "");
                w.Write(targetNetId);
                w.Write(tx); w.Write(ty); w.Write(tz);
                w.Write(bodyPartId);
                w.Write(nonce);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeIntentAbility(byte[] data, out IntentAbility intent)
        {
            intent = default(IntentAbility);
            // Minimum: i32 shooter + at least a 1-byte length-prefixed string + i32 target + 3*f32 + i32 bodyPart + u32.
            if (data == null || data.Length < 4 + 1 + 4 + 12 + 4 + 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    int shooter = r.ReadInt32();
                    string guid = r.ReadString();
                    int targetNetId = r.ReadInt32();
                    float tx = r.ReadSingle(); float ty = r.ReadSingle(); float tz = r.ReadSingle();
                    int bodyPartId = r.ReadInt32();
                    uint nonce = r.ReadUInt32();
                    intent = new IntentAbility(shooter, guid, targetNetId, tx, ty, tz, bodyPartId, nonce);
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── tac.damage (host→all, Inc 3a) ────────────────────────────────
        // A flattened DamageResult (DamageResult.cs) — the FINAL applied result the host captured in its
        // ApplyDamage postfix. The client rebuilds a DamageResult from this and applies it to the target
        // mirror, so HP/armor/stun/status/effects/stat-mods all converge. ImpactHit is intentionally omitted
        // (left default on rebuild — it only drives local hit FX, not authoritative state). The shooter's
        // post-shot AP/WP ride along so the client mirror can set the shooter's spent AP/WP exactly.
        public struct DamageStatus { public string DefGuid; public float Value; public int SourceNetId; }
        public struct DamageStatMod { public string StatName; public int ModKind; public float Value; }

        public sealed class DamagePayload
        {
            public uint Seq;
            public int TargetNetId;
            public int SourceNetId;            // -1 sentinel when the damage source is unresolved/null
            public float HealthDamage, ArmorDamage, ArmorMitigatedDamage, StunValue, HealValue;
            public float IfX, IfY, IfZ;        // ImpactForce
            public float DoX, DoY, DoZ;        // DamageOrigin
            public bool ForceHurt;
            public string DamageTypeDefGuid;
            public List<DamageStatus> Statuses = new List<DamageStatus>();
            public List<string> EffectGuids = new List<string>();
            public List<DamageStatMod> StatMods = new List<DamageStatMod>();
            public int ShooterNetId;           // -1 when the source is not the shooter / not carried
            public float ShooterApAfter, ShooterWpAfter;
        }

        public static byte[] EncodeDamage(DamagePayload p)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(p.Seq);
                w.Write(p.TargetNetId);
                w.Write(p.SourceNetId);
                w.Write(p.HealthDamage); w.Write(p.ArmorDamage); w.Write(p.ArmorMitigatedDamage);
                w.Write(p.StunValue); w.Write(p.HealValue);
                w.Write(p.IfX); w.Write(p.IfY); w.Write(p.IfZ);
                w.Write(p.DoX); w.Write(p.DoY); w.Write(p.DoZ);
                w.Write(p.ForceHurt);
                w.Write(p.DamageTypeDefGuid ?? "");

                var statuses = p.Statuses ?? new List<DamageStatus>();
                w.Write(statuses.Count);
                foreach (var s in statuses) { w.Write(s.DefGuid ?? ""); w.Write(s.Value); w.Write(s.SourceNetId); }

                var effects = p.EffectGuids ?? new List<string>();
                w.Write(effects.Count);
                foreach (var e in effects) w.Write(e ?? "");

                var mods = p.StatMods ?? new List<DamageStatMod>();
                w.Write(mods.Count);
                foreach (var m in mods) { w.Write(m.StatName ?? ""); w.Write(m.ModKind); w.Write(m.Value); }

                w.Write(p.ShooterNetId);
                w.Write(p.ShooterApAfter); w.Write(p.ShooterWpAfter);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeDamage(byte[] data, out DamagePayload payload)
        {
            payload = null;
            // Minimum fixed-size prefix (before the var-length string/lists): seq + target + source +
            // 5 dmg floats + 3+3 vector floats + bool. The strings/lists are guarded by the reader.
            if (data == null || data.Length < 4 + 4 + 4 + (5 * 4) + (6 * 4) + 1) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    var p = new DamagePayload
                    {
                        Seq = r.ReadUInt32(),
                        TargetNetId = r.ReadInt32(),
                        SourceNetId = r.ReadInt32(),
                        HealthDamage = r.ReadSingle(),
                        ArmorDamage = r.ReadSingle(),
                        ArmorMitigatedDamage = r.ReadSingle(),
                        StunValue = r.ReadSingle(),
                        HealValue = r.ReadSingle(),
                        IfX = r.ReadSingle(), IfY = r.ReadSingle(), IfZ = r.ReadSingle(),
                        DoX = r.ReadSingle(), DoY = r.ReadSingle(), DoZ = r.ReadSingle(),
                        ForceHurt = r.ReadBoolean(),
                        DamageTypeDefGuid = r.ReadString(),
                    };

                    int statusCount = r.ReadInt32();
                    if (statusCount < 0 || statusCount > 4096) return false;
                    for (int i = 0; i < statusCount; i++)
                        p.Statuses.Add(new DamageStatus { DefGuid = r.ReadString(), Value = r.ReadSingle(), SourceNetId = r.ReadInt32() });

                    int effectCount = r.ReadInt32();
                    if (effectCount < 0 || effectCount > 4096) return false;
                    for (int i = 0; i < effectCount; i++) p.EffectGuids.Add(r.ReadString());

                    int modCount = r.ReadInt32();
                    if (modCount < 0 || modCount > 4096) return false;
                    for (int i = 0; i < modCount; i++)
                        p.StatMods.Add(new DamageStatMod { StatName = r.ReadString(), ModKind = r.ReadInt32(), Value = r.ReadSingle() });

                    p.ShooterNetId = r.ReadInt32();
                    p.ShooterApAfter = r.ReadSingle();
                    p.ShooterWpAfter = r.ReadSingle();
                    payload = p;
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── tac.fire.start (host→all, Feature C — client-side ATTACK ANIMATION) ───────────────────
        // The host broadcasts this at the MOMENT an actor BEGINS a relayable attack (shoot / grenade-throw /
        // melee — see TacticalAbilityRelay), so the client mirror plays the SHOOTING/THROW animation
        // CONCURRENTLY with the host. DAMAGE is owned exclusively by tac.damage (0x88) — this surface is
        // animation-only: the client replays FireWeaponAtTargetCrt with AttackType.Synced and a neutered
        // FireProjectile (no projectile → ZERO client damage) under a camera-hint guard (no camera fly).
        // Mirrors the EncodeIntentAbility layout (so target encode/decode is identical), MINUS the nonce
        // (an outcome-style host→all push carries a seq instead) PLUS a shotCount (informational; the host's
        // real GetNumberOfShots drives the replay, but it is carried for parity/diagnostics).
        //   [seq:u32][shooterNetId:i32][abilityDefGuid:string][targetNetId:i32][tx:f32][ty:f32][tz:f32][shotCount:i32]
        public struct FireStart
        {
            public uint Seq;
            public int ShooterNetId;
            public string AbilityDefGuid;
            public int TargetNetId;          // -1 sentinel when the target is a bare position, not an actor
            public float TX, TY, TZ;
            public int ShotCount;
            public FireStart(uint seq, int shooterNetId, string abilityDefGuid, int targetNetId,
                float tx, float ty, float tz, int shotCount)
            {
                Seq = seq; ShooterNetId = shooterNetId; AbilityDefGuid = abilityDefGuid ?? "";
                TargetNetId = targetNetId; TX = tx; TY = ty; TZ = tz; ShotCount = shotCount;
            }
        }

        public static byte[] EncodeFireStart(uint seq, int shooterNetId, string abilityDefGuid, int targetNetId,
            float tx, float ty, float tz, int shotCount)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq);
                w.Write(shooterNetId);
                w.Write(abilityDefGuid ?? "");
                w.Write(targetNetId);
                w.Write(tx); w.Write(ty); w.Write(tz);
                w.Write(shotCount);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeFireStart(byte[] data, out FireStart start)
        {
            start = default(FireStart);
            // Minimum: u32 seq + i32 shooter + at least a 1-byte length-prefixed string + i32 target + 3*f32 + i32.
            if (data == null || data.Length < 4 + 4 + 1 + 4 + 12 + 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    int shooter = r.ReadInt32();
                    string guid = r.ReadString();
                    int targetNetId = r.ReadInt32();
                    float tx = r.ReadSingle(); float ty = r.ReadSingle(); float tz = r.ReadSingle();
                    int shotCount = r.ReadInt32();
                    start = new FireStart(seq, shooter, guid, targetNetId, tx, ty, tz, shotCount);
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── tac.melee.start (host→all, Feature C melee — client-side MELEE ANIMATION) ───────────────
        // The MELEE counterpart of tac.fire.start (0x90). The host broadcasts this at the MOMENT an actor
        // BEGINS a melee swing (BashAbility — see TacticalAbilityRelay.MeleeStartAnimAbilityTypeNames), so the
        // client mirror plays the swing animation CONCURRENTLY with the host. DAMAGE is owned exclusively by
        // tac.damage (0x88) — this surface is animation-only. The client REPLAYS the native BashAbility.BashCrt
        // swing coroutine with damage/return-fire/known-counter/charge neutered (TacticalMeleeAnimSync +
        // MeleeAnimSyncPatches). Mirrors the FireStart layout EXACTLY, MINUS the shotCount (a melee is one swing).
        //   [seq:u32][attackerNetId:i32][abilityDefGuid:string][targetNetId:i32][tx:f32][ty:f32][tz:f32]
        public struct MeleeStart
        {
            public uint Seq;
            public int AttackerNetId;
            public string AbilityDefGuid;
            public int TargetNetId;          // -1 sentinel when the target is a bare position, not an actor
            public float TX, TY, TZ;
            public MeleeStart(uint seq, int attackerNetId, string abilityDefGuid, int targetNetId,
                float tx, float ty, float tz)
            {
                Seq = seq; AttackerNetId = attackerNetId; AbilityDefGuid = abilityDefGuid ?? "";
                TargetNetId = targetNetId; TX = tx; TY = ty; TZ = tz;
            }
        }

        public static byte[] EncodeMeleeStart(uint seq, int attackerNetId, string abilityDefGuid, int targetNetId,
            float tx, float ty, float tz)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq);
                w.Write(attackerNetId);
                w.Write(abilityDefGuid ?? "");
                w.Write(targetNetId);
                w.Write(tx); w.Write(ty); w.Write(tz);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeMeleeStart(byte[] data, out MeleeStart start)
        {
            start = default(MeleeStart);
            // Minimum: u32 seq + i32 attacker + at least a 1-byte length-prefixed string + i32 target + 3*f32.
            if (data == null || data.Length < 4 + 4 + 1 + 4 + 12) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    int attacker = r.ReadInt32();
                    string guid = r.ReadString();
                    int targetNetId = r.ReadInt32();
                    float tx = r.ReadSingle(); float ty = r.ReadSingle(); float tz = r.ReadSingle();
                    start = new MeleeStart(seq, attacker, guid, targetNetId, tx, ty, tz);
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── tac.nativemove (host→all, rca-jetjump — ORIGIN-NATIVE MOVE presentation replay) ─────────
        // The host broadcasts this at the MOMENT an actor BEGINS an origin-native MOVE (JetJump — see
        // TacticalAbilityRelay.OriginNativeMoveAbilityTypeNames), so every NON-origin peer plays the REAL native
        // flight animation CONCURRENTLY with the host instead of the 4 Hz 0x8F position snaps (the frozen-in-air
        // mirror). The host runs the move for its own click AND a relayed client intent (re-Activated in
        // HostOnGenericIntent) alike, both through the patched JetJumpAbility.Activate — so one host chokepoint
        // covers host-player + enemy-AI + relayed-client jumps. The ORIGIN client de-dups its own echo via its
        // still-open origin-native-move window (it already ran the native flight). POSITION authority stays with
        // the host (the 0x8F absolute flush + the move's OnPlayingActionEnd reconcile); this surface is
        // presentation-only. Mirrors the MeleeStart layout MINUS the target actor (a JetJump lands at a POSITION).
        //   [seq:u32][actorNetId:i32][abilityDefGuid:string][tx:f32][ty:f32][tz:f32]
        public struct NativeMove
        {
            public uint Seq;
            public int ActorNetId;
            public string AbilityDefGuid;
            public float TX, TY, TZ;   // landing cell (the JetJump target PositionToApply)
            public NativeMove(uint seq, int actorNetId, string abilityDefGuid, float tx, float ty, float tz)
            {
                Seq = seq; ActorNetId = actorNetId; AbilityDefGuid = abilityDefGuid ?? "";
                TX = tx; TY = ty; TZ = tz;
            }
        }

        public static byte[] EncodeNativeMove(uint seq, int actorNetId, string abilityDefGuid,
            float tx, float ty, float tz)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq);
                w.Write(actorNetId);
                w.Write(abilityDefGuid ?? "");
                w.Write(tx); w.Write(ty); w.Write(tz);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeNativeMove(byte[] data, out NativeMove move)
        {
            move = default(NativeMove);
            // Minimum: u32 seq + i32 actor + at least a 1-byte length-prefixed string + 3*f32.
            if (data == null || data.Length < 4 + 4 + 1 + 12) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    int actor = r.ReadInt32();
                    string guid = r.ReadString();
                    float tx = r.ReadSingle(); float ty = r.ReadSingle(); float tz = r.ReadSingle();
                    move = new NativeMove(seq, actor, guid, tx, ty, tz);
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
