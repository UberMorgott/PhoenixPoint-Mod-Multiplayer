using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.View;

namespace RailCheck
{
    /// <summary>
    /// L124 — TWO WINDOWS CAUSED BY TWO RAIL MESSAGES OPEN IN THE SAME ORDER ON EVERY PEER, AND THE
    /// CAMPAIGN INTRO IS A SHARED MOMENT.
    ///
    /// ─── PART A: THE CROSS-SURFACE ORDINAL ───────────────────────────────────────────────
    /// THE REPORT (2026-08-05). A research completion and a geoscape event left the host together; a client
    /// received them 384 ms apart and showed them in the OPPOSITE order. Contents were correct on every
    /// peer — only the sequence differed. The client's event window rides the synchronous 0xB6 raise
    /// (<c>EventPopup</c>, postfix on <c>GeoscapeView.OnGeoscapeEventRaised</c>); its research window is
    /// produced LOCALLY inside the 0xAC value-rail apply (<c>UiEventMap</c> →
    /// <c>ResearchSync.PresentFromMirror</c>), so it can only ride the next diff cycle. <c>SurfaceSeq</c> is
    /// PER SURFACE by design, so those two seqs are incomparable and presentation order collapsed to LOCAL
    /// ARRIVAL ORDER — which is not a property any two peers share.
    ///
    /// Priority could not repair it (all the windows in the 2026-08-04 measurement were priority 0) and
    /// neither could widening the rank table: <c>ReplenishSync.Rank</c> is per-KIND and L93's own falsifier
    /// <c>rank-table-overreaches</c> forbids growing it. The fix is a KEY, not a table:
    /// <c>RailOrdinal</c> mints one monotonic number per OUTBOUND ENVELOPE at the single encoder
    /// (<c>SyncProtocol.EncodeEnvelope</c>), so a 0xB6 raise and a 0xAC batch become comparable by
    /// construction; <c>SurfaceRouter.OnInbound</c> publishes the applying message's ordinal for the whole
    /// synchronous dispatch, so a window BORN inside an apply inherits the ordering key of its cause.
    ///
    /// AMPUTATED 2026-08-15 WITH L522, rather than left standing as prose the code no longer keeps. Arms C
    /// (the real <c>Reorder</c> over a real queue), D (the settle is a bounded local timeout) and G (the
    /// ordinal never overrides a rank) executed <c>WindowOrder.Compare</c>, <c>Reorder</c>,
    /// <c>SettleSeconds</c> and <c>SettleExpired</c>, and arm E executed <c>WindowOrder.Stamp</c>. Those
    /// members are DELETED: two order keys on one stream WAS the bug, so the client stopped sorting
    /// altogether and reconciles to the host's published journal order instead. L522 asserts they stay
    /// deleted. What remains here is the ORDINAL's own claim — minted at the one encoder (A), inherited
    /// inside an apply (B), published by the one inbound chokepoint (F) — plus the drain gate still being
    /// reached (E).
    ///
    /// ─── PART B: THE CAMPAIGN INTRO ──────────────────────────────────────────────────────
    /// The intro cinematic played on the HOST only. The native condition
    /// (<c>GeoLevelController</c>:741-743) is
    /// <c>IntroCinematicDef != null &amp;&amp; instanceData == null &amp;&amp; gameParams.PlayIntroCinematic</c>: the
    /// host meets all three while creating the campaign — with the clients still in the lobby — and no
    /// client ever can, because every client loads that campaign from the transferred autosave and its
    /// <c>instanceData</c> is non-null. <c>UIStateGeoCutscene</c> is not <c>IGeoscapeRestorableViewState</c>,
    /// so it does not ride the save either. So the creation is made silent
    /// (<c>NewCampaignInterceptPatch.ApplyCoopCampaignParams</c>) and the host re-issues ONE
    /// <c>GeoscapeView.ToCutsceneState</c> after the barrier reveal — the exact funnel <c>CutsceneMirror</c>
    /// already postfixes, so the existing 0xBA raise fans it to every peer and each plays its own local
    /// video. Never a video API directly (arm J), never before the reveal (arm L: the call sites are
    /// exactly the post-reveal seam), and never twice (arm K: the arm is one-shot).
    ///
    /// Falsify (each verified RED, then restored):
    ///   • drop the ordinal from the envelope, or stop minting in EncodeEnvelope → ordinal-not-on-the-wire
    ///   • make RailOrdinal.ForNewWindow ignore Current                         → local-window-inherits-nothing
    ///   • drop the RailOrdinal.Applying scope from SurfaceRouter.OnInbound     → apply-publishes-no-ordinal
    ///   • drop the ReadyToDequeue gate from the drain prefix                      → stamp-not-at-the-chokepoint
    ///   • drop PlayIntroCinematic from ApplyCoopCampaignParams                   → intro-not-suppressed
    ///   • make ReplayCampaignIntro build the cutscene state itself               → intro-bypasses-the-funnel
    ///   • make ConsumeIntroCinematicOwed read without clearing                   → intro-arm-not-one-shot
    ///   • move the replay off the post-reveal seam                               → intro-replay-off-seam
    /// </summary>
    internal static class L124_WindowOrdinalAndCampaignIntro
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            foreach (var v in PartA()) yield return v;
            foreach (var v in PartB()) yield return v;
        }

        // ═══ PART A ═══════════════════════════════════════════════════════════════════════

        private static IEnumerable<string> PartA()
        {
            // ─── ARM A: EXECUTED — every outbound envelope carries a fresh, monotonic ordinal ───
            // The whole universality claim is "minted at the ONE encoder, so no surface can forget it".
            // Round-trip two envelopes on DIFFERENT surfaces and require the ordinals to be comparable and
            // strictly increasing: that is precisely what a 0xB6 raise and a 0xAC batch need from each other.
            RailOrdinal.Reset();
            var wireA = SyncProtocol.EncodeEnvelope(SurfaceIds.GeoEventRaise, SyncKind.StateDelta, new byte[] { 7, 7 });
            var wireB = SyncProtocol.EncodeEnvelope(SurfaceIds.GeoRail, SyncKind.StateDelta, new byte[] { 9 });
            byte sidA, sidB; SyncKind kindA, kindB; uint ordA, ordB; byte[] bodyA, bodyB;
            bool okA = SyncProtocol.TryDecodeEnvelope(wireA, out sidA, out kindA, out ordA, out bodyA);
            bool okB = SyncProtocol.TryDecodeEnvelope(wireB, out sidB, out kindB, out ordB, out bodyB);
            if (!okA || !okB || sidA != SurfaceIds.GeoEventRaise || sidB != SurfaceIds.GeoRail ||
                bodyA == null || bodyA.Length != 2 || bodyB == null || bodyB.Length != 1)
                yield return "L124 ordinal-not-on-the-wire: the envelope no longer round-trips (surface/payload " +
                             "came back wrong). Every ordering conclusion below reads a field that is not there.";
            else if (ordA == 0 || ordB <= ordA)
                yield return "L124 ordinal-not-on-the-wire: two envelopes on DIFFERENT surfaces came back with " +
                             "ordinals " + ordA + " and " + ordB + " — not a strictly increasing cross-surface " +
                             "stream. A 0xB6 raise and a 0xAC batch are then incomparable again and modal order " +
                             "falls back to local arrival order, which is the 2026-08-05 report verbatim.";

            // ─── ARM B: EXECUTED — a window born inside an apply INHERITS its cause's ordinal ───
            // This is the crux: the client's research modal is produced locally by the value-rail apply and
            // has no message of its own. Without inheritance it carries no key at all.
            const uint applying = 4242u;
            uint insideScope;
            using (RailOrdinal.Applying(applying)) insideScope = RailOrdinal.ForNewWindow();
            uint outsideScope = RailOrdinal.ForNewWindow();
            if (insideScope != applying)
                yield return "L124 local-window-inherits-nothing: a window born while ordinal " + applying +
                             " is being applied takes " + insideScope + " instead. The research-complete modal " +
                             "the 0xAC batch produces then has no ordering key, and no re-sort can place it " +
                             "against the mirrored raises it must interleave with.";
            if (outsideScope == applying || outsideScope == 0)
                yield return "L124 local-window-inherits-nothing: outside an apply ForNewWindow returned " +
                             outsideScope + " — it must mint a fresh value ahead of everything seen or sent, " +
                             "not reuse the last applied ordinal (two unrelated windows would tie forever).";
            // Observe must never let this peer mint BELOW something the host already stamped, or a locally
            // born window sorts ahead of messages it provably followed.
            RailOrdinal.Observe(1000000u);
            if (RailOrdinal.ForNewWindow() <= 1000000u)
                yield return "L124 local-window-inherits-nothing: after observing ordinal 1000000 this peer still " +
                             "mints " + RailOrdinal.ForNewWindow() + ". A window it raises itself would sort " +
                             "ahead of host messages that already arrived.";
            RailOrdinal.Reset();

            // ─── ARMS C, D and G RETIRED 2026-08-15 with L522 ───
            // They executed WindowOrder.Reorder, SettleSeconds/SettleExpired and Compare — the client-side
            // comparator, settle and re-sort. All four members are DELETED: the client now reconciles to the
            // host's published journal order and never sorts, and L522 asserts they stay deleted. What
            // survives here is the ordinal's own remaining claim (arms A, B, F): it is on the wire, an apply
            // publishes it and a window born inside an apply inherits it.

            // ─── ARM E: the drain still runs through the mod's ONE gate ───
            // Coverage by construction, not by enumeration: every modal kind in the game is queued through
            // QueryStateSwitch and drained through ProcessQueriedStateSwitch.
            var settlePrefix = typeof(WindowOrder.QueueSettlePatch).GetMethod("Prefix", All);
            if (settlePrefix == null || !Calls(settlePrefix, "ReadyToDequeue"))
                yield return "L124 stamp-not-at-the-chokepoint: WindowOrder.QueueSettlePatch no longer gates " +
                             "ProcessQueriedStateSwitch through ReadyToDequeue. That gate is where the local " +
                             "queue is reconciled to the host's journal order; without it every peer drains " +
                             "in its own arrival order again.";
            var encode = typeof(SyncProtocol).GetMethod("EncodeEnvelope", All);
            if (encode == null || !Calls(encode, "Mint"))
                yield return "L124 ordinal-not-on-the-wire: SyncProtocol.EncodeEnvelope no longer mints from " +
                             "RailOrdinal. A surface that stamps its own key is exactly the per-family opt-in " +
                             "this mechanism exists to avoid.";

            // ─── ARM F: the apply PUBLISHES the ordinal, at the one inbound chokepoint ───
            var onInbound = typeof(SurfaceRouter).GetMethod("OnInbound", All);
            if (onInbound == null || !Calls(onInbound, "Applying") || !Calls(onInbound, "Observe"))
                yield return "L124 apply-publishes-no-ordinal: SurfaceRouter.OnInbound no longer wraps the " +
                             "dispatch in RailOrdinal.Applying (and observes the inbound value). Everything a " +
                             "family raises out of an apply — the research modal today, anything added " +
                             "tomorrow — loses the key of the message that caused it, with no per-family " +
                             "symptom to notice it by.";
        }

        // ═══ PART B ═══════════════════════════════════════════════════════════════════════

        private static IEnumerable<string> PartB()
        {
            // ─── ARM H: EXECUTED — a co-op campaign is created with the intro SUPPRESSED ───
            // Both flags, one function, run for real: TutorialEnabled because the bootstrap fires at the
            // first playable GEOSCAPE frame, PlayIntroCinematic because the host would otherwise be the only
            // peer that ever sees it.
            var gameParams = new PhoenixPoint.Common.Levels.Params.GeoscapeGameParams
            {
                TutorialEnabled = true,
                PlayIntroCinematic = true,
            };
            Multiplayer.Harmony.NewCampaignInterceptPatch.ApplyCoopCampaignParams(gameParams);
            if (gameParams.PlayIntroCinematic)
                yield return "L124 intro-not-suppressed: ApplyCoopCampaignParams left PlayIntroCinematic true. " +
                             "The host then plays the campaign intro alone while the clients sit in the lobby, " +
                             "and they can NEVER catch up — GeoLevelController:741 requires instanceData == null " +
                             "and every client loads the campaign from the transferred autosave.";
            if (gameParams.TutorialEnabled)
                yield return "L124 intro-not-suppressed: ApplyCoopCampaignParams left TutorialEnabled true. The " +
                             "bootstrap waits for a playable GEOSCAPE frame and the tutorial is a solo tactical " +
                             "mission ahead of it, so the transfer would never fire.";

            // ─── ARM I: the suppression is reached, and only for a pending co-op campaign ───
            var prefix = typeof(Multiplayer.Harmony.NewCampaignInterceptPatch)
                             .GetMethod("CreateSceneBinding_Prefix", All);
            if (prefix == null || !Calls(prefix, "ApplyCoopCampaignParams"))
                yield return "L124 intro-not-suppressed: CreateSceneBinding_Prefix no longer calls " +
                             "ApplyCoopCampaignParams — the executed rule above is dead code and the real " +
                             "campaign is created with the native flags.";
            else if (!Reads(prefix, "NewCampaignPending"))
                yield return "L124 intro-not-suppressed: CreateSceneBinding_Prefix no longer gates on " +
                             "NewCampaignPending. It would then rewrite the params of a SOLO new game too — " +
                             "silently taking the intro away from single-player.";
            else if (!Calls(prefix, "NoteIntroCinematicOwed"))
                yield return "L124 intro-replay-off-seam: CreateSceneBinding_Prefix no longer records whether an " +
                             "intro was owed. That prefix is the last place the def's own PlayIntroCinematic is " +
                             "readable, so without it the post-reveal replay either never fires or fires for a " +
                             "game mode that never wanted one.";

            // ─── ARM J: the replay goes through the game's own funnel, never a video API ───
            // ToCutsceneState is the ONE method CutsceneMirror postfixes; anything else reaches the video
            // without the 0xBA raise and plays on the host alone — the very bug being fixed.
            var replay = typeof(CutsceneMirror).GetMethod("ReplayCampaignIntro", All);
            if (replay == null)
                yield return "L124 intro-bypasses-the-funnel: CutsceneMirror.ReplayCampaignIntro is gone — the " +
                             "suppressed intro is now suppressed on every peer, which is worse than the bug.";
            else
            {
                if (!Calls(replay, "ToCutsceneState"))
                    yield return "L124 intro-bypasses-the-funnel: ReplayCampaignIntro does not call " +
                                 "GeoscapeView.ToCutsceneState. That call IS the 0xBA capture point " +
                                 "(CutsceneRaiseBroadcast postfixes it), so any other route plays the video on " +
                                 "the host and mirrors nothing.";
                foreach (var forbidden in new[] { "QueryStateSwitch", "ToGameOverState", "SwitchToState" })
                    if (Calls(replay, forbidden))
                        yield return "L124 intro-bypasses-the-funnel: ReplayCampaignIntro reaches " + forbidden +
                                     " directly. Building or queueing the cutscene state by hand skips the one " +
                                     "postfix that fans it to the other peers.";
                if (!Reads(replay, "IntroCinematicDef"))
                    yield return "L124 intro-bypasses-the-funnel: ReplayCampaignIntro no longer reads " +
                                 "GeoscapeView.IntroCinematicDef. A build (or a mod) with no intro def must be a " +
                                 "clean no-op, not a null handed to the cutscene state.";
            }

            // ─── ARM K: EXECUTED — the owed-intro arm is ONE-SHOT ───
            // An arm read without clearing re-fires the cinematic on the next reveal (a mid-session second
            // campaign, an F2 reload) — a video nobody asked for, on every peer, and a static law would
            // never see it. Constructed uninitialized on purpose: these three members touch one bool field
            // and nothing else, so the coordinator's engine/transport graph is not needed to drive them.
            var coord = (Multiplayer.Network.SaveTransferCoordinator)FormatterServices
                            .GetUninitializedObject(typeof(Multiplayer.Network.SaveTransferCoordinator));
            coord.NoteIntroCinematicOwed(true);
            bool first = coord.ConsumeIntroCinematicOwed();
            bool second = coord.ConsumeIntroCinematicOwed();
            if (!first || second)
                yield return "L124 intro-arm-not-one-shot: arm-then-consume-twice returned (" + first + ", " +
                             second + ") instead of (True, False). A reveal that does not clear the arm replays " +
                             "the campaign intro on every later reveal of the session.";
            coord.NoteIntroCinematicOwed(false);
            if (coord.IntroCinematicOwed)
                yield return "L124 intro-arm-not-one-shot: NoteIntroCinematicOwed(false) did not clear a stale " +
                             "arm. A game mode that plays no intro would inherit the previous campaign's owed " +
                             "one and open a cutscene state with no video behind it.";

            // ─── ARM L: the replay lands EXACTLY at the post-reveal seam ───
            // AFTER the reveal is the whole risk the analysis flagged: CutsceneMirror drops a raise with "no
            // live GeoscapeView", and before the reveal the peers are not in the level. Rather than naming a
            // line number, require the replay's call sites to be the SAME set as the proven post-reveal
            // re-seed's — both are consumed once, from the host's reveal branches, right after the lift.
            // Per-method CALL COUNTS, not just the caller set: both host reveal branches live in the same
            // Update(), so a missing call site is invisible to a set comparison and would leave one of the
            // two release paths (AllDone / liveness give-up) silently intro-less.
            var coordType = typeof(Multiplayer.Network.SaveTransferCoordinator);
            var replaySites = CallSiteCounts(coordType, "HostReplayIntroCinematic");
            var reseedSites = CallSiteCounts(coordType, "HostReseedAfterReveal");
            if (replaySites.Count == 0)
                yield return "L124 intro-replay-off-seam: nothing calls HostReplayIntroCinematic — the intro is " +
                             "suppressed at creation and never re-issued, so no peer sees it at all.";
            else if (!SameSites(replaySites, reseedSites))
                yield return "L124 intro-replay-off-seam: the intro replay is reached from " +
                             Describe(replaySites) + " but the post-reveal re-seed from " + Describe(reseedSites) +
                             ". They must be the SAME call sites: a replay ahead of RevealAll is dropped by " +
                             "CutsceneMirror with 'no live GeoscapeView' and is never retried, and a reveal " +
                             "branch that re-seeds without replaying starts that campaign with no intro.";
            var replayHost = coordType.GetMethod("HostReplayIntroCinematic", All);
            if (replayHost != null && !Calls(replayHost, "ConsumeIntroCinematicOwed"))
                yield return "L124 intro-arm-not-one-shot: HostReplayIntroCinematic no longer consumes the arm " +
                             "before replaying. Both host reveal branches can run in one session, so an " +
                             "unconsumed arm is a second cinematic.";

            // ─── ARM M: the new-campaign path reaches the barrier ONCE, and only via LaunchTransfer ───
            // L94/L122 territory: one entry, one load. The bootstrap coroutine is the only thing on this
            // path that may open the barrier, and it may do so exactly once — a second LaunchTransfer would
            // re-run the whole reveal (and, now, the intro) on top of a session already in it.
            var bootstrap = StateMachineOf(coordType, "NewCampaignAutosaveAndTransferCrt");
            if (bootstrap == null)
                yield return "L124 premise-changed: the new-campaign bootstrap coroutine " +
                             "(NewCampaignAutosaveAndTransferCrt) no longer resolves — the path this law " +
                             "reasons about is not there to be checked.";
            else
            {
                int launches = CallCount(bootstrap, "LaunchTransfer");
                if (launches != 1)
                    yield return "L124 intro-replay-off-seam: the new-campaign bootstrap reaches LaunchTransfer " +
                                 launches + " times instead of exactly once. Two entries into the load barrier " +
                                 "means two reveals, and the intro is now one of the things a reveal fires.";
            }
        }

        // ─── IL helpers — self-contained per law file (this repo's idiom) ────────────────

        /// <summary>Method name → how many times it calls <paramref name="calleeName"/>. The COUNT matters:
        /// two call sites in one method are two distinct release paths.</summary>
        private static Dictionary<string, int> CallSiteCounts(Type owner, string calleeName)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var m in owner.GetMethods(All)
                                   .Concat(owner.GetNestedTypes(All).SelectMany(t => t.GetMethods(All))))
            {
                int n = CallCount(m, calleeName);
                if (n > 0) map[m.DeclaringType.Name + "." + m.Name] = n;
            }
            return map;
        }

        private static bool SameSites(Dictionary<string, int> a, Dictionary<string, int> b) =>
            a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out int n) && n == kv.Value);

        private static string Describe(Dictionary<string, int> sites) =>
            "{" + string.Join(", ", sites.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                         .Select(kv => kv.Key + "×" + kv.Value)) + "}";

        /// <summary>The compiler-generated MoveNext of an iterator method, which is where its body actually
        /// lives (the declared method only builds the state machine).</summary>
        private static MethodBase StateMachineOf(Type owner, string iteratorName) =>
            owner.GetNestedTypes(All)
                 .Where(t => t.Name.Contains(iteratorName))
                 .SelectMany(t => t.GetMethods(All))
                 .FirstOrDefault(m => m.Name == "MoveNext");

        private static bool Calls(MethodBase m, string calleeName) => CallCount(m, calleeName) > 0;

        private static int CallCount(MethodBase m, string calleeName)
        {
            var typeArgs = m != null && m.DeclaringType != null && m.DeclaringType.IsGenericType
                ? m.DeclaringType.GetGenericArguments() : null;
            var methodArgs = m != null && m.IsGenericMethodDefinition ? m.GetGenericArguments() : null;
            int n = 0;
            foreach (var step in Walk(m))
            {
                if (step.Value.Op.OperandType != OperandType.InlineMethod ||
                    (step.Value.Op != OpCodes.Call && step.Value.Op != OpCodes.Callvirt &&
                     step.Value.Op != OpCodes.Newobj)) continue;
                MethodBase callee = null;
                try { callee = m.Module.ResolveMethod(BitConverter.ToInt32(step.Key, step.Value.Pos),
                                                      typeArgs, methodArgs); } catch { }
                if (callee != null && callee.Name == calleeName) n++;
            }
            return n;
        }

        /// <summary>Reads a field or auto-property named <paramref name="memberName"/> (the getter counts).</summary>
        private static bool Reads(MethodBase m, string memberName)
        {
            var typeArgs = m != null && m.DeclaringType != null && m.DeclaringType.IsGenericType
                ? m.DeclaringType.GetGenericArguments() : null;
            var methodArgs = m != null && m.IsGenericMethodDefinition ? m.GetGenericArguments() : null;
            foreach (var step in Walk(m))
            {
                if (step.Value.Op.OperandType == OperandType.InlineMethod)
                {
                    MethodBase callee = null;
                    try { callee = m.Module.ResolveMethod(BitConverter.ToInt32(step.Key, step.Value.Pos),
                                                          typeArgs, methodArgs); } catch { }
                    if (callee != null && callee.Name == "get_" + memberName) return true;
                }
                else if (step.Value.Op.OperandType == OperandType.InlineField)
                {
                    FieldInfo f = null;
                    try { f = m.Module.ResolveField(BitConverter.ToInt32(step.Key, step.Value.Pos),
                                                    typeArgs, methodArgs); } catch { }
                    if (f != null && (f.Name == memberName ||
                                      f.Name == "<" + memberName + ">k__BackingField")) return true;
                }
            }
            return false;
        }

        private struct Step { public OpCode Op; public int Pos; }

        private static IEnumerable<KeyValuePair<byte[], Step>> Walk(MethodBase m)
        {
            byte[] il = null;
            try { il = m == null ? null : m.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null) yield break;
            int i = 0;
            while (i < il.Length)
            {
                short code = il[i++];
                if (code == 0xFE)
                {
                    if (i >= il.Length) yield break;
                    code = (short)(0xFE00 | il[i++]);
                }
                OpCode op;
                if (!OpCodeByValue.TryGetValue(code, out op)) yield break;
                int size = OperandSize(op.OperandType, il, i);
                if (size < 0 || i + size > il.Length) yield break;
                yield return new KeyValuePair<byte[], Step>(il, new Step { Op = op, Pos = i });
                i += size;
            }
        }

        private static readonly Dictionary<short, OpCode> OpCodeByValue = BuildOpCodes();

        private static Dictionary<short, OpCode> BuildOpCodes()
        {
            var map = new Dictionary<short, OpCode>();
            foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.FieldType == typeof(OpCode)) { var op = (OpCode)f.GetValue(null); map[op.Value] = op; }
            return map;
        }

        private static int OperandSize(OperandType t, byte[] il, int pos)
        {
            switch (t)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineBrTarget:
                case OperandType.InlineField:
                case OperandType.InlineI:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.ShortInlineR: return 4;
                case OperandType.InlineI8:
                case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch: return pos + 4 > il.Length ? -1 : 4 + 4 * BitConverter.ToInt32(il, pos);
                default: return -1;
            }
        }
    }
}
