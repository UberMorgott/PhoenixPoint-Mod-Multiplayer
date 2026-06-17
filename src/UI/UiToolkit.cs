using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Multipleer.UI
{
    /// <summary>
    /// Tiny runtime-uGUI construction helpers shared by the lobby panel and save picker.
    /// Mirrors the from-code UI pattern already used by MultiplayerUI.CreateInGameBar/CreateText.
    /// </summary>
    internal static class UiToolkit
    {
        private static Font _font;

        // Built-in Arial fallback (only used before the native menu font is captured / if capture fails).
        private static Font FallbackFont
        {
            get
            {
                if (_font == null)
                    _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                return _font;
            }
        }

        /// <summary>
        /// Font for all from-code labels: the captured native main-menu uGUI Font when available
        /// (so lobby text matches the game's menu typeface — user request), else built-in Arial.
        /// Resolved per call so labels created before menu capture still pick up the menu font on
        /// the next text they create.
        /// </summary>
        public static Font DefaultFont => NativeWidgetFactory.MenuFont ?? FallbackFont;

        // Anchored text. anchor = anchorMin/Max pivot point inside the parent (e.g. (0.5,1) = top-center).
        // fontSize is the EFFECTIVE on-screen px (callers pass LobbyTheme.Scaled* values). A negative /
        // zero size falls back to the theme body size so a forgotten size still scales with the skin.
        public static Text CreateText(GameObject parent, string name, Vector2 pos, Vector2 size,
            string content, int fontSize = 0, TextAnchor align = TextAnchor.MiddleLeft,
            Vector2? anchor = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            var rect = go.AddComponent<RectTransform>();
            var a = anchor ?? new Vector2(0f, 1f);
            rect.anchorMin = a;
            rect.anchorMax = a;
            rect.pivot = a;
            rect.sizeDelta = size;
            rect.anchoredPosition = pos;

            var text = go.AddComponent<Text>();
            text.font = LobbyTheme.Font;
            text.fontSize = fontSize > 0 ? fontSize : LobbyTheme.ScaledBodyFontSize;
            text.color = LobbyTheme.BodyText;
            text.alignment = align;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.text = content;
            return text;
        }

        public static Button CreateButton(GameObject parent, string name, string label,
            Vector2 pos, Vector2 size, Vector2 anchor, Action onClick)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = anchor;
            rect.sizeDelta = size;
            rect.anchoredPosition = pos;

            var img = go.AddComponent<Image>();
            img.color = LobbyTheme.CardBackground;

            var btn = go.AddComponent<Button>();
            var nav = btn.navigation;
            nav.mode = Navigation.Mode.None;
            btn.navigation = nav;
            if (onClick != null)
                btn.onClick.AddListener((UnityAction)(() => onClick()));

            var txt = CreateText(go, "Label", Vector2.zero, size, label, LobbyTheme.ScaledButtonFontSize,
                TextAnchor.MiddleCenter, new Vector2(0.5f, 0.5f));
            txt.color = LobbyTheme.BodyText;

            return btn;
        }

        // Minimal uGUI InputField built from code (background Image + Text + placeholder).
        public static InputField CreateInputField(GameObject parent, string name, string initial,
            Vector2 pos, Vector2 size, Vector2 anchor, Action<string> onEndEdit)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = anchor;
            rect.sizeDelta = size;
            rect.anchoredPosition = pos;

            var bg = go.AddComponent<Image>();
            bg.color = LobbyTheme.InputBackground;

            var textComp = CreateText(go, "Text", new Vector2(8, 0),
                new Vector2(size.x - 16, size.y), initial ?? "", LobbyTheme.ScaledRowFontSize,
                TextAnchor.MiddleLeft, new Vector2(0f, 0.5f));
            textComp.supportRichText = false;

            var input = go.AddComponent<InputField>();
            input.textComponent = textComp;
            input.text = initial ?? "";
            input.lineType = InputField.LineType.SingleLine;
            input.characterLimit = 24;
            if (onEndEdit != null)
                input.onEndEdit.AddListener((UnityAction<string>)(v => onEndEdit(v)));

            return input;
        }
    }
}
