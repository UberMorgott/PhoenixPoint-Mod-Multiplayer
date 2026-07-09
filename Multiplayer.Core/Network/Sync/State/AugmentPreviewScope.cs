namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// PURE depth latch (BCL-only → unit-testable) marking "an augmentation-screen PREVIEW write is
    /// stamping the character model right now" (augment preview regression RCA 2026-07-09). The native
    /// mutation/bionics screens implement the 3D preview as a REAL <c>GeoCharacter.SetItems</c> write
    /// (UIModuleMutate.OnAugmentClicked:184 / UIModuleBionics.OnAugmentClicked:198), reverted on
    /// Escape/exit — so the host's SetItems dirty seam (GeoCharacterSetItemsStateDirtyPatch) saw every
    /// preview CLICK as a genuine edit and broadcast the transient preview on #9/#1: the client model got
    /// stamped with an uncommitted pick and the client's open augment screen repainted mid-preview. The
    /// Harmony adapter (AugmentPreviewScopePatch) brackets ONLY <c>OnAugmentClicked</c> (both modules) with
    /// <see cref="Enter"/>/<see cref="Exit"/>; the dirty seam skips marking while <see cref="Active"/> —
    /// preview clicks become 100% local. The REVERT writes (ClearAugment/RevertUnconfirmedChanges) keep
    /// their marks (baseline re-stamp → hash-culled to zero wire normally, corrective self-heal if a bulk
    /// sweep leaked mid-preview state), and the COMMIT re-marks via AugmentCommitDirtyPatch
    /// (OnAugmentApplied), so authoritative results still mirror. Depth-counted with an underflow guard
    /// for re-entrancy safety; main-thread only (Unity UI callbacks), so a plain int suffices.
    /// </summary>
    public static class AugmentPreviewScope
    {
        private static int _depth;

        /// <summary>A preview-transaction SetItems write is on the stack — model writes are UI-local.</summary>
        public static bool Active => _depth > 0;

        public static void Enter() => _depth++;

        /// <summary>Underflow-guarded: an unmatched Exit (Harmony finalizer on a method whose prefix never
        /// ran) can never push the latch negative and permanently disarm the dirty seam.</summary>
        public static void Exit() { if (_depth > 0) _depth--; }

        /// <summary>Test/session hygiene: drop any stale depth (never carried across sessions).</summary>
        public static void Reset() => _depth = 0;
    }
}
