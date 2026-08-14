using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Multiplayer.UI
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

        /// <summary>
        /// A bare square image cell (no sprite yet, no raycast) — the avatar cell every roster surface
        /// puts left of the nickname. preserveAspect so a non-square Steam picture is letterboxed rather
        /// than stretched; raycastTarget off because none of the three surfaces wants it to eat a click.
        /// </summary>
        public static Image CreateImage(GameObject parent, string name, Vector2 pos, Vector2 size,
            Vector2? anchor = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            var rect = go.AddComponent<RectTransform>();
            var a = anchor ?? new Vector2(0f, 0.5f);
            rect.anchorMin = a;
            rect.anchorMax = a;
            rect.pivot = a;
            rect.sizeDelta = size;
            rect.anchoredPosition = pos;

            var img = go.AddComponent<Image>();
            img.raycastTarget = false;
            img.preserveAspect = true;
            return img;
        }

        /// <summary>
        /// THE AVATAR REPAINT SEAM — one call from each per-frame row paint, and the whole of the
        /// reactivity story for this feature. There is no event to raise and nothing to invalidate: the
        /// roster paints already run every frame, so a picture that finishes downloading, or a peer that
        /// joins, shows up on an ALREADY-OPEN lobby / player panel on the very next frame.
        ///
        /// The cell is INACTIVE while there is no picture (id 0 = a LAN / direct-IP peer, or Steam absent).
        /// In a layout group an inactive child is skipped entirely, so those rows keep exactly the layout
        /// they had before this cell existed.
        /// </summary>
        public static void PaintAvatar(Image cell, ulong steamId)
        {
            if (cell == null) return;
            var sprite = Multiplayer.Transport.SteamProbe.Avatar(steamId);
            if (!ReferenceEquals(cell.sprite, sprite)) cell.sprite = sprite;
            var show = sprite != null;
            if (cell.gameObject.activeSelf != show) cell.gameObject.SetActive(show);
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

        /// <summary>
        /// Minimal uGUI InputField built from code (background Image + Text + placeholder).
        ///
        /// THERE IS NO CALLBACK PARAMETER, AND THAT IS THE POINT. This used to take an
        /// <c>onEndEdit</c> action — and Unity's legacy InputField raises <c>onEndEdit</c> on DESELECT as
        /// well as on Enter, so a handler hung there turns "the player clicked somewhere else" into "the
        /// player pressed the button": on the join screen, clicking the address box and then the friends
        /// list below it started a connection nobody asked for; in the lobby, clicking out of the chat box
        /// broadcast a half-typed line. This uGUI build has no <c>onSubmit</c> event to move to (it landed
        /// in a later UnityEngine.UI than the game ships), so the submit key is read where it can be told
        /// apart from a deselect — see <see cref="SubmittedThisFrame"/>, polled by the panel that owns the
        /// field. Losing focus now does exactly what it should: nothing, with the typed text left alone.
        /// </summary>
        public static InputField CreateInputField(GameObject parent, string name, string initial,
            Vector2 pos, Vector2 size, Vector2 anchor)
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

            return input;
        }

        /// <summary>
        /// True on the frame the player pressed Enter INTO <paramref name="field"/> — the deliberate act
        /// <see cref="CreateInputField"/> refuses to confuse with losing focus. Poll it from the owning
        /// panel's per-frame tick.
        ///
        /// The focus test is "focused OR still the EventSystem's selection" on purpose. A single-line
        /// InputField DEACTIVATES ITSELF the moment it sees Enter (inside the EventSystem's own Update,
        /// whose order against ours Unity does not fix), so <c>isFocused</c> alone loses the keypress on
        /// whichever frame that Update happens to run first. Deactivating does not clear the SELECTION,
        /// which is why the selection survives the race; a genuine click elsewhere clears both, so a
        /// deselected field still answers false.
        /// </summary>
        public static bool SubmittedThisFrame(InputField field)
        {
            if (field == null || !field.gameObject.activeInHierarchy) return false;
            if (!field.isFocused)
            {
                var es = EventSystem.current;   // == on purpose: this is a liveness question, not identity
                if (es == null || !ReferenceEquals(es.currentSelectedGameObject, field.gameObject))
                    return false;
            }
            return Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
        }
    }
}
