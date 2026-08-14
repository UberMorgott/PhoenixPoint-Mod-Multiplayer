using System;
using System.Collections.Generic;
using HarmonyLib;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Levels;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Law 3 at the HAVEN-RESEARCH ASSIGNMENT funnel, and the reason
    /// <c>S#&lt;n&gt;.SerializationData.HavenData.AssignedResearchId</c> sat in the host's CRC backstop as
    /// STILL diverged after a forced re-emit (live session 2026-08-14, roots S#75/S#76).
    ///
    /// <c>GeoHavenResearchManager.UpdateFactionHavensResearch</c> (GeoHavenResearchManager.cs:66-99) is the
    /// SINGLE funnel every haven-research assignment bottoms out in — <c>UpdateCompletedResearch</c>:38,
    /// <c>UpdateUnassignedVisitedHavens</c>:46, <c>UpdateHavensResearch</c>:52 and
    /// <c>UpdateHavenResearch</c>:62 all end here. Its body first NULLS <c>AssignedResearch</c> on every
    /// haven of the faction (:73) and then re-assigns from an UNSEEDED local RNG — two
    /// <c>GetRandomElement()</c> draws per pairing (:91/:94). So each peer mints its own assignment, and
    /// a diff-based rail can never correct it: the diff is host-now vs host-before, so once the client has
    /// re-rolled, the host has nothing left to re-state. That is exactly what the backstop reported —
    /// the re-emit lands the host's id and the client's next re-roll overwrites it.
    ///
    /// It is not a rare path on a client either. Every peer reaches it at level start
    /// (<c>GeoPhoenixFaction</c>:341, right after the save transfer), on every completed research
    /// (:1069 <c>OnResearchCompleted</c>) and on every first haven visit (:1168
    /// <c>OnSiteFirstTimeVisited</c>) — and the client's sim clock is deliberately NOT frozen
    /// (see <see cref="ClientSimGate"/>), so all three fire there too.
    ///
    /// ONE gate at the funnel, never one per caller: two gates on the same assignment can disagree, and the
    /// disagreement is the bug (the argument <see cref="ResearchSync"/> already records for
    /// <c>ClientResearchGate</c>). Host and solo stay fully native. The method is void and every caller
    /// discards it, so nothing dereferences a blocked call.
    ///
    /// CLOSED BY DEFAULT, and it never consults <c>IsActiveSession</c> — the same argument
    /// <see cref="ClientResearchGate"/> records. The client's very first reach into this funnel is
    /// GeoPhoenixFaction:341 at level start, i.e. inside the UNKNOWN window right after the save transfer
    /// (<c>Session.HostPeerId</c> momentarily unset), and ONE tick there re-rolls every haven of the
    /// faction. A peer whose engine exists and is not the host is a CLIENT, session predicate or not; solo
    /// has no engine and stays fully native. No level latch is needed as ClientResearchGate needs one:
    /// every caller here runs off a live <c>GeoLevelController</c> that only exists while the engine does.
    ///
    /// DELIBERATELY NO <c>SyncApplyScope</c> ARM, unlike <see cref="GeoscapeEventRaiseGate"/> and for
    /// <see cref="VehicleArrivalGate"/>'s reason: nothing on the rail replays this method, and an open apply
    /// arm would let a re-roll run on precisely the path that lands the host's assignment.
    ///
    /// REACTIVITY: the host's value rides the ordinary <c>AssignedResearchId</c> leaf on the haven's own
    /// <c>S#&lt;n&gt;</c> root, so it repaints through the DEFAULT universal arm — every rail batch ends in
    /// <c>OpenUiRepaint</c>'s one re-enter of the open view state, and the haven panel reads
    /// <c>_haven.AssignedResearch</c> live on rebuild (HavenFacilityItemController.cs:249-251). Blocking the
    /// local write removes no repaint: before this gate the client repainted its OWN wrong roll.
    /// </summary>
    [HarmonyPatch]
    internal static class HavenResearchGate
    {
        private static readonly HashSet<string> _logged = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Named as the game names it; a drifted name yields null, which RailCheck L23 reports as an
        /// unbound handle rather than a silently absent gate. Parameter types are named EXACTLY —
        /// <c>AccessTools</c> does no widening.</summary>
        internal static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(typeof(GeoHavenResearchManager), "UpdateFactionHavensResearch",
                               new[] { typeof(GeoFaction), typeof(IEnumerable<GeoHaven>) });

        private static bool Prefix(GeoFaction owner)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || engine.IsHost) return true; // solo/host: native — see "CLOSED BY DEFAULT"

            // Diagnostics only — never let a log path decide whether the gate holds (and RailCheck L480
            // invokes this prefix outside Unity, where the logger has no runtime under it).
            try
            {
                string who = owner?.Def == null ? "?" : owner.Def.name;
                if (_logged.Add(who))
                    MpLog.Log("[MP][research] client-local haven research re-roll for '" + who + "' BLOCKED — " +
                              "the assignment is an unseeded RNG draw (GeoHavenResearchManager.cs:91/:94); " +
                              "the host's AssignedResearchId arrives via the rail");
            }
            catch { }
            return false;
        }
    }
}
