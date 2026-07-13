namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// PURE (Unity-free) classifier for the geoscape-event mirror's mission-deploy exclusion — the decision
    /// behind <c>EventReflection.IsMissionDeployEvent</c> (which reads the live per-choice flags off the
    /// raised <c>GeoscapeEvent</c> and feeds them here).
    ///
    /// A PURE DEPLOY PROMPT is the small transient site popup ("begin mission / leave"): at least one choice
    /// STARTS a tactical mission (<c>Outcome.StartMission.MissionTypeDef != null</c>,
    /// GeoEventChoiceOutcome.cs:315) and every OTHER choice is a bare decline (null <c>Outcome</c> or an
    /// all-empty one — no outcome text, no reward payload). Only that window is excluded from the client
    /// mirror: it is a host-local PRE-DECISION prompt whose host cancel would strand a mirrored copy
    /// (phantom result page — the 9e80b24 goal, kept).
    ///
    /// REGRESSION PIN (2026-07-13): 9e80b24 classified on ANY mission-starting choice, so every STORY event
    /// that merely CONTAINED a mission-launch choice (PROG_* windows with real rewarded/outcome-bearing
    /// alternatives) was silently skipped — clients stopped seeing all story/site-arrival windows. A
    /// mission-launch choice MIXED with a real story choice (any non-mission choice carrying outcome
    /// text / rewards — <c>EventReflection.ChoiceHasOutcomePayload</c>) must MIRROR again.
    /// </summary>
    public static class MissionDeployClassifier
    {
        /// <summary>
        /// True iff the event is a PURE deploy prompt (skip the client mirror): ≥1 choice starts a mission
        /// AND every non-mission choice has NO outcome payload. Null <paramref name="choiceStartsMission"/>
        /// → false (fail OPEN to broadcast; never suppress a legit event on a read failure). Null
        /// <paramref name="choiceHasPayload"/> → non-mission choices are treated as bare declines (callers
        /// that can read mission flags but not payloads keep the deploy-prompt exclusion). Extra/missing
        /// payload entries are ignored/decline respectively (arrays are read index-parallel).
        /// </summary>
        public static bool IsPureDeployPrompt(bool[] choiceStartsMission, bool[] choiceHasPayload)
        {
            if (choiceStartsMission == null) return false;
            bool anyMission = false;
            for (int i = 0; i < choiceStartsMission.Length; i++)
            {
                if (choiceStartsMission[i]) { anyMission = true; continue; }
                if (choiceHasPayload != null && i < choiceHasPayload.Length && choiceHasPayload[i])
                    return false;   // real story alternative (reward/outcome-bearing) → not a pure prompt → mirror
            }
            return anyMission;
        }

        /// <summary>
        /// True iff a RESOLVED choice should close the client mirror with a plain DISMISS instead of a result
        /// page: the choice STARTS a tactical mission (its native follow-up is the mission brief / deploy flow,
        /// never a text page), the rebuilt result body is EMPTY and the dismiss carried NO reward — there is
        /// nothing to show. Anything renderable (body text or reward lines) → false, normal result page.
        /// (Live failure 2026-07-13: PROG_AN0_MISS mirrored under the narrowed classifier above; resolving its
        /// mission choice synthesized a BLANK page with a lone OK button on every client.)
        /// </summary>
        public static bool ShouldSuppressEmptyMissionResult(bool choiceStartsMission, bool bodyEmpty, bool rewardEmpty)
            => choiceStartsMission && bodyEmpty && rewardEmpty;
    }
}
