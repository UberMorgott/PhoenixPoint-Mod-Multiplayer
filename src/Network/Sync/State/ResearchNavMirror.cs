using System.Collections.Generic;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// PURE (Unity-free) decision + pending-state store for mirroring the host's NATIVE "new research
    /// available" nav line on the client's mirrored GeoResearchComplete popup.
    ///
    /// WHY (2-instance soak 2026-07-05, ANU_AnuPriest_ResearchDef): the native bind
    /// (<c>GeoReseatchCompleteDataBind.ModalShowHandler</c>, GeoReseatchCompleteDataBind.cs:124-125) toggles
    /// <c>NewResearchesGroup</c> from <c>ResearchElement.UnlocksResearches</c> — a LIVE recompute over
    /// <c>Faction.Research.Noncompleted</c> states + requirement progress (ResearchElement.GetNextResearches).
    /// The HOST evaluates it against the authoritative sim → its render IS the native SP render. The CLIENT
    /// evaluates the same code against MIRRORED research state that intentionally skips the native completion
    /// cascade (<c>ResearchStateReflection.CompleteEchoOnly</c> writes <c>_state</c> directly, so
    /// <c>Research.OnResearchCompletedHandler → CheckInvalidates</c> (Research.cs:594-606) never hides
    /// invalidated elements client-side, and requirement <c>Progress</c> is never synced) → the client's
    /// recompute can disagree with the host (observed: client showed the nav line, host natively had none).
    ///
    /// FIX: the host broadcasts its native answer (one tri-state flag riding the Research payload's
    /// <c>ShareLevel</c> field — unused by that variant) and the client FORCES its mirrored popup's
    /// NewResearchesGroup to the host's value. The HOST is NEVER overridden (host transparency, S1 invariant):
    /// <see cref="ShouldOverride"/> is client-only by construction. <c>ClientResearchNavigatePatch</c> (the
    /// client's nav-line click → ToResearchState) is untouched — when the host shows the line, the client
    /// shows it too and the click still navigates.
    /// </summary>
    public static class ResearchNavMirror
    {
        // ── tri-state nav flag on the wire (Research variant's ShareLevel field) ──
        public const int NavUnknown = 0;   // host read failed / legacy payload → client stays native (no override)
        public const int NavHidden = 1;    // host's native popup shows NO "new research available" line
        public const int NavShown = 2;     // host's native popup shows the line

        /// <summary>Host-side: fold the native bind's visibility answer into the wire flag.</summary>
        public static int FlagFor(bool hostHasNewResearch) => hostHasNewResearch ? NavShown : NavHidden;

        /// <summary>
        /// True iff the mirrored popup's NewResearchesGroup must be forced to the host value: only on a CLIENT
        /// in an active session with a definite host answer. The HOST always renders native (never overridden) —
        /// this is the "host keeps its native button" invariant.
        /// </summary>
        public static bool ShouldOverride(bool isHost, bool isActiveSession, int navFlag)
            => !isHost && isActiveSession && (navFlag == NavHidden || navFlag == NavShown);

        /// <summary>The forced visibility for a definite flag (callers gate on <see cref="ShouldOverride"/>).</summary>
        public static bool NavVisible(int navFlag) => navFlag == NavShown;

        // ── pending per-popup override, keyed by researchId (multiple popups may queue) ──────────────
        private const int MaxPending = 16;   // hard cap; stray never-shown entries must not accumulate
        private static readonly Dictionary<string, int> _pending = new Dictionary<string, int>();

        /// <summary>
        /// Client: remember the host's nav flag for the researchId whose mirrored popup is about to queue.
        /// Unknown flags and empty ids are not stored (the bind then stays native — fail-open).
        /// </summary>
        public static void Arm(string researchId, int navFlag)
        {
            if (string.IsNullOrEmpty(researchId)) return;
            if (navFlag != NavHidden && navFlag != NavShown) return;
            lock (_pending)
            {
                if (_pending.Count >= MaxPending && !_pending.ContainsKey(researchId)) _pending.Clear();
                _pending[researchId] = navFlag;
            }
        }

        /// <summary>One-shot consume of the pending flag for <paramref name="researchId"/> (false = stay native).</summary>
        public static bool TryConsume(string researchId, out int navFlag)
        {
            navFlag = NavUnknown;
            if (string.IsNullOrEmpty(researchId)) return false;
            lock (_pending)
            {
                if (!_pending.TryGetValue(researchId, out navFlag)) return false;
                _pending.Remove(researchId);
                return true;
            }
        }

        /// <summary>Boundary belt (save-transfer/reload): never inherit a stale pending override.</summary>
        public static void Reset()
        {
            lock (_pending) _pending.Clear();
        }
    }
}
