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
    /// </summary>
    public class SavePickerPanel
    {
        private GameObject _root;
        private GameObject _listArea;
        private readonly List<GameObject> _rows = new List<GameObject>();
        private Action<SavegameMetaData> _onPick;

        // Cap displayed rows (no ScrollRect for the minimal first cut); most-recent first.
        private const int MaxRows = 10;
        private const float RowHeight = 36f;
        private const float RowWidth = 540f;

        public bool IsVisible => _root != null && _root.activeSelf;

        public void Build(Canvas menuCanvas)
        {
            if (_root != null || menuCanvas == null) return;

            _root = new GameObject("MultipleerSavePicker");
            // The picker parents directly under the LIVE menu canvas, so it MUST be hidden from the
            // very first frame and guaranteed hidden even if a build sub-step throws (the throw is
            // swallowed by the OnMenuReady try/catch in MainMenuPatches). Set inactive BEFORE parenting
            // under the live canvas, then re-assert it in a finally — the empty GO must never be active
            // under the live menu canvas even for an instant, and a half-built picker can never leak onto the menu.
            _root.SetActive(false);
            _root.transform.SetParent(menuCanvas.transform, false);

            try
            {
                var rect = _root.AddComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(580, 480);
                rect.anchoredPosition = Vector2.zero;

                var bg = _root.AddComponent<Image>();
                bg.color = new Color(0.04f, 0.05f, 0.07f, 0.97f);

                UiToolkit.CreateText(_root, "Title", new Vector2(0, -24),
                    new Vector2(560, 30), "CHOOSE A SAVE TO HOST", 17,
                    TextAnchor.MiddleCenter, new Vector2(0.5f, 1f));

                _listArea = new GameObject("ListArea");
                _listArea.transform.SetParent(_root.transform, false);
                var listRect = _listArea.AddComponent<RectTransform>();
                listRect.anchorMin = new Vector2(0.5f, 1f);
                listRect.anchorMax = new Vector2(0.5f, 1f);
                listRect.pivot = new Vector2(0.5f, 1f);
                listRect.sizeDelta = new Vector2(RowWidth, MaxRows * RowHeight);
                listRect.anchoredPosition = new Vector2(0, -64);

                // Cancel button (bottom-center) — native clone, fallback to from-code button.
                var cancel = NativeWidgetFactory.CloneMenuButton(_root.transform, "CancelBtn", "CANCEL", Hide);
                if (cancel != null)
                {
                    var crt = cancel.transform as RectTransform;
                    if (crt != null)
                    {
                        crt.anchorMin = new Vector2(0.5f, 0f);
                        crt.anchorMax = new Vector2(0.5f, 0f);
                        crt.pivot = new Vector2(0.5f, 0f);
                        crt.anchoredPosition = new Vector2(0, 18);
                    }
                }
                else
                {
                    UiToolkit.CreateButton(_root, "CancelBtn", "CANCEL",
                        new Vector2(0, 18), new Vector2(160, 40), new Vector2(0.5f, 0f),
                        Hide);
                }
            }
            finally
            {
                _root.SetActive(false);
            }
        }

        public void Show(Action<SavegameMetaData> onPick)
        {
            if (_root == null) return;
            _onPick = onPick;
            _root.SetActive(true);
            Populate();
        }

        public void Hide()
        {
            if (_root != null) _root.SetActive(false);
        }

        private void Populate()
        {
            // Clear any previous rows.
            foreach (var r in _rows)
                UnityEngine.Object.Destroy(r);
            _rows.Clear();

            var saves = GetLoadableSaves();
            if (saves.Count == 0)
            {
                var empty = new GameObject("Empty");
                empty.transform.SetParent(_listArea.transform, false);
                empty.AddComponent<RectTransform>();
                UiToolkit.CreateText(empty, "EmptyLabel", new Vector2(0, -RowHeight),
                    new Vector2(RowWidth, RowHeight), "No saves found.", 14,
                    TextAnchor.MiddleCenter, new Vector2(0.5f, 1f));
                _rows.Add(empty);
                return;
            }

            for (var i = 0; i < saves.Count && i < MaxRows; i++)
            {
                var meta = saves[i];
                var captured = meta;

                // Prefer a cloned NATIVE save row (UIModuleSaveGameSlot): native name/date/
                // location/icon visuals. InitUsedSaveSlot re-wires its OnLoad/OnDelete delegates
                // (-= then +=), so the cloned row does NOT carry the native single-player load.
                if (meta is PPSavegameMetaData pp)
                {
                    var row = NativeWidgetFactory.CloneSaveRow(_listArea.transform);
                    if (row != null)
                    {
                        var rt = row.transform as RectTransform;
                        if (rt != null)
                        {
                            rt.anchorMin = new Vector2(0.5f, 1f);
                            rt.anchorMax = new Vector2(0.5f, 1f);
                            rt.pivot = new Vector2(0.5f, 1f);
                            rt.anchoredPosition = new Vector2(0, -i * RowHeight);
                        }
                        // isInSaveWindow:false → shows the Load button; route it to our pick;
                        // overwrite/delete are no-ops; suppress the load confirmation popup.
                        row.InitUsedSaveSlot(pp, false,
                            _ => { }, _ => OnPick(captured), _ => { }, false);
                        _rows.Add(row.gameObject);
                        continue;
                    }
                }

                // Fallback (native save-row prefab not capturable): from-code labeled button.
                var label = DescribeSave(meta);
                var btn = UiToolkit.CreateButton(_listArea, $"SaveRow{i}", label,
                    new Vector2(0, -i * RowHeight), new Vector2(RowWidth, RowHeight - 4),
                    new Vector2(0.5f, 1f), () => OnPick(captured));
                _rows.Add(btn.gameObject);
            }
        }

        private void OnPick(SavegameMetaData meta)
        {
            Hide();
            _onPick?.Invoke(meta);
        }

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

        private static string DescribeSave(SavegameMetaData meta)
        {
            var name = meta.Name;
            if (meta is PPSavegameMetaData pp && !string.IsNullOrEmpty(pp.UserSetName))
                name = pp.UserSetName;
            if (string.IsNullOrEmpty(name)) name = "(unnamed)";

            var when = "";
            try { when = meta.SaveCreated?.DisplayDateTime ?? ""; }
            catch { /* defensive: metadata may be partial */ }

            return string.IsNullOrEmpty(when) ? name : $"{name}     {when}";
        }
    }
}
