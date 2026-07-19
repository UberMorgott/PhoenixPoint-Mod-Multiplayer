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
        private static int _deferredFrames;
        private static bool _deferLogged;

        /// <summary>Defer ceiling in frames (~5 s at 60 fps). See <see cref="FlushIfDirty"/>.</summary>
        private const int MaxDeferFrames = 300;

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
        /// inherit a dirty flag from the dead one, and re-arm the one-shot diagnostics.</summary>
        public static void Reset()
        {
            _dirty = false;
            _deferredFrames = 0;
            _deferLogged = false;
        }

        /// <summary>
        /// Driven once per frame from SyncEngine.Tick. No-op on the host (never marks dirty).
        ///
        /// N2 — a repaint DEFERS to uncommitted local input. Exit+Enter destroys and rebuilds every
        /// widget on the screen, so running it while the user is mid-drag yanks the item out of their
        /// hand. The defer happens BEFORE <c>_dirty</c> is cleared, so a deferred repaint is retried on
        /// the next frame rather than dropped — which is the difference between "later" and "never", and
        /// the reason this is not an opt-out. No screen is ever named here: the question asked is about
        /// INPUT state, not about which screen is open.
        /// </summary>
        public static void FlushIfDirty()
        {
            if (!_dirty) return;
            if (LocalInputInFlight())
            {
                // ponytail: bounded defer, ceiling = MaxDeferFrames. A leaked gesture flag or a wedged
                // drag can delay a repaint that long but can NEVER starve it forever — an unbounded
                // version of exactly this wedge (a4f3b2b's drag claim) cost a whole test cycle. Upgrade
                // path if this ever fires legitimately: make the stuck flag frame-scoped at its source,
                // do not raise the cap.
                if (++_deferredFrames < MaxDeferFrames) return; // _dirty stays set: retried next frame
                if (!_deferLogged)
                {
                    _deferLogged = true;
                    Debug.LogWarning("[Multiplayer][rail] open-UI repaint forced after " + MaxDeferFrames +
                                     " deferred frames — a drag or gesture flag is stuck (please report)");
                }
            }
            _deferredFrames = 0;
            _dirty = false;
            RepaintOpenGeoscapeScreen();
        }

        /// <summary>The local user has UNCOMMITTED input in flight. Asked of input state, not of screens:
        /// a screen with no drag icon simply answers false, so this needs no per-screen table.</summary>
        private static bool LocalInputInFlight()
        {
            var mods = GeoLevel()?.View?.GeoscapeModules;
            if (mods == null) return false;
            // ponytail: EquipSync's gesture flag is deliberately NOT consulted here — it is consumed by the
            // very next SetItems flush (same frame on an open equip screen), so it can never be "in flight"
            // long enough for a repaint to interleave, and a leaked one would defer repaints for nothing.
            // The drag half below already covers the case that yanks a held item.
            var soldierDrag = mods.SoldierEquipModule == null ? null : mods.SoldierEquipModule.ItemDragIcon;
            if (soldierDrag != null && soldierDrag.IsBeingDragged()) return true;
            var vehicleDrag = mods.VehicleEquipModule == null ? null : mods.VehicleEquipModule.ItemDragIcon;
            return vehicleDrag != null && vehicleDrag.IsBeingDragged();
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
