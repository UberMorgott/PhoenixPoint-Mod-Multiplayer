using System.Collections.Generic;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// PURE (engine-free) client-apply decisions for the MISSION-CONCLUSION mirror (spec TS4, surface
    /// <c>tac.missionend</c> 0x95). Isolated from the reflection glue in <see cref="TacticalMissionEndSync"/> so the
    /// conclusion-handoff SEAM is unit-testable:
    ///   • <see cref="ShouldEndClientMission"/> — the client closes its tactical scene EXACTLY ONCE, on the terminal
    ///     (gameover) phase, and ONLY when not already game-over (idempotent; wrappingup is a pre-notify that never ends).
    ///   • <see cref="ShouldDisplayOutcome"/> — TS4 NEVER shows the outcome modal itself; the geoscape popup-mirror
    ///     (MissionOutcome 0x69) owns it. This is the "no double-outcome" contract in one asserted place.
    ///   • <see cref="ResolveObjectiveApplies"/> — map the host objective records to the client (index,state) applies,
    ///     keyed by the stable ORDINAL id, degrade-to-notify on any unknown / out-of-range id.
    /// </summary>
    public static class TacticalMissionEndGate
    {
        /// <summary>The client ends (closes) its tactical scene EXACTLY ONCE — on the terminal <c>gameover</c> phase,
        /// only when the client isn't already game-over. <c>wrappingup</c> is a soft pre-close notify (never ends).
        /// Idempotent: a re-sent gameover after the flag is already set → false (no double close).</summary>
        public static bool ShouldEndClientMission(byte phase, bool alreadyGameOver)
            => phase == TacticalMissionEndCodec.PhaseGameOver && !alreadyGameOver;

        /// <summary>TS4 NEVER displays the post-mission outcome modal — the geoscape popup-mirror rail
        /// (MissionOutcome 0x69, deferred + non-occupying in the display sequencer) owns it. TS4 only closes the
        /// TACTICAL scene, so it cannot double the 0x69 display. Constant-false = the "no double-outcome" contract.</summary>
        public static bool ShouldDisplayOutcome() => false;

        /// <summary>PURE: map the host objective records → the client (objectiveIndex, state) applies. Objectives are
        /// keyed by an ORDINAL id (their index within the shared mission's objective list — stable host↔client since
        /// both share the same mission def). Only ids that parse to an in-range ordinal on a client with
        /// <paramref name="clientObjectiveCount"/> slots are returned; an unknown / out-of-range id is skipped
        /// (degrade-to-notify). Engine-free → unit-tested.</summary>
        public static List<KeyValuePair<int, byte>> ResolveObjectiveApplies(
            IReadOnlyList<TacticalMissionEndCodec.ObjectiveRec> objectives, int clientObjectiveCount)
        {
            var applies = new List<KeyValuePair<int, byte>>();
            if (objectives == null || clientObjectiveCount <= 0) return applies;
            foreach (var o in objectives)
            {
                if (o == null) continue;
                if (int.TryParse(o.ObjectiveId, out int idx) && idx >= 0 && idx < clientObjectiveCount)
                    applies.Add(new KeyValuePair<int, byte>(idx, o.State));
            }
            return applies;
        }
    }
}
