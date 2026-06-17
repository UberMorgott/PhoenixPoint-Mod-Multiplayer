using System;
using System.Collections.Generic;
using System.Linq;
using Base.Core;
using Base.Serialization;
using PhoenixPoint.Common.Game;
using PhoenixPoint.Common.Saves;
using UnityEngine;
using UnityEngine.UI;

namespace Multipleer.UI
{
    /// <summary>
    /// Minimal mod-drawn uGUI save picker over PhoenixSaveManager.GetSaves() (decompile:
    /// PhoenixSaveManager.cs:279 → returns _allSaves.Values as PPSavegameMetaData). Opened from the
    /// lobby Play button (host only). The selected SavegameMetaData feeds
    /// SaveTransferCoordinator.HostStartSession(chosen) directly (it already calls
    /// SerializationComponent.ReadSavegameBinary(SavegameMetaData, ...)).
    ///
    /// Reuses the native LoadGameModule's underlying collection (GetSaves) rather than re-hosting the
    /// heavyweight prefab module (see flow-reconciliation-plan §2/§3).
    ///
    /// LAYOUT — mirrors LobbyPanel's responsive discipline exactly (docs/plans/responsive-layout-
    /// refactor.md): an OWN ROOT canvas parented under the mod's ModGO (NOT the menu canvas) so the
    /// CanvasScaler is honoured and overrideSorting wins; a full-screen backdrop for a true modal;
    /// nested LayoutGroups + LayoutElement + ContentSizeFitter so lightweight mod-drawn save rows
    /// (name + date built from metadata, NOT clones of the heavyweight native save-slot prefab) get
    /// a uniform LayoutElement height, stacked by a VerticalLayoutGroup in a clipped scroll viewport.
    /// </summary>
    public class SavePickerPanel
    {
        private GameObject _canvasGo;     // SINGLE visibility lever (active = picker shown)
        private CanvasScaler _scaler;     // kept so the aspect-adaptive match can be re-applied live
        private GameObject _panel;        // centered modal panel
        private RectTransform _content;   // scroll content (rows parented here)
        private readonly List<GameObject> _rows = new List<GameObject>();
        private Action<SavegameMetaData> _onPick;

        // Cap displayed rows; most-recent first. Sizes are theme-scaled so the picker grows with UiScale.
        private const int MaxRows = 10;
        private static float RowHeight => LobbyTheme.ScaledSaveRowHeight;     // native-style row (preview + 3 text lines)
        private static float PreviewWidth => LobbyTheme.Scale(140);           // fixed preview box width
        private static float PreviewHeight => LobbyTheme.Scale(72);           // fixed preview box height (16:9-ish, < RowHeight)
        private static int ClonedButtonFontCap => LobbyTheme.ScaledClonedButtonFontCap;

        public bool IsVisible => _canvasGo != null && _canvasGo.activeSelf;

        public void Build(Transform owner)
        {
            if (_canvasGo != null || owner == null) return;

            // OWN ROOT canvas under the mod's ModGO transform (NOT the menu canvas). A canvas parented
            // under ANOTHER canvas is NESTED → Unity ignores its CanvasScaler and won't drive its
            // RectTransform to the screen rect (the lobby's documented cramping/scaler bug). ModGO is a
            // plain GameObject (no Canvas ancestor), so this is a ROOT canvas: scaler honoured, overlay
            // fills the screen, overrideSorting decisively wins. Mirrors MultipleerBarCanvas /
            // MultipleerLobbyCanvas exactly.
            _canvasGo = new GameObject("MultipleerSavePickerCanvas");
            // Hide from the very first frame — the canvas is the SINGLE visibility lever; while
            // inactive its whole subtree is hidden even if a Build sub-step throws (swallowed by the
            // OnMenuReady try/catch), so a half-built picker can never leak onto the menu.
            _canvasGo.SetActive(false);
            _canvasGo.transform.SetParent(owner, false);

            var canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // overrideSorting on a root overlay canvas isn't strictly required, but keep it explicit so
            // the picker is unambiguously ABOVE the lobby overlay (4000) AND the in-geoscape status bar
            // (MultiplayerUI._inGameBar, 5000).
            canvas.overrideSorting = true;
            canvas.sortingOrder = 6000;

            // Responsive: native aspect-adaptive scaler (match WIDTH ≤16:9 / HEIGHT >16:9) replaces the
            // old fixed match=0.5 so the picker scales cleanly across aspect ratios. Re-applied live in
            // Show()/Populate via RefreshScaler (resolution can change at runtime).
            _scaler = _canvasGo.AddComponent<CanvasScaler>();
            LobbyTheme.ConfigureScaler(_scaler);

            // Own GraphicRaycaster so the backdrop + CANCEL + save rows receive clicks above the
            // lobby's raycaster underneath.
            _canvasGo.AddComponent<GraphicRaycaster>();

            var canvasRect = _canvasGo.GetComponent<RectTransform>();
            if (canvasRect != null)
            {
                canvasRect.anchorMin = Vector2.zero;
                canvasRect.anchorMax = Vector2.one;
                canvasRect.pivot = new Vector2(0.5f, 0.5f);
                canvasRect.offsetMin = Vector2.zero;
                canvasRect.offsetMax = Vector2.zero;
                canvasRect.localScale = Vector3.one;
            }

            try
            {
                // Full-screen semi-transparent backdrop (below the panel, above the lobby) so the
                // picker reads as a true modal and swallows stray clicks. raycastTarget defaults true
                // on Image → clicks on the dim area are absorbed, not passed through to the lobby.
                var backdrop = new GameObject("Backdrop");
                backdrop.transform.SetParent(_canvasGo.transform, false);
                var brt = backdrop.AddComponent<RectTransform>();
                brt.anchorMin = Vector2.zero;
                brt.anchorMax = Vector2.one;
                brt.pivot = new Vector2(0.5f, 0.5f);
                brt.offsetMin = Vector2.zero;
                brt.offsetMax = Vector2.zero;
                var bimg = backdrop.AddComponent<Image>();
                bimg.color = new Color(0f, 0f, 0f, 0.6f);

                // Centered modal panel — fixed sensible size, laid out by a VerticalLayoutGroup
                // (Title / scroll host / Cancel). NOT a native full-screen size.
                _panel = new GameObject("PickerPanel");
                _panel.transform.SetParent(_canvasGo.transform, false);
                var prt = _panel.AddComponent<RectTransform>();
                prt.anchorMin = new Vector2(0.5f, 0.5f);
                prt.anchorMax = new Vector2(0.5f, 0.5f);
                prt.pivot = new Vector2(0.5f, 0.5f);
                // Modal panel size scales with UiScale so the bigger rows/fonts fit comfortably.
                prt.sizeDelta = new Vector2(LobbyTheme.Scale(700), LobbyTheme.Scale(600));
                prt.anchoredPosition = Vector2.zero;
                prt.localScale = Vector3.one;

                // Themed modal frame: native sliced sprite (or PanelFill flat) + themed border Outline.
                var pimg = _panel.AddComponent<Image>();
                var poutline = _panel.AddComponent<Outline>();
                LobbyTheme.ApplyPanelSkin(pimg, poutline, LobbyTheme.PanelFill, LobbyTheme.PanelBorder);

                var pad = LobbyTheme.ScaledPadding;
                var pvlg = _panel.AddComponent<VerticalLayoutGroup>();
                pvlg.padding = new RectOffset(pad, pad, pad, pad);
                pvlg.spacing = pad - pad / 4;
                pvlg.childControlWidth = true;
                pvlg.childControlHeight = true;
                pvlg.childForceExpandWidth = true;
                pvlg.childForceExpandHeight = false;
                pvlg.childAlignment = TextAnchor.UpperCenter;

                var titleH = LobbyTheme.ScaledHeaderFontSize + pad;
                var title = UiToolkit.CreateText(_panel, "Title", Vector2.zero,
                    new Vector2(600, titleH), "CHOOSE A SAVE TO HOST", LobbyTheme.ScaledHeaderFontSize,
                    TextAnchor.MiddleCenter, new Vector2(0.5f, 0.5f));
                title.color = LobbyTheme.Accent;
                var tle = LE(title.gameObject); tle.minHeight = titleH; tle.flexibleHeight = 0;

                // Scroll host fills the panel (flexibleHeight 1). Plain RectTransform — NOT a layout
                // group — so the scroll content's ContentSizeFitter is conflict-free (§E-1). A
                // RectMask2D guarantees rows can never overflow the panel, even on the fallback path.
                var scrollHost = new GameObject("ScrollHost");
                scrollHost.transform.SetParent(_panel.transform, false);
                scrollHost.AddComponent<RectTransform>();
                LE(scrollHost).flexibleHeight = 1;
                scrollHost.AddComponent<RectMask2D>();

                _content = NativeWidgetFactory.CloneScroller(scrollHost.transform);
                if (_content != null)
                {
                    // Stretch the native scroller GO (child of scrollHost) to fill the group-sized host.
                    StretchScrollerToHost(_content, scrollHost.transform);
                    ConfigureScrollContent(_content, padding: 4);
                }
                else
                {
                    // Fallback: a plain content rect, top-anchored; rows stacked downward by a VLG and
                    // sized by a ContentSizeFitter (parent is scrollHost, not a layout group → safe).
                    var fb = new GameObject("ContentFallback");
                    fb.transform.SetParent(scrollHost.transform, false);
                    var fbrt = fb.AddComponent<RectTransform>();
                    fbrt.anchorMin = new Vector2(0f, 1f);
                    fbrt.anchorMax = new Vector2(1f, 1f);
                    fbrt.pivot = new Vector2(0.5f, 1f);
                    fbrt.offsetMin = Vector2.zero;
                    fbrt.offsetMax = Vector2.zero;
                    fbrt.anchoredPosition = Vector2.zero;
                    _content = fbrt;
                    ConfigureScrollContent(_content, padding: 4);
                }

                // Cancel button (bottom) — native clone, fallback to from-code button. Full-width bar.
                var cancelH = LobbyTheme.Scale(44);
                var cancel = NativeWidgetFactory.CloneMenuButton(_panel.transform, "CancelBtn", "CANCEL", Hide);
                if (cancel != null)
                {
                    AddCloneCancelLayoutElement(cancel.transform, _panel.transform, cancelH);
                }
                else
                {
                    var btn = UiToolkit.CreateButton(_panel, "CancelBtn", "CANCEL",
                        Vector2.zero, new Vector2(LobbyTheme.Scale(160), cancelH), new Vector2(0.5f, 0.5f), Hide);
                    var cle = LE(btn.gameObject); cle.minHeight = cancelH; cle.preferredHeight = cancelH; cle.flexibleHeight = 0;
                }
            }
            finally
            {
                // Pin the SINGLE visibility lever OFF on every path; _panel stays active inside the
                // inactive canvas so its active-state can never disagree with the lever.
                if (_panel != null) _panel.SetActive(true);
                _canvasGo.SetActive(false);
            }
        }

        public void Show(Action<SavegameMetaData> onPick)
        {
            if (_canvasGo == null) return;
            _onPick = onPick;
            _canvasGo.SetActive(true);
            // Re-apply the aspect-adaptive match for the current resolution before showing.
            if (_scaler != null) _scaler.matchWidthOrHeight = LobbyTheme.CurrentMatch;
            Populate();
        }

        public void Hide()
        {
            if (_canvasGo != null) _canvasGo.SetActive(false);
        }

        private void Populate()
        {
            // Clear any previous rows.
            foreach (var r in _rows)
                UnityEngine.Object.Destroy(r);
            _rows.Clear();

            if (_content == null) return;

            var saves = GetLoadableSaves();
            if (saves.Count == 0)
            {
                var empty = new GameObject("Empty");
                empty.transform.SetParent(_content.transform, false);
                empty.AddComponent<RectTransform>();
                var lbl = UiToolkit.CreateText(empty, "EmptyLabel", Vector2.zero,
                    new Vector2(560, RowHeight), "No saves found.", LobbyTheme.ScaledBodyFontSize,
                    TextAnchor.MiddleCenter, new Vector2(0.5f, 0.5f));
                var ele = LE(empty); ele.minHeight = RowHeight; ele.flexibleWidth = 1;
                _rows.Add(empty);
                return;
            }

            for (var i = 0; i < saves.Count && i < MaxRows; i++)
            {
                var meta = saves[i];
                var captured = meta;   // capture per-iteration → correct save picked on click

                // Native-LOOKING but fully mod-controlled row built from save METADATA — NOT a clone of
                // the heavyweight native UIModuleSaveGameSlot prefab (cloning it produced giant
                // overlapping rows whose native fonts/layout no outer LayoutElement could shrink). One
                // fixed-size row per save (preview box + name/date/difficulty), uniform LayoutElement
                // height; the content VLG stacks them, the RectMask2D clips them.
                // DEFENSIVE: one malformed save must never blank the whole list — wrap each row build so
                // a thrown metadata access (e.g. a corrupt DifficultyDef) is logged and skipped, not
                // propagated up to abort Populate mid-list (the earlier swallow-hides-bug lesson).
                try
                {
                    var row = BuildSaveRow($"SaveRow{i}", meta, () => OnPick(captured));
                    _rows.Add(row);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[Multipleer] SavePicker: skipped save '{SafeName(meta)}' — " +
                                   $"row build threw: {e}");
                }
            }

            // ROOT-CAUSE FIX: rows are NESTED layout groups (content VLG → row HLG → info VLG → text)
            // created at runtime, under a content ContentSizeFitter. On the single deferred auto-rebuild
            // Unity runs, the OUTER content/row width is not yet resolved when the INNER row HLG / info
            // VLG compute, so the row background (driven only by the content VLG's childForceExpandWidth)
            // gets sized and shows as a bar — but the preview box + text rects, which need a SECOND pass
            // against the resolved row width, stay at size 0 and never recompute → the "empty dark bars"
            // seen in-game. Forcing an immediate, synchronous rebuild on the content rect cascades the
            // full layout pass through the nested subtree so the inner children resolve in this frame.
            // (Unity ugui: MarkLayoutForRebuild only defers to end-of-frame; ForceRebuildLayoutImmediate
            // runs it now.)
            ForceRebuildRows();
        }

        // Force the nested row layout (content VLG → row HLG → info VLG → text) to resolve immediately
        // so preview/text rects get their sizes in the same frame instead of staying collapsed at 0.
        private void ForceRebuildRows()
        {
            try
            {
                if (_panel != null && _panel.transform is RectTransform prt)
                    LayoutRebuilder.ForceRebuildLayoutImmediate(prt);
                if (_content != null)
                    LayoutRebuilder.ForceRebuildLayoutImmediate(_content);
            }
            catch (Exception e)
            {
                Debug.LogError("[Multipleer] SavePicker: layout rebuild failed: " + e.Message);
            }
        }

        // Best-effort save name for diagnostics; never throws.
        private static string SafeName(SavegameMetaData meta)
        {
            try { return SaveDisplayName(meta); }
            catch { return "(unknown)"; }
        }

        // Build one native-LOOKING but fully mod-controlled save row from metadata: a clickable
        // fixed-size bar with a left preview box + a right column (name / date / difficulty). No native
        // prefab clone, no oversized fonts, nothing that can overflow the fixed RowHeight.
        private GameObject BuildSaveRow(string name, SavegameMetaData meta, Action onClick)
        {
            // Row root: plain GO under the scroll content; uniform LayoutElement height (the content
            // VLG positions it). A subtle background Image is the raycast target → whole row clicks.
            var row = new GameObject(name);
            row.transform.SetParent(_content.transform, false);
            row.AddComponent<RectTransform>();

            var bg = row.AddComponent<Image>();
            bg.color = LobbyTheme.CardBackground;   // raycastTarget defaults true on Image

            var btn = row.AddComponent<Button>();
            var nav = btn.navigation; nav.mode = Navigation.Mode.None; btn.navigation = nav;
            if (onClick != null)
                btn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onClick()));

            var le = LE(row);
            le.minHeight = RowHeight;
            le.preferredHeight = RowHeight;
            le.flexibleHeight = 0;
            le.flexibleWidth = 1;

            // Horizontal split: fixed-width PREVIEW on the left, flexible text COLUMN on the right.
            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(6, 10, 6, 6);
            hlg.spacing = 10;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;
            hlg.childAlignment = TextAnchor.MiddleLeft;

            BuildPreview(row, meta);
            BuildInfoColumn(row, meta);

            return row;
        }

        // LEFT: a fixed-size preview box. Native UIModuleSaveGameSlot.InitUsedSaveSlot
        // (decompiled UIModuleSaveGameSlot.cs:150-177) shows a Geoscape/Tactical/Error sprite on its
        // SaveIcon (the real per-save screenshot, PPSavegameMetaData.ScreenshotStringData, is nulled by
        // PhoenixSaveManager.ResolveSave:313 + after every write :231, so it is NEVER available at
        // runtime). We attempt to decode that field if a save ever carries it; otherwise we render a
        // neutral placeholder tinted by IsTacticalSave — echoing native's geoscape/tactical distinction.
        // The box is hard-constrained (fixed LayoutElement + preserveAspect + RectMask2D) so it can
        // NEVER overflow the row.
        private static void BuildPreview(GameObject row, SavegameMetaData meta)
        {
            var preview = new GameObject("Preview");
            preview.transform.SetParent(row.transform, false);
            preview.AddComponent<RectTransform>();
            preview.AddComponent<RectMask2D>();   // clip any loaded sprite to the fixed box

            var img = preview.AddComponent<Image>();
            img.preserveAspect = true;            // a real sprite letterboxes inside the box, never spills

            var sprite = TryLoadScreenshotSprite(meta);
            if (sprite != null)
            {
                img.sprite = sprite;
                img.color = Color.white;
            }
            else
            {
                // Neutral placeholder (solid dark box, slight tactical/geoscape tint).
                img.sprite = null;
                img.color = SaveIsTactical(meta)
                    ? new Color(0.22f, 0.10f, 0.10f, 1f)   // tactical → dark red
                    : new Color(0.10f, 0.14f, 0.22f, 1f);  // geoscape → dark blue
            }

            var ple = LE(preview);
            ple.minWidth = PreviewWidth; ple.preferredWidth = PreviewWidth; ple.flexibleWidth = 0;
            ple.minHeight = PreviewHeight; ple.preferredHeight = PreviewHeight; ple.flexibleHeight = 0;
        }

        // RIGHT: a flexible-width vertical column — name (bold) / date / difficulty — matching the
        // native fields (SaveNameField, RealtimeDateField = SaveCreated.DisplayDateTime, DifficultyLoc),
        // kept compact inside the fixed row height.
        private static void BuildInfoColumn(GameObject row, SavegameMetaData meta)
        {
            var col = new GameObject("Info");
            col.transform.SetParent(row.transform, false);
            col.AddComponent<RectTransform>();

            var vlg = col.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(0, 0, 0, 0);
            vlg.spacing = 2;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.MiddleLeft;

            // Theme-scaled line heights (name + date + difficulty stacked in the row).
            var nameH = LobbyTheme.ScaledRowFontSize + 6;
            var subH = LobbyTheme.ScaledSubFontSize + 4;

            // Give the column its OWN explicit min/preferred height so the row HLG (childControlHeight,
            // not force-expand) can never collapse it to 0 even if the inner VLG's reported preferred
            // height fails to propagate on the first pass — it always claims the full text-stack height.
            var colH = nameH + subH * 2;
            var cle = LE(col);
            cle.flexibleWidth = 1; cle.minWidth = 0;
            cle.minHeight = colH; cle.preferredHeight = colH; cle.flexibleHeight = 0;

            // Line 1: display name, bold, single line, truncated. Explicit body color so it can never
            // render dark-on-dark (CreateText already defaults to it; pinned here against future drift).
            var nameText = UiToolkit.CreateText(col, "Name", Vector2.zero,
                new Vector2(380, nameH), SaveDisplayName(meta), LobbyTheme.ScaledRowFontSize,
                TextAnchor.MiddleLeft, new Vector2(0f, 0.5f));
            nameText.color = LobbyTheme.BodyText;
            nameText.fontStyle = FontStyle.Bold;
            nameText.horizontalOverflow = HorizontalWrapMode.Wrap;
            nameText.verticalOverflow = VerticalWrapMode.Truncate;
            var nle = LE(nameText.gameObject); nle.minHeight = nameH; nle.preferredHeight = nameH;
            nle.flexibleWidth = 1; nle.minWidth = 0; nle.flexibleHeight = 0;

            // Line 2: realtime save date (native RealtimeDateField = SaveCreated.DisplayDateTime).
            var dateText = UiToolkit.CreateText(col, "Date", Vector2.zero,
                new Vector2(380, subH), SaveDate(meta), LobbyTheme.ScaledSubFontSize,
                TextAnchor.MiddleLeft, new Vector2(0f, 0.5f));
            dateText.color = LobbyTheme.SubText;
            var dle = LE(dateText.gameObject);
            dle.minHeight = subH; dle.preferredHeight = subH; dle.flexibleHeight = 0; dle.flexibleWidth = 1; dle.minWidth = 0;

            // Line 3: difficulty (native DifficultyLoc = DifficultyDef.Name) — only when present.
            var diff = SaveDifficulty(meta);
            if (!string.IsNullOrEmpty(diff))
            {
                var diffText = UiToolkit.CreateText(col, "Difficulty", Vector2.zero,
                    new Vector2(380, subH), diff, LobbyTheme.ScaledSubFontSize,
                    TextAnchor.MiddleLeft, new Vector2(0f, 0.5f));
                diffText.color = LobbyTheme.SubText;
                var fle = LE(diffText.gameObject);
                fle.minHeight = subH; fle.preferredHeight = subH; fle.flexibleHeight = 0; fle.flexibleWidth = 1; fle.minWidth = 0;
            }
        }

        private void OnPick(SavegameMetaData meta)
        {
            Hide();
            _onPick?.Invoke(meta);
        }

        // ─── Layout helpers (mirror LobbyPanel) ─────────────────────────────

        private static LayoutElement LE(GameObject go)
        {
            return go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
        }

        // Walk from a cloned child up to the clone root (the GO parented directly under `container`).
        private static Transform ResolveCloneRoot(Transform start, Transform container)
        {
            if (start == null) return null;
            var t = start;
            while (t.parent != null && t.parent != container)
                t = t.parent;
            return t;
        }

        // Full-width cancel bar: reset prefab scale, fixed height, cap oversized prefab font.
        private static void AddCloneCancelLayoutElement(Transform start, Transform container, float height)
        {
            var root = ResolveCloneRoot(start, container);
            if (root == null) return;
            root.localScale = Vector3.one;

            var le = LE(root.gameObject);
            le.minHeight = height;
            le.preferredHeight = height;
            le.flexibleHeight = 0;
            le.flexibleWidth = 1;

            var label = start.GetComponentInChildren<Text>(true);
            if (label != null && label.fontSize > ClonedButtonFontCap)
                label.fontSize = ClonedButtonFontCap;
        }

        // Stretch the cloned native scroller GO (a child of `host`) to fill the group-sized host.
        private static void StretchScrollerToHost(RectTransform content, Transform host)
        {
            if (content == null || host == null) return;
            Transform t = content.transform;
            while (t.parent != null && t.parent != host)
                t = t.parent;
            var rt = t as RectTransform;
            if (rt == null) return;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;
        }

        // Standard ScrollRect-content pattern: VLG stacks rows, ContentSizeFitter sizes the content.
        // Conflict-free because content's parent is the viewport, NOT a layout group (§E-1 trap).
        private static void ConfigureScrollContent(RectTransform content, int padding)
        {
            if (content == null) return;
            content.pivot = new Vector2(0.5f, 1f);   // growth/scroll downward

            var vlg = content.gameObject.GetComponent<VerticalLayoutGroup>()
                      ?? content.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 4;
            vlg.padding = new RectOffset(padding, padding, padding, padding);
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            var fitter = content.gameObject.GetComponent<ContentSizeFitter>()
                         ?? content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        // ─── Save enumeration ───────────────────────────────────────────────

        // Enumerate loadable saves, most recent first.
        private static List<SavegameMetaData> GetLoadableSaves()
        {
            var result = new List<SavegameMetaData>();
            try
            {
                var game = GameUtl.GameComponent<PhoenixGame>();
                var sm = game?.SaveManager;
                if (sm == null) return result;

                var all = sm.GetSaves();
                if (all == null) return result;

                foreach (var meta in all)
                {
                    // Skip unloadable entries (decompile PPSavegameMetaData.IsLoadable()).
                    if (meta is PPSavegameMetaData pp && !pp.IsLoadable())
                        continue;
                    result.Add(meta);
                }

                // Most recent first by SaveCreated.
                result = result
                    .OrderByDescending(m => m.SaveCreated.dateTime)
                    .ToList();
            }
            catch (Exception e)
            {
                Debug.LogError("[Multipleer] Failed to enumerate saves: " + e.Message);
            }
            return result;
        }

        // Display name: prefer the user-set name (PPSavegameMetaData.UserSetName:20), else the raw
        // SavegameMetaData.Name:15, else a placeholder.
        private static string SaveDisplayName(SavegameMetaData meta)
        {
            var name = meta.Name;
            if (meta is PPSavegameMetaData pp && !string.IsNullOrEmpty(pp.UserSetName))
                name = pp.UserSetName;
            return string.IsNullOrEmpty(name) ? "(unnamed)" : name;
        }

        // Real-world save timestamp: SavegameMetaData.SaveCreated (UnityDateTime:18) → DisplayDateTime
        // ("dd/MM/yyyy - HH:mm", UnityDateTime.cs:24). Defensive: metadata may be partial.
        private static string SaveDate(SavegameMetaData meta)
        {
            try { return meta.SaveCreated.DisplayDateTime ?? ""; }
            catch { return ""; }
        }

        // Difficulty display string: PPSavegameMetaData.DifficultyDef:29 →
        // GameDifficultyLevelDef.Name (LocalizedTextBind, GameDifficultyLevelDef.cs:140) →
        // .Localize() (LocalizedTextBind.cs:35) — same source the native slot feeds DifficultyLoc.SetTerm
        // (UIModuleSaveGameSlot.cs:180). Empty when no difficulty (autosaves/old saves).
        private static string SaveDifficulty(SavegameMetaData meta)
        {
            try
            {
                if (meta is PPSavegameMetaData pp && pp.DifficultyDef != null)
                    return pp.DifficultyDef.Name?.Localize() ?? "";
            }
            catch { /* localization may be unavailable */ }
            return "";
        }

        // PPSavegameMetaData.IsTacticalSave:32 — native picks Tactical vs Geoscape icon from this
        // (UIModuleSaveGameSlot.cs:154). We use it only to tint the placeholder preview box.
        private static bool SaveIsTactical(SavegameMetaData meta)
        {
            return meta is PPSavegameMetaData pp && pp.IsTacticalSave;
        }

        // Best-effort per-save preview. PPSavegameMetaData.ScreenshotStringData:23 is the only screenshot
        // field, but PhoenixSaveManager nulls it on load (ResolveSave:313) AND after every write
        // (PhoenixSaveManager.cs:231), so at runtime it is ALWAYS null → this returns null and the row
        // falls back to the neutral placeholder. The decode (base64 PNG → Texture2D → Sprite) is kept
        // for forward-compatibility if a (modded) save ever carries the data; its exact encoding is
        // unverified in the decompiled subset, hence the broad try/catch.
        private static Sprite TryLoadScreenshotSprite(SavegameMetaData meta)
        {
            try
            {
                if (!(meta is PPSavegameMetaData pp) || string.IsNullOrEmpty(pp.ScreenshotStringData))
                    return null;

                var bytes = Convert.FromBase64String(pp.ScreenshotStringData);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(bytes))   // resizes the texture to the PNG/JPG dimensions
                {
                    UnityEngine.Object.Destroy(tex);
                    return null;
                }
                tex.Apply();
                return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f), 100f);
            }
            catch
            {
                return null;   // unknown encoding / corrupt data → placeholder
            }
        }
    }
}
