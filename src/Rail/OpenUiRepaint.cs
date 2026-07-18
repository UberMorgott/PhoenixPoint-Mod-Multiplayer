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
    /// UIStateEditSoldier.ExitState() COMMITS its stale UI model (SetItems/UpdateStorage/
    /// CommitStatChanges) — re-entering it destroys just-applied remote state; skipped here (decompile
    /// survey 2026-07-18: the only commit-on-exit state among the 38).
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
            // opt-out KEPT even with ClientSimGate.ClientEquipFlushGate restored: (a) the gate covers
            // UpdateStorage/UpdateSoldierEquipment on the CLIENT only, but this repaint ALSO runs on
            // the HOST (ClaimSync.RepaintOrDefer → MarkDirty on claim ops), where ExitState flushes
            // natively — the original RCA 2026-07-18 (host log 202.1s) was exactly a host-side
            // claim-triggered re-enter one frame after a remote equip intent applied: Exit wrote the
            // pre-intent lists back over GeoCharacter and drained storage via repeated takes;
            // (b) ExitState:232 also runs UIModuleCharacterProgression.CommitStatChanges (:369-386) —
            // stale UI stat/skill-point/mutagen snapshots written into the live model, ungated on any
            // peer (progression is not a migrated subsystem). This screen is UI-model-authoritative
            // while open; it must NEVER be re-entered as a repaint.
            if (current is UIStateEditSoldier) return;
            if (!(StatesStackField?.GetValue(view) is StateStack<GeoscapeViewContext> stack)) return;
            try
            {
                // law 8: a re-enter that fires native events must not echo an intent back to the host.
                using (SyncApplyScope.Enter())
                {
                    current.Exit(stack);
                    current.Enter(stack);
                }
                Debug.Log("[MP][uirepaint] re-entered " + current.GetType().Name); // TEMP diag (remove with mfgdiag)
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Multiplayer][rail] open-UI re-enter failed for " + current.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
