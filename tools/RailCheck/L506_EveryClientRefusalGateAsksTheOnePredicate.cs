using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L506 — EVERY CLIENT REFUSAL GATE ASKS THE ONE PREDICATE, AND CAPTURE KEEPS ITS OWN.
    ///
    /// THE SPLIT THIS LAW HOLDS IN PLACE. The mod's client-side gates are two different animals:
    ///   • REFUSAL gates skip a local mutation the host owns. A wrong refusal costs ONE stale value that the
    ///     next rail delta re-states, so a refusal gate must fail CLOSED — it must hold in the unknown window
    ///     (mid-load, mission boundary, the momentarily unset <c>Session.HostPeerId</c> right after a save
    ///     transfer). That is exactly where the 2026-08-14 research cascade came from.
    ///   • CAPTURE seams (<c>IntentRail.ShouldRunNative</c>, 96 call sites) redirect the player's input onto
    ///     the wire. A capture that fires with NO host link swallows the input and native never ran, and
    ///     nothing restores it — so capture must stay fail OPEN and keeps the <c>IsActiveSession</c> term.
    /// Unifying the two is the tempting mistake, and it turns every swallowed click into a lost action. Arm
    /// (e) is the tripwire for it.
    ///
    /// THE PREDICATE. <see cref="ClientAuthority.IsClient"/> — <c>Instance != null &amp;&amp; IsActive &amp;&amp; !IsHost</c>.
    /// It drops the session term deliberately, and it KEEPS <c>IsActive</c>, which is not decoration: it is a
    /// live single-player bug fix. <c>NetworkEngine.Shutdown()</c> (NetworkEngine.cs:225-252) clears
    /// <c>IsActive</c>/<c>IsHost</c> but leaves <c>Instance</c> NON-NULL (only <c>TearDown</c>:294 nulls it),
    /// so a peer that backed out of the lobby after a join attempt and then started a SOLO campaign satisfied
    /// the old "<c>engine != null &amp;&amp; !IsHost</c>" spelling — and had its research FROZEN in single
    /// player.
    ///
    /// THE ARMS:
    ///   (a) <c>registry</c> — every named refusal gate still exists, still carries <c>[HarmonyPatch]</c> and
    ///       still has a <c>Prefix</c>. RED if one is renamed or unhooked away from under the sweep.
    ///   (b) <c>derives-its-own</c> — IL sweep of each registered Prefix: EXACTLY ONE call to
    ///       <c>ClientAuthority.IsClient</c>, and ZERO calls to <c>NetworkEngine.get_Instance</c>,
    ///       <c>get_IsActiveSession</c> or <c>get_IsHost</c>. A gate that re-derives the predicate is how the
    ///       spellings drifted apart in the first place. The sweep is over <c>Prefix</c>, i.e. over the
    ///       REFUSAL DECISION. <c>EquipStorageGate</c> is the one registered gate that also carries a HOST
    ///       arm (<c>IsHost &amp;&amp; !SyncApplyScope.Active</c>, the 2026-07-18 revert guard); that arm is
    ///       not a client test and cannot be spelled with the client predicate, so it lives in its own
    ///       <c>HostOrSolo</c> method and the client refusal is the whole of the Prefix.
    ///   (c) <c>premise-changed</c> / <c>not-closed</c> — the predicate RUN, not read, outside Unity: it must
    ///       answer FALSE both with <c>Instance == null</c> (solo runs native) and for a <c>Shutdown()</c>
    ///       husk (<c>IsActive=false, IsHost=false</c>) — the state that froze research in single player.
    ///   (d) <c>unlisted-gate-derives</c> — anti-spread. Every <c>*Gate</c> type in the mod assembly minus an
    ///       explicit in-source allow-list; RED if a non-listed gate reads <c>IsHost</c>. Adding a gate must
    ///       force a deliberate listing, not a silent second spelling.
    ///   (e) <c>capture-went-closed</c> — <c>IntentRail.ShouldRunNative</c> must STILL read
    ///       <c>IsActiveSession</c> and must NOT call <c>IsClient</c>.
    ///
    /// Falsify: drop the IsActive term from IsClient → (c); make a converted gate derive its own predicate
    /// again → (b); make ShouldRunNative call IsClient() → (e).
    /// </summary>
    internal static class L506_EveryClientRefusalGateAsksTheOnePredicate
    {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static |
                                         BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>The CONVERTED refusal gates. Later steps extend this list; a gate is only in it once its
        /// prefix actually asks the one predicate.</summary>
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

        /// <summary>Gates that legitimately still read the engine themselves, each for a stated reason. This
        /// list is the whole point of arm (d): a NEW gate is RED until someone writes its name here or
        /// converts it.
        ///   • StatCommitApplyGate / SetItemsApplyGate — they gate the HOST inside SyncApplyScope; the one
        ///     predicate's negation would let the host revert a just-applied delta (the 2026-07-18/07-24 bug).
        ///   • GeoWindowCoverageGate, LoadBarrierGate, CurtainTakedownGate, GeoTeardownResetGate,
        ///     ViewStateExitLoudGate, ProgressGate — barrier/teardown/presence gates, not value refusals.
        ///   • ActorDamageClientGate, AccumulationClientGate, FumbleCheckGate — the tactical damage arm
        ///     (TacticalDamageSync.DamageIsSomebodyElses and its LiveEngine copies) answers "whose actor is
        ///     this", not "am I a client".
        ///   • ClientAiGate, ClientAiEvaluationSeamGate — the AI-evaluation doors, owned by L320/L472; the
        ///     door set, not this predicate, is what those laws quantify over.
        ///   • MissionCancelGate — a host-arm gate: it carries its own SyncApplyScope reasoning and refuses
        ///     on the HOST side of a replay, which the client predicate cannot express.
        ///   • The remainder are refusal gates NOT YET converted (steps 5..): each is scheduled, and the
        ///     listing is what keeps them visible instead of forgotten.</summary>
        private static readonly HashSet<string> AllowedToDerive = new HashSet<string>(StringComparer.Ordinal)
        {
            "StatCommitApplyGate", "SetItemsApplyGate",
            "GeoWindowCoverageGate", "LoadBarrierGate", "CurtainTakedownGate", "GeoTeardownResetGate",
            "ViewStateExitLoudGate", "ProgressGate",
            "ActorDamageClientGate", "AccumulationClientGate", "FumbleCheckGate",
            "ClientAiGate", "ClientAiEvaluationSeamGate", "MissionCancelGate",
            // not yet converted — steps 5..
            "CameraAbilityHintGate", "TacLaunchGate", "ClientDeployGate",
        };

        internal static IEnumerable<string> Check()
        {
            var isClient = typeof(ClientAuthority).GetMethod("IsClient", All);
            var instance = typeof(NetworkEngine).GetProperty("Instance", All);
            var shouldRunNative = typeof(IntentRail).GetMethod("ShouldRunNative", All);
            if (isClient == null || instance?.GetSetMethod(true) == null || shouldRunNative == null)
            {
                yield return "L506 premise-changed: ClientAuthority.IsClient, NetworkEngine.Instance or " +
                             "IntentRail.ShouldRunNative cannot be reached reflectively any more. Every arm " +
                             "below is defined over those three names — recalibrate before trusting a green run.";
                yield break;
            }

            // ── (a) the registry still names live, hooked gates ────────────────────────────────────────
            var prefixes = new List<Tuple<Type, MethodBase>>();
            foreach (var t in RefusalGates)
            {
                var p = t.GetMethod("Prefix", All);
                bool hooked = t.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0;
                if (p == null || !hooked)
                {
                    yield return "L506 registry: " + t.Name + " " + (p == null ? "has no Prefix" : "is not " +
                                 "[HarmonyPatch]-annotated") + ". A refusal gate that stopped being a patch " +
                                 "stopped refusing anything, and the sweep below would pass over its absence.";
                    continue;
                }
                prefixes.Add(Tuple.Create(t, (MethodBase)p));
            }

            // ── (b) each registered prefix ASKS, never derives ─────────────────────────────────────────
            foreach (var pair in prefixes)
            {
                var called = CallsIn(pair.Item2);
                int asks = called.Count(m => m.DeclaringType == typeof(ClientAuthority) && m.Name == "IsClient");
                var derived = called.Where(m => m.DeclaringType == typeof(NetworkEngine) &&
                                                (m.Name == "get_Instance" || m.Name == "get_IsActiveSession" ||
                                                 m.Name == "get_IsHost"))
                                    .Select(m => m.Name).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

                if (asks != 1)
                    yield return "L506 derives-its-own: " + pair.Item1.Name + ".Prefix calls " +
                                 "ClientAuthority.IsClient " + asks + " time(s), not exactly once. One reading " +
                                 "of the one predicate per refusal is the whole contract — zero means it went " +
                                 "back to its own spelling, more than one means the gate can disagree with " +
                                 "itself inside a single tick.";

                if (derived.Count > 0)
                    yield return "L506 derives-its-own: " + pair.Item1.Name + ".Prefix reads NetworkEngine " +
                                 "directly (" + string.Join(", ", derived) + "). Every such spelling drifted " +
                                 "apart before: the IsActiveSession term opens the gate in exactly the unknown " +
                                 "window the client is most dangerous in, and a missing IsActive term freezes " +
                                 "SINGLE PLAYER behind a Shutdown() husk. The predicate lives in " +
                                 "ClientAuthority and nowhere else.";
            }

            // ── (c) the predicate RUN: engine gone must mean native ────────────────────────────────────
            var saved = instance.GetValue(null);
            bool soloOpen, huskOpen;
            try
            {
                instance.SetValue(null, null);
                soloOpen = !(bool)isClient.Invoke(null, null);

                // The Shutdown() HUSK: engine present, IsActive false, IsHost false — the exact state a peer
                // sits in after backing out of the lobby, because Shutdown never nulls Instance.
                var husk = System.Runtime.Serialization.FormatterServices
                             .GetUninitializedObject(typeof(NetworkEngine));
                instance.SetValue(null, husk);
                huskOpen = !(bool)isClient.Invoke(null, null);
            }
            finally { instance.SetValue(null, saved); }

            if (!soloOpen)
                yield return "L506 not-closed: ClientAuthority.IsClient() answered TRUE with " +
                             "NetworkEngine.Instance null. No engine is SOLO, and a solo campaign that reads " +
                             "as a client has every refusal gate held against it — research frozen, haven " +
                             "assignments never rolled, in a game with no host to mirror from.";

            if (!huskOpen)
                yield return "L506 not-closed: ClientAuthority.IsClient() answered TRUE for an engine with " +
                             "IsActive=false and IsHost=false. That is not a client, it is the husk " +
                             "Shutdown() leaves behind (NetworkEngine.cs:246, Instance never nulled — only " +
                             "TearDown:294 does that). Without the IsActive term, a player who backs out of " +
                             "the lobby and then starts a SINGLE-PLAYER campaign reads as a client and every " +
                             "refusal gate holds: research frozen in solo, with no host to mirror from. That " +
                             "was a live bug, not a hypothetical.";

            // ── (d) anti-spread over every *Gate in the mod assembly ───────────────────────────────────
            int swept = 0;
            foreach (var t in ModTypes(typeof(ClientAuthority).Assembly))
            {
                if (!t.Name.EndsWith("Gate", StringComparison.Ordinal)) continue;
                swept++;
                if (AllowedToDerive.Contains(t.Name) || RefusalGates.Contains(t)) continue;

                var reads = t.GetMethods(All)
                             .SelectMany(CallsIn)
                             .Any(m => m.DeclaringType == typeof(NetworkEngine) && m.Name == "get_IsHost");
                if (reads)
                    yield return "L506 unlisted-gate-derives: " + t.Name + " reads NetworkEngine.IsHost but is " +
                                 "neither a converted refusal gate nor listed in AllowedToDerive. Either it is a " +
                                 "client refusal — then it asks ClientAuthority.IsClient like the rest — or it " +
                                 "is one of the deliberate exceptions (host-side apply gates, barrier/teardown " +
                                 "gates, the tactical ownership gates), and then it is named in the list with " +
                                 "its reason. A gate must not pick a third spelling in silence.";
            }
            if (swept == 0)
                yield return "L506 positive-control: the anti-spread sweep found NO *Gate type in the mod " +
                             "assembly. The mod ships dozens; a sweep over nothing cannot report a new one.";

            // ── (e) capture stays fail-OPEN ────────────────────────────────────────────────────────────
            var captureCalls = CallsIn(shouldRunNative);
            if (!captureCalls.Any(m => m.DeclaringType == typeof(NetworkEngine) && m.Name == "get_IsActiveSession"))
                yield return "L506 capture-went-closed: IntentRail.ShouldRunNative no longer reads " +
                             "IsActiveSession. It is a CAPTURE seam, not a refusal: 96 call sites hand it the " +
                             "player's input, and a capture that fires with no host link swallows that input " +
                             "with native never having run and nothing to restore it. Capture fails OPEN and " +
                             "keeps the session term; only refusal gates fail closed.";
            if (captureCalls.Any(m => m.DeclaringType == typeof(ClientAuthority) && m.Name == "IsClient"))
                yield return "L506 capture-went-closed: IntentRail.ShouldRunNative now asks " +
                             "ClientAuthority.IsClient. That is 'finishing the unification' in the one place it " +
                             "must not be finished — the refusal predicate holds in the unknown window by " +
                             "design, and holding a CAPTURE there turns every click made mid-load into a " +
                             "swallowed, unrecoverable action.";
        }

        /// <summary>Every method this body calls, resolved. Naive IL walk (same shape as L505's field scan):
        /// <c>call</c>/<c>callvirt</c> plus a 4-byte metadata token. A token that will not resolve is
        /// skipped rather than thrown on — the arms above only ever ask whether a KNOWN name is present.</summary>
        private static List<MethodBase> CallsIn(MethodBase m)
        {
            var found = new List<MethodBase>();
            byte[] il;
            try { il = m?.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null) return found;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6F) continue;      // call / callvirt
                try
                {
                    var target = m.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1));
                    if (target != null) found.Add(target);
                }
                catch { }
            }
            return found;
        }

        private static IEnumerable<Type> ModTypes(Assembly mod)
        {
            Type[] all;
            try { all = mod.GetTypes(); }
            catch (ReflectionTypeLoadException e) { all = e.Types.Where(t => t != null).ToArray(); }
            return all;
        }
    }
}
