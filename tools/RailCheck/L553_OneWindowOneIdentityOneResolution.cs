using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L553 — EVERY LIVE WINDOW HAS ITS OWN IDENTITY, AND EVERY ANSWER CHANNEL RESOLVES ITS TARGET THE
    /// SAME WAY.
    ///
    /// TWO REPORTS, 2026-08-15, both of them about the answer path and neither about any one window.
    ///
    /// (1) AN IDENTITY THAT IS NOT UNIQUE. <c>WindowQueueSync.Identity</c> was the window KIND plus the
    /// 0xB7 payload, and such a string is only as unique as the payload happens to be. The asset-deploy
    /// payload's <c>Ref</c> is OPTIONAL BY CONSTRUCTION — a manufactured aircraft is named by a def, not
    /// by a <c>GeoCharacter</c> (GeoModalMirror.cs:349, which contradicted the "RootRef always names one"
    /// comment at :289) — so TWO manufactured aircraft produced the identical string
    /// <c>AssetDeployment|AssetDeploy||&lt;guid&gt;,&lt;guid&gt;</c>. A stale copy answered later then
    /// validated against the NEWER window and the host deployed the WRONG asset: a wrong result, which is
    /// worse than a lost one and cannot be seen in any log.
    ///
    /// (2) ONE CHANNEL KNEW ABOUT THE QUEUE AND ITS SIBLING DID NOT. <c>HandleAdvance</c> answers a window
    /// that is still QUEUED (<c>AnswerQueued</c>, the L176 arm), because <c>WindowOrder.HoldsForOpenScreen</c>
    /// keeps a raised window in <c>_viewStateSwitchRequests</c> while the peer is inside a screen it opened.
    /// <c>HandleDeploy</c> read <c>_currentStateSwitchRequest</c> and nothing else — and an asset-deployment
    /// prompt is raised FROM a screen (manufacturing), so on the host it is normally queued and never
    /// current. Every deploy answer was refused, the transport was never placed, and the prompt wedged on
    /// every peer.
    ///
    /// THE TWO GENERAL RULES, which is what this law asserts — never a verdict about aircraft:
    ///   • EVERY RAISED WINDOW IS DISTINGUISHABLE FROM EVERY OTHER LIVE ONE, whatever its payload resolves
    ///     to. The discriminator is the HOST'S JOURNAL POSITION of the raise (<c>WindowJournal
    ///     .MintHostPosition</c> — monotonic, never reused, minted at the one seam every pushed window
    ///     passes and already shipped in every raise as <c>Raise.JournalPos</c>), carried to the live
    ///     window on both peers by <c>WindowQueueSync.TagRaise</c>. NOT a new id scheme and NOT a table:
    ///     a per-window list would be stale the day the game, a DLC or another mod adds a window, and its
    ///     staleness would be SILENT — the unlisted window would simply share somebody else's name.
    ///   • EVERY INBOUND ANSWER CHANNEL RESOLVES ITS TARGET THROUGH ONE FUNCTION
    ///     (<c>WindowQueueSync.ResolveAnswerTarget</c>), so no channel can drop an answer its sibling
    ///     would have accepted. Arm (c) is what makes a THIRD channel that reads only the current slot
    ///     RED on the day it is written rather than on the day a player loses a transport.
    ///
    /// AND THE UNCLASSIFIABLE WINDOW IS REFUSED LOUDLY, not silently: a window with no tag gets NO
    /// identity at all (arm (a)), which is the one verdict that can never answer somebody else's window,
    /// and the send sites announce which of the two refusals they hit (<c>IdentityRefusal</c>).
    ///
    /// ANSWER-ONCE (arm (e)). With a queued arm on both channels, an answer must not be applicable twice.
    /// It cannot be, structurally and with no second ledger (L523): the window leaves the host's pending
    /// list in <c>TakeQueued</c> BEFORE its consequence runs, the current arm is cleared by the game's own
    /// <c>FinishQueriedState</c>, and an identity now names ONE RAISE — so a second peer's stale answer
    /// names a window that no longer exists and is refused in words by <c>ValidateIdentity</c>.
    ///
    /// ROLES SEPARATED (§C.3): (a), (d) and (f) are role-free pure calls; (b), (c) and (e) are statements
    /// about the shipped assembly. Nothing here waits on a peer — no quorum.
    ///
    /// Falsify (compile-valid src mutations, each named and each verified RED then restored):
    ///   drop the tag from the identity — <c>Identity(kind, p, tag) =&gt; kind + "|" + p.Shape + "|" +
    ///   p.Ref + "|" + string.Join(",", p.Keys)</c>, the literal pre-fix spelling → (a)
    ///   <c>identity-is-not-unique</c>;
    ///   make <c>HandleDeploy</c> read the current slot alone (the pre-fix body) → (c)
    ///   <c>channel-skips-the-queue</c>;
    ///   delete the <c>TagRaise</c> call from the coverage gate → (b) <c>raise-is-untagged</c>;
    ///   delete the <c>TakeQueued</c> call from either queued arm → (e) <c>answer-may-apply-twice</c>.
    /// </summary>
    internal static class L553_OneWindowOneIdentityOneResolution
    {
        private const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public |
                                         BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check()
        {
            var asm = typeof(WindowQueueSync).Assembly;
            var sync = typeof(WindowQueueSync);
            var identity = sync.GetMethod("Identity", Any);
            var identityOf = sync.GetMethod("IdentityOf", Any);
            var raiseTagOf = sync.GetMethod("RaiseTagOf", Any);
            var raiseTagFor = sync.GetMethod("RaiseTagFor", Any);
            var tagRaise = sync.GetMethod("TagRaise", Any);
            var resolve = sync.GetMethod("ResolveAnswerTarget", Any);
            var takeQueued = sync.GetMethod("TakeQueued", Any);
            var registerIntents = sync.GetMethod("RegisterIntents", Any);
            var answerQueued = sync.GetMethod("AnswerQueued", Any);
            var handleDeploy = sync.GetMethod("HandleDeploy", Any);
            var gate = asm.GetTypes().FirstOrDefault(t => t.Name == "GeoWindowCoverageGate")
                         ?.GetMethod("Postfix", Any);
            var raiseMirrored = asm.GetTypes().FirstOrDefault(t => t.Name == "GeoModalMirror")
                                  ?.GetMethod("RaiseMirrored", Any);

            if (identity == null || identityOf == null || raiseTagOf == null || raiseTagFor == null ||
                tagRaise == null || resolve == null || takeQueued == null || registerIntents == null ||
                answerQueued == null || handleDeploy == null || gate == null || raiseMirrored == null)
            {
                yield return "L553 premise-changed: one of WindowQueueSync.{Identity,IdentityOf,RaiseTagOf," +
                             "RaiseTagFor,TagRaise,ResolveAnswerTarget,TakeQueued,RegisterIntents," +
                             "AnswerQueued,HandleDeploy}, GeoWindowCoverageGate.Postfix or " +
                             "GeoModalMirror.RaiseMirrored no longer resolves. Both halves of the 2026-08-15 " +
                             "answer report ride these members: a window that cannot be told apart from " +
                             "another is answered WRONGLY, and a channel that cannot see the queue answers " +
                             "NOTHING at all.";
                yield break;
            }

            // ── (a) THE IDENTITY IS UNIQUE PER RAISE, EXECUTED over the payload space ────────────────
            // The measured collision itself: the SAME payload — the aircraft case, whose Ref is empty by
            // construction — raised twice. Two live windows, two identities, or the wrong asset is deployed.
            var payload = Deploy("");
            string first = Id(identity, payload, "j7");
            string second = Id(identity, payload, "j8");
            if (first == null || second == null)
                yield return "L553 identity-is-constant: a tagged, describable payload got NO identity, so " +
                             "this arm cannot show anything and no window on this build can be answered at all.";
            else if (first == second)
                yield return "L553 identity-is-not-unique: two SEPARATE raises of a structurally identical " +
                             "window ('" + first + "') share one identity string. That is the measured " +
                             "defect: two manufactured aircraft named the same window, a stale copy answered " +
                             "later validated against the NEWER prompt, and the host deployed the WRONG " +
                             "asset. Uniqueness must come from the RAISE, because the payload cannot supply " +
                             "it — an asset-deploy ref is empty by construction.";
            if (Id(identity, payload, "j7") != first)
                yield return "L553 identity-disagrees-with-itself: the same raise described twice yielded two " +
                             "identities, so the host and the peer holding one window would name it " +
                             "differently and every answer would be refused as 'a different INSTANCE'.";

            // TOTALITY AND THE SAFE DEFAULT, over every shape the codec ships and both ref cases: an
            // untagged window — this peer's own, or a raise that claimed no journal position — must get NO
            // identity rather than a shared one, and nothing may throw on a click.
            string threw = null;
            bool sawNamed = false;
            foreach (var shape in Enum.GetValues(typeof(GeoModalMirror.DataShape)).Cast<object>())
                foreach (var reference in new[] { "S#1", "", null })
                    foreach (var tag in new[] { "j1", "", null })
                    {
                        string got;
                        try
                        {
                            got = Id(identity, new GeoModalMirror.Raise
                            {
                                Shape = (GeoModalMirror.DataShape)shape,
                                Ref = reference,
                                Keys = new[] { "k" },
                            }, tag);
                        }
                        catch (Exception ex)
                        { threw = threw ?? (shape + "/" + (tag ?? "<null>") + ": " + ex.GetType().Name); continue; }
                        if (got != null && string.IsNullOrEmpty(tag))
                            yield return "L553 untagged-window-is-nameable: a window with NO per-raise tag " +
                                         "was given the identity '" + got + "'. An untagged window is either " +
                                         "this peer's own or a raise no journal position was claimed for — " +
                                         "in both cases no other peer holds that instance, so naming it lets " +
                                         "an answer land on somebody else's window. The safe verdict is null.";
                        if (got != null) sawNamed = true;
                    }
            if (threw != null)
                yield return "L553 identity-is-not-total: the identity derivation threw on payload '" + threw +
                             "'. Every reachable window must get a verdict — a shape that throws is an " +
                             "exception on a mouse click.";
            if (!sawNamed)
                yield return "L553 identity-is-constant: NO payload at all yields an identity, so the " +
                             "derivation is a constant null and no peer can answer any window ever again.";
            if (Id(identity, new GeoModalMirror.Raise
                { Shape = GeoModalMirror.DataShape.Unsupported, Ref = "S#1", Keys = new string[0] }, "j1") != null)
                yield return "L553 unshipped-payload-is-nameable: a payload the 0xB7 raise has no shape for " +
                             "was named anyway. That window never crossed, so the name it was given exists " +
                             "on exactly one screen.";

            // ── (b) THE TAG COMES FROM THE RAISE, ON BOTH SIDES ─────────────────────────────────────
            if (!Il.References(identityOf, raiseTagOf))
                yield return "L553 identity-ignores-the-raise: WindowQueueSync.IdentityOf does not read the " +
                             "per-raise tag, so it is naming windows out of the payload alone again — which " +
                             "is exactly as unique as the payload happens to be.";
            foreach (var site in new[] { new { M = gate, Where = "the host's coverage gate" },
                                         new { M = raiseMirrored, Where = "the peer's mirrored raise" } })
            {
                if (!Il.References(site.M, tagRaise))
                    yield return "L553 raise-is-untagged: " + site.Where + " does not call " +
                                 "WindowQueueSync.TagRaise, so the window it produces carries no raise tag. " +
                                 "One side untagged means every identity comparison fails and no answer ever " +
                                 "applies; BOTH sides untagged means two identical windows share a name again.";
                if (!Il.References(site.M, raiseTagFor))
                    yield return "L553 tag-is-not-derived: " + site.Where + " does not derive its tag through " +
                                 "WindowQueueSync.RaiseTagFor, so it is minting an id of its own. Two id " +
                                 "schemes cannot agree, and the host and the peer must spell ONE string for " +
                                 "one window.";
            }
            // The derivation itself, executed: position 0 means 'this raise claimed no position' and is NOT
            // a tag; two positions are two tags.
            if (WindowQueueSync.RaiseTagFor(0) != null)
                yield return "L553 tag-is-not-derived: journal position 0 — a raise the host never queued — " +
                             "was given a tag. That position is shared by every such raise, so it would make " +
                             "them all one window.";
            if (WindowQueueSync.RaiseTagFor(7) == WindowQueueSync.RaiseTagFor(8) ||
                WindowQueueSync.RaiseTagFor(7) != WindowQueueSync.RaiseTagFor(7))
                yield return "L553 tag-is-not-derived: RaiseTagFor is not a stable injection over journal " +
                             "positions, so the one number that IS unique per raise stops carrying that " +
                             "property into the identity.";

            // ── (c) EVERY ANSWER CHANNEL RESOLVES THE SAME WAY ──────────────────────────────────────
            var handlers = Il.ReferencedMethods(registerIntents)
                .Where(m => m.DeclaringType == sync && IsOpHandler(m))
                .Distinct().ToArray();
            if (handlers.Length < 2)
                yield return "L553 premise-changed: WindowQueueSync.RegisterIntents names " + handlers.Length +
                             " op handler(s) of the 0x" + SurfaceIds.GeoWindowIntent.ToString("X2") +
                             " surface, so arm (c) cannot compare channels and would pass while a channel " +
                             "answers nothing.";
            foreach (var handler in handlers.OrderBy(m => m.Name, StringComparer.Ordinal))
                if (!Il.References(handler, resolve))
                    yield return "L553 channel-skips-the-queue: the answer channel " + handler.Name +
                                 " does not resolve its target through WindowQueueSync.ResolveAnswerTarget, " +
                                 "so it is deciding on its own which of the host's windows an answer names. " +
                                 "That is the measured defect: HandleDeploy read _currentStateSwitchRequest " +
                                 "alone, and a host inside the manufacturing screen — where the prompt is " +
                                 "raised FROM — refused every deploy answer, so the transport was never " +
                                 "placed and the prompt wedged on every peer.";

            // ── (d) THE RESOLUTION ITSELF, EXECUTED ─────────────────────────────────────────────────
            const string want = "AssetDeployment|AssetDeploy||g1,g2|j7";
            const string other = "AssetDeployment|AssetDeploy||g1,g2|j8";
            if (WindowQueueSync.ResolveAnswerTarget(want, new[] { other }, want) !=
                WindowQueueSync.TargetCurrent)
                yield return "L553 current-window-unanswerable: the window ON SCREEN, named exactly, is not " +
                             "resolved as the current target. The game's own exit funnel would never run.";
            if (WindowQueueSync.ResolveAnswerTarget(null, new[] { other, want }, want) != 1)
                yield return "L553 channel-skips-the-queue: a window the host holds in " +
                             "_viewStateSwitchRequests, named exactly, with nothing on screen, is not " +
                             "resolved at all. A host inside any screen of its own would refuse every " +
                             "answer — the report verbatim.";
            if (WindowQueueSync.ResolveAnswerTarget(null, new[] { other }, want) !=
                WindowQueueSync.TargetNone)
                yield return "L553 answer-lands-on-another-window: an answer naming a window this host does " +
                             "NOT hold resolved to one it does. The identity is the whole safety property of " +
                             "this surface: a stale answer must be refused, never re-aimed.";
            if (WindowQueueSync.ResolveAnswerTarget(want, new[] { want }, want) !=
                WindowQueueSync.TargetCurrent)
                yield return "L553 answer-lands-on-another-window: with the SAME identity current and queued, " +
                             "the resolution did not prefer the current one. Two funnels would answer one " +
                             "window and the loser leaves a dead _currentStateSwitchRequest wedging the queue.";
            if (WindowQueueSync.ResolveAnswerTarget(null, null, want) != WindowQueueSync.TargetNone ||
                WindowQueueSync.ResolveAnswerTarget(null, new string[0], null) != WindowQueueSync.TargetNone)
                yield return "L553 answer-lands-on-another-window: an empty answer or an empty queue did not " +
                             "resolve to 'nothing here answers that'. A nameless answer must never resolve.";

            // ── (e) ANSWER-ONCE: BOTH QUEUED ARMS TAKE THE WINDOW OUT FIRST ─────────────────────────
            foreach (var arm in new[] { answerQueued, handleDeploy })
                if (!Il.References(arm, takeQueued))
                    yield return "L553 answer-may-apply-twice: " + arm.Name + " does not remove the answered " +
                                 "window through WindowQueueSync.TakeQueued, so the host's queue still holds " +
                                 "a window whose decision has been taken. A second peer's stale answer would " +
                                 "find it and apply the consequence again — a second DeployAsset, a second " +
                                 "LaunchMission — and the same window would be offered to this host later.";

            // ── (f) NO PER-WINDOW LIST CAN HIDE IN THE DERIVATION ───────────────────────────────────
            // The signature is the proof, as in L552 arm (c): Identity takes the PAYLOAD and the TAG, and
            // ResolveAnswerTarget takes IDENTITY STRINGS. Neither can express "if this is the aircraft
            // window, do something else", so the engine classifies a window nobody has written yet.
            var identityParams = identity.GetParameters();
            if (identityParams.Length != 3 || identityParams[0].ParameterType != typeof(string) ||
                identityParams[1].ParameterType != typeof(GeoModalMirror.Raise) ||
                identityParams[2].ParameterType != typeof(string) || identity.ReturnType != typeof(string))
                yield return "L553 identity-is-not-derived: WindowQueueSync.Identity(string, Raise, string) " +
                             "did not resolve. Identity must be derived from the raise a window came from — " +
                             "a signature that takes the payload and the tag cannot hold a per-window table, " +
                             "which is the point: a window the game, a DLC or another mod adds tomorrow is " +
                             "named the day it ships, with no edit here.";
            var resolveParams = resolve.GetParameters();
            if (resolveParams.Length != 3 || resolveParams[0].ParameterType != typeof(string) ||
                resolveParams[1].ParameterType != typeof(IList<string>) ||
                resolveParams[2].ParameterType != typeof(string) || resolve.ReturnType != typeof(int))
                yield return "L553 resolution-is-not-derived: " +
                             "WindowQueueSync.ResolveAnswerTarget(string, IList<string>, string) did not " +
                             "resolve. The resolution must see IDENTITIES and nothing else — a signature " +
                             "that takes view states or ModalTypes is one a channel can special-case, and a " +
                             "special case is how the two channels drifted apart in the first place.";
        }

        private static string Id(MethodInfo identity, GeoModalMirror.Raise payload, string tag) =>
            (string)identity.Invoke(null, new object[] { "AssetDeployment", payload, tag });

        /// <summary>The measured payload: an asset-deploy raise whose entity ref is EMPTY, which is the
        /// normal case for a manufactured aircraft and the reason the payload cannot supply uniqueness.</summary>
        private static GeoModalMirror.Raise Deploy(string reference) => new GeoModalMirror.Raise
        {
            Shape = GeoModalMirror.DataShape.AssetDeploy,
            Ref = reference,
            Keys = new[] { "g1", "g2" },
        };

        /// <summary>An inbound answer channel: the rail's own op-handler shape
        /// (<c>IntentRail.OpHandler</c>).</summary>
        private static bool IsOpHandler(MethodBase m)
        {
            var p = m.GetParameters();
            return p.Length == 5 && p[0].ParameterType == typeof(NetworkEngine) &&
                   p[1].ParameterType == typeof(ulong) && p[2].ParameterType == typeof(uint) &&
                   p[3].ParameterType == typeof(byte) && p[4].ParameterType == typeof(BinaryReader);
        }
    }
}
