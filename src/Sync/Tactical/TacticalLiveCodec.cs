using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multipleer.Sync.Tactical
{
    /// <summary>
    /// PURE (engine-free) wire codecs + seq/dedup for the LIVE tactical outcome rail (spec §5, Inc 2/4):
    /// soldier MOVE + END-TURN. BinaryWriter/Reader only, so it unit-tests in isolation alongside
    /// <see cref="TacticalDeployCodec"/>. The engine glue (<c>TacticalMoveSync</c> / <c>TacticalTurnSync</c>)
    /// binds the live game types via reflection and is NOT in the test assembly.
    ///
    /// Why a SELF-CONTAINED tactical seq (not the geoscape <c>SequenceTracker</c>): the deploy rail already
    /// rides the SurfaceRouter tactical FAST-PATH that deliberately bypasses the action relay's shared global
    /// seq (a request-free host push has no correct seq there, and reusing it would poison post-mission
    /// geoscape action ordering). The live outcome rail rides the SAME fast-path, so it carries its OWN
    /// monotonic seq stamped by the host and a per-surface last-writer-wins guard on the client.
    ///
    /// Frames:
    ///   tac.intent.move      [netId:i32][x:f32][y:f32][z:f32][nonce:u32]                  (client→host)
    ///   tac.move.start       [seq:u32][netId:i32][x:f32][y:f32][z:f32]                    (host→all)
    ///   tac.move             [seq:u32][netId:i32][x:f32][y:f32][z:f32][stopReason:i32]    (host→all)
    ///   tac.intent.endturn   [nonce:u32]                                                  (client→host)
    ///   tac.turn             [seq:u32][currentFactionIndex:i32][turnNumber:i32][factionDefGuid:string]
    /// </summary>
    public static class TacticalLiveCodec
    {
        // ─── tac.intent.move (client→host) ────────────────────────────────
        public struct MoveIntent
        {
            public int NetId;
            public float X, Y, Z;
            public uint Nonce;
            public MoveIntent(int netId, float x, float y, float z, uint nonce)
            { NetId = netId; X = x; Y = y; Z = z; Nonce = nonce; }
        }

        public static byte[] EncodeMoveIntent(int netId, float x, float y, float z, uint nonce)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(netId); w.Write(x); w.Write(y); w.Write(z); w.Write(nonce);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeMoveIntent(byte[] data, out MoveIntent intent)
        {
            intent = default(MoveIntent);
            if (data == null || data.Length < 4 + 4 + 4 + 4 + 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    int netId = r.ReadInt32();
                    float x = r.ReadSingle(); float y = r.ReadSingle(); float z = r.ReadSingle();
                    uint nonce = r.ReadUInt32();
                    intent = new MoveIntent(netId, x, y, z, nonce);
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── tac.move.start (host→all) ────────────────────────────────────
        // Broadcast at the MOMENT the host BEGINS a move (its own click or a relayed client intent), so the
        // client mirror animates CONCURRENTLY with the host instead of waiting for the END outcome. Mirrors
        // the EncodeMove layout MINUS stopReason (the destination is the requested cell, not the landed one).
        public struct MoveStart
        {
            public uint Seq;
            public int NetId;
            public float X, Y, Z;
            public MoveStart(uint seq, int netId, float x, float y, float z)
            { Seq = seq; NetId = netId; X = x; Y = y; Z = z; }
        }

        public static byte[] EncodeMoveStart(uint seq, int netId, float x, float y, float z)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq); w.Write(netId); w.Write(x); w.Write(y); w.Write(z);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeMoveStart(byte[] data, out MoveStart start)
        {
            start = default(MoveStart);
            if (data == null || data.Length < 4 + 4 + 4 + 4 + 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    int netId = r.ReadInt32();
                    float x = r.ReadSingle(); float y = r.ReadSingle(); float z = r.ReadSingle();
                    start = new MoveStart(seq, netId, x, y, z);
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── tac.move (host→all) ──────────────────────────────────────────
        public struct MoveOutcome
        {
            public uint Seq;
            public int NetId;
            public float X, Y, Z;
            public int StopReason;
            public MoveOutcome(uint seq, int netId, float x, float y, float z, int stopReason)
            { Seq = seq; NetId = netId; X = x; Y = y; Z = z; StopReason = stopReason; }
        }

        public static byte[] EncodeMove(uint seq, int netId, float x, float y, float z, int stopReason)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq); w.Write(netId); w.Write(x); w.Write(y); w.Write(z); w.Write(stopReason);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeMove(byte[] data, out MoveOutcome outcome)
        {
            outcome = default(MoveOutcome);
            if (data == null || data.Length < 4 + 4 + 4 + 4 + 4 + 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    int netId = r.ReadInt32();
                    float x = r.ReadSingle(); float y = r.ReadSingle(); float z = r.ReadSingle();
                    int stop = r.ReadInt32();
                    outcome = new MoveOutcome(seq, netId, x, y, z, stop);
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── tac.intent.endturn (client→host) ─────────────────────────────
        public static byte[] EncodeEndTurnIntent(uint nonce)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(nonce);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeEndTurnIntent(byte[] data, out uint nonce)
        {
            nonce = 0;
            if (data == null || data.Length < 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    nonce = r.ReadUInt32();
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── tac.turn (host→all) ──────────────────────────────────────────
        public struct TurnOutcome
        {
            public uint Seq;
            public int CurrentFactionIndex;
            public int TurnNumber;
            public string FactionDefGuid;
            public TurnOutcome(uint seq, int idx, int turnNumber, string guid)
            { Seq = seq; CurrentFactionIndex = idx; TurnNumber = turnNumber; FactionDefGuid = guid ?? ""; }
        }

        public static byte[] EncodeTurn(uint seq, int currentFactionIndex, int turnNumber, string factionDefGuid)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq);
                w.Write(currentFactionIndex);
                w.Write(turnNumber);
                w.Write(factionDefGuid ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryDecodeTurn(byte[] data, out TurnOutcome outcome)
        {
            outcome = default(TurnOutcome);
            if (data == null || data.Length < 4 + 4 + 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    int idx = r.ReadInt32();
                    int turn = r.ReadInt32();
                    string guid = r.ReadString();
                    outcome = new TurnOutcome(seq, idx, turn, guid);
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── tac.intent.ability (client→host, Inc 3a) ─────────────────────
        // A client SHOOT intent: which actor shoots which ability (by def guid) at which target. The target
        // is carried as BOTH a netId (actor target; -1 sentinel when the shot is at a bare position) AND a
        // position (always present — the host rebuilds a TacticalAbilityTarget from whichever is valid).
        public struct IntentAbility
        {
            public int ShooterNetId;
            public string AbilityDefGuid;
            public int TargetNetId;          // -1 sentinel when the target is a position, not an actor
            public float TX, TY, TZ;
            public uint Nonce;
            public IntentAbility(int shooterNetId, string abilityDefGuid, int targetNetId,
                float tx, float ty, float tz, uint nonce)
            {
                ShooterNetId = shooterNetId; AbilityDefGuid = abilityDefGuid ?? ""; TargetNetId = targetNetId;
                TX = tx; TY = ty; TZ = tz; Nonce = nonce;
            }
        }

        public const int TargetNetIdNone = -1;   // sentinel: the shot target is a position, not an actor

        public static byte[] EncodeIntentAbility(int shooterNetId, string abilityDefGuid, int targetNetId,
            float tx, float ty, float tz, uint nonce)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(shooterNetId);
                w.Write(abilityDefGuid ?? "");
                w.Write(targetNetId);
                w.Write(tx); w.Write(ty); w.Write(tz);
                w.Write(nonce);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeIntentAbility(byte[] data, out IntentAbility intent)
        {
            intent = default(IntentAbility);
            // Minimum: i32 shooter + at least a 1-byte length-prefixed string + i32 target + 3*f32 + u32.
            if (data == null || data.Length < 4 + 1 + 4 + 12 + 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    int shooter = r.ReadInt32();
                    string guid = r.ReadString();
                    int targetNetId = r.ReadInt32();
                    float tx = r.ReadSingle(); float ty = r.ReadSingle(); float tz = r.ReadSingle();
                    uint nonce = r.ReadUInt32();
                    intent = new IntentAbility(shooter, guid, targetNetId, tx, ty, tz, nonce);
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
        // tac.damage (0x88) — this surface is animation-only. Phase 1 is the WIRE FOUNDATION only: the client
        // decodes + STUB-logs (no replay yet); the BashCrt-shaped replay + damage/cost neuter is a follow-on.
        // Mirrors the FireStart layout EXACTLY, MINUS the shotCount (a melee is a single swing).
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

        // ─── tac.intent.equip (client→host, Inc Equip) ───────────────────
        // A client EQUIPMENT-SWAP intent: which actor switches its SELECTED equipment to which slot. The
        // equipment is identified by its INDEX in the actor's ordered equipment list (actor.Equipments.Equipments,
        // a List<Equipment>) — both sides build the SAME list from the shared save, so the index is a stable
        // cross-side identifier (mirrors the deploy/SwitchActorEquipmentSeqAction convention, which also keys
        // on the equipment index). EquipIndexNone (-1) means "select null" (e.g. a death-cascade clears the
        // selection). No AP/WP rides along: selecting a weapon is FREE in the engine (the AP cost lives in the
        // abilities the weapon exposes, charged at fire time — EquipmentComponent.SetSelectedEquipment spends none).
        public struct EquipIntent
        {
            public int ActorNetId;
            public int EquipIndex;   // -1 sentinel = select null (no equipment)
            public uint Nonce;
            public EquipIntent(int actorNetId, int equipIndex, uint nonce)
            { ActorNetId = actorNetId; EquipIndex = equipIndex; Nonce = nonce; }
        }

        public const int EquipIndexNone = -1;   // sentinel: select null (no equipment)

        public static byte[] EncodeEquipIntent(int actorNetId, int equipIndex, uint nonce)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(actorNetId); w.Write(equipIndex); w.Write(nonce);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeEquipIntent(byte[] data, out EquipIntent intent)
        {
            intent = default(EquipIntent);
            if (data == null || data.Length < 4 + 4 + 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    int actorNetId = r.ReadInt32();
                    int equipIndex = r.ReadInt32();
                    uint nonce = r.ReadUInt32();
                    intent = new EquipIntent(actorNetId, equipIndex, nonce);
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── tac.equip (host→all, Inc Equip) ──────────────────────────────
        // The host's authoritative outcome of an equipment swap: the actor now has the equipment at this index
        // selected. The client mirrors it by calling the SAME EquipmentComponent.SetSelectedEquipment with the
        // equipment at this index (which updates BOTH the visible weapon — holster/draw-out — AND the
        // abilities-available, so a subsequent client shoot resolves against the synced weapon). Self-contained
        // tactical seq (last-writer-wins) like tac.move / tac.turn / tac.vision.
        public struct EquipOutcome
        {
            public uint Seq;
            public int ActorNetId;
            public int EquipIndex;   // -1 sentinel = null selection
            public EquipOutcome(uint seq, int actorNetId, int equipIndex)
            { Seq = seq; ActorNetId = actorNetId; EquipIndex = equipIndex; }
        }

        public static byte[] EncodeEquip(uint seq, int actorNetId, int equipIndex)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq); w.Write(actorNetId); w.Write(equipIndex);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeEquip(byte[] data, out EquipOutcome outcome)
        {
            outcome = default(EquipOutcome);
            if (data == null || data.Length < 4 + 4 + 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    int actorNetId = r.ReadInt32();
                    int equipIndex = r.ReadInt32();
                    outcome = new EquipOutcome(seq, actorNetId, equipIndex);
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>PURE index-validity helper shared by the host/client equip appliers (unit-tested without
        /// engine types): an equip index is APPLICABLE when it is the null sentinel (-1, "select null") OR a
        /// real slot inside the actor's equipment list [0, listCount). Anything else (a stale/corrupt index ≥
        /// listCount, or any negative other than -1) is rejected so a desynced list never indexes out of range.</summary>
        public static bool IsApplicableEquipIndex(int equipIndex, int listCount)
        {
            if (equipIndex == EquipIndexNone) return true;          // select null
            return equipIndex >= 0 && equipIndex < listCount;
        }

        // ─── tac.intent.overwatch (client→host, Inc Overwatch) ────────────
        // A client OVERWATCH-ARM intent: which actor goes on overwatch, watching which CONE. The cone is built
        // entirely CLIENT-side (UIStateOverwatchAbilitySelected builds it from the player's cursor +
        // OverwatchAbility.GetAbilityTargetCone), so the host has no way to re-derive the player's chosen watch
        // direction/spread — the intent MUST carry the flattened cone. The cone is the engine's
        // Base.Utils.Maths.Cone struct, whose REAL serializable fields are Tip(Vector3), Height(float),
        // Radius(float), and _forward(Vector3, set via the normalizing Forward property) — flattened here as
        // 8 floats (Tip.xyz, Height, Radius, Forward.xyz). The host rebuilds the Cone, wraps it in a
        // TacticalAbilityTarget{Cone=…}, and re-invokes OverwatchAbility.Activate so it is authoritatively armed
        // (→ it triggers reaction fire on enemy moves; the reaction DAMAGE already replicates via tac.damage).
        public struct OverwatchIntent
        {
            public int ActorNetId;
            public uint Nonce;
            public float TipX, TipY, TipZ, Height, Radius, FwdX, FwdY, FwdZ;
            public OverwatchIntent(int actorNetId, uint nonce,
                float tipX, float tipY, float tipZ, float height, float radius, float fwdX, float fwdY, float fwdZ)
            {
                ActorNetId = actorNetId; Nonce = nonce;
                TipX = tipX; TipY = tipY; TipZ = tipZ; Height = height; Radius = radius;
                FwdX = fwdX; FwdY = fwdY; FwdZ = fwdZ;
            }
        }

        public static byte[] EncodeOverwatchIntent(int actorNetId, uint nonce,
            float tipX, float tipY, float tipZ, float height, float radius, float fwdX, float fwdY, float fwdZ)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(actorNetId); w.Write(nonce);
                w.Write(tipX); w.Write(tipY); w.Write(tipZ); w.Write(height); w.Write(radius);
                w.Write(fwdX); w.Write(fwdY); w.Write(fwdZ);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeOverwatchIntent(byte[] data, out OverwatchIntent intent)
        {
            intent = default(OverwatchIntent);
            // i32 actor + u32 nonce + 8 floats = 4 + 4 + 32 = 40 bytes.
            if (data == null || data.Length < 4 + 4 + (8 * 4)) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    int actorNetId = r.ReadInt32();
                    uint nonce = r.ReadUInt32();
                    float tipX = r.ReadSingle(), tipY = r.ReadSingle(), tipZ = r.ReadSingle();
                    float height = r.ReadSingle(), radius = r.ReadSingle();
                    float fwdX = r.ReadSingle(), fwdY = r.ReadSingle(), fwdZ = r.ReadSingle();
                    intent = new OverwatchIntent(actorNetId, nonce, tipX, tipY, tipZ, height, radius, fwdX, fwdY, fwdZ);
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── tac.overwatch.state (host→all, Inc Overwatch) ────────────────
        // The host's authoritative overwatch STATE for an actor: armed (with the watch cone) or cleared. Two
        // triggers, both funnelling through OverwatchStatus.SetCone(Cone?): ARM (SetCone(realCone), from the
        // host running OverwatchAbility.Activate — its own click OR a relayed client intent) and CLEAR
        // (SetCone(null), from OverwatchStatus.OnUnapply — fired by EVERY status removal: consume-after-reaction,
        // next-turn expiry, manual cancel). The client applies it COSMETICALLY: when armed it (re)creates the
        // actor's OverwatchStatus + SetCone so the cone shows; when cleared it removes that status so the cone
        // disappears. The client's mirrored status is INERT (client enemy-moves mirror with TriggerOverwatch=
        // false), so it NEVER double reaction-fires. Self-contained tactical seq (last-writer-wins) like the
        // other live surfaces. When !Armed the cone fields are omitted (clear is a 9-byte frame).
        public struct OverwatchState
        {
            public uint Seq;
            public int ActorNetId;
            public bool Armed;
            public float TipX, TipY, TipZ, Height, Radius, FwdX, FwdY, FwdZ;   // valid only when Armed
            public OverwatchState(uint seq, int actorNetId, bool armed,
                float tipX, float tipY, float tipZ, float height, float radius, float fwdX, float fwdY, float fwdZ)
            {
                Seq = seq; ActorNetId = actorNetId; Armed = armed;
                TipX = tipX; TipY = tipY; TipZ = tipZ; Height = height; Radius = radius;
                FwdX = fwdX; FwdY = fwdY; FwdZ = fwdZ;
            }
        }

        public static byte[] EncodeOverwatchState(uint seq, int actorNetId, bool armed,
            float tipX, float tipY, float tipZ, float height, float radius, float fwdX, float fwdY, float fwdZ)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq); w.Write(actorNetId); w.Write(armed);
                if (armed)
                {
                    w.Write(tipX); w.Write(tipY); w.Write(tipZ); w.Write(height); w.Write(radius);
                    w.Write(fwdX); w.Write(fwdY); w.Write(fwdZ);
                }
                return ms.ToArray();
            }
        }

        /// <summary>Encode a CLEAR (armed=false) state — the cone fields are zeroed/omitted.</summary>
        public static byte[] EncodeOverwatchClear(uint seq, int actorNetId)
            => EncodeOverwatchState(seq, actorNetId, false, 0, 0, 0, 0, 0, 0, 0, 0);

        public static bool TryDecodeOverwatchState(byte[] data, out OverwatchState state)
        {
            state = default(OverwatchState);
            // Fixed prefix: u32 seq + i32 actor + 1-byte armed = 9 bytes. When armed, +8 floats (32 bytes).
            if (data == null || data.Length < 4 + 4 + 1) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    int actorNetId = r.ReadInt32();
                    bool armed = r.ReadBoolean();
                    if (!armed)
                    {
                        state = new OverwatchState(seq, actorNetId, false, 0, 0, 0, 0, 0, 0, 0, 0);
                        return true;
                    }
                    // Armed → the cone fields MUST be present; a truncated armed frame is a clean reject.
                    if (ms.Length - ms.Position < 8 * 4) return false;
                    float tipX = r.ReadSingle(), tipY = r.ReadSingle(), tipZ = r.ReadSingle();
                    float height = r.ReadSingle(), radius = r.ReadSingle();
                    float fwdX = r.ReadSingle(), fwdY = r.ReadSingle(), fwdZ = r.ReadSingle();
                    state = new OverwatchState(seq, actorNetId, true, tipX, tipY, tipZ, height, radius, fwdX, fwdY, fwdZ);
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── tac.vision (host→all, Inc Vision) ────────────────────────────
        // A full RECONCILE snapshot of the player/viewer faction's KnownActors: which enemy/actor netIds the
        // host currently knows, and at what state (2=Revealed/red — seen+LOS; 1=Located/grey — position known).
        // The host pushes this on every FactionKnowledgeChangedEvent for the player faction; the client RECONCILES
        // its mirror of that faction's vision to the snapshot (set listed, forget absent). Self-contained tactical
        // seq (last-writer-wins) like tac.move / tac.turn. Engine-free codec → unit-testable.
        //   [seq:u32][viewerFactionIndex:i32][count:i32]  then per entry: [netId:i32][knownState:i32]
        public struct VisionEntry
        {
            public int NetId;
            public int KnownState;   // 2 = Revealed (red), 1 = Located (grey)
            public VisionEntry(int netId, int knownState) { NetId = netId; KnownState = knownState; }
        }

        public struct VisionSnapshot
        {
            public uint Seq;
            public int ViewerFactionIndex;
            public List<VisionEntry> Entries;
            public VisionSnapshot(uint seq, int viewerFactionIndex, List<VisionEntry> entries)
            { Seq = seq; ViewerFactionIndex = viewerFactionIndex; Entries = entries ?? new List<VisionEntry>(); }
        }

        /// <summary>Wire state values (also the engine→client mapping). Higher = stronger knowledge.</summary>
        public const int VisionStateRevealed = 2;   // RED  — seen + line of sight
        public const int VisionStateLocated = 1;     // GREY — position known, no LOS

        public static byte[] EncodeVision(uint seq, int viewerFactionIndex, List<VisionEntry> entries)
        {
            entries = entries ?? new List<VisionEntry>();
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq);
                w.Write(viewerFactionIndex);
                w.Write(entries.Count);
                foreach (var e in entries) { w.Write(e.NetId); w.Write(e.KnownState); }
                return ms.ToArray();
            }
        }

        public static bool TryDecodeVision(byte[] data, out VisionSnapshot snapshot)
        {
            snapshot = default(VisionSnapshot);
            // Minimum: seq + viewerFactionIndex + count = 12 bytes.
            if (data == null || data.Length < 4 + 4 + 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    int viewerFactionIndex = r.ReadInt32();
                    int count = r.ReadInt32();
                    if (count < 0 || count > 4096) return false;
                    // Each entry is fixed 8 bytes (2*i32); guard the count against the remaining buffer so a
                    // corrupt huge count can't allocate wildly (mirrors TacticalDeployCodec's actor-table guard).
                    if ((long)count * 8 > ms.Length - ms.Position) return false;
                    var entries = new List<VisionEntry>(count);
                    for (int i = 0; i < count; i++)
                    {
                        int netId = r.ReadInt32();
                        int state = r.ReadInt32();
                        entries.Add(new VisionEntry(netId, state));
                    }
                    snapshot = new VisionSnapshot(seq, viewerFactionIndex, entries);
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── tac.actorstate (host→all, Inc T1 — generic per-actor STATE-DELTA spine) ───────────────────
        // A batch of CHANGED-actor records (the host ships only actors whose signature drifted this flush).
        // Each per-actor record is EXTENSIBLE via a u16 fieldMask: a field's bytes are present ONLY when its
        // bit is set, so later increments fold in position/facing/health/armor/selected-equip/overwatch-cone
        // WITHOUT a wire break (an old decoder reading a record with an unknown bit set would misalign — so the
        // mask is read strictly in bit order and unknown bits beyond the ones we encode are never set by us;
        // the decoder consumes exactly the fields for the bits it knows, in ascending bit order). T1 encodes
        // only AP / WP / STATUSES. All values are ABSOLUTE (re-applying a record is a no-op → idempotent).
        //   [seq:u32][count:i32]  then per actor (fields in ASCENDING bit order):
        //     [netId:i32][fieldMask:u16]
        //     (if AP)       [ap:f32]
        //     (if WP)       [wp:f32]
        //     (if STATUSES) [statusCount:i32]  then per status: [defGuid:string][sourceNetId:i32][value:f32]
        //     (if POS)      [posX:f32][posY:f32][posZ:f32]                                   (Inc1 full-state)
        //     (if HEALTH)   [health:f32]
        //     (if BODYPART) [partCount:i32]  then per part: [slotName:string][hp:f32]

        /// <summary>fieldMask bit assignments. T1 encodes AP|WP|STATUSES; the rest are RESERVED for later
        /// increments (encode/decode them in ascending bit order when added).</summary>
        public const ushort ActorFieldAp        = 0x0001;
        public const ushort ActorFieldWp        = 0x0002;
        public const ushort ActorFieldStatuses  = 0x0004;
        // Inc1 full-state: actor ABSOLUTE POSITION (world Vector3 → 3×f32). The single host→client state writer
        // for soldier movement: the client turns a POSITION delta into a native walk animation (Navigate) when
        // the change is a plausible move, or an instant snap (SetPosition) for a sub-cell nudge / large
        // disconnected jump (see TacticalActorStateDiff.DecidePositionApply). ABSOLUTE (re-apply = idempotent).
        // Encoded in ascending bit order AFTER statuses (0x0004) and BEFORE facing/health. Runs ADDITIVE
        // alongside the tac.move / tac.move.start rails (which stay the proven path until a later increment).
        public const ushort ActorFieldPos       = 0x0008;   // 3×f32 position
        // Reserved (NOT encoded yet) — fold in ascending bit order:
        public const ushort ActorFieldFacing    = 0x0010;   // 3×f32 forward (Inc2)
        // Feature D: actor-level absolute HEALTH (HP) mirror. Carries the host's CURRENT HP (float) so a
        // host-side HEAL or any non-damage HP drift converges on the client (HP DECREASES already replicate
        // via tac.damage 0x88; DEATH stays owned by tac.damage — the client apply is DEATH-SAFE, see
        // TacticalActorStateSync). Max HP is set at deploy (Health.SetMax(Toughness)) and not carried.
        // Encoded in ascending bit order AFTER statuses (0x0004) and BEFORE bodypart-HP (0x0200).
        public const ushort ActorFieldHealth    = 0x0020;   // f32 absolute current health
        public const ushort ActorFieldArmor     = 0x0040;   // f32 absolute armor (backstop)
        public const ushort ActorFieldEquip     = 0x0080;   // i32 selected-equip index
        public const ushort ActorFieldOverwatch = 0x0100;   // bool + 8×f32 cone
        // Feature B: per-bodypart HP sub-channel (limb-disable mirror). Keyed by ItemSlot.GetSlotName() — a
        // STABLE host↔client key (both sides build the body from the SAME shared save → identical slot names;
        // the bodypart def guid would also be stable but the slot name is what the healthbar UI + stat
        // registration already key on, so it is the canonical identity). The client SETS each part's StatusStat
        // HP so the native StatChangeEvent → OnBodyPartHealthChanged drives the engine's own limb-disable path
        // (ItemSlot.SetToDisabled() flips ItemSlot.Enabled when GetHealth() <= 1E-05) and its UI fires for free
        // (limb-disable is NOT a status). Encoded in ascending bit order AFTER statuses (it is the highest bit
        // we emit; the reserved bits between are never set by us).
        public const ushort ActorFieldBodyPartHp = 0x0200;  // i32 count then per part: [slotName:string][hp:f32]

        /// <summary>One synced status on the wire (T1): def guid + source-actor netId (-1 = none) + value.</summary>
        public struct ActorStatus
        {
            public string DefGuid;
            public int SourceNetId;
            public float Value;
            public ActorStatus(string defGuid, int sourceNetId, float value)
            { DefGuid = defGuid ?? ""; SourceNetId = sourceNetId; Value = value; }
        }

        /// <summary>One bodypart HP entry on the wire (Feature B): the slot name (stable host↔client key) + the
        /// part's absolute current HP. The client sets the part's StatusStat to this so the native body-part
        /// changed event drives the disabled-limb UI (limb-disable is NOT modelled as a status).</summary>
        public struct BodyPartHp
        {
            public string SlotName;
            public float Hp;
            public BodyPartHp(string slotName, float hp) { SlotName = slotName ?? ""; Hp = hp; }
        }

        /// <summary>One per-actor state record. Only the fields whose <see cref="FieldMask"/> bit is set are
        /// valid on the wire (and were read from the host actor).</summary>
        public sealed class ActorStateRecord
        {
            public int NetId;
            public ushort FieldMask;
            public float Ap;
            public float Wp;
            public float Health;   // Feature D: absolute current HP (valid only when HasHealth)
            public float PosX, PosY, PosZ;   // Inc1 full-state: absolute world position (valid only when HasPos)
            public List<ActorStatus> Statuses = new List<ActorStatus>();
            public List<BodyPartHp> BodyParts = new List<BodyPartHp>();

            public bool HasAp => (FieldMask & ActorFieldAp) != 0;
            public bool HasWp => (FieldMask & ActorFieldWp) != 0;
            public bool HasStatuses => (FieldMask & ActorFieldStatuses) != 0;
            public bool HasPos => (FieldMask & ActorFieldPos) != 0;
            public bool HasHealth => (FieldMask & ActorFieldHealth) != 0;
            public bool HasBodyParts => (FieldMask & ActorFieldBodyPartHp) != 0;
        }

        public sealed class ActorStateBatch
        {
            public uint Seq;
            public List<ActorStateRecord> Actors = new List<ActorStateRecord>();
            public ActorStateBatch() { }
            public ActorStateBatch(uint seq, List<ActorStateRecord> actors)
            { Seq = seq; Actors = actors ?? new List<ActorStateRecord>(); }
        }

        /// <summary>Hard caps so a corrupt count can never allocate wildly.</summary>
        public const int ActorStateMaxActors = 4096;
        public const int ActorStateMaxStatusesPerActor = 1024;
        public const int ActorStateMaxBodyPartsPerActor = 256;

        public static byte[] EncodeActorState(ActorStateBatch batch)
        {
            batch = batch ?? new ActorStateBatch();
            var actors = batch.Actors ?? new List<ActorStateRecord>();
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(batch.Seq);
                w.Write(actors.Count);
                foreach (var a in actors)
                {
                    w.Write(a.NetId);
                    w.Write(a.FieldMask);
                    if ((a.FieldMask & ActorFieldAp) != 0) w.Write(a.Ap);
                    if ((a.FieldMask & ActorFieldWp) != 0) w.Write(a.Wp);
                    if ((a.FieldMask & ActorFieldStatuses) != 0)
                    {
                        var statuses = a.Statuses ?? new List<ActorStatus>();
                        w.Write(statuses.Count);
                        foreach (var s in statuses)
                        {
                            w.Write(s.DefGuid ?? "");
                            w.Write(s.SourceNetId);
                            w.Write(s.Value);
                        }
                    }
                    // POSITION (0x0008) — Inc1 full-state. Encoded AFTER statuses (0x0004) and BEFORE health
                    // (0x0020), i.e. ascending bit order. Absolute world Vector3 as 3×f32.
                    if ((a.FieldMask & ActorFieldPos) != 0)
                    {
                        w.Write(a.PosX);
                        w.Write(a.PosY);
                        w.Write(a.PosZ);
                    }
                    // HEALTH (0x0020) — Feature D. Encoded AFTER statuses (0x0004), BEFORE bodypart-HP (0x0200),
                    // i.e. ascending bit order. Absolute current HP (float).
                    if ((a.FieldMask & ActorFieldHealth) != 0) w.Write(a.Health);
                    // BODYPARTHP is the highest bit we emit → encoded LAST (ascending bit order).
                    if ((a.FieldMask & ActorFieldBodyPartHp) != 0)
                    {
                        var parts = a.BodyParts ?? new List<BodyPartHp>();
                        w.Write(parts.Count);
                        foreach (var p in parts)
                        {
                            w.Write(p.SlotName ?? "");
                            w.Write(p.Hp);
                        }
                    }
                }
                return ms.ToArray();
            }
        }

        public static bool TryDecodeActorState(byte[] data, out ActorStateBatch batch)
        {
            batch = null;
            // Minimum: seq(u32) + count(i32) = 8 bytes.
            if (data == null || data.Length < 8) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    int count = r.ReadInt32();
                    if (count < 0 || count > ActorStateMaxActors) return false;

                    var result = new ActorStateBatch { Seq = seq, Actors = new List<ActorStateRecord>(count) };
                    for (int i = 0; i < count; i++)
                    {
                        // Per-actor fixed prefix: netId(i32) + fieldMask(u16) = 6 bytes.
                        if (ms.Length - ms.Position < 6) return false;
                        var rec = new ActorStateRecord
                        {
                            NetId = r.ReadInt32(),
                            FieldMask = r.ReadUInt16(),
                        };
                        if ((rec.FieldMask & ActorFieldAp) != 0)
                        {
                            if (ms.Length - ms.Position < 4) return false;
                            rec.Ap = r.ReadSingle();
                        }
                        if ((rec.FieldMask & ActorFieldWp) != 0)
                        {
                            if (ms.Length - ms.Position < 4) return false;
                            rec.Wp = r.ReadSingle();
                        }
                        if ((rec.FieldMask & ActorFieldStatuses) != 0)
                        {
                            if (ms.Length - ms.Position < 4) return false;
                            int sc = r.ReadInt32();
                            if (sc < 0 || sc > ActorStateMaxStatusesPerActor) return false;
                            for (int j = 0; j < sc; j++)
                            {
                                // string is length-prefixed (guarded by the reader); then i32 + f32 = 8 bytes.
                                string guid = r.ReadString();
                                if (ms.Length - ms.Position < 8) return false;
                                int src = r.ReadInt32();
                                float val = r.ReadSingle();
                                rec.Statuses.Add(new ActorStatus(guid, src, val));
                            }
                        }
                        // POSITION (0x0008) — Inc1 full-state. Decoded AFTER statuses, BEFORE health (ascending
                        // bit order), only if its bit is set. 3×f32 = 12 bytes, guarded as a unit.
                        if ((rec.FieldMask & ActorFieldPos) != 0)
                        {
                            if (ms.Length - ms.Position < 12) return false;
                            rec.PosX = r.ReadSingle();
                            rec.PosY = r.ReadSingle();
                            rec.PosZ = r.ReadSingle();
                        }
                        // HEALTH (0x0020) — Feature D. Decoded AFTER statuses, BEFORE bodypart-HP (ascending
                        // bit order), only if its bit is set.
                        if ((rec.FieldMask & ActorFieldHealth) != 0)
                        {
                            if (ms.Length - ms.Position < 4) return false;
                            rec.Health = r.ReadSingle();
                        }
                        // BODYPARTHP decoded LAST (ascending bit order), only if its bit is set.
                        if ((rec.FieldMask & ActorFieldBodyPartHp) != 0)
                        {
                            if (ms.Length - ms.Position < 4) return false;
                            int bc = r.ReadInt32();
                            if (bc < 0 || bc > ActorStateMaxBodyPartsPerActor) return false;
                            for (int j = 0; j < bc; j++)
                            {
                                // string is length-prefixed (guarded by the reader); then f32 = 4 bytes.
                                string slot = r.ReadString();
                                if (ms.Length - ms.Position < 4) return false;
                                float hp = r.ReadSingle();
                                rec.BodyParts.Add(new BodyPartHp(slot, hp));
                            }
                        }
                        result.Actors.Add(rec);
                    }
                    batch = result;
                    return true;
                }
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// HOST: monotonic per-surface seq source for live outcomes. CLIENT: last-writer-wins guard. PURE
    /// (no engine types). One instance per live mission on each side; reset on mission exit / re-deploy.
    ///
    /// Seq is assigned PER SURFACE (tac.move and tac.turn each get an independent monotonic stream) so a
    /// turn outcome never suppresses a move outcome and vice-versa. The host emits over a reliable, per-peer
    /// ORDERED transport, so a strictly-greater check is sufficient last-writer-wins (a stale duplicate or
    /// re-send is dropped; nothing newer can be overtaken).
    /// </summary>
    public sealed class TacticalLiveSeq
    {
        private readonly Dictionary<ushort, uint> _hostNext = new Dictionary<ushort, uint>();
        private readonly Dictionary<ushort, uint> _clientLast = new Dictionary<ushort, uint>();

        /// <summary>HOST: take the next monotonic seq for a surface (starts at 1).</summary>
        public uint Next(ushort surfaceId)
        {
            _hostNext.TryGetValue(surfaceId, out var cur);
            uint next = cur + 1;
            _hostNext[surfaceId] = next;
            return next;
        }

        /// <summary>CLIENT: true if this seq is newer than the last applied for the surface. Does NOT mark
        /// (call <see cref="Mark"/> after a successful apply) so a failed apply can be retried by a re-send.</summary>
        public bool ShouldApply(ushort surfaceId, uint seq)
        {
            _clientLast.TryGetValue(surfaceId, out var last);
            return seq > last;
        }

        /// <summary>CLIENT: record the last applied seq for a surface.</summary>
        public void Mark(ushort surfaceId, uint seq)
        {
            _clientLast.TryGetValue(surfaceId, out var last);
            if (seq > last) _clientLast[surfaceId] = seq;
        }

        public void Reset()
        {
            _hostNext.Clear();
            _clientLast.Clear();
        }

        /// <summary>HOST: capture-time per-mission seq hook, called from the deploy capture.
        /// Intentionally does NOT touch the host streams: by the time the deploy capture runs the level
        /// is already turn-0-ready and the pre-deploy <c>tac.turn</c> (seq=1) has been emitted on this
        /// instance. Recreating/resetting the stream here (the previous <c>LiveSeq = new TacticalLiveSeq()</c>)
        /// rewound <c>_hostNext[TacTurn]</c> to 0, so the next turn re-emitted seq=1 and the client's strict
        /// <c>seq &gt; last</c> guard dropped it ⇒ "turn doesn't end". The stream is created exactly once per
        /// mission (constructor + <c>OnMissionExit</c> reset) and must survive the capture monotonically.</summary>
        public void BeginDeployCaptureMission()
        {
            // No-op by design: the host seq streams must survive a mid-mission deploy capture (never rewind).
        }
    }

    /// <summary>
    /// HOST-side intent de-duplicator: the reliable transport can double-send a client intent envelope; a
    /// double-applied MOVE would step the actor twice. Keyed by the intent's (surfaceId, nonce); a bounded
    /// ring drops the oldest so memory stays flat over a long battle. PURE (no engine types).
    /// </summary>
    public sealed class TacticalIntentDedup
    {
        private readonly int _capacity;
        private readonly HashSet<ulong> _seen = new HashSet<ulong>();
        private readonly Queue<ulong> _order = new Queue<ulong>();

        public TacticalIntentDedup(int capacity = 512) { _capacity = capacity < 16 ? 16 : capacity; }

        private static ulong Key(ushort surfaceId, uint nonce) => ((ulong)surfaceId << 32) | nonce;

        /// <summary>True the FIRST time a (surface,nonce) is offered; false on any repeat (drop it).</summary>
        public bool IsNew(ushort surfaceId, uint nonce)
        {
            ulong k = Key(surfaceId, nonce);
            if (_seen.Contains(k)) return false;
            _seen.Add(k);
            _order.Enqueue(k);
            if (_order.Count > _capacity) _seen.Remove(_order.Dequeue());
            return true;
        }

        public void Reset()
        {
            _seen.Clear();
            _order.Clear();
        }
    }
}
