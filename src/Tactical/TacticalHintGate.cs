using System.Collections.Generic;
using Base.Core;
using HarmonyLib;
using Multiplayer.Network;
using PhoenixPoint.Tactical.View;
using UnityEngine;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// A HINT IS ONE PEER'S SCREEN, NEVER EVERY PEER'S CLOCK (law L91, the zero-blocking rule).
    ///
    /// THE REPORT (live 3-instance run, 2026-08-05). An Umbra appeared. The HOST got the native
    /// first-encounter panel, the ALIEN TURN stopped dead — on every peer — and nothing moved anywhere until
    /// the host pressed OK. The clients never saw a hint at all: zero <c>Showing hint</c> and zero
    /// <c>UIStateTacticalContextHelp</c> in either client log, while client I2 logged
    /// <c>still holding in 'Alien_TacticalFactionDef's turn after 60s</c> and was released at t=481,8 —
    /// the exact moment the host's popup closed (host: enter at t=346,7, <c>Showing hint: UmbraSighted</c>,
    /// exit at t=471,7). Two players sat in front of a frozen battlefield waiting on a third player's mouse.
    ///
    /// THE ROOT, and it is a single shared line. The alien turn is <c>TacticalFaction.AIUpdateCrt</c>, which
    /// only the host runs (<see cref="ClientAiGate"/> replaces it on clients with a hold on the host's turn
    /// cursor). That coroutine yields on the LOCAL hint queue twice —
    /// <c>TacticalFaction.cs</c>:567 (once, before the actor loop) and :621 (after EVERY executed AI action)
    /// — through <c>TacticalView.WaitUntilHintsAreConfirmed</c> (<c>TacticalView.cs</c>:359-373), whose loop
    /// spins while <c>_statesStack.CurrentState</c> is the hint popup. Those two lines are the ONLY callers
    /// of that method in the whole game, so ONE prefix on the funnel covers both — no per-call-site guard.
    ///
    /// WHY LOCAL AND NOT REPLICATED. Replicating the pause would need a new surface, a release message AND a
    /// timeout, and it would STILL wedge the battle if the peer holding the popup dropped — the same
    /// cross-peer wait wearing a rail costume. Local costs nothing, because the game already delivers the
    /// panel per peer without this method: the hint stays queued in <c>ContextHelpManager._hintsPendingDisplay</c>
    /// and <c>UIStateCharacterSelected.UpdateState</c>:1103-1106 calls <c>TacticalView.TryShowContextHint</c>
    /// EVERY FRAME, so each peer pops its own Umbra panel in its own idle state and releases only itself.
    /// Hints are already declared per-peer presentation on the rail (<c>ContextHelpData</c> is an excluded
    /// field, <c>RailMeta.cs</c>:753), and <see cref="ClientMissionStartHints"/> already rebuilds them
    /// client-side for exactly that reason.
    ///
    /// THE EMPTY ENUMERATOR IS THE NATIVE ANSWER, not a guess. Both call sites consume the result as
    /// <c>yield return TacticalLevel.Timing.Call(WaitUntilHintsAreConfirmed())</c>, and
    /// <c>Timing.Call</c> (<c>Base.Core/Timing.cs</c>:257-260) hands it straight to
    /// <c>Start(coroutine, NextUpdate.Now, setCaller: true)</c> — a null would go into the scheduler, not be
    /// tested for. An enumerator that finishes immediately is the method's OWN hot path: line 361-364 is
    /// <c>if (!TryShowContextHint()) yield break;</c>, i.e. every turn with no pending hint already runs this
    /// exact shape through this exact <c>Timing.Call</c>. So <see cref="Nothing"/> reproduces a case the
    /// engine takes thousands of times per battle; <c>null</c> reproduces nothing.
    ///
    /// APPLIES ON EVERY PEER INCLUDING THE HOST — that is the point. The host is the peer whose popup froze
    /// everyone else. Single-player is untouched (<c>IsActiveSession</c> false → the native method runs).
    /// </summary>
    [HarmonyPatch(typeof(TacticalView), "WaitUntilHintsAreConfirmed")]
    internal static class HintWaitGate
    {
        private static bool Prefix(ref IEnumerator<NextUpdate> __result)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return true;
            Debug.Log("[Multiplayer][tac] hint wait made LOCAL — the turn coroutine does not stop on this " +
                      "peer's popup. The hint stays queued and TryShowContextHint pops it on each peer's own " +
                      "idle frame.");
            __result = Nothing();
            return false;
        }

        private static IEnumerator<NextUpdate> Nothing() { yield break; }
    }
}
