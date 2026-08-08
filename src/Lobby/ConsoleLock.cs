using Base.Utils.GameConsole;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Network
{
    /// <summary>
    /// A CLIENT gets no developer console. The host is authoritative: ITS console commands are the ones the
    /// rail replicates to everybody, while a client's console command writes that client's own world behind
    /// the rail's back — and a diff rail can never correct a value the host itself never changed, so the
    /// campaign desyncs silently and permanently. The host keeps its console; solo keeps its console.
    ///
    /// ONE FUNNEL. Every route into the console window bottoms out in
    /// <c>GameConsoleWindow.ToggleVisibility</c> (decompile GameConsoleWindow.cs:220), because "open" IS
    /// <c>base.enabled</c> (<c>Visible =&gt; base.enabled</c>, :82) and that line is the only writer of it:
    ///   • the ` / "/" hotkey — GameConsoleInput.cs:254-264;
    ///   • the UP-DOWN-LEFT-RIGHT-S-N-A-P-S-H-O-T unlock code, which clears the game's own
    ///     <c>DisableConsoleAccess</c> and then <c>Invoke("ShowConsole")</c> — GameConsoleInput.cs:212-236,
    ///     :336-342. This is why the flag alone is not a lock: the code turns it back off.
    ///   • the close paths — Escape (GameConsoleInput.cs:279-282) and
    ///     <c>EnableInput(false)</c> from the F10 report window (GameConsoleInput.cs:446-453,
    ///     ReportIssueInput.cs:30). Closing is always allowed.
    /// (<c>ReportIssueInput.cs:41</c> and <c>:56</c> toggle the ReportIssueWindow, a different window;
    /// AnvilCheats is a separate cheat UI reached by a joystick code and is not a console-open path.)
    /// Nothing else in Assembly-CSharp writes the window's <c>enabled</c>: PPML only clears the flag
    /// (ModManager.cs:76 — which is exactly why the console is available in a modded game at all).
    ///
    /// The block is the GAME'S OWN lock, held for the duration of the call: with
    /// <c>DisableConsoleAccess</c> up, :222 evaluates <c>enabled = !DisableConsoleAccess &amp;&amp; !enabled</c>
    /// to false, so the toggle can only ever end CLOSED — animator and input field handled by the native
    /// body. The postfix hands the flag straight back, so nothing about it persists past the call: quit to
    /// the menu, or get promoted to host, and the very next toggle opens normally.
    ///
    /// Refusal is SILENT to the player apart from one log line. The console is a debug key with no native
    /// "refused" affordance on this path, and the project's native-UI-first rule points at reusing an
    /// existing widget — there is none for this, and inventing a popup for a keypress most players never
    /// make is the custom overlay that rule exists to prevent.
    /// </summary>
    [HarmonyPatch(typeof(GameConsoleWindow), "ToggleVisibility")]
    internal static class ConsoleLock
    {
        /// <summary>One log line per lock, not per keypress: reset the moment the peer stops being a
        /// locked client (session over, or promoted to host).</summary>
        private static bool _said;

        /// <summary>PURE, and the single decision both patches ask — kept parameterised so L341 can assert
        /// BOTH halves (client blocked, host and solo not) without a live session.</summary>
        internal static bool LockedFor(bool sessionActive, bool isHost) => sessionActive && !isHost;

        /// <summary>Role from the mod's one source of truth, same shape as IntentRail.ShouldRunNative.</summary>
        internal static bool Locked()
        {
            var engine = NetworkEngine.Instance;
            return engine != null && LockedFor(engine.IsActiveSession, engine.IsHost);
        }

        private static void Prefix(ref bool __state)
        {
            __state = GameConsoleWindow.DisableConsoleAccess;
            if (!Locked()) { _said = false; return; }
            GameConsoleWindow.DisableConsoleAccess = true;
            if (_said) return;
            _said = true;
            Debug.Log("[MP][consolelock] developer console refused — this peer is a CLIENT in an active " +
                      "session. Host-only: a client console command edits this world behind the rail and " +
                      "the host never learns of it. Logged once per lock, not per keypress.");
        }

        /// <summary>The flag is the game's, not ours — a lock that outlived the call would leave a peer
        /// consoleless after quitting to the main menu.</summary>
        private static void Postfix(bool __state)
        {
            GameConsoleWindow.DisableConsoleAccess = __state;
        }
    }

    /// <summary>
    /// The console that was ALREADY OPEN when this peer became a client — otherwise the lock is bypassed by
    /// opening the console first and joining second. <c>GameConsoleWindow.Update</c> ticks ONLY while the
    /// component is enabled, which is the definition of Visible (:82), so this costs nothing whenever the
    /// console is closed, needs no session-lifecycle hook of its own, and equally covers a host demoted to
    /// client mid-session (NetworkEngine.cs:415).
    /// </summary>
    [HarmonyPatch(typeof(GameConsoleWindow), "Update")]
    internal static class ConsoleLockSweep
    {
        private static bool Prefix(GameConsoleWindow __instance)
        {
            if (!ConsoleLock.Locked()) return true;
            __instance.ToggleVisibility();   // through ConsoleLock: the flag is up, so this can only CLOSE
            return false;
        }
    }
}
