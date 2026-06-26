namespace Multipleer.Network.Sync
{
    /// <summary>
    /// Default-OFF rollout gate for the client geoscape EVENT-WINDOW desync fixes (additive-first, mirrors
    /// <see cref="GeoRailGate"/> / <see cref="ReportMirrorGate"/>). It guards two coupled fixes to the host->client
    /// event-dialog mirror:
    ///   • <b>Symptom 2 (burst / wrong-page):</b> the synthetic RESULT page is pushed WITH its occurrence id (not
    ///     the legacy forced 0) so <c>EventDisplay.Dismiss</c>'s occId close-guard keeps a DIFFERENT occurrence's
    ///     dismiss from evicting it — the client becomes a faithful occId-keyed mirror of the native modal queue.
    ///   • <b>Symptom 1 (reward line dropped):</b> the buffered reward is never silently <c>DropBufferedReward</c>'d
    ///     out from under a page the client will show, and the render is armed in a BURST-SAFE keyed slot
    ///     (<see cref="State.RewardPendingSlots"/>) instead of a single reference-identity slot that a second
    ///     result page (or an empty-reward <c>ClearPending</c>) could clobber before the deferred render consumes it.
    ///
    /// While <see cref="Enabled"/> is false (the SHIPPED default) BOTH fixes are inert and the legacy behavior is
    /// byte-for-byte unchanged: result pages push with occId 0 and the reward render uses the single-slot path.
    /// Flip to true (a one-line dev edit + recompile) only after in-game verification.
    /// </summary>
    public static class EventMirrorFixGate
    {
        /// <summary>Master switch for the additive event-window desync fixes. Shipped OFF.</summary>
        public static bool Enabled = false;
    }
}
