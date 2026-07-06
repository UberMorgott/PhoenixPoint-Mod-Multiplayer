using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Sync.Tactical
{
    public static partial class TacticalLiveCodec
    {
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
    }
}
