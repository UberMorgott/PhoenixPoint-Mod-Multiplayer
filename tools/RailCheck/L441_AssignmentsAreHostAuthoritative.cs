using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L441 — THE ASSIGNMENTS BOARD HAS EXACTLY ONE SIMULATOR, AND IT IS THE HOST.
    ///
    /// TFTV BaseRework's ASSIGNMENTS screen is the one surface where a client that keeps simulating costs
    /// REAL CAMPAIGN MONEY and says nothing: every assigned worker is worth +4 research/production
    /// (ResearchAndManufacturing.cs:14,114-117), the state lives in TFTV statics the rail cannot see, and it
    /// only ever persisted through the game's mod-save hooks — i.e. inside <c>ModData</c>, which the rail
    /// refuses by design. The two peers' economies drifted apart with no exception, no log line and no
    /// divergence report anywhere. <c>AssignSync</c> is the fix; this law is what keeps it a fix.
    ///
    /// THE DECISIONS ARE PURE FUNCTIONS, NOT A SHAPE. <c>AssignAuthority</c> takes booleans and returns a
    /// boolean, so the harness EXECUTES the real answer instead of a copy that can agree with itself while
    /// production disagrees (the <c>LoadBarrierAuthority</c> pattern).
    ///
    /// ARMS
    ///   (a) <c>POSITIVE CONTROL</c> — a host, in a live session, over a resolved Phoenix character with a
    ///       defined role byte, must ACCEPT. A law whose accept case never fires only ever proves that
    ///       everything is refused, which is also a broken surface.
    ///   (b) <c>authority-forged</c> — each of the five terms alone, inverted, must REFUSE. The role byte is
    ///       the one an attacker owns outright: it is raw wire input that ends up inside
    ///       <c>Enum.ToObject</c> and then inside a TFTV method.
    ///   (c) <c>mute-inverted</c> — every automation mute prefix is INVOKED, in the harness's own posture (no
    ///       engine ⇒ solo ⇒ run native), and must answer TRUE. This is the arm that survives the mutation
    ///       the wiring arm below cannot see: inverting <c>if (ShouldRunNative())</c> in a prefix still CALLS
    ///       <c>ShouldRunNative</c>, so only executing the prefix catches it. A false here means a solo
    ///       player's own auto-assign, eviction, training clock and completed-deployment popup are all dead.
    ///   (d) <c>mute-bypasses-the-one-door</c> — and every one of those prefixes must still REACH
    ///       <c>IntentRail.ShouldRunNative</c>, directly or through this file's one-line funnel. A prefix that
    ///       hard-codes <c>true</c> passes arm (c) and mutes nothing on a client.
    ///   (e) <c>capture-bypasses-the-one-door</c> — the same for the four gesture captures: a capture that
    ///       does not ask the door writes the client's own model, which is the block-first law (P4a).
    ///   (f) <c>decision-unwired</c> — the host handler must reach <c>AssignAuthority.AcceptAssignMove</c>,
    ///       and its refusal must reach <c>IntentRail.Reject</c>. A refusal that is log-only leaves the
    ///       gesturing client's board permanently ahead of the host (P7 — never log-only).
    ///   (g) <c>mute-spec-disagrees</c> — <c>AssignAuthority.ClientMutesAutomation</c> is the pure statement
    ///       of what that door means here, and its truth table is executed: a client outside an apply mutes;
    ///       a host, a solo peer and a client INSIDE an apply scope do not. The apply case is law 8 — an
    ///       apply that fires native events must never be blocked by the guard that stops it echoing.
    ///   (h) <c>root-unregistered</c> / <c>lifecycle-unwired</c> — "M#assign" must still be the key,
    ///       <c>Register</c> must still reach <c>IdentityResolver.RegisterModRoot</c>, and the three
    ///       lifecycle seams must still be reached from <c>SyncEngine</c>. The reload-boundary one is not
    ///       housekeeping: a mod root that is non-empty at a boundary gets captured by the post-reload
    ///       baseline walk, is therefore never emitted, and the client stays permanently stale until a full
    ///       resend (IdentityResolver.cs:205-206).
    ///
    /// Falsify: invert the gate in any mute prefix → (c); make a prefix `return true` → (d); drop the
    /// <c>ShouldRunNative</c> call from a capture → (e); call the TFTV method without asking
    /// <c>AcceptAssignMove</c> → (f); make <c>ClientMutesAutomation</c> ignore the apply scope → (g); drop
    /// <c>AssignSync.ResetForReloadBoundary</c> from <c>SyncEngine</c> → (h).
    /// </summary>
    internal static class L441_AssignmentsAreHostAuthoritative
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>The four automation mutes (§7 of the design). Each is a parameterless bool prefix, which
        /// is what lets arm (c) execute it.</summary>
        private static readonly string[] MuteClasses =
        {
            "TftvPersonnelAutomationMute", "TftvRecruitRegenMute",
            "TftvTrainingClockMute", "TftvCompletedDeploymentMute",
        };

        /// <summary>The four gesture captures (§8). Their prefixes take arguments, so they are checked by
        /// wiring rather than executed.</summary>
        private static readonly string[] CaptureClasses =
        {
            "TftvAssignWorkerCapture", "TftvUnassignWorkerCapture",
            "TftvTrainQueueCapture", "TftvTrainFinalizeCapture",
        };

        internal static IEnumerable<string> Check()
        {
            var asm = typeof(AssignSync).Assembly;
            var sync = typeof(AssignSync);
            var door = typeof(IntentRail).GetMethod("ShouldRunNative", All);
            var funnel = sync.GetMethod("RunNativeAutomation", All);
            var strictFunnel = sync.GetMethod("RunNativeSimulation", All);
            var resolve = sync.GetMethod("Resolve", All);
            var reject = sync.GetMethod("Reject", All);
            var register = sync.GetMethod("Register", All);
            var registerIntents = sync.GetMethod("RegisterIntents", All);
            var registerOps = typeof(IntentRail).GetMethod("RegisterOps", All);
            var engineCtor = typeof(SyncEngine).GetConstructors(All).FirstOrDefault();
            var rootKey = sync.GetField("RootKey", All);
            var handlers = new[] { "HandleAssignMove", "HandleAssignTrain", "HandleAssignFinalize" }
                .Select(n => new KeyValuePair<string, MethodInfo>(n, sync.GetMethod(n, All))).ToArray();
            var engineTick = typeof(SyncEngine).GetMethod("Tick", All);
            var engineBoundary = typeof(SyncEngine).GetMethod("ResetForReloadBoundary", All);
            var engineDetach = typeof(SyncEngine).GetMethod("DetachAllChannels", All);

            if (door == null || funnel == null || strictFunnel == null || resolve == null || reject == null ||
                register == null || registerIntents == null || registerOps == null || engineCtor == null ||
                rootKey == null || engineTick == null || engineBoundary == null || engineDetach == null ||
                handlers.Any(h => h.Value == null))
            {
                yield return "L441 premise-changed: AssignSync's authority seam no longer resolves " +
                             "(RunNativeAutomation / RunNativeSimulation / Resolve / the three host handlers / " +
                             "Reject / Register / RegisterIntents / RootKey, IntentRail.ShouldRunNative, " +
                             "IntentRail.RegisterOps, or the SyncEngine constructor and lifecycle entry points). " +
                             "TFTV internals are reached by reflection here, so an upstream or local rename " +
                             "makes this whole surface fail SILENTLY — re-point the law at whatever carries " +
                             "the decision now; do not delete it, because a client that keeps simulating " +
                             "assignments moves both campaigns' economies apart with nothing in any log";
                yield break;
            }

            // ═══ (a) POSITIVE CONTROL ═══
            if (!AssignAuthority.AcceptAssignMove(true, true, true, true, true))
                yield return "L441 POSITIVE CONTROL: an active host refused a resolved Phoenix character with " +
                             "a defined role, so NO assignment gesture can ever be applied and the ASSIGNMENTS " +
                             "board is frozen for every client.";

            // ═══ (b) each term alone must refuse ═══
            if (AssignAuthority.AcceptAssignMove(false, true, true, true, true))
                yield return "L441 authority-forged: a NON-HOST peer applies an assignment gesture. Host is the " +
                             "only simulator on this surface; a client that writes its own table diverges the " +
                             "research/production economy silently.";
            if (AssignAuthority.AcceptAssignMove(true, false, true, true, true))
                yield return "L441 authority-forged: an assignment gesture is applied outside a live session, " +
                             "where the mirror it would announce reaches nobody.";
            if (AssignAuthority.AcceptAssignMove(true, true, false, true, true))
                yield return "L441 authority-forged: an UNRESOLVED character id is accepted — the host would " +
                             "hand null to a TFTV method that dereferences it.";
            if (AssignAuthority.AcceptAssignMove(true, true, true, false, true))
                yield return "L441 authority-forged: a character that is NOT the shared Phoenix faction's is " +
                             "accepted, so a client intent can drive an NPC faction's personnel.";
            if (AssignAuthority.AcceptAssignMove(true, true, true, true, false))
                yield return "L441 authority-forged: an UNDEFINED role byte is accepted. The role is raw wire " +
                             "input that ends up inside Enum.ToObject and then inside a TFTV method — an " +
                             "out-of-range column is the cheapest thing a hostile peer can send.";

            // ═══ (g) the pure spec of the one door ═══
            if (!AssignAuthority.ClientMutesAutomation(true, false, false))
                yield return "L441 mute-spec-disagrees: a CLIENT in a live session outside an apply scope no " +
                             "longer mutes TFTV's automation — it would run its own auto-assign, eviction and " +
                             "training clocks against a table the host owns.";
            if (AssignAuthority.ClientMutesAutomation(true, false, true))
                yield return "L441 mute-spec-disagrees: automation stays muted INSIDE a delta apply. Law 8 is " +
                             "the opposite: an apply must be allowed to run the native code, because the guard " +
                             "exists to stop it ECHOING, not to stop it landing.";
            if (AssignAuthority.ClientMutesAutomation(true, true, false))
                yield return "L441 mute-spec-disagrees: the HOST mutes its own automation, so nobody simulates " +
                             "the board at all and every mirrored table is frozen.";
            if (AssignAuthority.ClientMutesAutomation(false, false, false))
                yield return "L441 mute-spec-disagrees: a SOLO player's automation is muted. Outside a session " +
                             "this mod owes the game exactly vanilla+TFTV behaviour.";

            // The stricter gate: same decision MINUS law 8's apply-scope carve-out, for the three funnels
            // that simulate. AttachCharacter fires on a MIRRORED character arrival, i.e. inside the apply
            // scope, and its tail auto-assigns — the carve-out is precisely what let the client write there.
            if (!AssignAuthority.ClientMutesSimulation(true, false))
                yield return "L441 mute-spec-disagrees: a client no longer mutes the SIMULATION funnels " +
                             "(auto-assign, living-capacity eviction, AttachCharacter), so a mirrored " +
                             "character arriving on a client assigns itself locally and the mirror never " +
                             "moves — drift that survives until the host next touches the table.";
            if (AssignAuthority.ClientMutesSimulation(true, true) ||
                AssignAuthority.ClientMutesSimulation(false, false))
                yield return "L441 mute-spec-disagrees: the SIMULATION mute fires for the host or for a solo " +
                             "player, so nobody assigns anybody and the board is frozen for everyone.";

            // ═══ (c)+(d) the mutes, EXECUTED and then wired ═══
            foreach (var name in MuteClasses)
            {
                var t = sync.GetNestedType(name, All);
                var prefix = t?.GetMethod("Prefix", All);
                if (prefix == null || prefix.ReturnType != typeof(bool) || prefix.GetParameters().Length != 0)
                {
                    yield return "L441 premise-changed: AssignSync." + name + " no longer carries a " +
                                 "parameterless bool Prefix, so the mute this law executes cannot be found. " +
                                 "Re-point the arm; an unexecuted mute is the whole defect class here";
                    continue;
                }
                bool ranNative = false;
                string threw = null;
                try { ranNative = (bool)prefix.Invoke(null, null); }
                catch (Exception ex)
                { threw = ((ex as TargetInvocationException)?.InnerException ?? ex).GetType().Name; }
                if (threw != null)
                {
                    yield return "L441 mute-inverted: AssignSync." + name + ".Prefix THREW headless (" + threw +
                                 ") — a prefix that can throw takes TFTV's method down with it inside a " +
                                 "Harmony patch, on every peer including the solo player";
                    continue;
                }
                if (!ranNative)
                    yield return "L441 mute-inverted: AssignSync." + name + ".Prefix answers FALSE with no " +
                                 "engine at all, i.e. for a SOLO player. The gate is inverted: on a client it " +
                                 "now lets TFTV simulate the board locally, and on a solo campaign it kills " +
                                 "auto-assign, living-capacity eviction, the training clocks and the " +
                                 "completed-deployment popup outright.";
                // Only the SIMULATION mute may answer through the stricter funnel. Every other prefix —
                // mute or capture — must keep law 8's apply-scope carve-out, or a delta apply would be
                // blocked from running the native code and the mirror's own write would echo back as an
                // intent. That is why strictFunnel is passed HERE and nowhere else.
                if (!Reaches(prefix, asm, door,
                             name == "TftvPersonnelAutomationMute" ? new MethodBase[] { funnel, strictFunnel }
                                                                   : new MethodBase[] { funnel }))
                    yield return "L441 mute-bypasses-the-one-door: AssignSync." + name + ".Prefix never reaches " +
                                 "IntentRail.ShouldRunNative nor AssignAuthority.ClientMutesSimulation. It " +
                                 "answers with something of its own, so the " +
                                 "client/host posture this surface depends on is decided in two places — which " +
                                 "is exactly the drift §7 forbids with 'no local copy of the decision'.";
            }

            // ═══ (c2) and the three SIMULATION funnels take the STRICTER gate ═══
            // Not interchangeable with the plain door: ShouldRunNative answers TRUE inside a delta apply
            // (law 8), and AttachCharacter fires on a MIRRORED character arrival — i.e. inside that very
            // scope — with a tail that auto-assigns. Gating those three on the plain door lets a client
            // assign people to itself while a delta is landing; the mirror does not move, so the drift
            // survives until the host next touches the table and no law but this arm can see it.
            var simulationMute = sync.GetNestedType("TftvPersonnelAutomationMute", All)?.GetMethod("Prefix", All);
            if (simulationMute != null &&
                !Program.Callees(simulationMute, asm).Any(m => m.MetadataToken == strictFunnel.MetadataToken))
                yield return "L441 simulation-mute-keeps-the-apply-carveout: " +
                             "AssignSync.TftvPersonnelAutomationMute.Prefix no longer reaches " +
                             "RunNativeSimulation. TryAutoAssignUnassignedPersonnel, EnforceLivingCapacity " +
                             "and AttachCharacter must be muted on a client WHETHER OR NOT a delta apply is " +
                             "on the stack — AttachCharacter is reached from the apply itself.";

            // ═══ (e) the captures ask the same door ═══
            foreach (var name in CaptureClasses)
            {
                var prefix = sync.GetNestedType(name, All)?.GetMethod("Prefix", All);
                if (prefix == null)
                {
                    yield return "L441 premise-changed: AssignSync." + name + ".Prefix no longer resolves — the " +
                                 "gesture capture it names is the only thing standing between a client's click " +
                                 "and its own model";
                    continue;
                }
                // The plain door ONLY: a capture routed through RunNativeSimulation would block the native
                // call inside a delta apply, so the mirror's own write would come back out as an intent.
                if (!Reaches(prefix, asm, door, funnel))
                    yield return "L441 capture-bypasses-the-one-door: AssignSync." + name + ".Prefix never " +
                                 "reaches IntentRail.ShouldRunNative — it either decides the posture itself, " +
                                 "so the client's click runs the native TFTV method against its OWN table " +
                                 "instead of becoming an intent (P4a), or it took the SIMULATION gate, which " +
                                 "drops law 8's apply-scope carve-out and turns the mirror's own write into " +
                                 "an echoed intent.";
            }

            // ═══ (f) the decision and the refusal are wired ═══
            if (!Program.Callees(resolve, asm).Any(m => m.Name == "AcceptAssignMove"))
                yield return "L441 decision-unwired: AssignSync.Resolve — the one preamble all three ops share " +
                             "— no longer asks AssignAuthority.AcceptAssignMove, so every arm above is " +
                             "asserting a function production does not consult.";
            foreach (var h in handlers)
                if (!Program.Callees(h.Value, asm).Any(m => m.MetadataToken == resolve.MetadataToken))
                    yield return "L441 decision-unwired: AssignSync." + h.Key + " bypasses AssignSync.Resolve, " +
                                 "i.e. it reaches a TFTV mutation with no authority preamble at all — the wire " +
                                 "id is handed straight to a method that moves personnel.";
            if (!Program.Callees(reject, asm).Any(m => m.Name == "Reject" && m.DeclaringType == typeof(IntentRail)))
                yield return "L441 reject-is-log-only: AssignSync.Reject no longer reaches IntentRail.Reject. A " +
                             "refusal that only logs leaves the gesturing client's board staged ahead of the " +
                             "host forever — host state did not change, so the diff rail ships nothing of its " +
                             "own and only the forced re-emit converges it (P7).";

            // ═══ (h) the root and the lifecycle ═══
            if (!string.Equals(rootKey.GetValue(null) as string, "M#assign", StringComparison.Ordinal))
                yield return "L441 root-unregistered: AssignSync.RootKey is no longer \"M#assign\". The whole " +
                             "surface is that one mod root on the 0xAC value rail; a renamed root is a root " +
                             "neither peer mirrors and every client's board silently stops moving.";
            if (!Program.Callees(register, asm).Any(m => m.Name == "RegisterModRoot"))
                yield return "L441 root-unregistered: AssignSync.Register no longer reaches " +
                             "IdentityResolver.RegisterModRoot, so the state class exists and nothing walks it.";
            // The gestures' half of the same wiring, and it fails in silence: 0xAF is PersonnelSync's
            // surface, so ops 13-15 exist only because RegisterIntents MERGES them into that family. Drop
            // either link and every ASSIGNMENTS click on a client dies as "unknown op" while the mirror keeps
            // working, which reads as a UI bug rather than a missing registration.
            if (!Program.Callees(engineCtor, asm).Any(m => m.MetadataToken == registerIntents.MetadataToken))
                yield return "L441 lifecycle-unwired: the SyncEngine constructor does not reach " +
                             "AssignSync.RegisterIntents, so ops 13-15 are never on the 0xAF table and every " +
                             "client assignment, training and finalize gesture is refused as an unknown op.";
            if (!Program.Callees(registerIntents, asm).Any(m => m.MetadataToken == registerOps.MetadataToken))
                yield return "L441 lifecycle-unwired: AssignSync.RegisterIntents no longer reaches " +
                             "IntentRail.RegisterOps. A plain IntentRail.Register there would be worse than " +
                             "nothing — it REPLACES the family table, taking PersonnelSync's ops 1-12 with it.";
            foreach (var pair in new[]
                     {
                         new KeyValuePair<MethodBase, string>(engineTick, "Tick"),
                         new KeyValuePair<MethodBase, string>(engineBoundary, "ResetForReloadBoundary"),
                         new KeyValuePair<MethodBase, string>(engineDetach, "Reset"),
                     })
            {
                var target = sync.GetMethod(pair.Value, All);
                if (target == null || !Program.Callees(pair.Key, asm).Any(m => m.MetadataToken == target.MetadataToken))
                    yield return "L441 lifecycle-unwired: SyncEngine." + pair.Key.Name + " does not reach " +
                                 "AssignSync." + pair.Value + ". For the reload boundary that is not " +
                                 "housekeeping: a mod root that is non-empty when the post-reload BASELINE walk " +
                                 "runs is captured by it and therefore never emitted, so the client keeps the " +
                                 "pre-reload table until somebody forces a full resend.";
            }
        }

        /// <summary>Does this prefix ask the ONE door — directly, or through one of the funnels it is
        /// ALLOWED to use? The funnel set is per-prefix on purpose (see the two call sites): accepting the
        /// stricter one everywhere is what let a gesture capture silently drop law 8's carve-out.</summary>
        private static bool Reaches(MethodBase from, Assembly asm, MethodBase door, params MethodBase[] funnels)
        {
            foreach (var callee in Program.Callees(from, asm))
            {
                if (callee.MetadataToken == door.MetadataToken) return true;
                foreach (var funnel in funnels)
                    if (callee.MetadataToken == funnel.MetadataToken &&
                        Program.Callees(funnel, asm).Any(m => m.MetadataToken == door.MetadataToken ||
                                                              (m.Name == "ClientMutesSimulation" &&
                                                               m.DeclaringType == typeof(AssignAuthority))))
                        return true;
            }
            return false;
        }
    }
}
