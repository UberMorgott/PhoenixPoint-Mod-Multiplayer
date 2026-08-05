using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Text;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Geoscape.Core;
using PhoenixPoint.Geoscape.Events;
using PhoenixPoint.Geoscape.Levels.Factions;
using PhoenixPoint.Geoscape.View.ViewControllers.Modal;
using PhoenixPoint.Geoscape.View.ViewModules;

namespace RailCheck
{
    /// <summary>
    /// L101 — THE REWARD PAGE SHIPS EVERY ROW THE GAME DRAWS, AS ADDRESSES, AND NEVER APPLIES ONE.
    ///
    /// THE REPORT (3 instances, DLL 842240 B @ 2026-08-04 23:22). "On the clients we were only shown how much
    /// materials / tech / etc. we were given, while the host was ALSO shown the DIPLOMATIC RELATION changes."
    /// That was the DECLARED ceiling of 0xBB/0xBD: the codec shipped <c>Resources</c> and <c>Items</c> and
    /// nothing else, because the remaining rows of <c>GeoFactionRewardApplyResult</c> hold LIVE references
    /// (<c>GeoFaction</c>, <c>GeoHavenLeader</c>, <c>GeoCharacter</c>, <c>GeoSite</c>, <c>GeoVehicle</c>) and
    /// an object cannot cross the wire. The premise "each row needs its own identity resolution" was true of
    /// the ROWS and false of the MECHANISM — every row either native page draws is (kind, up to two
    /// references, one int), so ONE generic row and one table covers all seventeen, with the references
    /// riding the rail's own stable addresses (law 2) and re-resolved on the receiving peer.
    ///
    /// WHY THIS LAW AND NOT A LOG LINE. Three ways the extension can rot, none of which shows up as an error:
    ///   • the GAME adds a row kind in a patch and the page silently lists less than the host's — ARM
    ///     <c>row-kind-uncovered</c> reads the field set straight out of <c>UIModuleSiteEncounters.ShowReward</c>
    ///     and <c>RewardsController.SetReward</c>, so the table is checked against the RENDERERS and not
    ///     against a list somebody typed once;
    ///   • a copy-pasted table row ships its neighbour's list — ARM <c>row-kind-mismatched</c> reads each
    ///     <c>Write</c> lambda's IL and demands it touch the field the row DECLARES it is;
    ///   • the two halves of a hand-written codec drift — ARMS <c>rows-lost-in-roundtrip</c> and
    ///     <c>head-format-diverged</c> EXECUTE the real production codec, they do not assert about it.
    ///
    /// AND THE ONE THAT MATTERS MOST (<c>mirror-applies-reward</c>): this payload is PRESENTATION. Every value
    /// in it already reached the peer as ordinary replicated state — resources and items on the value rail,
    /// diplomacy as <c>PartyDiplomacy+Relation._diplomacy</c>, a covered leaf under both
    /// <c>FactionDiplomacy</c> and the haven leader (docs/rail-baseline.txt:137-140, :492-495). The day
    /// somebody "fixes" a missing row by APPLYING it here, a client mints resources or rewrites diplomacy off
    /// a display message and law 3 is gone with no log line anywhere. So the granting APIs are banned
    /// mechanically from the whole codec, and <c>build-drops-unresolved</c> executes the same rule from the
    /// other side: a row whose address does not resolve is dropped, never guessed at.
    ///
    /// SECOND HALF — THE MISSING WINDOW. Same report: "on the HOST there was ONE MORE window than on the
    /// clients". Measured, both clients identically: <c>RE26</c>'s held replay threw
    /// <c>ArgumentNullException</c> out of <c>GeoscapeEventSystem.GetEventByID</c> at 23:45:33.839 —
    /// <c>_events</c> (GeoscapeEventSystem.cs:84, filled at :135) is still null while the view already
    /// exists, and <c>GetEventByID</c>:282 dereferences it with no null test. <c>DrainHeldRaises</c> had
    /// already popped the entry, so the catch logged it and the window was gone; <c>PROG_AN2_WIN</c>'s replay
    /// 340 ms later on the same peer succeeded, which is what proves it a TIMING gate and not a verdict. The
    /// fix is that the readiness question is asked of the event system rather than of the view, in ONE
    /// predicate both callers use — <c>window-readiness-*</c> holds all three parts of that.
    ///
    /// THIRD HALF — THE SAME WINDOW, REPORTED AGAIN (2026-08-05 00:49, DLL 885760 B): "on the HOST there was
    /// a THIRD window with a pile of rewards listed with numbers; on the CLIENTS it was not there." The
    /// readiness gate above WORKED — <c>RE26</c> replayed clean this time — and the window in the report was
    /// never the encounter page at all: it is the POST-MISSION outcome modal, and it failed twice over.
    /// <c>outcome-late-*</c> holds the cause the player saw (the host's 0xBB lands after a faster-clicking
    /// client has already stamped an empty reward, and every outcome DataBind HIDES its reward section on
    /// <c>HasRewards()==false</c>); <c>row-render-precondition</c> holds the one underneath it (a row whose
    /// address resolves can still be unrenderable, and one such row NREs the WHOLE page). Both are in
    /// <see cref="RenderPrecondition"/> / <see cref="LateOutcomeDelivery"/>, whose own doc comments carry the
    /// measurements — and both prove WIRING, not landing: the landing is an in-game gate, said plainly here
    /// because the previous law on this window was green while the window was still missing.
    ///
    /// Falsify: delete a table row → <c>row-kind-uncovered</c>; point row 2's Write at
    /// <c>RevealedSites</c> → <c>row-kind-mismatched</c>; drop the row block from either Encode overload →
    /// <c>rows-lost-in-roundtrip</c> / <c>head-format-diverged</c>; make any Build/Deref path call
    /// <c>GeoFactionReward.Apply</c> or a <c>Wallet</c> member → <c>mirror-applies-reward</c>; make Build
    /// substitute a stand-in for an unresolved address → <c>build-drops-unresolved</c>; revert
    /// <c>CanCarryWindow</c> to the view-only test → <c>window-readiness-view-only</c>; stop calling it from
    /// either caller → <c>window-readiness-not-gating</c>; rename the game's field → <c>premise-events-field-gone</c>;
    /// drop the <c>RestampLateOutcome</c> call from <c>HandleInbound</c> → <c>outcome-late-not-drained</c>;
    /// repaint by re-opening the modal instead of re-populating it → <c>outcome-repaint-reopens</c>; delete
    /// row kind 10's diplomacy-state guard → <c>row-render-precondition</c>.
    /// </summary>
    internal static class L101_RewardRows
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        /// <summary>Read by the renderers but NOT row-shipped, each for a stated reason. <c>Resources</c> and
        /// <c>Items</c> ride the wire HEAD (they are lists of values, not of references, and predate the row
        /// codec). <c>CapturedAliens</c> is a <c>GeoUnitDescriptor</c> — a generated recruit descriptor that
        /// lives in no registry and carries no id, so there is genuinely nothing to address; it is the one
        /// remaining declared gap and this list is where that claim is recorded.</summary>
        private static readonly HashSet<string> NotRowShipped =
            new HashSet<string> { "Resources", "Items", "CapturedAliens" };

        internal static IEnumerable<string> Check()
        {
            var table = MissionOutcomeMirror.RowKinds;
            if (table == null || table.Length == 0)
            {
                yield return "L101 row-table-gone: MissionOutcomeMirror.RowKinds is empty — the reward page " +
                             "is back to resources+items and every diplomacy/unit/site row silently vanishes.";
                yield break;
            }

            // ─── The table is well formed and names real fields ───

            foreach (var dup in table.GroupBy(k => k.Id).Where(g => g.Count() > 1))
                yield return "L101 row-kind-duplicate-id: kind " + dup.Key + " is declared " + dup.Count() +
                             " times (" + string.Join(", ", dup.Select(k => k.Member)) + "). The decoder takes " +
                             "the FIRST match, so the later rows decode into the wrong list with no error.";

            var applyType = typeof(GeoFactionRewardApplyResult);
            foreach (var k in table)
                if (applyType.GetField(k.Member, BindingFlags.Public | BindingFlags.Instance) == null)
                    yield return "L101 row-kind-unknown-member: kind " + k.Id + " declares member '" + k.Member +
                                 "', which GeoFactionRewardApplyResult does not have. Either the row is a typo " +
                                 "or the game renamed the field and this row now ships nothing.";

            // ─── The table is checked against the RENDERERS, not against a typed list ───

            var drawn = new HashSet<string>();
            var showReward = typeof(UIModuleSiteEncounters).GetMethod("ShowReward", AllMembers);
            var setReward = typeof(RewardsController).GetMethod("SetReward", AllMembers);
            if (showReward == null || setReward == null)
            {
                yield return "L101 premise-renderer-gone: UIModuleSiteEncounters.ShowReward or " +
                             "RewardsController.SetReward no longer exists, so this law cannot derive what the " +
                             "page draws and the row table is unchecked. Re-ground it against the new renderer.";
            }
            else
            {
                foreach (var m in new[] { showReward, setReward })
                    foreach (var f in FieldsRead(m))
                        if (f.DeclaringType == applyType) drawn.Add(f.Name);

                if (drawn.Count == 0)
                    yield return "L101 premise-renderer-indirect: neither ShowReward nor SetReward reads a " +
                                 "GeoFactionRewardApplyResult field directly any more. The whole table is derived " +
                                 "from those reads, so it is now proving nothing.";

                var covered = new HashSet<string>(table.Select(k => k.Member));
                foreach (var name in drawn)
                    if (!covered.Contains(name) && !NotRowShipped.Contains(name))
                        yield return "L101 row-kind-uncovered: the native reward page draws " +
                                     "GeoFactionRewardApplyResult." + name + " and no row kind ships it, so a " +
                                     "mirroring peer's page lists less than the host's — the exact 2026-08-04 " +
                                     "report. Add a table row or declare it in NotRowShipped with a reason.";
            }

            // Each Write lambda must touch the field its row DECLARES. Lambdas compile into a nested closure
            // type, so the search is over the whole class closure, not one method.
            var writeMethods = ClosureMethods(typeof(MissionOutcomeMirror)).ToList();
            foreach (var k in table)
                if (writeMethods.Any(m => FieldsRead(m).Any(f => f.DeclaringType == applyType)) &&
                    !writeMethods.Any(m => FieldsRead(m).Any(f => f.DeclaringType == applyType && f.Name == k.Member)))
                    yield return "L101 row-kind-mismatched: no code in MissionOutcomeMirror reads " +
                                 "GeoFactionRewardApplyResult." + k.Member + ", yet kind " + k.Id + " claims to be " +
                                 "it. A row that ships its neighbour's list shows the player the wrong page and " +
                                 "logs nothing.";

            // ─── The codec is EXECUTED, both overloads, one format ───

            foreach (var f in RoundTrip()) yield return f;

            // ─── Presentation only: nothing here may grant ───

            foreach (var f in NeverApplies()) yield return f;

            // ─── The missing window: readiness is asked of the event system ───

            foreach (var f in WindowReadiness()) yield return f;

            // ─── A row must satisfy what the renderer dereferences UNGUARDED ───

            foreach (var f in RenderPrecondition()) yield return f;

            // ─── A payload that lands LATE still reaches the panel ───

            foreach (var f in LateOutcomeDelivery()) yield return f;

            // ─── The page ENDS with the reward the resolution minted ───

            foreach (var f in ReplayKeepsMintedReward()) yield return f;
        }

        // ─── ARM: a replay may not clobber a reward that was actually minted ──

        /// <summary>THE HOST-SHAPED ARM, and the one every other arm in this file was missing. Everything
        /// above proves WIRING — the codec round-trips, <c>CanCarryWindow</c> gates, <c>RestampLateOutcome</c>
        /// drains — and every one of them is written from the CLIENT's side, where the reward arrives on
        /// 0xBD and the only question is whether it survives the trip. So all of them stayed green through
        /// this, measured on the host 2026-08-05: a point-of-interest resolution granted its reward and the
        /// outcome page listed nothing.
        ///
        /// The host reaches <see cref="EventPopup.ReplayResolution"/> whenever it LOSES the click race
        /// (EventChoiceClientLock -> ReplayResolution -> MarkResolvedInstance) holding the
        /// <c>GeoFactionReward</c> its own <c>CompleteEvent</c>:101 just minted — and the stub was written
        /// unconditionally over it. <c>HasRewards()</c> then answers false, <c>ShowReward</c>:363 returns at
        /// once, and the page renders outcome TEXT with no gain numbers. Nothing throws and nothing logs.
        ///
        /// EXECUTED, not asserted: the real production method runs over a real <c>GeoscapeEvent</c> carrying
        /// a real non-empty reward, and the OUTCOME — what the page would read afterwards — is what is
        /// checked. Both directions, because "never overwrite" is satisfied perfectly by never writing at
        /// all, and a null <c>ChoiceReward</c> is what <c>SetClosingEncounter</c>:357 and
        /// <c>SelectChoice</c>:604 dereference unguarded.
        ///
        /// Falsify: make MarkResolvedInstance stamp the stub unconditionally again →
        /// <c>replay-clobbers-minted-reward</c>; drop the stub entirely → <c>replay-leaves-null-reward</c>.</summary>
        private static IEnumerable<string> ReplayKeepsMintedReward()
        {
            var mark = typeof(EventPopup).GetMethod("MarkResolvedInstance", AllMembers);
            var rewardSetter = typeof(GeoscapeEvent).GetProperty("ChoiceReward",
                BindingFlags.Public | BindingFlags.Instance)?.GetSetMethod(nonPublic: true);
            if (mark == null || rewardSetter == null)
            {
                yield return "L101 outcome-reward-path-gone: EventPopup.MarkResolvedInstance or " +
                             "GeoscapeEvent.ChoiceReward's setter did not resolve. That pair IS the replay's " +
                             "whole effect on what the outcome page draws, so this arm can no longer tell a " +
                             "page that lists the host's rewards from one that lists nothing.";
                yield break;
            }

            GeoscapeEvent withReward = null, withNone = null;
            GeoFactionReward minted = null;
            string blind = null;
            try
            {
                withReward = (GeoscapeEvent)FormatterServices.GetUninitializedObject(typeof(GeoscapeEvent));
                withNone = (GeoscapeEvent)FormatterServices.GetUninitializedObject(typeof(GeoscapeEvent));
                // FactionSkillPoints alone makes HasRewards() true (GeoFactionRewardApplyResult.cs:69) with
                // no object graph at all — this arm is about the reward being KEPT, not about its rows.
                minted = new GeoFactionReward
                { ApplyResult = new GeoFactionRewardApplyResult { FactionSkillPoints = 5 } };
                rewardSetter.Invoke(withReward, new object[] { minted });
                mark.Invoke(null, new object[] { withReward });
                mark.Invoke(null, new object[] { withNone });
            }
            catch (Exception ex)
            {
                blind = "L101 outcome-reward-arm-blind: the replay could not be executed headless (" +
                        ex.GetType().Name + ": " + (ex.InnerException ?? ex).Message + "). The arm is " +
                        "ASLEEP, not satisfied — it proves nothing about what the outcome page ends up " +
                        "holding.";
            }
            if (blind != null) { yield return blind; yield break; }

            if (!ReferenceEquals(withReward.ChoiceReward, minted))
                yield return "L101 replay-clobbers-minted-reward: the replay REPLACED a GeoFactionReward that " +
                             "was already there. On the peer that minted it — the HOST, whenever another peer " +
                             "wins the click race — that object is the real reward its own CompleteEvent:101 " +
                             "granted, and the display stub that took its place answers HasRewards()==false, so " +
                             "ShowReward:363 returns immediately and the outcome page shows the text with no " +
                             "gain numbers under it. The stub is a FALLBACK for a peer that has nothing, never " +
                             "a reset.";
            else if (withReward.ChoiceReward?.ApplyResult == null ||
                     !withReward.ChoiceReward.ApplyResult.HasRewards())
                yield return "L101 replay-clobbers-minted-reward: the reward object survived the replay but no " +
                             "longer reports HasRewards() — which is the single condition every outcome page " +
                             "tests before drawing a single row, so the player sees exactly what an empty stub " +
                             "would have given them.";
            if (!withReward.IsCompleted)
                yield return "L101 replay-not-marked-resolved: the replay left IsCompleted false. " +
                             "SelectChoice:598 tests it and would re-run CompleteEvent, granting the ENTIRE " +
                             "reward a second time on a peer that is only looking at a picture of it.";
            if (withNone.ChoiceReward == null)
                yield return "L101 replay-leaves-null-reward: a replay with NO reward to keep left ChoiceReward " +
                             "null. SetClosingEncounter:357 and SelectChoice:604 dereference it unguarded, so " +
                             "the observer's outcome page becomes an NRE inside the native module instead of a " +
                             "page with an empty list — 'never overwrite' must not have become 'never write'.";
        }

        // ─── ARM: resolving the address is not the same as being renderable ──

        /// <summary>THIRD REPORT OF THE SAME WINDOW (2026-08-05 00:49, DLL 885760 B): "on the HOST there was
        /// a THIRD window with a pile of rewards listed with numbers; on the CLIENTS that window was not
        /// there." Two independent causes, and this arm holds the second one.
        ///
        /// <c>UIModuleSiteEncounters.ShowReward</c>:500 renders a
        /// <c>FactionDiplomacyObjectiveChanged</c> row as
        /// <c>f.Diplomacy.GetFactionDiplomacyState(Context.ViewerFaction).PointOfInterest</c> — and
        /// <c>GetFactionDiplomacyState</c> is a <c>FirstOrDefault</c> over <c>_factionsDiplomacyState</c>
        /// (FactionDiplomacy.cs:120-123) whose null return the GAME's own callers test for (:151-155) and
        /// this one does not. Measured on both clients identically at 00:40:01.910: NRE at IL_06cc
        /// (<c>ldfld FactionDiplomacyState::PointOfInterest</c>) out of the mirrored replay of
        /// <c>PROG_AN2_WIN</c>, which killed the ENTIRE page — three rows the host resolved fine cost the
        /// clients the whole window, not one line of it.
        ///
        /// The general rule this encodes: a row is shippable only where this peer can also RENDER it. Row
        /// <c>Read</c> returning true on a resolved address is NOT sufficient when the renderer then
        /// dereferences a second, nullable lookup on it. Falsify: delete the
        /// <c>GetFactionDiplomacyState</c> guard from row kind 10 → <c>row-render-precondition</c>.</summary>
        private static IEnumerable<string> RenderPrecondition()
        {
            var lookups = typeof(FactionDiplomacy).GetMethods(AllMembers)
                                                  .Where(m => m.Name == "GetFactionDiplomacyState").ToList();
            if (lookups.Count == 0)
            {
                yield return "L101 premise-render-precondition-gone: FactionDiplomacy.GetFactionDiplomacyState " +
                             "no longer exists. That nullable lookup, dereferenced with no null test at " +
                             "UIModuleSiteEncounters:500, is the ENTIRE reason row kind 10 carries a render " +
                             "guard — with it renamed the guard is checking nothing and the page-killing NRE " +
                             "of 2026-08-05 can return with no error until a player loses a window.";
                yield break;
            }

            var show = typeof(UIModuleSiteEncounters).GetMethod("ShowReward", AllMembers);
            if (show != null && !Calls(show).Any(c => c.DeclaringType == typeof(FactionDiplomacy) &&
                                                     c.Name == "GetFactionDiplomacyState"))
                yield return "L101 premise-render-precondition-unused: ShowReward no longer calls " +
                             "FactionDiplomacy.GetFactionDiplomacyState, so the precondition row kind 10 pays " +
                             "for is derived from a renderer that no longer has it. Re-ground the guard " +
                             "against what the page reads NOW — including whatever replaced it.";

            if (!ClosureMethods(typeof(MissionOutcomeMirror))
                    .Any(m => Calls(m).Any(c => c.DeclaringType == typeof(FactionDiplomacy) &&
                                                c.Name == "GetFactionDiplomacyState")))
                yield return "L101 row-render-precondition: no row-table Read asks " +
                             "FactionDiplomacy.GetFactionDiplomacyState before shipping a " +
                             "FactionDiplomacyObjectiveChanged row. Resolving the faction's address is not " +
                             "enough — where this peer holds no diplomacy state for the pair the renderer " +
                             "NREs at :500 and the mirroring peer loses the WHOLE reward page, exactly as it " +
                             "did on both clients at 2026-08-05 00:40:01.910.";
        }

        // ─── ARM: the payload that lands after the panel is already up ───────

        /// <summary>THE FIRST of the two causes, and the one the player actually reported. 0xBB is broadcast
        /// when the HOST applies its reward — i.e. when the HOST's player clicks through the battle summary —
        /// so a client that clicks faster reaches <c>StampMirroredOutcome</c> with NO payload, stamps an empty
        /// reward, and every outcome DataBind then HIDES its whole reward section because it gates on
        /// <c>Reward.HasRewards()</c> (AncientSiteOutcomeDataBind.cs:105-113). That is why the report says the
        /// window was ABSENT rather than empty. Measured 2026-08-05: client stamp 00:39:53.963, host apply
        /// 00:39:55.978 — 2.0 s, and the gap is a human's reaction time, so it is not a sliver to declare.
        ///
        /// Both halves must exist or the payload is decoded into a slot nobody reads again — that is the
        /// SWALLOW shape this repo keeps meeting, and the old code even logged the warning while dropping the
        /// data. And the repaint must go through the game's own <c>IModalHandler</c> dispatch
        /// (<c>UIModal.Show</c>:29-33) and NOT through <c>Show</c>/<c>Hide</c>: re-opening a persistent modal
        /// reorders the queue, and re-entering a view state that is not the live current one is what
        /// resurrected a zombie ability and replayed a cutscene seven times.
        ///
        /// HONEST LIMIT, stated rather than implied: RailCheck is reflection + IL over a dead assembly, so
        /// this arm proves the late path is WIRED end to end, not that the window lands. The landing is an
        /// in-game gate. Falsify: drop the <c>RestampLateOutcome</c> call from <c>HandleInbound</c>, or swap
        /// the <c>ModalShowHandler</c> dispatch for <c>UIModal.Show</c>.</summary>
        private static IEnumerable<string> LateOutcomeDelivery()
        {
            var t = typeof(MissionOutcomeMirror);
            var stamp = t.GetMethod("StampMirroredOutcome", AllMembers);
            var inbound = t.GetMethod("HandleInbound", AllMembers);
            var late = t.GetMethod("RestampLateOutcome", AllMembers);
            var repaint = t.GetMethod("RepopulateModal", AllMembers);
            var slot = t.GetField("_awaitingReward", AllMembers);

            if (stamp == null || inbound == null || late == null || repaint == null || slot == null)
            {
                yield return "L101 outcome-late-path-gone: MissionOutcomeMirror is missing one of " +
                             "_awaitingReward / StampMirroredOutcome / HandleInbound / RestampLateOutcome / " +
                             "RepopulateModal. Together they are the ONLY thing that saves the outcome " +
                             "reward list on a peer that returns from the battle before the host does, which " +
                             "is every peer whose player clicks faster.";
                yield break;
            }

            bool Touches(MethodBase m) =>
                FieldsRead(m).Any(f => f.Name == "_awaitingReward" && f.DeclaringType == t);

            if (!Touches(stamp))
                yield return "L101 outcome-late-not-armed: StampMirroredOutcome no longer records the mission " +
                             "it stamped with an EMPTY reward, so a payload arriving one moment later has " +
                             "nothing to attach to and is dropped in silence — the panel keeps its hidden " +
                             "reward section for good.";

            if (!Calls(inbound).Any(c => c.Name == "RestampLateOutcome"))
                yield return "L101 outcome-late-not-drained: HandleInbound decodes the payload and never " +
                             "offers it to a mission this peer has already completed. The wire is then held " +
                             "for a stamp that already happened, which is the swallow the 2026-08-05 log " +
                             "recorded as a warning while losing all 13 items the host listed.";

            if (!Touches(late))
                yield return "L101 outcome-late-slot-unread: RestampLateOutcome does not read _awaitingReward, " +
                             "so it cannot tell a late payload from an ordinary one and would restamp a " +
                             "mission nobody is looking at.";

            if (!Calls(late).Any(c => c.Name == "RepopulateModal"))
                yield return "L101 outcome-late-not-repainted: RestampLateOutcome writes the reward but never " +
                             "repaints. The modal is ALREADY on screen and every DataBind reads the reward " +
                             "once, at population — model fresh, view stale, this repo's dominant bug shape " +
                             "(law 11).";

            if (!Calls(repaint).Any(c => c.Name == "ModalShowHandler"))
                yield return "L101 outcome-repaint-not-native: RepopulateModal no longer dispatches through " +
                             "IModalHandler.ModalShowHandler. That dispatch IS the game's own population pass " +
                             "(UIModal.Show:29-33) and is what keeps this generic over all nine outcome " +
                             "DataBinds instead of naming one of them.";

            foreach (var c in Calls(repaint))
                if (c.DeclaringType?.Name == "UIModal" && (c.Name == "Show" || c.Name == "Hide"))
                {
                    yield return "L101 outcome-repaint-reopens: RepopulateModal calls UIModal." + c.Name + ". " +
                                 "A persistent outcome modal must be RE-POPULATED in place, never re-opened: " +
                                 "Show re-raises OnModalShow and re-runs SetActive, which reorders the " +
                                 "persistent queue and puts this rail back on the Exit/Enter path that " +
                                 "resurrected a zombie ability and replayed a cutscene seven times.";
                    yield break;
                }
        }

        // ─── ARM: the wire ──────────────────────────────────────────────────

        /// <summary>Builds a result holding one row of every kind whose reference can legitimately be null
        /// (the three <c>Dictionary&lt;entity,int&gt;</c> kinds cannot — a null key throws — and are covered
        /// structurally above), ships it through the REAL Encode, reads it back through the REAL DecodeRaw,
        /// and demands the exact rows and values return with no bytes left over.</summary>
        private static IEnumerable<string> RoundTrip()
        {
            var result = new GeoFactionRewardApplyResult();
            result.Diplomacy.Add(new RewardDiplomacyChange(null, null, 7));
            result.Units.Add(new RewardNewUnit(null, null));
            result.RevealedSites.Add(null);
            result.DamageZones.Add(new KeyValuePair<PhoenixPoint.Geoscape.Entities.Sites.GeoHavenZone, int>(null, 11));
            result.ChangeHavenPopulation.Add(new KeyValuePair<PhoenixPoint.Geoscape.Entities.GeoHaven, int>(null, 12));
            result.ChangeMaxDiplomacyState.Add(new RewardMaxDiplomacyStateChange { Faction = null, State = (PartyDiplomacyState)2 });
            result.FactionDiplomacyObjectiveChanged.Add(null);
            result.SpawnedHavenDefensesAt.Add(null);
            result.FactionSkillPoints = 5;
            result.AllSoldiersDamage = 6;
            result.AllSoldiersTiredness = 7;
            result.Vehicles.Add(null);
            result.Research.Add(new RewardResearchElement { ReserachDef = null, Progress = 9 });

            // C# forbids `yield` inside try/catch, so every arm below stashes its message and yields after
            // the block. Same law, same falsifiers — only the plumbing differs.
            MissionOutcomeMirror.RewardWire wire = null;
            long tail = 0;
            byte[] bytes;
            string fail = null;
            try
            {
                using (var ms = new MemoryStream())
                {
                    using (var w = new BinaryWriter(ms, Encoding.UTF8, true)) MissionOutcomeMirror.Encode(w, result);
                    bytes = ms.ToArray();
                }
                using (var ms = new MemoryStream(bytes))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    wire = MissionOutcomeMirror.DecodeRaw(r);
                    tail = ms.Length - ms.Position;
                }
            }
            catch (Exception ex)
            {
                fail = "L101 rows-lost-in-roundtrip: the reward codec threw on a full result — " + ex.GetType().Name +
                       ": " + ex.Message + ". Every mirrored reward page would open empty.";
            }
            if (fail != null) { yield return fail; yield break; }

            if (tail != 0)
                yield return "L101 rows-lost-in-roundtrip: " + tail + " bytes left unread after DecodeRaw. The " +
                             "reward is not the last thing in either envelope's body on 0xBD, so a reader that " +
                             "stops short corrupts whatever follows it.";

            // The kinds this result must produce, and what each must carry. NewPhoenixBase is deliberately
            // absent: it is null here, and a row for it would mean the emitter ignores its own null test.
            var expected = new Dictionary<byte, int> { { 1, 7 }, { 2, 0 }, { 3, 0 }, { 7, 11 }, { 8, 12 }, { 9, 2 },
                                                       { 10, 0 }, { 11, 0 }, { 13, 5 }, { 14, 6 }, { 15, 7 },
                                                       { 16, 0 }, { 17, 9 } };
            foreach (var kv in expected)
            {
                var got = wire.Rows.Where(r => r.Kind == kv.Key).ToList();
                if (got.Count != 1)
                    yield return "L101 rows-lost-in-roundtrip: kind " + kv.Key + " round-tripped " + got.Count +
                                 " times, expected exactly 1 — that row kind no longer reaches a mirroring peer.";
                else if (got[0].Value != kv.Value)
                    yield return "L101 rows-lost-in-roundtrip: kind " + kv.Key + " came back with value " +
                                 got[0].Value + ", sent " + kv.Value + " — the page would draw the wrong number.";
            }
            if (wire.Rows.Any(r => r.Kind == 12))
                yield return "L101 rows-lost-in-roundtrip: kind 12 (NewPhoenixBase) shipped for a NULL base. The " +
                             "receiver would drop it, but the emitter is ignoring its own null test.";

            // Build with NO level: every address resolves nowhere. Ref rows must DROP; scalars still apply.
            GeoFactionReward built = null;
            try { built = MissionOutcomeMirror.Build(null, wire, "test"); }
            catch (Exception ex)
            {
                fail = "L101 build-drops-unresolved: Build threw when nothing resolved (" + ex.GetType().Name +
                       ") instead of dropping the rows. On a peer mid-load that is the whole page lost.";
            }
            if (fail != null) { yield return fail; yield break; }
            if (built.ApplyResult.Diplomacy.Count != 0 || built.ApplyResult.RevealedSites.Count != 0 ||
                built.ApplyResult.Units.Count != 0)
                yield return "L101 build-drops-unresolved: an address that resolved to NOTHING still produced a " +
                             "row. A null in any of these lists NREs inside the native renderer's own foreach, " +
                             "and a fabricated stand-in would show the player a reward naming the wrong thing.";
            if (built.ApplyResult.FactionSkillPoints != 5 || built.ApplyResult.AllSoldiersDamage != 6)
                yield return "L101 build-drops-unresolved: a REFLESS row (a plain number) was dropped along with " +
                             "the unresolvable ones. Those rows have nothing to resolve and must always land.";

            // The two Encode overloads are ONE format — 0xBB and 0xBD share this codec, and a head-only
            // writer that skips the row count desynchronises every reader behind it.
            var headArm = new List<string>();
            try
            {
                byte[] head;
                using (var ms = new MemoryStream())
                {
                    using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
                        MissionOutcomeMirror.Encode(w, result.Resources, result.Items);
                    head = ms.ToArray();
                }
                using (var ms = new MemoryStream(head))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    var back = MissionOutcomeMirror.DecodeRaw(r);
                    if (ms.Length != ms.Position)
                        headArm.Add("L101 head-format-diverged: the resources+items Encode overload leaves " +
                                    (ms.Length - ms.Position) + " bytes unread by DecodeRaw. The two overloads " +
                                    "must write ONE format (an empty row block), or a 0xBB payload written by " +
                                    "one is misread by the other.");
                    else if (back.Rows.Count != 0)
                        headArm.Add("L101 head-format-diverged: the head-only overload emitted " + back.Rows.Count +
                                    " rows out of nowhere.");
                }
            }
            catch (Exception ex)
            {
                headArm.Add("L101 head-format-diverged: the head-only overload no longer round-trips (" +
                            ex.GetType().Name + ": " + ex.Message + ").");
            }
            foreach (var v in headArm) yield return v;
        }

        // ─── ARM: presentation only ─────────────────────────────────────────

        /// <summary>The granting APIs, by the shape that makes them grants rather than by a class list:
        /// <c>GeoFactionReward.Apply</c> is the method that MINTS an ApplyResult (GeoFactionReward.cs:110),
        /// <c>Wallet</c> is the resource ledger, and <c>PartyDiplomacy+Relation</c>'s setters are where a
        /// diplomacy number is actually written. A display path may read any of these; it may never call
        /// one.</summary>
        private static bool IsGrant(MethodBase m)
        {
            var t = m.DeclaringType;
            if (t == null) return false;
            if (t == typeof(GeoFactionReward) && m.Name == "Apply") return true;
            if (t == typeof(Wallet)) return true;
            if (t.FullName == "PhoenixPoint.Common.Core.PartyDiplomacy+Relation" &&
                m.Name.StartsWith("set_", StringComparison.Ordinal)) return true;
            return m.Name == "OnMissionRewardApplied";
        }

        private static IEnumerable<string> NeverApplies()
        {
            var scanned = ClosureMethods(typeof(MissionOutcomeMirror)).ToList();
            var popup = typeof(EventPopup);
            foreach (var name in new[] { "StubReward", "RestampLiveInstance" })
            {
                var m = popup.GetMethod(name, AllMembers);
                if (m == null)
                    yield return "L101 mirror-display-path-gone: EventPopup." + name + " no longer exists. The " +
                                 "0xBD payload reaches the page through it, so this law can no longer prove the " +
                                 "page only DISPLAYS the reward.";
                else scanned.Add(m);
            }

            foreach (var m in scanned)
                foreach (var callee in Calls(m))
                    if (IsGrant(callee))
                    {
                        yield return "L101 mirror-applies-reward: " + m.DeclaringType.Name + "." + m.Name +
                                     " calls " + callee.DeclaringType.Name + "." + callee.Name + ". This surface " +
                                     "is PRESENTATION — every value in it already reached the peer as replicated " +
                                     "state. Applying one here mints resources or rewrites diplomacy on a client " +
                                     "off a display message (law 3), and nothing would log it.";
                        yield break;   // one line, not one per call site
                    }
        }

        // ─── ARM: the missing window ────────────────────────────────────────

        private static IEnumerable<string> WindowReadiness()
        {
            if (typeof(GeoscapeEventSystem).GetField("_events", BindingFlags.NonPublic | BindingFlags.Instance) == null)
                yield return "L101 premise-events-field-gone: GeoscapeEventSystem._events is gone. That field " +
                             "being null is the ENTIRE reason a mirrored window used to be lost on arrival " +
                             "(GetEventByID:282 dereferences it with no null test), and EventPopup's readiness " +
                             "gate reads it by name — with the field renamed the gate silently answers 'ready' " +
                             "always and the dropped-window bug is back, invisibly.";

            var popup = typeof(EventPopup);
            var gate = popup.GetMethod("CanCarryWindow", AllMembers);
            if (gate == null)
            {
                yield return "L101 window-readiness-gone: EventPopup.CanCarryWindow no longer exists — the " +
                             "readiness question is back to 'is there a view', which is what cost the clients a " +
                             "whole window on 2026-08-04.";
                yield break;
            }

            if (!FieldsRead(gate).Any(f => f.Name == "EventsField"))
                yield return "L101 window-readiness-view-only: CanCarryWindow no longer reads EventsField, i.e. it " +
                             "asks about the VIEW and not the event system. The view exists first; GetEventByID " +
                             "then throws and the raise is dropped mid-replay with nothing left to retry.";

            foreach (var name in new[] { "DrainHeldRaises", "RaiseMirrored" })
            {
                var m = popup.GetMethod(name, AllMembers);
                if (m == null)
                    yield return "L101 window-readiness-not-gating: EventPopup." + name + " is gone; the gate has " +
                                 "one fewer caller than the two paths a raise can take.";
                else if (!Calls(m).Any(c => c.Name == "CanCarryWindow"))
                    yield return "L101 window-readiness-not-gating: EventPopup." + name + " does not call " +
                                 "CanCarryWindow. Both paths need it: DrainHeldRaises must not POP an entry it " +
                                 "cannot place (popping and re-appending reorders the queue, and the host's " +
                                 "arrival order is the only ordering these windows have), and RaiseMirrored must " +
                                 "HOLD a live raise that lands in the same window instead of throwing it away.";
            }
        }

        // ─── IL helpers (per-law copies, same shape as L96) ─────────────────

        /// <summary>A type's own methods plus those of its compiler-generated closures — a lambda in the row
        /// table is a method on a nested display class, not on the type itself.</summary>
        private static IEnumerable<MethodBase> ClosureMethods(Type t)
        {
            foreach (var m in t.GetMethods(AllMembers)) yield return m;
            foreach (var nested in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                foreach (var m in nested.GetMethods(AllMembers)) yield return m;
        }

        private static List<FieldInfo> FieldsRead(MethodBase m)
        {
            var found = new List<FieldInfo>();
            var typeArgs = m.DeclaringType != null && m.DeclaringType.IsGenericType
                ? m.DeclaringType.GetGenericArguments() : null;
            var methodArgs = m.IsGenericMethodDefinition ? m.GetGenericArguments() : null;
            foreach (var step in Walk(m))
            {
                if (step.Value.Op.OperandType != OperandType.InlineField) continue;
                FieldInfo f = null;
                try { f = m.Module.ResolveField(BitConverter.ToInt32(step.Key, step.Value.Pos), typeArgs, methodArgs); }
                catch { }
                if (f != null) found.Add(f);
            }
            return found;
        }

        private static List<MethodBase> Calls(MethodBase m)
        {
            var seq = new List<MethodBase>();
            var typeArgs = m.DeclaringType != null && m.DeclaringType.IsGenericType
                ? m.DeclaringType.GetGenericArguments() : null;
            var methodArgs = m.IsGenericMethodDefinition ? m.GetGenericArguments() : null;
            foreach (var step in Walk(m))
            {
                if (step.Value.Op.OperandType != OperandType.InlineMethod ||
                    (step.Value.Op != OpCodes.Call && step.Value.Op != OpCodes.Callvirt &&
                     step.Value.Op != OpCodes.Newobj)) continue;
                MethodBase callee = null;
                try { callee = m.Module.ResolveMethod(BitConverter.ToInt32(step.Key, step.Value.Pos), typeArgs, methodArgs); }
                catch { }
                if (callee != null) seq.Add(callee);
            }
            return seq;
        }

        private struct Step { public OpCode Op; public int Pos; }

        /// <summary>A naive byte scan would match operand bytes and invent edges, and a law that cries wolf is
        /// a law that gets ignored. Anything unparseable ABANDONS the method rather than guessing.</summary>
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
                case OperandType.InlineSwitch:
                    if (pos + 4 > il.Length) return -1;
                    return 4 + 4 * BitConverter.ToInt32(il, pos);
                default: return -1;
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
    }
}
