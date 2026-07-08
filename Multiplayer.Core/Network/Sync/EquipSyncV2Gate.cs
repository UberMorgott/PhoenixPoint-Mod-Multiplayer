namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Rollout gate for the co-op soldier-equip sync v2 (the from-scratch edit-session rebuild — design:
    /// docs/superpowers/specs/2026-07-08-coop-edit-session-engine-design.md; mirrors
    /// <see cref="EventReplayModeGate"/>/<see cref="ReportMirrorGate"/>). The old flush-diff relay layer
    /// (SetItemsEditRelayPatch + LoadoutRelayDedup + StorageDirtyPatches + the GeoUiRefresh equip pending-repaint
    /// machinery) is DELETED, not toggled — so with <see cref="Enabled"/> FALSE the soldier-equip screen is
    /// pure NATIVE VANILLA (client edits apply locally, no relay, no mirror-repaint, no flush suppression), NOT
    /// the old layer. Flip ON (default) exercises the v2 engine:
    ///   • CLIENT: the per-frame equip/storage flush (UIStateEditSoldier.UpdateSoldierEquipment/UpdateStorage) is
    ///     no-oped — the client model is written ONLY by #9/#1 mirror applies; each equip GESTURE relays ONE
    ///     EquipSoldierAction from the post-gesture UI list truth; the mirror repaint is <see cref="EditSession"/>-
    ///     gated (deferred only while a drag is in hand, drained on drop / Tick, hard-capped ~2s).
    ///   • HOST: fully native; #9/#1 dirty from the (once-per-edit, NOT per-frame) SetItems seam; a relayed
    ///     equip apply reconciles faction storage by the authoritative loadout delta.
    /// </summary>
    public static class EquipSyncV2Gate
    {
        /// <summary>Master switch for soldier-equip sync v2. ON for 2-instance in-game validation 2026-07-08.</summary>
        public static bool Enabled = true;
    }
}
