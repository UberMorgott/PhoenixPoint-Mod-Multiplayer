using System;
using System.Reflection;
using Base.Core;
using Base.UI;
using HarmonyLib;
using PhoenixPoint.Common.View.ViewModules;
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
    /// OVERRIDE TABLE (the ONLY place screen-specific knowledge lives, per mandate):
    /// • UIStateManufacturing.ExitState() nulls _filter + closes its module, so a bare Exit+Enter
    ///   loses the active filter. It keeps its own ManufactureSync.DoFilter repaint; skipped here.
    /// • UIStateEditSoldier.ExitState() COMMITS its stale UI model (:225 UpdateStorage,
    ///   :226 UpdateSoldierEquipment→GeoCharacter.SetItems, :232 CommitStatChanges) — re-entering it
    ///   destroys just-applied remote state (decompile survey 2026-07-18: the only commit-on-exit state
    ///   among the 38). Routed to ReseedEditSoldier instead — see there.
    /// • UIStateEditVehicle needs NO override: its ExitState (:277-309) is pure unsubscribe — no
    ///   UpdateVehicleEquipment, no UpdateStorage, no CommitStatChanges — so the universal path is safe.
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
        /// inherit a dirty flag from the dead one, and re-arm the one-shot diagnostics.</summary>
        public static void Reset()
        {
            _dirty = false;
            _bindLogged = false;
            _dragDeferLogged = false;
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
            // Exit+Enter is FORBIDDEN here on EVERY peer, not just the client: this repaint also runs on
            // the HOST (EquipSync.ReseedLocalScreenAfterRemoteMutation :577, straight after a remote equip
            // intent applies), where ExitState flushes natively — the original RCA 2026-07-18 (host log
            // 202.1s) was exactly such a host-side re-enter one frame after a remote equip intent applied:
            // Exit wrote the pre-intent lists back over GeoCharacter and drained storage via repeated takes.
            // Reseed reads the model into the UI instead; it never writes UI→model.
            if (current is UIStateEditSoldier editSoldier) { ReseedEditSoldier(editSoldier, view); return; }
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

        // ─── UIStateEditSoldier override: reseed UI from the model, never the reverse ───

        // GetSoldierEquipModuleData()/RefreshStorage() are private no-arg methods and _refreshStorage a
        // real private bool — all three resolve via AccessTools (the module accessors do NOT: they are
        // expression-bodied properties, so AccessTools.Field returns null for them; we take the public
        // GeoscapeView.GeoscapeModules path they delegate to — GeoscapeViewState.cs:28 — instead).
        private static readonly MethodInfo GetEquipDataMethod =
            AccessTools.Method(typeof(UIStateEditSoldier), "GetSoldierEquipModuleData");
        private static readonly MethodInfo RefreshStorageMethod =
            AccessTools.Method(typeof(UIStateEditSoldier), "RefreshStorage");
        private static readonly FieldInfo RefreshStorageFlag =
            AccessTools.Field(typeof(UIStateEditSoldier), "_refreshStorage");

        private static bool _bindLogged;
        private static bool _dragDeferLogged;

        /// <summary>Non-destructive repaint for the one commit-on-exit screen: re-populate its widget lists
        /// from the live model, in the native EnterState order (:132/:161/:162/:177), READ-direction only.
        /// Deliberately does NOT set _uiRefreshNeeded — that arms UpdateState (:459-475) which calls
        /// UpdateSoldierEquipment + UpdateStorage, i.e. exactly the UI→model flush we exist to avoid
        /// (gated on the client by ClientEquipFlushGate, but UNgated on the host).</summary>
        private static void ReseedEditSoldier(UIStateEditSoldier state, GeoscapeView view)
        {
            if (GetEquipDataMethod == null || RefreshStorageMethod == null || RefreshStorageFlag == null)
            {
                if (!_bindLogged)
                {
                    _bindLogged = true;
                    Debug.LogError("[MP][reseed] UIStateEditSoldier member unresolved — equip/storage lists will NOT mirror:" +
                                   " GetSoldierEquipModuleData=" + (GetEquipDataMethod != null) +
                                   " RefreshStorage=" + (RefreshStorageMethod != null) +
                                   " _refreshStorage=" + (RefreshStorageFlag != null));
                }
                return;
            }

            var modules = view.GeoscapeModules;
            var equip = modules?.SoldierEquipModule;
            var actorCycle = modules?.ActorCycleModule;
            var character = actorCycle?.CurrentCharacter;
            if (equip == null || character == null) return;

            // Prefer losing a frame of freshness over yanking the local player's drag: OnDataChanged:923
            // would InterruptDrag, and InterruptDrag:1407 dereferences SelectedSlot unguarded (NRE).
            // Re-arm instead — the flag is re-checked next frame, so the reseed lands on drag end.
            if (equip.ItemDragIcon != null && equip.ItemDragIcon.IsBeingDragged())
            {
                _dirty = true;
                if (!_dragDeferLogged) { _dragDeferLogged = true; Debug.Log("[MP][reseed] deferred — local drag in flight"); }
                return;
            }
            _dragDeferLogged = false;

            try
            {
                // law 8: native events fired by the reseed must not echo an intent back to the host.
                using (SyncApplyScope.Enter())
                {
                    // 1. drag/selection guard + manufacture/scrap dialogs hidden + proficiencies (:919-943).
                    equip.OnDataChanged((UIModuleSoldierEquipData)GetEquipDataMethod.Invoke(state, null));
                    // 2. equip lists: per-list Deinit+Init from the live GeoCharacter (:626-667). The
                    //    inventorySlots arg is ignored on this screen (:628 hard-zeroes the bonus) — omitted.
                    equip.UpdateData(character.InventoryItems, character.EquipmentItems, character.ArmourItems);
                    // 3. 3D doll.
                    actorCycle.DisplaySoldier(character, resetAnimation: true);
                    // 4. storage list + free-space indicator, via the screen's own RefreshStorage (:587-597).
                    //    The flag is mandatory: with it false RefreshStorage re-seeds from the STALE UI list
                    //    and silently does nothing. ToList() hands out CLONES (ItemStorage.cs:171-178), so
                    //    nothing here can touch model items.
                    RefreshStorageFlag.SetValue(state, true);
                    RefreshStorageMethod.Invoke(state, null);
                    // 5. skill points / stat panel / ability tracks re-read from the model (:463-478).
                    //    RefreshStats() alone is NOT enough — it copies the cached stale baseline.
                    //    Accepted: discards unspent LOCAL stat allocation, which is correct for a mirror.
                    modules.CharacterProgressionModule?.SetCharacterProgression(GeoLevel()?.ViewerFaction, character);
                }
                Debug.Log("[MP][reseed] UIStateEditSoldier reseeded from model");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MP][reseed] UIStateEditSoldier reseed failed: " + ex);
            }
        }
    }
}
