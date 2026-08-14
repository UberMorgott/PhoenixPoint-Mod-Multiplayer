using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Events;
using PhoenixPoint.Geoscape.View.ViewModules;

namespace RailCheck
{
    /// <summary>
    /// L471 — THE OPEN MARKETPLACE REPAINT REACHES THE NATIVE LIST REBUILD, AND REACHES IT THE WAY THE GAME
    /// OPENS THE SHOP.
    ///
    /// Reported 2026-08-14: an open shop stayed stale after a rail push, and the log said so three times
    /// ("[MP][market] repaint of the open shop threw — screen kept: … ArgumentOutOfRangeException", host 2x,
    /// client 1x). The throw came from inside TFTV, not from us: <c>UIModuleTheMarketplace_UpdateList_patch</c>
    /// (TFTVVanillaFixes.cs:4754) replaces <c>UpdateList</c> wholesale and, above 7 offers, hands
    /// <c>VirtualScrollRect.InitVertical</c> a PHANTOM row — <c>count = MarketplaceChoices.Count + 1</c>
    /// (:4774-4777) — while its element callback still indexes <c>MarketplaceChoices[index]</c> (:4784).
    /// <c>VirtualScrollRect.SetElementsTransforms</c>:264-270 realises indices up to
    /// <c>_firstVisibleIndex + _visibleElements - 1</c>, and <c>GetTopLeftIndex</c>:258 clamps that top index to
    /// <c>_totalNumElements - _visibleElements</c>, so the last realised index is exactly <c>Count</c> — one past
    /// the end — for every scroll position that is not the top, and ALWAYS after a shrink (a purchase removed a
    /// row and the retained offset is clamped onto that last window).
    ///
    /// Native never trips it because <c>UIModuleTheMarketplace.ShowEncounter</c>:156-158 raises the private
    /// <c>_isInit</c> around its own <c>SetEncounter</c> call and <c>UpdateList</c>:197-200 then snaps the scroll
    /// back to the top. Our repaint invoked the private <c>SetEncounter</c> DIRECTLY and skipped the flag, so the
    /// precondition the scroll rect is entitled to never held. The catch around it made that a silent stale
    /// screen — a REACTIVITY defect (law 11), not a safe fallback: a peer's purchase is invisible to everyone
    /// whose shop is open until they leave the screen and come back.
    ///
    /// So this law asserts the REPAINT ITSELF, not the presence of a catch: the module's own rebuild is reached
    /// by a resolved handle, and the opening flag that resets the scroll is reached with it.
    ///
    /// Falsify: drop <c>MarketplaceSync.ModuleIsInit</c> or point it elsewhere → <c>scroll-reset-gone</c>; delete
    /// the flag raise from <c>RebuildOpenShopList</c> → <c>scroll-reset-unreached</c> (the ArgumentOutOfRange comes
    /// straight back); unbind <c>ModuleSetEncounter</c> → <c>list-rebuild-gone</c>; drop its Invoke →
    /// <c>list-rebuild-unreached</c> (a repaint that repaints nothing); stop calling the rebuild →
    /// <c>rebuild-detached</c>. Rename the native field or method → <c>premise-changed</c>.
    /// </summary>
    internal static class L471_TheOpenShopRepaintReachesTheNativeList
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var sync = typeof(MarketplaceSync);
            var repaint = sync.GetMethod("RepaintOpenMarketplace", All);
            // The rebuild body is asserted SEPARATELY from its caller on purpose: the caller's null-guard
            // reads both handles, so a reachability check there would stay green with the reset deleted.
            var rebuild = sync.GetMethod("RebuildOpenShopList", All);
            if (repaint == null || rebuild == null)
            {
                yield return "L471 seam-gone: MarketplaceSync." +
                             (repaint == null ? "RepaintOpenMarketplace" : "RebuildOpenShopList") +
                             " does not resolve — an open shop is repainted by nothing and every peer's offer list " +
                             "goes stale on the first push";
                yield break;
            }
            if (!Program.Callees(repaint, sync.Assembly).Any(c => c.MetadataToken == rebuild.MetadataToken &&
                                                                  c.Module == rebuild.Module))
                yield return "L471 rebuild-detached: RepaintOpenMarketplace no longer calls RebuildOpenShopList — " +
                             "the seam the rail drives is wired to nothing and the open shop keeps its stale rows";

            // (a) THE NATIVE LIST REBUILD. Private + reflection-invoked, so the cached handle IS the assertable
            // identity — an IL callee walk can never see through an Invoke.
            var setEncounter = typeof(UIModuleTheMarketplace).GetMethod(
                "SetEncounter", All, null, new[] { typeof(GeoscapeEvent) }, null);
            if (setEncounter == null)
            {
                yield return "L471 premise-changed: UIModuleTheMarketplace.SetEncounter(GeoscapeEvent) no longer " +
                             "resolves — the module's own rebuild (→ UpdateList → VirtualScrollRect.InitVertical) is " +
                             "gone, and a guessed replacement is exactly what leaves the shop stale";
                yield break;
            }
            var rebuildHandle = sync.GetField("ModuleSetEncounter", All);
            object rebuildBound = null;
            try { rebuildBound = rebuildHandle?.GetValue(null); } catch { }
            if (rebuildHandle == null || !(rebuildBound is MethodBase rb) ||
                rb.MetadataToken != setEncounter.MetadataToken || rb.Module != setEncounter.Module)
                yield return "L471 list-rebuild-gone: MarketplaceSync.ModuleSetEncounter is " +
                             (rebuildHandle == null ? "gone" : rebuildBound == null ? "NULL" :
                              "bound to " + ((MethodBase)rebuildBound).Name) +
                             " instead of UIModuleTheMarketplace.SetEncounter(GeoscapeEvent) — the repaint drives no " +
                             "list rebuild at all, and drives none silently";
            else if (!L92_DerivedGeoWidgets.ReadsField(rebuild, rebuildHandle, 1))
                yield return "L471 list-rebuild-unreached: RebuildOpenShopList never reads " +
                             "MarketplaceSync.ModuleSetEncounter — the open shop's rows are never rebuilt from the " +
                             "mirrored model, so a peer's purchase or reroll stays invisible until the screen is reopened";

            // (b) THE OPENING FLAG, which IS the scroll reset. Without it the repaint hands TFTV's phantom row a
            // retained scroll offset and throws ArgumentOutOfRange before a single row is redrawn.
            var isInit = typeof(UIModuleTheMarketplace).GetField("_isInit", All);
            if (isInit == null)
            {
                yield return "L471 premise-changed: UIModuleTheMarketplace._isInit is gone — the flag ShowEncounter" +
                             ":156-158 raises to make UpdateList:197-200 snap the scroll to the top no longer exists, " +
                             "so 'repaint the shop the way the game opens it' is unenforceable";
                yield break;
            }
            var initHandle = sync.GetField("ModuleIsInit", All);
            object initBound = null;
            try { initBound = initHandle?.GetValue(null); } catch { }
            if (initHandle == null || !(initBound is FieldInfo ib) ||
                ib.MetadataToken != isInit.MetadataToken || ib.Module != isInit.Module)
                yield return "L471 scroll-reset-gone: MarketplaceSync.ModuleIsInit is " +
                             (initHandle == null ? "gone" : initBound == null ? "NULL" :
                              "bound to " + ((FieldInfo)initBound).Name) +
                             " instead of UIModuleTheMarketplace._isInit — the repaint rebuilds the list without " +
                             "resetting the scroll, and TFTV's phantom +1 row (TFTVVanillaFixes.cs:4774-4784) then " +
                             "indexes MarketplaceChoices one past the end on any shop that is scrolled or shrank";
            else if (!L92_DerivedGeoWidgets.ReadsField(rebuild, initHandle, 1))
                yield return "L471 scroll-reset-unreached: RebuildOpenShopList never reads " +
                             "MarketplaceSync.ModuleIsInit — the native opening invariant is not restored around the " +
                             "rebuild, the ArgumentOutOfRangeException of 2026-08-14 comes straight back, and the catch " +
                             "turns it into a silently stale shop";
        }
    }
}
