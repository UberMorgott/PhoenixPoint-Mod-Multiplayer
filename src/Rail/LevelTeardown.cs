using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Base.Core;
using Base.UI;
using HarmonyLib;
using Multiplayer.Network;
using PhoenixPoint.Common.Game;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewStates;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// LEVEL TEARDOWN IS SAFE AND LOUD (law L70) — the fix for the 2026-07-31 live blocker where the
    /// geoscape→tactical transition froze on every peer and tactical was never reached.
    ///
    /// WHAT HAPPENED (reproduced from PP-Instance2\Player.log:4699-4708 and :4771-4781, both stacks
    /// identical): a peer was sitting in a geoscape SUB-SCREEN (<c>UIStateEditSoldier</c> — the soldier
    /// equip page; multiplayer-2.log 00:22:14.778 "SKIP GeoscapeLog on UIStateEditSoldier" is the same
    /// peer, same screen) when the level was torn down. The teardown order is the whole bug:
    ///   <c>Level.SetState</c> → <c>GameView.OnLevelStateChanged</c> → <c>GeoscapeView.CleanupView</c>
    ///   (GeoscapeView.cs:1086-1100) → <c>_statesStack.Clear()</c> → <c>UIStateEditSoldier.ExitState</c>
    ///   → <c>UpdateSoldierEquipment</c> → <c>GeoCharacter.Faction</c>
    /// and <c>GeoCharacter.Faction</c> (GeoCharacter.cs:183-188) re-resolves its faction through
    /// <c>GameUtl.CurrentLevel().GetComponent&lt;GeoLevelController&gt;()</c> — UNGUARDED. By the time
    /// <c>CleanupView</c> runs the current level is ALREADY the new one, that GetComponent is null, and the
    /// exit throws NRE. <c>StateStack.Clear</c> (Base.UI/StateStack.cs:107-121) has no per-state try/catch,
    /// so the throw escaped into the level-switch coroutine and killed it: "InvalidCastException" +
    /// "CHECK/REPORT PREVIOUS ERROR!!! Broken coroutine call chain" one frame later (:4761, :4840), and the
    /// load then sat at "phase-2 pump: slot=2 pct=87" forever. NOTHING logged the state that did it — the
    /// exact silent-swallow class this project keeps paying for.
    ///
    /// TWO ARMS, and they are deliberately different in kind:
    ///
    /// (a) DON'T ENTER A TEARDOWN WITH A SUB-SCREEN OPEN. <see cref="GeoTeardownResetGate"/> parks the
    ///     view state BEFORE the level can switch. The seam is <c>PhoenixGame.FinishLevel</c>
    ///     (PhoenixGame.cs:262-266) because that is THE funnel — it is what the host's own tactical launch
    ///     ends in (<c>GeoLevelController.LaunchTacticalGameCrt</c>:1465 <c>GameController.FinishLevel</c>),
    ///     what our client entry calls directly (<c>SaveTransferCoordinator.EnterLevel</c>), and what the
    ///     F2 mid-session reload and the quit-to-lobby paths route through
    ///     (<c>FinishLevelAndLoadGame</c>:268, <c>FinishLevelAndGoToLobby</c>:284). One guard on the funnel
    ///     covers all of them; a guard at each call site would have missed the reload and the return. And
    ///     it is safe to sit here precisely because <c>FinishLevel</c> is ASYNCHRONOUS — it only stores
    ///     <c>_levelResult</c> and pulses a monitor, so the level is STILL the geoscape when the prefix
    ///     runs and <c>GeoCharacter.Faction</c> still resolves.
    ///
    ///     The call is <c>GeoscapeView.ToLoadingState()</c> (GeoscapeView.cs:691-694), NOT
    ///     <c>ResetViewState()</c>. Both clear the stack, but <c>ResetViewState</c> pushes
    ///     <c>UIStateInitial</c>, whose <c>EnterState</c> selects an actor and can THROW outright on a
    ///     faction with no vehicles and no inspected site (UIStateInitial.cs:80-87) — trading one teardown
    ///     exception for another. <c>UIStateLoading</c> has a two-line <c>EnterState</c> that cannot fail,
    ///     and NATIVE-UI-FIRST decides it anyway: <c>ToLoadingState</c> is the exact call the game itself
    ///     makes on this very transition (<c>LaunchTacticalGameCrt</c>:1444) one step before
    ///     <c>FinishLevel</c>. We are not inventing a teardown protocol, we are applying the game's own to
    ///     the paths that skip it.
    ///
    /// (b) IF IT THROWS ANYWAY, NAME IT. <see cref="ViewStateExitLoudGate"/> is a FINALIZER on
    ///     <c>GeoscapeViewState.Exit</c> (GeoscapeViewState.cs:96 — the one base method every sub-screen's
    ///     exit goes through). Arm (a) fixes the state we KNOW about; this arm makes the next one a named
    ///     error line and a completed teardown instead of a dead coroutine and a frozen load. It is the
    ///     per-state try/catch <c>StateStack.Clear</c> does not have.
    /// </summary>
    [HarmonyPatch(typeof(PhoenixGame), "FinishLevel")]
    internal static class GeoTeardownResetGate
    {
        private static void Prefix()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return; // solo: vanilla teardown, untouched
            var view = (GameUtl.CurrentLevel()?.GetComponent<GeoLevelController>())?.View;
            if (view == null) return;                              // not leaving a geoscape

            DumpStack(view);
            // Already parked by the game's own path (host tactical launch) — re-pushing would only churn.
            if (view.CurrentViewState is UIStateLoading) return;
            Debug.Log("[MP][teardown] parking the geoscape view state before FinishLevel (was " +
                      (view.CurrentViewState?.GetType().Name ?? "<none>") + ") — every sub-screen exits " +
                      "NOW, while GameUtl.CurrentLevel() is still the geoscape.");
            view.ToLoadingState();
        }

        // Diagnostic the RCA could not answer from the logs: StateStack.Clear only calls Exit() on the TOP
        // state (Base.UI/StateStack.cs:113-117), so the crash named UIStateEditSoldier but said nothing
        // about what was underneath it. Both hops are private fields, so both are reflected; a diagnostic
        // that throws would be worse than no diagnostic, hence the blanket catch.
        private static readonly System.Reflection.FieldInfo ViewStackField =
            AccessTools.Field(typeof(GeoscapeView), "_statesStack");                        // GeoscapeView.cs:108
        private static readonly System.Reflection.FieldInfo StatesField =
            AccessTools.Field(typeof(StateStack<GeoscapeViewContext>), "_statesStack");     // Base.UI/StateStack.cs:19

        private static void DumpStack(GeoscapeView view)
        {
            try
            {
                var stack = ViewStackField?.GetValue(view);
                var states = stack == null ? null : StatesField?.GetValue(stack) as IEnumerable;
                var names = states == null
                    ? new List<string>()
                    : states.Cast<object>().Select(s => s?.GetType().Name ?? "<null>").ToList();
                Debug.Log("[MP][teardown] view-state stack at FinishLevel (top first, " + names.Count +
                          "): " + (names.Count == 0 ? "<empty>" : string.Join(" / ", names)));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MP][teardown] could not read the view-state stack: " + e.Message);
            }
        }
    }

    /// <summary>Arm (b) of law L70 — see <see cref="GeoTeardownResetGate"/>.</summary>
    [HarmonyPatch(typeof(GeoscapeViewState), "Exit")]
    internal static class ViewStateExitLoudGate
    {
        private static Exception Finalizer(Exception __exception, GeoscapeViewState __instance)
        {
            if (__exception == null) return null;
            var engine = NetworkEngine.Instance;
            // Solo keeps vanilla behaviour byte-for-byte: rethrow untouched.
            if (engine == null || !engine.IsActiveSession) return __exception;

            Debug.LogError("[MP][teardown] " + (__instance?.GetType().Name ?? "<unknown state>") +
                           ".ExitState THREW during a view-state exit (" + __exception.GetType().Name + ": " +
                           __exception.Message + ") — SWALLOWED so StateStack.Clear finishes tearing the " +
                           "rest of the stack down. Escaping here kills the level-switch coroutine outright " +
                           "(\"Broken coroutine call chain\") and the load never completes, which is how the " +
                           "geoscape→tactical transition froze on 2026-07-31. The state named above is the " +
                           "bug; this line is the belt.");
            return null;
        }
    }
}
