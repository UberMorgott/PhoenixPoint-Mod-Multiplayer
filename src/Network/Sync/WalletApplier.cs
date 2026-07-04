using System;
using System.Collections.Generic;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Reflection bridge to the player Wallet: snapshot all <c>ResourceType</c> slots (host) and
    /// apply a target snapshot as signed diffs (client). Mechanism A of the action-sync engine —
    /// host-authoritative currency echo. Converges ALL currency change call-sites to host truth.
    /// </summary>
    public static class WalletApplier
    {
        // The 12 vanilla ResourceType flag values (mod currency reuses these vanilla flags).
        // Verified against PhoenixPoint.Common.Core.ResourceType:
        // Supplies=1, Materials=2, Tech=4, AICore1=8, AICore2=0x10, AICore3=0x20, Research=0x40,
        // Production=0x80, Mutagen=0x100, LivingCrystals=0x200, Orichalcum=0x400, ProteanMutane=0x800.
        // Production (0x80) omitted: Wallet.Apply no-ops it (non-accumulating income, not a stored balance).
        private static readonly int[] Types = { 1, 2, 4, 8, 0x10, 0x20, 0x40, 0x100, 0x200, 0x400, 0x800 };

        public static List<(int type, float value)> Snapshot(GeoRuntime rt)
        {
            var wallet = rt?.Wallet();
            if (wallet == null) return null;
            var list = new List<(int, float)>(Types.Length);
            foreach (var t in Types)
                list.Add((t, WalletReflection.GetAmount(wallet, t)));
            return list;
        }

        public static void Apply(GeoRuntime rt, List<(int type, float value)> target)
        {
            var wallet = rt?.Wallet();
            if (wallet == null || target == null) return;
            foreach (var (t, v) in target)
            {
                float cur = WalletReflection.GetAmount(wallet, t);
                float diff = v - cur;
                if (Math.Abs(diff) > 0.0001f)
                    WalletReflection.ApplyDiff(wallet, t, diff);
            }
        }
    }
}
