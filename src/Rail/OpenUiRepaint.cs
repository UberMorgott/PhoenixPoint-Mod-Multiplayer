using System;
using System.Reflection;
using Base.Core;
using Base.UI;
using HarmonyLib;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewStates;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Law 11, UNIVERSAL path. After a remote mirror batch lands on the CLIENT, re-drive the OPEN
    /// geoscape screen through its own native full-rebuild (Exit → Enter on the SAME view-state
    /// instance) so it repaints exactly as it does when the user opens it — one generic mechanism for
    /// all 38 <see cref="GeoscapeViewState"/> screens, no per-panel repaint code.
    ///
    /// GROUNDED (decompile): GeoscapeViewState.Exit(stack) removes the input handler + runs ExitState();
    /// Enter(stack) re-adds it (AddUnique = no double-sub) + runs EnterState() = the screen's ONLY full
    /// rebuild path (there is no native Refresh/Invalidate; the engine never repaints an open screen from
    /// model changes). Same instance preserves the ctor fields (_site/_base/_vehicle…); Context/_stateStack
    /// were set in Push and survive. Enter/Exit take the StateStack&lt;GeoscapeViewContext&gt; — the
    /// GeoscapeView._statesStack private field.
    ///
    /// OPT-OUT: UIStateManufacturing.ExitState() nulls _filter + closes its module, so a bare Exit+Enter
    /// loses the active filter. It keeps its own ManufactureSync.DoFilter repaint; skipped here.
    ///
    /// DEBOUNCE: one mirror tick can arrive as several chunked GeoRail packets processed in ONE frame
    /// (NetworkEngine.Update drains all inbound via Transport.Update BEFORE Sync.Tick). Re-entering a
    /// screen is not free, so the batch boundary only sets a dirty flag; SyncEngine.Tick flushes exactly
    /// ONE re-enter per frame. Host never marks dirty (it never applies a delta), so the flush is inert there.
    /// </summary>
    public static class OpenUiRepaint
    {
        private static bool _dirty;

        private static readonly FieldInfo StatesStackField =
            AccessTools.Field(typeof(GeoscapeView), "_statesStack");

        private static GeoLevelController GeoLevel()
        {
            var level = GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<GeoLevelController>();
        }

        /// <summary>Close of a remote mirror-apply batch (client). Coalesced to one re-enter per frame
        /// by <see cref="FlushIfDirty"/> — cheaper than re-entering per chunk on a multi-packet resend.</summary>
        public static void MarkDirty() => _dirty = true;

        /// <summary>Session teardown: drop the pending repaint so the NEXT session's first Tick does not
        /// inherit a dirty flag from the dead one.</summary>
        public static void Reset()
        {
            _dirty = false;
        }

        /// <summary>Driven once per frame from SyncEngine.Tick. No-op on the host (never marks dirty).</summary>
        public static void FlushIfDirty()
        {
            if (!_dirty) return;
            _dirty = false;
            RepaintOpenGeoscapeScreen();
        }

        /// <summary>Re-drive the open geoscape screen's native full-rebuild (Exit → Enter, same instance).
        /// Guarded per-state so one of the ~31 un-audited screens misbehaving can't crash the apply loop.</summary>
        public static void RepaintOpenGeoscapeScreen()
        {
            var view = GeoLevel()?.View;
            var current = view?.CurrentViewState;
            if (current == null) return;
            if (current is UIStateManufacturing) return; // opt-out: bare Exit+Enter drops its _filter
            if (!(StatesStackField?.GetValue(view) is StateStack<GeoscapeViewContext> stack)) return;
            try
            {
                // law 8: a re-enter that fires native events must not echo an intent back to the host.
                using (SyncApplyScope.Enter())
                {
                    current.Exit(stack);
                    current.Enter(stack);
                }
                if (MpDiag.On) Debug.Log("[MP][uirepaint] re-entered " + current.GetType().Name); // TEMP diag (remove with mfgdiag)
            }
            catch (Exception ex)
            {
                // Exit() ALREADY ran (module Deinit/Done, marker destroyed, 6 events unsubscribed —
                // UIStateVehicleSelected.cs:237-258), so a swallowed throw strands the screen half-entered:
                // dead widgets, wrong icons, and it re-fails on every later rail batch. Roll FORWARD to a
                // known-good state instead of leaving the stack where it broke. UIStateNothingSelected is
                // the game's own recovery for an unenterable geoscape screen (UIStateVehicleSelected
                // .EnterState:134-137 does exactly this when it cannot resolve a vehicle), and its
                // EnterState:84 re-establishes SetActiveState/SetInputState, so input is live again.
                // Generic: one recovery for all ~31 screens, no per-screen knowledge.
                // Log the FULL exception — ex.Message alone hid the stack and left the NRE unidentified.
                Debug.LogWarning("[Multiplayer][rail] open-UI re-enter failed for " + current.GetType().Name +
                                 " — recovering to UIStateNothingSelected: " + ex);
                try
                {
                    using (SyncApplyScope.Enter())
                        stack.SwitchToState(new UIStateNothingSelected(), StateStackAction.ClearStackAndPush);
                }
                catch (Exception recoverEx)
                {
                    Debug.LogError("[Multiplayer][rail] recovery to UIStateNothingSelected FAILED: " + recoverEx);
                }
            }
        }
    }
}
