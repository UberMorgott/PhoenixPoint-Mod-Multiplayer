using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace RailCheck
{
    /// <summary>
    /// L129 — DE-BLOCKERING A POPUP MUST NOT DELETE IT. Every peer still SEES the panel.
    ///
    /// THE PAIR THIS LAW COMPLETES. L91 arm (g) says the shared alien turn may not stop on one peer's
    /// screen, and the cure it demands is a prefix that hands back an immediately-finished enumerator
    /// (<c>HintWaitGate</c>, src\Tactical\TacticalHintGate.cs). But the funnel it neutralises,
    /// <c>TacticalView.WaitUntilHintsAreConfirmed</c> (TacticalView.cs:359-373), is not a pure wait — its
    /// first statement is <c>if (!TryShowContextHint()) yield break;</c>. So the SAME one-line cure that
    /// satisfies L91 also removes a display call, and nothing was watching that half. Suppress the
    /// modality and you have a fix; suppress the popup and you have the bug the fix was for, wearing the
    /// fix's name — a TFTV boss panel (Umbra/VoidTouched/gang intro) that the host sees and no client does.
    /// L91 would stay green through it. This law is the other half of that cure.
    ///
    /// WHY THE DISPLAY SURVIVES TODAY, and it is the game's own machinery rather than anything of ours: the
    /// hint stays queued in <c>ContextHelpManager._hintsPendingDisplay</c> and FIVE native tactical view
    /// states call <c>TacticalView.TryShowContextHint</c> from their per-frame <c>UpdateState</c>
    /// (UIStateCharacterSelected:1105, UIStateShoot:1127, UIStateAbilitySelected:372, UIStateInventory:421,
    /// UIStateOverwatchAbilitySelected:111). Each peer therefore pops its own panel on its own idle frame
    /// and releases only itself — measured on a client 2026-08-05 21:58 (client Player.log
    /// "Showing hint: VoidTouchedSighted" at t=539,98, dismissed at 540,50 and 545,68, while the turn
    /// changed to Phoenix at 540,04 — shown, and blocking nobody). That premise is what the arms below
    /// keep true; it is not restated in a comment somewhere and hoped for.
    ///
    /// THE ARMS:
    ///   (a) <c>substituted-wait-still-waits</c> — the enumerator this mod hands back in place of the
    ///       native hint wait must FINISH. Any iterator inside that patch class that reads
    ///       <c>NextUpdate.NextFrame</c> is a hold by construction (L91's own criterion), i.e. we replaced
    ///       one peer's clock with our own.
    ///   (b) <c>display-pump-gone</c> / <c>display-pump-patched</c> — THE OUTCOME. Derived from the game,
    ///       not listed: every <c>UpdateState</c> in <c>PhoenixPoint.Tactical.View.ViewStates</c> whose IL
    ///       calls <c>TryShowContextHint</c> is a pump. The set must be non-empty (or a queued hint is
    ///       queued forever and "each peer pops its own" is fiction), and this mod must patch NONE of them,
    ///       nor <c>TryShowContextHint</c> itself, nor <c>ContextHelpManager.RegisterContextHelpHint</c> —
    ///       the three places a gate would turn "shown on every peer" back into "shown on the host".
    ///   (c) <c>client-replay-fires-nothing</c> — the peers whose battle is a LOADED SAVE never cross the
    ///       turn-1 edge that registers the already-visible enemies (TacContextHelpManager:244-262), so the
    ///       mod re-fires it (<c>ClientMissionStartHints</c>). Asserted by EFFECT: some method of this mod
    ///       must actually call <c>ContextHelpManager.EventTypeTriggered</c>. A replay that registers
    ///       nothing is a client with an empty queue and no panel to pop.
    ///
    /// CEILING: arm (b) reads Harmony ATTRIBUTES, so a patch that resolves its target in a
    /// <c>TargetMethod()</c> body is invisible to it. That is under-reporting, never a false red, and it is
    /// the same trade L91's arm (g) already takes.
    ///
    /// Falsify: point <c>HintWaitGate</c> at <c>TryShowContextHint</c> instead of
    /// <c>WaitUntilHintsAreConfirmed</c> — the exact "suppressed the popup, not its modality" slip —
    /// → <c>display-pump-patched</c>; make <c>HintWaitGate.Nothing()</c> yield
    /// <c>NextUpdate.NextFrame</c> → <c>substituted-wait-still-waits</c>.
    /// </summary>
    internal static class L129_HintStillShows
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        private const string WaitName = "WaitUntilHintsAreConfirmed";
        private const string ShowName = "TryShowContextHint";
        private const string ViewStates = "PhoenixPoint.Tactical.View.ViewStates";

        internal static IEnumerable<string> Check(Assembly game)
        {
            var mod = typeof(Multiplayer.Network.Sync.DiffEngine).Assembly;
            var view = game.GetType("PhoenixPoint.Tactical.View.TacticalView");
            var manager = game.GetType("PhoenixPoint.Common.ContextHelp.ContextHelpManager");
            // The trigger entry point is the TACTICAL subclass, not the shared base: only it knows the
            // tactical HintTrigger set (TacContextHelpManager.cs:53).
            var tacManager = game.GetType("PhoenixPoint.Tactical.ContextHelp.TacContextHelpManager");
            var nextFrame = game.GetType("Base.Core.NextUpdate")?.GetField("NextFrame", All);
            var show = view?.GetMethod(ShowName, All);
            var register = manager?.GetMethod("RegisterContextHelpHint", All);
            var triggered = tacManager?.GetMethod("EventTypeTriggered", All);

            if (view == null || manager == null || tacManager == null || nextFrame == null || show == null ||
                register == null || triggered == null)
            {
                yield return "L129 premise-changed: one of TacticalView." + ShowName + " / " +
                             "ContextHelpManager.RegisterContextHelpHint / TacContextHelpManager." +
                             "EventTypeTriggered / NextUpdate.NextFrame no longer " +
                             "resolves. The hint display path has moved, so this law is asserting something " +
                             "about a shape the game no longer has — re-read it before trusting that a " +
                             "de-blockered boss panel still reaches every peer.";
                yield break;
            }

            var patched = PatchedGameMethods(mod);

            // ── (a) what we hand back in place of the native wait must FINISH ──
            var gate = mod.GetTypes().FirstOrDefault(t => Patches(t).Contains(view.Name + "." + WaitName));
            if (gate == null)
                yield return "L129 no-wait-patch: no type in this mod declares a Harmony patch on TacticalView." +
                             WaitName + ". That funnel is where the shared alien turn stops on ONE peer's " +
                             "un-dismissed panel (L91 arm (g)), and this law's whole subject is what the cure " +
                             "for it must not break. Nothing to check means the cure is gone, not that it holds.";
            else
                foreach (var iter in gate.GetNestedTypes(All))
                {
                    var move = iter.GetMethod("MoveNext", All);
                    if (move != null && Program.ReadsField(move, nextFrame))
                        yield return "L129 substituted-wait-still-waits: " + gate.Name + "." + iter.Name +
                                     " yields NextUpdate.NextFrame. This iterator is what we hand the engine " +
                                     "INSTEAD of the native hint wait, and a substitute that itself lets " +
                                     "wall-clock time pass is the original deadlock with our name on it — the " +
                                     "alien turn goes back to running at the speed of one player's mouse.";
            }

            // ── (b) THE OUTCOME: the per-frame pump exists on every peer and we do not touch it ──
            var pumps = game.GetTypes()
                            .Where(t => t.Namespace == ViewStates)
                            .SelectMany(t => t.GetMethods(All).Where(m => m.Name == "UpdateState"))
                            .Where(m => Program.Callees(m, game).Any(c => c.Name == ShowName))
                            .ToList();
            if (pumps.Count == 0)
                yield return "L129 display-pump-gone: not one UpdateState in " + ViewStates + " calls " +
                             ShowName + " any more. That per-frame call is the ONLY reason a hint queued " +
                             "while the shared turn was de-blockered ever reaches a screen: the panel is not " +
                             "pushed by whoever registered it, it is popped by each peer's own idle state. " +
                             "Without it a de-blockered hint is a deleted hint, on every peer.";

            foreach (var m in pumps.Cast<MethodBase>().Concat(new MethodBase[] { show, register }))
            {
                var key = m.DeclaringType.Name + "." + m.Name;
                if (patched.Contains(key))
                    yield return "L129 display-pump-patched: this mod patches " + key + ", which is on the " +
                                 "path that puts a queued hint on the screen. Gate it — on IsHost, on a view " +
                                 "state, on anything — and the TFTV boss panel goes back to being something " +
                                 "only the host ever sees, while L91 stays green because the turn no longer " +
                                 "stops. De-blocker the WAIT (TacticalView." + WaitName + "), never the show.";
            }

            // ── (c) the replay the clients need must actually register something ──
            var fires = mod.GetTypes()
                           .SelectMany(t => t.GetMethods(All).Cast<MethodBase>()
                                             .Concat(t.GetConstructors(All)))
                           .Any(m => Program.Callees(m, game).Any(c => c.Name == triggered.Name &&
                                                                       c.DeclaringType == tacManager));
            if (!fires)
                yield return "L129 client-replay-fires-nothing: no method of this mod calls " +
                             "TacContextHelpManager.EventTypeTriggered. A client's battle is the host's mid-tactical " +
                             "SAVE, so it is always past the turn-1 edge where the game registers hints for the " +
                             "enemies already on the board (TacContextHelpManager:244-262) — the host crosses it " +
                             "natively and no client ever does. Without our re-fire the client's pending queue " +
                             "is empty, and a pump with nothing to pop shows nothing.";
        }

        /// <summary>Every game method this mod declares a Harmony patch on, as "Type.Method".
        /// <c>HarmonyPatch</c> is AllowMultiple and splits the type/name across attributes, so the pair is
        /// assembled per patch class rather than per attribute.</summary>
        private static HashSet<string> PatchedGameMethods(Assembly mod)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in mod.GetTypes())
                foreach (var pair in Patches(t))
                    set.Add(pair);
            return set;
        }

        private static IEnumerable<string> Patches(Type t)
        {
            Type declaring = null;
            string method = null;
            object[] attrs;
            try { attrs = t.GetCustomAttributes(typeof(HarmonyPatch), inherit: false); }
            catch { yield break; }
            foreach (HarmonyPatch a in attrs)
            {
                if (a.info == null) continue;
                if (a.info.declaringType != null) declaring = a.info.declaringType;
                if (!string.IsNullOrEmpty(a.info.methodName)) method = a.info.methodName;
            }
            if (declaring != null && method != null) yield return declaring.Name + "." + method;
        }
    }
}
