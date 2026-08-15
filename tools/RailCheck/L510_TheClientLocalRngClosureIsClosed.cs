using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L510 — EVERY UNSEEDED GEOSCAPE DRAW A CLIENT CAN STILL REACH IS EITHER BEHIND A GATE OR REVIEWED.
    ///
    /// THE DEFECT THIS CLOSES. The mod's recurring failure is not "one more RNG site" — it is that each
    /// one is found by a player, in a session, and patched POINTWISE, with nothing that says whether the
    /// next one exists. There is no facade to hook: the geoscape assembly draws from an UNSEEDED
    /// <c>UnityEngine.Random</c> / a private <c>System.Random</c> in ~120 places across ~60 files
    /// (<c>GeoInitialWorldSetup</c> 14, <c>GeoHavenDefenseMission</c> 7, <c>GeoHaven</c> 6,
    /// <c>FactionCharacterGenerator</c> 6, <c>GeoFactionReward</c> 4, <c>GeoEventChoiceOutcome</c> 3, …),
    /// and <c>src/Rail/SurfaceIds.cs:39</c> records the same for tactical. A single RUNTIME interception
    /// seam is therefore impossible. What IS single is the DECISION — <c>ClientAuthority.IsClient</c>
    /// (L506) — so the closure has to be proved at BUILD time, over the call graph, and that is this law.
    ///
    /// A client-minted roll is the worst thing the rail can be handed: the diff is host-now vs
    /// host-before, so a value only the client rolled is never mentioned again and no re-emit can heal it
    /// (the S#75/S#76 CRC argument L480 was born from).
    ///
    /// ── THE CLOSURE, EXACTLY ────────────────────────────────────────────────────────────────────────
    /// ENTRY SET (declared, not derived — see the LIMIT below). Five client-LOCAL entries, each a place a
    /// client's own unfrozen clock or its own click reaches native geoscape code:
    ///   • <c>GeoLevelController.LevelHourlyUpdateCrt</c> — the hourly sim tick (ClientSimGate's target).
    ///   • <c>GeoPhoenixFaction.OnResearchCompleted</c> — GeoPhoenixFaction.cs:1069, research completion.
    ///   • <c>GeoPhoenixFaction.OnSiteFirstTimeVisited</c> — :1168, first haven visit.
    ///   • <c>GeoscapeEvent.CompleteEvent</c> — GeoscapeEvent.cs:86, the event-choice outcome.
    ///   • <c>GeoFactionReward.Apply</c> — GeoFactionReward.cs:110, the reward grant.
    /// DRAW SET (derived, never named): a call to any member of <c>UnityEngine.Random</c> except
    /// <c>InitState</c>/<c>state</c> (seeding, not drawing), or to any member of <c>System.Random</c>.
    /// The game's own helpers are deliberately NOT listed — <c>UnityUtil.GetRandomElement</c>:416,
    /// <c>WeightedRandomElement</c>:437, <c>Shuffle</c>:478 all bottom out in <c>Random.Range</c>, so the
    /// transitive walk finds them and a NEW wrapper needs no edit here.
    /// FRONTIER: the walk is PRUNED at every method a converted client refusal gate is bound to (targets
    /// read off the gate types themselves, so a retargeted or unhooked gate re-opens the walk).
    /// DEPTH: <see cref="Depth"/> call levels from each entry, visited-capped at <see cref="MaxVisited"/>
    /// methods; hitting either cap is REPORTED, never silently absorbed.
    ///
    /// ── WHAT IT PROVABLY DOES NOT COVER (read this before trusting a green) ─────────────────────────
    ///   • The entry set is DECLARED. "Which native methods does a client actually run" is not derivable
    ///     from IL — it needs a runtime trace — so a client-local entry nobody wrote down here is invisible
    ///     to this law. Adding one is the maintenance this law asks for.
    ///   • Reflection, delegates stored in fields, and virtual dispatch to an override the caller's IL does
    ///     not name are NOT followed (the walk resolves the IL token, i.e. the STATIC target). Interface
    ///     and abstract calls therefore stop at the declaring signature.
    ///   • Beyond <see cref="Depth"/> levels the walk stops. Deeper draws exist and are not asserted about.
    ///   • It says nothing about TACTICAL RNG, and nothing about whether a gate that IS bound actually
    ///     refuses — that is L506 (the predicate) and L480/L451 (the behaviour), which this law leans on.
    ///
    /// POSITIVE CONTROL. With the frontier EMPTIED the same walk must find draws, and with it applied the
    /// hourly entry must contribute none. A walk that finds nothing either way is broken, not clean, and
    /// would otherwise print green over the whole point of the law.
    ///
    /// Falsify: retarget or unhook <c>ClientSimGate</c> → the hourly tick stops being pruned and every
    /// draw the hourly sim reaches is named; delete an allow-list row that is still reachable → that row
    /// is named as an ungated draw; make the allow-list name a site the walk no longer reaches →
    /// <c>allow-list-stale</c>.
    /// </summary>
    internal static class L510_TheClientLocalRngClosureIsClosed
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>Call levels walked from each entry. Correct-and-bounded beats exhaustive-and-hung: an
        /// unbounded walk over the geoscape assembly is a minutes-long sweep on every RailCheck run.</summary>
        private const int Depth = 4;

        /// <summary>Hard cap on visited methods. Hitting it is REPORTED (scan-truncated), never absorbed —
        /// a truncated walk that printed green would be the exact "green by omission" this repo kills.</summary>
        private const int MaxVisited = 40000;

        /// <summary>The converted client refusal gates (L506's registry). Their Harmony targets are the
        /// frontier: past one of them the client never executes, so nothing behind it can draw.</summary>
        private static readonly Type[] RefusalGates =
        {
            typeof(ClientResearchGate),
            typeof(HavenResearchGate),
            typeof(FacilityPowerGate),
            typeof(AutomanufactureGate),
            typeof(ClientSimGate),
            typeof(EquipStorageGate),
            typeof(VehicleArrivalGate),
            typeof(SiteExploredOutcomeGate),
            typeof(GeoscapeEventRaiseGate),
            typeof(VehicleGestureGate),
            typeof(FactionSpawnerGestureGate),
        };

        /// <summary>REVIEWED draws: reachable from a client-local entry, not behind a refusal gate, and
        /// deliberately left native — each with the reason a gate would be WRONG there. A row here is a
        /// decision, not a suppression: the law also reports a row the walk no longer reaches, so the list
        /// cannot rot into a permanent hole.</summary>
        private static readonly Dictionary<string, string> AllowList =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // All three sit UNDER GeoscapeEvent.CompleteEvent (GeoscapeEvent.cs:86 → :101
                // GenerateFactionReward → :102 Apply), which is covered by a CAPTURE seam and by two
                // instance-level blocks, not by a refusal gate — deliberately, and a gate here would be the
                // "half-gating" that costs more than it buys:
                //   • the player's choice click is BLOCKED at its presentation seam
                //     (EventSync.EventChoiceClientLock, block-first per IntentRail.ShouldRunNative) and
                //     relayed as an 0xB4 answer; the HOST runs CompleteEvent and the outcome reaches every
                //     peer as ordinary 0xAC deltas plus the 0xBD reward payload;
                //   • the game's own trigger-time auto-answer of a single-choice event
                //     (GeoscapeEventSystem.OnEventTriggered:651-655) is unreachable on a client because
                //     GeoscapeEventRaiseGate refuses OnGeoscapeEvent, its only caller (:622);
                //   • the mirrored-instance route (UIModuleSiteEncounters.SelectChoice:598,
                //     "if (!ev.IsCompleted) ev.CompleteEvent(...)") is closed by EventPopup marking the
                //     instance completed (MarkResolvedInstance) plus StubReward.
                // Why NOT a refusal gate: CompleteEvent RETURNS the GeoFactionReward its callers immediately
                // dereference (SelectChoice:604, SetClosingEncounter:357), so a prefix that skips it hands
                // them null — the NRE class EventPopup's StubReward exists to prevent. A fourth mechanism on
                // an already-covered path, whose failure mode is a crash rather than a stale value, is worse
                // than the three that are there.
                { "UnityUtil.GetRandomElement",
                  "GeoEventChoiceOutcome:421/:427/:548 pick a site/haven for an encounter or diplomatic " +
                  "objective while the host resolves the choice; a client never runs the resolution." },
                { "RangeDataInt.RandomValue",
                  "GeoEventChoiceOutcome:172/:245 roll the outcome's resource/weight ranges inside the same " +
                  "host-side resolution; the amounts reach the client as wallet deltas + the 0xBD reward." },
                { "FactionCharacterGenerator.GeneratePersonalAbilities",
                  "A reward that grants Units MINTS soldiers — GeoEventChoiceOutcome:294/:302 " +
                  "(CharacterGenerator.GenerateUnit) inside GenerateFactionReward, i.e. still inside the " +
                  "host's resolution. Host-only for the sharpest form of the same reason: a soldier the " +
                  "CLIENT rolled is a ROOT the host-now-vs-host-before diff never mentions again." },
            };

        internal static IEnumerable<string> Check(Assembly game)
        {
            // ── the entries still resolve ───────────────────────────────────────────────────────────────
            var entries = new List<KeyValuePair<string, MethodBase>>();
            var missing = new List<string>();
            foreach (var e in EntrySpecs())
            {
                var m = Resolve(game, e.Key, e.Value);
                if (m == null) missing.Add(e.Key + "." + e.Value);
                else entries.Add(new KeyValuePair<string, MethodBase>(e.Key + "." + e.Value, m));
            }
            if (missing.Count > 0)
            {
                yield return "L510 premise-changed: the client-local entry point(s) [" +
                             string.Join(", ", missing) + "] no longer resolve in the game assembly. The " +
                             "closure below is DEFINED over those entries, so a green run with one of them " +
                             "missing is a walk over a smaller game than the one that ships — re-derive the " +
                             "entry set from the decompile before trusting it.";
                yield break;
            }

            // ── the frontier is real ────────────────────────────────────────────────────────────────────
            var frontier = new HashSet<MethodBase>();
            foreach (var g in RefusalGates)
                foreach (var t in GateTargets(g))
                    frontier.Add(t);
            if (frontier.Count < RefusalGates.Length)
            {
                yield return "L510 premise-changed: only " + frontier.Count + " Harmony target(s) could be " +
                             "read off " + RefusalGates.Length + " refusal gate(s). The frontier IS the gate " +
                             "set — a walk pruned by a frontier it could not read reports whatever it likes.";
                yield break;
            }

            // ── POSITIVE CONTROL: the walk is live, and the gates are what stop it ──────────────────────
            bool truncatedOpen;
            var openDraws = Draws(entries, new HashSet<MethodBase>(), game, out truncatedOpen);
            if (openDraws.Count == 0)
                yield return "L510 positive-control: with the gate frontier EMPTIED the walk found ZERO " +
                             "unseeded draws reachable from the client-local entries. The geoscape assembly " +
                             "draws in ~120 places, so this is a broken IL walk, not a clean game — every " +
                             "arm below would print green by seeing nothing.";

            bool truncated;
            var reached = Draws(entries, frontier, game, out truncated);
            if (truncated || truncatedOpen)
                yield return "L510 scan-truncated: the walk hit its " + MaxVisited + "-method cap, so the set " +
                             "below is a PREFIX of the reachable draws and its silence about the rest means " +
                             "nothing. Raise the cap or narrow the entry set deliberately.";

            var hourlyOnly = Draws(entries.Where(e => e.Key.EndsWith("LevelHourlyUpdateCrt", StringComparison.Ordinal)),
                                   frontier, game, out _);
            if (hourlyOnly.Count > 0)
                yield return "L510 hourly-unpruned: the hourly sim tick still reaches " + hourlyOnly.Count +
                             " unseeded draw(s) WITH the frontier applied (" + Sample(hourlyOnly) + "). " +
                             "ClientSimGate is supposed to be bound to exactly that method, so either it is " +
                             "retargeted, unhooked, or the target the walk starts from is not the one the gate " +
                             "patches — and the whole hourly sim rolls locally on every client.";

            // ── the closure ─────────────────────────────────────────────────────────────────────────────
            var ungated = reached.Keys.Where(k => !AllowList.ContainsKey(k))
                                 .OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (ungated.Count > 0)
                yield return "L510 ungated-draw: " + ungated.Count + " geoscape method(s) reachable within " +
                             Depth + " call level(s) of a CLIENT-LOCAL entry point draw from an unseeded RNG " +
                             "with no client refusal gate between them and the client: [" +
                             string.Join(", ", ungated.Take(12).Select(k => k + " (via " + reached[k] + ")")) +
                             (ungated.Count > 12 ? ", …" : "") +
                             "]. Each is a value the client MINTS locally; the diff is host-now vs host-before, " +
                             "so it is never mentioned again and no re-emit heals it. Gate it the way " +
                             "ClientSimGate/HavenResearchGate do (refuse, wait for the host value on the rail), " +
                             "or put it on this law's AllowList with the reason a gate would be wrong.";

            var stale = AllowList.Keys.Where(k => !reached.ContainsKey(k))
                                 .OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (stale.Count > 0)
                yield return "L510 allow-list-stale: [" + string.Join(", ", stale) + "] are allow-listed as " +
                             "reviewed client-reachable draws, but the walk no longer reaches them. Either the " +
                             "game moved and the reason is now about nothing, or a gate started covering them " +
                             "— either way the row is a hole nobody is reviewing. Re-derive it or delete it.";
        }

        /// <summary>Type-name → method-name entry pairs. Named by STRING so a namespace move is reported as
        /// a premise change rather than as a compile error in the harness.</summary>
        private static IEnumerable<KeyValuePair<string, string>> EntrySpecs()
        {
            yield return new KeyValuePair<string, string>(
                "PhoenixPoint.Geoscape.Levels.GeoLevelController", "LevelHourlyUpdateCrt");
            yield return new KeyValuePair<string, string>(
                "PhoenixPoint.Geoscape.Levels.Factions.GeoPhoenixFaction", "OnResearchCompleted");
            yield return new KeyValuePair<string, string>(
                "PhoenixPoint.Geoscape.Levels.Factions.GeoPhoenixFaction", "OnSiteFirstTimeVisited");
            yield return new KeyValuePair<string, string>(
                "PhoenixPoint.Geoscape.Events.GeoscapeEvent", "CompleteEvent");
            yield return new KeyValuePair<string, string>(
                "PhoenixPoint.Geoscape.Core.GeoFactionReward", "Apply");
        }

        private static MethodBase Resolve(Assembly game, string typeName, string method)
        {
            Type t = null;
            try { t = game.GetType(typeName, false); } catch { }
            if (t == null) return null;
            try { return t.GetMethods(All).FirstOrDefault(m => m.DeclaringType == t && m.Name == method); }
            catch { return null; }
        }

        /// <summary>Every game method a gate class is bound to: <c>TargetMethod</c>/<c>TargetMethods</c> when
        /// the gate computes its own set, the <c>[HarmonyPatch]</c> attribute when it declares one. Both
        /// shapes ship in <c>src/Rail/ClientSimGate.cs</c>, so reading only one would silently drop gates.</summary>
        private static IEnumerable<MethodBase> GateTargets(Type gate)
        {
            var one = gate.GetMethod("TargetMethod", All);
            if (one != null && one.IsStatic)
            {
                object r = null;
                try { r = one.Invoke(null, null); } catch { }
                if (r is MethodBase mb) yield return mb;
            }
            var many = gate.GetMethod("TargetMethods", All);
            if (many != null && many.IsStatic)
            {
                IEnumerable<MethodBase> r = null;
                try { r = many.Invoke(null, null) as IEnumerable<MethodBase>; } catch { }
                if (r != null) foreach (var m in r) if (m != null) yield return m;
            }
            foreach (HarmonyPatch a in gate.GetCustomAttributes(typeof(HarmonyPatch), false))
            {
                var info = a.info;
                if (info?.declaringType == null || string.IsNullOrEmpty(info.methodName)) continue;
                MethodBase m = null;
                try
                {
                    m = info.argumentTypes != null
                        ? AccessTools.Method(info.declaringType, info.methodName, info.argumentTypes)
                        : AccessTools.Method(info.declaringType, info.methodName);
                }
                catch { }
                if (m != null) yield return m;
            }
        }

        /// <summary>Breadth-first over the STATIC call graph from the given roots, pruning at the frontier,
        /// bounded by <see cref="Depth"/> and <see cref="MaxVisited"/>. Returns the drawing methods keyed by
        /// <c>Type.Method</c>, valued by the depth they were first seen at.
        ///
        /// TWO walks per method, deliberately: <c>Program.Callees(m, game)</c> takes every InlineMethod
        /// operand in the GAME assembly — <c>newobj</c> and <c>ldftn</c> included, which is the only way a
        /// lambda or an iterator body is an edge at all — while <c>Program.CalleeSequence</c> drops the
        /// assembly filter, which is the only way <c>UnityEngine.Random</c> (a different assembly) is
        /// visible. Either walk alone reports a graph that is missing exactly the thing the law is about.</summary>
        private static Dictionary<string, string> Draws(IEnumerable<KeyValuePair<string, MethodBase>> roots,
                                                        HashSet<MethodBase> frontier, Assembly game,
                                                        out bool truncated)
        {
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            var seen = new Dictionary<MethodBase, string>();
            var level = new List<MethodBase>();
            truncated = false;
            foreach (var r in roots)
                if (r.Value != null && !frontier.Contains(r.Value) && !seen.ContainsKey(r.Value))
                { seen[r.Value] = r.Key; level.Add(r.Value); }

            for (int d = 0; d < Depth && level.Count > 0; d++)
            {
                var next = new List<MethodBase>();
                foreach (var m in level)
                {
                    string via = seen[m];
                    foreach (var c in Program.CalleeSequence(m))
                        if (IsDraw(c)) { var k = Key(m); if (!found.ContainsKey(k)) found[k] = via; }

                    if (d + 1 >= Depth) continue;
                    foreach (var c in Program.Callees(m, game))
                    {
                        if (c == null || frontier.Contains(c)) continue;
                        if (seen.Count >= MaxVisited) { truncated = true; break; }
                        if (!seen.ContainsKey(c)) { seen[c] = via; next.Add(c); }
                        // A lambda or an iterator reaches its body through the closure TYPE, not through the
                        // method token the caller emits, so pull the whole compiler-generated type in.
                        var dt = c.DeclaringType;
                        if (dt == null || !dt.IsNested || dt.Name.IndexOf('<') < 0) continue;
                        MethodInfo[] gen;
                        try { gen = dt.GetMethods(All); } catch { continue; }
                        foreach (var g in gen)
                            if (g.DeclaringType == dt && !frontier.Contains(g) && !seen.ContainsKey(g))
                            { seen[g] = via; next.Add(g); }
                    }
                    if (truncated) break;
                }
                if (truncated) break;
                level = next;
            }
            return found;
        }

        /// <summary>The PRIMITIVE entropy sources, taken from the engine rather than from a name list: the
        /// game's own helpers all bottom out here (UnityUtil.cs:416/437/478), so the walk finds a new wrapper
        /// with no edit. <c>InitState</c>/<c>state</c> are seeding, not drawing — the map generator's
        /// deterministic path uses them and is not a divergence.</summary>
        private static bool IsDraw(MethodBase c)
        {
            var dt = c?.DeclaringType;
            if (dt == null) return false;
            if (dt.FullName == "System.Random") return true;
            if (dt.FullName != "UnityEngine.Random") return false;
            return c.Name != "InitState" && c.Name != "set_state" && c.Name != "get_state";
        }

        private static string Key(MethodBase m) =>
            (m.DeclaringType == null ? "?" : m.DeclaringType.Name) + "." + m.Name;

        private static string Sample(Dictionary<string, string> d) =>
            string.Join(", ", d.Keys.OrderBy(k => k, StringComparer.Ordinal).Take(6));
    }
}
