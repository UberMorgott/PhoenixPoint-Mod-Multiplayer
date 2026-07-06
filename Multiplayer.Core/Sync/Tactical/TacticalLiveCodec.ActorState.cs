using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Sync.Tactical
{
    public static partial class TacticalLiveCodec
    {
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
        //     (if FACING)   [facingX:f32][facingY:f32][facingZ:f32]                          (Inc2)
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
        // Inc2 full-state: actor ABSOLUTE FACING (world forward Vector3 → 3×f32). The host reads
        // ActorComponent.Rot (transform.rotation) → forward = Rot * Vector3.forward; the client applies it via
        // ActorComponent.SetForward so a turn-in-place / post-move heading mirrors (and the derived crouch/cover
        // idle pose follows from Pos+Facing). ABSOLUTE (re-apply = idempotent). Encoded in ascending bit order
        // AFTER position (0x0008) and BEFORE health (0x0020). Runs ADDITIVE alongside the existing rails.
        public const ushort ActorFieldFacing    = 0x0010;   // 3×f32 forward (Inc2)
        // Feature D: actor-level absolute HEALTH (HP) mirror. Carries the host's CURRENT HP (float) so a
        // host-side HEAL or any non-damage HP drift converges on the client (HP DECREASES already replicate
        // via tac.damage 0x88; DEATH stays owned by tac.damage — the client apply is DEATH-SAFE, see
        // TacticalActorStateSync). Max HP is set at deploy (Health.SetMax(Toughness)) and not carried.
        // Encoded in ascending bit order AFTER statuses (0x0004) and BEFORE bodypart-HP (0x0200).
        public const ushort ActorFieldHealth    = 0x0020;   // f32 absolute current health
        // Reserved (NOT encoded yet) — fold in ascending bit order:
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
        // TS5 (a): per-equipped-weapon MAGAZINE charges. Keyed by the equipment's INDEX in the actor's ordered
        // Equipments.Equipments list (the SAME stable host↔client key the equip surface 0x8A/0x8B uses — both sides
        // build the list from the shared save). The client value-writes each weapon's CurrentCharges so a host
        // reload / any host-side ammo change converges (magazine count matches on both). Encoded AFTER bodypart-HP
        // (0x0200) in ascending bit order. ABSOLUTE (re-apply = idempotent). i32 count then per weapon [slotIndex:i32][charges:i32].
        public const ushort ActorFieldAmmo      = 0x0400;   // i32 count then per weapon: [slotIndex:i32][charges:i32]
        // TS5 (b): actor CURRENT faction INDEX (position in TacticalLevelController.Factions). DISPLAY-ONLY mirror of
        // a mind-control / zombify faction flip — the client stamps the display faction so the unit's side/healthbar
        // repaints, WITHOUT the native SetFaction sim re-home (TacticalLevel.ActorFactionChanged). Encoded LAST
        // (highest bit we emit, ascending order) as a single i32. ABSOLUTE (re-apply gated to on-change → no churn).
        public const ushort ActorFieldFaction   = 0x0800;   // i32 faction index (display-only)

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

        /// <summary>One per-weapon ammo entry on the wire (TS5 a): the equipment's index in the actor's ordered
        /// Equipments.Equipments list (stable host↔client key, like the equip surface) + the weapon's absolute
        /// current magazine charges. The client value-writes this so a reload / ammo change converges.</summary>
        public struct WeaponAmmo
        {
            public int SlotIndex;
            public int Charges;
            public WeaponAmmo(int slotIndex, int charges) { SlotIndex = slotIndex; Charges = charges; }
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
            public float FacingX, FacingY, FacingZ;   // Inc2: absolute world forward vector (valid only when HasFacing)
            public int Faction;    // TS5 (b): absolute faction index (display-only, valid only when HasFaction)
            public List<ActorStatus> Statuses = new List<ActorStatus>();
            public List<BodyPartHp> BodyParts = new List<BodyPartHp>();
            public List<WeaponAmmo> Ammo = new List<WeaponAmmo>();   // TS5 (a): per-weapon magazine charges (valid only when HasAmmo)

            public bool HasAp => (FieldMask & ActorFieldAp) != 0;
            public bool HasWp => (FieldMask & ActorFieldWp) != 0;
            public bool HasStatuses => (FieldMask & ActorFieldStatuses) != 0;
            public bool HasPos => (FieldMask & ActorFieldPos) != 0;
            public bool HasFacing => (FieldMask & ActorFieldFacing) != 0;
            public bool HasHealth => (FieldMask & ActorFieldHealth) != 0;
            public bool HasBodyParts => (FieldMask & ActorFieldBodyPartHp) != 0;
            public bool HasAmmo => (FieldMask & ActorFieldAmmo) != 0;
            public bool HasFaction => (FieldMask & ActorFieldFaction) != 0;
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
        public const int ActorStateMaxWeaponsPerActor = 256;   // TS5 (a): guard the per-actor ammo count

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
                    // FACING (0x0010) — Inc2. Encoded AFTER position (0x0008) and BEFORE health (0x0020), i.e.
                    // ascending bit order. Absolute world forward vector as 3×f32 (client applies via
                    // ActorComponent.SetForward).
                    if ((a.FieldMask & ActorFieldFacing) != 0)
                    {
                        w.Write(a.FacingX);
                        w.Write(a.FacingY);
                        w.Write(a.FacingZ);
                    }
                    // HEALTH (0x0020) — Feature D. Encoded AFTER statuses (0x0004), BEFORE bodypart-HP (0x0200),
                    // i.e. ascending bit order. Absolute current HP (float).
                    if ((a.FieldMask & ActorFieldHealth) != 0) w.Write(a.Health);
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
                    // AMMO (0x0400) — TS5 (a). Encoded AFTER bodypart-HP (0x0200), BEFORE faction (0x0800),
                    // i.e. ascending bit order. i32 count then per weapon [slotIndex:i32][charges:i32].
                    if ((a.FieldMask & ActorFieldAmmo) != 0)
                    {
                        var ammo = a.Ammo ?? new List<WeaponAmmo>();
                        w.Write(ammo.Count);
                        foreach (var wa in ammo)
                        {
                            w.Write(wa.SlotIndex);
                            w.Write(wa.Charges);
                        }
                    }
                    // FACTION (0x0800) — TS5 (b) is the highest bit we emit → encoded LAST (ascending bit order).
                    if ((a.FieldMask & ActorFieldFaction) != 0) w.Write(a.Faction);
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
                        // FACING (0x0010) — Inc2. Decoded AFTER position, BEFORE health (ascending bit order),
                        // only if its bit is set. 3×f32 = 12 bytes, guarded as a unit.
                        if ((rec.FieldMask & ActorFieldFacing) != 0)
                        {
                            if (ms.Length - ms.Position < 12) return false;
                            rec.FacingX = r.ReadSingle();
                            rec.FacingY = r.ReadSingle();
                            rec.FacingZ = r.ReadSingle();
                        }
                        // HEALTH (0x0020) — Feature D. Decoded AFTER statuses, BEFORE bodypart-HP (ascending
                        // bit order), only if its bit is set.
                        if ((rec.FieldMask & ActorFieldHealth) != 0)
                        {
                            if (ms.Length - ms.Position < 4) return false;
                            rec.Health = r.ReadSingle();
                        }
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
                        // AMMO (0x0400) — TS5 (a). Decoded AFTER bodypart-HP, BEFORE faction (ascending bit
                        // order), only if its bit is set. i32 count then per weapon 2×i32 = 8 bytes, guarded.
                        if ((rec.FieldMask & ActorFieldAmmo) != 0)
                        {
                            if (ms.Length - ms.Position < 4) return false;
                            int wc = r.ReadInt32();
                            if (wc < 0 || wc > ActorStateMaxWeaponsPerActor) return false;
                            if ((long)wc * 8 > ms.Length - ms.Position) return false;
                            for (int j = 0; j < wc; j++)
                            {
                                int slotIndex = r.ReadInt32();
                                int charges = r.ReadInt32();
                                rec.Ammo.Add(new WeaponAmmo(slotIndex, charges));
                            }
                        }
                        // FACTION (0x0800) — TS5 (b) decoded LAST (ascending bit order), only if its bit is set.
                        if ((rec.FieldMask & ActorFieldFaction) != 0)
                        {
                            if (ms.Length - ms.Position < 4) return false;
                            rec.Faction = r.ReadInt32();
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
}
