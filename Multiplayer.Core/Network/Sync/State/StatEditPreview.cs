namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// DISPLAY-ONLY preview of a peer's IN-PROGRESS (uncommitted) soldier stat edit — a co-op reactivity
    /// feature so a watcher whose progression panel is open on the SAME soldier sees the spender's live +/-
    /// clicks (stat values + the two SP labels moving) BEFORE the spend commits. It carries NO authoritative
    /// state: the wire is a transient cosmetic broadcast (PacketType.StatEditPreview 0x6E), NOT an
    /// <c>ISyncedAction</c> — the real spend still rides the SpendStatPoints intent + the #9 blob, which always
    /// wins. The watcher writes ONLY the UI label text/slider values (never the panel's _current*/_starting*
    /// buffer, never the model), so a preview can never corrupt state; on preview-clear, a real #9 apply for the
    /// unit, or a 10 s safety expiry it re-drives from the model (real state wins).
    /// </summary>
    public readonly struct StatEditPreviewPayload
    {
        public readonly long UnitId;
        public readonly int DStr;       // pending base-stat deltas (current − starting), any sign
        public readonly int DWill;
        public readonly int DSpeed;
        public readonly int SoldierSP;  // live buffer _currentSkillPoints (per-soldier SP display)
        public readonly int FactionSP;  // live buffer _currentFactionPoints (shared faction-SP pool display)
        public readonly bool Clear;     // true = drop any active preview for UnitId (deltas ignored)

        public StatEditPreviewPayload(long unitId, int dStr, int dWill, int dSpeed, int soldierSP, int factionSP, bool clear)
        {
            UnitId = unitId; DStr = dStr; DWill = dWill; DSpeed = dSpeed;
            SoldierSP = soldierSP; FactionSP = factionSP; Clear = clear;
        }
    }

    /// <summary>Pure gate for the WATCHER side of the stat-edit preview. Extracted so the reflective UI caller
    /// (<c>GeoUiRefresh.ApplyStatEditPreview</c>) stays dumb and the rule is unit-testable.</summary>
    public static class StatEditPreviewDecision
    {
        /// <summary>Show the preview labels only when the progression panel is OPEN on the SAME soldier the
        /// preview is for AND the watcher has NO uncommitted local edit of its own (its live buffer is more
        /// relevant than a remote peer's preview — never clobber a local editor's view).</summary>
        public static bool ShouldShowOnWatcher(bool panelOpen, long openUnitId, long previewUnitId, bool watcherHasLocalEdit)
            => panelOpen && openUnitId == previewUnitId && openUnitId != 0 && !watcherHasLocalEdit;
    }
}
