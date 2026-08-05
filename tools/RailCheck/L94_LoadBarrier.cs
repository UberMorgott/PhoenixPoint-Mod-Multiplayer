using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Transport;
using PhoenixPoint.Common.Game;
using PhoenixPoint.Common.Levels.Params;

namespace RailCheck
{
    /// <summary>
    /// L94 — NOBODY PLAYS UNTIL EVERYBODY IS IN, AND NOBODY WAITS ON A PEER WHO IS NEVER COMING.
    ///
    /// THE REPORT (3 instances, 2026-08-04): "whoever finishes loading FIRST gets his window lit up, sees the
    /// game and can already act on it while the others are still loading." One peer acting on a world the
    /// others have not reached yet is a desync manufactured at every load boundary.
    ///
    /// THE BARRIER WAS NEVER MISSING — ITS ARM WAS. <c>SaveTransferCoordinator._revealed</c> is a LATCH, and
    /// <c>SaveTransferMath.HoldCurtain</c> reads it: once ANY reveal has happened, every later native curtain
    /// lift passes straight through until something re-arms. Three boundaries re-arm themselves
    /// (<c>OpenBarrier</c> for the lobby/save load and the F2 reload, <c>OpenTacticalEntryBarrier</c> for the
    /// host's geoscape→tactical launch, <c>OnSaveChunk</c>'s first-chunk branch for the entering client). The
    /// tactical→geoscape RETURN re-armed nothing, because it carries no save transfer at all — the mission end
    /// rides the native path <c>TacticalView.GoToGeoscape</c> → <c>PhoenixGame.FinishLevel</c> → geoscape load.
    /// So on the way back from every battle the hold was already open and each peer lifted on its OWN load
    /// finishing. <c>_reachedPlaying</c> is latched the same way, which is why not even the SYMPTOM was
    /// visible in the logs: <c>OnReachedPlaying</c> early-returned, so no peer sent <c>LoadComplete</c> and the
    /// roster progress overlay stayed empty — the silent-swallow shape this repo keeps paying for.
    /// <c>OpenReturnBarrier</c> existed, fully written, carrying the comment "DEAD until the MISSION-END arc —
    /// no caller yet". The arc landed; the caller did not. THAT is the entire bug, and arm (a) is the arm that
    /// would have caught it on the commit that shipped the dead method.
    ///
    /// WHY THE LAW TESTS THE FUNNEL AND NOT THE TRANSITION (arm b). One barrier for every load boundary is the
    /// whole point — a per-transition arm is a macaroni factory that grows a new hole with every new boundary.
    /// <c>PhoenixGame.FinishLevel</c>:262 is the single door every level change in the game goes through, which
    /// is why <c>LevelTeardown</c> (law L70) already chose it. A future refactor that moves the arm onto
    /// <c>GoToGeoscape</c> would still fix TODAY's report and would silently re-open the hole for the next
    /// boundary, so the law names the funnel.
    ///
    /// WHY A QUIT MUST BE EXCLUDED (arm c). <c>FinishLevelAndGoToLobby</c>:284 / <c>AndQuitGame</c>:276 reach
    /// the same funnel with a <c>QuitGameResult</c>. The peer is going to the MAIN MENU; no co-op level is
    /// loading on the far side, so an arm there holds a curtain over a lobby waiting on peers who are not
    /// loading anything — a barrier turned into a hang by the one transition that has no other side.
    ///
    /// THE RELEASE RULE, RULED ON 2026-08-05 (arms d + e + g). "If it's loading from geoscape into tactical, or
    /// from tactical back, then without options EVERYONE must load, in order to start. And if someone
    /// hard-crashes — the process died, the connection broke — we wait for them; if nothing is happening, then
    /// they get dropped and we load on without them." So the barrier STAYS for level transitions, and only its
    /// release criterion changed: it opens when every LIVE peer has loaded. LIVE is two signals already on the
    /// wire — that peer's download/native-load percent still advancing, and any packet still arriving from it —
    /// and while either is fresh the wait is UNBOUNDED BY DESIGN. The flat 180 s wall clock is gone: it
    /// force-revealed a healthy-but-slow peer mid-load (a black globe, and his transition lost to owning an
    /// HDD) and, in the other direction, made forty-nine players stare at three minutes of nothing when the
    /// fiftieth had plainly died. Arm (d) EXECUTES the roster predicate (a peer that LEFT stops being expected),
    /// arm (e) EXECUTES the per-peer one — never abandon a peer that is progressing, never wait on one that has
    /// gone silent AND still — and arm (g) pins the distinction that makes this safe: dropping out of the
    /// BARRIER is not leaving the SESSION. The peer keeps its row, slot, permissions and guid binding (law L84)
    /// and re-converges through the on-demand join when it returns.
    ///
    /// ARM (k) IS THE REACHABILITY ARM, AND IT IS THE ONE THAT WAS MISSING (added 2026-08-05, second pass).
    /// Arms (d)/(e) execute the release RULE and (h) proves BEGIN still fires — and all three stayed green
    /// through a run in which nobody was ever released, because none of them can see whether the release code
    /// is REACHED. Both branches in <c>Update()</c> are gated on <c>_loadPhaseActive</c>, and
    /// <c>OpenTacticalEntryBarrier</c> set it FALSE (it was doubling as "the previous load's aggregation has
    /// ended"), so the host's entire geo→tactical entry ran with a reveal hold armed and NO path able to lift
    /// it — permanently when the L122 guard returns early or the deploy-ready coroutine never reaches the
    /// transfer, with the clients' self-reveal belt shut by design because the host was alive and talking.
    /// The flag now means exactly one thing, "a reveal hold is armed and unreleased", and arm (k) executes
    /// that: every method that arms a hold (<c>_revealed = false</c>) must set it, and
    /// <c>PerformDeferredLift</c> — the one release — must clear it. Checking the VALUE and not merely the
    /// write is the whole arm; the write-exists probe arms (f)/(i) use passes on the bug itself.
    ///
    /// ARM (f) IS A PREMISE ARM. Re-arming <c>_revealed</c> alone looks correct and is not: with
    /// <c>_reachedPlaying</c> left latched, <c>OnReachedPlaying</c> returns on its first line, no peer ever
    /// reports <c>LoadComplete</c>, <c>AllDone</c> never holds, and the barrier releases only on the liveness
    /// give-up — a minute of black screen at the end of every battle that reads as a network fault. The law
    /// pins both writes so that half-fix cannot ship looking like a whole one.
    /// </summary>
    internal static class L94_LoadBarrier
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                               BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check()
        {
            var coord = typeof(SaveTransferCoordinator);
            var arm = coord.GetMethod("OpenReturnBarrier", AllMembers);
            if (arm == null)
            {
                yield return "L94 barrier-gone: SaveTransferCoordinator.OpenReturnBarrier no longer exists — the " +
                             "tactical→geoscape return has no way to re-arm the synchronized reveal at all, so the " +
                             "first peer to finish loading is back to playing alone while the others load";
                yield break;
            }

            // ─── (a) THE ARM IS WIRED. This is the reported bug, stated mechanically. ───
            var callers = ModCallersOf(arm).ToList();
            if (callers.Count == 0)
                yield return "L94 barrier-unarmed: NOTHING in the mod calls SaveTransferCoordinator" +
                             ".OpenReturnBarrier. The method is written and dead, so _revealed stays latched true " +
                             "from the previous reveal, HoldCurtain never holds, and every peer lifts its own " +
                             "curtain the moment its OWN load finishes — one player acting on a world the others " +
                             "have not reached yet, at the end of every single battle";

            // ─── (b) IT IS ARMED AT THE FUNNEL, SO EVERY BOUNDARY IS COVERED BY ONE MECHANISM. ───
            // Only meaningful once (a) holds; the self-release arms below run unconditionally, because
            // "the barrier never opens" and "the barrier never closes" are independent ways to lose.
            var seams = callers.Where(c => PatchesFinishLevel(c.DeclaringType)).ToList();
            if (callers.Count > 0 && seams.Count == 0)
                yield return "L94 seam-not-universal: OpenReturnBarrier is called from " +
                             string.Join(", ", callers.Select(c => c.DeclaringType?.Name + "." + c.Name)
                                                      .OrderBy(n => n, StringComparer.Ordinal)) +
                             ", none of which is a HarmonyPatch on PhoenixGame.FinishLevel. FinishLevel:262 is the " +
                             "ONE door every level change passes (host tactical launch, client entry, F2 reload, " +
                             "post-mission return); arming anywhere else fixes the boundary in front of you and " +
                             "leaves the next one to be rediscovered live. (Ceiling: the call must sit on the " +
                             "patch class itself, not on a helper class it delegates to.)";

            // ─── (c) A QUIT IS NOT A LOAD BOUNDARY. ───
            foreach (var seamType in seams.Select(s => s.DeclaringType).Where(t => t != null).Distinct())
                if (!seamType.GetMethods(AllMembers).Cast<MethodBase>()
                             .Any(m => RefsType(m, typeof(QuitGameResult))))
                    yield return "L94 quit-arms-barrier: " + seamType.Name + " arms the load barrier on " +
                                 "FinishLevel without ever testing for QuitGameResult, so leaving to the main menu " +
                                 "(FinishLevelAndGoToLobby:284 / AndQuitGame:276) now arms a barrier for a level " +
                                 "nobody is loading — the curtain holds over the lobby until the liveness " +
                                 "give-up finally times somebody out, " +
                                 "which is this fix turning into the hang it exists to prevent";

            // ─── (f) PREMISE: BOTH LATCHES ARE RE-ARMED, NOT JUST THE VISIBLE ONE. ───
            foreach (var name in new[] { "_revealed", "_reachedPlaying" })
            {
                var f = coord.GetField(name, AllMembers);
                if (f == null)
                    yield return "L94 premise-changed: SaveTransferCoordinator." + name + " is gone — this law's " +
                                 "whole reasoning is that the barrier is two latches that must be re-armed " +
                                 "together, and it can no longer prove that";
                else if (!ArmersOf(coord, arm).Any(m => WritesField(m, f)))
                    yield return "L94 half-armed: OpenReturnBarrier does not reset " + name + ". " +
                                 (name == "_revealed"
                                     ? "HoldCurtain reads exactly that flag, so the curtain gate never engages and " +
                                       "the barrier is decorative"
                                     : "OnReachedPlaying returns on its first line while this is latched, so no " +
                                       "peer ever sends LoadComplete, AllDone never holds, and the reveal is left " +
                                       "to the 180 s deadline — a three-minute black screen after every battle") +
                                 ". Re-arming one of the two looks like a fix and is not";
            }

            // ─── (d) SELF-RELEASING: A PEER THAT LEAVES THE ROSTER STOPS BEING WAITED ON. ───
            // EXECUTED against the real production predicate (InternalsVisibleTo), not asserted about it.
            var tracker = new RosterProgressTracker();
            var roster = new List<byte> { 1, 2, 3 };
            tracker.MarkDone(1);
            tracker.MarkDone(2);

            if (tracker.AllDone(roster))
                yield return "L94 releases-early: AllDone reported every slot in for a roster of 3 with only 2 " +
                             "reported. The barrier would open while a peer is still loading, which IS the " +
                             "reported desync — arriving through the release predicate instead of the missing arm";

            var afterDrop = new List<byte> { 1, 2 };   // slot 3 crashed / quit / disconnected mid-load
            if (!tracker.AllDone(afterDrop))
                yield return "L94 drop-blocks-forever: a peer that left the roster mid-load is still being waited " +
                             "on, so the remaining players sit behind the loading screen until a timeout for " +
                             "someone who is never coming back. This breaks the standing rule that ANY player must " +
                             "be able to play EVERYTHING at any moment — 49 of 50 AFK and the last one still " +
                             "finishes the game — and turns a desync guard into a session-ending block";

            if (!tracker.AllDone(new List<byte>()))
                yield return "L94 empty-roster-blocks: an EMPTY expected set does not release. The last peer " +
                             "standing would hold its own curtain forever waiting on nobody at all";

            // Teardown and reveal both open the gate; a live un-revealed session is the only thing that holds.
            if (!SaveTransferMath.HoldCurtain(engineActive: true, sessionStarted: true, revealed: false))
                yield return "L94 barrier-never-holds: HoldCurtain does not hold for a live, started, un-revealed " +
                             "session — the arm can fire all it likes, every peer still lifts on its own load";
            if (SaveTransferMath.HoldCurtain(engineActive: true, sessionStarted: true, revealed: true))
                yield return "L94 hold-survives-reveal: the curtain still holds AFTER the synchronized reveal, so " +
                             "the release that is supposed to free everyone frees nobody";
            if (SaveTransferMath.HoldCurtain(engineActive: false, sessionStarted: true, revealed: false))
                yield return "L94 hold-survives-teardown: the curtain holds with the engine INACTIVE. A peer whose " +
                             "session died mid-load is then stuck behind a loading screen with nothing left to " +
                             "release it";

            // ─── (e) THE WAIT ENDS ON ABSENCE OF LIFE, NEVER ON A CLOCK. ───
            // EXECUTED against the real per-peer predicate, same as arm (d).
            var update = coord.GetMethod("Update", AllMembers);
            var lift = coord.GetMethod("PerformDeferredLift", AllMembers);
            var giveUp = coord.GetMethod("NoLiveLoaderLeft", AllMembers);
            var holds = typeof(SaveTransferMath).GetMethod("HoldsBarrier", AllMembers);
            var graceF = coord.GetField("BarrierLivenessGraceMs", AllMembers);
            if (update == null || lift == null || giveUp == null || holds == null || graceF == null)
                yield return "L94 premise-changed: SaveTransferCoordinator.Update / PerformDeferredLift / " +
                             "NoLiveLoaderLeft / BarrierLivenessGraceMs / SaveTransferMath.HoldsBarrier no longer " +
                             "all exist, so this law can no longer prove the barrier waits on liveness rather than " +
                             "on a stopwatch — and cannot prove it has any escape at all from a peer that hangs " +
                             "without dropping its connection";
            else
            {
                if (!CallsMethod(update, giveUp) || !CallsMethod(update, lift))
                    yield return "L94 liveness-release-gone: Update no longer asks NoLiveLoaderLeft before it " +
                                 "reveals. Either the barrier has gone back to a wall-clock deadline — which " +
                                 "force-revealed a healthy-but-slow peer mid-load and cost him his transition for " +
                                 "owning an HDD — or it has no escape at all and one crashed process now blocks " +
                                 "everybody else's level transition for ever";

                if (!CallsMethod(giveUp, holds))
                    yield return "L94 release-not-the-predicate: NoLiveLoaderLeft does not call " +
                                 "SaveTransferMath.HoldsBarrier, so the rule the arms below EXECUTE is not the " +
                                 "rule the host actually runs. A second copy of the release condition is how the " +
                                 "law goes green while the game hangs";

                long grace = Convert.ToInt64(graceF.GetRawConstantValue());

                if (!SaveTransferMath.HoldsBarrier(false, msSinceProgress: 0, msSinceHeard: 0, graceMs: grace))
                    yield return "L94 abandons-a-live-peer: a peer that reported progress THIS INSTANT does not " +
                                 "hold the barrier. The ruling is that a peer which is alive and still making " +
                                 "progress is waited for indefinitely — a slow disk or a lossy link must never " +
                                 "cost a player his transition";

                if (!SaveTransferMath.HoldsBarrier(false, grace, grace, grace))
                    yield return "L94 abandons-a-live-peer: at EXACTLY the grace window the peer is already " +
                                 "dropped from the barrier. The window is how long it may be silent, so the " +
                                 "give-up belongs strictly after it — an off-by-one here abandons peers on the " +
                                 "boundary that the honest reading of the constant says are still fine";

                if (SaveTransferMath.HoldsBarrier(false, grace + 1, grace + 1, grace))
                    yield return "L94 waits-on-the-dead: a peer that has neither moved nor said anything for " +
                                 "longer than the grace window still holds the barrier. That is the crashed " +
                                 "process the ruling names — the rest of the session must load on without it";

                if (SaveTransferMath.HoldsBarrier(false, grace + 1, 0, grace))
                    yield return "L94 idle-peer-holds-forever: a peer whose progress has been frozen past the " +
                                 "grace window still holds the barrier because packets keep arriving. Heartbeats " +
                                 "prove a socket, not a load: a peer stuck at 12% for ever would hold forty-nine " +
                                 "others behind a curtain while chatting happily";

                // Fresh clocks on purpose: a stale-clock probe here passes for the WRONG reason (the timing
                // terms alone answer false), which is exactly how the done term could be dropped unnoticed.
                if (SaveTransferMath.HoldsBarrier(true, msSinceProgress: 0, msSinceHeard: 0, graceMs: grace))
                    yield return "L94 done-still-holds: a slot that already reported LoadComplete keeps holding " +
                                 "the barrier while it is still chattering. Every peer talks right up to the " +
                                 "moment it is in, so nobody would ever be released — the block this whole " +
                                 "release path exists to make impossible";

                var hbF = typeof(SessionManager).GetField("HeartbeatTimeoutMs", AllMembers);
                if (hbF != null && grace < 2L * Convert.ToInt64(hbF.GetRawConstantValue()))
                    yield return "L94 grace-too-tight: BarrierLivenessGraceMs (" + grace + " ms) is under twice " +
                                 "SessionManager.HeartbeatTimeoutMs. A peer on a bad link routinely goes quiet for " +
                                 "one heartbeat window and gets PAUSED, then resumes on its next packet; a grace " +
                                 "that tight abandons exactly those players at every single level transition — the " +
                                 "'fight to be able to play' the N=50 mandate forbids";
            }

            // ─── (h) A BARRIER THAT OPENED MUST PRODUCE ITS BEGIN. ───
            // The 2026-08-05 blocker, as an OUTCOME arm. Arm (e) above only ever asked whether Update CALLS
            // NoLiveLoaderLeft — and it did, all the way through a run where nobody was ever released, because
            // the call site was UNREACHABLE: Begin() early-returned, so _loadPhaseActive stayed false and both
            // release paths were gated off. The reachability of a call is not something a caller-check can see,
            // so the decision itself is now a pure predicate and this arm EXECUTES it, the same way (d)/(e) do.
            var suppressed = typeof(SaveTransferMath).GetMethod("BeginSuppressed", AllMembers);
            var begin = coord.GetMethod("Begin", AllMembers);
            if (suppressed == null || begin == null)
                yield return "L94 premise-changed: SaveTransferMath.BeginSuppressed / SaveTransferCoordinator" +
                             ".Begin no longer both exist, so the rule that decides whether SessionBegin is " +
                             "broadcast is back to being an inline condition no law can execute";
            else
            {
                if (!CallsMethod(begin, suppressed))
                    yield return "L94 begin-not-the-predicate: Begin() does not call SaveTransferMath" +
                                 ".BeginSuppressed, so the rule this arm executes is not the rule the host runs. " +
                                 "A second copy of the guard is how this law goes green while every client sits " +
                                 "outside the battle";

                // THE LIVE ROW. Host still in its tactical level (begun), reveal-hold already dropped by a
                // PerformDeferredLift the PREVIOUS load triggered, barrier for THIS entry open.
                if (SaveTransferMath.BeginSuppressed(begun: true, hostEntryHold: false, barrierOpen: true))
                    yield return "L94 begin-suppressed-with-barrier-open: a barrier that has been OPENED does not " +
                                 "produce its BEGIN. SessionBegin is never broadcast, so every client stays at " +
                                 "SessionStarted=false, TacticalCommandSync.LiveEngine() returns null and every " +
                                 "move, shot and kill that client makes is dropped before it reaches the rail — " +
                                 "and _loadPhaseActive is never set either, so BOTH reveal-release paths in " +
                                 "Update() become unreachable and nobody is ever let in (live 2026-08-05: three " +
                                 "minutes of RosterProgress, a client killing enemies nobody else saw)";

                if (SaveTransferMath.BeginSuppressed(begun: true, hostEntryHold: true, barrierOpen: true))
                    yield return "L94 entry-hold-cannot-begin: the tac-entry relaxation is gone — a host that is " +
                                 "ALREADY live in its tactical level (begun stays true by design there) can no " +
                                 "longer broadcast the SessionBegin that lets the client enter the level it just " +
                                 "downloaded";

                if (SaveTransferMath.BeginSuppressed(begun: false, hostEntryHold: false, barrierOpen: true))
                    yield return "L94 first-start-suppressed: the very first session start is suppressed. Nothing " +
                                 "would ever begin at all";

                if (!SaveTransferMath.BeginSuppressed(begun: true, hostEntryHold: false, barrierOpen: false))
                    yield return "L94 begin-refires: with no barrier open and no entry hold there is nothing to " +
                                 "begin, yet BEGIN would fire again — a second SessionBegin mid-play re-enters " +
                                 "the level on every peer";
            }

            // ─── (i) THE ENTRY OWNS ITS OWN LOAD PHASE, AND ENDS THE PREVIOUS ONE. ───
            // Two writes, one mechanism, both of them the second half of the same live bug. The previous load's
            // aggregation must stop the moment a tactical entry arms (or its AllDone reveals the host ALONE
            // mid-entry and drops _hostEntryHold), and the entry's own barrier must be armed SYNCHRONOUSLY at
            // deploy-ready — not inside the coroutine, behind a ~1.15 s save write during which acks arrive
            // for a barrier nobody owns.
            var phase = coord.GetField("_loadPhaseActive", AllMembers);
            var armEntry = coord.GetMethod("OpenTacticalEntryBarrier", AllMembers);
            var beginEntry = coord.GetMethod("HostBeginTacticalEntryTransfer", AllMembers);
            var openBarrier = coord.GetMethod("OpenBarrier", AllMembers);
            if (phase == null || armEntry == null || beginEntry == null || openBarrier == null)
                yield return "L94 premise-changed: SaveTransferCoordinator._loadPhaseActive / " +
                             "OpenTacticalEntryBarrier / HostBeginTacticalEntryTransfer / OpenBarrier no longer " +
                             "all exist, so this law can no longer prove the tactical entry owns its own reveal";
            else
            {
                // (The "OpenTacticalEntryBarrier must touch _loadPhaseActive" half of this arm moved into arm
                // (k) below, which checks the VALUE. It used to demand the flag be CLEARED here — the write
                // that made the entry's own hold unreleasable. The stale-aggregation danger it was aimed at
                // is now held where it belongs: the AllDone branch is gated on the host's own done-mark, and
                // the entry path leaves _loadCompleteSent set so slot 0 cannot re-enter the done set before
                // deploy-ready.)
                if (!CallsMethod(beginEntry, openBarrier) || !WritesField(beginEntry, phase))
                    yield return "L94 entry-armed-too-late: HostBeginTacticalEntryTransfer does not open the " +
                                 "barrier AND arm _loadPhaseActive synchronously. Armed inside the coroutine " +
                                 "instead, the arm lands ~1.15 s later (live: bytes=1580544 ms=1151) and every " +
                                 "ack that arrives during the mid-tactical save write belongs to a barrier that " +
                                 "does not exist yet";
            }

            // ─── (k) EVERY ARMED HOLD HAS A REACHABLE RELEASE. ───
            // Arms (d)/(e) prove the release RULE is right and (h) proves BEGIN still fires. All three were
            // green through a hang, because none of them can see REACHABILITY: both release branches in
            // Update() are gated on _loadPhaseActive, and OpenTacticalEntryBarrier SET IT FALSE — it was
            // doubling as "the previous load's aggregation has ended". So the host's whole geo→tac entry ran
            // with a hold armed and no path able to lift it, permanently whenever the L122 guard returned
            // early or the deploy-ready coroutine never reached the transfer; the clients' self-reveal belt
            // stays shut in that window by design, because the host is alive and talking. Three peers, no way
            // out, and nothing red anywhere.
            //
            // Stated as the invariant that makes the mandated liveness timeout cover EVERY barrier window
            // instead of one belt per path: _loadPhaseActive means "a reveal hold is armed and unreleased",
            // so ARMING the hold (_revealed = false) must set it, and the one release must clear it. The
            // VALUE is the whole content of the arm — a write-exists probe (WritesField, used by arms f/i)
            // passes just as happily on the write that caused the hang.
            var revealedF = coord.GetField("_revealed", AllMembers);
            var phaseK = coord.GetField("_loadPhaseActive", AllMembers);
            var liftK = coord.GetMethod("PerformDeferredLift", AllMembers);
            if (revealedF == null || phaseK == null || liftK == null)
                yield return "L94 premise-changed: SaveTransferCoordinator._revealed / _loadPhaseActive / " +
                             "PerformDeferredLift no longer all exist, so this law can no longer prove that a " +
                             "hold which was armed can ever be lifted";
            else
            {
                foreach (var m in SafeMethods(coord))
                {
                    if (!WritesBool(m, revealedF, false)) continue;   // does not arm a hold
                    if (WritesBool(m, phaseK, true)) continue;        // …and says so
                    yield return "L94 hold-armed-with-no-release: SaveTransferCoordinator." + m.Name +
                                 " re-arms the reveal hold (_revealed = false) without setting " +
                                 "_loadPhaseActive = true. Both reveal-release branches in Update() — the " +
                                 "AllDone reveal and the BarrierLivenessGraceMs give-up — are gated on that " +
                                 "flag, so from this call on the curtain is held by something no timeout and " +
                                 "no all-done can reach. The peer's own self-reveal belt does not cover it " +
                                 "either: that one fires only when the HOST goes silent, and a host stuck " +
                                 "behind its own entry load is talking the whole time. Mission loading is the " +
                                 "one wait the mandate allows, and even it must time out and leave the stuck " +
                                 "peer behind";
                }
                if (!WritesBool(liftK, phaseK, false))
                    yield return "L94 hold-never-released: PerformDeferredLift does not clear " +
                                 "_loadPhaseActive. It is the ONE release of the hold, so a flag left set " +
                                 "after the reveal keeps the host broadcasting a finished load's snapshots " +
                                 "and leaves the next barrier's arm indistinguishable from the last one's " +
                                 "residue — the ambiguity this flag was collapsed to one meaning to end";
            }

            // ─── (j) ABSENCE OF DATA IS "NOT STARTED", NEVER DEATH. ───
            // EXECUTED against the real production method. HoldsBarrier above is only as honest as the clocks
            // fed to it, and LastSeenMsForSlot used to answer 0 on both miss paths — an epoch timestamp, so
            // `now - 0` is ~1.78e12 ms, past every conceivable grace. A roster slot with no _clients row (a
            // PAUSED or re-registering peer, whose row law L84 keeps) was therefore pronounced dead on the very
            // first sample, before it had been given one chance to be slow.
            var session = new SessionManager(null);
            var nowMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            foreach (var probe in new[] { (byte)0, (byte)1, (byte)7 })
                if (nowMs - session.LastSeenMsForSlot(probe) > 60_000)
                    yield return "L94 unheard-reads-as-dead: SessionManager.LastSeenMsForSlot(" + probe + ") " +
                                 "answers a clock older than a minute for a slot nothing has ever been heard " +
                                 "from. NoLiveLoaderLeft subtracts that from now and hands the result to " +
                                 "HoldsBarrier, so the peer fails the grace instantly and the reveal barrier " +
                                 "abandons a player who has not been given a single chance to load. Absence of " +
                                 "data is NOT STARTED; only a clock that was once fresh and has since aged past " +
                                 "the grace may mean dead";

            // ─── (g) DROPPING OUT OF THE BARRIER IS NOT LEAVING THE SESSION (L84's line, restated here). ───
            // The release above gives up on a peer. That must cost it the WAIT and nothing else: its row, slot,
            // permissions and guid binding stay, and it re-converges through the on-demand join. If a barrier
            // path ever "tidies up" by removing the peer it just stopped waiting for, a slow HDD becomes an
            // eviction — the single most user-hostile thing this mod ever did, arriving through the back door.
            var removeClient = typeof(SessionManager).GetMethod("RemoveClient", AllMembers);
            var disconnect = typeof(ITransport).GetMethod("DisconnectPeer", AllMembers);
            if (removeClient == null || disconnect == null)
                yield return "L94 premise-changed: SessionManager.RemoveClient / ITransport.DisconnectPeer no " +
                             "longer both exist, so this law can no longer prove the barrier never evicts the peer " +
                             "it stopped waiting for";
            else
                foreach (var name in new[] { "Update", "NoLiveLoaderLeft", "PerformDeferredLift",
                                             "TryReleaseBarrier", "OnLoadComplete", "OpenReturnBarrier",
                                             "ArmSelfLoadBarrier" })
                {
                    var m = coord.GetMethod(name, AllMembers);
                    if (m == null) continue;
                    if (CallsMethod(m, removeClient) || CallsMethod(m, disconnect))
                        yield return "L94 barrier-removes-the-peer: SaveTransferCoordinator." + name + " removes a " +
                                     "client / severs a transport link. The barrier may stop WAITING for a peer; " +
                                     "it may never take one out of the session (law L84). A peer that loses power " +
                                     "mid-load must find its seat, its slot and its permissions exactly where it " +
                                     "left them when it comes back";
                }

            // ─── (l) THE COVERAGE ARM. NO PEER'S SCREEN COMES DOWN BEFORE THE REVEAL — ANY OF THEM. ───
            foreach (var v in TakedownCoverage()) yield return v;
        }

        /// <summary>
        /// ARM (l) — EVERY WAY THE LOADING SCREEN CAN COME DOWN IS GATED, NOT JUST THE ONE WE FOUND.
        ///
        /// Every arm above asserts the AGGREGATE and the MATH: (c) executes AllDone, (d) executes
        /// HoldCurtain, (k) checks the _loadPhaseActive writers. All of them were GREEN through five
        /// successive "fixes" of the same report — one peer playing while two still load — because none of
        /// them asks the only question that was actually wrong: WHICH WRITERS DOES THE GATE COVER. In the
        /// freshest captured run the aggregate worked perfectly (three peers released within one hop, host
        /// last), and the screen still came down early, because what the player calls "the loading screen"
        /// is THREE independent GameObjects and the barrier held one:
        ///   1. the ink curtain, via LevelSwitchCurtainController.LiftCurtainCrt — gated all along;
        ///   2. the "LoadingScreen" child, via SceneFadeController.SetLoadingScreenVisible(false) — a plain
        ///      SetActive nobody had ever looked at;
        ///   3. InGameLoadingCurtain.CurtainObject, via HideCurtain — likewise, and self-driven per peer off
        ///      purely LOCAL state (WaitForView:50 spins on this peer's own Playing + ViewInitialized), which
        ///      is the shape the report describes exactly.
        ///
        /// L125 asserts that the targets we DECLARE bind. It cannot assert the target SET is complete — a
        /// patch list that is missing an entry binds perfectly. This arm is that missing half, and it is the
        /// one that makes a sixth regression cost a red harness instead of another live session.
        ///
        /// THE SET IS DERIVED, NEVER LISTED. A hand list of methods is the same failure one indirection
        /// later: it goes stale the day the game adds a fourth path, silently, which is precisely how this
        /// bug survived. So the writers are read structurally off the SHIPPED Assembly-CSharp — every method
        /// on the three curtain components (including their compiler-generated iterator bodies) that calls
        /// GameObject.SetActive with anything other than a literal `true`, while referencing a curtain /
        /// loading-screen member of those components. Deactivating the loading TIPS or the progress text is
        /// not taking the screen down, which is why the member the SetActive rides on is part of the rule.
        ///
        /// COVERED means gated, not necessarily patched: a writer whose only entries inside the components
        /// are themselves covered is covered (SceneFadeController.LiftCurtain is reached solely through
        /// LevelSwitchCurtainController.LiftCurtainCrt, which our gate wraps). A writer with NO entry inside
        /// the components is called from the outside world — PhoenixSaveManager, GeoLevelController,
        /// PhoenixGame — and must be gated on itself. Ceiling: the dominator search stops at the component
        /// boundary, so gating a caller OUTSIDE the three types does not count as coverage. That is
        /// deliberate; a gate on one of many external callers is not coverage.
        ///
        /// FALSIFIED (observed 2026-08-05, before the fix): with paths 2 and 3 unpatched this arm reports
        /// takedown-uncovered for SceneFadeController.SetLoadingScreenVisible and
        /// InGameLoadingCurtain.HideCurtain and RailCheck goes RED. Adding the two gates turns it green,
        /// while the ink-curtain writer stays green throughout because it always was covered.
        /// </summary>
        private static IEnumerable<string> TakedownCoverage()
        {
            var roots = new[] { "Base.Utils.SceneFadeController",
                                "Base.Utils.LevelSwitchCurtainController",
                                "Base.UI.InGameLoadingScreen.InGameLoadingCurtain" }
                        .Select(AccessTools.TypeByName).ToList();
            var setActive = AccessTools.Method(typeof(UnityEngine.GameObject), "SetActive", new[] { typeof(bool) });
            if (roots.Any(t => t == null) || setActive == null)
            {
                yield return "L94 premise-changed: the shipped assembly no longer carries all of " +
                             "SceneFadeController / LevelSwitchCurtainController / InGameLoadingCurtain / " +
                             "GameObject.SetActive, so this law can no longer enumerate the ways the loading " +
                             "screen comes down — and an enumeration it cannot compute is the exact blind spot " +
                             "that let one peer play while two were still loading, five fixes running";
                yield break;
            }

            // Every body that belongs to a curtain component, iterator state machines included: a lift is an
            // IEnumerator, so its real instructions live in a nested <Name>d__N::MoveNext.
            var bodies = roots.SelectMany(t => new[] { t }.Concat(t.GetNestedTypes(AllMembers)))
                              .SelectMany(SafeMethods).ToList();
            var writers = bodies.Where(m => HidesCurtainGameObject(m, setActive, roots))
                                .Select(Owner).Distinct().ToList();
            var patched = new HashSet<string>(OurPatchTargets().Select(Key));

            if (writers.Count == 0)
                yield return "L94 takedown-set-empty: not one method on the curtain components reads as able to " +
                             "hide the loading screen. The derivation has stopped matching the shipped assembly, " +
                             "so this arm is now green for the reason that makes a law worthless — it is not " +
                             "looking at anything";

            foreach (var w in writers.OrderBy(Describe, StringComparer.Ordinal))
                if (!Covered(w, patched, bodies, new HashSet<string>()))
                    yield return "L94 takedown-uncovered: " + Describe(w) + " takes the loading screen down and " +
                                 "no patch of ours gates it. It runs on each peer's OWN clock, so the first peer " +
                                 "to get there is looking at the world and can act in it while the others are " +
                                 "still loading — the report, verbatim, for the sixth time. Every take-down must " +
                                 "ask the ONE shared hold predicate (CurtainTakedownGate.Hold) and be re-issued " +
                                 "when the synchronized reveal or a teardown opens it";
        }

        /// <summary>
        /// Structural take-down probe: does this body deactivate a CURTAIN GameObject? Two halves, both
        /// required. (1) A SetActive whose argument is not the literal `true` — a `false`, or a parameter,
        /// which is what SetLoadingScreenVisible(bool) passes. (2) The body also touches a curtain /
        /// loading-screen member of the components, so hiding the loading TIPS container or the progress
        /// text — real SetActive(false) calls that live right next door — is not mistaken for hiding the
        /// screen. Same flat token scan, and the same ceiling, as the probes above.
        /// </summary>
        private static bool HidesCurtainGameObject(MethodBase m, MethodBase setActive, List<Type> roots)
        {
            var il = Il(m);
            if (il == null) return false;
            bool hides = false, curtain = false;
            for (int i = 1; i + 4 < il.Length; i++)
            {
                var op = il[i];
                if (op != 0x28 && op != 0x6F && op != 0x7B && op != 0x7C) continue;  // call/callvirt/ldfld/ldflda
                var tok = BitConverter.ToInt32(il, i + 1);
                MemberInfo member = null;
                try
                {
                    member = (op == 0x28 || op == 0x6F) ? (MemberInfo)m.Module.ResolveMethod(tok)
                                                        : m.Module.ResolveField(tok);
                }
                catch { }
                if (member == null) continue;
                if (member is MethodBase mb && mb.MetadataToken == setActive.MetadataToken &&
                    mb.Module == setActive.Module && il[i - 1] != 0x17)   // 0x17 = ldc.i4.1, i.e. a SHOW
                    hides = true;
                if (roots.Contains(member.DeclaringType) && IsCurtainMember(member.Name)) curtain = true;
            }
            return hides && curtain;
        }

        private static bool IsCurtainMember(string name)
        {
            var n = name.ToLowerInvariant();
            return n.Contains("curtain") || n.Contains("loadingscreen");
        }

        /// <summary>A compiler-generated iterator body IS its declaring method — Harmony gates the lift by
        /// wrapping the enumerator the kickoff returns, so coverage has to be asked of the kickoff.</summary>
        private static MethodBase Owner(MethodBase m)
        {
            var t = m.DeclaringType;
            if (t == null || !t.IsNested || !t.Name.StartsWith("<")) return m;
            var close = t.Name.IndexOf('>');
            if (close <= 1) return m;
            var kickoff = t.DeclaringType.GetMethods(AllMembers)
                           .FirstOrDefault(k => k.Name == t.Name.Substring(1, close - 1));
            return kickoff ?? m;
        }

        /// <summary>Gated on itself, or reached only through entries that are themselves gated. No entry
        /// inside the components at all means the outside world calls it directly, so it must be gated on
        /// itself; a cycle is not a hole.</summary>
        private static bool Covered(MethodBase m, HashSet<string> patched, List<MethodBase> bodies, HashSet<string> seen)
        {
            if (patched.Contains(Key(m))) return true;
            if (!seen.Add(Key(m))) return true;
            var callers = bodies.Where(c => CallsMethod(c, m)).Select(Owner)
                                .Where(c => Key(c) != Key(m)).GroupBy(Key).Select(g => g.First()).ToList();
            return callers.Count > 0 && callers.All(c => Covered(c, patched, bodies, seen));
        }

        /// <summary>The methods our Harmony patch classes actually attach to, resolved the way Harmony
        /// itself resolves them (Prepare runs first, TargetMethod/TargetMethods run for real) — a
        /// hand-maintained mirror of the patch list would rot exactly like a hand list of targets.</summary>
        private static IEnumerable<MethodBase> OurPatchTargets()
        {
            var mod = typeof(SaveTransferCoordinator).Assembly;
            var harmony = new HarmonyLib.Harmony("railcheck.L94");
            var pcpType = typeof(PatchClassProcessor);
            var getBulk = AccessTools.Method(pcpType, "GetBulkMethods");
            var containerField = AccessTools.Field(pcpType, "containerAttributes");
            var patchMethodsField = AccessTools.Field(pcpType, "patchMethods");
            var getOriginal = AccessTools.Method(typeof(HarmonyLib.Harmony).Assembly.GetType("HarmonyLib.PatchTools"),
                                                 "GetOriginalMethod");

            foreach (var type in AccessTools.GetTypesFromAssembly(mod))
            {
                var pcp = new PatchClassProcessor(harmony, type);
                if (containerField.GetValue(pcp) == null) continue;   // not a patch class

                var prepare = AccessTools.GetDeclaredMethods(type)
                                         .FirstOrDefault(p => p.Name == "Prepare" && p.GetParameters().Length == 0);
                List<MethodBase> targets = null;
                try
                {
                    if (prepare == null || Equals(prepare.Invoke(null, null), true))
                        targets = (List<MethodBase>)getBulk.Invoke(pcp, null);
                }
                catch { }
                if (targets == null) continue;                        // gated off this run

                if (targets.Count == 0)
                    foreach (var ap in (IEnumerable<object>)patchMethodsField.GetValue(pcp))
                    {
                        var info = (HarmonyMethod)AccessTools.Field(ap.GetType(), "info").GetValue(ap);
                        MethodBase resolved = null;
                        try { resolved = (MethodBase)getOriginal.Invoke(null, new object[] { info }); } catch { }
                        if (resolved != null) targets.Add(resolved);
                    }

                foreach (var t in targets) if (t != null) yield return t;
            }
        }

        private static string Key(MethodBase m) => m.Module.FullyQualifiedName + "#" + m.MetadataToken;

        private static string Describe(MethodBase m) =>
            (m.DeclaringType == null ? "?" : m.DeclaringType.FullName.Replace('+', '/')) + "." + m.Name;

        // ── Harmony target, read off the attribute's own constructor arguments so this does not depend on
        //    any Harmony-internal field name (a renamed field must not silently turn the arm green). ──
        private static bool PatchesFinishLevel(Type t)
        {
            if (t == null) return false;
            foreach (var cad in CustomAttributeData.GetCustomAttributes(t))
            {
                if (cad.AttributeType != typeof(HarmonyPatch)) continue;
                var a = cad.ConstructorArguments;
                if (a.Count == 2 && a[0].Value as Type == typeof(PhoenixGame) &&
                    (a[1].Value as string) == nameof(PhoenixGame.FinishLevel)) return true;
            }
            return false;
        }

        private static IEnumerable<MethodBase> ModCallersOf(MethodBase target)
        {
            foreach (var t in SafeTypes(target.DeclaringType.Assembly))
                foreach (var m in SafeMethods(t))
                    if (CallsMethod(m, target)) yield return m;
        }

        // ── IL probes. Deliberately a flat token scan rather than a full instruction walk: the methods under
        //    test are a ~10-instruction Harmony prefix and a handful of accessors, and the failure direction
        //    of an unaligned hit is a token that ResolveX rejects or resolves to something unrelated. A full
        //    walker buys nothing here and is 60 lines of it. ──
        private static byte[] Il(MethodBase m)
        {
            try { return m?.GetMethodBody()?.GetILAsByteArray(); } catch { return null; }
        }

        private static IEnumerable<int> TokensAfter(MethodBase m, params byte[] opcodes)
        {
            var il = Il(m);
            if (il == null) yield break;
            for (int i = 0; i + 4 < il.Length; i++)
                if (Array.IndexOf(opcodes, il[i]) >= 0)
                    yield return BitConverter.ToInt32(il, i + 1);
        }

        private static bool CallsMethod(MethodBase caller, MethodBase target)
        {
            foreach (var tok in TokensAfter(caller, 0x28, 0x6F))   // call / callvirt
            {
                MethodBase c = null;
                try { c = caller.Module.ResolveMethod(tok); } catch { }
                if (c != null && c.MetadataToken == target.MetadataToken && c.Module == target.Module) return true;
            }
            return false;
        }

        private static bool RefsType(MethodBase m, Type type)
        {
            foreach (var tok in TokensAfter(m, 0x74, 0x75))        // castclass / isinst
            {
                try { if (m.Module.ResolveType(tok) == type) return true; } catch { }
            }
            return false;
        }

        private static bool WritesField(MethodBase m, FieldInfo f) => TouchesField(m, f, 0x7D, 0x80); // stfld / stsfld

        /// <summary>Does this method store the LITERAL <paramref name="value"/> into a bool field? Arm (k)
        /// needs the value and not merely the write: `_loadPhaseActive = false` and `= true` are the bug and
        /// the fix, and <see cref="WritesField"/> cannot tell them apart. A literal bool assignment is always
        /// `ldc.i4.0|1` immediately before the `stfld`, so the byte before the opcode IS the value. Same flat
        /// scan (and same ceiling) as the probes above: an unaligned hit fails ResolveField and is skipped.</summary>
        private static bool WritesBool(MethodBase m, FieldInfo f, bool value)
        {
            var il = Il(m);
            if (il == null) return false;
            byte want = value ? (byte)0x17 : (byte)0x16;   // ldc.i4.1 / ldc.i4.0
            for (int i = 1; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x7D || il[i - 1] != want) continue;   // stfld preceded by the literal
                FieldInfo c = null;
                try { c = m.Module.ResolveField(BitConverter.ToInt32(il, i + 1)); } catch { }
                if (c != null && c.MetadataToken == f.MetadataToken && c.Module == f.Module) return true;
            }
            return false;
        }

        /// <summary>
        /// OpenReturnBarrier plus the same-type helpers it delegates to. Both latches must still be re-armed on
        /// the return path; whether the stfld sits in OpenReturnBarrier itself or in a helper it calls
        /// unconditionally is a refactor, not a behaviour change — the native tactical ENTRY experiment (law
        /// L103) extracted this exact body to <c>ArmSelfLoadBarrier</c> to share it geo→tac. ONE level only, so
        /// the law still fails if the re-arm merely moves somewhere nothing on this path reaches.
        /// </summary>
        private static IEnumerable<MethodBase> ArmersOf(Type coord, MethodBase arm) =>
            new[] { arm }.Concat(coord.GetMethods(AllMembers).Where(m => m != arm && CallsMethod(arm, m)));

        private static bool TouchesField(MethodBase m, FieldInfo f, params byte[] opcodes)
        {
            foreach (var tok in TokensAfter(m, opcodes))
            {
                FieldInfo c = null;
                try { c = m.Module.ResolveField(tok); } catch { }
                if (c != null && c.MetadataToken == f.MetadataToken && c.Module == f.Module) return true;
            }
            return false;
        }

        private static IEnumerable<Type> SafeTypes(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
        }

        private static IEnumerable<MethodBase> SafeMethods(Type t)
        {
            try
            {
                return t.GetMethods(AllMembers).Cast<MethodBase>()
                        .Concat(t.GetConstructors(AllMembers).Cast<MethodBase>());
            }
            catch { return Enumerable.Empty<MethodBase>(); }
        }
    }
}
