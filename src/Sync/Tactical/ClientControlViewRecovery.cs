using System;
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
        /// re-establish control: set <c>TacticalView.SelectedActor</c> to <paramref name="actor"/> when it is a
        /// selectable own actor (so <c>UIStateInitial.cs:77</c> picks it), then call the native
        /// <c>TacticalView.ResetViewState()</c> (re-pushes <c>UIStateInitial</c> → <c>UIStateCharacterSelected</c>).
        /// Fully guarded + reflection-only: a missing member or any throw must NEVER wedge the apply (recovery is
        /// best-effort on top of an already-applied authoritative mirror). Idempotent — <c>ResetViewState</c> only
        /// re-pushes <c>UIStateInitial</c> if not already there.
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

                // (1) Select the acting actor IF it is a valid own selection target, so UIStateInitial.cs:77 picks
                //     it (else native falls back to GetSelectableActors().FirstOrDefault() — still an own actor).
                TrySelectActor(view, actor);

                // (2) Re-establish the control view the native way: ResetViewState() →
                //     SwitchToState(new UIStateInitial(), ClearStackAndPush) (TacticalView.cs:262-268). This
                //     re-pushes UIStateInitial onto the (possibly empty) stack; its InitialStateUpdateCrt then
                //     advances to UIStateCharacterSelected (HasAliveActors on the client's player turn).
                var reset = AccessTools.Method(view.GetType(), "ResetViewState", Type.EmptyTypes);
                if (reset != null) reset.Invoke(view, null);
                else Debug.LogError("[Multipleer][tac] RestoreClientControlView: ResetViewState() not found");

                Debug.Log("[Multipleer][tac] CLIENT restored control view after suppressed non-shoot action (wedge=" +
                          (curStateName ?? "<empty>") + ")");
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] RestoreClientControlView failed: " + ex); }
        }

        /// <summary>Set <c>TacticalView.SelectedActor</c> to <paramref name="actor"/> only when the view reports it
        /// as selectable (<c>TacticalView.IsActorSelectable(TacticalActor)</c>, TacticalView.cs:886). A non-own /
        /// non-selectable actor (host/enemy) is skipped — native then selects the first selectable own actor.</summary>
        private static void TrySelectActor(object view, object actor)
        {
            try
            {
                if (actor == null) return;
                var isSelectable = AccessTools.Method(view.GetType(), "IsActorSelectable");
                if (isSelectable != null)
                {
                    object ok = isSelectable.Invoke(view, new[] { actor });
                    if (!(ok is bool b && b)) return;   // not a valid own selection → leave it to the native fallback
                }
                var prop = AccessTools.Property(view.GetType(), "SelectedActor");
                if (prop != null && prop.CanWrite) prop.SetValue(view, actor, null);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer][tac] RestoreClientControlView select failed: " + ex); }
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
