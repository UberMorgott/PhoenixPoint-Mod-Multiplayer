using System;
using System.Collections;
using System.Reflection;
using Base.Core;
using Base.Input;
using HarmonyLib;
using PhoenixPoint.Geoscape.Levels;
using UnityEngine;

namespace Multiplayer.Network
{
    /// <summary>
    /// THE INPUT UNLOCK IS AN INVARIANT, NOT AN EDGE — AND IT LIVES ON <see cref="InputController"/>.
    ///
    /// The game's only unlock edge is one pair: <c>GeoscapeView.InitView</c>:340 takes
    /// <c>RequestOverrideInputSet(LoadingScreenInputSet)</c> and <c>GeoscapeView.OnCurtainLifted</c>:2160
    /// is the SOLE release, raised only at the tail of a COMPLETED
    /// <c>LevelSwitchCurtainController.LiftCurtainCrt</c>. Co-op kills that edge on purpose
    /// (<c>CurtainShowPatch.Prefix</c> suppresses the Loaded→Playing auto-lift, <c>CurtainLiftGatePatch</c>
    /// parks every lift) and re-issues it from <c>SaveTransferCoordinator.PerformDeferredLift</c>. Any run
    /// whose re-issued lift never reaches its tail latches the override FOREVER, and the failure is SILENT:
    /// <c>InputController.SetInputSets</c>:520-527 applies a new set only while no override is held, so every
    /// later <c>UIState*.EnterState → SetInputState(…)</c> is stored and discarded. The world still renders at
    /// full fps with zero exceptions — camera, hotkeys, world clicks and cutscene skip are simply dead, while
    /// uGUI buttons keep working because they never consult an input set.
    ///
    /// WHY THE PREVIOUS REPAIR MISSED TWO WHOLE WINDOWS. It resolved the override's owner through
    /// <c>GameUtl.CurrentLevel()?.GetComponent&lt;GeoLevelController&gt;()?.View</c> and bailed on
    /// <c>CurrentViewState == null</c>. The first made it GEOSCAPE-ONLY — but the override lives on the
    /// GAME-scoped <c>InputController</c>, so a latch taken on the geoscape is CARRIED across the level
    /// switch into tactical, where no geo level exists and the repair could not even look. The second made it
    /// abstain for the whole window between the reveal and the first geoscape view state. Live proof
    /// (2026-08-06): the repair line fired ONCE at the geoscape reveal 21:17:23.187 and never again, including
    /// at the tactical reveal 21:22:45.589.
    ///
    /// SO THE DECISION TAKES NO LEVEL AND NO VIEW STATE. <see cref="ShouldClear"/> is given the reveal flag,
    /// the overrides actually held, and the loading-path set — nothing else, which is what makes "every level,
    /// every frame, including before any view state exists" a property of the signature rather than a promise.
    /// The geo lookup survives only in <see cref="LearnLoadingSet"/>, which LEARNS the set's identity once and
    /// never gates the clear afterwards.
    ///
    /// THE OWNERSHIP TEST IS POSITIVE, AND THAT IS WHY IT IS SAFE. Overrides do not stack:
    /// <c>InputController.SetOverrideInputSets</c>:550 REPLACES <c>_overrideInputSets</c> wholesale. A message
    /// box (<c>MessageBoxPromptController</c>:97), a cure confirmation (<c>UIModuleCureConfirmation</c>:76), a
    /// tutorial (<c>TacticalTutorial</c>:333) or the cheat console (<c>AnvilCheatsUI</c>:311) therefore each
    /// hold a DIFFERENT set, and their presence is observable as the absence of ours. Clearing only when the
    /// held array still contains the loading set is exactly "no legitimate holder is holding this".
    /// </summary>
    internal static class RevealInputLock
    {
        private static readonly FieldInfo OverrideSetsField =
            AccessTools.Field(typeof(InputController), "_overrideInputSets");

        // The loading path's own input set, learned once and then owned level-agnostically. It is a BaseDef
        // (Base.Input.InputSetDef : BaseDef), not a scene object, so the reference stays valid across every
        // later level switch — which is the whole reason the geoscape lookup can be a one-shot LEARN.
        private static InputSetDef _loadingSet;

        /// <summary>Does the held override array still belong to the LOADING path? Pure, and typed on
        /// <see cref="Array"/> because that is what the reflected field hands back — a real modal/tutorial
        /// holder replaced the array with its own set and answers false here.</summary>
        internal static bool IsLoadingOverride(Array active, object loadingSet) =>
            loadingSet != null && active != null && Array.IndexOf(active, loadingSet) >= 0;

        /// <summary>THE INVARIANT, as a pure decision: once this peer has revealed, no override left behind by
        /// the loading path may still be held. Deliberately takes no level, no view and no view state.</summary>
        internal static bool ShouldClear(bool revealed, Array activeOverrides, object loadingSet) =>
            revealed && IsLoadingOverride(activeOverrides, loadingSet);

        // One-shot identity learn. The set is a public field on the GeoscapeView that takes the override, so
        // the only place it can be read is a live geoscape — and reading it there is enough, because a latch
        // that reaches tactical was necessarily taken on a geoscape first.
        private static void LearnLoadingSet()
        {
            if (_loadingSet != null) return;
            var set = GameUtl.CurrentLevel()?.GetComponent<GeoLevelController>()?.View?.LoadingScreenInputSet;
            if (set != null) _loadingSet = set;
        }

        /// <summary>Converge the invariant on the game-scoped <see cref="InputController"/>. Returns true on
        /// the frame it actually cleared something, so the caller can report it.</summary>
        internal static bool Converge(bool revealed)
        {
            LearnLoadingSet();
            var input = GameUtl.GameComponent<InputController>();
            if (input == null || !input.HaveOverrideInput) return false;
            if (!ShouldClear(revealed, OverrideSetsField?.GetValue(input) as Array, _loadingSet)) return false;
            input.ClearOverrideInputSet();
            return true;
        }

        // ── DIAGNOSTIC, 2026-08-10 — REMOVE WHEN THE CLIENT INPUT-DEATH REPORT IS CLOSED ─────────────
        // Reported live and confirmed by the player: on CLIENTS the whole keyboard, the geoscape camera and
        // WORLD CLICKS are dead while uGUI tabs still click — the exact signature this class's header
        // describes, but NOT the stale override this class repairs. The 2026-08-10 logs prove the override
        // was cleared FOUR FRAMES BEFORE UIStateGeoCutscene entered and the 2:01 intro still could not be
        // skipped on either client (the host skipped it at 11.85 s). Only three things inside
        // InputController can swallow a view state's input, so this names all three and prints the tuple
        // ONLY WHEN IT CHANGES — a complete timeline of when input died and what killed it, at no
        // per-frame log cost:
        //   · OverrideEventHandlers non-empty → DispatchEvent:947 skips the NORMAL handler list entirely
        //     (every GeoscapeViewState.OnInputEventInternal) for every event type but InputTypeChanged.
        //   · _callHandlersRefCount > 0 → Update:1102 skips the whole input pass, which ALSO freezes any
        //     pending override change (SetOverrideInputSets is inside the skipped branch at :1111).
        //   · the active set is simply wrong → the named sets and the active action COUNT say so.
        private static readonly FieldInfo InputSetsField =
            AccessTools.Field(typeof(InputController), "_inputSets");
        private static readonly FieldInfo CallHandlersRefField =
            AccessTools.Field(typeof(InputController), "_callHandlersRefCount");
        private static readonly FieldInfo ActiveActionHashesField =
            AccessTools.Field(typeof(InputController), "_activeActionHashes");
        private static string _lastProbe;

        private static string Names(object sets)
        {
            var array = sets as Array;
            if (array == null || array.Length == 0) return "[]";
            var names = new string[array.Length];
            for (int i = 0; i != array.Length; i++)
            {
                var o = array.GetValue(i) as UnityEngine.Object;
                names[i] = o == null ? "?" : o.name;
            }
            return "[" + string.Join(",", names) + "]";
        }

        /// <summary>Print the input-dispatch tuple whenever it changes. Reads only; never throws out.</summary>
        internal static void ProbeInputDispatch()
        {
            try
            {
                var input = GameUtl.GameComponent<InputController>();
                if (input == null) return;
                var hashes = ActiveActionHashesField?.GetValue(input) as ICollection;
                string probe = "sets=" + Names(InputSetsField?.GetValue(input)) +
                               " override=" + Names(OverrideSetsField?.GetValue(input)) +
                               " actions=" + (hashes == null ? -1 : hashes.Count) +
                               " overrideHandlers=" + input.OverrideEventHandlers.Count +
                               " handlers=" + input.EventHandlers.Count +
                               " callHandlers=" + input.CallHandlers +
                               " refCount=" + (CallHandlersRefField?.GetValue(input) ?? "?");
                if (string.Equals(probe, _lastProbe, StringComparison.Ordinal)) return;
                _lastProbe = probe;
                Debug.Log("[MP][input] " + probe);
            }
            catch (Exception e) { Debug.LogError("[MP][input] probe failed: " + e.Message); }
        }
    }
}
