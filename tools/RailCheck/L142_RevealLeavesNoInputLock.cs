using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View;

namespace RailCheck
{
    /// <summary>
    /// L142 — AFTER A REVEAL, NO INPUT-SET OVERRIDE LEFT BY THE LOADING PATH IS STILL HELD, ON ANY LEVEL.
    ///
    /// THE REPORT (2026-08-06): on the client the world runs at full fps with zero exceptions, uGUI buttons
    /// still click, and everything else is dead — keyboard, hotkeys, world/map clicks, aircraft control,
    /// cutscene skip. The mod installs no input patch at all; the block is the GAME's, latched by our reveal.
    /// <c>GeoscapeView.InitView</c>:340 takes <c>RequestOverrideInputSet(LoadingScreenInputSet)</c> and the
    /// ONLY release is <c>GeoscapeView.OnCurtainLifted</c>:2160, raised solely at the tail of a COMPLETED
    /// <c>LevelSwitchCurtainController.LiftCurtainCrt</c>. Co-op deliberately kills that edge
    /// (<c>CurtainShowPatch.Prefix</c> + <c>CurtainLiftGatePatch</c> park every lift) and re-issues it from
    /// <c>SaveTransferCoordinator.PerformDeferredLift</c>. Lose the tail once and the override latches
    /// FOREVER, silently: <c>InputController.SetInputSets</c>:520-527 applies a new set only while no override
    /// is held, so every later <c>UIState*.EnterState → SetInputState(…)</c> is stored and discarded. uGUI
    /// survives because a button never consults an input set — which is exactly why the symptom reads as
    /// "the game is fine, my hands are gone".
    ///
    /// THE REPAIR EXISTED AND ABSTAINED IN THE TWO WINDOWS THAT MATTERED. It resolved the override's owner
    /// through <c>GameUtl.CurrentLevel()?.GetComponent&lt;GeoLevelController&gt;()?.View</c> — GEOSCAPE-ONLY,
    /// while the override itself lives on the GAME-scoped <c>InputController</c> and is therefore CARRIED
    /// across the level switch into tactical, where no geo level exists and the repair could not even look —
    /// and it bailed on <c>CurrentViewState == null</c>, which is the whole window between the reveal and the
    /// first geoscape view state. Live proof: `reveal input-lock repair: cleared LoadingScreenInputSet` fired
    /// ONCE at the geoscape reveal 21:17:23.187 and NEVER again, including at the tactical reveal 21:22:45.589.
    /// The unskippable intro is the same latch seen from the other side: the cutscene raise lands in the same
    /// millisecond as the reveal, while the lift fade still holds the set, so its skip binding is discarded.
    ///
    /// THE FIX IS THE SHAPE, NOT THE PATCH: an edge we can lose is held as an INVARIANT at the level-agnostic
    /// owner. <c>RevealInputLock.ShouldClear(revealed, activeOverrides, loadingSet)</c> is given the reveal
    /// flag, the overrides actually held and the loading set — NO level, NO view, NO view state — so "covers
    /// tactical" and "covers the pre-view-state window" are properties of the signature rather than promises.
    /// The geo lookup survives only in <c>LearnLoadingSet</c>, which learns the set's identity once (it is a
    /// <c>BaseDef</c>, so the reference outlives the scene) and never gates the clear afterwards.
    ///
    /// THE OWNERSHIP TEST IS POSITIVE, AND THAT IS WHAT PROTECTS LEGITIMATE HOLDERS. Overrides do not stack —
    /// <c>InputController.SetOverrideInputSets</c>:550 REPLACES the array wholesale — so a message box
    /// (<c>MessageBoxPromptController</c>:97), a cure confirmation (<c>UIModuleCureConfirmation</c>:76), a
    /// tutorial (<c>TacticalTutorial</c>:333) or the cheat console (<c>AnvilCheatsUI</c>:311) each hold a
    /// DIFFERENT set, and their presence is observable as the ABSENCE of ours. "Clear only when the held array
    /// still contains the loading set" is therefore exactly "no legitimate holder is holding this", with no
    /// enumeration of holders to rot.
    ///
    /// ARMS (a)-(d) EXECUTE the shipped decision; (e)/(f) are the two holes, pinned structurally because a
    /// console host cannot stand up an <c>InputController</c>; (g) keeps the invariant converged every frame.
    /// Falsify: restore the <c>GeoLevelController</c>-only lookup inside <c>Converge</c> →
    /// <c>decision-consults-a-level</c> (tactical uncovered again); restore the <c>CurrentViewState == null</c>
    /// bail → <c>decision-waits-for-a-view-state</c> (the pre-reveal window uncovered again); invert
    /// <c>ShouldClear</c> either way → (a) or (b).
    /// </summary>
    internal static class L142_RevealLeavesNoInputLock
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var modAsm = typeof(SaveTransferCoordinator).Assembly;
            var lockType = modAsm.GetType("Multiplayer.Network.RevealInputLock");
            var shouldClear = lockType?.GetMethod("ShouldClear", All);
            var converge = lockType?.GetMethod("Converge", All);
            var repair = typeof(SaveTransferCoordinator).GetMethod("RepairRevealInputLock", All);
            var update = typeof(SaveTransferCoordinator).GetMethod("Update", All);

            if (shouldClear == null || converge == null || repair == null || update == null)
            {
                yield return "L142 premise-changed: RevealInputLock.{ShouldClear,Converge} / " +
                             "SaveTransferCoordinator.{RepairRevealInputLock,Update} no longer resolve. The " +
                             "input-unlock invariant has moved and this law is asserting something about a shape " +
                             "the mod no longer has — re-read it before assuming a client can still move its camera.";
                yield break;
            }

            // ── (a)-(d) the decision, EXECUTED on plain sentinels ────────────
            // Typed on Array/object precisely so it can be executed here: the real values are InputSetDef
            // (a ScriptableObject-derived BaseDef) which no console host can construct, and identity is the
            // only property the decision ever asks about.
            object loading = new object();
            object modal = new object();
            Func<bool, object[], object, bool> ask = (revealed, held, set) =>
                (bool)shouldClear.Invoke(null, new object[] { revealed, held, set });

            if (!ask(true, new[] { loading }, loading))
                yield return "L142 reveal-leaves-the-lock-on: ShouldClear answers FALSE for a peer that has " +
                             "REVEALED while the loading path's own override is still held. That is the whole " +
                             "report: InputController.SetInputSets:520-527 discards every later SetInputState " +
                             "while an override is held, so the world renders at full fps with zero exceptions " +
                             "and the player has no camera, no hotkeys and no world clicks, forever.";
            if (ask(true, new[] { modal }, loading))
                yield return "L142 clears-a-modals-override: ShouldClear clears an override the peer is holding " +
                             "for something ELSE. Overrides do not stack (SetOverrideInputSets:550 replaces the " +
                             "array), so a foreign set means a MessageBox, a cure confirmation, a tutorial or the " +
                             "cheat console owns input right now — and stealing it back mid-prompt is a worse bug " +
                             "than the one this law exists for.";
            if (ask(true, new object[0], loading))
                yield return "L142 clears-nothing-held: ShouldClear answers TRUE with NO override held at all. " +
                             "The clear is a request the game applies on its own Update, so a decision that is " +
                             "true on an empty array fires every frame forever and its log line stops meaning " +
                             "anything.";
            if (ask(false, new[] { loading }, loading))
                yield return "L142 clears-during-the-load: ShouldClear clears the loading override BEFORE this " +
                             "peer revealed. That override is the game's legitimate hold while the level loads — " +
                             "clearing it there hands the player a live camera over a half-built world, which is " +
                             "law L70's blocker arriving by the other door.";
            if (ask(true, new[] { loading }, null))
                yield return "L142 clears-an-unlearned-set: ShouldClear clears while the loading set's identity " +
                             "is still unknown. With nothing to compare against, every override in the game — " +
                             "modal, tutorial, console — matches by default.";

            // ── (e) the first hole: the decision may not be level-gated ──────
            var viewField = typeof(GeoLevelController).GetField("View", All);
            if (viewField != null && Program.ReadsField(converge, viewField))
                yield return "L142 decision-consults-a-level: RevealInputLock.Converge reads " +
                             "GeoLevelController.View again. The override lives on the GAME-scoped " +
                             "InputController and is CARRIED across the level switch, so resolving its owner " +
                             "through the geo level makes the repair geoscape-only and leaves a latch taken at " +
                             "the geoscape reveal untouchable for the whole battle — measured 2026-08-06: the " +
                             "repair line fired once at 21:17:23.187 and never at the tactical reveal 21:22:45.589.";

            // ── (f) the second hole: nor may it wait for a view state ────────
            if (Program.Callees(converge, typeof(GeoscapeView).Assembly)
                       .Any(c => c.Name == "get_CurrentViewState"))
                yield return "L142 decision-waits-for-a-view-state: RevealInputLock.Converge consults " +
                             "GeoscapeView.CurrentViewState again. That is null for the whole window between the " +
                             "reveal and the first geoscape view state — exactly the window the reveal opens — so " +
                             "the invariant abstains at the one moment it was written for.";

            // ── (g) an invariant that is not converged is a comment ──────────
            if (!Program.Callees(update, modAsm).Any(c => c.MetadataToken == repair.MetadataToken) ||
                !Program.Callees(repair, modAsm).Any(c => c.MetadataToken == converge.MetadataToken))
                yield return "L142 invariant-not-converged: SaveTransferCoordinator.Update no longer reaches " +
                             "RevealInputLock.Converge through RepairRevealInputLock. The unlock is an edge the " +
                             "co-op reveal machinery can lose; holding it as a state is the entire fix, and a " +
                             "state nothing re-evaluates is just the lost edge with a longer comment.";
        }
    }
}
