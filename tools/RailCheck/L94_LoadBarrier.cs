using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
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
    /// SELF-RELEASE IS A FIRST-CLASS LAW ARM, NOT A ROBUSTNESS NICETY (arms d + e). The user's standing rule is
    /// that at ANY moment ANY player must be able to play EVERYTHING — if 49 of 50 players are AFK the single
    /// active one still finishes the whole game. A barrier that waits on a peer who crashed, quit or died
    /// mid-load would block the rest FOREVER, which is strictly worse than the desync it prevents. Arm (d)
    /// EXECUTES the real release predicate to prove a peer leaving the roster opens the barrier for whoever is
    /// left, and arm (e) holds the bounded belt in place for the peer that neither reports nor drops.
    ///
    /// ARM (f) IS A PREMISE ARM. Re-arming <c>_revealed</c> alone looks correct and is not: with
    /// <c>_reachedPlaying</c> left latched, <c>OnReachedPlaying</c> returns on its first line, no peer ever
    /// reports <c>LoadComplete</c>, <c>AllDone</c> never holds, and the barrier releases only on the 180 s
    /// deadline — a three-minute black screen at the end of every battle that reads as a network fault. The
    /// law pins both writes so that half-fix cannot ship looking like a whole one.
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
                                 "nobody is loading — the curtain holds over the lobby until the 180 s deadline, " +
                                 "which is this fix turning into the hang it exists to prevent";

            // ─── (f) PREMISE: BOTH LATCHES ARE RE-ARMED, NOT JUST THE VISIBLE ONE. ───
            foreach (var name in new[] { "_revealed", "_reachedPlaying" })
            {
                var f = coord.GetField(name, AllMembers);
                if (f == null)
                    yield return "L94 premise-changed: SaveTransferCoordinator." + name + " is gone — this law's " +
                                 "whole reasoning is that the barrier is two latches that must be re-armed " +
                                 "together, and it can no longer prove that";
                else if (!WritesField(arm, f))
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

            // ─── (e) SELF-RELEASING: THE BOUNDED BELT FOR A PEER THAT NEITHER REPORTS NOR DROPS. ───
            var update = coord.GetMethod("Update", AllMembers);
            var lift = coord.GetMethod("PerformDeferredLift", AllMembers);
            var deadline = coord.GetField("_phase2DeadlineMs", AllMembers);
            if (update == null || lift == null || deadline == null)
                yield return "L94 premise-changed: SaveTransferCoordinator.Update / PerformDeferredLift / " +
                             "_phase2DeadlineMs no longer all exist, so this law can no longer prove the barrier " +
                             "has a bounded escape for a peer that hangs without disconnecting";
            else if (!ReadsField(update, deadline) || !CallsMethod(update, lift))
                yield return "L94 deadline-fallback-gone: Update no longer reads _phase2DeadlineMs and reveals on " +
                             "it. A peer that hangs mid-load WITHOUT dropping its connection is never removed from " +
                             "the roster, so arm (d)'s shrink never happens and this deadline is the only thing " +
                             "left between the other players and a permanent block";
        }

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
        private static bool ReadsField(MethodBase m, FieldInfo f) => TouchesField(m, f, 0x7B, 0x7E);  // ldfld / ldsfld

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
