using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L475 — THE OPENING GRANT LIST IS SILENT UNTIL THIS PEER'S FIRST MAP SURFACE.
    ///
    /// THE FAILURE (live 2026-08-14, fresh campaign, client). The host granted the whole campaign-opening
    /// research list while the client was still in the intro cutscene — PX_PhoenixProject, PX_HavenRecruits,
    /// PX_DisciplesOfAnu, PX_NewJericho, PX_NJ_Gauss_AssaultRifle and one modded guid, across 21:22-21:27.
    /// <c>ResearchSync.SeedLatchFromMirror</c> had already run (the mirror seed lands with the first batch,
    /// GenericApplier), so every one of those grants read as a GENUINE transition, went into the deferral
    /// list, and flushed as back-to-back "research completed" modals the instant the peer first reached a
    /// map surface (UIStateVehicleSelected, 21:27:49). Nothing was functionally wrong — and the player read
    /// it as "all researches completed at once", which cost a session to misdiagnose.
    ///
    /// THE RULE. Until this peer has stood on a geoscape map surface ONCE, the mirror is still arriving:
    /// completions there are backlog and latch SILENTLY, exactly like the unseeded path. After that first
    /// surface the gate never closes again — a real completion still presents, including one that lands
    /// while the peer is away in Manufacturing (that one defers and opens on return, unchanged).
    ///
    /// NOT A QUORUM: the gate reads THIS peer's own view state and nothing else; no peer waits on a human.
    ///
    /// THE ARMS (all executed against the shipped presenter's own gate):
    ///   (a) <c>pre-surface-announces</c> — a fresh session that has never seen a map surface must SWALLOW
    ///       the completion (the real deferral list stays empty); this is the bug, reproduced.
    ///   (b) <c>surface-does-not-arm</c> — a completion latched on a map surface must reach that list.
    ///   (c) <c>gate-not-sticky</c> — once open it must STAY open when the peer steps off the map, or the
    ///       fix would silently eat the real completions the whole feature exists to announce.
    ///   (d) <c>reset-does-not-rearm</c> — <c>ResearchSync.Reset</c> (a new session) must put the gate back
    ///       to closed, or a second campaign inherits the first one's "already surfaced".
    ///   (e) <c>gate-unwired</c> / <c>surface-unmeasured</c> — IL: <c>PresentFromMirror</c> must reach the
    ///       deferral list through <c>LatchCompletion</c> and must measure the surface with
    ///       <c>DurableWindowRegistry.MayPresent</c>. Arms (a)-(d) drive one method; without this they stay
    ///       green while the presenter walks around it.
    ///
    /// Falsify: drop the surface guard inside LatchCompletion → (a); make it non-sticky → (c); enqueue
    /// straight into the deferral list from PresentFromMirror → (e).
    /// </summary>
    internal static class L475_TheOpeningGrantListIsSilentUntilTheFirstMapSurface
    {
        internal static IEnumerable<string> Check()
        {
            // (a)-(d): drive the shipped latch through both states, and read the REAL deferral list it feeds.
            ResearchSync.Reset();
            bool queued = ResearchSync.LatchCompletion("PX_PhoenixProject", false);
            if (queued || ResearchSync.DeferredCompletionCount != 0)
                yield return "L475 pre-surface-announces: a completion arriving BEFORE this peer ever reached a " +
                             "geoscape map surface was queued for presentation (deferred=" +
                             ResearchSync.DeferredCompletionCount + "). That is the campaign-opening grant list " +
                             "(6 researches in the 2026-08-14 session) queueing six modals for the first frame " +
                             "of the map — the state is right, the client is told the campaign just happened.";

            if (!ResearchSync.LatchCompletion("PX_HavenRecruits", true) ||
                ResearchSync.DeferredCompletionCount != 1)
                yield return "L475 surface-does-not-arm: a completion latched while standing ON a map surface was " +
                             "not queued (deferred=" + ResearchSync.DeferredCompletionCount + "), so a client " +
                             "would never be told about a research again. Silence is not the goal; silence until " +
                             "this peer's first map surface is.";

            if (!ResearchSync.LatchCompletion("PX_NewJericho", false) ||
                ResearchSync.DeferredCompletionCount != 2)
                yield return "L475 gate-not-sticky: the gate closed again once this peer stepped off the map " +
                             "(deferred=" + ResearchSync.DeferredCompletionCount + "). A completion landing while " +
                             "a peer is in Manufacturing must DEFER and open on return (PumpDeferredCompletions); " +
                             "swallowing it loses the announcement the whole latch exists to make.";

            ResearchSync.Reset();
            if (ResearchSync.LatchCompletion("PX_DisciplesOfAnu", false) ||
                ResearchSync.DeferredCompletionCount != 0)
                yield return "L475 reset-does-not-rearm: ResearchSync.Reset left the gate OPEN, so a peer joining " +
                             "a fresh campaign after an earlier session inherits 'already surfaced' and gets the " +
                             "opening grant list as modals again.";
            ResearchSync.Reset(); // leave no test rows in the real deferral list

            // (e): the gate must be in the presenter's path, not merely present in the file.
            const BindingFlags Any = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            var present = typeof(ResearchSync).GetMethod("PresentFromMirror", Any);
            var gate = typeof(ResearchSync).GetMethod("LatchCompletion", Any);
            var mayPresent = typeof(DurableWindowRegistry).GetMethod("MayPresent", Any,
                null, new[] { typeof(bool), typeof(Type) }, null);
            if (present == null || gate == null || mayPresent == null)
            {
                yield return "L475 premise-changed: ResearchSync.PresentFromMirror / .LatchCompletion / " +
                             "DurableWindowRegistry.MayPresent(bool,Type) did not resolve, so this law cannot see " +
                             "the presenter it guards. Re-point it before believing any verdict above.";
                yield break;
            }
            if (!References(present, gate))
                yield return "L475 gate-unwired: PresentFromMirror does not call LatchCompletion — so something " +
                             "else is putting rows into the deferral list, past the one door the gate sits in, " +
                             "and every pre-surface completion is deferred again: the 2026-08-14 modal storm, " +
                             "back verbatim.";
            if (!References(present, mayPresent))
                yield return "L475 surface-unmeasured: PresentFromMirror does not call " +
                             "DurableWindowRegistry.MayPresent. 'This peer reached a map surface' has exactly one " +
                             "definition in this repo (DurableWindowRegistry.MapStates); a second one would drift " +
                             "away from the deferral gate it must agree with.";
        }

        /// <summary>Does <paramref name="m"/>'s IL mention <paramref name="callee"/>? Raw 4-byte metadata
        /// token scan (same shape as L107).</summary>
        private static bool References(MethodBase m, MethodBase callee)
        {
            byte[] il = null;
            try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null || callee == null) return false;
            int token = callee.MetadataToken;
            for (int i = 0; i + 4 <= il.Length; i++)
                if (BitConverter.ToInt32(il, i) == token) return true;
            return false;
        }
    }
}
