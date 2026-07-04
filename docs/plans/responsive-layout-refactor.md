# Multiplayer Co-op Lobby — Responsive "Rubber" Layout Refactor Plan

**Scope:** `E:\DEV\PhoenixPoint\Multiplayer\src\UI\LobbyPanel.cs` (primary) + `src\UI\NativeWidgetFactory.cs` (no edit strictly required; one toggle note). Design only.

**Core insight (grounding):** responsiveness comes from the **layout tree**, not the scaler. The current code is correct at the canvas level but lays out every zone with **fractional anchors + hardcoded pixel offsets** and stacks rows with **manual `anchoredPosition` Y math**. Replacing those with nested `LayoutGroup`s makes the whole overlay reflow on any resolution/aspect with zero pixel coordinates.

**API verified** against the game's own `D:\Steam\steamapps\common\Phoenix Point\PhoenixPointWin64_Data\Managed\UnityEngine.UI.dll` (reflection), so every property/enum below is exact for this build:

- `HorizontalOrVerticalLayoutGroup` (base of both H/V groups): `childControlWidth`, `childControlHeight`, `childForceExpandWidth`, `childForceExpandHeight`, `childScaleWidth`, `childScaleHeight` (all `bool`); `spacing` (`float`).
- `LayoutGroup` base: `childAlignment` (`TextAnchor`), `padding` (`RectOffset`).
- `LayoutElement`: `ignoreLayout`, `minWidth`, `minHeight`, `preferredWidth`, `preferredHeight`, `flexibleWidth`, `flexibleHeight`, `layoutPriority`.
- `ContentSizeFitter`: `horizontalFit`, `verticalFit` — `FitMode { Unconstrained, MinSize, PreferredSize }`.
- `CanvasScaler`: `uiScaleMode` (`ScaleMode { ConstantPixelSize, ScaleWithScreenSize, ConstantPhysicalSize }`), `referenceResolution`, `screenMatchMode` (`ScreenMatchMode { MatchWidthOrHeight, Expand, Shrink }`), `matchWidthOrHeight`.

---

## A) CANVAS / SCALER

**Current (LobbyPanel.cs:127-131) — already correct, KEEP unchanged:**

```
uiScaleMode      = ScaleWithScreenSize
referenceResolution = (1920, 1080)
screenMatchMode  = MatchWidthOrHeight
matchWidthOrHeight = 0.5
```

Plus `RenderMode.ScreenSpaceOverlay`, `sortingOrder = 4000`, root canvas under `_owner.transform` (the nesting-vs-root note at 102-114 is correct and must be preserved).

**`matchWidthOrHeight = 0.5` — keep. Justification:** the overlay's child rects all **stretch** to the screen rect, so *reflow correctness is independent of the match value* — the match only scales **fixed-pixel elements** (fonts, button heights, paddings). `0.5` (balanced log-blend of width & height) keeps those fixed elements sane across 16:9, 21:9 (extra width handled by flexible center column, not by shrinking fonts) and 4:3 (extra height absorbed by the flexible MainRow). `0` (width-match) would shrink everything on 4:3; `1` (height-match) would bloat fixed elements on ultrawide. **No change needed** — this is the one part of the current code that's already right.

**Safe-area / edge padding (ultrawide):** achieved purely via `LayoutGroup.padding`, no new components:

- Root `VerticalLayoutGroup.padding` = `(left 48, right 48, top 24, bottom 24)`.
- MainRow `HorizontalLayoutGroup.padding` = `0`, `spacing = 24`.

So columns never touch screen edges on 21:9. *Optional refinement (not required):* for >21:9, wrap MainRow content in a centered child carrying `LayoutElement.preferredWidth` with flexible spacers either side to cap max width — flag only, default padding is sufficient.

---

## B) TARGET LAYOUT TREE

Legend: `LE` = LayoutElement, `VLG`/`HLG` = Vertical/Horizontal LayoutGroup. All `childControlWidth/Height = true` unless noted (required so LE/inner-group sizes are honored).

```
_root  (RectTransform full-stretch 0..1, zero offsets — UNCHANGED from 152-162)
│  + VerticalLayoutGroup
│      padding (48,48,24,24)  spacing 16
│      childControlWidth=true   childControlHeight=true
│      childForceExpandWidth=true  childForceExpandHeight=false
│      childAlignment = UpperCenter
│
├─ TopBar            LE{ minHeight 78, flexibleHeight 0 }
│   + Image + Outline (AddFramedPanel — keep)
│   + VerticalLayoutGroup{ childAlignment=MiddleCenter, childForceExpandHeight=false, spacing 2 }
│       ├ Title    (Text 32)   LE{ minHeight 44 }
│       └ Subtitle (Text 16)   LE{ minHeight 24 }   → _roleText
│
├─ MainRow           LE{ flexibleHeight 1 }      ← takes ALL leftover vertical space
│   + HorizontalLayoutGroup
│       spacing 24  padding 0
│       childControlWidth=true   childControlHeight=true
│       childForceExpandWidth=true   childForceExpandHeight=true   ← 3 equal-tall cards
│   │
│   ├─ ConnectColumn   LE{ flexibleWidth 1 }
│   │   + Image+Outline
│   │   + VerticalLayoutGroup{ padding 12, spacing 8, childForceExpandHeight=false, childAlignment=UpperLeft }
│   │       ├ "CONNECT" header      LE{ minHeight 28 }
│   │       ├ StunLabel             LE{ minHeight 20 }
│   │       ├ StunValue (copy btn)  LE{ minHeight 22 }   → _railStunValue
│   │       ├ SaveLabel             LE{ minHeight 20 }
│   │       ├ SaveValue             LE{ minHeight 40 }   → _railSaveValue
│   │       ├ ChooseSaveBtn (clone) LE{ preferredHeight 38 }
│   │       └ InviteBtn  (clone)    LE{ preferredHeight 38 }
│   │     ← rows flow top-down; removing one self-reflows, NO Y math
│   │
│   ├─ ChatColumn      LE{ flexibleWidth 2 }    ← center widest (ratio 1:2:1)
│   │   + Image+Outline
│   │   + VerticalLayoutGroup{ padding 12, spacing 8, childForceExpandHeight=false }
│   │       ├ "CHAT" header         LE{ minHeight 28, flexibleHeight 0 }
│   │       ├ ChatScrollHost        LE{ flexibleHeight 1, minHeight 80 }   ← fills middle
│   │       │    └ Scroller (native ScrollRect OR fallback rect) — anchor-stretch 0..1 to fill host
│   │       │         └ Content (RectTransform, pivot (0.5,1))
│   │       │              + VerticalLayoutGroup{ spacing 2, padding 4, childForceExpandHeight=false }
│   │       │              + ContentSizeFitter{ verticalFit=PreferredSize, horizontalFit=Unconstrained }
│   │       │                  └ ChatRowN (Text)  LE{ minHeight 18, flexibleWidth 1 }
│   │       └ ChatInputRow           LE{ minHeight 30, flexibleHeight 0 }
│   │            + HorizontalLayoutGroup{ spacing 8, childForceExpandWidth=false }
│   │              ├ ChatInput (InputField) LE{ flexibleWidth 1 }   → _chatInput
│   │              └ ChatSend  (button)     LE{ preferredWidth 70, flexibleWidth 0 }
│   │
│   └─ PlayersColumn   LE{ flexibleWidth 1 }
│       + Image+Outline
│       + VerticalLayoutGroup{ padding 12, spacing 8, childForceExpandHeight=false }
│           ├ "PLAYERS (n)" header  LE{ minHeight 28, flexibleHeight 0 }   → _connectText
│           └ RosterScrollHost      LE{ flexibleHeight 1 }
│                └ RosterContent (= _rosterArea)
│                     + VerticalLayoutGroup{ spacing 2, childForceExpandHeight=false }
│                     + ContentSizeFitter{ verticalFit=PreferredSize }
│                       └ RowN  LE{ minHeight 30, flexibleWidth 1 }   (pooled, SetActive)
│
└─ Footer            LE{ minHeight 48, flexibleHeight 0 }
    + HorizontalLayoutGroup
        spacing 12
        childControlWidth=true  childControlHeight=true
        childForceExpandWidth=false  childForceExpandHeight=true
        childAlignment = MiddleLeft
      ├ LeaveBtn (clone)     LE{ preferredWidth 150 }   → left
      ├ Spacer (empty GO)    LE{ flexibleWidth 1 }      ← pushes rest right
      ├ JoinBtn (clone)      LE{ preferredWidth 150 }
      ├ ReadySlot (wrapper)  LE{ preferredWidth 160 }   → holds ready toggle clone (stretched inside)
      └ PlayBtn (clone)      LE{ preferredWidth 150 }
```

**Column width ratio 1:2:1** via `LE.flexibleWidth` (1 / 2 / 1) under MainRow's `childForceExpandWidth=true` — the three columns share row width proportionally at any resolution. Footer Leave-left / Ready+Play-right is done with the flexible **Spacer** (cleaner and more rubber than `childAlignment`).

---

## C) NATIVE-WIDGET INTEGRATION

General rule under a `LayoutGroup` with `childControl* = true`: the **group drives child size from `LayoutElement`**, ignoring the child's own `sizeDelta`/`anchoredPosition`. So every cloned widget needs a `LayoutElement` on its **clone root** (the GO parented directly under the layout container), and its manual anchor/pos/sizeDelta become dead.

1. **Cloned menu button** (`CloneMenuButton`, returns inner `Button`; root = instantiated prefab GO). Per button:
   - Walk up to the clone root (reuse the existing walk in `AnchorCloneRoot`, 448-457).
   - `rt.localScale = Vector3.one` (prefab may carry scale >1 — keep this reset, 460).
   - Add/get `LayoutElement` on the clone root → set `preferredWidth`/`preferredHeight` (rail 216×38, footer 150×40) and `flexibleWidth = 0`.
   - Keep the **font cap** (469-471) — still needed; menu glyphs overflow the constrained rect.
   - **Drop** the `anchorMin/Max/pivot/anchoredPosition/sizeDelta` block (462-466) — the group owns position/size now.
   - Gotcha: if the prefab already carries a `LayoutElement`, our values win by setting them on the same component; if it carries a `ContentSizeFitter`, set both fits `Unconstrained` (or `Destroy` it) so it can't fight the group.

2. **Cloned ready toggle** (`CloneReadyToggle`, returns inner `Toggle`; clone root = a `GameOptionViewController` option-row). This is the known gotcha (option-row root drifts to prefab default, 366-374). **Do NOT anchor it manually. Use a wrapper to fully isolate the unpredictable option-row:**
   - Create a plain `ReadySlot` GO under Footer with `LayoutElement{ preferredWidth 160, preferredHeight 40, flexibleWidth 0 }`.
   - Clone the toggle with `ReadySlot.transform` as parent.
   - Walk to the clone root, `localScale = 1`, set its RectTransform to **stretch** inside ReadySlot (anchors 0..1, zero offsets) — ReadySlot (not the footer) absorbs whatever internal layout the option-row imposes, and the footer only ever sees ReadySlot's clean `LayoutElement`.
   - State sync (`SetIsOnWithoutNotify`, 658) unchanged.

3. **Cloned scroller** (`CloneScroller`, returns content `RectTransform` of a native `ScrollRect`):
   - The scroller GO is a child of `ChatScrollHost`/`RosterScrollHost` (those hosts are sized by their column's VLG via `LE.flexibleHeight 1`). The scroller GO itself is **anchor-stretched 0..1, zero offsets** to fill the host — this is relative, not pixel, so it's rubber. No `LayoutElement` on the scroller GO (host isn't a layout group).
   - On the returned **content** RectTransform add `VerticalLayoutGroup` + `ContentSizeFitter{ verticalFit = PreferredSize, horizontalFit = Unconstrained }`. This is the standard ScrollRect-content pattern and is **conflict-free** because content's parent is the viewport (not a layout group). Ensure content pivot = `(0.5, 1)` so growth/scroll is downward (verify in-game — native pivot may already be top).
   - Chat/roster rows then carry only a `LayoutElement` (min/preferred height) — the content's VLG positions them.

4. **From-code widgets** (`UiToolkit.CreateText/CreateButton/CreateInputField`, set their own anchors at 42-48/66-71/96-101): inside a layout group their manual pos/anchor are overridden. Where a fixed size matters, add a `LayoutElement` (Send button `preferredWidth 70`; chat input `flexibleWidth 1`; text rows `minHeight`). No factory change needed — `LobbyPanel` adds the `LayoutElement` after creation. *(Optional convenience: a `UiToolkit.AddLayoutElement(go, …)` helper, but not required.)*

---

## D) MIGRATION STEPS (ordered, concrete; `LobbyPanel.cs` unless noted)

> Each step names the method and what changes. **DELETE** flags the current manual-coordinate code being removed.

1. **`Build` (98-194):** keep canvas/scaler/_root setup verbatim (102-162). **ADD** after the `_root` rect setup (~163): `var rootVlg = _root.AddComponent<VerticalLayoutGroup>()` with the props from §B (padding 48/48/24/24, spacing 16, childControlWidth/Height true, childForceExpandWidth true, childForceExpandHeight false, childAlignment UpperCenter). Change the build-call block (178-182) to: `BuildTopBar(); BuildMainRow(); BuildFooter();` (the three columns move under MainRow). Keep the `try/finally` visibility pinning (176-193) **unchanged**.

2. **NEW `BuildMainRow()`:** create `MainRow` under `_root`; add `LayoutElement{ flexibleHeight = 1 }` + `HorizontalLayoutGroup` (spacing 24, padding 0, childControlWidth/Height true, childForceExpandWidth true, childForceExpandHeight true). Then call `BuildConnectRail(mainRow)`, `BuildChatZone(mainRow)`, `BuildRosterZone(mainRow)` — change those three to take a `GameObject parent` and parent under it instead of `_root`.

3. **`BuildTopBar` (197-216):** **DELETE** `brt.anchorMin/anchorMax/pivot/sizeDelta/anchoredPosition` (204-208). Add `LayoutElement{ minHeight 78, flexibleHeight 0 }` + an inner `VerticalLayoutGroup{ childAlignment = MiddleCenter, spacing 2 }`. Replace the two fixed-pos `CreateText` calls (211-216) with layout-driven children carrying `LayoutElement.minHeight` (Title 44, Subtitle 24); keep `_roleText` assignment.

4. **`BuildConnectRail` (220-256):** signature → `(GameObject parent)`. **DELETE** `rrt.anchorMin/anchorMax/offsetMin/offsetMax` (225-228). Add `LayoutElement{ flexibleWidth 1 }` + `VerticalLayoutGroup` (padding 12, spacing 8, childForceExpandHeight false, childAlignment UpperLeft). **DELETE every hardcoded `Vector2 pos`** in the header/label/value/button calls (231-256) — add `LayoutElement.minHeight` per row instead. For ChooseSaveBtn/InviteBtn: replace the `AnchorButton(...)` calls (246, 253) with the new `AddCloneLayoutElement(btn, 216, 38)`; keep the fallback `CreateButton` branch but feed it a `LayoutElement` too.

5. **`MakeCopyableValue` (260-266):** **DELETE** the `pos` param and `CreateButton` pos arg; add `LayoutElement{ minHeight 22 }` to the button. Update the one caller (236).

6. **`BuildChatZone` (270-315):** signature → `(GameObject parent)`. **DELETE** `crt.anchorMin/anchorMax/offsetMin/offsetMax` (275-278). Add `LayoutElement{ flexibleWidth 2 }` + `VerticalLayoutGroup`. Header → `LayoutElement{ minHeight 28, flexibleHeight 0 }`. **DELETE** `shrt.offsetMin/offsetMax` and the fractional anchors on ChatScrollHost (288-291) → `LayoutElement{ flexibleHeight 1, minHeight 80 }`. Keep `CloneScroller`; on success set content's `VerticalLayoutGroup` + `ContentSizeFitter` (§C-3); on fallback (297-306) give the fallback rect the same VLG+fitter instead of `sizeDelta(0,600)`. **DELETE** the manual input/send positions (309-315) → build a `ChatInputRow` (HLG) with input `LE.flexibleWidth 1` and send `LE.preferredWidth 70`.

7. **`BuildRosterZone` (319-340):** signature → `(GameObject parent)`. **DELETE** `prt.anchorMin/anchorMax/offsetMin/offsetMax` (324-327). Add `LayoutElement{ flexibleWidth 1 }` + `VLG`. Header → `LE.minHeight 28` (keep `_connectText`). **DELETE** `rosterRect.anchorMin/anchorMax/pivot/sizeDelta(0,400)/anchoredPosition(0,-46)` (336-340). Make `_rosterArea` the **RosterContent** with `VerticalLayoutGroup + ContentSizeFitter{verticalFit=PreferredSize}`, wrapped in a `RosterScrollHost` carrying `LE.flexibleHeight 1` (optionally `CloneScroller` here too for a real scrollbar).

8. **`BuildFooter` (344-392):** rewrite. Create `Footer` GO under `_root` with `LE{ minHeight 48, flexibleHeight 0 }` + `HorizontalLayoutGroup` (spacing 12, childForceExpandWidth false, childForceExpandHeight true, childAlignment MiddleLeft). **DELETE all four `AnchorButton` fractional-X calls** (351, 358, 380, 389) and the `AnchorCloneRoot` toggle call (373). Parent order: Leave (`LE.preferredWidth 150`) → **Spacer** GO (`LE.flexibleWidth 1`) → Join (`150`) → **ReadySlot wrapper** (`160`, §C-2) → Play (`150`). Keep the clone-vs-fallback branches; each path gets a `LayoutElement`.

9. **`AnchorButton`/`AnchorCloneRoot` (435-472):** **repurpose** into `AddCloneLayoutElement(Transform start, Transform container, float w, float h)`: keep the clone-root walk (453-454) and `localScale = 1` (460) and font cap (469-471); **DELETE** the `anchorMin/anchorMax/pivot/anchoredPosition/sizeDelta` block (462-466); instead `GetComponent<LayoutElement>() ?? AddComponent` and set `preferredWidth=w, preferredHeight=h, flexibleWidth=0`.

10. **`CreateRow` (768-792):** **DELETE** `rect.anchorMin/anchorMax/pivot/sizeDelta/anchoredPosition` (773-777). Parent to RosterContent; add `LayoutElement{ minHeight RowHeight, flexibleWidth 1 }`. Pooling/`SetActive` logic in `RefreshRoster` (697-739) unchanged — inactive rows are auto-skipped by the layout group.

11. **`RenderChat` (605-633):** **DELETE** the manual `anchoredPosition = (0, -i*20)` (630) and the per-row offsetMin/Max anchor tweaks (614-620); each pooled chat row gets `LayoutElement{ minHeight 18, flexibleWidth 1 }`. Pool grow + `SetActive` logic unchanged. (Layout group now positions rows.)

12. **Constants (64-67):** **DELETE** `RosterTop` (130f) and `RowWidth` (520f) — now unused. Keep `RowHeight` (feeds `LE.minHeight`). `RailButtonSize`/`FooterButtonSize` (426-427) kept but now feed `LayoutElement.preferred*`, not `sizeDelta`.

13. **`NativeWidgetFactory.cs`:** **no required edit.** All `LayoutElement` additions live in `LobbyPanel`. The only factory-adjacent risk is the ready-toggle option-row, handled by the LobbyPanel wrapper (§C-2). *(If in-game testing shows the cloned scroller content pivot isn't top, add a one-line pivot set in `CloneScroller` 366-397.)*

---

## E) RISKS / GOTCHAS

1. **`ContentSizeFitter` × `LayoutGroup` conflict** — the classic trap. **Rule enforced here:** `ContentSizeFitter` appears **only** on ScrollRect content (chat + roster), whose parent is the viewport, *not* a layout group → safe. Never add a fitter to a column root or zone (they're sized by the parent group via `childControl*`). Columns need no fitter — their height = MainRow height (flexible), interior children are fixed (`LE.minHeight`) plus one flexible filler.

2. **Cloned-widget RectTransform fights** — prefab clones carry serialized anchors/scale and may carry their own `LayoutElement`/`ContentSizeFitter`. Mitigation: `localScale = 1`, set our `LayoutElement` on the clone root (same component if present), and if the prefab has a `ContentSizeFitter` set it `Unconstrained`. Use `LayoutElement.layoutPriority` higher than the prefab's if values are ignored.

3. **Ready-toggle option-row** (the documented stray-fragment bug) — the `GameOptionViewController` row has unpredictable internal layout. The **ReadySlot wrapper** (§C-2) isolates it: footer sees only the wrapper's clean `LE.preferredWidth 160`. **Must verify in-game** the toggle visual sits inside the slot.

4. **Scroll view inside a layout group** — scroller GO anchor-stretches to fill its (group-sized) host; viewport needs the native mask (the cloned scroller already has `RectMask2D`/`ScrollRect`). Content uses VLG + ContentSizeFitter. **Verify** content pivot/scroll direction is top-down in-game.

5. **Performance / `LayoutRebuilder`** — `Refresh()` (526) runs **every frame** and writes `Text.text` on roster rows each frame (736); with layout groups, a text change can dirty the layout → per-frame rebuild of the roster/chat groups. Chat is already version-gated (573) so it's fine; roster is the watch item. Mitigation: gate row text writes behind a change check, or accept the rebuild (roster is small, ≤ player count). **Flag, not blocking.**

6. **`childForceExpandHeight = true` on MainRow** requires `childControlHeight = true` — both set, giving three equal-height cards. `childForceExpandWidth = true` + per-column `LE.flexibleWidth` gives the 1:2:1 share.

7. **In-game visual checks REQUIRED** (cannot verify from code): (a) ready-toggle inside ReadySlot; (b) native scroller content pivot/scroll direction with the added VLG; (c) cloned menu-button preferred size vs the prefab's internal layout (font cap still needed); (d) **21:9 and 4:3** column proportions + that padding keeps columns off the edges; (e) top bar/footer fonts remain sane on extremes under `match = 0.5`.

---

## Files referenced

- `E:\DEV\PhoenixPoint\Multiplayer\src\UI\LobbyPanel.cs` (all line numbers above)
- `E:\DEV\PhoenixPoint\Multiplayer\src\UI\NativeWidgetFactory.cs` (clone methods 195-398)
- `E:\DEV\PhoenixPoint\Multiplayer\src\UI\UiToolkit.cs` (factories 35-119)
- `E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.csproj` (SDK paths 14-15, refs 23-48)
- uGUI API verified against `D:\Steam\steamapps\common\Phoenix Point\PhoenixPointWin64_Data\Managed\UnityEngine.UI.dll`
