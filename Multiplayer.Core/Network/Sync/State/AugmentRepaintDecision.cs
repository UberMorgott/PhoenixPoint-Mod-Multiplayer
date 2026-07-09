using System.Collections.Generic;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// PURE decision (BCL-only → unit-testable): should an open augmentation screen (mutation/bionics)
    /// repaint after an authoritative apply? (augment preview regression RCA 2026-07-09). The repaint
    /// resets the module's cached <c>CharacterOriginalItems</c> baseline from the LIVE model and clears the
    /// pending preview — correct ONLY when the apply actually stamped the character shown on the screen.
    /// Firing it on unrelated applies (hourly bulk sweep, another soldier's equip, a different soldier's
    /// augment) ate the user's uncommitted LOCAL preview and, because the live ArmourItems still held the
    /// transient preview item, baked that preview into the baseline (phantom never-purchased augment).
    /// </summary>
    public static class AugmentRepaintDecision
    {
        /// <summary>True when the apply stamped the screen's character. <paramref name="stampedUnitIds"/>
        /// null = caller cannot say which units were stamped → conservative repaint (legacy behavior);
        /// empty = the apply stamped no soldier state → the screen cannot be stale → keep the local
        /// preview. <paramref name="openUnitId"/> 0 = unresolved character id → conservative repaint
        /// (a missed repaint is a stale mirror forever; an extra one only costs a preview).</summary>
        public static bool ShouldRepaint(long openUnitId, IReadOnlyList<long> stampedUnitIds)
        {
            if (stampedUnitIds == null) return true;
            if (openUnitId == 0) return stampedUnitIds.Count > 0;
            for (int i = 0; i < stampedUnitIds.Count; i++)
                if (stampedUnitIds[i] == openUnitId) return true;
            return false;
        }
    }
}
