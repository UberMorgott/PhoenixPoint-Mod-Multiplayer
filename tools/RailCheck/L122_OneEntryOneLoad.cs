using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;

namespace RailCheck
{
    /// <summary>
    /// L122 — ONE SESSION ENTRY INTO A TACTICAL LEVEL, ONE LOAD PER PEER.
    ///
    /// The failure (live 3-instance test, 2026-08-05): loading straight into a tactical battle from a save.
    /// The first load was correct AND simultaneous — one 995,978 B blob, <c>host reveal released: AllDone
    /// (loadedClients=2)</c>. Then a SECOND transfer fired on top of it: <c>host tactical level Playing —
    /// waiting for deploy-ready</c> → <c>deploy-ready after 21 frame(s) → mid-tactical save transfer</c> →
    /// another ~996 KB blob → <c>blob sent … (reveal-hold armed at launch, no self-enter)</c>. Only the
    /// CLIENTS reloaded; the host stayed in the level it was already in.
    ///
    /// WHY IT IS AN ARM AND NOT A BETTER PREDICATE. <c>TacDeployReadyCapture</c> rides
    /// <c>TacticalLevelController.OnLevelStateChanged(Playing)</c>, guarded only by IsHost + SessionStarted.
    /// <c>Playing</c> is a fact about the LEVEL, and the two cases it has to separate are facts about how the
    /// peers GOT there: "I launched this battle from the geoscape, nobody else has it, ship it" versus "every
    /// peer consumed one blob into this same level, shipping it again reloads them". Nothing observable at
    /// <c>Playing</c> distinguishes those. The one place that knows is the geo→tac seam itself
    /// (<c>TacLaunchGate.Prefix</c>, which already arms <c>OpenTacticalEntryBarrier</c> for the identical
    /// reason). Same shape as 858eee3: ARM AT THE TRANSITION, GUARD ON THE ARM.
    ///
    /// THE ARMS:
    ///   (a) PREMISE — the entry family resolves.
    ///   (b) THE ARM IS AT THE TRANSITION: <c>TacLaunchGate.Prefix</c> arms, on the same host branch that
    ///       opens the reveal barrier.
    ///   (c) THE CAPTURE GUARDS ON IT, AND GUARDS FIRST: <c>TacDeployReadyCapture.Postfix</c> consumes the
    ///       arm BEFORE it hands the deploy-ready coroutine to Timing. The second blob is written by that
    ///       coroutine, so a guard behind it decides nothing.
    ///   (d) EXECUTED: THE ARM IS ONE-SHOT. Arm once, consume twice: true then false. A guard that reads
    ///       without clearing passes again on the next <c>Playing</c> — the same double transfer one beat
    ///       later — and a purely static law would never see it.
    ///   (e) THE GUARDED PATH IS THE ONLY PATH: <c>HostBeginTacticalEntryTransfer</c> — the mid-tactical
    ///       write + <c>SendBlob</c> — has exactly ONE caller in the mod, and it is the deploy-ready
    ///       coroutine behind the guard. This also holds the other half: the ordinary save transfer
    ///       (<c>HostSerializeAndSendCrt</c>, the FIRST and entirely correct blob in the report) must NOT be
    ///       among the callers, because the cure for a duplicate load must not be reached by suppressing the
    ///       load.
    ///
    /// Falsify (verified to go RED, then restored):
    ///   • drop the ConsumeSessionEntry() guard from TacDeployReadyCapture.Postfix → capture-not-guarded-by-entry
    ///   • make ConsumeSessionEntry() return the flag without clearing it   → entry-arm-not-one-shot
    /// </summary>
    internal static class L122_OneEntryOneLoad
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var mod = typeof(SaveTransferCoordinator).Assembly;
            var launch = mod.GetType("Multiplayer.Tactical.TacLaunchGate");
            var capture = mod.GetType("Multiplayer.Tactical.TacDeployReadyCapture");

            var prefix = launch?.GetMethod("Prefix", All);
            var postfix = capture?.GetMethod("Postfix", All);
            var arm = launch?.GetMethod("ArmSessionEntry", All);
            var consume = launch?.GetMethod("ConsumeSessionEntry", All);
            var crt = capture?.GetMethod("CaptureWhenPlayableCrt", All);
            var openBarrier = typeof(SaveTransferCoordinator).GetMethod("OpenTacticalEntryBarrier", All);
            var beginTransfer = typeof(SaveTransferCoordinator).GetMethod("HostBeginTacticalEntryTransfer", All);

            if (prefix == null || postfix == null || arm == null || consume == null || crt == null ||
                openBarrier == null || beginTransfer == null)
            {
                yield return "L122 premise-changed: the tactical-entry family no longer resolves " +
                             "(TacLaunchGate.Prefix/ArmSessionEntry/ConsumeSessionEntry, " +
                             "TacDeployReadyCapture.Postfix/CaptureWhenPlayableCrt, SaveTransferCoordinator." +
                             "OpenTacticalEntryBarrier/HostBeginTacticalEntryTransfer). Every arm below would " +
                             "pass vacuously, so 'one entry, one load' is UNCHECKED rather than satisfied";
                yield break;
            }

            // ═══ (b) THE ARM IS AT THE TRANSITION ═══
            if (!Reaches(prefix, arm, mod))
                yield return "L122 entry-not-armed-at-the-transition: TacLaunchGate.Prefix no longer arms the " +
                             "session entry. LaunchTacticalGame is the ONLY moment that knows this battle is " +
                             "being CREATED rather than loaded; without the arm the capture below either fires " +
                             "for every Playing (the double load) or for none (clients never get the battle)";
            if (!Reaches(prefix, openBarrier, mod))
                yield return "L122 premise-changed: TacLaunchGate.Prefix no longer opens the tactical entry " +
                             "barrier, so the arm is no longer sitting on the host branch of the geo→tac seam " +
                             "this law reasons about";

            // ═══ (c) THE CAPTURE GUARDS ON IT, AND GUARDS FIRST ═══
            // Callees comes back in IL order, so "before" is answerable without a second walker.
            var order = Program.Callees(postfix, mod).ToList();
            int atConsume = order.FindIndex(c => Same(c, consume));
            int atCrt = order.FindIndex(c => Same(c, crt));
            if (atConsume < 0)
                yield return "L122 capture-not-guarded-by-entry: TacDeployReadyCapture.Postfix fires on ANY " +
                             "host tactical level reaching Playing without asking whether this peer LAUNCHED " +
                             "it. A battle loaded from a blob every peer already consumed is not an entry: the " +
                             "capture writes a second ~996 KB mid-tactical save and reloads the clients out of " +
                             "the level they just loaded into, while the host stays in it";
            else if (atCrt >= 0 && atCrt < atConsume)
                yield return "L122 capture-not-guarded-by-entry: TacDeployReadyCapture.Postfix consumes the " +
                             "entry arm only AFTER it has already built the deploy-ready coroutine. That " +
                             "coroutine is what writes the second blob, so a guard behind it decides nothing";

            // ═══ (d) EXECUTED: THE ARM IS ONE-SHOT ═══
            arm.Invoke(null, null);
            bool first = (bool)consume.Invoke(null, null);
            bool second = (bool)consume.Invoke(null, null);
            if (!first)
                yield return "L122 entry-arm-does-not-arm: ArmSessionEntry() followed by " +
                             "ConsumeSessionEntry() answered false, so a battle the host genuinely LAUNCHED " +
                             "would never be shipped and no client would ever receive it";
            if (second)
                yield return "L122 entry-arm-not-one-shot: a SECOND ConsumeSessionEntry() with no intervening " +
                             "launch still answered true. The guard reads without clearing, so the very next " +
                             "tactical level to reach Playing — the one every peer loaded from one blob — " +
                             "passes it and ships the duplicate transfer this law exists to stop, one beat " +
                             "later and with nothing in the log to say why";

            // ═══ (e) THE GUARDED PATH IS THE ONLY PATH ═══
            var callers = mod.GetTypes()
                             .SelectMany(Methods)
                             .Where(m => Reaches(m, beginTransfer, mod))
                             .ToList();
            if (callers.Count != 1 || !Owner(callers[0]).Contains("TacDeployReadyCapture"))
                yield return "L122 second-entry-point-to-the-transfer: HostBeginTacticalEntryTransfer (the " +
                             "mid-tactical save write + SendBlob) is reached from " + callers.Count +
                             " place(s) — [" + string.Join(", ", callers.Select(
                                 c => Owner(c) + "." + c.Name).ToArray()) + "]. Exactly one is allowed, and it " +
                             "is the deploy-ready coroutine behind the entry guard. Any other caller — the " +
                             "ordinary save transfer HostSerializeAndSendCrt above all, which is the FIRST and " +
                             "entirely correct blob in the report — carries no arm and no guard, so this law " +
                             "would stay green while a second blob went out for a level the peers already hold";
        }

        private static IEnumerable<MethodBase> Methods(Type t)
        {
            try { return t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All)); }
            catch { return Enumerable.Empty<MethodBase>(); }
        }

        private static string Owner(MethodBase m) =>
            m.DeclaringType == null ? "?" : m.DeclaringType.FullName;

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;

        private static bool Reaches(MethodBase from, MethodBase target, Assembly asm) =>
            from != null && target != null && Program.Callees(from, asm).Any(c => Same(c, target));
    }
}
