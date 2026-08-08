using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;

namespace RailCheck
{
    /// <summary>
    /// L325 — THE CLIENT SUPPRESSES EXACTLY THE ENEMIES TFTV WOULD NAME, AND THE HOST SAYS WHAT IT SENDS.
    ///
    /// THE REPORT (2026-08-08, live 3-instance sweep). <c>TftvClientChampGuard.Prefix</c> announced a
    /// suppression for every actor that entered play — deploy crates, AI waypoints, dropped containers,
    /// structural targets, scenery props — 77 lines per client, and TFTV would not have touched one of them.
    /// <c>TFTVHumanEnemies.GiveRankAndNameToHumaoidEnemy</c> (TFTVHumanEnemies.cs:1617) returns immediately
    /// unless <c>actor.BaseDef.name == "Soldier_ActorDef"</c>, so the guard was suppressing a call that would
    /// have done nothing, and burying the cases that matter in noise.
    ///
    /// BOTH DIRECTIONS ARE WRONG AND THE LAW HOLDS BOTH ENDS. Too WIDE is the reported defect (noise, and a
    /// log in which the real suppressions are unfindable). Too NARROW is far worse and looks like a tidy-up:
    /// one candidate that slips past the guard is re-rolled locally off TFTV's
    /// <c>Random.InitState((int)Stopwatch.GetTimestamp())</c> seed, and that peer then fights a DIFFERENT
    /// elite with a different name than the host's — measured 13:38:12 that same day, one battlefield and
    /// three names (<c>Havara</c> / <c>Starbuck</c> / <c>Dag</c>). So the law does not assert "a guard
    /// exists"; it EXECUTES <c>TftvClientChampGuard.TftvWouldRoll</c> and demands TFTV's own set back — the
    /// candidate suppressed, everything else untouched, and no widening by prefix, substring or case.
    ///
    /// AND IT ASSERTS THE DECISION IS FED THE MARKER. <c>Prefix</c> must reach <c>BaseDef</c>: narrowing by
    /// the parameter type Harmony hands you is the recorded mistake this repo has paid for before, and a
    /// guard keyed on <c>actor.name</c> would answer a different question entirely.
    ///
    /// THE SECOND HALF IS FALSIFIABILITY OF THE CARRY ITSELF. <c>TftvChampIdentity.Collect</c> was silent and
    /// the client's <c>Apply</c> speaks only when it CHANGES something, so a session with no champ line in
    /// any log could equally mean "no human enemy was in that mission" or "the identity is not being
    /// collected at all" — and the 2026-08-08 sweep could not tell them apart. One host line per identity
    /// separates them: a host line with no client line is a carry that is failing, no host line at all is a
    /// mission with nothing to carry. The dedup key is the IDENTITY, not the actor — executed below — because
    /// keyed on the actor a re-roll would be swallowed and the distinction would rot back out.
    ///
    /// Falsify: make <c>TftvWouldRoll</c> return <c>true</c> unconditionally (the shipped defect, verbatim) →
    /// <c>non-candidate-suppressed</c>; make it <c>false</c> for the candidate → <c>real-candidate-escapes</c>;
    /// widen it to <c>StartsWith</c>/<c>Contains</c>/case-insensitive → <c>non-candidate-suppressed</c> on the
    /// matching corner; stop calling it from <c>Prefix</c> → <c>decision-unreached</c>; guard on
    /// <c>actor.name</c> instead of <c>BaseDef</c> → <c>decision-unfed</c>; delete the <c>Announce</c> call
    /// from <c>Collect</c> → <c>host-silent</c>; key the dedup on the actor alone → <c>reroll-swallowed</c>;
    /// give the control the real predicate → <c>control-not-red</c>.
    /// </summary>
    internal static class L325_OnlyAnEnemyTftvWouldNameIsSuppressed
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>POSITIVE CONTROL: the PRE-FIX predicate, verbatim — guard everything that reaches the
        /// method. If the corner runner calls this sound, it cannot tell the fix from the defect.</summary>
        private static bool GuardEverything(string baseDefName) => true;

        internal static IEnumerable<string> Check(Assembly game)
        {
            var guard = typeof(TftvClientChampGuard);
            var identity = typeof(TftvChampIdentity);
            var mod = guard.Assembly;

            var prefix = guard.GetMethod("Prefix", All);
            var wouldRoll = guard.GetMethod("TftvWouldRoll", All);
            var collect = identity.GetMethod("Collect", All);
            var announce = identity.GetMethod("Announce", All);
            var announceKey = identity.GetMethod("AnnounceKey", All);
            var idType = identity.GetNestedType("Identity", All);
            var baseDef = game.GetType("PhoenixPoint.Tactical.Entities.TacticalActorBase")?
                              .GetProperty("BaseDef", All)?.GetGetMethod(true);

            if (prefix == null || wouldRoll == null || collect == null || announce == null ||
                announceKey == null || idType == null || baseDef == null)
            {
                yield return "L325 premise-changed: TftvClientChampGuard.{Prefix,TftvWouldRoll} / " +
                             "TftvChampIdentity.{Collect,Announce,AnnounceKey,Identity} / " +
                             "TacticalActorBase.BaseDef no longer resolves. The champ suppression or the " +
                             "identity carry has been restructured, and this law can no longer tell whether " +
                             "the client still suppresses exactly TFTV's own candidates — re-read both files " +
                             "before trusting the green.";
                yield break;
            }

            // ── (a) TFTV'S OWN SET, executed in both directions ────────────────────────────
            foreach (var v in Corners(n => (bool)wouldRoll.Invoke(null, new object[] { n }), "L325"))
                yield return v;

            // ── (b) the decision is reached, and it is fed the MARKER not the parameter type ─
            if (!Program.Callees(prefix, mod).Any(c => c.MetadataToken == wouldRoll.MetadataToken &&
                                                       c.Module == wouldRoll.Module))
                yield return "L325 decision-unreached: TftvClientChampGuard.Prefix never calls TftvWouldRoll, " +
                             "so whatever set it suppresses is not the one proved sound above. Every actor " +
                             "that enters play reaches this prefix; the set it picks out of them is the whole " +
                             "law.";
            // `game`, not `mod`: Callees FILTERS by assembly, and this callee lives across the boundary.
            if (!Program.Callees(prefix, game).Any(c => c.Name == baseDef.Name))
                yield return "L325 decision-unfed: TftvClientChampGuard.Prefix never reads " +
                             "TacticalActorBase.BaseDef, so it is deciding on something other than the marker " +
                             "TFTV's own line tests (TFTVHumanEnemies.cs:1617). Narrowing by the parameter " +
                             "type Harmony hands you, or by actor.name, is the recorded mistake this repo has " +
                             "already paid for — the owning code's own predicate is the only one that cannot " +
                             "drift away from it.";

            // ── (c) THE HOST SAYS WHAT IT SENDS ────────────────────────────────────────────
            if (!Program.Callees(collect, mod).Any(c => c.MetadataToken == announce.MetadataToken &&
                                                        c.Module == announce.Module))
                yield return "L325 host-silent: TftvChampIdentity.Collect does not announce the identity it " +
                             "collected. Apply speaks only on CHANGE, so with the host silent a log with no " +
                             "champ line at all is ambiguous between 'no human enemy in that mission' and " +
                             "'the rider never ran' — which is precisely what the 2026-08-08 sweep could not " +
                             "resolve. An unfalsifiable carry is an unverified one.";
            // CalleeSequence, not Callees: UnityEngine.Debug is a THIRD assembly, and Callees filters to one.
            if (!Program.CalleeSequence(announce).Any(c => (c.Name == "Log" || c.Name == "LogWarning") &&
                                                          c.DeclaringType != null && c.DeclaringType.Name == "Debug"))
                yield return "L325 announcement-says-nothing: TftvChampIdentity.Announce reaches no " +
                             "Debug.Log/LogWarning, so the host-side line this law exists to guarantee is not " +
                             "actually emitted.";

            // ── (d) the dedup is on the IDENTITY, executed ─────────────────────────────────
            var a = MakeIdentity(idType, "Havara", new[] { "HumanEnemyFaction_Anu", "HumanEnemyRank_Champ" });
            var same = MakeIdentity(idType, "Havara", new[] { "HumanEnemyFaction_Anu", "HumanEnemyRank_Champ" });
            var renamed = MakeIdentity(idType, "Starbuck", new[] { "HumanEnemyFaction_Anu", "HumanEnemyRank_Champ" });
            var retagged = MakeIdentity(idType, "Havara", new[] { "HumanEnemyFaction_NJ", "HumanEnemyRank_Champ" });

            string kA = (string)announceKey.Invoke(null, new object[] { 7, a });
            if (kA != (string)announceKey.Invoke(null, new object[] { 7, same }))
                yield return "L325 repeat-speaks-again: the same identity on the same key produces two " +
                             "different dedup keys, so Collect — which runs on EVERY settle of EVERY actor — " +
                             "would repeat the host line every frame and drown the log it was added to make " +
                             "readable.";
            if (kA == (string)announceKey.Invoke(null, new object[] { 7, renamed }) ||
                kA == (string)announceKey.Invoke(null, new object[] { 7, retagged }))
                yield return "L325 reroll-swallowed: a CHANGED identity on the same actor key produces the " +
                             "same dedup key, so a re-roll is silently swallowed. The host line then no " +
                             "longer distinguishes 'nothing to say' from 'already said something else', and " +
                             "arm (c) buys nothing.";
            if (kA == (string)announceKey.Invoke(null, new object[] { 8, a }))
                yield return "L325 identities-collide-across-actors: two different actors carrying the same " +
                             "identity share one dedup key, so the second one is never announced at all.";

            // ── (e) POSITIVE CONTROL ───────────────────────────────────────────────────────
            if (!Corners(GuardEverything, "control").Any())
                yield return "L325 control-not-red: the corner runner reports 'guard everything that reaches " +
                             "the prefix' — the shipped defect, verbatim — as sound. Arm (a) therefore cannot " +
                             "tell TFTV's own candidate set from every actor in the level, and the green " +
                             "above is decoration.";
        }

        private static object MakeIdentity(Type idType, string name, string[] tags)
        {
            var id = Activator.CreateInstance(idType, nonPublic: true);
            idType.GetField("Name", All).SetValue(id, name);
            idType.GetField("Tags", All).SetValue(id, new List<string>(tags));
            return id;
        }

        /// <summary>TFTV's candidate line, both directions. Run over the real predicate and over the pre-fix
        /// one, so a green means the test discriminates.</summary>
        private static IEnumerable<string> Corners(Func<string, bool> decide, string id)
        {
            // The one that must STILL be suppressed — a real candidate that escapes is re-rolled locally and
            // the peers disagree about which elite is on the field.
            if (!decide("Soldier_ActorDef"))
                yield return id + " real-candidate-escapes: an actor whose BaseDef is Soldier_ActorDef — the " +
                             "exact and only shape TFTVHumanEnemies.cs:1617 will roll — is NOT suppressed on " +
                             "the client. TFTV then re-rolls its rank and name off a Stopwatch-seeded " +
                             "generator on this peer, and that screen fights a different elite than the " +
                             "host's (measured 2026-08-08 13:38:12: Havara / Starbuck / Dag, one battlefield).";

            // Everything else in the level. All of these reach the prefix; none of them is a candidate.
            foreach (var name in new[] { null, "", "Crate_ActorDef", "AIWaypoint_ActorDef",
                                         "Soldier_ActorDefExtra", "soldier_actordef", "Soldier" })
                if (decide(name))
                    yield return id + " non-candidate-suppressed: BaseDef name " +
                                 (name == null ? "<null>" : "'" + name + "'") + " is suppressed, and TFTV " +
                                 "would not have rolled it — GiveRankAndNameToHumaoidEnemy returns at :1617 " +
                                 "for anything that is not exactly \"Soldier_ActorDef\". Suppressing it stops " +
                                 "nothing and costs a log line for every deploy crate, waypoint, container, " +
                                 "structural target and scenery prop in the level (77 per client, 2026-08-08) " +
                                 "— which is how a real suppression becomes unfindable.";
        }
    }
}
