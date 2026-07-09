using System.Collections.Generic;
using Multiplayer.Network;
using Multiplayer.Network.Sync;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// CLIENT-side augmentation-screen (mutation/bionics) mirror repaint driver — the <see cref="EditSession"/>-
    /// gated sibling of <see cref="EquipMirrorRepaint"/> (design: docs/superpowers/specs/
    /// 2026-07-08-coop-edit-session-engine-design.md — screens are thin adapters around the ONE pure engine).
    /// The augmentation screens have NO drag gesture (preview + commit are click-driven), so the session never
    /// defers: capTicks 0 = "never defer" and no GestureBegin/End hooks are wired — an authoritative #9
    /// apply repaints immediately (baseline reset + pending-preview clear + RequestViewRefresh via
    /// <see cref="GeoUiRefresh.RepaintAugmentation"/>; a no-op when neither screen is open) — but ONLY when
    /// the apply actually stamped the character shown on the screen (<see cref="AugmentRepaintDecision"/>):
    /// repainting on unrelated #9 traffic (hourly bulk sweep, another soldier's edit) ate the user's
    /// uncommitted LOCAL preview and baked the transient preview item into the module's baseline (preview
    /// regression RCA 2026-07-09). Kept as a session adapter rather than a bare call so augmentation follows
    /// the spec's one-primitive rule and inherits gating/lifecycle for free if a deferrable seam ever appears.
    /// </summary>
    public static class AugmentMirrorRepaint
    {
        private static readonly EditSession _session = new EditSession(0);   // no gesture on these screens → never defer

        /// <summary>True only for an ACTIVE co-op CLIENT with the gate on (not host / single-player). Gate OFF
        /// = the augment family is pure native vanilla (matches <see cref="EquipMirrorRepaint"/> and the
        /// AugmentGestureRelay suppress side).</summary>
        private static bool ClientActive()
        {
            if (!EquipSyncV2Gate.Enabled) return false;
            var eng = NetworkEngine.Instance;
            return eng != null && eng.IsActiveSession && !eng.IsHost;
        }

        /// <summary>A #9 (personnel blob) authoritative apply stamped the client model → repaint the open
        /// augmentation screen now (never deferred — see the class summary). <paramref name="stampedUnitIds"/>
        /// = the unit ids whose soldier STATE this apply actually stamped (empty = membership/SP-only apply →
        /// the screen cannot be stale → keep the local preview; null = unknown → conservative repaint).</summary>
        public static void OnRemoteApplied(GeoRuntime rt, IReadOnlyList<long> stampedUnitIds)
        {
            if (!ClientActive()) return;
            if (stampedUnitIds != null && stampedUnitIds.Count == 0) return;   // nothing stamped → nothing stale
            _session.RemoteApplied();
            if (_session.DrainRepaint(0)) GeoUiRefresh.RepaintAugmentation(rt, stampedUnitIds);
        }
    }
}
