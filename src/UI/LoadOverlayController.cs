using System.Collections.Generic;
using Base.Core;
using Base.Levels;
using Multipleer.Network;
using Multipleer.Network.MessageLayer;
using UnityEngine;
using UnityEngine.UI;

namespace Multipleer.UI
{
    /// <summary>
    /// Co-op loading overlay: a mod-owned ScreenSpaceOverlay canvas (sortingOrder 7000) drawn over
    /// the vanilla curtain. One row per slot (name + single bar + phase label + % text). Drives this
    /// peer's phase-2 native-load read and reports it to the host each frame (throttled).
    /// </summary>
    public sealed class LoadOverlayController : MonoBehaviour
    {
        private Canvas _canvas;
        private Transform _root;
        private bool _visible;

        private sealed class Row
        {
            public Text Name;
            public Image Fill;
            public Text Label;   // "Downloading" / "Loading"
            public Text Percent;
        }
        private readonly Dictionary<byte, Row> _rows = new Dictionary<byte, Row>();

        private int _lastReportedLoadPct = -1;

        private void EnsureCanvas()
        {
            if (_canvas != null) return;
            var go = new GameObject("MultipleerLoadOverlay");
            go.transform.SetParent(transform, false); // under ModGO (persistent root)

            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.overrideSorting = true;
            _canvas.sortingOrder = 7000; // above the native curtain (confirm in-game, open item #1)

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            // Display-only: no GraphicRaycaster.

            var panel = new GameObject("Panel");
            panel.transform.SetParent(go.transform, false);
            var prt = panel.AddComponent<RectTransform>();
            prt.anchorMin = new Vector2(1f, 1f); // top-right
            prt.anchorMax = new Vector2(1f, 1f);
            prt.pivot = new Vector2(1f, 1f);
            prt.anchoredPosition = new Vector2(-24f, -24f);
            prt.sizeDelta = new Vector2(460f, 270f); // ~quarter screen
            var vlg = panel.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 6f;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            var bg = panel.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.55f);

            _root = panel.transform;
        }

        private Row BuildRow(byte slot, string name)
        {
            var go = new GameObject("Row" + slot);
            go.transform.SetParent(_root, false);
            go.AddComponent<RectTransform>().sizeDelta = new Vector2(0f, 44f);
            go.AddComponent<LayoutElement>().minHeight = 44f;

            var nameGo = new GameObject("Name");
            nameGo.transform.SetParent(go.transform, false);
            var nameTxt = nameGo.AddComponent<Text>();
            nameTxt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            nameTxt.fontSize = 18;
            nameTxt.color = Color.white;
            nameTxt.text = name;
            var nrt = nameTxt.rectTransform;
            nrt.anchorMin = new Vector2(0f, 0.5f); nrt.anchorMax = new Vector2(1f, 1f);
            nrt.offsetMin = new Vector2(6f, 0f); nrt.offsetMax = new Vector2(-6f, 0f);

            var barBg = new GameObject("BarBg");
            barBg.transform.SetParent(go.transform, false);
            var barBgImg = barBg.AddComponent<Image>();
            barBgImg.color = new Color(1f, 1f, 1f, 0.15f);
            var brt = barBgImg.rectTransform;
            brt.anchorMin = new Vector2(0f, 0f); brt.anchorMax = new Vector2(1f, 0.5f);
            brt.offsetMin = new Vector2(6f, 4f); brt.offsetMax = new Vector2(-6f, -2f);

            var fillGo = new GameObject("Fill");
            fillGo.transform.SetParent(barBg.transform, false);
            var fill = fillGo.AddComponent<Image>();
            fill.color = new Color(0.3f, 0.8f, 1f, 0.9f);
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = (int)Image.OriginHorizontal.Left;
            fill.fillAmount = 0f;
            var frt = fill.rectTransform;
            frt.anchorMin = Vector2.zero; frt.anchorMax = Vector2.one;
            frt.offsetMin = Vector2.zero; frt.offsetMax = Vector2.zero;

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(barBg.transform, false);
            var label = labelGo.AddComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            label.fontSize = 14;
            label.alignment = TextAnchor.MiddleLeft;
            label.color = Color.white;
            var lrt = label.rectTransform;
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(6f, 0f); lrt.offsetMax = Vector2.zero;

            var pctGo = new GameObject("Pct");
            pctGo.transform.SetParent(barBg.transform, false);
            var pct = pctGo.AddComponent<Text>();
            pct.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            pct.fontSize = 14;
            pct.alignment = TextAnchor.MiddleRight;
            pct.color = Color.white;
            var prt2 = pct.rectTransform;
            prt2.anchorMin = Vector2.zero; prt2.anchorMax = Vector2.one;
            prt2.offsetMin = Vector2.zero; prt2.offsetMax = new Vector2(-6f, 0f);

            return new Row { Name = nameTxt, Fill = fill, Label = label, Percent = pct };
        }

        public void Show()
        {
            EnsureCanvas();
            _canvas.gameObject.SetActive(true);
            _visible = true;
        }

        public void Hide()
        {
            if (_canvas != null) _canvas.gameObject.SetActive(false);
            _visible = false;
        }

        private void Update()
        {
            if (!_visible) return;
            var engine = NetworkEngine.Instance;
            if (engine == null || engine.SaveTransfer == null || engine.Session == null) return;

            // DRIVE phase-2: read native progress and report this peer's load percent (throttled).
            var level = GameUtl.CurrentLevel();
            var lp = level != null ? level.LoadingProgress : null;
            if (lp != null)
            {
                var pct = RosterProgressTracker.ProgressByte(lp.Progress);
                if (pct != _lastReportedLoadPct)
                {
                    _lastReportedLoadPct = pct;
                    engine.SaveTransfer.ReportLoadProgress(pct);
                }
            }
            else if (_lastReportedLoadPct >= 0)
            {
                // Native load finished (LoadingProgress went null) → event-driven done.
                _lastReportedLoadPct = -1;
                engine.SaveTransfer.SendLoadComplete();
            }

            Refresh(engine);
        }

        private void Refresh(NetworkEngine engine)
        {
            var tracker = engine.SaveTransfer.Tracker;
            var localSlot = engine.Session.LocalSlotIndex;
            foreach (var p in engine.Session.GetLobbyRoster())
            {
                // Show only OTHER players: hide this instance's own row (each instance hides only
                // ITS OWN slot, so host and clients stay symmetric). No local row is ever built.
                if (p.SlotIndex == localSlot) continue;

                if (!_rows.TryGetValue(p.SlotIndex, out var row))
                {
                    EnsureCanvas();
                    row = BuildRow(p.SlotIndex, p.Nickname);
                    _rows[p.SlotIndex] = row;
                }
                row.Name.text = p.Nickname;
                var (phase, percent) = tracker.Get(p.SlotIndex);
                row.Fill.fillAmount = percent / 100f;
                row.Label.text = phase == 0 ? "Downloading" : "Loading";
                row.Percent.text = percent + "%";
            }
        }
    }
}
