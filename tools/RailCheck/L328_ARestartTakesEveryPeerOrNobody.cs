using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Base.Levels;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Game;
using PhoenixPoint.Common.Levels.Params;

namespace RailCheck
{
    /// <summary>
    /// L328 — A RESTART TAKES EVERY PEER OR IT TAKES NOBODY.
    ///
    /// THE BUG (live 2026-08-08, D:\Downloads\logs\Player.log:40404). The CLIENT pressed the pause screen's
    /// Restart button. <c>UIModulePauseScreen: Restarting level</c>, then :40405 <c>self-load barrier armed
    /// (tac-&gt;geo return): host=False</c> — it tore its tactical level down and reloaded a fresh one ALONE
    /// while the host stayed pinned at 68 % for 372 samples. From that frame on the two peers were standing
    /// in DIFFERENT tactical levels with different derived key maps, so every actor key on the wire named a
    /// different board on each side. The session was over and nothing in any log said why.
    ///
    /// WHY NOTHING CAUGHT IT: <c>RestartGameResult</c> appeared ZERO times in <c>Multiplayer2\src</c>. The
    /// engine gives a restart no method of its own — <c>UIModulePauseScreen.OnRestartConfirmed</c>:203-211
    /// calls <c>PhoenixGame.FinishLevel(new RestartGameResult(GameUtl.CurrentLevel().LevelParams))</c> and
    /// <c>PhoenixGame.TacticalGameCrt</c>:574-580 loops that RESULT TYPE back into <c>RunGameLevel</c>. So a
    /// restart is distinguishable from every other level change ONLY by the type of the object handed to the
    /// one funnel this mod already patches, and the mod's prefix there filtered on <c>QuitGameResult</c> and
    /// let a restart fall straight through to <c>OpenReturnBarrier()</c> — armed for a return that was in
    /// fact a reload, by one peer, unannounced.
    ///
    /// THE OUTCOME THIS LAW ASSERTS, not the calls that produce it:
    ///   (a) THE VERDICT IS EXECUTED, not inspected. <c>LoadBarrierGate.Classify</c> is a pure three-way
    ///       function and this law RUNS it on the four real inputs: null and a <c>QuitGameResult</c> are
    ///       NotALoad, a <c>RestartGameResult</c> is Restart, an ordinary level result is Ordinary. Delete
    ///       the Restart arm and the restart becomes invisible again — which is exactly the shipped bug, so
    ///       that is what goes red.
    ///   (b) THE FUNNEL CAN ACTUALLY STOP ONE. A prefix that returns <c>void</c> cannot skip the original,
    ///       so a client's press would reload its map no matter what the surrounding code says. The prefix
    ///       must return <c>bool</c> AND route the Restart boundary through <c>TacticalTurnSync
    ///       .OnLocalRestart</c>, whose FALSE is the block.
    ///   (c) A CLIENT'S PRESS CANNOT ANNOUNCE ANYTHING. <c>HostBroadcastRestart</c> must read
    ///       <c>NetworkEngine.IsHost</c>, and it must have exactly ONE caller in the whole mod
    ///       (<c>OnLocalRestart</c>). Two announcers is two reloads; an unguarded one is a client telling
    ///       the host to reload.
    ///   (d) AND THE OTHER PEERS ACTUALLY FOLLOW. The host's announcement must reach an apply that RELOADS:
    ///       <c>TacticalTurnSync.HandleInbound</c> → <c>ApplyRestart</c> → <c>PhoenixGame.FinishLevel</c>
    ///       with a <c>RestartGameResult</c>, inside a <c>SyncApplyScope</c> (without the scope the follower
    ///       bounces the ask back to the host as a second restart, law 8). Half of this fix — blocking the
    ///       client without carrying the peers — leaves the host reloading alone, the SAME divergence
    ///       mirrored, so it is asserted in the same law rather than left to the next reader.
    ///   (e) THE HOST'S VERDICT ON AN ASK IS EXECUTED: <c>ValidateRestart</c> accepts a live battle and
    ///       refuses a missing level and a finished one. Positive control — an always-null validator would
    ///       make (c)/(d) describe a road nothing travels.
    ///
    /// NOT A QUORUM, and this law would go red if it became one: nothing here waits for a peer to ACT. One
    /// player presses Restart and every peer follows, exactly as one player's Continue takes everyone out of
    /// a finished battle. What follows the reload is the ordinary simultaneous-start barrier, which waits
    /// only on LOADS that end by themselves.
    ///
    /// Falsify: delete the <c>Boundary.Restart</c> arm of <c>Classify</c> → (a) red. Make <c>Prefix</c>
    /// return void, or drop its <c>OnLocalRestart</c> call → (b) red. Drop the <c>IsHost</c> guard in
    /// <c>HostBroadcastRestart</c>, or call it from a second place → (c) red. Drop <c>ApplyRestart</c> from
    /// the inbound dispatch, or its <c>FinishLevel</c>/<c>SyncApplyScope</c> → (d) red. Make
    /// <c>ValidateRestart</c> always return null → (e) red.
    /// </summary>
    internal static class L328_ARestartTakesEveryPeerOrNobody
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var mod = typeof(IntentRail).Assembly;
            var gate = mod.GetType("Multiplayer.Harmony.LoadBarrierGate");
            var turnSync = mod.GetType("Multiplayer.Tactical.TacticalTurnSync");
            var scope = mod.GetType("Multiplayer.Network.Sync.SyncApplyScope");

            var classify = gate?.GetMethod("Classify", All);
            var prefix = gate?.GetMethod("Prefix", All);
            var onLocalRestart = turnSync?.GetMethod("OnLocalRestart", All);
            var broadcast = turnSync?.GetMethod("HostBroadcastRestart", All);
            var applyRestart = turnSync?.GetMethod("ApplyRestart", All);
            var inbound = turnSync?.GetMethod("HandleInbound", All);
            var validate = turnSync?.GetMethod("ValidateRestart", All);
            var finishLevel = typeof(PhoenixGame).GetMethod("FinishLevel", All);
            var isHost = typeof(NetworkEngine).GetProperty("IsHost", All)?.GetGetMethod(true);
            var enterScope = scope?.GetMethod("Enter", All);

            if (classify == null || prefix == null || onLocalRestart == null || broadcast == null ||
                applyRestart == null || inbound == null || validate == null || finishLevel == null ||
                isHost == null || enterScope == null)
            {
                yield return "L328 premise-changed: the restart family no longer resolves " +
                             "(LoadBarrierGate.Classify/Prefix, TacticalTurnSync.OnLocalRestart/" +
                             "HostBroadcastRestart/ApplyRestart/HandleInbound/ValidateRestart, " +
                             "PhoenixGame.FinishLevel, NetworkEngine.IsHost, SyncApplyScope.Enter). Every arm " +
                             "below would pass vacuously, so 'a restart takes every peer or nobody' is " +
                             "UNCHECKED rather than satisfied";
                yield break;
            }

            // ═══ (a) THE VERDICT, EXECUTED ═══
            // The four inputs the funnel really sees. PlayTacticalGameLevelResult stands in for "an ordinary
            // level change" because it is one — the host's own tactical launch passes it.
            var cases = new List<KeyValuePair<ILevelParams, string>>
            {
                new KeyValuePair<ILevelParams, string>(null, "NotALoad"),
                new KeyValuePair<ILevelParams, string>(new QuitGameResult(), "NotALoad"),
                new KeyValuePair<ILevelParams, string>(new RestartGameResult(), "Restart"),
                new KeyValuePair<ILevelParams, string>(new PlayTacticalGameLevelResult(), "Ordinary"),
            };
            foreach (var c in cases)
            {
                string got = null, threw = null;
                try { got = Convert.ToString(classify.Invoke(null, new object[] { c.Key })); }
                catch (Exception ex) { threw = (ex.InnerException ?? ex).Message; }
                if (threw != null)
                    yield return "L328 boundary-verdict-threw: LoadBarrierGate.Classify(" +
                                 (c.Key == null ? "null" : c.Key.GetType().Name) + ") threw '" + threw +
                                 "' — the one decision that tells a restart from every other level change " +
                                 "cannot be made at all";
                else if (got != c.Value)
                    yield return "L328 restart-not-distinguished: LoadBarrierGate.Classify(" +
                                 (c.Key == null ? "null" : c.Key.GetType().Name) + ") answers " + got +
                                 ", expected " + c.Value + ". A restart is distinguishable from every other " +
                                 "level change ONLY by this result type (PhoenixGame.cs:574-580 loops it back " +
                                 "into RunGameLevel; there is no RestartMission method to patch), so a wrong " +
                                 "answer here is a peer that tears down and reloads its map alone while the " +
                                 "others keep playing the old one — different levels, different key maps, " +
                                 "every actor key on the wire naming a different board (live 2026-08-08)";
            }

            // ═══ (b) THE FUNNEL CAN STOP ONE ═══
            if (prefix.ReturnType != typeof(bool))
                yield return "L328 restart-cannot-be-blocked: LoadBarrierGate.Prefix returns " +
                             prefix.ReturnType.Name + ", not bool. A void Harmony prefix cannot skip the " +
                             "original, so a client's Restart press reaches PhoenixGame.FinishLevel whatever " +
                             "the code around it decides, and that peer reloads its map alone";
            if (!Reaches(prefix, onLocalRestart, mod))
                yield return "L328 restart-boundary-unrouted: LoadBarrierGate.Prefix no longer routes the " +
                             "Restart boundary through TacticalTurnSync.OnLocalRestart. Classify may still " +
                             "name a restart correctly, but nothing acts on the answer: the press runs " +
                             "natively on whichever peer made it";

            // ═══ (c) ONE ANNOUNCER, AND IT IS THE HOST ═══
            if (!Reaches(broadcast, isHost, mod))
                yield return "L328 restart-announced-by-anyone: TacticalTurnSync.HostBroadcastRestart no " +
                             "longer reads NetworkEngine.IsHost, so a client re-entering it from " +
                             "ApplyRestart's own apply scope announces the restart back at everyone — the " +
                             "reload loop this law exists to prevent, running once per peer";
            var announcers = mod.GetTypes()
                                .SelectMany(Methods)
                                .Where(m => !Same(m, broadcast) && Reaches(m, broadcast, mod))
                                .Select(Name)
                                .Distinct()
                                .OrderBy(n => n, StringComparer.Ordinal)
                                .ToList();
            if (announcers.Count != 1 || announcers[0] != Name(onLocalRestart))
                yield return "L328 restart-has-" + announcers.Count + "-emission-points: [" +
                             (announcers.Count == 0 ? "<none>" : string.Join(", ", announcers.ToArray())) +
                             "] reach HostBroadcastRestart; the only one allowed is " + Name(onLocalRestart) +
                             ". One emission point is what makes a client's accepted ask and the host's own " +
                             "press the SAME road (HandleRestartMission runs the host's native restart, which " +
                             "re-enters the funnel); a second one announces a reload nobody performs, or " +
                             "performs one twice, and zero means the host reloads alone";

            // ═══ (d) AND THE PEERS FOLLOW ═══
            if (!Reaches(inbound, applyRestart, mod))
                yield return "L328 restart-not-followed: TacticalTurnSync.HandleInbound no longer dispatches " +
                             "to ApplyRestart, so the host's restart announcement lands on a client that does " +
                             "nothing with it. The host reloads and the client keeps playing the dead level — " +
                             "the same divergence as the shipped bug, only mirrored";
            if (!Reaches(applyRestart, finishLevel, typeof(PhoenixGame).Assembly))
                yield return "L328 restart-apply-reloads-nothing: TacticalTurnSync.ApplyRestart does not " +
                             "reach PhoenixGame.FinishLevel. Whatever else it does, the peer stays in the " +
                             "level the host has already torn down";
            if (!CtorReached(applyRestart, typeof(RestartGameResult)))
                yield return "L328 restart-apply-is-not-a-restart: TacticalTurnSync.ApplyRestart no longer " +
                             "constructs a RestartGameResult. Any other result type leaves TacticalGameCrt's " +
                             "reload loop (PhoenixGame.cs:574-580) and takes that peer out of the mission " +
                             "entirely while the rest reload it";
            if (!Reaches(applyRestart, enterScope, mod))
                yield return "L328 restart-apply-echoes: TacticalTurnSync.ApplyRestart no longer runs inside " +
                             "a SyncApplyScope, so its own FinishLevel is captured as a fresh local gesture " +
                             "and sent back to the host as a second restart (law 8, the direct echo loop)";

            // ═══ (e) THE HOST'S VERDICT, EXECUTED ═══
            foreach (var v in Verdict(validate, true, false, false, "a live, unfinished battle")) yield return v;
            foreach (var v in Verdict(validate, false, false, true, "no tactical level on the host")) yield return v;
            foreach (var v in Verdict(validate, true, true, true, "a battle that is already over")) yield return v;
        }

        /// <summary>ValidateRestart is pure, so the law RUNS it rather than describing it. Non-vacuous in
        /// both directions: an always-null validator fails the two refusal rows, an always-refusing one
        /// fails the accept row — and either would make the rest of this law describe a road nothing takes
        /// (a host that never honours an ask, or one that restarts on a dead level).</summary>
        private static IEnumerable<string> Verdict(MethodInfo validate, bool hasLevel, bool isGameOver,
                                                   bool wantRefusal, string what)
        {
            string got = null, threw = null;
            try { got = validate.Invoke(null, new object[] { hasLevel, isGameOver }) as string; }
            catch (Exception ex) { threw = (ex.InnerException ?? ex).Message; }
            if (threw != null)
            {
                yield return "L328 restart-verdict-threw: ValidateRestart(" + hasLevel + ", " + isGameOver +
                             ") threw '" + threw + "'";
                yield break;
            }
            bool refused = got != null;
            if (refused != wantRefusal)
                yield return "L328 restart-verdict-wrong: ValidateRestart says " +
                             (refused ? "REFUSED ('" + got + "')" : "accepted") + " for " + what +
                             ", expected " + (wantRefusal ? "a refusal" : "an accept") +
                             ". The host is the only peer that may restart the mission; a validator that " +
                             "accepts a dead level restarts nothing, and one that refuses a live battle " +
                             "leaves every client's Restart button permanently dead";
        }

        private static IEnumerable<MethodBase> Methods(Type t)
        {
            try { return t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All)); }
            catch { return Enumerable.Empty<MethodBase>(); }
        }

        private static string Name(MethodBase m) =>
            (m.DeclaringType == null ? "?" : m.DeclaringType.FullName) + "." + m.Name;

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;

        private static bool Reaches(MethodBase from, MethodBase target, Assembly asm) =>
            from != null && target != null && Program.Callees(from, asm).Any(c => Same(c, target));

        /// <summary>A `newobj` of this type — which is how "it builds the right KIND of level result" is
        /// answerable from IL at all. Program.Callees, not CalleeSequence: the ordered walk keeps to
        /// call/callvirt and a `newobj` is neither, so the sequence cannot see a constructor.</summary>
        private static bool CtorReached(MethodBase from, Type t) =>
            from != null && Program.Callees(from, t.Assembly).Any(c => c is ConstructorInfo && c.DeclaringType == t);
    }
}
