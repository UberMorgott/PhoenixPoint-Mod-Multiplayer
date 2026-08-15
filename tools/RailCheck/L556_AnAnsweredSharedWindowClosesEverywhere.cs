using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L556 — A WINDOW WHOSE ANSWER DISPOSES A SHARED ASSET CLOSES ON EVERY PEER; AN INFORMATIONAL ONE
    /// CLOSES ONLY ON THE PEER WHO DISMISSED IT.
    ///
    /// THE REPORT (2026-08-15): a player accepted a soldier offered at a haven and the offer window stayed
    /// open on every other peer — an offer that had already been taken, still asking to be taken again. The
    /// same shape was reported for the asset-distribution prompt ("where do I send the aircraft I bought").
    /// The deployment-preparation screen did NOT have the defect, and the difference was the whole bug: it
    /// is the one family in <c>WindowJournal.FamilyScope</c>, so it alone reached the host-minted void.
    /// Every other window is <c>UIStateGeoModal</c>, one LOCAL family, so no void was ever minted for any
    /// of them whatever their answer did.
    ///
    /// THE RULE IS DERIVED, NEVER LISTED, and this law is what forbids the list. A per-<c>ModalType</c> or
    /// per-family table would be stale the day the game, a DLC or TFTV adds a window; the property is asked
    /// of the RAISE — i.e. of the payload the wire already carries — so a window nobody has written yet is
    /// classified the day it ships.
    ///
    /// TWO INDEPENDENT ARMS, both of them the rail's own grammar:
    ///   • <c>DataShape.AssetDeploy</c> — the shape IS "put this asset somewhere", and its answer is
    ///     <c>GeoPhoenixFaction.DeployAsset</c> whichever half of the bind is filled. Its entity ref is
    ///     OPTIONAL (a manufactured aircraft is named by a def, not by a GeoCharacter), which is exactly why
    ///     <c>NamesEntity</c> alone cannot carry this question.
    ///   • a raise that NAMES a ROSTER ASSET — the payload class rides in <c>Keys[0]</c> for the generic
    ///     EntityRef arm (<c>GeoModalMirror.Describe</c>:310) precisely so a peer can check it, and a
    ///     GeoCharacter / GeoVehicle is a thing the campaign OWNS AND MOVES. Accepting one is a disposition
    ///     taken once for everybody.
    ///
    /// AND WHAT IT MUST LEAVE ALONE. A raise that names a MAP FEATURE the world owns anyway is SHOWING it,
    /// not deciding about it: <c>ModalType.PandoranRevealResult</c>'s payload is <c>RevealedSites[0]</c>, a
    /// GeoSite, and <c>GeoscapeView.ModalResultCallback</c>:843-845 answers it with an empty <c>break</c> —
    /// there is nothing for a void to be the consequence of, and the coverage declaration says so in words
    /// ("INFORMATIONAL: each peer dismisses its own copy and nothing closes anyone else's"). Research
    /// completion, the diplomacy brief and a null payload are informational by SHAPE and were already out.
    /// The per-peer answer class (mission brief / outcome, <c>GeoWindowCoverage.IsPerPeerAnswer</c>, derived
    /// from the game's own <c>GetMissionBriefModal</c>) is out by construction: one player declining is "I
    /// am busy", never "cancelled for everyone".
    ///
    /// THE VOID NAMES THE ANSWERED RAISE, NOT A FAMILY. <c>WindowJournal.FindUnread(family)</c> takes the
    /// FIRST unread entry of a NAME, so with two unread windows of one family it voids the wrong one — a
    /// latent hazard while exactly one family could mint a void, and a live one the moment every shared
    /// window can. Every raise has carried a per-raise tag since <c>4c3d278</c>
    /// (<c>WindowQueueSync.RaiseTagFor</c> over the host journal position), so the void is minted off THAT.
    ///
    /// AND IT CLOSES AN ALREADY-OPEN COPY. Voiding only pruned the UNREAD backlog, which is not where the
    /// reported window was: the other peers had already READ it — it was on their screens. So the void has
    /// to reach the open copy too, through the game's own exit and nothing else.
    ///
    /// ARMS:
    ///   (a) shared-asset-is-not-global — EXECUTED: an EntityRef raise naming a GeoCharacter is GLOBAL.
    ///   (b) asset-deploy-is-not-global — EXECUTED: an AssetDeploy raise with NO entity ref (the aircraft
    ///       case) is GLOBAL, so the arm cannot be folded back into NamesEntity.
    ///   (c) informational-is-global — EXECUTED, the other direction so the predicate cannot be a constant:
    ///       a GeoSite EntityRef (the Pandoran reconnaissance window), ResearchComplete, DiplomacyReward,
    ///       None and Unsupported are all LOCAL.
    ///   (d) per-peer-answer-is-global — EXECUTED: the mission brief/outcome class stays per-peer even over
    ///       a payload that would otherwise be global.
    ///   (e) deploy-prep-regression — EXECUTED: the one declared GLOBAL family still is one, so the live
    ///       deployment-preparation void path is not traded away for the new one.
    ///   (f) derivation-is-not-a-list — DismissalIsGlobal reaches AnswerDisposesSharedAsset reaches
    ///       NamesEntity, so it shares the repo's ONE definition of "this payload names a rail entity"
    ///       instead of opening a second opinion; and no method anywhere names a ModalType constant AND
    ///       calls DismissalIsGlobal.
    ///   (g) void-named-by-family — the imprecise family-keyed minter is gone and the call-site helper takes
    ///       a STATE (whose raise tag names ONE raise), so two unread entries of one family cannot be
    ///       confused.
    ///   (h) answer-does-not-void — the two host answer seams reach the minter: WindowQueueSync.TakeQueued
    ///       (the held-window answer, both channels) and the FinishQueriedState capture (the on-screen one).
    ///   (i) open-copy-survives-the-void — GeoModalMirror.ApplyVoidLocally reaches
    ///       WindowQueueSync.CloseVoidedRaise. Without it a void prunes the backlog and the window the
    ///       report was about — already read, already on screen — stays up.
    ///
    /// NO QUORUM. The void travels one way and waits for nobody: the host mints it as it runs the answer,
    /// every peer applies what it is sent, and a peer that never answers anything is never waited on.
    ///
    /// ROLES SEPARATED (§C.3): (a)-(e) are role-free pure calls; (f)-(i) are statements about the shipped
    /// assembly, and the minting half of every one of them is HOST-only by HostMayPublish.
    ///
    /// Falsify (compile-valid src mutations, each named): drop the AssetDeploy arm from
    /// AnswerDisposesSharedAsset → (b); make it NamesEntity alone → (a) stays green and (c) goes RED on the
    /// GeoSite; drop the !perPeerAnswer conjunct → (d); bring HostVoidFamily(string) back and call it from
    /// DeploymentWindowClose → (g); drop the CloseVoidedRaise call from ApplyVoidLocally → (i).
    /// </summary>
    internal static class L556_AnAnsweredSharedWindowClosesEverywhere
    {
        private const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public |
                                         BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        /// <summary>The one raise shape the arms below vary, spelled once so no arm can drift from another.</summary>
        private static GeoModalMirror.Raise Raise(GeoModalMirror.DataShape shape, string @ref, string cls) =>
            new GeoModalMirror.Raise
            {
                Shape = shape,
                Ref = @ref,
                Keys = cls == null ? new string[0] : new[] { cls },
            };

        internal static IEnumerable<string> Check()
        {
            var mirror = typeof(GeoModalMirror);
            var dismissal = mirror.GetMethod("DismissalIsGlobal", Any);
            var disposes = mirror.GetMethod("AnswerDisposesSharedAsset",
                                            Any, null, new[] { typeof(GeoModalMirror.Raise) }, null);
            var namesEntity = mirror.GetMethod("NamesEntity", Any);
            if (dismissal == null || disposes == null || namesEntity == null)
            {
                yield return "L556 premise-changed: GeoModalMirror." +
                             (dismissal == null ? "DismissalIsGlobal" : disposes == null
                                 ? "AnswerDisposesSharedAsset(Raise)" : "NamesEntity") +
                             " does not exist, so NOTHING derives whether an answered window closes on the " +
                             "other peers. Every UIStateGeoModal is one LOCAL family, so with no derivation " +
                             "the only window that ever closes everywhere is the deployment-preparation " +
                             "screen — which is exactly the reported defect: a soldier accepted at a haven " +
                             "leaves the offer open, and already taken, on every other peer.";
                yield break;
            }

            string character = typeof(PhoenixPoint.Geoscape.Entities.GeoCharacter).FullName;
            string site = typeof(PhoenixPoint.Geoscape.Entities.GeoSite).FullName;

            // (a) A ROSTER ASSET IS DISPOSED ONCE, FOR EVERYBODY.
            if (!GeoModalMirror.DismissalIsGlobal("UIStateGeoModal",
                    Raise(GeoModalMirror.DataShape.EntityRef, "U#1", character), false))
                yield return "L556 shared-asset-is-not-global: a raise naming a GeoCharacter is not " +
                             "globally dismissed. Accepting a soldier offered at a haven runs " +
                             "reward.Apply on the host for the WHOLE campaign, so the offer has been taken " +
                             "and every other peer's copy is asking for a decision that no longer exists.";

            // (b) AND THE SHAPE THAT NAMES NO ENTITY AT ALL. An aircraft bind has no GeoCharacter, so this
            // arm is what keeps NamesEntity from being mistaken for the whole answer.
            if (!GeoModalMirror.DismissalIsGlobal("UIStateGeoModal",
                    Raise(GeoModalMirror.DataShape.AssetDeploy, "", null), false))
                yield return "L556 asset-deploy-is-not-global: an AssetDeploy raise with no entity ref — a " +
                             "manufactured AIRCRAFT, named by a def and not by a GeoCharacter — is not " +
                             "globally dismissed. Its answer is GeoPhoenixFaction.DeployAsset either way, " +
                             "so the prompt asking where to send it must vanish once anybody has sent it.";

            // (c) THE OTHER DIRECTION, five shapes, so the predicate cannot be a constant.
            var informational = new[]
            {
                Tuple.Create(Raise(GeoModalMirror.DataShape.EntityRef, "S#1", site),
                             "a GeoSite EntityRef — ModalType.PandoranRevealResult's payload is " +
                             "RevealedSites[0] and GeoscapeView.ModalResultCallback:843-845 answers it with " +
                             "an empty break. Nothing is decided, so nothing may be closed for anyone else"),
                Tuple.Create(Raise(GeoModalMirror.DataShape.ResearchComplete, "F#1", null),
                             "a research-completed announcement — ResearchCompleteModalHandler:2108 only " +
                             "navigates THIS peer's own view"),
                Tuple.Create(Raise(GeoModalMirror.DataShape.DiplomacyReward, "F#1", null),
                             "a diplomacy research brief — a NULL DialogCallback in the shipped game"),
                Tuple.Create(Raise(GeoModalMirror.DataShape.None, "", null),
                             "a window with no payload at all"),
                Tuple.Create(Raise(GeoModalMirror.DataShape.Unsupported, "", null),
                             "a payload this build cannot describe, which must get the SAFE verdict and " +
                             "never global dismissal on the strength of ignorance"),
            };
            foreach (var t in informational)
                if (GeoModalMirror.DismissalIsGlobal("UIStateGeoModal", t.Item1, false))
                    yield return "L556 informational-is-global: " + t.Item2 + " is globally dismissed. An " +
                                 "informational window is dismissed by each player individually; closing it " +
                                 "under them takes away something they were still reading.";

            // (d) THE PER-PEER ANSWER CLASS IS OUT BY CONSTRUCTION.
            if (GeoModalMirror.DismissalIsGlobal("UIStateGeoModal",
                    Raise(GeoModalMirror.DataShape.EntityRef, "U#1", character), true))
                yield return "L556 per-peer-answer-is-global: a window of the per-peer answer class (the " +
                             "mission brief and its outcome sibling) is globally dismissed. Every peer " +
                             "answers that one for itself — one player declining means 'I am busy', and " +
                             "closing it everywhere is that decline taken for the whole team.";

            // (e) THE LIVE PATH DOES NOT REGRESS.
            if (!GeoModalMirror.DismissalIsGlobal("UIStateRosterDeployment",
                    Raise(GeoModalMirror.DataShape.None, "", null), false))
                yield return "L556 deploy-prep-regression: the deployment-preparation screen stopped being " +
                             "globally dismissed. Its state carries NO describable payload, so no per-raise " +
                             "derivation can classify it and WindowJournal's declaration table is the arm " +
                             "that still must — the new derivation ADDS to that table, it does not replace " +
                             "it.";

            var asm = mirror.Assembly;

            // (f) ONE DEFINITION, NOT A SECOND OPINION — and no list anywhere.
            if (!Il.References(dismissal, disposes) || !Il.References(disposes, namesEntity))
                yield return "L556 derivation-is-not-a-list: DismissalIsGlobal does not reach NamesEntity " +
                             "through AnswerDisposesSharedAsset, so it is deciding 'is this payload about a " +
                             "shared thing' with its own opinion instead of the repo's one definition. Two " +
                             "definitions of one property drift, and the drift is silent.";
            var modalNames = Enum.GetNames(typeof(PhoenixPoint.Common.Utils.ModalType));
            var listers = asm.GetTypes()
                .SelectMany(t => t.GetMethods(Any).Cast<MethodBase>().Concat(t.GetConstructors(Any)))
                .Where(m => Il.References(m, dismissal) && Il.MentionsAnyString(m, modalNames))
                .Select(m => m.DeclaringType.Name + "." + m.Name)
                .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (listers.Count > 0)
                yield return "L556 derivation-is-not-a-list: " + string.Join(", ", listers) + " name(s) a " +
                             "ModalType constant AND call DismissalIsGlobal — a per-window table growing " +
                             "back around the derivation. A window the game, a DLC or TFTV adds tomorrow " +
                             "must be classified with no edit anywhere.";

            // (g) THE VOID NAMES ONE RAISE.
            if (mirror.GetMethod("HostVoidFamily", Any) != null)
                yield return "L556 void-named-by-family: GeoModalMirror.HostVoidFamily(string) is back. A " +
                             "family NAME cannot pick between two unread entries of that family — " +
                             "WindowJournal.FindUnread takes the FIRST — so it would void a window nobody " +
                             "answered while leaving the answered one up.";
            var voidRaise = mirror.GetMethod("HostVoidRaise", Any);
            if (voidRaise == null || voidRaise.GetParameters().Length != 1 ||
                voidRaise.GetParameters()[0].ParameterType != typeof(object))
                yield return "L556 void-named-by-family: GeoModalMirror.HostVoidRaise(object state) does " +
                             "not exist. The void must be minted off the ANSWERED WINDOW, whose per-raise " +
                             "tag names exactly one journal position, and never off a family name.";

            // (h) THE ANSWER SEAMS MINT IT.
            var queue = asm.GetTypes().FirstOrDefault(t => t.Name == "WindowQueueSync");
            var takeQueued = queue == null ? null : queue.GetMethod("TakeQueued", Any);
            var finishCapture = asm.GetTypes().FirstOrDefault(t => t.Name == "FinishQueriedStateCapture");
            if (voidRaise != null)
            {
                if (takeQueued == null || !Il.References(takeQueued, voidRaise))
                    yield return "L556 answer-does-not-void: WindowQueueSync.TakeQueued does not reach " +
                                 "HostVoidRaise. That is the ONE spelling both answer channels use for a " +
                                 "window the host was holding queued, so an answer taken there would close " +
                                 "nobody else's copy.";
                if (finishCapture == null ||
                    !finishCapture.GetMethods(Any).Cast<MethodBase>().Any(m => Il.References(m, voidRaise)))
                    yield return "L556 answer-does-not-void: the FinishQueriedState capture does not reach " +
                                 "HostVoidRaise. That is the host's own chokepoint for the window that is " +
                                 "ON SCREEN — its own click and the relayed answer it runs through " +
                                 "FinishDialog both leave through it.";
            }

            // (i) AND THE OPEN COPY GOES.
            var applyVoid = mirror.GetMethod("ApplyVoidLocally", Any);
            var closeVoided = queue == null ? null : queue.GetMethod("CloseVoidedRaise", Any);
            if (applyVoid == null || closeVoided == null || !Il.References(applyVoid, closeVoided))
                yield return "L556 open-copy-survives-the-void: GeoModalMirror.ApplyVoidLocally does not " +
                             "reach WindowQueueSync.CloseVoidedRaise. WindowJournal.ApplyVoid removes an " +
                             "UNREAD entry, and the window in the report was not unread — it had been read " +
                             "and was on the player's screen. A void that only prunes the backlog leaves " +
                             "exactly the copy it was minted to close.";
        }
    }
}
