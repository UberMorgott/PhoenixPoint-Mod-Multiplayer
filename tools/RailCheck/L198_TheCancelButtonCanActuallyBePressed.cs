using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.UI;
using Multiplayer.Network.Sync;
using UnityEngine.UI;

namespace RailCheck
{
    /// <summary>
    /// L198 — THE CANCEL BUTTON IS A BUTTON, NOT A PICTURE OF ONE. A PRESS REACHES A HANDLER, AND A PRESS
    /// THAT CHANGES NOTHING STILL LEAVES A LINE.
    ///
    /// THE LIVE BUG (2026-08-08, three peers, the countdown's FIRST ever live exposure). The owner pressed
    /// CANCEL on the deployment-countdown overlay and the drop went ahead anyway. What made it a whole
    /// session's work rather than a minute's was that all three mod logs were EMPTY on the subject — the
    /// host's carried `launch HELD for 5 s` at 01:51:59.118 and `countdown reached zero` at 01:52:04.173
    /// exactly five seconds later, both clients carried the mirrored `DeployCountdownState` arriving, and
    /// NOT ONE peer carried anything about a cancel. Not a rejection, not a bail, nothing.
    ///
    /// THE CAUSE WAS THE CANVAS, NOT THE COUNTDOWN. `MultiplayerUI.EnsureBarCanvas` built the mod's overlay
    /// render root as `Canvas` + `CanvasScaler` and NOTHING ELSE. Unity's EventSystem only hits a Graphic on
    /// a canvas that carries a `GraphicRaycaster`, so every widget on that canvas was decorative by
    /// construction and the click never reached a handler AT ALL. It survived that long because nothing on
    /// that canvas had ever been clickable: the in-game bar and PlayerPanel set `raycastTarget = false` on
    /// every Graphic they build, deliberately, and the countdown's Cancel is the first interactive thing to
    /// land there. The game's own from-code canvas is all three components together —
    /// `Assembly-CSharp/src/LlockhamIndustries.Misc/SceneSingleton.cs:81-83`.
    ///
    /// L177 WAS GREEN THROUGH THIS AND WAS RIGHT TO BE. It asserts the countdown's DECISION — the gate, the
    /// clock, the veto not being a tally, the mission not being cancelled — and every one of those was
    /// correct. `HandleCancel` really did forward straight to `Cancel`; nothing ever asked it to. The hole
    /// is that no law had an opinion about the SURFACE the veto is pressed on, which is the L175/L191 shape
    /// again: a law that asserts the decision passes while the thing that invokes it is inert.
    ///
    ///   (a) <c>canvas-eats-every-click</c> — the panel's own canvas must carry a `GraphicRaycaster`.
    ///       Asserted as: `Attach` calls `EnsureRaycaster`, and `EnsureRaycaster` names `GraphicRaycaster` on
    ///       both an `AddComponent` and a `GetComponent` (added if absent, idempotent if not). This is the
    ///       defect itself and the one arm that would have caught it.
    ///
    ///   (b) <c>button-has-no-face</c> — a `Button` added at RUNTIME has a null `targetGraphic`, because
    ///       Unity only fills that field from the editor's `Reset`. That is the field the colour transition
    ///       drives AND the field a click resolves against, so the button reacted to nothing and looked dead
    ///       even on hover. `SkinButton` must assign it and `Attach` must call `SkinButton`. Exactly the
    ///       defect L182 already repaired one screen over (`TacticalReadySync.cs:522`), and the game's own
    ///       from-code button assigns it in the same breath as the Image
    ///       (`UnityTools.UI.SnapshotText.Lib/SnapshotTextPooling.cs:145-148`).
    ///
    ///   (c) <c>click-is-silent</c>, THE GENERAL LESSON. Every bail on the cancel path must log. The button
    ///       is wired to `CountdownPanel.OnCancelClicked` — which logs and THEN calls `RequestCancel` — and
    ///       both silent early returns are gone: `DeployCountdown.RequestCancel`'s no-session bail and
    ///       `DeployCountdown.Cancel`'s nothing-running bail each carry a Debug call now. Asserted
    ///       structurally over those three methods. Without this arm the raycaster could regress and the
    ///       next reader would again have three logs that say nothing.
    ///
    ///   (e) <c>ready-button-greyed-away</c>, THE SAME LESSON ON THE OTHER BUTTON (2026-08-10, and again
    ///       2026-08-11). Three peers reported "the READY button is gone" — twice. It was never gone: all
    ///       three were in the locked state at once (host `Player.log` 0:22 run, "JOIN from 1/2 has parity
    ///       mismatch (soft-gate, ready locked)" plus a host with no save chosen), and `RefreshControls`
    ///       answered that by invoking the clone's native `PhoenixGeneralButton.SetInteractable(false)`.
    ///       On a CLONE that is not cosmetic: it runs `ResetButtonAnimations` → `UpdateColorElements` →
    ///       `UIInteractableColorController.SetColor` (`Base.UI/UIInteractableColorController.cs:41-110`),
    ///       which paints the MAIN MENU prefab's `DisabledIdleColor` onto the backing Image AND the label
    ///       Text. On the lobby's dark card that is a dark shape holding dark text — a picture of nothing,
    ///       which is arm (b)'s defect with the polarity flipped. `fca24ef` blamed label clipping and
    ///       shortened the strings; the button vanished again, which is what proves the paint.
    ///       Asserted as: `LobbyPanel` declares NO ready-button `SetInteractable` reflection member, its
    ///       label handle is the ARRAY (`_readyButtonLabels`) so the clone's second Text cannot keep
    ///       drawing the clone-time "READY" underneath, and `OnReadyPressed` — the single shared handler
    ///       for the clone and the from-code fallback — exists to decline a locked press instead.
    ///       Declining is NOT a quorum (P13): it waits on nobody, and the host refuses both cases
    ///       authoritatively regardless.
    ///
    ///   (d) POSITIVE CONTROL, <c>panel-is-never-built</c> — arms (a)-(c) describe a panel. If
    ///       `MultiplayerUI.Awake` stops calling `Attach`, or `MultiplayerUI.Update` stops calling `Sync`,
    ///       all of them pass over a panel that is never built or never repainted, and the overlay is gone
    ///       with no line to say so.
    ///
    /// NOT A QUORUM, AND THIS LAW DOES NOT MAKE IT ONE (P13). Making the veto reachable is the opposite of a
    /// vote: it costs one peer one click, requires nobody's agreement, and a countdown nobody touches still
    /// completes on the host's clock. L177 arm (b) keeps that side honest and this law never widens it.
    ///
    /// Falsify (each turns it RED): delete the `EnsureRaycaster(barCanvas)` line from `Attach` →
    /// <c>canvas-eats-every-click</c>; delete `b.targetGraphic = face;` from `SkinButton` →
    /// <c>button-has-no-face</c>; wire the button straight to `DeployCountdown.RequestCancel` again, or
    /// remove the Debug line from either bail → <c>click-is-silent</c>; drop the `_countdownPanel.Attach`
    /// or `_countdownPanel?.Sync()` line from `MultiplayerUI` → <c>POSITIVE CONTROL</c>; rename a member →
    /// <c>premise-changed</c>.
    /// </summary>
    internal static class L198_TheCancelButtonCanActuallyBePressed
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var panel = typeof(CountdownPanel);
            var mod = panel.Assembly;
            var ui = mod.GetType("Multiplayer.UI.MultiplayerUI");

            var attach = panel.GetMethod("Attach", All);
            var ensure = panel.GetMethod("EnsureRaycaster", All);
            var skin = panel.GetMethod("SkinButton", All);
            var clicked = panel.GetMethod("OnCancelClicked", All);
            var sync = panel.GetMethod("Sync", All);
            var request = typeof(DeployCountdown).GetMethod("RequestCancel", All);
            var cancel = typeof(DeployCountdown).GetMethod("Cancel", All);
            var awake = ui?.GetMethod("Awake", All);
            var update = ui?.GetMethod("Update", All);
            var lobbyType = mod.GetType("Multiplayer.Network.LobbyCountdown");
            var lobbyRequest = lobbyType?.GetMethod("RequestCancel", All);
            var lobbyCancel = lobbyType?.GetMethod("Cancel", All);
            var lobbyPanel = mod.GetType("Multiplayer.UI.LobbyPanel");
            var refreshControls = lobbyPanel?.GetMethod("RefreshControls", All);
            var readyLabel = lobbyPanel?.GetMethod("UpdateReadyButtonLabel", All);

            if (attach == null || ensure == null || skin == null || clicked == null || sync == null ||
                request == null || cancel == null || awake == null || update == null ||
                lobbyRequest == null || lobbyCancel == null ||
                refreshControls == null || readyLabel == null)
            {
                yield return "L198 premise-changed: one of CountdownPanel.{Attach,EnsureRaycaster,SkinButton," +
                             "OnCancelClicked,Sync}, DeployCountdown.{RequestCancel,Cancel}, " +
                             "LobbyCountdown.{RequestCancel,Cancel}, " +
                             "LobbyPanel.{RefreshControls,UpdateReadyButtonLabel} or MultiplayerUI.{Awake,Update} no longer " +
                             "resolves. This law is the only thing asserting that the veto L177 protects can " +
                             "actually be PRESSED, and a vacuous pass here reads exactly like the three empty " +
                             "logs that started it.";
                yield break;
            }

            // ── (a) the canvas can answer a pointer at all ─────────────────────────────────────────
            if (!Calls(attach, ensure, mod))
                yield return "L198 canvas-eats-every-click: CountdownPanel.Attach does not call " +
                             "EnsureRaycaster. Unity's EventSystem can only hit a Graphic on a canvas that " +
                             "carries a GraphicRaycaster, and the mod's overlay canvas is built with Canvas + " +
                             "CanvasScaler alone — so without this call every widget on it is decorative and " +
                             "the CANCEL press reaches no handler on any peer, silently. That is the reported " +
                             "bug verbatim.";

            var named = GenericArgs(ensure, mod).ToList();
            foreach (var want in new[] { "AddComponent", "GetComponent" })
                if (!named.Any(g => g.method == want && g.arg == typeof(GraphicRaycaster)))
                    yield return "L198 canvas-eats-every-click: EnsureRaycaster never names GraphicRaycaster " +
                                 "on a " + want + ". It must ASK (so the call is idempotent — Attach runs once " +
                                 "per session but the canvas is persistent) and it must ADD, or the canvas " +
                                 "still cannot answer a pointer. The game builds all three components together: " +
                                 "SceneSingleton.cs:81-83.";

            // ── (b) the button names its own clickable face ────────────────────────────────────────
            if (!Calls(attach, skin, mod))
                yield return "L198 button-has-no-face: CountdownPanel.Attach does not call SkinButton, so the " +
                             "runtime-built Button keeps the null targetGraphic Unity leaves on it outside the " +
                             "editor — no hover, no pressed state, nothing to tell a player the button is live.";

            if (!Program.Callees(skin, typeof(Selectable).Assembly).Any(c => c.Name == "set_targetGraphic"))
                yield return "L198 button-has-no-face: SkinButton never assigns Selectable.targetGraphic. That " +
                             "is the field Unity's colour transition drives and the one the button resolves a " +
                             "press against; L182 had to repair the identical omission on the cloned End Turn " +
                             "button (TacticalReadySync.cs:522) and the game's own from-code button sets it " +
                             "beside the Image (SnapshotTextPooling.cs:145-148).";

            // ── (c) nothing on this path may bail without saying so ────────────────────────────────
            var wired = Program.Callees(attach, mod).ToList();
            if (!wired.Any(c => c.MetadataToken == clicked.MetadataToken && c.Module == clicked.Module))
                yield return "L198 click-is-silent: the cancel button is not wired to " +
                             "CountdownPanel.OnCancelClicked. Wiring it straight to DeployCountdown." +
                             "RequestCancel is what made the dead click unknowable: the first thing on the path " +
                             "was a method that can return without a word, so 'the click did nothing' and 'the " +
                             "click never arrived' produced the same evidence — none.";

            // ONE BUTTON, TWO COUNTDOWNS (2026-08-10). The lobby's own five-second start countdown reuses
            // this exact plate and this exact button, so the dispatch in OnCancelClicked is now part of
            // "the press reaches a handler": wired to only one of them, the other countdown draws a CANCEL
            // that is a picture of a button again — which is this law's whole subject. The lobby veto also
            // carries more weight than the deployment one (it clears the canceller's own READY, without
            // which the gate re-arms the countdown on the next frame), so a swallowed press there does not
            // merely fail to stop the clock, it lets the session start.
            if (!Program.Callees(clicked, mod).Any(c => c.MetadataToken == lobbyRequest.MetadataToken &&
                                                        c.Module == lobbyRequest.Module))
                yield return "L198 click-is-silent: CountdownPanel.OnCancelClicked never reaches " +
                             "LobbyCountdown.RequestCancel. The same panel draws the LOBBY start countdown " +
                             "(pre-session, its own 0x49/0x4A ids because the sync rail is not live yet), and a " +
                             "cancel button wired only to the deployment veto is inert on every lobby " +
                             "countdown there will ever be — the dead click of 2026-08-08 in a new place.";

            foreach (var m in new[] { clicked, request, cancel, lobbyRequest, lobbyCancel })
                if (!Logs(m))
                    yield return "L198 click-is-silent: " + m.DeclaringType.Name + "." + m.Name + " contains no " +
                                 "logging call. Every step of the veto — the press, the send, and the host's " +
                                 "decision including the case where it lands on no running countdown — must " +
                                 "leave a line. This repo's dominant bug class is the silent swallow, and this " +
                                 "path has already cost one live session to it.";

            // ── (e) the OTHER button a player must find: READY is never painted into the background ──
            foreach (var dead in new[] { "_readyButtonSetInteractable", "_readyButtonCtrl", "_readyInteractableCache" })
                if (lobbyPanel.GetField(dead, All) != null)
                    yield return "L198 ready-button-greyed-away: LobbyPanel carries " + dead + " again, i.e. the " +
                                 "READY button's native disabled repaint is back. PhoenixGeneralButton." +
                                 "SetInteractable(false) on a CLONE paints the main-menu prefab's " +
                                 "DisabledIdleColor onto the button's Image AND its label Text " +
                                 "(UIInteractableColorController.cs:41-110) — on the lobby's dark card that is a " +
                                 "dark shape holding dark text, which three peers twice reported as 'the READY " +
                                 "button is gone'. The lock is UX only (the host refuses both cases anyway): " +
                                 "decline the press in OnReadyPressed and let the label name the reason.";

            var labels = lobbyPanel.GetField("_readyButtonLabels", All);
            if (labels == null || labels.FieldType != typeof(Text[]))
                yield return "L198 ready-button-greyed-away: LobbyPanel._readyButtonLabels is not a Text[]. " +
                             "NativeWidgetFactory.CloneMenuButton relabels EVERY Text in the clone's subtree " +
                             "because the native TemplateMenuButton carries a second one; holding a single Text " +
                             "means the sibling keeps drawing the clone-time \"READY\" on top of \"NO SAVE\" in " +
                             "the same 160-px cell, which is the other half of an unreadable locked button.";

            var pressed = lobbyPanel.GetMethod("OnReadyPressed", All);
            if (pressed == null)
                yield return "L198 ready-button-greyed-away: LobbyPanel.OnReadyPressed is gone. It is the single " +
                             "handler shared by the cloned native button and the from-code fallback, and the only " +
                             "place a locked READY is declined now that nothing greys the button. Two inline " +
                             "copies is two places to forget the lock; no copy at all is a lock that does nothing.";

            if (!Calls(refreshControls, readyLabel, mod))
                yield return "L198 ready-button-greyed-away: LobbyPanel.RefreshControls no longer calls " +
                             "UpdateReadyButtonLabel. With the grey deliberately gone, that label IS the entire " +
                             "signal that READY is locked and why — MultiplayerUI.Update:1967 -> Refresh -> " +
                             "RefreshControls is the per-frame repaint seam that keeps it true on an already-open " +
                             "lobby when the host's save choice or a parity verdict lands.";

            // ── (d) POSITIVE CONTROL: the panel is built and repainted ─────────────────────────────
            if (!Calls(awake, attach, mod))
                yield return "L198 POSITIVE CONTROL: MultiplayerUI.Awake never calls CountdownPanel.Attach, so " +
                             "every arm above is a rule about a panel that is never built. No overlay, no " +
                             "cancel button, and the drop happens with no warning at all.";
            if (!Calls(update, sync, mod))
                yield return "L198 POSITIVE CONTROL: MultiplayerUI.Update never calls CountdownPanel.Sync. Sync " +
                             "re-reading the mirrored M#deploy root every frame IS the reactivity (postulate 1) " +
                             "— it is why an arming countdown appears on an ALREADY-OPEN screen and why one " +
                             "peer's cancel takes the overlay away on every other peer within a frame of the " +
                             "rail batch, with no edge to raise and none to forget.";
        }

        private static bool Calls(MethodBase from, MethodBase to, Assembly asm) =>
            from != null && to != null &&
            Program.Callees(from, asm).Any(c => c.MetadataToken == to.MetadataToken && c.Module == to.Module);

        /// <summary>Logging by SHAPE, not by one spelling: anything on UnityEngine.Debug whose name starts
        /// with "Log". A bail that logs a warning is as good as one that logs info; a bail that says nothing
        /// is the defect.</summary>
        private static bool Logs(MethodBase m) =>
            Program.CalleeSequence(m)
                   .Any(c => c.DeclaringType == typeof(Multiplayer.MpLog) && c.Name.StartsWith("Log", StringComparison.Ordinal));

        /// <summary>Every generic method call in <paramref name="m"/>, as (method name, single type arg) —
        /// the only way to see which component `AddComponent&lt;T&gt;()` actually asks for.</summary>
        private static IEnumerable<(string method, Type arg)> GenericArgs(MethodBase m, Assembly asm)
        {
            foreach (var c in Program.Callees(m, asm))
            {
                Type[] args = null;
                try { args = c.IsGenericMethod ? c.GetGenericArguments() : null; } catch { }
                if (args != null && args.Length == 1) yield return (c.Name, args[0]);
            }
            foreach (var c in Program.Callees(m, typeof(UnityEngine.GameObject).Assembly))
            {
                Type[] args = null;
                try { args = c.IsGenericMethod ? c.GetGenericArguments() : null; } catch { }
                if (args != null && args.Length == 1) yield return (c.Name, args[0]);
            }
        }
    }
}
