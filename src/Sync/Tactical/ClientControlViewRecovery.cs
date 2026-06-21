using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multipleer.Sync.Tactical
{
    /// <summary>
    /// RUNTIME side of the CLIENT non-shoot-action view-freeze fix (the reflection boundary; the pure decision is
    /// <see cref="ClientControlViewRecoveryGate"/>). Called at the END of a mirrored non-shoot action apply —
    /// <see cref="TacticalMoveSync.ClientOnMove"/> (covers MOVE + melee/charge, which begin with the suppressed
    /// move) and <see cref="TacticalOverwatchSync.HandleOverwatchState"/> (covers OVERWATCH). No-op off-mirror /
    /// off-client; pure view/selection (no game-state mutation).
    /// </summary>
    public static class ClientControlViewRecovery
    {
        /// <summary>
        /// If the client mirror's tactical view is WEDGED (per <see cref="ClientControlViewRecoveryGate"/>),
        /// re-establish control by driving the view DIRECTLY into <c>UIStateCharacterSelected</c> — exactly what a
        /// player CLICK on a soldier does, and what the native <c>reset_tactical_view</c> console command does
        /// (<c>UIStateCharacterSelected.ResetTacticalViewCmd</c>, UIStateCharacterSelected.cs:1292-1300 =
        /// <c>view._statesStack.SwitchToState(new UIStateCharacterSelected(), StateStackAction.ClearStackAndPush)</c>).
        ///
        /// WHY NOT <c>ResetViewState()</c> (the previous, in-game-proven NO-OP): <c>ResetViewState</c> targets
        /// <c>UIStateInitial</c> and GUARDS <c>if (!(CurrentState is UIStateInitial))</c> (TacticalView.cs:264) — but
        /// post-move the wedged view is ALREADY in <c>UIStateInitial</c>, so the guard skipped the switch and nothing
        /// happened. And <c>UIStateInitial.InitialStateUpdateCrt</c> never advanced on its own post-move. Switching
        /// STRAIGHT to <c>UIStateCharacterSelected</c> sidesteps the dead <c>UIStateInitial</c> spin entirely:
        /// <c>UIStateCharacterSelected.EnterState</c> (UIStateCharacterSelected.cs:349-379) AUTO-SELECTS an actor
        /// (keeps <c>SelectedCharacter</c> if it can act, else <c>GetSelectableActors().FirstOrDefault()</c> /
        /// <c>ForceAwakeAndSelectSomeActor()</c>) and calls <c>SelectCharacter</c> — a fully live control state with a
        /// selected soldier, no dependence on the <c>UIStateInitial</c> coroutine.
        ///
        /// We first set <c>TacticalView.SelectedActor</c> to <paramref name="actor"/> when it is a selectable own
        /// actor, so <c>EnterState</c> KEEPS the soldier that just acted selected (matches a manual click). If it is
        /// not selectable (host/enemy), we skip and let <c>EnterState</c> auto-pick a valid own actor.
        ///
        /// Fully guarded + reflection-only: a missing member or any throw must NEVER wedge the apply (recovery is
        /// best-effort on top of an already-applied authoritative mirror). The state switch mirrors the proven
        /// reflection seam in <c>TacticalTurnSync.TryDriveClientViewDown</c>.
        /// </summary>
        public static void RestoreClientControlView(object actor)
        {
            try
            {
                if (!TacticalDeploySync.IsClientMirroring) return;

                object tlc = TacticalDeploySync.LiveTlc;
                object view = tlc != null ? GetProp(tlc, "View") : null;
                if (view == null) return;

                // Read the live top view-state type name (null when the stack is empty → the worst wedge).
                object curState = GetProp(view, "CurrentState");
                string curStateName = curState != null ? curState.GetType().Name : null;

                if (!ClientControlViewRecoveryGate.ShouldRecoverControlView(
                        TacticalDeploySync.IsClientMirroring, curStateName))
                    return;   // view is healthy (a live control state) → never clobber it

                // (1) Pre-select the acting actor IF it is a valid own selection target so EnterState KEEPS it
                //     selected (else EnterState auto-picks the first selectable own actor).
                bool preSelected = TrySelectActor(view, actor);

                // (2) Drive the view STRAIGHT into UIStateCharacterSelected (native reset_tactical_view primitive,
                //     UIStateCharacterSelected.cs:1299): _statesStack.SwitchToState(new UIStateCharacterSelected(),
                //     ClearStackAndPush). EnterState then auto-selects + shows the action HUD. This is robust even
                //     when the wedge is the dead UIStateInitial that ResetViewState() could not escape.
                bool switched = TrySwitchToCharacterSelected(view);

                Debug.Log("[Multipleer][tac] CLIENT recovered control view: " +
                          (preSelected ? "selected acting actor" : "auto-select") +
                          " -> UIStateCharacterSelected switched=" + switched + " (wedge=" + (curStateName ?? "<empty>") + ")");
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] RestoreClientControlView failed: " + ex); }
        }

        /// <summary>Set <c>TacticalView.SelectedActor</c> to <paramref name="actor"/> only when the view reports it
        /// as selectable (<c>TacticalView.IsActorSelectable(TacticalActor)</c>, TacticalView.cs:886). A non-own /
        /// non-selectable actor (host/enemy) is skipped — <c>UIStateCharacterSelected.EnterState</c> then selects the
        /// first selectable own actor. Returns true iff it actually set the selection.</summary>
        private static bool TrySelectActor(object view, object actor)
        {
            try
            {
                if (actor == null) return false;
                var isSelectable = AccessTools.Method(view.GetType(), "IsActorSelectable");
                if (isSelectable != null)
                {
                    object ok = isSelectable.Invoke(view, new[] { actor });
                    if (!(ok is bool b && b)) return false;   // not a valid own selection → leave it to EnterState
                }
                var prop = AccessTools.Property(view.GetType(), "SelectedActor");
                if (prop != null && prop.CanWrite) { prop.SetValue(view, actor, null); return true; }
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] RestoreClientControlView select failed: " + ex); }
            return false;
        }

        /// <summary>Drive the live view's state stack straight to <c>UIStateCharacterSelected</c> via
        /// <c>StateStack.SwitchToState(new UIStateCharacterSelected(false, true), StateStackAction.ClearStackAndPush)</c>
        /// — the SAME call native uses for <c>reset_tactical_view</c> (UIStateCharacterSelected.cs:1299) and on a
        /// selection reset (UIStateCharacterSelected.cs:310). <c>UIStateCharacterSelected</c> is an INTERNAL class
        /// with ctor <c>(bool showPlayersIntroText=false, bool cameraChaseSelectedCharacter=true)</c> — bind
        /// non-public, pass the native defaults. Reflection mirrors the proven seam in
        /// <c>TacticalTurnSync.TryDriveClientViewDown</c>. Returns true iff the switch was invoked.</summary>
        private static bool TrySwitchToCharacterSelected(object view)
        {
            try
            {
                object statesStack = Traverse.Create(view).Field("_statesStack").GetValue();
                if (statesStack == null) { Debug.LogError("[Multipleer][tac] RestoreClientControlView: _statesStack null"); return false; }

                var selType = AccessTools.TypeByName("PhoenixPoint.Tactical.View.ViewStates.UIStateCharacterSelected");
                var actionType = AccessTools.TypeByName("Base.UI.StateStackAction");
                if (selType == null || actionType == null)
                { Debug.LogError("[Multipleer][tac] RestoreClientControlView: UIStateCharacterSelected/StateStackAction type not found"); return false; }

                // new UIStateCharacterSelected(showPlayersIntroText:false, cameraChaseSelectedCharacter:true) — the
                // native default (matches `new UIStateCharacterSelected()` at UIStateCharacterSelected.cs:1299).
                object selState = Activator.CreateInstance(
                    selType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new object[] { false, true }, null);
                object clearAndPush = Enum.Parse(actionType, "ClearStackAndPush");

                var switchTo = AccessTools.Method(statesStack.GetType(), "SwitchToState");
                if (switchTo == null) { Debug.LogError("[Multipleer][tac] RestoreClientControlView: SwitchToState not found"); return false; }
                switchTo.Invoke(statesStack, new[] { selState, clearAndPush });
                return true;
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] TrySwitchToCharacterSelected failed: " + ex); return false; }
        }

        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            var p = AccessTools.Property(obj.GetType(), name);
            if (p != null) return p.GetValue(obj, null);
            var f = AccessTools.Field(obj.GetType(), name);
            return f?.GetValue(obj);
        }
    }
}
