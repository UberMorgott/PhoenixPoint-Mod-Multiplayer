using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Base.Utils.GameConsole;
using HarmonyLib;
using Multiplayer.Network;

namespace RailCheck
{
    /// <summary>
    /// L341 — ON A CLIENT IN AN ACTIVE SESSION THE DEVELOPER CONSOLE ENDS EVERY OPEN ATTEMPT CLOSED; ON THE
    /// HOST, AND WITH NO SESSION, THE SAME ATTEMPT STILL OPENS IT.
    ///
    /// THE OUTCOME, not the call. BOTH halves are asserted on purpose and neither is sufficient alone: a law
    /// that only checked the client half is greenest of all when the console is disabled for EVERYBODY,
    /// which is the opposite of what was asked — the host's console is the authoritative one, the only one
    /// whose commands the rail replicates.
    ///
    /// THE DEFECT IT STANDS AGAINST. A client's console command writes that client's own campaign directly.
    /// The rail is a DIFF rail: it ships what changed on the HOST, so a value only the client moved is
    /// never contradicted and never repaired — the two campaigns drift apart with no error, no log, and no
    /// symptom until much later. Silent divergence is this codebase's dominant bug class; this is a whole
    /// input device for it.
    ///
    /// WHAT "OPEN" IS. <c>GameConsoleWindow.Visible =&gt; base.enabled</c> (decompile :82) and the ONE writer
    /// of that field is <c>ToggleVisibility</c> :222,
    /// <c>base.enabled = !DisableConsoleAccess &amp;&amp; !_instance.enabled</c>. Arm (a)-(d) execute exactly
    /// that expression against the mod's live decision (arms (a)-(c)), so they measure the resulting state and not
    /// the fact that a patch exists. Every route in reaches that one line — the ` / "/" hotkey
    /// (GameConsoleInput.cs:254-264) and the SNAPSHOT unlock code that clears the flag itself and then
    /// invokes ShowConsole (:212-236, :336-342).
    ///
    /// ARMS
    ///   (a) <c>client-console-opens</c> — a client's toggle on a CLOSED console must leave it closed.
    ///   (b) <c>host-console-blocked</c> — the HOST's toggle must still open it. This is the half that goes
    ///       red the moment the block stops asking about role.
    ///   (c) <c>solo-console-blocked</c> — no session, no lock. Single-player is not multiplayer.
    ///   (d) <c>funnel-bypassed</c> — the shipped prefix really is attached to
    ///       <c>GameConsoleWindow.ToggleVisibility</c>, really consults the decision arms (a)-(c) exercise,
    ///       and really touches <c>DisableConsoleAccess</c>. Without it the arms above are proved about code
    ///       the game never runs.
    ///   (e) <c>already-open-not-swept</c> — something closes a console that was open BEFORE the peer became
    ///       a client, by driving the same funnel. Requirement 3, and the ONLY arm that covers it: the
    ///       native toggle closes an open window on its own, so "an already-open console ends closed" is
    ///       unfalsifiable as an expression of :222 and can only be asserted as a seam that runs.
    ///   (f) <c>lock-outlives-the-session</c> — a postfix hands <c>DisableConsoleAccess</c> back, so quitting
    ///       to the menu does not leave the peer permanently consoleless.
    ///
    /// Falsify (each verified RED, then restored):
    ///   • <c>LockedFor =&gt; false</c> (the hole, reintroduced) → <c>client-console-opens</c>
    ///   • <c>LockedFor =&gt; sessionActive</c> (block the host too) → <c>host-console-blocked</c>
    ///   • drop the sweep's ToggleVisibility call → <c>already-open-not-swept</c>
    ///   • drop the Postfix → <c>lock-outlives-the-session</c>
    ///   • rename/move ConsoleLock or the console's own members → <c>premise-changed</c>
    /// </summary>
    internal static class L341_TheConsoleIsTheHostsAlone
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>GameConsoleWindow.cs:222, verbatim, with the flag the peer's toggle will actually see.
        /// TRUE = the window ends up OPEN.</summary>
        private static bool OpenAfterToggle(bool wasOpen, bool lockFlag) => !lockFlag && !wasOpen;

        internal static IEnumerable<string> Check()
        {
            var lockedFor = typeof(ConsoleLock).GetMethod("LockedFor", All);
            var locked = typeof(ConsoleLock).GetMethod("Locked", All);
            var prefix = typeof(ConsoleLock).GetMethod("Prefix", All);
            var postfix = typeof(ConsoleLock).GetMethod("Postfix", All);
            var sweep = typeof(ConsoleLockSweep).GetMethod("Prefix", All);
            var toggle = typeof(GameConsoleWindow).GetMethod("ToggleVisibility", All);
            var accessFlag = typeof(GameConsoleWindow).GetField("DisableConsoleAccess", All);
            if (lockedFor == null || locked == null || prefix == null || postfix == null || sweep == null ||
                toggle == null || accessFlag == null)
            {
                yield return "L341 premise-changed: ConsoleLock.LockedFor/Locked/Prefix/Postfix, " +
                             "ConsoleLockSweep.Prefix, GameConsoleWindow.ToggleVisibility or " +
                             "GameConsoleWindow.DisableConsoleAccess no longer resolves. The console is the " +
                             "one input device that writes a client's campaign with no intent and no delta, " +
                             "so with the block gone every arm below passes while a client's `set_resource` " +
                             "silently forks the save from the host's.";
                yield break;
            }

            // ── (a)-(c) the OUTCOME: the game's own line, run against the mod's own decision ─────────────
            // The flag a modded game holds when the player toggles is FALSE — PPML clears it at load
            // (ModManager.cs:76) — so the mod's decision is the only thing that can raise it.
            bool ClientLock() => ConsoleLock.LockedFor(sessionActive: true, isHost: false);
            if (OpenAfterToggle(wasOpen: false, lockFlag: ClientLock()))
                yield return "L341 client-console-opens: a client in an active session pressed the console " +
                             "key and the window ended up OPEN. Every command typed into it writes this " +
                             "peer's own campaign; the rail ships the HOST's diffs, so nothing ever " +
                             "contradicts the edit and the two campaigns drift apart with no error anywhere.";
            if (!OpenAfterToggle(wasOpen: false, lockFlag: ConsoleLock.LockedFor(sessionActive: true, isHost: true)))
                yield return "L341 host-console-blocked: the HOST's own console no longer opens. The host is " +
                             "authoritative — its console commands are the ones the rail replicates to every " +
                             "peer — so disabling it is not a stricter version of this law, it is the " +
                             "opposite of it, and it is what a client-only assertion would happily pass.";
            if (!OpenAfterToggle(wasOpen: false, lockFlag: ConsoleLock.LockedFor(sessionActive: false, isHost: false)))
                yield return "L341 solo-console-blocked: the console is refused with NO session active. " +
                             "Single-player has no rail to desync and no host to defer to; a peer who quit to " +
                             "the main menu is in exactly this state.";

            // ── (d) the arms above are about code the game runs ──────────────────────────────────────────
            var patch = typeof(ConsoleLock).GetCustomAttributes(typeof(HarmonyPatch), false)
                                           .Cast<HarmonyPatch>().FirstOrDefault();
            if (patch == null || patch.info.declaringType != typeof(GameConsoleWindow) ||
                patch.info.methodName != "ToggleVisibility")
                yield return "L341 funnel-bypassed: ConsoleLock is no longer attached to " +
                             "GameConsoleWindow.ToggleVisibility, the ONE writer of the window's enabled flag " +
                             "(:222). Patching a caller instead leaves every other caller open — the SNAPSHOT " +
                             "unlock code alone reaches it without the hotkey (GameConsoleInput.cs:226-230).";
            var modAsm = typeof(ConsoleLock).Assembly;
            if (!Program.Callees(prefix, modAsm).Any(c => c.MetadataToken == locked.MetadataToken) ||
                !Program.ReadsField(prefix, accessFlag))
                yield return "L341 funnel-bypassed: the shipped prefix does not consult ConsoleLock.Locked " +
                             "and raise GameConsoleWindow.DisableConsoleAccess, so arms (a)-(d) are proved " +
                             "about a decision function the console path never asks.";

            // ── (e) requirement 3: open BEFORE the session, closed by it ─────────────────────────────────
            if (!Program.Callees(sweep, modAsm).Any(c => c.MetadataToken == locked.MetadataToken) ||
                !Program.CalleeSequence(sweep).Any(c => c.MetadataToken == toggle.MetadataToken &&
                                                        c.Module == toggle.Module))
                yield return "L341 already-open-not-swept: nothing drives an already-open console through the " +
                             "funnel when this peer is a client. GameConsoleWindow.Update ticks only while the " +
                             "window is enabled, so it is the one seam that observes that case at all — " +
                             "without it the whole lock is bypassed by opening the console before joining.";

            // ── (f) the lock is borrowed, not kept ───────────────────────────────────────────────────────
            if (!Program.ReadsField(postfix, accessFlag))
                yield return "L341 lock-outlives-the-session: the postfix no longer hands " +
                             "DisableConsoleAccess back. The flag is the game's global console switch — held " +
                             "past the call it locks the player out of their own single-player console after " +
                             "they quit the session, which is a bug we would have shipped as a feature.";
        }
    }
}
