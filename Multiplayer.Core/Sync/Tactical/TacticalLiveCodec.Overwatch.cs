using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Sync.Tactical
{
    public static partial class TacticalLiveCodec
    {
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
    }
}
