using System;
using System.IO;

namespace Multipleer.Serialization
{
    public static class NetworkSerializer
    {
        // ─── TacticalAbilityTarget serialization ──────────────────────────
        // Serializes the target data for a tactical action so it can be
        // reconstructed on the host for validation + execution.

        public static byte[] SerializeTargetData(
            int targetActorGeoId,
            float posX, float posY, float posZ,
            byte attackType,
            string followupAbilityDefId = null)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(targetActorGeoId);
                bw.Write(posX);
                bw.Write(posY);
                bw.Write(posZ);
                bw.Write(attackType);
                bw.Write(followupAbilityDefId ?? "");
                return ms.ToArray();
            }
        }

        public static TacticalTargetData DeserializeTargetData(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                return new TacticalTargetData
                {
                    TargetActorGeoId = br.ReadInt32(),
                    PositionX = br.ReadSingle(),
                    PositionY = br.ReadSingle(),
                    PositionZ = br.ReadSingle(),
                    AttackType = br.ReadByte(),
                    FollowupAbilityDefId = br.ReadString()
                };
            }
        }

        // ─── ActionResult serialization ───────────────────────────────────

        public static byte[] SerializeActionResult(
            bool success, float apRemaining, float wpRemaining,
            int[] damagedTargets = null, float[] damageValues = null)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(success);
                bw.Write(apRemaining);
                bw.Write(wpRemaining);

                var targetCount = damagedTargets?.Length ?? 0;
                bw.Write(targetCount);
                for (int i = 0; i < targetCount; i++)
                {
                    bw.Write(damagedTargets[i]);
                    bw.Write(damageValues?[i] ?? 0f);
                }

                return ms.ToArray();
            }
        }

        public static ActionResultData DeserializeActionResult(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                var result = new ActionResultData
                {
                    Success = br.ReadBoolean(),
                    ApRemaining = br.ReadSingle(),
                    WpRemaining = br.ReadSingle()
                };

                var targetCount = br.ReadInt32();
                result.DamagedTargets = new int[targetCount];
                result.DamageValues = new float[targetCount];
                for (int i = 0; i < targetCount; i++)
                {
                    result.DamagedTargets[i] = br.ReadInt32();
                    result.DamageValues[i] = br.ReadSingle();
                }

                return result;
            }
        }

        // ─── Move Result ──────────────────────────────────────────────────

        public static byte[] SerializeMoveResult(
            float finalX, float finalY, float finalZ,
            float apRemaining)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(finalX);
                bw.Write(finalY);
                bw.Write(finalZ);
                bw.Write(apRemaining);
                return ms.ToArray();
            }
        }

        public static MoveResultData DeserializeMoveResult(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                return new MoveResultData
                {
                    FinalX = br.ReadSingle(),
                    FinalY = br.ReadSingle(),
                    FinalZ = br.ReadSingle(),
                    ApRemaining = br.ReadSingle()
                };
            }
        }
    }

    // ─── Data Transfer Types ─────────────────────────────────────────────

    public class TacticalTargetData
    {
        public int TargetActorGeoId { get; set; }
        public float PositionX { get; set; }
        public float PositionY { get; set; }
        public float PositionZ { get; set; }
        public byte AttackType { get; set; }
        public string FollowupAbilityDefId { get; set; }
    }

    public class ActionResultData
    {
        public bool Success { get; set; }
        public float ApRemaining { get; set; }
        public float WpRemaining { get; set; }
        public int[] DamagedTargets { get; set; }
        public float[] DamageValues { get; set; }
    }

    public class MoveResultData
    {
        public float FinalX { get; set; }
        public float FinalY { get; set; }
        public float FinalZ { get; set; }
        public float ApRemaining { get; set; }
    }
}
