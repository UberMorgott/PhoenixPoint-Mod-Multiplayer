using System;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using UnityEngine;
using UnityEngine.UI;

namespace Multiplayer.UI
{
    /// <summary>
    /// THE DROP IS IN N SECONDS, AND THIS BUTTON STOPS IT — for everyone, from any peer.
    ///
    /// A PRESENTATION SEAM (P4c, law L158). It READS <see cref="DeployCountdown.State"/>, the mirrored
    /// <c>"M#deploy"</c> mod root, and writes nothing but its own widgets and one gesture:
    /// <see cref="DeployCountdown.RequestCancel"/>, which is the host's decision on the host and op 2 on
    /// 0xB8 from a client. No rail write, no native call, no <c>[SerializeMember]</c> leaf, and NOTHING
    /// reads this file back — no launch, barrier, turn or readiness predicate may, and none does.
    ///
    /// IT IS NOT A QUORUM AND IT MUST NOT BE READ AS ONE (P13, L84/L91/L145). The number counts down on the
    /// HOST'S clock and reaching zero requires nobody to press anything; this panel is where a peer may
    /// VETO, not where peers agree. A peer who ignores it entirely — or who is not looking at the screen at
    /// all — changes nothing about when the battle starts.
    ///
    /// WHERE IT LIVES, and why not a native window. It hangs on the mod's own persistent overlay canvas
    /// (<c>MultiplayerUI.EnsureBarCanvas</c>, sortingOrder 5000), the same render root
    /// <see cref="PlayerPanel"/> uses, so it draws over WHATEVER is on screen — the geoscape, the research
    /// tree, the deployment screen itself — with no view-state transition anywhere in the path. The game's
    /// own always-on-top surfaces are all queued view states (<c>UIStateGeoModal</c> and friends go through
    /// <c>GeoscapeViewSwitchQuery</c>), and every one of them TAKES the screen and pauses the game — which
    /// is exactly the behaviour the window-history rule was just written to stop, and would make a five
    /// second panel yank three players out of what they were doing. So the panel is drawn from code and
    /// SKINNED with the native theme instead: <see cref="LobbyTheme"/>'s captured colours, the captured
    /// native menu typeface through <see cref="UiToolkit"/>, and the same <c>ApplyPanelSkin</c> plate the
    /// lobby and the player panel already wear.
    ///
    /// IT EATS EXACTLY ONE CLICK. Every Graphic it builds has <c>raycastTarget = false</c> except the cancel
    /// button's own Image — so the map, the research tree and the deployment roster underneath keep every
    /// click that is not on that button. This is the same hazard <c>PlayerPanel</c> and
    /// <c>TacticalReadyButton.TrimRaycast</c> exist for, and here it is load-bearing: the panel is on screen
    /// while a player may still be re-arming a soldier, which is the whole reason it is there.
    ///
    /// REACTIVITY IS BY CONSTRUCTION (postulate 1). <see cref="Sync"/> re-reads the mirrored root every
    /// frame from <c>MultiplayerUI.Update</c> — the idiom <c>PlayerPanel</c> is already in-game confirmed
    /// on — so an arming countdown appears on an ALREADY-OPEN screen within one frame of the rail batch, a
    /// decrement repaints the number, and a cancel takes the panel away. There is no edge to raise and none
    /// to forget.
    /// </summary>
    internal sealed class CountdownPanel : MonoBehaviour
    {
        private static int Pad => LobbyTheme.Scale(10);
        private static int PanelW => LobbyTheme.Scale(260);
        /// <summary>Two wrapped lines' worth, not one. The caption is "MISSION STARTS IN 25 SECONDS" at its
        /// longest and the plate is deliberately narrow; a one-line box forced best-fit down to an
        /// unreadable size instead of letting it wrap. The panel's own height is derived from this, so
        /// nothing else moves.</summary>
        private static int TitleH => LobbyTheme.Scale(44);
        private static int ButtonW => LobbyTheme.Scale(120);
        private static int ButtonH => LobbyTheme.Scale(30);
        private static int PanelH => Pad * 3 + TitleH + ButtonH;

        /// <summary>Same see-through plate the player panel wears: this deliberately overhangs live UI, and
        /// an opaque box would hide the soldier the player is still equipping.</summary>
        private const float FillAlpha = 0.80f;
        private const float BorderAlpha = 0.60f;

        private RectTransform _canvasRect;
        private GameObject _root;
        private RectTransform _rootRect;
        private Text _title;
        private Button _cancel;
        private int _shown = -1;
        private bool _loggedFailure;

        /// <summary>Build the (hidden) panel under the mod's overlay canvas. One call, from
        /// <c>MultiplayerUI.Awake</c>; the canvas is persistent, so this survives every scene load.</summary>
        internal void Attach(Transform barCanvas)
        {
            _canvasRect = barCanvas as RectTransform;
            EnsureRaycaster(barCanvas);

            _root = new GameObject("MultiplayerDeployCountdown");
            _root.transform.SetParent(barCanvas, false);
            _rootRect = _root.AddComponent<RectTransform>();
            _rootRect.anchorMin = _rootRect.anchorMax = new Vector2(0.5f, 1f);
            _rootRect.pivot = new Vector2(0.5f, 1f);
            _rootRect.sizeDelta = new Vector2(PanelW, PanelH);
            // Under the top edge, centred: the one place no native geoscape or tactical HUD element owns.
            _rootRect.anchoredPosition = new Vector2(0f, -LobbyTheme.Scale(60));

            var img = _root.AddComponent<Image>();
            img.raycastTarget = false;                       // the plate itself must not swallow a click
            var outline = _root.AddComponent<Outline>();
            LobbyTheme.ApplyPanelSkin(img, outline, Fade(LobbyTheme.PanelFill, FillAlpha),
                                                   Fade(LobbyTheme.PanelBorder, BorderAlpha));

            _title = UiToolkit.CreateText(_root, "Title", new Vector2(0f, -Pad), new Vector2(PanelW - Pad * 2, TitleH),
                                          "", LobbyTheme.ScaledHeaderFontSize, TextAnchor.MiddleCenter,
                                          new Vector2(0.5f, 1f));
            _title.raycastTarget = false;
            FitInsideRect(_title);

            // CENTRED, as asked: the button sits on the panel's own horizontal centre, under the line that
            // says how long is left. It is the ONLY raycastable thing this class builds.
            _cancel = UiToolkit.CreateButton(_root, "Cancel", "CANCEL", new Vector2(0f, Pad),
                                             new Vector2(ButtonW, ButtonH), new Vector2(0.5f, 0f),
                                             OnCancelClicked);
            SkinButton(_cancel);

            _root.SetActive(false);
        }

        /// <summary>
        /// THE CLICK THAT NEVER ARRIVED (2026-08-08, live, three peers). The owner pressed CANCEL and the
        /// countdown ran to zero — and NOT ONE of the three mod logs carried a single line about it, on any
        /// peer. That is the whole diagnosis: the press never reached a handler, because the mod's overlay
        /// canvas carried a <c>Canvas</c> and a <c>CanvasScaler</c> AND NO <c>GraphicRaycaster</c>
        /// (<c>MultiplayerUI.EnsureBarCanvas</c>). Unity's EventSystem can only hit a Graphic on a canvas
        /// that carries a raycaster, so every widget on this canvas was decorative BY CONSTRUCTION.
        ///
        /// It went unnoticed for as long as it did because nothing on that canvas was ever CLICKABLE before:
        /// the in-game bar and <see cref="PlayerPanel"/> both set <c>raycastTarget = false</c> on every
        /// Graphic they build, deliberately. The countdown's Cancel is the first interactive thing to land
        /// there, and it inherited a canvas that had never had to answer a pointer.
        ///
        /// The game's own from-code canvas is the three components together —
        /// <c>Assembly-CSharp/src/LlockhamIndustries.Misc/SceneSingleton.cs:81-83</c> adds
        /// <c>Canvas</c> + <c>CanvasScaler</c> + <c>GraphicRaycaster</c> in one breath. Added here rather
        /// than in the canvas factory only because that file is another agent's this session; the raycaster
        /// belongs on the canvas and this is idempotent either way.
        /// </summary>
        private void EnsureRaycaster(Transform barCanvas)
        {
            if (barCanvas == null) return;
            if (barCanvas.GetComponent<GraphicRaycaster>() != null) return;
            barCanvas.gameObject.AddComponent<GraphicRaycaster>();
            Debug.Log("[Multiplayer] deployment-countdown panel added the missing GraphicRaycaster to the mod " +
                      "overlay canvas — without it no click on this canvas reaches any handler at all, which " +
                      "is why the CANCEL press produced no log line on any peer.");
        }

        /// <summary>The one place a cancel press is recorded, BEFORE any decision can drop it. The dead
        /// click above was unknowable precisely because the path started at a silent method; from here on a
        /// press that changes nothing still leaves a line saying it happened.</summary>
        private static void OnCancelClicked()
        {
            Debug.Log("[MP][deploy] CANCEL pressed on this peer at " +
                      (string.IsNullOrEmpty(DeployCountdown.State.SiteRef) ? "S#?" : DeployCountdown.State.SiteRef) +
                      " with " + DeployCountdown.State.SecondsLeft + " s left — one peer's veto stops the drop " +
                      "for everyone (no vote, nobody else has to agree).");
            DeployCountdown.RequestCancel();
        }

        /// <summary>
        /// THE LABEL RAN OUTSIDE THE PLATE (same live report). <see cref="UiToolkit.CreateText"/> leaves
        /// <c>horizontalOverflow = Overflow</c>, so "MISSION STARTS IN 5 SECONDS" at header size simply drew
        /// past both edges of a 260-wide panel.
        ///
        /// Fixed with the auto-size component uGUI already has, and with the SAME settings the mod already
        /// puts on the NATIVE End Turn button's own caption (<c>TacticalReadySync.FitLabel</c>:625-635) —
        /// which is the owner's reference for a correct button. No hardcoded font size and no magic offset:
        /// the frame is fixed and the text shrinks and wraps inside it, never the other way round.
        /// </summary>
        private static void FitInsideRect(Text t)
        {
            if (t == null) return;
            t.resizeTextForBestFit = true;
            t.resizeTextMaxSize = t.fontSize > 0 ? t.fontSize : 24;
            t.resizeTextMinSize = 8;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Truncate;
        }

        /// <summary>
        /// Give the button the hover feedback a native one has. A <c>Button</c> added at RUNTIME has a NULL
        /// <c>targetGraphic</c> — Unity only fills that field from the editor's <c>Reset</c> — and that is
        /// the field the colour transition drives, so the button reacted to nothing. Exactly the defect
        /// L182 already had to repair on the cloned End Turn button
        /// (<c>TacticalReadySync.cs:522</c>, "the button had no hover feedback either"), and the game's own
        /// from-code button assigns it in the same breath as the Image:
        /// <c>Assembly-CSharp/src/UnityTools.UI.SnapshotText.Lib/SnapshotTextPooling.cs:145-148</c>.
        ///
        /// The hover colour is the theme's captured NATIVE accent, not an invented one, so the highlight is
        /// the same amber the rest of the game's UI lights up with.
        /// </summary>
        private static void SkinButton(Button b)
        {
            if (b == null) return;
            var face = b.GetComponent<Image>();
            if (face == null) return;
            face.raycastTarget = true;          // the ONE click surface this class keeps
            b.targetGraphic = face;
            var c = b.colors;
            c.normalColor = Color.white;        // ColorTint multiplies the skinned plate; white = as skinned
            c.highlightedColor = LobbyTheme.Accent;
            var a = LobbyTheme.Accent;
            c.pressedColor = new Color(a.r * 0.7f, a.g * 0.7f, a.b * 0.7f, 1f);
            c.selectedColor = Color.white;
            c.fadeDuration = 0.1f;
            b.colors = c;

            foreach (var label in b.GetComponentsInChildren<Text>(true)) FitInsideRect(label);
        }

        /// <summary>The repaint, and the containment point L158 names: one try/catch, because this is called
        /// from <c>MultiplayerUI.Update</c> and a throw here would unwind into the game's own loop and take
        /// the session with it. Failure hides the panel and logs once — the countdown itself is the HOST'S
        /// and completes regardless, so a broken panel costs a peer his veto, never his battle.</summary>
        internal void Sync()
        {
            try { SyncCore(); }
            catch (Exception e)
            {
                if (_root != null) _root.SetActive(false);
                if (_loggedFailure) return;
                _loggedFailure = true;
                Debug.LogError("[Multiplayer] deployment-countdown panel FAILED and is hidden for the rest of " +
                               "this run. The drop still happens on the host's own clock; what is lost is this " +
                               "peer's cancel button: " + e);
            }
        }

        private void SyncCore()
        {
            if (_root == null) return;

            var engine = NetworkEngine.Instance;
            int left = DeployCountdown.State.SecondsLeft;
            bool show = engine != null && engine.IsActiveSession && left > 0;

            if (!show)
            {
                if (_root.activeSelf) _root.SetActive(false);
                _shown = -1;
                return;
            }
            if (!_root.activeSelf) _root.SetActive(true);
            if (left == _shown) return;                      // per-frame work is one int compare
            _shown = left;

            _title.text = "MISSION STARTS IN " + left + (left == 1 ? " SECOND" : " SECONDS");
            _title.color = LobbyTheme.BodyText;
        }

        private static Color Fade(Color c, float a) => new Color(c.r, c.g, c.b, a);
    }
}
