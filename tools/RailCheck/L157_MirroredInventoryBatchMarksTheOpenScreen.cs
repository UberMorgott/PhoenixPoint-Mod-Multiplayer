using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using Multiplayer.Tactical;

namespace RailCheck
{
    /// <summary>
    /// L157 — A MIRRORED TACTICAL INVENTORY BATCH MARKS THE OPEN CONTAINER SCREEN, AND THE REPAINT IT ASKS
    /// FOR NEVER RE-COMMITS ON THE PEER THAT IS ONLY WATCHING.
    ///
    /// THE REPORT (2026-08-07, in-mission — NOT the geoscape screen L156 covers). Two peers have the same
    /// soldier's TACTICAL inventory open. One moves the rifle out of an equipment slot and a medkit in from
    /// the backpack; the other "sees NOTHING until he closes and re-opens the inventory". Second face of the
    /// same report: two soldiers standing adjacent, so one screen shows the NEIGHBOUR's inventory too for
    /// trading, and a change on one peer does not appear on the other's open panel either.
    ///
    /// WHAT L116 ALREADY HOLDS, AND WHAT IT DOES NOT. L116 asserts the SHAPE of the rebuild —
    /// <c>RepaintContainerView</c> drives <c>UpdateData</c>:626 AND <c>UpdateSecondaryData</c>:767 (so the
    /// neighbour panel is redrawn, not just the primary), is reached from the flush, defers a held drag, and
    /// keeps <c>UIStateInventory</c> out of <c>AbilityBarStates</c>. Every one of those arms describes what
    /// happens AFTER something asks for a repaint. NOT ONE OF THEM ASSERTS THAT ANYTHING EVER ASKS. Move
    /// <c>TacticalUiRepaint.MarkDirty</c> out of <c>ApplyLayout</c> — or behind the <c>moved &gt; 0</c> test it
    /// is deliberately NOT behind — and L116 stays green in full while the flush never fires, the model holds
    /// the new kit, the panel keeps painting the old one, and the player is told to close and re-open. That is
    /// the reported symptom exactly, and it is the load-bearing link L116 leaves unpinned. Arms (b) and (c)
    /// below are that link, taken at BOTH apply paths: the peer receiving a settle
    /// (<c>ApplyInventory</c>) and the HOST applying a client's intent (<c>HandleInventoryIntent</c>), which
    /// goes stale in exactly the same way because a client's batch executes on the host with no host-side view
    /// transition at all.
    ///
    /// ARM (d) IS THE RE-ENTRANCY THE FIX CLOSED, and it is a P3/L67 question rather than a cosmetic one.
    /// <c>RepaintContainerView</c> finishes its rebuild by invoking <c>UIStateInventory.RefreshUI</c> — the
    /// game's own both-panel precedent (<c>UndoInventoryActions</c>:905-918) ends the same way. But
    /// <c>InventoryGestureSeam</c> POSTFIXES that very method: it is how a drag becomes a shipped batch
    /// (<c>RefreshUI</c> is the game's <c>OnItemsSwapped</c> handler, subscribed at
    /// <c>UIStateInventory.EnterState</c>:332). So the mirrored repaint re-enters the local commit path, and
    /// <c>FlushGesture</c> runs <c>AttemptMoveItems</c> + <c>ApplyInventoryActions</c> →
    /// <c>InventoryQuery.SyncItems</c> → real <c>InventoryComponent.AddItem/RemoveItem</c> on a peer that is
    /// only MIRRORING. Nothing crosses the wire (<c>OnBatchCommitting</c> checks the same scope), so it is a
    /// model write on a non-authoritative peer with not one line to show for it — this repo's dominant bug
    /// shape — and <c>SyncItems</c>:61 additionally <c>Reset</c>s every query, discarding whatever that player
    /// had staged and not yet confirmed.
    ///
    /// ARM (e) IS THE POSITIVE CONTROL, and it is what stops (d) becoming a rule about nothing. The guard in
    /// <c>FlushGesture</c> only earns its keep while the loop is REAL, so this asserts the loop rather than
    /// assuming it: the method <c>RepaintContainerView</c> invokes through its <c>InvRefreshUi</c> handle and
    /// the method <c>InventoryGestureSeam.TargetMethod()</c> patches must be THE SAME
    /// <c>UIStateInventory.RefreshUI</c>. Both are read at runtime, not matched by name in IL, because the
    /// repaint reaches the game method by reflection and no IL walk can see it.
    ///
    /// Falsify: drop <c>MarkDirty</c> from <c>ApplyLayout</c> → <c>apply-unmarked</c>; make either apply entry
    /// point stop calling <c>ApplyLayout</c> → <c>apply-path-unmarked</c>; delete the
    /// <c>SyncApplyScope.Active</c> line from <c>FlushGesture</c> → <c>mirror-recommits</c>; point
    /// <c>InvRefreshUi</c> or the seam's <c>TargetMethod</c> at anything else → <c>POSITIVE CONTROL</c>;
    /// rename any of the six members → <c>premise-changed</c>.
    /// </summary>
    internal static class L157_MirroredInventoryBatchMarksTheOpenScreen
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var sync = typeof(TacticalInventorySync);
            var repaint = typeof(TacticalUiRepaint);
            var mod = sync.Assembly;

            var applyLayout = sync.GetMethod("ApplyLayout", All);
            var applyInventory = sync.GetMethod("ApplyInventory", All);
            var handleIntent = sync.GetMethod("HandleInventoryIntent", All);
            var flushGesture = sync.GetMethod("FlushGesture", All);
            var markDirty = repaint.GetMethod("MarkDirty", All);
            var container = repaint.GetMethod("RepaintContainerView", All);
            var active = typeof(SyncApplyScope).GetProperty("Active", All)?.GetGetMethod();
            var seam = mod.GetType("Multiplayer.Tactical.InventoryGestureSeam");
            var targetMethod = seam?.GetMethod("TargetMethod", All);
            var refreshHandle = repaint.GetField("InvRefreshUi", All);

            if (applyLayout == null || applyInventory == null || handleIntent == null || flushGesture == null ||
                markDirty == null || container == null || active == null || targetMethod == null ||
                refreshHandle == null)
            {
                yield return "L157 premise-changed: one of TacticalInventorySync.{ApplyLayout,ApplyInventory," +
                             "HandleInventoryIntent,FlushGesture}, TacticalUiRepaint.{MarkDirty," +
                             "RepaintContainerView,InvRefreshUi}, SyncApplyScope.Active or " +
                             "InventoryGestureSeam.TargetMethod no longer resolves. The tactical inventory arc " +
                             "has been reshaped and this law is asserting something about a shape it no longer " +
                             "has — re-read it before trusting either the repaint or its scope guard.";
                yield break;
            }

            // ── (b) THE OUTCOME: applying a mirrored batch ASKS for a repaint ──
            if (!Program.Callees(applyLayout, mod).Any(c => Same(c, markDirty)))
                yield return "L157 apply-unmarked: TacticalInventorySync.ApplyLayout never calls " +
                             "TacticalUiRepaint.MarkDirty, so a mirrored batch moves the items in the model and " +
                             "nothing ever asks the open screen to redraw. The flush is driven by that one flag " +
                             "— no ability executes for an inventory move, so the AbilityExecuted mark cannot " +
                             "cover it — and L116's whole both-panel rebuild sits behind it, correct and never " +
                             "reached. The player is left closing and re-opening the inventory to see what " +
                             "another peer did, which is the defect this law exists for.";

            // ── (c) BOTH apply paths, not just the one the settle takes ────
            foreach (var entry in new[] { applyInventory, handleIntent })
                if (!Program.Callees(entry, mod).Any(c => Same(c, applyLayout)))
                    yield return "L157 apply-path-unmarked: TacticalInventorySync." + entry.Name + " does not " +
                                 "reach ApplyLayout, so that path applies (or is meant to apply) a batch without " +
                                 "the mark that repaints an open screen. Both matter and they fail differently: " +
                                 "ApplyInventory is every peer receiving the host's settle, HandleInventoryIntent " +
                                 "is the HOST executing a client's batch — and the host's own screen goes stale " +
                                 "exactly the same way, because a client's intent lands there with no host-side " +
                                 "view transition of any kind.";

            // ── (d) the repaint cannot re-commit on a mirroring peer ───────
            if (!Program.Callees(flushGesture, mod).Any(c => Same(c, active)))
                yield return "L157 mirror-recommits: TacticalInventorySync.FlushGesture never asks " +
                             "SyncApplyScope.Active, so the RefreshUI that ENDS every mirrored repaint comes " +
                             "back through the per-gesture commit seam and makes a peer that is only watching " +
                             "run AttemptMoveItems + ApplyInventoryActions against its own model " +
                             "(InventoryQuery.SyncItems → InventoryComponent.AddItem/RemoveItem). That is a " +
                             "model write on a non-authoritative peer (P3/L67) that ships nothing and logs " +
                             "nothing, and SyncItems:61 also Resets every query — throwing away the drag that " +
                             "player had staged and not confirmed.";

            // ── (e) POSITIVE CONTROL: the loop arm (d) guards is REAL ──────
            var patched = SafeInvoke(targetMethod);
            var invoked = refreshHandle.GetValue(null) as MethodBase;
            if (patched == null || invoked == null || !Same(patched, invoked))
                yield return "L157 POSITIVE CONTROL: the method InventoryGestureSeam patches (" +
                             Describe(patched) + ") and the method RepaintContainerView invokes through " +
                             "InvRefreshUi (" + Describe(invoked) + ") are no longer the same " +
                             "UIStateInventory.RefreshUI. Either the repaint stopped ending on the game's own " +
                             "both-panel precedent, or the gesture seam moved off it — and the moment they are " +
                             "different methods the scope guard arm (d) asserts is a rule about nothing. " +
                             "Re-read which method now carries the gesture before keeping either arm.";
        }

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;

        /// <summary>TargetMethod is the seam's own Harmony hook; asking it is the only way to learn what the
        /// postfix actually attaches to, and a throw here is a premise failure rather than a harness crash.</summary>
        private static MethodBase SafeInvoke(MethodInfo targetMethod)
        {
            try { return targetMethod.Invoke(null, null) as MethodBase; }
            catch (Exception) { return null; }
        }

        private static string Describe(MethodBase m) =>
            m == null ? "<unresolved>" : m.DeclaringType?.Name + "." + m.Name;
    }
}
