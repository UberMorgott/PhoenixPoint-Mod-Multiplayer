using Base.Utils.GameConsole;
using HarmonyLib;
using PhoenixPoint.Geoscape.Cheats;
using UnityEngine;
using UnityTools.AnvilCheats;

namespace Multiplayer.Network
{
    /// <summary>
    /// A CLIENT gets no cheat menu, for exactly the reason it gets no developer console (see
    /// <see cref="ConsoleLock"/>): every AnvilCheats entry is a console command, and a console command run on
    /// a client writes that client's own campaign behind the rail's back. The rail is a DIFF rail — it ships
    /// what changed on the HOST — so a value only the client moved is never contradicted and never repaired.
    /// The host keeps its cheats; solo keeps its cheats.
    ///
    /// THE HOLE THIS CLOSES. <see cref="ConsoleLock"/> locks the console WINDOW; the cheat menu is a
    /// different window with its own opener, and a player opened it in a live session with a GAMEPAD.
    ///
    /// EVERY OPEN PATH, and they all bottom out in <c>AnvilCheatsUI.ToggleState(bool)</c> (decompile
    /// AnvilCheatsUI.cs:295) — "open" IS the public <c>Active</c> field (:301) and that method is its only
    /// writer, with <c>OnGUI</c> returning immediately while it is false (:181):
    ///   • GAMEPAD — <c>Input.GetKeyDown(KeyCode.JoystickButton9)</c> in <c>AnvilCheatsUI.Update</c>
    ///     (:262-267). This is the one the player used.
    ///   • KEYBOARD — the console unlock code with <c>OpenAnvilCheats</c> set clears
    ///     <c>DisableConsoleAccess</c> and then calls <c>ToggleState(true)</c> (GameConsoleInput.cs:212-236,
    ///     specifically :233). Note it CLEARS the flag first, which is why this lock forces the parameter
    ///     instead of leaning on the game's own <c>if (DisableConsoleAccess) state = false;</c> at :297 —
    ///     that guard is already defeated by the time this path calls in.
    ///   • CONSOLE — the <c>toggle_anvil_cheats</c> command (:389-401), also refused by
    ///     <see cref="CheatCommandLock"/> below.
    ///   • The close paths — SerializationCommands.cs:180 and ReportIssueInput.cs:48 both call
    ///     <c>ToggleState(false)</c>. Closing is always allowed.
    /// Forcing <c>state</c> to false is the GAME'S OWN behaviour under its own lock (:297), so the body runs
    /// the vanilla close bookkeeping — cursor override released, input handler unhooked
    /// (<c>ClearOverrideInputSet</c> is idempotent: InputController.cs:535 → :540 no-ops with no override).
    /// Nothing persists past the call, so a peer promoted to host, or back at the main menu, cheats again.
    ///
    /// Refusal is SILENT to the player apart from one log line, same reasoning as ConsoleLock: a debug menu
    /// has no native "refused" affordance to reuse, and inventing a popup is the custom overlay the
    /// native-UI-first rule exists to prevent.
    /// </summary>
    [HarmonyPatch(typeof(AnvilCheatsUI), "ToggleState", new[] { typeof(bool) })]
    internal static class CheatLock
    {
        /// <summary>One log line per lock, not per button press: reset the moment the peer stops being a
        /// locked client (session over, or promoted to host).</summary>
        private static bool _said;

        private static void Prefix(ref bool state)
        {
            if (!ConsoleLock.Locked()) { _said = false; return; }
            if (!state) return;          // closing is never refused, and needs no log
            state = false;
            if (_said) return;
            _said = true;
            Debug.Log("[MP][cheatlock] cheat menu refused — this peer is a CLIENT in an active session. " +
                      "Host-only: every cheat is a console command and it would edit this world behind the " +
                      "rail, which the host never learns of. Logged once per lock, not per press.");
        }
    }

    /// <summary>
    /// The cheat menu that was ALREADY OPEN when this peer became a client — otherwise the lock is bypassed
    /// by opening the menu first and joining second. <c>AnvilCheatsUI.Update</c> is the seam that observes
    /// it, and it is also where the gamepad opener lives (:262-267), so this runs before that check every
    /// frame; the cost while unlocked is one null-check and two bools. Equally covers a host demoted to
    /// client mid-session (NetworkEngine.cs:415). The close goes through <see cref="CheatLock"/>, which lets
    /// it past untouched.
    /// </summary>
    [HarmonyPatch(typeof(AnvilCheatsUI), "Update")]
    internal static class CheatLockSweep
    {
        private static void Prefix(AnvilCheatsUI __instance)
        {
            if (!__instance.Active || !ConsoleLock.Locked()) return;
            Debug.Log("[MP][cheatlock] the cheat menu was open when this peer became a CLIENT — closing it.");
            __instance.ToggleState(state: false);
        }
    }

    /// <summary>
    /// Locking the two WINDOWS is theatre on its own: a cheat can be fired with BOTH of them closed.
    /// <c>bind &lt;key&gt; &lt;command&gt;</c> (GameConsoleInput.cs:415) registers a keybind that
    /// <c>GameConsoleInput.Update</c> fires at :326-337 — inside <c>if (_console.Visible || flag) return;</c>,
    /// i.e. ONLY while the console is CLOSED. A player binds a cheat in solo (or before joining, the binds
    /// live in the process, not the save) and then presses that key as a client with both windows locked shut.
    ///
    /// So the ACTION funnel is locked too, and there is exactly one:
    /// <c>ConsoleCommandAttribute.Invoke(string, string[], IConsole)</c> (ConsoleCommandAttribute.cs:99).
    /// Everything reaches it — a typed line (GameConsoleWindow.cs:289 → CommandLineParser.cs:57 →
    /// CommandCallCommand.cs:14), a keybind (GameConsoleInput.cs:331), an <c>exec</c>'d file
    /// (Helper.cs:193), and the cheat menu's own entries, which skip the console entirely and call it
    /// directly (AnvilCheatEntry.cs:41). The game's own in-code uses are all themselves inside
    /// <c>[ConsoleCommand]</c> methods (GeoMarketplace.cs:340-352), so nothing the game drives by itself
    /// passes through here — refusing on a client refuses only what a player typed, bound or clicked.
    /// </summary>
    [HarmonyPatch(typeof(ConsoleCommandAttribute), "Invoke",
                  new[] { typeof(string), typeof(string[]), typeof(IConsole) })]
    internal static class CheatCommandLock
    {
        private static bool _said;

        private static bool Prefix(string command)
        {
            if (!ConsoleLock.Locked()) { _said = false; return true; }
            if (!_said)
            {
                _said = true;
                Debug.Log("[MP][cheatlock] console command '" + command + "' refused — this peer is a CLIENT " +
                          "in an active session. A bound key fires cheats with every window shut, so the " +
                          "command itself is the funnel. Logged once per lock, not per command.");
            }
            return false;
        }
    }

    /// <summary>
    /// A console line is not always a console COMMAND. <c>god_mode = true</c> is a console VARIABLE
    /// assignment, and it never goes near <see cref="CheatCommandLock"/>: the parser branches on the
    /// identifier (CommandLineParser.cs:54 command / :63 variable) and builds an
    /// <c>AssignVariableCommand</c>, whose <c>Execute</c> calls
    /// <c>ConsoleVariableAttribute.SetValue</c> DIRECTLY (AssignVariableCommand.cs:11).
    ///
    /// That is a live hole on a client with every window shut, by the same route that made the command
    /// funnel necessary: <c>bind &lt;key&gt; "god_mode = true"</c> (GameConsoleInput.cs:415) is fired at
    /// GameConsoleInput.cs:331 through <c>ExecuteCommandLine</c>, which parses — so the bound key reaches
    /// SetValue while <see cref="ConsoleLock"/> holds the window closed and
    /// <see cref="CheatCommandLock"/> never sees a command. <c>god_mode</c> and <c>infinite_ap</c> are
    /// variables, not commands (ACE_MakeImmortal.cs:25 is the game setting one itself).
    ///
    /// SetValue is the funnel and it has exactly two callers, both player-driven: that parser node, and
    /// ACE_MakeImmortal.cs:25 — which is itself inside a <c>[ConsoleCommand]</c> and therefore already
    /// refused upstream. NOTHING in the engine assigns a console variable on its own, so refusing here
    /// refuses only what a player typed, bound or exec'd. (<c>Persistent</c> on the attribute is
    /// decorative in this build — no code path saves or reloads variables, so this cannot strand a
    /// setting.) Reads are untouched: a query goes through <c>QueryVariableCommand</c> → <c>GetValue</c>.
    ///
    /// With this, <c>IConsole.ExecuteCommandLine</c> is closed: the parser emits exactly four
    /// <c>ICommand</c>s — <c>EmptyCommand</c>, <c>CommandCallCommand</c> (refused by
    /// <see cref="CheatCommandLock"/>), <c>QueryVariableCommand</c> (read-only) and this one.
    /// </summary>
    [HarmonyPatch(typeof(ConsoleVariableAttribute), "SetValue",
                  new[] { typeof(string), typeof(string) })]
    internal static class CheatVariableLock
    {
        private static bool _said;

        private static bool Prefix(string variableName)
        {
            if (!ConsoleLock.Locked()) { _said = false; return true; }
            if (!_said)
            {
                _said = true;
                Debug.Log("[MP][cheatlock] console variable '" + variableName + "' assignment refused — " +
                          "this peer is a CLIENT in an active session. A variable assignment is not a " +
                          "console command, so it reaches SetValue with both windows shut. Logged once " +
                          "per lock, not per assignment.");
            }
            return false;
        }
    }

    /// <summary>
    /// The geoscape's resource-giver cheat, which reaches the wallet with NO console involved at all:
    /// <c>UIGeoResourceGiverCheat.GiveResources</c> (UIGeoResourceGiverCheat.cs:18) has no C# caller —
    /// it is a <c>UnityEvent</c> target on a cheat-panel button, sitting beside the F4 globe tools
    /// (UIGeoDistanceTool.cs:38) — and it applies a <c>ResourcePack</c> straight to
    /// <c>ViewerFaction.Wallet</c> with <c>OperationReason.Cheat</c> (:25). Shift halves it, Ctrl negates
    /// it (:20-24). Neither window lock nor either action funnel is on this path.
    ///
    /// A client's wallet is a mirror of the host's, so the write is the textbook silent desync: the rail
    /// ships what changed on the HOST, and a total only the client moved is never contradicted. Refusing
    /// the whole method is right rather than clamping the pack — there is no non-cheat use of it, which
    /// is what both its type name and <c>OperationReason.Cheat</c> say. The console command that does the
    /// same thing (GeoLevelController.cs:1744) is already refused by <see cref="CheatCommandLock"/>.
    /// </summary>
    [HarmonyPatch(typeof(UIGeoResourceGiverCheat), "GiveResources")]
    internal static class ResourceGiverCheatLock
    {
        private static bool _said;

        private static bool Prefix()
        {
            if (!ConsoleLock.Locked()) { _said = false; return true; }
            if (!_said)
            {
                _said = true;
                Debug.Log("[MP][cheatlock] geoscape resource-giver cheat refused — this peer is a CLIENT " +
                          "in an active session. It writes ViewerFaction.Wallet with OperationReason.Cheat " +
                          "and needs no console, so no other lock covers it. Logged once per lock.");
            }
            return false;
        }
    }
}
