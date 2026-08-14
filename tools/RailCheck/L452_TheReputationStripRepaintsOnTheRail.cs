using System;
using System.Collections.Generic;
using System.Reflection;
using Base.UI;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.View.ViewModules;

namespace RailCheck
{
    /// <summary>
    /// L452 — THE TOP-RIGHT RESOURCE/REPUTATION STRIP IS REPAINTED BY THE RAIL, NOT BY THE GAME.
    /// Reported 2026-08-14: the geoscape faction reputation percentages disagreed between peers. The STATE
    /// was never wrong — <c>FactionDiplomacyState.Relation</c> is the same instance the native
    /// <c>PartyDiplomacy._relations</c> holds, and it reaches every peer through
    /// <c>_factionsDiplomacyState</c>; what was wrong was PIXELS. TFTV's <c>TopInforBar</c>:127 writes the
    /// Anu/Nj/Syn numbers from a POSTFIX on <c>UIModuleInfoBar.UpdatePopulation</c>, and that method runs
    /// only from <c>Init</c>/<c>SetPopulationVisibility</c>:185 or off the <c>_populationChaged</c> flag
    /// (:243-247) that <c>Level.OnWorldPopulationChanged</c> sets. Diplomacy fires none of it, and the rail
    /// writes the stored field DIRECTLY, so on a client no native event fires at all — the strip froze at
    /// its last <c>Init</c> until the player left the screen and came back.
    ///
    /// Same structural gap L92 names, third citizen: <c>UIModuleInfoBar</c> is a MODULE, so no
    /// <c>UiNativeRepaint.Table</c> row (keyed by view-state TYPE) can ever reach it. The fix is therefore
    /// the same shape and the same place — one more block in <c>RefreshPersistentHud</c>, reached BY HANDLE
    /// off <c>GeoscapeModulesData.ResourcesModule</c>.
    ///
    /// Falsify: delete the info-bar block from RefreshPersistentHud → <c>strip-unreached</c>; drop the
    /// cached native handle → <c>repaint-handle-gone</c>; drop the <c>_context</c> guard → <c>guard-dead</c>
    /// (the module NREs on an un-Init'd <c>_context.View</c>, :278).
    /// </summary>
    internal static class L452_TheReputationStripRepaintsOnTheRail
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var repaint = typeof(OpenUiRepaint);
            var refresh = repaint.GetMethod("RefreshPersistentHud", All);
            if (refresh == null)
            {
                yield return "L452 seam-gone: OpenUiRepaint.RefreshPersistentHud does not resolve — the module half " +
                             "of the repaint has no entry point and the reputation strip is repainted by nothing";
                yield break;
            }

            // (a) THE HANDLE the strip is reached through — never a scene scan (L92 owns that ban).
            var barField = typeof(GeoscapeModulesData).GetField("ResourcesModule", All);
            if (barField == null || barField.FieldType != typeof(UIModuleInfoBar))
            {
                yield return "L452 module-handle-gone: GeoscapeModulesData.ResourcesModule is not the UIModuleInfoBar " +
                             "field this seam reads (" + (barField == null ? "missing" : barField.FieldType.Name) +
                             ") — the strip can no longer be reached while its GameObject is switched off";
                yield break;
            }
            // The IL walk is L92's — same seam, same under-reporting reach; a second copy would be a second
            // thing to keep true.
            if (!L92_DerivedGeoWidgets.ReadsField(refresh, barField, 3))
                yield return "L452 strip-unreached: RefreshPersistentHud never reads " +
                             "GeoscapeModulesData.ResourcesModule — nothing repaints the top-right resource/reputation " +
                             "strip on a peer that did not perform the gesture, so the faction percentages keep " +
                             "disagreeing between peers until someone re-enters the screen";

            // (b) THE NATIVE REPAINT, resolved not guessed. Private + no-arg: an IL callee walk can never
            // see a reflection Invoke, so the cached handle IS the assertable identity.
            var updatePopulation = typeof(UIModuleInfoBar).GetMethod("UpdatePopulation", All, null, Type.EmptyTypes, null);
            if (updatePopulation == null)
            {
                yield return "L452 native-repaint-gone: UIModuleInfoBar.UpdatePopulation() no longer resolves — the " +
                             "method TFTV's TopInforBar postfixes to write the reputation percentages is gone, and a " +
                             "guessed replacement is what has broken this repaint before";
                yield break;
            }
            var handle = repaint.GetField("InfoBarUpdatePopulation", All);
            object bound = null;
            try { bound = handle?.GetValue(null); } catch { }
            if (handle == null || !(bound is MethodBase m) ||
                m.MetadataToken != updatePopulation.MetadataToken || m.Module != updatePopulation.Module)
                yield return "L452 repaint-handle-gone: OpenUiRepaint.InfoBarUpdatePopulation is " +
                             (handle == null ? "gone" : bound == null ? "NULL" : "bound to " + ((MethodBase)bound).Name) +
                             " instead of UIModuleInfoBar.UpdatePopulation() — the strip refresh drives nothing, and it " +
                             "drives nothing silently";

            // (c) THE GUARD that keeps an un-Init'd module from NRE-ing must exist AND still be needed:
            // UpdatePopulation:278 dereferences _context.View on its very first line.
            var ctxField = typeof(UIModuleInfoBar).GetField("_context", All);
            var ctxHandle = repaint.GetField("InfoBarContext", All);
            object ctxBound = null;
            try { ctxBound = ctxHandle?.GetValue(null); } catch { }
            if (ctxField == null)
                yield return "L452 premise-changed: UIModuleInfoBar._context is gone — the field the refresh gates on " +
                             "no longer exists, so 'never repaint a module that was not Init'd' is unenforceable";
            else if (ctxHandle == null || ctxBound == null)
                yield return "L452 guard-dead: OpenUiRepaint.InfoBarContext is " +
                             (ctxHandle == null ? "gone" : "NULL") + " — either the refresh now NREs on an un-Init'd " +
                             "module, or the guard refuses EVERY repaint instead, and refuses it without a word";
        }
    }
}
