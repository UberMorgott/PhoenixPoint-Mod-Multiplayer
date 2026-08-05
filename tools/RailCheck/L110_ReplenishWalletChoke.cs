using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Geoscape.View.ViewModules;

namespace RailCheck
{
    /// <summary>
    /// L110 — NO SPEND ON THE RESUPPLY SCREEN MAY ESCAPE THE RAIL, AND THE SEAM MUST SIT WHERE EVERY CALLER
    /// PASSES.
    ///
    /// The reload capture sat on <c>UIModuleReplenish.SingleItemReloadAndRefresh</c>:224 — the ROW-CLICK
    /// wrapper — because only that method still knew which soldier. The OK button's <c>ReplenishAll</c>:288
    /// calls <c>SingleItemReload</c>:234 DIRECTLY, one level below the seam, so on a client it ran
    /// <c>_faction.Wallet.Take(cost, Purchase)</c> out of the SHARED wallet plus a local
    /// <c>ModifyCharges</c> for every un-full magazine in the returning squad. Nothing logged it: host state
    /// never changed, so the diff rail had nothing to ship and no delta could ever correct the divergence —
    /// the same silent-money shape <c>GeoCharacter.RepairItem</c> had, through a second door.
    ///
    /// WHAT IS ASSERTED IS THE PROPERTY, not the patch:
    ///   (a) <c>Wallet.Take</c> is reachable from exactly ONE method of <c>UIModuleReplenish</c>, and
    ///   (b) that method is the one the capture patches.
    /// Together they say "a client cannot spend the shared wallet from this screen without the rail seeing
    /// it", and they fail in BOTH directions that matter: moving the seam back up to a wrapper breaks (b),
    /// and the game growing a second spender (a patch, a DLC, a mod) breaks (a) instead of quietly
    /// reopening the leak.
    ///   (c) the two callers are still the two callers — if <c>ReplenishAll</c> stops routing through the
    ///       captured method, (a)+(b) stay green while the OK button leaks again;
    ///   (d) the seam BLOCKS and SENDS: a prefix that returns void cannot stop the native spend, and one
    ///       that stops it without emitting an intent leaves the player's click doing nothing anywhere
    ///       (law 91) — both halves, or neither is a fix.
    /// ponytail: (a) and (c) walk IL with Program's own walker; a callee this harness cannot parse
    /// under-reports rather than inventing an edge, so the failure mode is a missed red, never a false one.
    /// </summary>
    internal static class L110_ReplenishWalletChoke
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var module = typeof(UIModuleReplenish);
            var take = typeof(Wallet).GetMethods(All).FirstOrDefault(m => m.Name == "Take");
            var choke = module.GetMethod("SingleItemReload", All);
            var wrapper = module.GetMethod("SingleItemReloadAndRefresh", All);
            var batch = module.GetMethod("ReplenishAll", All);
            var prefix = typeof(ReplenishSync.ReloadCapturePatch).GetMethod("Prefix", All);
            var patch = typeof(ReplenishSync.ReloadCapturePatch)
                .GetCustomAttributes(typeof(HarmonyPatch), inherit: false)
                .Cast<HarmonyPatch>().Select(a => a.info).FirstOrDefault();
            if (take == null || choke == null || wrapper == null || batch == null || prefix == null)
            {
                yield return "L110 premise-changed: Wallet.Take / UIModuleReplenish.SingleItemReload | " +
                             "SingleItemReloadAndRefresh | ReplenishAll, or ReplenishSync.ReloadCapturePatch" +
                             ".Prefix, no longer resolves. The resupply screen's spend path has moved and this " +
                             "law is asserting something about a shape the game no longer has — re-read it " +
                             "before assuming the capture still covers anything.";
                yield break;
            }

            // ── (a) exactly one spender on this screen ─────────────────────
            var spenders = module.GetMethods(All)
                .Where(m => !m.IsAbstract && m.DeclaringType == module)
                // By DECLARING TYPE + NAME, not by token: Wallet.Take is overloaded and "which overload"
                // is not the question — any Take is a spend.
                .Where(m => Program.Callees(m, typeof(Wallet).Assembly)
                                   .Any(c => c.DeclaringType == typeof(Wallet) && c.Name == "Take"))
                .Select(m => m.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (spenders.Count != 1 || spenders[0] != "SingleItemReload")
                yield return "L110 second-spender: Wallet.Take is called from [" + string.Join(",", spenders) +
                             "] in UIModuleReplenish, not from SingleItemReload alone. Every extra one is a " +
                             "client-side spend of the SHARED wallet that no delta can correct — host state " +
                             "never changed, so nothing is ever shipped to put it back.";

            // ── (b) the capture is ON that method ──────────────────────────
            if (patch == null || patch.declaringType != module || patch.methodName != "SingleItemReload")
                yield return "L110 seam-off-the-choke: ReplenishSync.ReloadCapturePatch patches " +
                             (patch?.declaringType?.Name ?? "nothing") + "." + (patch?.methodName ?? "?") +
                             " instead of UIModuleReplenish.SingleItemReload. A seam on a WRAPPER covers the " +
                             "caller it wraps and no other — which is precisely how ReplenishAll (the OK " +
                             "button) spent the shared wallet locally on every un-full magazine at once.";

            // ── (c) both callers still route through it ────────────────────
            foreach (var caller in new[] { wrapper, batch })
                if (!Program.Callees(caller, module.Assembly).Any(c => c.MetadataToken == choke.MetadataToken &&
                                                                       c.Module == choke.Module))
                    yield return "L110 caller-bypasses-choke: UIModuleReplenish." + caller.Name + " no longer " +
                                 "calls SingleItemReload, so it reaches the spend some other way and the capture " +
                                 "does not see it. That is the original bug with the roles swapped: the seam is " +
                                 "in the right place and one caller walked around it.";

            // ── (d) it blocks, and it sends ────────────────────────────────
            if (prefix.ReturnType != typeof(bool))
                yield return "L110 seam-cannot-block: ReloadCapturePatch.Prefix does not return bool, so it " +
                             "cannot stop the native body. The client would emit the intent AND spend the wallet " +
                             "locally — the divergence doubled rather than fixed.";
            if (!Program.Callees(prefix, typeof(IntentRail).Assembly)
                        .Any(c => c.DeclaringType == typeof(IntentRail) && c.Name == "Send"))
                yield return "L110 seam-silent: ReloadCapturePatch.Prefix does not call IntentRail.Send. A block " +
                             "with no intent is a reload button that does nothing for anybody — law 91 — and it " +
                             "looks exactly like a working screen.";
            if (!Program.Callees(prefix, typeof(IntentRail).Assembly)
                        .Any(c => c.DeclaringType == typeof(IntentRail) && c.Name == "ShouldRunNative"))
                yield return "L110 seam-unconditional: ReloadCapturePatch.Prefix does not ask " +
                             "IntentRail.ShouldRunNative, so it blocks on the HOST and in SOLO too — the one " +
                             "peer allowed to run the game's own reload would stop doing it.";
        }
    }
}
