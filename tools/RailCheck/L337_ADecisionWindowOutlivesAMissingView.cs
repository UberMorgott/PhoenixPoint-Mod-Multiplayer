using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Utils;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L337 — A MIRRORED RAISE THAT CARRIES A PLAYER DECISION STILL REACHES THE PLAYER ONCE THE VIEW EXISTS,
    /// AND ONE THAT DELIBERATELY WILL NOT IS REPORTED RATHER THAN DROPPED IN SILENCE.
    ///
    /// L107 proved a raise that arrives before its ENTITY waits and is shown. The peer's own SCREEN was the
    /// hole left beside it: <c>GeoModalMirror.RaiseMirrored</c> discarded every raise that landed while this
    /// peer had no live <c>GeoscapeView</c> — a tactical mission, or the mid-load on the way back from one —
    /// on the reasoning that "windows are not replayed after the fact". Two <c>AssetDeployment</c> prompts
    /// went that way inside one session, both at 20:26:31.693 (`multiplayer.log:2381-2382`), while the peer
    /// was returning from a mission: two "where does this soldier go" decisions, gone, with nothing that ever
    /// asks again. L332's own row named this and left it uncovered.
    ///
    /// BOTH HALVES ARE THE LAW ON PURPOSE, and one without the other is worthless:
    ///   • a law that only checked "it waits" would pass if EVERY raise parked — including the stale mission
    ///     brief for a mission the host has since launched, which is a WRONG window, worse than a missing one;
    ///   • a law that only checked "the drop speaks" would pass with the original defect intact, since the
    ///     original defect logged a perfectly clear warning while losing the decision.
    ///
    /// WHAT IS ASSERTED IS THE OUTCOME, EXECUTED — <c>GeoModalMirror.NoViewRefusal</c> and the real
    /// <see cref="ModalParkQueue"/> are pure precisely so this is a run and not an inspection:
    ///   (a) THE VERDICT: the deployment prompt is cleared to wait (null), and every <c>ModalType</c> the
    ///       coverage table declares Mirrored is refused with a NON-BLANK reason that NAMES the window. A
    ///       blank or absent reason is a swallow with a return value;
    ///   (b) THE SURVIVAL: driven through the REAL queue with the view absent, the deployment raise is
    ///       neither shown nor expired and is still parked; the moment the view exists it is SHOWN, same seq,
    ///       same payload. That is the defect, executed, and it is the half the log proves was lost;
    ///   (c) SELF-RELEASING, NEVER A QUORUM (law 91): what releases it is the view, an event that arrives on
    ///       its own — the queue is driven only by a readiness predicate and never by a count of peers, so no
    ///       arm of it can wait on another player acting. Asserted by construction below: the pump's release
    ///       test is answered by THIS peer's own state and the release happens on the first true reading;
    ///   (d) BOUNDED: the non-replayable kind exits IMMEDIATELY with its reason rather than parking forever,
    ///       and the queue still evicts loudly when full, so neither wait can become unbounded;
    ///   (e) THE SEAMS, or every arm above is a pure function nobody calls: <c>NeedsPark</c> must consult
    ///       <c>NoViewRefusal</c> (the wait decision IS this verdict) and <c>SwitchQuery</c> (the ONE view
    ///       probe); <c>RaiseMirrored</c> must consult BOTH, so the drop it takes speaks that same reason and
    ///       is measured against that same probe; <c>PumpParked</c> must consult <c>SwitchQuery</c>, or a
    ///       raise parked for the view would be released into a peer that still has none.
    ///
    /// PREMISE: <c>UIStateAssetDeployment</c> must still be declared Mirrored. If it is not, this peer never
    /// receives that window at all and arm (a)'s "the one kind that carries a decision" is about nothing.
    ///
    /// FALSIFIED BOTH WAYS, verbatim. Reintroduce the drop — <c>NoViewRefusal</c> made to refuse the
    /// deployment prompt too (`if (false) return null;`) → RED, `L337 decision-window-dropped`. The other
    /// way — made to clear everything (`if (true) return null;`) → RED, `L337 one-shot-replayed-late` once
    /// per Mirrored ModalType (AncientSiteAttackBrief, GeoScavengeBrief, FactionSoldierJoin, …). GREEN
    /// restored after each: `laws-run=217/217 known-violations=1`.
    /// ponytail: (e) is an IL callee scan, the same lenient trade L107/L333 make.
    /// </summary>
    internal static class L337_ADecisionWindowOutlivesAMissingView
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>The prompt whose loss started this: an asset-deployment raise exactly as the 0xB7
        /// non-modal arm ships one (ModalType.None, the AssetDeploy shape, the manufactured-aircraft case
        /// where the asset carries no entity ref at all).</summary>
        private static GeoModalMirror.Raise Deployment() => new GeoModalMirror.Raise
        {
            Kind = GeoModalMirror.StateKind.AssetDeployment,
            ModalType = (int)ModalType.None,
            Shape = GeoModalMirror.DataShape.AssetDeploy,
            Ref = "",
            Keys = new[] { "someAircraftDefGuid", "" },
            Num = 1,
            Priority = 0,
        };

        private static GeoModalMirror.Raise Presentation(ModalType t) => new GeoModalMirror.Raise
        {
            Kind = GeoModalMirror.StateKind.Modal,
            ModalType = (int)t,
            Shape = GeoModalMirror.DataShape.None,
            Ref = "",
            Keys = new string[0],
            Priority = 99,
        };

        internal static IEnumerable<string> Check()
        {
            // ── PREMISE: the decision window is still one this peer receives ──
            var deployRule = GeoWindowCoverage.RuleFor(typeof(UIStateAssetDeployment));
            if (deployRule == null || deployRule.Sync != WindowSync.Mirrored)
            {
                yield return "L337 premise-changed: UIStateAssetDeployment is no longer declared Mirrored (" +
                             (deployRule == null ? "undeclared" : deployRule.Sync.ToString()) + "), so no peer " +
                             "receives the one window this law calls a carried decision and every arm below " +
                             "would be asserting things about a raise that never crosses.";
                yield break;
            }

            // ── (a) THE VERDICT, executed on the pure decision ────────────────
            if (GeoModalMirror.NoViewRefusal(Deployment()) != null)
                yield return "L337 decision-window-dropped: an AssetDeployment raise that cannot be shown yet is " +
                             "REFUSED instead of waiting — '" + GeoModalMirror.NoViewRefusal(Deployment()) + "'. " +
                             "That window is the standing prompt for an asset that is already manufactured or " +
                             "recruited and sits UNPLACED until somebody answers; nothing re-asks (all three " +
                             "native raise sites are an acquisition COMPLETING), so a peer that misses it has " +
                             "silently lost a decision — twice in one session, multiplayer.log:2381-2382.";

            var mirroredModals = Enum.GetValues(typeof(ModalType)).Cast<ModalType>()
                .Where(t => GeoWindowCoverage.RuleForModal(t)?.Sync == WindowSync.Mirrored).ToList();
            if (mirroredModals.Count == 0)
            {
                yield return "L337 premise-changed: no ModalType is declared Mirrored any more, so the 'one-shot " +
                             "presentation' half of this law is checking an empty set — a law that passes because " +
                             "it has nothing to look at.";
                yield break;
            }
            foreach (var t in mirroredModals)
            {
                var reason = GeoModalMirror.NoViewRefusal(Presentation(t));
                if (reason == null)
                {
                    yield return "L337 one-shot-replayed-late: the mirrored modal '" + t + "' would be PARKED and " +
                                 "shown after this peer returns to its geoscape. Its copy here is " +
                                 "non-authoritative by construction (dialogHandler null — no button on it runs " +
                                 "anything), so nothing is recovered by replaying it, while a brief for a mission " +
                                 "the host has since launched or cancelled is a WRONG window. Worse than a " +
                                 "missing one, and it holds every later raise behind it in the FIFO queue.";
                    continue;
                }
                if (reason.Trim().Length == 0 || reason.IndexOf(t.ToString(), StringComparison.Ordinal) < 0)
                    yield return "L337 refusal-is-silent: the refusal for '" + t + "' is blank or does not name " +
                                 "the window ('" + reason + "'). A drop nobody can attribute to a window is this " +
                                 "repo's dominant bug class wearing a return value — the whole point of refusing " +
                                 "loudly is that the next session can find WHICH window went missing.";
            }

            // ── (b) THE SURVIVAL, executed on the REAL queue ──────────────────
            // The predicate stands in for GeoModalMirror.SwitchQuery() != null: false = this peer has no live
            // GeoscapeView (tactical / mid-load), true = the view is up and a window can be pushed.
            var q = new ModalParkQueue();
            var shown = new List<string>();
            var expired = new List<string>();
            bool viewUp = false;
            Action pump = () => q.Pump(p => viewUp,
                                       (p, s) => shown.Add(p.Kind + "/" + p.Shape + "/" + p.Num + "@" + s),
                                       (p, s, w) => expired.Add(p.Kind + "@" + s + "/" + w));

            if (q.Park(77u, Deployment()) != null)
                yield return "L337 park-rejected: the FIRST parked raise was evicted on arrival, so a decision " +
                             "window can never wait for the view and this law's whole subject is inert.";
            for (int i = 0; i < ModalParkQueue.MaxBatches - 1; i++) pump();
            if (shown.Count != 0 || q.Count != 1)
                yield return "L337 raised-into-no-view: the deployment prompt was released while this peer still " +
                             "had no GeoscapeView (shown=[" + string.Join(",", shown.ToArray()) + "] parked=" +
                             q.Count + "). Pushing a queued state through a view that does not exist is the NRE " +
                             "the drop was there to avoid — waiting is the fix, not raising anyway.";
            if (expired.Count != 0)
                yield return "L337 decision-window-expired: the queue EXPIRED the deployment prompt inside its " +
                             "own budget of " + ModalParkQueue.MaxBatches + " batches (" +
                             string.Join(",", expired.ToArray()) + "), so a raise announced as surviving the " +
                             "wait does not survive it. (What keeps a whole MISSION from burning that budget is " +
                             "the seam below: PumpParked does not run at all without a view, so a viewless wait " +
                             "costs no batches — assert both or the survival is only true for short absences.)";

            viewUp = true;
            pump();
            if (shown.Count != 1 || shown[0] != "AssetDeployment/AssetDeploy/1@77" || q.Count != 0)
                yield return "L337 decision-window-not-shown: the deployment prompt was not raised once the view " +
                             "existed (shown=[" + string.Join(",", shown.ToArray()) + "] parked=" + q.Count +
                             "). THIS IS THE LAW: a raise the peer could not show YET must still reach the " +
                             "player when it can, released by the VIEW arriving — an event that comes by itself " +
                             "with no other player acting (law 91), unlike the drop it replaces, which needed " +
                             "the player to have been lucky about where he was standing.";

            // ── (d) BOUNDED: the refused kind never enters the queue at all ───
            var bound = new ModalParkQueue();
            string evicted = null;
            for (int i = 0; i <= ModalParkQueue.MaxParked; i++) evicted = bound.Park((uint)(90 + i), Deployment());
            if (evicted == null || bound.Count != ModalParkQueue.MaxParked)
                yield return "L337 wait-unbounded: parking " + (ModalParkQueue.MaxParked + 1) + " view-waiting " +
                             "raises neither capped the queue (count=" + bound.Count + ") nor named what it " +
                             "dropped. A park that can never be released is worse than a drop, and a peer away " +
                             "from its geoscape is exactly the case that stacks them up.";

            // ── (e) THE SEAMS: the verdict and the probe must be the ones that RUN ──
            var mirror = typeof(GeoModalMirror);
            var needsPark = mirror.GetMethod("NeedsPark", All);
            var raiseMirrored = mirror.GetMethod("RaiseMirrored", All);
            var pumpParked = mirror.GetMethod("PumpParked", All);
            var refusal = mirror.GetMethod("NoViewRefusal", All);
            var switchQuery = mirror.GetMethod("SwitchQuery", All);
            if (needsPark == null || raiseMirrored == null || pumpParked == null || refusal == null ||
                switchQuery == null)
            {
                yield return "L337 seam-gone: GeoModalMirror.NeedsPark / RaiseMirrored / PumpParked / " +
                             "NoViewRefusal / SwitchQuery no longer resolves — the verdict proven above is a pure " +
                             "function wired to nothing, which passes this law while the window is dropped again.";
                yield break;
            }
            if (!References(needsPark, refusal))
                yield return "L337 wait-decided-elsewhere: GeoModalMirror.NeedsPark does not call NoViewRefusal, " +
                             "so what actually decides to wait is not the verdict this law just executed. Two " +
                             "places answering 'may this be shown later' is how the drop comes back for one kind " +
                             "while the law keeps passing.";
            if (!References(raiseMirrored, refusal))
                yield return "L337 refusal-unspoken: GeoModalMirror.RaiseMirrored does not call NoViewRefusal, so " +
                             "the drop it takes when there is no view no longer speaks the stated reason — a " +
                             "window vanishing under a generic line is the silent swallow with extra steps.";
            foreach (var m in new[] { needsPark, pumpParked, raiseMirrored })
                if (!References(m, switchQuery))
                    yield return "L337 two-view-probes: GeoModalMirror." + m.Name + " does not go through " +
                                 "SwitchQuery. 'Can this be shown' and 'must this wait' must read the SAME probe " +
                                 "— the game's own _viewSwichQuery, built beside the states stack at " +
                                 "GeoscapeView.cs:334-335 — or a raise gets parked by one answer and dropped by " +
                                 "the other, which loses it exactly as before while both sides look correct.";

            // The pump must not run a release test into a viewless peer: PumpParked reads the probe (asserted
            // just above) and hands ModalParkQueue.Pump nothing but a readiness predicate — no peer count, no
            // roster, no acknowledgement. Asserted structurally because it is the NO-QUORUM property.
            var pumpM = typeof(ModalParkQueue).GetMethod("Pump", All);
            if (pumpM == null || pumpM.GetParameters().Length != 3 ||
                pumpM.GetParameters()[0].ParameterType != typeof(Func<GeoModalMirror.Raise, bool>))
                yield return "L337 release-gate-changed: ModalParkQueue.Pump no longer releases on a single " +
                             "predicate over the raise itself. The release must stay answerable by THIS peer " +
                             "alone — the instant another peer's state could gate it, one player's window would " +
                             "depend on another player acting, which is the quorum this project forbids outright.";
        }

        /// <summary>Does <paramref name="m"/>'s IL mention <paramref name="callee"/>? Raw 4-byte metadata
        /// token scan — see the ponytail note on the class.</summary>
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
