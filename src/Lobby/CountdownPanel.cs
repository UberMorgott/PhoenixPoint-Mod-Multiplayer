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
        private static int TitleH => LobbyTheme.Scale(26);
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

            // CENTRED, as asked: the button sits on the panel's own horizontal centre, under the line that
            // says how long is left. It is the ONLY raycastable thing this class builds.
            _cancel = UiToolkit.CreateButton(_root, "Cancel", "CANCEL", new Vector2(0f, Pad),
                                             new Vector2(ButtonW, ButtonH), new Vector2(0.5f, 0f),
                                             DeployCountdown.RequestCancel);

            _root.SetActive(false);
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
