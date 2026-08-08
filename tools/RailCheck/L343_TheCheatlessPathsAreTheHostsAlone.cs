using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Base.Utils.GameConsole;
using HarmonyLib;
using Multiplayer.Network;
using PhoenixPoint.Geoscape.Cheats;

namespace RailCheck
{
    /// <summary>
    /// L343 — ON A CLIENT IN AN ACTIVE SESSION THE TWO CHEAT PATHS THAT NEED NO CONSOLE COMMAND LEAVE THE
    /// GAME STATE UNCHANGED; ON THE HOST, AND WITH NO SESSION, THE SAME TWO PATHS STILL WORK.
    ///
    /// THE OUTCOME, not the call, and BOTH halves on purpose. A law that only asserted the client half is
    /// greenest of all when these are dead for EVERYBODY — which is the opposite of what was asked. The
    /// host is authoritative: ITS writes are the ones the rail replicates, and single-player has no rail
    /// to desync.
    ///
    /// THE HOLES IT STANDS AGAINST — both are things L341/L342 provably do NOT cover:
    ///
    ///   1. A console line is not always a console COMMAND. <c>god_mode = true</c> is a console VARIABLE
    ///      assignment: <c>CommandLineParser</c> branches on the identifier (:54 command, :63 variable)
    ///      and emits an <c>AssignVariableCommand</c>, whose <c>Execute</c> calls
    ///      <c>ConsoleVariableAttribute.SetValue</c> DIRECTLY (AssignVariableCommand.cs:11).
    ///      <see cref="CheatCommandLock"/> sits on <c>ConsoleCommandAttribute.Invoke</c> and never sees
    ///      it. It is live with EVERY window shut, by the exact route that made the command funnel
    ///      necessary: <c>bind &lt;key&gt; "god_mode = true"</c> (GameConsoleInput.cs:415) is fired at
    ///      GameConsoleInput.cs:331 through <c>ExecuteCommandLine</c>, which parses — inside
    ///      <c>if (_console.Visible || flag) return;</c>, i.e. ONLY while the console is closed.
    ///      SetValue is the funnel and it has exactly two callers, both player-driven; nothing in the
    ///      engine assigns a console variable by itself.
    ///
    ///   2. <c>UIGeoResourceGiverCheat.GiveResources</c> (UIGeoResourceGiverCheat.cs:18) needs no console
    ///      at all. It has no C# caller — it is a <c>UnityEvent</c> target on a geoscape cheat-panel
    ///      button — and it applies a <c>ResourcePack</c> to <c>ViewerFaction.Wallet</c> with
    ///      <c>OperationReason.Cheat</c> (:25). No window lock and neither action funnel is on that path.
    ///
    /// Together with L342 arm (f) this closes <c>IConsole.ExecuteCommandLine</c>: the parser emits
    /// exactly four <c>ICommand</c>s — <c>EmptyCommand</c>, <c>CommandCallCommand</c> (refused by
    /// <see cref="CheatCommandLock"/>), <c>QueryVariableCommand</c> (read-only), and the assignment
    /// arms (a)-(d) below are about.
    ///
    /// ARMS
    ///   (a) <c>client-variable-assign-lands</c> — a client's assignment must not reach the field.
    ///   (b) <c>host-variable-assign-blocked</c> — the HOST's must still land. The half that goes red the
    ///       moment the block stops asking about role.
    ///   (c) <c>solo-variable-assign-blocked</c> — no session, no lock.
    ///   (d) <c>variable-funnel-bypassed</c> — the shipped prefix really is attached to
    ///       <c>ConsoleVariableAttribute.SetValue(string,string)</c> — NOT to
    ///       <c>ConsoleCommandAttribute.Invoke</c>, which is a different method and the whole point of
    ///       this law — really returns <c>bool</c> (the only way a prefix can skip the write) and really
    ///       consults the decision arms (a)-(c) exercise. Without it those arms are proved about code the
    ///       game never runs.
    ///   (e) <c>client-resource-giver-pays</c> / (f) <c>host-resource-giver-blocked</c> /
    ///       (g) <c>solo-resource-giver-blocked</c> — the same three outcomes for the wallet cheat.
    ///   (h) <c>giver-funnel-bypassed</c> — the giver prefix is attached to
    ///       <c>UIGeoResourceGiverCheat.GiveResources</c>, returns <c>bool</c>, and consults the same
    ///       decision. Patching a caller is not an option here: it HAS no C# caller.
    ///
    /// Falsify (each verified RED, then restored — see the commit body):
    ///   • <c>ConsoleLock.LockedFor =&gt; false</c> (the hole, reintroduced) → (a) and (e)
    ///   • <c>ConsoleLock.LockedFor =&gt; sessionActive</c> (block the host too) → (b) and (f)
    ///   • drop either <c>[HarmonyPatch]</c>/prefix → (d) / (h)
    ///   • rename or move any subject → <c>premise-changed</c>
    /// </summary>
    internal static class L343_TheCheatlessPathsAreTheHostsAlone
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>The write reaches the world iff the prefix let the native body run, and a prefix that
        /// returns <c>false</c> is the only thing that stops it. TRUE = the state CHANGED.</summary>
        private static bool StateChanges(bool locked) => !locked;

        internal static IEnumerable<string> Check()
        {
            var lockedFor = typeof(ConsoleLock).GetMethod("LockedFor", All);
            var locked = typeof(ConsoleLock).GetMethod("Locked", All);
            var varPrefix = typeof(CheatVariableLock).GetMethod("Prefix", All);
            var giverPrefix = typeof(ResourceGiverCheatLock).GetMethod("Prefix", All);
            var setValue = typeof(ConsoleVariableAttribute).GetMethod(
                "SetValue", All, null, new[] { typeof(string), typeof(string) }, null);
            var give = typeof(UIGeoResourceGiverCheat).GetMethod("GiveResources", All);
            if (lockedFor == null || locked == null || varPrefix == null || giverPrefix == null ||
                setValue == null || give == null)
            {
                yield return "L343 premise-changed: ConsoleLock.LockedFor/Locked, CheatVariableLock.Prefix, " +
                             "ResourceGiverCheatLock.Prefix, ConsoleVariableAttribute.SetValue(string,string) " +
                             "or UIGeoResourceGiverCheat.GiveResources no longer resolves. With the block gone " +
                             "every arm below passes while a client presses one bound key — `god_mode = true` " +
                             "never touches ConsoleCommandAttribute.Invoke — or clicks one cheat-panel button, " +
                             "and edits its own campaign with no intent, no delta and no log.";
                yield break;
            }

            var modAsm = typeof(ConsoleLock).Assembly;

            // ── (a)-(c) the OUTCOME of a console VARIABLE assignment ─────────────────────────────────────
            if (StateChanges(ConsoleLock.LockedFor(sessionActive: true, isHost: false)))
                yield return "L343 client-variable-assign-lands: a client in an active session assigned a " +
                             "console variable and the value LANDED. `god_mode = true` is not a console " +
                             "command — the parser routes it to ConsoleVariableAttribute.SetValue " +
                             "(AssignVariableCommand.cs:11), which CheatCommandLock never sees — and a bound " +
                             "key fires it precisely BECAUSE both windows are shut. The rail ships the HOST's " +
                             "diffs, so nothing ever contradicts the edit.";
            if (!StateChanges(ConsoleLock.LockedFor(sessionActive: true, isHost: true)))
                yield return "L343 host-variable-assign-blocked: the HOST can no longer assign a console " +
                             "variable. The host is authoritative — its edits are the ones the rail " +
                             "replicates to every peer — so this is not a stricter version of the law, it is " +
                             "the opposite of it, and it is exactly what a client-only assertion would pass.";
            if (!StateChanges(ConsoleLock.LockedFor(sessionActive: false, isHost: false)))
                yield return "L343 solo-variable-assign-blocked: a console variable is refused with NO " +
                             "session active. Single-player has no rail to desync and no host to defer to, " +
                             "and a peer who quit to the main menu is in exactly this state.";

            // ── (d) the arms above are about code the game runs, on the RIGHT method ─────────────────────
            var varPatch = typeof(CheatVariableLock).GetCustomAttributes(typeof(HarmonyPatch), false)
                                                    .Cast<HarmonyPatch>().FirstOrDefault();
            if (varPatch == null || varPatch.info.declaringType != typeof(ConsoleVariableAttribute) ||
                varPatch.info.methodName != "SetValue" || varPrefix.ReturnType != typeof(bool) ||
                !Program.Callees(varPrefix, modAsm).Any(c => c.MetadataToken == locked.MetadataToken))
                yield return "L343 variable-funnel-bypassed: nothing skippable sits on " +
                             "ConsoleVariableAttribute.SetValue, the ONE funnel every variable assignment " +
                             "reaches. It is a DIFFERENT method from ConsoleCommandAttribute.Invoke — that is " +
                             "the entire finding: the command lock is on the command branch of " +
                             "CommandLineParser.cs:54 and the assignment takes the branch at :63. A prefix " +
                             "that does not return bool cannot skip the write, and one that does not consult " +
                             "ConsoleLock.Locked proves arms (a)-(c) about a decision the path never asks.";

            // ── (e)-(g) the OUTCOME of the console-less wallet cheat ─────────────────────────────────────
            if (StateChanges(ConsoleLock.LockedFor(sessionActive: true, isHost: false)))
                yield return "L343 client-resource-giver-pays: a client in an active session clicked the " +
                             "geoscape resource-giver cheat and its wallet CHANGED " +
                             "(UIGeoResourceGiverCheat.cs:25, OperationReason.Cheat). A client's wallet is a " +
                             "mirror of the host's, so a total only the client moved is never contradicted " +
                             "and never repaired.";
            if (!StateChanges(ConsoleLock.LockedFor(sessionActive: true, isHost: true)))
                yield return "L343 host-resource-giver-blocked: the HOST's own resource-giver cheat no longer " +
                             "pays out. The host's writes are what the rail replicates; refusing them is the " +
                             "opposite of this law.";
            if (!StateChanges(ConsoleLock.LockedFor(sessionActive: false, isHost: false)))
                yield return "L343 solo-resource-giver-blocked: the resource-giver cheat is refused with NO " +
                             "session active. Single-player is untouched by this mod's locks.";

            // ── (h) the console-less path has no caller to patch instead ─────────────────────────────────
            var giverPatch = typeof(ResourceGiverCheatLock).GetCustomAttributes(typeof(HarmonyPatch), false)
                                                           .Cast<HarmonyPatch>().FirstOrDefault();
            if (giverPatch == null || giverPatch.info.declaringType != typeof(UIGeoResourceGiverCheat) ||
                giverPatch.info.methodName != "GiveResources" || giverPrefix.ReturnType != typeof(bool) ||
                !Program.Callees(giverPrefix, modAsm).Any(c => c.MetadataToken == locked.MetadataToken))
                yield return "L343 giver-funnel-bypassed: nothing skippable sits on " +
                             "UIGeoResourceGiverCheat.GiveResources. It reaches ViewerFaction.Wallet with no " +
                             "console involved, so neither window lock nor either action funnel covers it, " +
                             "and there is no caller to patch instead — its only invoker is a UnityEvent on a " +
                             "cheat-panel button.";
        }
    }
}
