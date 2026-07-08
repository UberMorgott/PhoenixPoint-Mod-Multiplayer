using System;
using Stopwatch = System.Diagnostics.Stopwatch;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// CLIENT-side soldier-equip mirror repaint driver (v2 rebuild — the impure adapter around the pure
    /// <see cref="EditSession"/>; design: docs/superpowers/specs/2026-07-08-coop-edit-session-engine-design.md).
    /// The client model is written ONLY by #9/#1 mirror applies; this re-drives the open equip screen from the
    /// freshly-stamped model, DEFERRING the repaint only while a drag is physically in hand (never on mere
    /// screen-open — the reverted <c>_uiRefreshNeeded</c> lesson), draining it the instant the drag drops / on
    /// Tick, with a hard ~2s cap (a stale view beats a frozen one).
    ///
    /// The clock lives HERE (impure), not in <see cref="EditSession"/>: the pure engine takes injected ticks so
    /// every decision is deterministic + unit-tested. ZERO per-frame work: <see cref="Tick"/> early-outs on a
    /// pure bool (no reflection, no log) whenever no repaint is owed — the equip UI reflection runs ONLY when a
    /// real apply is owed and not deferred (once per round-tripped edit), never in the per-frame path.
    /// </summary>
    public static class EquipMirrorRepaint
    {
        private const long CapMs = 2000;   // hard defer cap (~2s of monotonic ms)
        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        private static readonly EditSession _session = new EditSession(CapMs);

        private static long NowTicks() => _clock.ElapsedMilliseconds;

        /// <summary>True only for an ACTIVE co-op CLIENT with the gate on (not host / single-player / applying).
        /// The whole driver is inert otherwise, so gate-OFF and the host are pure native.</summary>
        private static bool ClientActive()
        {
            if (!EquipSyncV2Gate.Enabled) return false;
            var eng = NetworkEngine.Instance;
            return eng != null && eng.IsActiveSession && !eng.IsHost;
        }

        /// <summary>A #9/#1 authoritative apply stamped the client model → a repaint is owed. Fires it now unless a
        /// drag is in flight (then it defers, drained by <see cref="Tick"/> on drop / cap).</summary>
        public static void OnRemoteApplied(GeoRuntime rt)
        {
            if (!ClientActive()) return;
            _session.RemoteApplied();
            TryRepaint(rt);
        }

        /// <summary>Per-frame drain belt (from SyncEngine.Tick, client). Fast no-op — a single pure bool read —
        /// until a repaint is actually pending, so the idle open equip screen costs ZERO per-frame work.</summary>
        public static void Tick(GeoRuntime rt)
        {
            if (!_session.PendingRepaint) return;   // pure early-out BEFORE any client/gate/reflection check
            if (!ClientActive()) { return; }
            TryRepaint(rt);
        }

        private static void TryRepaint(GeoRuntime rt)
        {
            if (_session.DrainRepaint(NowTicks()))
                GeoUiRefresh.RepaintEquipAndStorage(rt);
        }

        /// <summary>A native item drag/pick started (UIInventoryItemDragIcon.BeginDrag) — mark the gesture in
        /// flight so a mirror repaint landing mid-drag defers instead of clobbering the dangling drag. Resolves
        /// the open soldier once here (drag start = human speed, never per-frame).</summary>
        public static void OnGestureBegin(GeoRuntime rt)
        {
            if (!ClientActive()) return;
            try
            {
                var character = GeoUiRefresh.ActiveEditSoldierCharacter(rt);
                long unit = character != null ? PersonnelReflection.ReadUnitId(character) : 0;
                _session.GestureBegin(unit, NowTicks());
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EquipMirrorRepaint.OnGestureBegin failed: " + ex.Message); }
        }

        /// <summary>The drag ended/cancelled (UIInventoryItemDragIcon.EndDrag) — clear the in-flight gesture so any
        /// repaint deferred during it drains on the next Tick.</summary>
        public static void OnGestureEnd()
        {
            if (_session.Target.HasValue) _session.GestureEnd(_session.Target.Value);
        }

        /// <summary>Drop all session state on a session boundary (new co-op session / reload) — the singleton is
        /// static, so a gesture/pending armed in a dying session must never leak into the next one.</summary>
        public static void ResetForNewSession()
        {
            if (_session.Target.HasValue) _session.Close(_session.Target.Value);
        }
    }
}
