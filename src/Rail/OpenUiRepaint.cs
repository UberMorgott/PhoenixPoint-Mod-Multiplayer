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
    /// ONE re-enter per frame. The HOST marks dirty too (post-intent reseeds in EquipSync/PersonnelSync)
    /// — every repaint, both sides, rides the same defer + coalescing.
    /// </summary>
    public static class OpenUiRepaint
    {
        private static bool _dirty;
        private static int _deferredFrames;
        private static bool _deferLogged;
        private static int _marksSinceFlush;
        private static float _nextDiagAt;
        private static readonly System.Collections.Generic.HashSet<string> _loggedFailures =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        /// <summary>Defer ceiling in frames (~5 s at 60 fps). See <see cref="FlushIfDirty"/>.</summary>
        private const int MaxDeferFrames = 300;

        private static readonly FieldInfo StatesStackField =
            AccessTools.Field(typeof(GeoscapeView), "_statesStack");

        private static GeoLevelController GeoLevel()
        {
            var level = GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<GeoLevelController>();
        }

        /// <summary>Close of a remote mirror-apply batch (client) or a host-side post-intent reseed.
        /// Coalesced to one re-enter per frame by <see cref="FlushIfDirty"/> — cheaper than re-entering
        /// per chunk on a multi-packet resend.</summary>
        public static void MarkDirty() { _dirty = true; _marksSinceFlush++; }

        /// <summary>Session teardown: drop the pending repaint so the NEXT session's first Tick does not
        /// inherit a dirty flag from the dead one, and re-arm the one-shot diagnostics.</summary>
        public static void Reset()
        {
            _dirty = false;
            _deferredFrames = 0;
            _deferLogged = false;
            _marksSinceFlush = 0;
            _loggedFailures.Clear();
        }

        /// <summary>
        /// Driven once per frame from SyncEngine.Tick, both sides.
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
            // Typing guard: Exit+Enter rebuilds every widget, wiping an active text entry mid-word
            // (soldier rename = UnityEngine.UI.InputField, UIModuleActorCycle.SoldierNameEditField:71;
            // the game's UI ships NO TMP input fields — TMPro appears only in I2.Loc label targets).
            var selected = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
            if (selected != null)
            {
                var field = selected.GetComponent<UnityEngine.UI.InputField>();
                if (field != null && field.isFocused) return true;
            }
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
        /// Guarded per-state so one of the ~31 un-audited screens misbehaving can't crash the apply loop.
        /// Private: every caller goes through <see cref="MarkDirty"/> so no repaint can bypass the
        /// drag/typing defer + per-frame coalescing in <see cref="FlushIfDirty"/>.</summary>
        private static void RepaintOpenGeoscapeScreen()
        {
            int marks = _marksSinceFlush;
            _marksSinceFlush = 0;
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
                    try { current.Exit(stack); }
                    catch (Exception exitEx)
                    {
                        // GeoscapeViewState.Exit removes the input handler BEFORE ExitState (decompile
                        // GeoscapeViewState.cs:98) — bailing out after a thrown ExitState would leave the
                        // screen DEAF. Always fall through to Enter: AddUnique re-subscribes idempotently.
                        if (_loggedFailures.Add(current.GetType().Name + ":Exit"))
                            Debug.LogWarning("[Multiplayer][rail] open-UI Exit for " + current.GetType().Name +
                                             " threw — attempting Enter anyway (logged once per screen): " + exitEx);
                    }
                    current.Enter(stack);
                }
                // The ONE place a repaint actually executes — throttled diag so a re-enter storm is
                // visible in the log without flooding it (family switch: MULTIPLAYER_DIAG, see MpDiag).
                if (MpDiag.On && Time.realtimeSinceStartup >= _nextDiagAt)
                {
                    _nextDiagAt = Time.realtimeSinceStartup + 1f;
                    Debug.Log("[MP][uirepaint] re-entered " + current.GetType().Name + " marks=" + marks);
                }
            }
            catch (Exception ex)
            {
                // NON-DESTRUCTIVE: stay on the screen the user is looking at. A throw inside EnterState is a
                // PARTIAL repaint, not a lost screen — and a partial repaint beats ejecting the player.
                //
                // The previous behaviour rolled the stack forward to UIStateNothingSelected, which reads to
                // the user as "the game kicked me out of the roster", once per rail batch. Grounded reason it
                // is not needed: GeoscapeViewState.Enter (decompile GeoscapeViewState.cs:88-94) re-registers
                // the input handler at :91 BEFORE calling EnterState(), and a geoscape state sets its own
                // MainUILayer/input state in the opening statements of EnterState (UIStateEditSoldier.cs:99-100)
                // — so a throw further in leaves the screen live and closable. The observed failure throws at
                // the LAST statement of UIStateEditSoldier.EnterState (:177 SelectCharacterProgression), i.e.
                // everything except the progression panel had already been rebuilt.
                //
                // We keep repainting this screen on later batches ON PURPOSE: the repaint mostly works, and
                // law 11 (reactivity) outranks log tidiness. Once per state TYPE the full exception is logged,
                // then it goes quiet — a per-frame stack dump was its own kind of freeze.
                if (_loggedFailures.Add(current.GetType().Name))
                    Debug.LogWarning("[Multiplayer][rail] open-UI re-enter for " + current.GetType().Name +
                                     " threw — screen kept, that panel may be partially painted until the " +
                                     "underlying subtree is complete (logged once per screen): " + ex);
            }
        }
    }
}
