using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Base.Utils.GameConsole;
using HarmonyLib;
using Multiplayer.Network;
using UnityTools.AnvilCheats;

namespace RailCheck
{
    /// <summary>
    /// L342 — ON A CLIENT IN AN ACTIVE SESSION EVERY CHEAT-OPEN PATH ENDS WITH THE CHEAT MENU CLOSED AND
    /// EVERY CHEAT COMMAND REFUSED; ON THE HOST, AND WITH NO SESSION, THE SAME PATHS STILL WORK.
    ///
    /// THE OUTCOME, not the call. BOTH halves are asserted on purpose and neither is sufficient alone: a law
    /// that only checked the client half is greenest of all when cheats are dead for EVERYBODY, which is the
    /// opposite of what was asked — the host is authoritative and its cheats are the ones the rail replicates.
    ///
    /// THE DEFECT IT STANDS AGAINST. A player opened the cheat menu with a GAMEPAD in a live session. Every
    /// AnvilCheats entry is a console command (AnvilCheatEntry.cs:41), and a command run on a client writes
    /// that client's own campaign. The rail is a DIFF rail — it ships what changed on the HOST — so a value
    /// only the client moved is never contradicted and never repaired: the two campaigns drift apart with no
    /// error, no log and no symptom. L341 shut the console window; this is the OTHER window, plus the path
    /// that needs no window at all.
    ///
    /// WHAT "OPEN" IS. <c>AnvilCheatsUI.Active</c> (decompile :24), read by <c>OnGUI</c> :181 and written
    /// ONLY by <c>ToggleState(bool)</c> :301. Every route in reaches that one method — the gamepad
    /// (JoystickButton9, AnvilCheatsUI.cs:262-267), the keyboard unlock code (GameConsoleInput.cs:233), the
    /// <c>toggle_anvil_cheats</c> command (:389-401), and the two close paths (SerializationCommands.cs:180,
    /// ReportIssueInput.cs:48). Arms (a)-(c) execute that method's own decision against the mod's live one.
    ///
    /// ARMS
    ///   (a) <c>client-cheat-menu-opens</c> — a client's toggle on a CLOSED menu must leave it closed.
    ///   (b) <c>host-cheat-menu-blocked</c> — the HOST's toggle must still open it. The half that goes red
    ///       the moment the block stops asking about role.
    ///   (c) <c>solo-cheat-menu-blocked</c> — no session, no lock. Single-player is not multiplayer.
    ///   (d) <c>open-funnel-bypassed</c> — the shipped prefix really is attached to
    ///       <c>AnvilCheatsUI.ToggleState</c>, really consults the decision arms (a)-(c) exercise, and really
    ///       takes the <c>state</c> parameter BY REF (the only way to force the outcome). Without it the arms
    ///       above are proved about code the game never runs. Patching a caller instead is not equivalent:
    ///       the keyboard unlock code reaches ToggleState after CLEARING the game's own
    ///       <c>DisableConsoleAccess</c>, so the native guard at :297 is already defeated there.
    ///   (e) <c>already-open-not-swept</c> — something closes a menu that was open BEFORE the peer became a
    ///       client, by driving the same funnel. Requirement 3, and the ONLY arm that covers it.
    ///   (f) <c>action-funnel-open</c> — the cheat ACTION is locked, not just the two windows.
    ///       <c>bind &lt;key&gt; &lt;command&gt;</c> (GameConsoleInput.cs:415) fires a cheat at
    ///       GameConsoleInput.cs:326-337 — inside <c>if (_console.Visible || flag) return;</c>, i.e. only
    ///       while the console is CLOSED. Locking the windows alone leaves that key working, so the prefix
    ///       must sit on the one funnel every command reaches,
    ///       <c>ConsoleCommandAttribute.Invoke(string, string[], IConsole)</c>, and must be able to SKIP it.
    ///
    /// Falsify (each verified RED, then restored):
    ///   • <c>ConsoleLock.LockedFor =&gt; false</c> (the hole, reintroduced) → <c>client-cheat-menu-opens</c>
    ///   • <c>ConsoleLock.LockedFor =&gt; sessionActive</c> (block the host too) → <c>host-cheat-menu-blocked</c>
    ///   • drop the sweep's ToggleState call → <c>already-open-not-swept</c>
    ///   • drop CheatCommandLock → <c>action-funnel-open</c>
    ///   • rename/move CheatLock or AnvilCheatsUI's own members → <c>premise-changed</c>
    /// </summary>
    internal static class L342_TheCheatMenuIsTheHostsAlone
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>AnvilCheatsUI.cs:266 (<c>ToggleState(!Active)</c>) met by :297-301, with the lock the
        /// peer's press will actually see. TRUE = the menu ends up OPEN.</summary>
        private static bool ActiveAfterToggle(bool wasActive, bool locked) => !wasActive && !locked;

        internal static IEnumerable<string> Check()
        {
            var lockedFor = typeof(ConsoleLock).GetMethod("LockedFor", All);
            var locked = typeof(ConsoleLock).GetMethod("Locked", All);
            var prefix = typeof(CheatLock).GetMethod("Prefix", All);
            var sweep = typeof(CheatLockSweep).GetMethod("Prefix", All);
            var cmdPrefix = typeof(CheatCommandLock).GetMethod("Prefix", All);
            var toggle = typeof(AnvilCheatsUI).GetMethod("ToggleState", All, null, new[] { typeof(bool) }, null);
            var active = typeof(AnvilCheatsUI).GetField("Active", All);
            var invoke = typeof(ConsoleCommandAttribute).GetMethod(
                "Invoke", All, null, new[] { typeof(string), typeof(string[]), typeof(IConsole) }, null);
            if (lockedFor == null || locked == null || prefix == null || sweep == null || cmdPrefix == null ||
                toggle == null || active == null || invoke == null)
            {
                yield return "L342 premise-changed: ConsoleLock.LockedFor/Locked, CheatLock.Prefix, " +
                             "CheatLockSweep.Prefix, CheatCommandLock.Prefix, AnvilCheatsUI.ToggleState(bool), " +
                             "AnvilCheatsUI.Active or ConsoleCommandAttribute.Invoke(string,string[],IConsole) " +
                             "no longer resolves. With the block gone every arm below passes while a client " +
                             "presses one gamepad button and edits its own campaign with no intent and no " +
                             "delta — which is how this hole was found in the first place.";
                yield break;
            }

            // ── (a)-(c) the OUTCOME: the menu's own decision, run against the mod's own ──────────────────
            if (ActiveAfterToggle(wasActive: false, locked: ConsoleLock.LockedFor(sessionActive: true, isHost: false)))
                yield return "L342 client-cheat-menu-opens: a client in an active session pressed the cheat " +
                             "button and the menu ended up OPEN. Every entry in it is a console command that " +
                             "writes this peer's own campaign; the rail ships the HOST's diffs, so nothing " +
                             "ever contradicts the edit and the two campaigns drift apart silently.";
            if (!ActiveAfterToggle(wasActive: false, locked: ConsoleLock.LockedFor(sessionActive: true, isHost: true)))
                yield return "L342 host-cheat-menu-blocked: the HOST's own cheat menu no longer opens. The " +
                             "host is authoritative — its cheats are the ones the rail replicates to every " +
                             "peer — so disabling it is not a stricter version of this law, it is the " +
                             "opposite of it, and it is what a client-only assertion would happily pass.";
            if (!ActiveAfterToggle(wasActive: false, locked: ConsoleLock.LockedFor(sessionActive: false, isHost: false)))
                yield return "L342 solo-cheat-menu-blocked: the cheat menu is refused with NO session active. " +
                             "Single-player has no rail to desync and no host to defer to; a peer who quit to " +
                             "the main menu is in exactly this state.";

            // ── (d) the arms above are about code the game runs ──────────────────────────────────────────
            var modAsm = typeof(ConsoleLock).Assembly;
            var patch = typeof(CheatLock).GetCustomAttributes(typeof(HarmonyPatch), false)
                                         .Cast<HarmonyPatch>().FirstOrDefault();
            if (patch == null || patch.info.declaringType != typeof(AnvilCheatsUI) ||
                patch.info.methodName != "ToggleState")
                yield return "L342 open-funnel-bypassed: CheatLock is no longer attached to " +
                             "AnvilCheatsUI.ToggleState, the ONE writer of the menu's Active field (:301). " +
                             "Patching a caller instead leaves every other caller open — the gamepad " +
                             "(AnvilCheatsUI.cs:264) and the keyboard unlock code (GameConsoleInput.cs:233) " +
                             "are different callers, and the latter clears DisableConsoleAccess on its way in.";
            var stateParam = prefix.GetParameters().FirstOrDefault(p => p.Name == "state");
            if (stateParam == null || !stateParam.ParameterType.IsByRef ||
                !Program.Callees(prefix, modAsm).Any(c => c.MetadataToken == locked.MetadataToken))
                yield return "L342 open-funnel-bypassed: the shipped prefix does not take `ref bool state` " +
                             "and consult ConsoleLock.Locked, so arms (a)-(c) are proved about a decision " +
                             "function the cheat path never asks. By-value it cannot force the outcome at " +
                             "all — it would merely observe the press it was meant to refuse.";

            // ── (e) requirement 3: open BEFORE the session, closed by it ─────────────────────────────────
            if (!Program.Callees(sweep, modAsm).Any(c => c.MetadataToken == locked.MetadataToken) ||
                !Program.CalleeSequence(sweep).Any(c => c.MetadataToken == toggle.MetadataToken &&
                                                        c.Module == toggle.Module))
                yield return "L342 already-open-not-swept: nothing drives an already-open cheat menu through " +
                             "the funnel when this peer is a client. AnvilCheatsUI.Update is the seam that " +
                             "observes that case — without it the whole lock is bypassed by opening the menu " +
                             "before joining, and a host demoted mid-session keeps its menu too.";

            // ── (f) the window is not the cheat: a bound key fires one with every window shut ────────────
            var cmdPatch = typeof(CheatCommandLock).GetCustomAttributes(typeof(HarmonyPatch), false)
                                                   .Cast<HarmonyPatch>().FirstOrDefault();
            if (cmdPatch == null || cmdPatch.info.declaringType != typeof(ConsoleCommandAttribute) ||
                cmdPatch.info.methodName != "Invoke" || cmdPrefix.ReturnType != typeof(bool) ||
                !Program.Callees(cmdPrefix, modAsm).Any(c => c.MetadataToken == locked.MetadataToken))
                yield return "L342 action-funnel-open: nothing skippable sits on " +
                             "ConsoleCommandAttribute.Invoke, the one funnel every cheat reaches. Locking the " +
                             "two windows is then theatre: `bind <key> <command>` (GameConsoleInput.cs:415) " +
                             "fires a cheat at GameConsoleInput.cs:326-337 precisely BECAUSE the console is " +
                             "closed, and the cheat menu's own entries call Invoke directly, skipping the " +
                             "console entirely (AnvilCheatEntry.cs:41).";
        }
    }
}
