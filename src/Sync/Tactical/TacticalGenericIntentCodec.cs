using System.IO;
using System.Text;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// PURE (engine-free) wire codec for the GENERIC ability-intent surface <c>tac.intent.generic</c>
    /// (0x8E, client→host — spec TS2). Where the 0x87 <c>tac.intent.ability</c> is the shoot/melee
    /// DAMAGE-DEALER path (limb-snap-tuned, UNTOUCHED), 0x8E is the RICHER generic intent for OWN-soldier
    /// abilities beyond shoot/bash (heal, recover-will, rally, psychic scream, …). It carries a target-KIND
    /// discriminator so ONE surface expresses every non-damage ability's target shape:
    ///
    ///   [actorNetId:i32][abilityDefGuid:str][targetKind:u8][payloadLen:u16][payload:N][nonce:u32]
    ///
    /// <c>targetKind</c> (see the <c>Kind*</c> consts):
    ///   0 none/self    — no payload
    ///   1 actor        — [targetNetId:i32]                (heal / rally / frenzy / mind-control target)
    ///   2 pos          — [tx:f32][ty:f32][tz:f32]         (deploy-turret / throw-turret cell)
    ///   3 equipmentSlot— [slotIndex:i32]                  (reload / ammo-clip — TS5 ammo surface)
    ///   4 object       — [objectNetId:i32]                (interact / crate / drop — TS5 loot registry)
    ///
    /// BACKWARD-TOLERANT: the payload is LENGTH-PREFIXED (u16) — an older/newer peer that does not know a
    /// <c>targetKind</c> reads <c>payloadLen</c>, SKIPS exactly that many bytes, then reads the trailing nonce
    /// (recLen-skip discipline, spec §1 "backward-tolerant wire"). Parsing a known kind reads its fields from
    /// the payload sub-buffer, so a future kind that grows the SAME payload (extra tail bytes) still decodes
    /// its known prefix (forward-tolerant). Truncation / a payload shorter than a known kind needs → clean
    /// <c>false</c> (no partial accept), exactly like <see cref="TacticalDeployCodec"/> /
    /// <see cref="TacticalActorLifecycleCodec"/> — the reliable transport guarantees full delivery.
    /// </summary>
    public static class TacticalGenericIntentCodec
    {
        /// <summary>Target-kind discriminator carried in the 0x8E wire (u8).</summary>
        public const byte KindNone   = 0;   // none / self — no payload
        public const byte KindActor  = 1;   // [targetNetId:i32]
        public const byte KindPos    = 2;   // [tx:f32][ty:f32][tz:f32]
        public const byte KindSlot   = 3;   // [slotIndex:i32] — equipment slot (reload)
        public const byte KindObject = 4;   // [objectNetId:i32] — ground object (interact / crate)

        /// <summary>Sentinel returned by the pure ability→kind map for an ability it does not know (never
        /// written to the wire; the client degrades-to-notify rather than sending an unresolvable intent).</summary>
        public const byte KindUnknown = 255;

        /// <summary>Sentinel netId when a kind carries no actor/object (none / pos / slot).</summary>
        public const int TargetNetIdNone = -1;
        /// <summary>Sentinel slot index when a kind carries no equipment slot.</summary>
        public const int SlotIndexNone = -1;

        public struct GenericIntent
        {
            public int ActorNetId;           // the CASTER (own soldier) net id
            public string AbilityDefGuid;
            public byte TargetKind;
            public int TargetNetId;          // Actor / Object kinds (else TargetNetIdNone)
            public float TX, TY, TZ;         // Pos kind (else 0)
            public int SlotIndex;            // Slot kind (else SlotIndexNone)
            public uint Nonce;

            public GenericIntent(int actorNetId, string abilityDefGuid, byte targetKind,
                int targetNetId, float tx, float ty, float tz, int slotIndex, uint nonce)
            {
                ActorNetId = actorNetId; AbilityDefGuid = abilityDefGuid ?? "";
                TargetKind = targetKind; TargetNetId = targetNetId;
                TX = tx; TY = ty; TZ = tz; SlotIndex = slotIndex; Nonce = nonce;
            }
        }

        // ─── Kind-specific factory helpers (keep call sites intent-clear) ─────────────────
        public static GenericIntent None(int actorNetId, string guid, uint nonce)
            => new GenericIntent(actorNetId, guid, KindNone, TargetNetIdNone, 0f, 0f, 0f, SlotIndexNone, nonce);
        public static GenericIntent Actor(int actorNetId, string guid, int targetNetId, uint nonce)
            => new GenericIntent(actorNetId, guid, KindActor, targetNetId, 0f, 0f, 0f, SlotIndexNone, nonce);
        public static GenericIntent Pos(int actorNetId, string guid, float tx, float ty, float tz, uint nonce)
            => new GenericIntent(actorNetId, guid, KindPos, TargetNetIdNone, tx, ty, tz, SlotIndexNone, nonce);
        public static GenericIntent Slot(int actorNetId, string guid, int slotIndex, uint nonce)
            => new GenericIntent(actorNetId, guid, KindSlot, TargetNetIdNone, 0f, 0f, 0f, slotIndex, nonce);
        public static GenericIntent Object(int actorNetId, string guid, int objectNetId, uint nonce)
            => new GenericIntent(actorNetId, guid, KindObject, objectNetId, 0f, 0f, 0f, SlotIndexNone, nonce);

        public static byte[] Encode(GenericIntent i)
        {
            byte[] payload = BuildPayload(i);
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(i.ActorNetId);
                w.Write(i.AbilityDefGuid ?? "");
                w.Write(i.TargetKind);
                w.Write((ushort)payload.Length);
                if (payload.Length > 0) w.Write(payload);
                w.Write(i.Nonce);
                return ms.ToArray();
            }
        }

        private static byte[] BuildPayload(GenericIntent i)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                switch (i.TargetKind)
                {
                    case KindActor:  w.Write(i.TargetNetId); break;
                    case KindObject: w.Write(i.TargetNetId); break;
                    case KindPos:    w.Write(i.TX); w.Write(i.TY); w.Write(i.TZ); break;
                    case KindSlot:   w.Write(i.SlotIndex); break;
                    case KindNone:   break;   // no payload
                    default:         break;   // unknown kind on ENCODE: no payload (never happens — encode-side kinds are known)
                }
                return ms.ToArray();
            }
        }

        /// <summary>Decode a 0x8E generic intent. Returns false (no partial accept) on truncation or a payload
        /// too short for a KNOWN kind. An UNKNOWN kind still decodes cleanly (kind byte preserved, no target
        /// fields, nonce read after skipping the length-prefixed payload) so the host degrades-to-notify.</summary>
        public static bool TryDecode(byte[] data, out GenericIntent intent)
        {
            intent = default(GenericIntent);
            // Minimum: i32 actor + ≥1-byte length-prefixed string + u8 kind + u16 payloadLen + u32 nonce.
            if (data == null || data.Length < 4 + 1 + 1 + 2 + 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    int actorNetId = r.ReadInt32();
                    string guid = r.ReadString();
                    byte kind = r.ReadByte();
                    int payloadLen = r.ReadUInt16();
                    if (payloadLen < 0 || ms.Length - ms.Position < (long)payloadLen + 4) return false;  // +4 = trailing nonce
                    byte[] payload = payloadLen > 0 ? r.ReadBytes(payloadLen) : new byte[0];
                    if (payload.Length != payloadLen) return false;

                    int targetNetId = TargetNetIdNone;
                    float tx = 0f, ty = 0f, tz = 0f;
                    int slotIndex = SlotIndexNone;
                    if (!ParsePayload(kind, payload, ref targetNetId, ref tx, ref ty, ref tz, ref slotIndex))
                        return false;   // a KNOWN kind whose payload was too short → garbage → clean drop

                    uint nonce = r.ReadUInt32();
                    intent = new GenericIntent(actorNetId, guid, kind, targetNetId, tx, ty, tz, slotIndex, nonce);
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>Parse the known target fields from the length-prefixed payload sub-buffer. Extra tail bytes
        /// (a future-extended same-kind) are ignored (forward-tolerant); an UNKNOWN kind leaves all defaults and
        /// succeeds (skip). Returns false only when a KNOWN kind's payload is shorter than its fixed fields.</summary>
        private static bool ParsePayload(byte kind, byte[] payload,
            ref int targetNetId, ref float tx, ref float ty, ref float tz, ref int slotIndex)
        {
            switch (kind)
            {
                case KindNone:   return true;   // no fields
                case KindActor:
                case KindObject:
                    if (payload.Length < 4) return false;
                    targetNetId = System.BitConverter.ToInt32(payload, 0);
                    return true;
                case KindPos:
                    if (payload.Length < 12) return false;
                    tx = System.BitConverter.ToSingle(payload, 0);
                    ty = System.BitConverter.ToSingle(payload, 4);
                    tz = System.BitConverter.ToSingle(payload, 8);
                    return true;
                case KindSlot:
                    if (payload.Length < 4) return false;
                    slotIndex = System.BitConverter.ToInt32(payload, 0);
                    return true;
                default:
                    return true;   // UNKNOWN kind → skip payload, host degrades-to-notify (never crash)
            }
        }
    }
}
