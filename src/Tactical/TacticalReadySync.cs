using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.View.ViewControllers;
using PhoenixPoint.Tactical.Levels;
using PhoenixPoint.Tactical.View.ViewModules;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// THE "I AM DONE MOVING" COURTESY LABEL — a second button under the native End Turn one, and the
    /// only thing in this repo that is deliberately allowed to mean NOTHING.
    ///
    /// THE ASK (developer, 2026-08-05): "It is ONLY a visual indicator of how many players have finished
    /// their moves and are willing to skip, so nobody has to keep asking everyone whether they're done.
    /// This does NOT change the fact that anyone can press End Turn at any moment — it just saves asking."
    ///
    /// ADVISORY, AND THE HARNESS KEEPS IT THAT WAY. The tally gates NOTHING: not the End Turn button, not
    /// the turn, not one rail decision. The moment any code reads it to decide something it is a QUORUM,
    /// which laws L84 ("nobody waits for anybody") and L91 ("no host decision reads other peers'
    /// membership") forbid outright — and a quorum was a real, removed bug class in this repo as recently
    /// as this morning. RailCheck L119 asserts the negative mechanically: exactly one method may read the
    /// two counters, and the two tactical arbiters are EXECUTED against a hostile tally to prove they do
    /// not consult it.
    ///
    /// NO PEER TABLE ON THE RAIL, for the same reason. The per-peer flags live on
    /// <c>SessionManager.ClientInfo.TacReady</c> next to <c>IsReady</c>/<c>IsPaused</c> — peer bookkeeping
    /// is the lobby's job, and L91 arm (c) forbids a rail type from holding a peer-id collection at all.
    /// What lives here is a bool (this peer's own flag, which paints the green) and two ints (the numbers
    /// on the label).
    ///
    /// NO NEW SURFACE (constraint 5, and the geoscape band is full anyway). The flag rides the EXISTING
    /// tactical turn family, which is literally the same question one step earlier: 0x81 TacTurnIntent
    /// gains op 3 <c>setReady</c> (client→host, one byte) and 0x80 TacTurn gains op 4 <c>readyTally</c>
    /// (host→all, two ints). Riding 0x80 also means the tally shares the turn stream's SurfaceSeq, so a
    /// stale tally can never overtake the turn edge that resets it.
    ///
    /// HOST IS AUTHORITATIVE FOR THE AGGREGATE, each peer for its own green. A peer flips its own bool
    /// instantly (no round trip for the thing it just clicked) and the host's tally follows a frame later;
    /// nothing can desync because the label and the green are two different facts.
    ///
    /// A PAUSED PEER counts in M and counts as NOT READY — see <c>SessionManager.TacReadyTally</c>. It
    /// keeps its seat (law L84), but its flag was sent before it went quiet, so treating it as ready would
    /// tell the remaining players "everyone is done" about somebody who is not there.
    ///
    /// RESET is the game's own new-player-round edge (<c>TacMission.OnNewTurn</c> via
    /// <see cref="TacNewTurnHook"/>, which already fires on every peer running the native turn machine) —
    /// no poll, no timer.
    /// </summary>
    internal static class TacticalReadySync
    {
        /// <summary>client→host on <see cref="SurfaceIds.TacTurnIntent"/> (1 = endTurn, 2 = leaveBattle).</summary>
        internal const byte OpSetReady = 3;
        /// <summary>host→all on <see cref="SurfaceIds.TacTurn"/> (1 = turn, 2 = end, 3 = leave).</summary>
        internal const byte OpReadyTally = 4;

        /// <summary>THIS peer's own flag. Drives the green state and nothing else — the label's numbers
        /// come from the host.</summary>
        internal static bool LocalReady;

        /// <summary>What the LABEL shows. Read by exactly one method (<see cref="TacticalReadyButton.Repaint"/>);
        /// RailCheck L119 arm (d) fails the build if anything else ever loads them.</summary>
        internal static int ReadyCount;
        internal static int TotalCount;

        /// <summary>Per-BATTLE teardown, driven from <see cref="TacticalTurnSync.Reset"/>.</summary>
        internal static void Reset()
        {
            LocalReady = false;
            ReadyCount = 0;
            TotalCount = 0;
            TacticalReadyButton.Forget();
        }

        // ─── The click ─────────────────────────────────────────────────────

        /// <summary>The button. NOT block-first and not a capture seam: pressing it mutates no game state
        /// on any peer, so there is nothing to block — it is the same posture as the 0x88 aim-pose intent
        /// (law 7, arbitration IS arrival order).</summary>
        internal static void Toggle()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession)
            {
                Debug.LogWarning("[Multiplayer][tac] ready toggle ignored — no live session. The button should " +
                                 "not exist outside one; it is only built when a session is active.");
                return;
            }
            LocalReady = !LocalReady;
            if (engine.IsHost)
            {
                var session = engine.Session;
                if (session != null) session.HostTacReady = LocalReady;
                HostBroadcastTally("host ready=" + LocalReady);
            }
            else
            {
                bool want = LocalReady;
                IntentRail.Send(SurfaceIds.TacTurnIntent, OpSetReady, "ready=" + want,
                                w => w.Write((byte)(want ? 1 : 0)));
            }
            TacticalReadyButton.Repaint(); // this peer's own green flips NOW; the tally follows from the host
            // THE PRESS LEAVES A MARK. The button was dead for a whole build and no log could tell "nobody
            // clicked" from "the click reached nothing" — this line is the difference, and it is the only
            // evidence that the hit surface is live short of asking a player to describe a colour.
            // The peer's OWN flag only: L119 keeps the tally counters out of every method but the four that
            // draw or write them, because a method that has the count in hand is one `if` from a quorum.
            Debug.Log("[Multiplayer][tac] ready button pressed — this peer is now " +
                      (LocalReady ? "READY" : "not ready") +
                      ". Advisory only: nothing waits on this.");
        }

        // ─── HOST ──────────────────────────────────────────────────────────

        /// <summary>A peer's flag arrived. NO validator on purpose: there is nothing to validate — the
        /// intent spends nothing, orders nothing and names no entity, so the only possible answer is
        /// "recorded". Rejecting it could not protect anything and would cost a forced re-emit.</summary>
        internal static void HandleSetReady(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op,
                                            BinaryReader r)
        {
            bool ready = r.ReadByte() != 0;
            var session = engine == null ? null : engine.Session;
            if (session == null)
            {
                Debug.LogError("[Multiplayer][tac] ready intent from peer=" + senderPeerId + " nonce=" + nonce +
                               " DROPPED — the host holds no SessionManager, so no seat can be marked and every " +
                               "peer's ready label stays wrong until the next round reset.");
                return;
            }
            session.SetTacReady(senderPeerId, ready);
            HostBroadcastTally("peer=" + senderPeerId + " ready=" + ready);
        }

        /// <summary>Recompute the aggregate off the HOST's own roster and ship it to everyone. The host's
        /// own label is repainted here too — a host that only repainted on inbound messages would never see
        /// its own click (law 11's missing-half, the same one IntentRail.HandleInbound closes).</summary>
        internal static void HostBroadcastTally(string why)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            var coord = engine.SaveTransfer;
            if (coord == null || !coord.SessionStarted) return;
            var session = engine.Session;
            if (session == null)
            {
                Debug.LogError("[Multiplayer][tac] ready tally NOT sent (" + why + ") — no SessionManager on the " +
                               "host, so nobody's ready label can be updated.");
                return;
            }
            int ready, total;
            session.TacReadyTally(out ready, out total);
            ReadyCount = ready;
            TotalCount = total;
            TacticalReadyButton.Repaint();
            TacticalTurnSync.Send(SurfaceIds.TacTurn, OpReadyTally,
                                  "ready tally " + ready + "/" + total + " (" + why + ")",
                                  w => { w.Write(ready); w.Write(total); });
        }

        // ─── CLIENT ────────────────────────────────────────────────────────

        /// <summary>REACTIVE ON ARRIVAL (law 11, and the user asked for it in as many words): the open
        /// tactical screen is repainted the instant the tally lands, never lazily on the next open.</summary>
        internal static void ApplyTally(int ready, int total)
        {
            ReadyCount = ready;
            TotalCount = total;
            TacticalReadyButton.Repaint();
            // RECEIVE-SIDE EVIDENCE, AND IT WAS MISSING. The host logged six tallies in one battle and
            // NEITHER client log held a single line about any of them — so "the client's label repaints" was
            // an assumption with no witness, which is this repo's dominant bug shape (silent swallow). One
            // line per ARRIVING tally (0x80 op 4, a handful per turn), never per frame.
            Debug.Log("[Multiplayer][tac] ready tally applied on this peer: " + ready + "/" + total +
                      " — the already-open HUD button was repainted from it. Advisory only: nothing waits " +
                      "on this.");
        }

        // ─── The round reset ───────────────────────────────────────────────

        /// <summary>A new PLAYER round started on this peer. Every flag drops. Driven from the game's own
        /// per-turn edge on <see cref="TacNewTurnHook"/> — the same one the turn cursor rides, so the reset
        /// can never land on a different beat than the turn it belongs to. Each peer clears its OWN flag
        /// locally (the green must drop immediately, not one round trip later) and the host additionally
        /// clears every seat and ships the zeroed tally.</summary>
        internal static void OnNewTurn(TacticalFaction next)
        {
            if (next == null || !next.IsControlledByPlayer) return;
            LocalReady = false;
            var engine = NetworkEngine.Instance;
            if (engine != null && engine.IsActiveSession && engine.IsHost)
            {
                var session = engine.Session;
                if (session == null)
                    Debug.LogError("[Multiplayer][tac] new-round ready reset SKIPPED on the host — no " +
                                   "SessionManager, so last round's flags stay on every peer's label.");
                else
                {
                    session.ResetTacReady();
                    HostBroadcastTally("new player round");
                }
            }
            TacticalReadyButton.Repaint();
        }
    }

    /// <summary>
    /// THE WIDGET. A CLONE of the game's own End Turn button
    /// (<c>UIModuleEndTurnContainer.Button</c>, a <c>PhoenixGeneralButton</c> —
    /// PhoenixPoint.Tactical.View.ViewModules/UIModuleEndTurnContainer.cs:12), parented as its SIBLING and
    /// placed directly beneath it. Native-UI-first: the frame, the font, the hover/press animator states
    /// and the colour controllers all come from the prefab, so it reads as part of the game rather than as
    /// an overlay, and this file draws nothing.
    ///
    /// VISIBILITY IS INHERITED, NOT INVENTED (constraint 4). The clone lives inside the End Turn
    /// container's own subtree, so the game's own module hide — <c>UIModuleBehavior.SetStateID</c>:24-54,
    /// which either <c>SetActive(false)</c>s the module or drives its Animator's state parameter — takes
    /// the clone with it. Enemy turn, cinematics, tutorial lockouts: whatever hides the End Turn button
    /// hides this one, with no rule of ours in the middle.
    ///
    /// ponytail: that inheritance holds because the module's hide animation moves the module ROOT. If a
    /// future prefab animated the <c>Button</c> child's transform alone, the clone would sit still while
    /// the original slid away. Upgrade path is one line — parent the clone to the native button itself
    /// instead of beside it — but that costs a rect-mask risk today for a hazard nothing exhibits.
    ///
    /// THE GREEN IS THE GAME'S OWN LATCHED TINT, plus one overlay of ours behind it. The first half of that
    /// sentence is new (2026-08-08) and it is the half that is load-bearing — see <see cref="Latch"/>. PP's
    /// colour mechanism is <c>UIInteractableColorController.GetColor</c>, a def-driven palette
    /// (idle/hover/pressed/disabled) that asks <c>IsSelectedTab</c> FIRST: a <c>ToggleButton</c> with
    /// <c>IsOn</c> on the button's own GameObject paints <c>PressedColor</c> onto every controlled element
    /// and NO hover or press can revert it. That is the native "this one is ON" state, and it lands on the
    /// clone's own frame/background graphics — the ones the settled log proves the canvas draws. None of the
    /// palette's six colours is green, so each controller gets a clone-private copy of the def whose
    /// PressedColor we own; the shared asset is never written.
    ///
    /// The <c>MP_ReadyGreen</c> child Image stays as a second layer — stretched over the button, raycast
    /// off, DEPTH-PLACED FROM THE REAL SUBTREE (<see cref="BuildGreenOverlay"/> derives its sibling index
    /// from the shallowest label child; index 0 is the BOTTOM of the draw order and would have been hidden
    /// by any opaque frame child). It has never been observed painting in any build, which is why
    /// <see cref="ReportGreen"/> now prints its enabled/colour/depth/cull on change: the native tint is what
    /// makes the state visible, and that log is what will finally say whether the overlay contributes at all.
    /// </summary>
    internal static class TacticalReadyButton
    {
        /// <summary>Every way this clone can display a string — <c>UnityEngine.UI.Text</c>, TMP, anything
        /// with a settable <c>text</c> property. Resolved once per battle by shape rather than by type, so
        /// the label can never silently come out blank because the prefab uses the other text stack.</summary>
        private static readonly List<Action<string>> _setters = new List<Action<string>>();
        private static Image _green;
        private static bool _loggedBuildFailure;

        /// <summary>The clone's own <c>PhoenixGeneralButton</c> — the thing that repaints the native tint on
        /// demand (<c>UpdateColorElements</c>), and the seam that makes an already-open HUD go green the
        /// instant a tally lands. Unity-null between battles.</summary>
        private static PhoenixGeneralButton _button;

        /// <summary>The <c>ToggleButton</c> we add to the clone purely so its <c>IsOn</c> can latch the tint.
        /// Nothing else uses it: its click listener is removed and <c>SetIsOn</c> is never called.</summary>
        private static ToggleButton _latch;

        /// <summary>The clone-private colour defs (one per colour-controlled Image on the clone) whose
        /// <c>PressedColor</c> this class owns, and the shade each of them shipped with — restored whenever
        /// the latch is off, so a plain press on a not-ready button does not flash the ready green.</summary>
        private static readonly List<Base.UI.UIInteractableColorsDef> _tintDefs =
            new List<Base.UI.UIInteractableColorsDef>();
        private static readonly List<Color> _tintPressed = new List<Color>();

        /// <summary>Last state <see cref="ReportGreen"/> printed. The log speaks on CHANGE only.</summary>
        private static string _lastGreenState;

        /// <summary>The clone's own rect, READ-ONLY to everyone outside this class, and the anchor the
        /// co-op player panel hangs itself under (<c>Multiplayer.UI.PlayerPanel</c>). Exposed rather than
        /// duplicated because the panel needs exactly two facts about this button — where its bottom edge
        /// ended up after the row settled, and whether the game currently has it on screen — and both are
        /// already true of this one rect. Unity-null between battles.</summary>
        internal static RectTransform Rect { get; private set; }

        /// <summary>Sibling index (under the clone's root) of the shallowest label-bearing child — where the
        /// ready tint is inserted so it lands above the frame and below the caption. -1 = no label found.</summary>
        private static int _labelDepth = -1;

        /// <summary>Battle teardown: the clone died with the tactical scene, so drop every handle to it.
        /// A retained setter would write into a destroyed object on the next battle's first repaint.</summary>
        internal static void Forget()
        {
            _setters.Clear();
            _green = null;
            _button = null;
            _latch = null;
            // The defs are runtime ScriptableObject copies owned by nobody else. Dropping the last reference
            // is the whole teardown: Unity collects them with the scene's unused assets, and RailCheck L67
            // bans Object.Destroy from this namespace outright.
            _tintDefs.Clear();
            _tintPressed.Clear();
            _lastGreenState = null;
            _labelDepth = -1;
            Rect = null;   // the panel stops finding a tactical anchor and falls back to its geoscape one
        }

        /// <summary>
        /// Build the clone. Runs on the module's OWN <c>Awake</c>, so it is once per battle and the
        /// <c>Button</c> field is already linked (the native Awake wires <c>Button.BaseButton.onClick</c>
        /// on that very line — UIModuleEndTurnContainer.cs:32-35).
        ///
        /// SOLO PLAY IS UNTOUCHED: with no live session nothing is built at all, so a single-player battle
        /// sees exactly the screen it always did.
        /// </summary>
        internal static void Build(UIModuleEndTurnContainer module)
        {
            Forget();
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return;
            var template = module == null || module.Button == null ? null : module.Button.gameObject;
            if (template == null || template.transform.parent == null)
            {
                Debug.LogError("[Multiplayer][tac] ready button NOT built — UIModuleEndTurnContainer.Button is " +
                               "null or unparented, so there is nothing to clone and no place to put it. The " +
                               "co-op ready indicator is simply absent this battle (nothing else is affected).");
                return;
            }
            try
            {
                var go = UnityEngine.Object.Instantiate(template, template.transform.parent);
                go.name = "MP_ReadyButton";
                go.SetActive(true);

                // A second copy of the End Turn HOTKEY would end the turn from a button that promises not to
                // do anything — and it also came across VISIBLY: the cloned subtree carries a "hotkey" node
                // whose Image draws the platform glyph (live log: sprite "XboxOne_Windows", the Xbox View
                // button — two panels in a circle), which is the stray icon players reported on our button.
                //
                // EVERY LINE HERE ACTS ON THE CLONE AND ONLY ON THE CLONE (2026-08-05). The earlier version
                // also called the native HotkeyController.SetDisplayed(false), on the reasoning that it is
                // what the game itself calls to take a hotkey display down. It is — but it takes that display
                // down through FOUR SERIALIZED LINKS (HotkeyIcon/HotkeyText/HotkeyLabel/Background,
                // HotkeyController.cs:131-164), and Object.Instantiate only re-points a serialized reference
                // when its target is INSIDE the copied subtree: a link the prefab aims at a shared HUD object
                // survives the copy pointing at the ORIGINAL, so SetDisplayed would SetActive(false) a NATIVE
                // object off OUR clone. Deactivating the controller's own GameObject removes the glyph without
                // dereferencing anything — the glyph Image is a child of that node (live subtree dump:
                // "hotkey | UI_ArrowRight<Image XboxOne_Windows>") — and it cannot reach outside the clone
                // because GetComponentsInChildren cannot return anything outside it. Cosmetic either way; the
                // difference is only whether a native widget can be caught in the blast radius.
                // DISABLED, never destroyed: RailCheck L67 bans Object.Destroy from this namespace outright
                // (v1 destroyed evacuating actors and wedged the battle summary), and a namespace-wide ban is
                // worth more than the one line it costs here.
                int hotkeys = 0;
                foreach (var h in go.GetComponentsInChildren<HotkeyController>(true))
                {
                    h.enabled = false;
                    // Never the clone ROOT: a prefab that put the controller on the button itself would
                    // otherwise take the whole button down with the glyph.
                    if (!ReferenceEquals(h.gameObject, go)) h.gameObject.SetActive(false);
                    hotkeys++;
                }
                if (hotkeys == 0)
                    Debug.LogWarning("[Multiplayer][tac] ready button found NO HotkeyController to hide — if a " +
                                     "stray platform glyph is still drawn on it, it is not coming from the " +
                                     "native hotkey display and the subtree dump below is where to look.");

                Rect = go.GetComponent<RectTransform>();
                PlaceBelow(template.GetComponent<RectTransform>(), Rect);
                WireClick(go);
                CollectLabels(go);      // before the overlay: the overlay's depth is derived from the labels'
                BuildGreenOverlay(go);
                // THE CLICK SURFACE IS THE CLONE'S OWN NATIVE GRAPHICS. DO NOT TRIM THEM (2026-08-08).
                // Twice now this spot has grown a TrimRaycast that silenced every cloned Graphic and put one
                // hand-built transparent Image in their place, to stop the clone answering
                // IsCursorOverGUI() over the map. Both times it killed the button outright, and the second
                // time it was measured: the live log reports the built face as
                // `MP_ReadyHit(layer=5 depth=258)` — drawn, batched, raycastTarget=true, the ONLY surface
                // left — and the button still took neither hover nor click on any peer. The known-good state
                // is the one this clone ships with: at e9500f8 the trim bailed out ("its Button names no
                // targetGraphic") and left all twelve native graphics live, and the button worked. 30a64e3
                // replaced them with the built face and it died; that is the whole before/after.
                //
                // Eating a click over its OWN rect is not the bug it was mistaken for — that is what a
                // button IS, and the native End Turn button does exactly the same over its own rect. The
                // clone mirrors End Turn's sizeDelta every frame (TacticalReadyRowFollower.Apply), so the
                // rect it eats is the art it draws, no more. If a click really must survive under this
                // widget, MOVE the widget; do not disarm its face.
                Repaint();
                // The CLONE's subtree, not the template's: the question this answers is "did the frame come
                // across", and only the clone can answer it.
                Debug.Log("[Multiplayer][tac] co-op ready button built under the native End Turn button (" +
                          _setters.Count + " label target(s)). Cloned subtree: " + Describe(go.transform));
            }
            catch (Exception ex)
            {
                if (!_loggedBuildFailure)
                {
                    _loggedBuildFailure = true;
                    Debug.LogError("[Multiplayer][tac] ready button build FAILED — the battle is unaffected but " +
                                   "players get no ready indicator (logged once per run): " + ex);
                }
                Forget();
            }
        }

        /// <summary>FALLBACK ONLY, for a row no layout group drives. The gap normally comes from the game's
        /// own <c>HorizontalOrVerticalLayoutGroup.spacing</c> (see
        /// <see cref="TacticalReadyRowFollower.NativeSpacing"/>); when there is no group to ask, this is the
        /// developer's "a line, or half a line", expressed as a FRACTION OF THE BUTTON'S OWN MEASURED HEIGHT
        /// rather than as pixels, so it is the same gap at every resolution and HUD scale.</summary>
        private const float GapFraction = 0.5f;

        /// <summary>
        /// STRICTLY BELOW, AND NATIVE UI MOVES FOR NOBODY.
        ///
        /// FOUR MISSES, FOUR DIFFERENT LIARS — a timing bug, a size bug, a UNIT bug, and finally the one that
        /// was never about arithmetic at all: measuring the RECT of a button whose art does not live inside it
        /// (see <see cref="TacticalReadyRowFollower.ExtraClearance"/>, which is where the placement is decided
        /// now — this method only wires the follower up).
        ///
        /// (1) <c>rect.height</c> read in <c>Awake</c>: the canvas has not laid the row out yet, so it is the
        ///     raw prefab number (~0 for a layout-driven button). Offset ~0 → clone ON the button.
        /// (2) <c>ignoreLayout</c> + a frozen <c>sizeDelta</c>: the clone kept that collapsed prefab size, so
        ///     the frame — sliced sprites that FILL the button rect — had no rect to fill and drew nothing,
        ///     while the caption, on its own child rect, kept drawing: "text floating in the air".
        /// (3) <c>rect.height</c> read in LateUpdate — the right VALUE in the WRONG SPACE. <c>rect</c> is
        ///     expressed in the button's OWN local space; <c>anchoredPosition</c> is expressed in its
        ///     PARENT's. The two differ by the button's own <c>localScale</c>, and this prefab is not drawn
        ///     at 1:1. Subtracting a self-space height from a parent-space position therefore moved the clone
        ///     down by only a FRACTION of a button — which is exactly the "overlaps by about half, coming up
        ///     from below" that came back from testing.
        ///
        /// SO MEASURE IN A SPACE THAT CANNOT LIE (<see cref="TacticalReadyRowFollower.ParentLocalHeight"/>):
        /// <c>GetWorldCorners</c> gives the button's height as actually rendered — it already accounts for the
        /// anchor mode (stretched anchors make <c>sizeDelta</c> an INSET, not a size, so <c>sizeDelta</c> may
        /// read 0 or negative under a 60px-tall button), for every scale in the chain, and for the layout
        /// settling late. Dividing by the PARENT's <c>lossyScale.y</c> converts that world height into the
        /// units <c>anchoredPosition</c> is actually denominated in. Everything after that is arithmetic.
        ///
        /// The clone still MIRRORS the source's anchors/pivot/scale/sizeDelta every LateUpdate, so it is
        /// rect-identical to End Turn — same frame art at the same size, drawn by the same prefab components —
        /// and it sits one measured height, plus the layout group's own spacing, plus whatever extra it takes
        /// to clear what the native row actually DRAWS, below it.
        ///
        /// <c>ignoreLayout</c> STAYS, and now only does the job it is good at: the group neither places our
        /// clone nor reserves a cell for it, so the native buttons never shift sideways to make room. What it
        /// no longer does is decide our size — the mirror re-injects the group's own computed size each frame.
        ///
        /// SIZED FOR EVERY LANGUAGE, BY DEFINITION. The width is the native End Turn button's width, live —
        /// and that button is already sized for every language the game ships. It is also the reason the digit
        /// jump cannot come back: our caption is never measured by anything, so "1/3" and "10/3" cannot move a
        /// pixel. A translation longer than the frame shrinks and wraps INSIDE it (see
        /// <see cref="FitLabel"/>) rather than widening it. Deliberately NOT a monospace swap: the prefab font
        /// is what makes the clone read as native.
        ///
        /// Sibling, still: making the clone a CHILD of the End Turn button would also dodge the group, but
        /// Unity's pointer enter/exit walks the whole parent chain, so hovering ours would light the native
        /// End Turn button up with it.
        /// </summary>
        private static void PlaceBelow(RectTransform src, RectTransform clone)
        {
            if (src == null || clone == null)
            {
                Debug.LogError("[Multiplayer][tac] ready button NOT placed — the native End Turn button or the " +
                               "clone has no RectTransform, so the clone stays wherever Instantiate left it " +
                               "(on top of the native button).");
                return;
            }

            var ignore = clone.GetComponent<LayoutElement>();
            if (ignore == null) ignore = clone.gameObject.AddComponent<LayoutElement>();
            ignore.ignoreLayout = true;

            foreach (var fitter in clone.GetComponents<ContentSizeFitter>()) fitter.enabled = false;

            var follower = clone.gameObject.AddComponent<TacticalReadyRowFollower>();
            follower.Source = src;
            follower.Gap = GapFraction;
            follower.Apply(); // first frame already looks right if the row happens to be settled
        }

        /// <summary>One line naming every node of the CLONED subtree with the drawable it carries, logged once
        /// per battle. The End Turn prefab is an asset — its child names are not in any source we can read — so
        /// this is the only way to confirm from a log that the frame really came across with the clone.</summary>
        private static string Describe(Transform root)
        {
            if (root == null) return "(none)";
            var sb = new System.Text.StringBuilder();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(t.name);
                var img = t.GetComponent<Image>();
                if (img != null) sb.Append("<Image ").Append(img.sprite == null ? "no-sprite" : img.sprite.name)
                                   .Append(' ').Append(img.type).Append('>');
                if (t.GetComponent<Text>() != null) sb.Append("<Text>");
            }
            return sb.ToString();
        }

        /// <summary>Re-wire the click exactly as <c>NativeWidgetFactory.CloneMenuButton</c> does: strip the
        /// cloned listeners (they point at the native End Turn handler) and add ours. <c>TabbingControl</c>
        /// is a SERIALIZED reference, so it survives Instantiate and would enrol our button in the native
        /// tab group — cleared.</summary>
        private static void WireClick(GameObject go)
        {
            var pgb = go.GetComponent<PhoenixGeneralButton>();
            if (pgb != null)
            {
                pgb.TabbingControl = null;
                pgb.IsSelected = false;
                // THE HOVER IS THE PREFAB'S, NOT OURS, AND THIS IS THE ONE LINE THAT KEEPS IT THAT WAY.
                // PhoenixGeneralButton.OnPointerEnter sets VisualHovered, and Update -> UpdateColorElements
                // walks ControlledElements (PhoenixGeneralButton.cs:147-164) while SetAnimationState drives the
                // Animator's HighlightedStateParameter — that pair IS the white frame the native End Turn
                // button shows, and the clone copies every component it needs. But ControlledElements is a
                // SERIALIZED list, and Object.Instantiate re-points a serialized reference only when its target
                // is INSIDE the copied subtree (same hazard as the hotkey links above): an entry the prefab
                // aims at a shared HUD object would still point at the NATIVE button's child, so hovering OURS
                // would recolour the native End Turn button and leave it stuck that way (its own Update only
                // repaints on a state CHANGE). Entries inside the clone — the frame and the caption, i.e. our
                // whole hover — are kept untouched.
                if (pgb.ControlledElements != null)
                    pgb.ControlledElements.RemoveAll(
                        c => c == null || !c.transform.IsChildOf(go.transform));
            }
            var btn = pgb != null && pgb.BaseButton != null ? pgb.BaseButton : go.GetComponentInChildren<Button>();
            if (btn == null)
            {
                Debug.LogError("[Multiplayer][tac] ready button has NO Unity Button after cloning — it will " +
                               "render but never respond to a click.");
                return;
            }
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(TacticalReadySync.Toggle);
            btn.interactable = true;
            if (pgb != null) pgb.SetInteractable(true);
            // AFTER the prune, always: the latch's own Awake calls UpdateColorElements, and running that over
            // an unpruned list would let a controller OUTSIDE the clone cache our button as its controller.
            Latch(go, pgb, btn);
        }

        /// <summary>
        /// THE NATIVE "THIS ONE IS LATCHED ON" TINT — the one green path the canvas is PROVED to draw.
        ///
        /// <c>UIInteractableColorController.GetColor</c> (Base.UI/UIInteractableColorController.cs:83-110)
        /// asks <c>IsSelectedTab</c> (:112-135) FIRST, before hover and before press, and that method answers
        /// true for a <c>ToggleButton</c> on the button's own GameObject with <c>IsOn</c> set. So a latched
        /// button paints <c>ColorSetupDef.PressedColor</c> onto every <c>ControlledElement</c> and no hover
        /// or press can revert it. Those elements are the clone's NATIVE graphics — live at depths
        /// 257/258/259 in the settled footprint log — which is exactly the guarantee our own overlay Image
        /// could never give: it is raycast-off by design, so both of this file's diagnostics filtered it out
        /// and "the green painted" has never been asserted by anything in any build.
        ///
        /// THE DEF IS COPIED, NEVER MUTATED IN PLACE. <c>UIInteractableColorsDef</c> is a shared
        /// <c>BaseDef</c> asset read by every button in the game and none of its six colours is green, so
        /// each controller gets a PRIVATE copy (<c>Object.Instantiate</c> of a ScriptableObject copies all
        /// six fields) whose PressedColor <see cref="Repaint"/> owns. Writing green into the shared def would
        /// turn every pressed button in Phoenix Point green.
        ///
        /// IMAGES ONLY. <c>SetColor</c> (:58-81) also writes the <c>Text</c> on the controller's own
        /// GameObject, so handing the CAPTION's controller a green def would print green letters on a green
        /// plate. The caption keeps the prefab's colours; the frame and the background carry the state.
        ///
        /// THE COMPONENT IS ADDED WHILE THE CLONE IS INACTIVE, deliberately: <c>ToggleButton.Awake</c>
        /// (ToggleButton.cs:22-30) dereferences <c>BaseButton</c>, so adding it to a live object throws
        /// before the field can be set. That same Awake re-adds its own <c>Toggle</c> to the click, which
        /// would invert <c>IsOn</c> behind our back on every press — removed again on the next line. Nothing
        /// ever calls <c>SetIsOn</c> (it dereferences <c>ShowHideGraphic</c>, which this clone has none of);
        /// <see cref="Repaint"/> writes the public <c>IsOn</c> field, which is all <c>IsSelectedTab</c> reads.
        /// </summary>
        private static void Latch(GameObject go, PhoenixGeneralButton pgb, Button btn)
        {
            _button = pgb;
            if (pgb == null || btn == null) return;
            if (pgb.ControlledElements != null)
                foreach (var c in pgb.ControlledElements)
                {
                    if (c == null || c.ColorSetupDef == null || c.GetComponent<Image>() == null) continue;
                    var copy = UnityEngine.Object.Instantiate(c.ColorSetupDef);
                    _tintPressed.Add(copy.PressedColor);
                    c.ColorSetupDef = copy;
                    _tintDefs.Add(copy);
                }

            go.SetActive(false);
            _latch = go.AddComponent<ToggleButton>();
            _latch.BaseButton = btn;
            go.SetActive(true);                       // ToggleButton.Awake runs HERE, with BaseButton set
            btn.onClick.RemoveListener(_latch.Toggle);

            if (_tintDefs.Count == 0)
                Debug.LogWarning("[Multiplayer][tac] ready button has NO colour-controlled Image to tint — the " +
                                 "ready state now rests on the MP_ReadyGreen overlay alone, which is the half " +
                                 "of this feature that has never been observed painting. Either the clone's " +
                                 "PhoenixGeneralButton.ControlledElements came across empty, or the prune in " +
                                 "WireClick removed every entry.");
        }

        /// <summary>The ready tint, stretched over the whole button and DEPTH-PLACED FROM THE REAL SUBTREE
        /// rather than guessed: directly beneath the shallowest label-bearing child, i.e. above every frame /
        /// background child and below the caption. Sibling index 0 (the previous choice) is the BOTTOM of the
        /// draw order, so an opaque frame child would have hidden the tint completely — and the prefab's child
        /// names are asset-only, so the order has to be derived at runtime, not assumed.</summary>
        private static void BuildGreenOverlay(GameObject go)
        {
            var overlay = new GameObject("MP_ReadyGreen", typeof(Image));
            overlay.layer = go.layer;   // a UI object built at runtime starts on layer 0, undrawn (L220)
            var rt = overlay.GetComponent<RectTransform>();
            rt.SetParent(go.transform, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.SetSiblingIndex(_labelDepth < 0 ? go.transform.childCount - 1 : _labelDepth);
            _green = overlay.GetComponent<Image>();
            _green.color = AllGreen;
            _green.raycastTarget = false;
            _green.enabled = false;
        }

        /// <summary>"EVERYBODY has pressed it" — THE ONLY GREEN, and that is the whole point (2026-08-08).
        /// There used to be a second, muted <c>MineGreen</c> for "I have pressed it", latched by
        /// <c>mine || all</c> — so the button went green the instant THIS player pressed, which is the exact
        /// opposite of the fact it exists to announce. Green now means all-ready and nothing else; "I have
        /// pressed it" is carried by the caption ("READY N/M" vs "READY? N/M") and by the player panel's tick
        /// marks, neither of which can be mistaken for the full table. The alpha belongs to the OVERLAY; the
        /// native tint takes the same RGB at alpha 1 (see <see cref="Repaint"/>), because there it multiplies
        /// the prefab's own sprites and a fractional alpha would make the frame translucent, not green.
        ///
        /// The loud shade, because that is the state the developer
        /// asked to be unmissable: nobody should sit waiting on a table that is already full.
        ///
        /// IT PAINTS AND IT DOES NOTHING ELSE. This is the exact shade a quorum would wear, so say the
        /// negative out loud: no code branches on all-ready, nothing waits for it, and End Turn stays
        /// pressable by anyone at any moment whatever colour this button is. The mechanical guarantee is
        /// L119 arm (d) — <see cref="Repaint"/> is the ONLY method permitted to load the two counters, and
        /// the build fails the moment a second one does, which is precisely the moment a colour could
        /// become a gate.
        ///
        /// A PLAIN <c>Image.color</c> WRITE IS SAFE ON THE OVERLAY, unlike on the button's own graphics: PP
        /// re-asserts colour through <c>UIInteractableColorController.SetColor</c>, which only ever writes the
        /// Image on the CONTROLLER'S OWN GameObject (UIInteractableColorController.cs:72-79). Nothing native
        /// carries a controller on this overlay, so no hover or press reverts it. On the button's own
        /// graphics the same shade arrives the game's own way instead — as the clone-private def's
        /// PressedColor, latched by <see cref="Latch"/>.</summary>
        private static readonly Color AllGreen = new Color(0.20f, 0.95f, 0.30f, 0.90f);

        /// <summary>Every text component on the clone, by SHAPE (a settable public <c>string text</c>), plus
        /// the standing-down of any I2 <c>Localize</c> component — a live localiser would overwrite our label
        /// with the End Turn string on its next refresh. Resolved by name (the mod does not reference I2) and
        /// DISABLED rather than destroyed (RailCheck L67, see the hotkey above).</summary>
        private static void CollectLabels(GameObject go)
        {
            var localize = AccessTools.TypeByName("I2.Loc.Localize");
            if (localize != null)
                foreach (var c in go.GetComponentsInChildren(localize, true))
                    if (c is Behaviour b) b.enabled = false;

            _labelDepth = -1;
            foreach (var c in go.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c is Image) continue;
                PropertyInfo p = null;
                try { p = c.GetType().GetProperty("text", typeof(string)); } catch { }
                if (p == null || !p.CanWrite) continue;
                var target = c;
                var prop = p;
                _setters.Add(s => { try { prop.SetValue(target, s, null); } catch { } });
                FitLabel(c);
                int depth = DirectChildIndex(go.transform, c.transform);
                if (depth >= 0 && (_labelDepth < 0 || depth < _labelDepth)) _labelDepth = depth;
            }
            if (_setters.Count == 0)
                Debug.LogWarning("[Multiplayer][tac] ready button found NO text component to label — the button " +
                                 "will show the cloned End Turn caption instead of the ready count.");
        }

        /// <summary>Sibling index of the ancestor of <paramref name="node"/> that is a DIRECT child of
        /// <paramref name="root"/>, or -1 if the node is the root itself / unrelated.</summary>
        private static int DirectChildIndex(Transform root, Transform node)
        {
            // ReferenceEquals, not `==`: this asks "is this the same object", which is an IDENTITY question
            // (L113) — Unity's operator would answer "is the native half alive" and quietly compare ids.
            while (node != null && node.parent != null && !ReferenceEquals(node.parent, root)) node = node.parent;
            return node != null && ReferenceEquals(node.parent, root) ? node.GetSiblingIndex() : -1;
        }

        /// <summary>
        /// LOCALIZATION SAFETY VALVE. The frame's width is the native End Turn button's (see
        /// <see cref="PlaceBelow"/>), which is already sized for every language the game ships — so the only
        /// thing left to decide is what a translation that is STILL longer does. It shrinks and wraps inside
        /// the frame; it never widens it, because a widening label is exactly the digit-jump bug wearing a
        /// Portuguese hat.
        ///
        /// uGUI <c>Text</c> directly (the End Turn caption is that stack), TMP by reflection — the mod does
        /// not reference TextMeshPro, and the two auto-size APIs are not the same shape.
        /// </summary>
        private static void FitLabel(Component c)
        {
            if (c is Text t)
            {
                t.resizeTextForBestFit = true;
                t.resizeTextMaxSize = t.fontSize > 0 ? t.fontSize : 24;
                t.resizeTextMinSize = 8;
                t.horizontalOverflow = HorizontalWrapMode.Wrap;
                t.verticalOverflow = VerticalWrapMode.Truncate;
                return;
            }
            try
            {
                var type = c.GetType();
                type.GetProperty("enableAutoSizing")?.SetValue(c, true, null);
                type.GetProperty("fontSizeMin")?.SetValue(c, 8f, null);
                type.GetProperty("enableWordWrapping")?.SetValue(c, true, null);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Multiplayer][tac] ready label auto-size not applied to " + c.GetType().Name +
                                 " — a long translation will be clipped rather than shrunk: " + ex.Message);
            }
        }

        /// <summary>
        /// THE ONE READER of the tally (RailCheck L119 arm (d) is a set equality on exactly that).
        ///
        /// LABELS: "READY? N/M" while this peer has not pressed it, "READY N/M" once it has — one character
        /// apart so both fit the End Turn button's width, with the green background carrying the real
        /// not-ready/ready distinction. N = peers currently ready, M = peers seated in the session.
        ///
        /// Unity-null aware: after a battle the clone is destroyed and <see cref="Forget"/> normally clears
        /// these, but a repaint racing teardown must be a no-op rather than a MissingReference throw.
        /// </summary>
        internal static void Repaint()
        {
            // NOTHING BUILT — solo play, a battle whose clone failed, or the headless RailCheck host. The
            // early-out is ReferenceEquals rather than Unity's `==`: this asks "do we hold a handle at all",
            // which is an identity question (L113), and it is what keeps the harness's EXECUTED probes of
            // OnNewTurn from reaching Unity's native liveness check with no engine under them.
            if (_setters.Count == 0 && ReferenceEquals(_green, null)) return;
            bool mine = TacticalReadySync.LocalReady;
            int ready = TacticalReadySync.ReadyCount, total = TacticalReadySync.TotalCount;
            // THE ONLY BRANCH THIS COUNT IS EVER ALLOWED TO FEED, AND IT FEEDS A COLOUR. `total > 0` is not
            // caution about division — it is the pre-tally state (0/0), which would otherwise read as
            // "everyone is ready" before a single tally has landed. Recomputed on every repaint, so it drops
            // back to the prefab's own shade by itself the moment a peer un-readies or the round edge zeroes the
            // flags; there is no separate "revert" path to forget to call.
            bool all = total > 0 && ready >= total;
            string label = (mine ? "READY " : "READY? ") + ready + "/" + total;
            for (int i = 0; i < _setters.Count; i++) _setters[i](label);

            // GREEN MEANS EVERYBODY, NOT ME. The latch follows `all` alone: `mine` never colours the button,
            // because a peer who sees green the moment he presses learns nothing he did not already know and
            // is told the table is full when it is not. `mine` still moves the caption one character
            // ("READY N/M" vs "READY? N/M") — that is the self-pressed indication, and it is deliberately
            // not a colour.
            if (_green != null)
            {
                _green.enabled = all;
                _green.color = AllGreen;
            }
            // THE NATIVE HALF. The same shade at full alpha — here it MULTIPLIES the prefab's own sprites,
            // so a fractional alpha would read as a see-through button rather than a green one. Restored to
            // the shade the prefab shipped with whenever the latch is off, so a plain press on a table that
            // is not yet full flashes the button's own colour and not this feature's.
            var tint = new Color(AllGreen.r, AllGreen.g, AllGreen.b, 1f);
            for (int i = 0; i < _tintDefs.Count; i++)
                _tintDefs[i].PressedColor = all ? tint : _tintPressed[i];
            if (_latch != null) _latch.IsOn = all;
            // THE REPAINT SEAM, AND THE REASON IT IS AN EXPLICIT CALL. PhoenixGeneralButton.Update repaints
            // only when a VISUAL state changed (PhoenixGeneralButton.cs:147-156) — a tally arriving off the
            // rail is not one, so the open HUD would keep last frame's colour until the player happened to
            // hover. Calling the game's own UpdateColorElements (:158-164 -> every controller's UpdateColor
            // -> GetColor -> IsSelectedTab -> PressedColor) is what makes an ALREADY-OPEN screen go green on
            // the beat the tally lands, which is this mod's first postulate.
            if (_button != null) _button.UpdateColorElements();
            ReportGreen(all);
        }

        /// <summary>
        /// THE OVERLAY HAS NEVER BEEN OBSERVED PAINTING, AND BY CONSTRUCTION IT COULD NOT BE. Both of this
        /// file's diagnostics filtered on <c>raycastTarget</c> and the overlay's is deliberately off; and
        /// <see cref="Describe"/> prints a sprite name and a fill type, never <c>enabled</c>, colour, depth
        /// or cull. So the single fact the original green rested on — that an enabled, coloured Image at that
        /// depth is actually drawn — has never appeared in a log, in any build, and the first time a human
        /// could press the button it did not appear on screen either.
        ///
        /// <c>Graphic.depth</c> is -1 until the canvas batches the graphic and <c>CanvasRenderer.cull</c> is
        /// uGUI's own "clipped away" flag: between them they separate "never enabled" from "enabled and not
        /// drawn", which is the whole remaining question. ON CHANGE ONLY — <see cref="Repaint"/> runs on a
        /// press, a tally or a round edge, never per frame, and this line speaks only when what it would
        /// print differs from what it printed last.
        /// </summary>
        private static void ReportGreen(bool all)
        {
            string state = _green == null
                ? "overlay=<none built>"
                : "overlay enabled=" + _green.enabled + " color=" + _green.color + " depth=" + _green.depth +
                  " cull=" + _green.canvasRenderer.cull + " sibling=" + _green.transform.GetSiblingIndex();
            state += " | native latch IsOn=" + (_latch == null ? "<no ToggleButton>" : _latch.IsOn.ToString()) +
                     " over " + _tintDefs.Count + " tinted def(s), state=" + (all ? "ALL-READY" : "off");
            if (state == _lastGreenState) return;
            _lastGreenState = state;
            Debug.Log("[Multiplayer][tac] ready button green: " + state +
                      ". The native latch is what colours the button's own frame; the overlay is the second " +
                      "layer whose paint this line exists to witness.");
        }
    }

    /// <summary>
    /// THE ROW. Keeps the cloned button rect-identical to the native End Turn button and exactly one row
    /// below it, every frame, forever.
    ///
    /// A ONE-SHOT COPY CANNOT DO THIS, and that is the whole reason this component exists. The clone is built
    /// from <c>UIModuleEndTurnContainer.Awake</c>, before the canvas has laid the row out: the numbers read
    /// there are raw prefab values (a layout-driven button measures ~0 high), which is how the first two
    /// attempts put the clone ON the native button and left it collapsed to a size with no room for the frame
    /// art. Reading in LateUpdate reads the size the layout actually settled on.
    ///
    /// AND IT MUST BE READ IN THE PARENT'S UNITS. <c>rect</c>/<c>sizeDelta</c> are the button's own; a
    /// <c>anchoredPosition</c> step is the parent's; mixing them is what put the clone half a button too high
    /// on the third attempt. <see cref="ParentLocalHeight"/> converts once and everything downstream is in
    /// parent units.
    ///
    /// AND THE RECT IS NOT THE PICTURE. Clearing End Turn's rect exactly — which the third attempt provably
    /// did — still left the clone under its glow and shadow, because those are child rects that bleed past
    /// the frame on purpose. <see cref="ExtraClearance"/> is what closed that, and it is also what makes a
    /// native sibling BELOW End Turn impossible to land on.
    ///
    /// It is also the localization guarantee and the anti-digit-jump guarantee in one line: the width is
    /// ALWAYS the native button's width — already sized for every shipped language — and our own caption is
    /// never measured by anything, so no string we write can move a pixel of geometry.
    ///
    /// Cost is four assignments a frame on one object. The alternative (a coroutine that settles once) breaks
    /// again the moment the row re-flows — resolution change, HUD scale, a module the game shows or hides.
    /// </summary>
    internal sealed class TacticalReadyRowFollower : MonoBehaviour
    {
        /// <summary>The native End Turn button's rect. Unity-null when the battle tears down.</summary>
        internal RectTransform Source;

        /// <summary>Gap as a fraction of the measured button height (see <c>TacticalReadyButton.GapFraction</c>).</summary>
        internal float Gap = 0.5f;

        /// <summary>Scratch for <see cref="RectTransform.GetWorldCorners"/>, which fills a caller-owned array.
        /// Static and reused: this runs every LateUpdate and Unity's UI is single-threaded, so allocating four
        /// Vector3s a frame forever would be litter for nothing.</summary>
        private static readonly Vector3[] Corners = new Vector3[4];

        /// <summary>The diagnostic below is a ONE-SHOT: it is the state at the first DRAWN frame, and a
        /// per-frame version would bury the log.</summary>
        private bool _measured;

        /// <summary>Frames spent waiting for the canvas to draw the clone before the one-shot report gives up
        /// and files its answer anyway.</summary>
        private int _waited;

        /// <summary>~10s at 60fps. A bound, not a guess at curtain length: a clone the canvas NEVER draws is
        /// the genuine "dead button" this whole diagnostic exists to catch, so the report must still land —
        /// and land saying depth=-1, which at that point is a true finding rather than a curtain artefact.</summary>
        private const int DrawWaitFrames = 600;

        /// <summary>Has the canvas drawn any of the clone's live surfaces yet? <c>Graphic.depth</c> is uGUI's
        /// own marker — -1 until the canvas batches the graphic, and the exact value GraphicRaycaster skips
        /// on. ACTIVE graphics only, and deliberately: <c>GetComponentsInChildren&lt;Graphic&gt;(true)</c>
        /// also returns the hotkey subtree this mod switched OFF, which is permanently depth=-1 by our own
        /// hand and is not a surface anybody wanted.
        ///
        /// NO <c>raycastTarget</c> FILTER (removed 2026-08-08). The question here is "has the canvas drawn
        /// anything of ours", which raycastTarget has nothing to do with — and the same predicate, written
        /// twice, was what struck <c>MP_ReadyGreen</c> (raycast-off by design) out of BOTH of this class's
        /// diagnostics, so the one graphic the ready state depended on had no witness anywhere. The active
        /// filter already excludes the switched-off hotkey subtree this clause was really guarding against.</summary>
        private bool Drawn()
        {
            foreach (var g in GetComponentsInChildren<Graphic>(false))
                if (g != null && g.depth >= 0) return true;
            return false;
        }

        private void LateUpdate() => Apply();

        internal void Apply()
        {
            var me = transform as RectTransform;
            if (Source == null || me == null)
            {
                // Teardown, or a prefab shape we cannot follow. Stop rather than write into a dead rect —
                // and say so once, because a silently frozen button is this repo's dominant bug class.
                if (enabled)
                    Debug.LogWarning("[Multiplayer][tac] ready button stopped following the native End Turn " +
                                     "button (source rect gone). It keeps its last position for the rest of " +
                                     "this battle; nothing else is affected.");
                enabled = false;
                return;
            }

            me.anchorMin = Source.anchorMin;
            me.anchorMax = Source.anchorMax;
            me.pivot = Source.pivot;
            me.localScale = Source.localScale;
            me.sizeDelta = Source.sizeDelta;

            float height = ParentLocalHeight(Source);
            // Not laid out yet (first frame, or the module is hidden and the canvas gave it no size). Leave the
            // clone where it is and try again next frame rather than placing it against a zero.
            if (height <= 0f) return;

            string spacingFrom;
            float gap = NativeSpacing(height, out spacingFrom);
            string clearanceFrom;
            float extra = ExtraClearance(me, height, out clearanceFrom);
            // THE LARGER OF THE TWO SEPARATIONS, NOT THEIR SUM — this is what stops the clone floating in
            // the battlefield instead of reading as the row's second line. The live measurement:
            // height=60 + gap=30 + extra=20 = 110 parent-local, 1.83 button heights of step, which left
            // 16.67 WORLD units of empty air between the HUD row's bottom edge and the top of this button.
            // The two terms are alternatives, not layers: `extra` is how far the row's art actually draws
            // below End Turn's rect (live: 20, by the sibling 'UIMainButton_HPriority'), and `gap` is the
            // breathing room we want between two stacked buttons. Adding them charges for the same air
            // twice. Stacking them was over-correction left from the four placement misses this class
            // documents — each of those pushed the clone FURTHER down, and the unit bug that actually
            // caused "overlaps by about half" was fixed by ParentLocalHeight, not by this padding.
            // ponytail: max() leaves 10 parent-local units past the drawn art. The clone's own upward glow
            // is feathered and will graze End Turn's — which is what a native stacked row looks like. If it
            // reads as a smear in game, go back to `gap + extra`; nothing else depends on this line.
            me.anchoredPosition = Source.anchoredPosition - new Vector2(0f, height + Mathf.Max(gap, extra));

            // AND THEN BACK ONTO THE SCREEN. Every fix so far pushed this clone FURTHER down — a height,
            // then a spacing, then the row's drawn overhang — and nothing ever checked that the place it
            // landed still exists. Nothing can hover or click a rect outside the canvas: it is not that the
            // raycaster refuses it, it is that no pointer position maps to it. Same clamp the co-op player
            // panel already applies for the same reason (PlayerPanel.ClampOnScreen), against the same bound
            // — the canvas' own rect, not Screen.height, because a ScaleWithScreenSize canvas is denominated
            // in scaled units and raw pixels would be wrong by the scale factor on every non-reference
            // resolution. Recomputed from Source every frame BEFORE this line, so the correction can never
            // accumulate.
            float lift = ClampOntoScreen(me);
            if (lift != 0f) me.anchoredPosition += new Vector2(0f, lift);

            if (_measured) return;
            // MEASURE A DRAWN CANVAS, NOT A PLACED RECT. The report below reads Graphic.depth and fires a
            // synthetic raycast; both are meaningless until the canvas has actually drawn this clone, and the
            // first frame the LAYOUT settles is not that frame — the battle opens behind the loading curtain,
            // where every graphic still reads depth=-1 and a full-screen vignette is on top of everything.
            // Firing there is what put "NOT HIT-TESTABLE" and "BURIED" into all three peers' logs as ERROR in
            // a battle where the host then pressed the button five times: two false alarms per battle, which
            // is how a real ERROR gets lost. Waiting costs nothing — this is a one-shot, and the answer it
            // gives once the canvas is up is the answer anyone actually wants.
            if (!Drawn() && ++_waited < DrawWaitFrames) return;
            _measured = true;
            Debug.Log("[Multiplayer][tac] ready row measured: EndTurn anchorMin=" + Source.anchorMin +
                      " anchorMax=" + Source.anchorMax + " pivot=" + Source.pivot +
                      " sizeDelta=" + Source.sizeDelta + " rect.height=" + Source.rect.height +
                      " localScale.y=" + Source.localScale.y + " parentLossyScale.y=" +
                      (Source.parent == null ? 1f : Source.parent.lossyScale.y) +
                      " | worldHeight=" + WorldHeight(Source) + " -> parentLocalHeight=" + height +
                      " | gap=" + gap + " (" + spacingFrom + "), extraClearance=" + extra + " (" +
                      clearanceFrom + "), offset=" + (height + Mathf.Max(gap, extra)) +
                      " (height + the LARGER of gap/clearance, never their sum), onScreenLift=" + lift +
                      " | EndTurn anchoredPosition=" + Source.anchoredPosition + " ready anchoredPosition=" +
                      me.anchoredPosition + " ready worldHeight=" + WorldHeight(me) +
                      " (must equal EndTurn's worldHeight — a 0 here means the clone has no rect for the " +
                      "frame sprites to fill).");
            ReportFootprint(me);
        }

        /// <summary>
        /// DOES OUR CLONE HANG OFF THE HUD, AND HOW MANY THINGS ON IT CAN EAT A CLICK — the two facts that
        /// decide whether this button can swallow a map click, logged from the SETTLED layout rather than
        /// from construction, because at construction the canvas has not placed anything yet and every
        /// number would be the raw prefab's (that mistake is miss #1 in <c>PlaceBelow</c>'s list).
        ///
        /// "HUD bounds" is read as the clone's own PARENT rect — the row container the native End Turn
        /// button lives in. Our clone is the one element deliberately pushed BELOW its row, so if its world
        /// bottom sits under the container's, it is drawing over whatever is behind the HUD, which in a
        /// battle is the tactical map. Combined with the raycast count this is the whole question: a rect
        /// over the map with nothing raycastable on it is harmless, and one raycastable graphic there is a
        /// dead confirm click. The count alone never settled that, so the EventSystem is asked directly at
        /// the end of this method — see the probe below for why a clean count proves nothing.
        /// </summary>
        private void ReportFootprint(RectTransform me)
        {
            var parent = me.parent as RectTransform;
            me.GetWorldCorners(Corners);
            float myBottom = Mathf.Min(Corners[0].y, Corners[3].y);
            float myTop = Mathf.Max(Corners[1].y, Corners[2].y);
            float myLeft = Mathf.Min(Corners[0].x, Corners[1].x);
            float myRight = Mathf.Max(Corners[2].x, Corners[3].x);
            string hud = "(no parent rect)";
            string verdict = "UNKNOWN";
            if (parent != null)
            {
                parent.GetWorldCorners(Corners);
                float pBottom = Mathf.Min(Corners[0].y, Corners[3].y);
                float pTop = Mathf.Max(Corners[1].y, Corners[2].y);
                hud = parent.name + " world y=[" + pBottom + ".." + pTop + "]";
                // BY CONSTRUCTION, AND SAID AS SUCH. This used to read "OVERHANGS ... drawn over the tactical
                // map", which is a defect's phrasing for the widget's own specification. The container is
                // exactly ONE button tall (live: EndTurnContainerModule world y=[633.33..653.33], End Turn's
                // own height 20 — it fills it), and the ask was a SECOND button UNDER that one, so no layout
                // puts this clone inside that rect without resizing native UI, which this file refuses to do.
                // Nor is the container a bound anything else respects: the row's own art already draws past
                // its bottom edge (live: the sibling 'UIMainButton_HPriority', which is what ExtraClearance
                // measures). The bound that CAN make this widget unhittable is the CANVAS, measured next and
                // re-clamped every frame in Apply — and the live reading there is ON SCREEN.
                //
                // What DID matter here was distance, not containment: the clone used to sit 16.67 world units
                // of empty air below the row and read as floating over the battlefield. That is fixed at the
                // placement line in Apply, not by this measurement.
                verdict = myBottom < pBottom
                    ? "extends " + (pBottom - myBottom) + " world units below it — expected: this clone is " +
                      "the deliberate second row under a container that is one button tall, and the row's own " +
                      "art already draws past that same edge (not a clipping parent). Watch this number for " +
                      "DRIFT, not for being non-zero"
                    : "inside its HUD container";
            }

            // THE CONTAINER THAT DECIDES WHETHER A POINTER CAN EVEN BE HERE. The line above measures the
            // clone against the End Turn MODULE, and that reading is informational only (see above).
            // The rect that CAN make a widget unhittable is the canvas: no pointer position maps to a rect
            // outside it, so hover and click are impossible with every wire on the button correct — which is
            // exactly the symptom, on every peer, always. This is the number four rounds of measuring our own
            // fields never took.
            string scr = "(no canvas)";
            var canvas = me.GetComponentInParent<Canvas>();
            var screen = canvas == null || canvas.rootCanvas == null
                ? null
                : canvas.rootCanvas.transform as RectTransform;
            if (screen != null)
            {
                screen.GetWorldCorners(Corners);
                float sBottom = Mathf.Min(Corners[0].y, Corners[3].y);
                float sTop = Mathf.Max(Corners[1].y, Corners[2].y);
                scr = screen.name + " world y=[" + sBottom + ".." + sTop + "] -> " +
                      (myBottom >= sBottom && myTop <= sTop
                          ? "ON SCREEN"
                          : "OFF SCREEN by " + ShiftOntoScreen(myBottom, myTop, sBottom, sTop) +
                            " world units — nothing can hover or click a rect the pointer cannot reach");
            }

            // LAYER AND DEPTH, NOT JUST THE COUNT. A surface can be raycastTarget=true and still be hit by
            // NOTHING: GraphicRaycaster skips any graphic the canvas never drew (depth == -1), and a UI
            // object built at runtime lands on layer 0 where the HUD camera does not draw it. Reporting the
            // count alone is what let a completely dead button log a clean line.
            //
            // ACTIVE CHILDREN ONLY (the `false`), and that alone halves the false alarm this line used to
            // raise: the includeInactive scan also returned the hotkey subtree THIS MOD switched off during
            // Build, which is depth=-1 permanently, by our own hand, and is not a hit surface anyone wanted —
            // six of the twelve surfaces in the live "NOT HIT-TESTABLE" report were exactly that. A graphic
            // nobody can hit because it is switched off is not a defect; a DRAWN one at depth=-1 is.
            int live = 0, undrawn = 0, seen = 0;
            var names = new List<string>();
            foreach (var g in GetComponentsInChildren<Graphic>(false))
            {
                if (g == null) continue;
                seen++;
                if (g.raycastTarget)
                {
                    live++;
                    if (g.depth == -1) undrawn++;
                }
                // LISTED EVEN WHEN IT CANNOT BE HIT (2026-08-08). The counts stay raycast-only because they
                // are about the CLICK surface — but the list is the only per-graphic evidence this button
                // ever produces, and that same filter used to strike MP_ReadyGreen, which is raycast-off by
                // design, out of it. A feature whose one graphic is excluded from every diagnostic is a
                // feature that can be dead for its whole life without a single log line saying so.
                if (names.Count < 16)
                    names.Add(g.gameObject.name + "(layer=" + g.gameObject.layer + " depth=" + g.depth +
                              (g.raycastTarget ? "" : " raycast-off") + (g.enabled ? "" : " component-off") +
                              (g.canvasRenderer != null && g.canvasRenderer.cull ? " culled" : "") + ")");
            }

            Debug.Log("[Multiplayer][tac] ready button footprint: world x=[" + myLeft + ".." + myRight +
                      "] y=[" + myBottom + ".." + myTop + "] vs HUD container " + hud + " -> " + verdict +
                      " | vs canvas " + scr +
                      " | raycast-enabled graphics: " + live + " of " + seen + " active [" +
                      string.Join(", ", names.ToArray()) +
                      "]. The raycast-enabled ones are the clone's OWN native graphics and they are meant to " +
                      "be live — they are " +
                      "the button's face, exactly as the native End Turn button's are. A count of 0 means " +
                      "something disarmed them and the button is dead.");
            // ALL OF THEM, NOT ANY OF THEM. `undrawn > 0` was the wrong test and the live settled log proves
            // it: 6 of 12 read depth=-1 on a button that worked, and they are the ones that SHOULD — the
            // `highlight` graphic is only drawn while hovering, `Mask`/`pattern`/`inline`/`underlight` are
            // state or mask layers the canvas culls. A partly-undrawn button is the NORMAL resting state of
            // this prefab. What is actually fatal is NOTHING drawn: then GraphicRaycaster has no surface to
            // return and the button takes neither hover nor click. For the partial case the authority is the
            // raycaster itself, asked directly in ProbeReachable below — an inference from our own fields
            // never could answer it, which is the whole reason that probe exists.
            if (live > 0 && undrawn == live)
                Debug.LogError("[Multiplayer][tac] ready button is NOT HIT-TESTABLE: ALL " + live + " of its " +
                               "raycast surface(s) report depth=-1, which is uGUI's own marker for " +
                               "\"the canvas never drew this\" — GraphicRaycaster skips exactly those, so the " +
                               "button takes neither hover nor click even though every wire on it is correct. " +
                               "The usual cause is a layer the HUD camera does not render (a runtime-built UI " +
                               "object starts on layer 0); compare the layer= values above against the clone's.");

            ProbeReachable(me, live);
        }

        /// <summary>
        /// ASK THE EVENTSYSTEM WHAT IS UNDER THIS BUTTON. DO NOT INFER IT FROM OUR OWN FIELDS.
        ///
        /// Every number in <see cref="ReportFootprint"/> is a scan of the clone's own children, and that is
        /// precisely what read CLEAN through a completely dead button: raycastTarget true, layer 5, depth 258,
        /// exactly one surface — and neither hover nor click on any peer. A scan of what we SET can never
        /// answer whether anything can HIT it; only the raycaster the game actually uses can, because only it
        /// applies the parts we do not own — the ancestor <c>ICanvasRaycastFilter</c> chain (a
        /// <c>CanvasGroup</c> with <c>blocksRaycasts</c> off, a <c>Mask</c>/<c>RectMask2D</c> whose rect this
        /// deliberately-overhanging widget sits outside of), the canvas a graphic is REGISTERED against, and
        /// anything drawn over us with a higher sort order.
        ///
        /// So this fires one synthetic raycast at the button's own centre and reports what comes back, top
        /// first. Ours on top = reachable, and any future "the button is dead" is NOT a raycast problem.
        /// Ours absent or buried = the name of the thing in the way is in the log, which is the one fact four
        /// rounds of inference never produced.
        /// </summary>
        private void ProbeReachable(RectTransform me, int live)
        {
            var es = EventSystem.current;
            if (es == null)
            {
                Debug.LogError("[Multiplayer][tac] ready button reachability UNKNOWN: no EventSystem.current, " +
                               "so nothing in this battle can be clicked and the probe has nothing to ask.");
                return;
            }

            var canvas = me.GetComponentInParent<Canvas>();
            var cam = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay
                ? null
                : canvas.worldCamera;
            var screen = RectTransformUtility.WorldToScreenPoint(cam, me.TransformPoint(me.rect.center));

            var hits = new List<RaycastResult>();
            es.RaycastAll(new PointerEventData(es) { position = screen }, hits);

            int mine = -1;
            var seen = new List<string>();
            for (int i = 0; i < hits.Count; i++)
            {
                var go = hits[i].gameObject;
                if (mine < 0 && go != null && go.transform.IsChildOf(transform)) mine = i;
                if (seen.Count < 6) seen.Add((go == null ? "<null>" : go.name) + "@" + hits[i].sortingOrder);
            }

            string where = "screen=" + screen + " canvas=" + (canvas == null ? "<none>" : canvas.name) +
                           " hits=" + hits.Count + " [" + string.Join(", ", seen.ToArray()) + "]" +
                           (mine == 0 ? "" : " ancestors=" + Filters());

            if (mine == 0)
                Debug.Log("[Multiplayer][tac] ready button IS REACHABLE: the EventSystem returns it on top at " +
                          "its own centre, so hover and click are wired to a surface that can be hit. " + where);
            else if (mine > 0)
                Debug.LogError("[Multiplayer][tac] ready button is BURIED: the EventSystem finds it at index " +
                               mine + " at its own centre, under " + hits[0].gameObject.name + " — that object " +
                               "takes the hover and the click instead. " + where);
            else
                Debug.LogError("[Multiplayer][tac] ready button is UNREACHABLE: the EventSystem returns NOTHING " +
                               "of ours at its own centre, though " + live + " of its graphics are raycast " +
                               "targets. The graphic is set up and the raycaster still refuses it, so the cause " +
                               "is OUTSIDE the clone: an ancestor CanvasGroup/Mask/RectMask2D filtering the " +
                               "point, or a canvas whose raycaster does not serve this graphic. " + where);
        }

        /// <summary>NAME THE THING IN THE WAY. Built only when the probe comes back bad, because that is the
        /// one moment the answer is worth a line: every ancestor up to the canvas, with the raycast filter it
        /// carries. <c>Graphic.Raycast</c> walks exactly this chain and asks each
        /// <c>ICanvasRaycastFilter</c> — a <c>CanvasGroup</c> with <c>blocksRaycasts</c> off, or a
        /// <c>Mask</c>/<c>RectMask2D</c> whose rect this widget sits outside — so if one of them refuses the
        /// point, it is in this list by name.</summary>
        private string Filters()
        {
            var sb = new System.Text.StringBuilder();
            for (var t = transform.parent; t != null; t = t.parent)
            {
                if (sb.Length > 0) sb.Append(" < ");
                sb.Append(t.name);
                var cg = t.GetComponent<CanvasGroup>();
                if (cg != null) sb.Append("<CanvasGroup blocksRaycasts=").Append(cg.blocksRaycasts)
                                  .Append(" alpha=").Append(cg.alpha).Append('>');
                if (t.GetComponent<Mask>() != null) sb.Append("<Mask>");
                if (t.GetComponent<RectMask2D>() != null) sb.Append("<RectMask2D>");
                if (t.GetComponent<Canvas>() != null) sb.Append("<Canvas>");
            }
            return sb.ToString();
        }

        /// <summary>
        /// HOW FAR THIS RECT HAS TO MOVE TO BE ON THE SCREEN AT ALL, in the parent's units (what
        /// <c>anchoredPosition</c> is denominated in). 0 when it already is, and 0 is the common case — a
        /// clamp that nudges a widget that was fine is a widget that drifts.
        ///
        /// The bound is the ROOT canvas' own rect. For a ScreenSpaceOverlay canvas that rect IS the screen in
        /// pixels; for a ScreenSpaceCamera one it is the camera's frustum at the canvas plane, which is also
        /// the screen. Either way it is the region a pointer position can land in, which is the only question
        /// that matters here. A nested canvas would be a sub-rect of it, hence <c>rootCanvas</c>.
        /// </summary>
        private float ClampOntoScreen(RectTransform me)
        {
            var canvas = me.GetComponentInParent<Canvas>();
            var screen = canvas == null || canvas.rootCanvas == null
                ? null
                : canvas.rootCanvas.transform as RectTransform;
            if (screen == null) return 0f;

            me.GetWorldCorners(Corners);
            float myBottom = Mathf.Min(Corners[0].y, Corners[3].y);
            float myTop = Mathf.Max(Corners[1].y, Corners[2].y);
            screen.GetWorldCorners(Corners);
            float screenBottom = Mathf.Min(Corners[0].y, Corners[3].y);
            float screenTop = Mathf.Max(Corners[1].y, Corners[2].y);

            float world = ShiftOntoScreen(myBottom, myTop, screenBottom, screenTop);
            if (world == 0f) return 0f;
            var parent = me.parent;
            float scale = parent == null ? 1f : Mathf.Abs(parent.lossyScale.y);
            return scale > 1e-5f ? world / scale : world;
        }

        /// <summary>
        /// THE ARITHMETIC, ON ITS OWN, IN WORLD UNITS — pure, so RailCheck L220 arm (f) can EXECUTE it on
        /// the numbers the live logs actually reported instead of scanning for a call that proves nothing.
        ///
        /// Returns the vertical shift that puts [<paramref name="myBottom"/>, <paramref name="myTop"/>]
        /// inside [<paramref name="screenBottom"/>, <paramref name="screenTop"/>]: 0 if it already is, the
        /// smallest move otherwise. A rect TALLER than the screen has no shift that satisfies both edges —
        /// its top is pinned, because the top of this button is the half that carries the caption.
        /// </summary>
        internal static float ShiftOntoScreen(float myBottom, float myTop, float screenBottom, float screenTop)
        {
            if (myBottom >= screenBottom && myTop <= screenTop) return 0f;
            if (myTop - myBottom > screenTop - screenBottom) return screenTop - myTop;
            return myBottom < screenBottom ? screenBottom - myBottom : screenTop - myTop;
        }

        /// <summary>
        /// The gap between two stacked HUD buttons, TAKEN FROM THE GAME rather than chosen by us: if the row
        /// is driven by a layout group — and the live measurement says it is, since End Turn reads back with
        /// point anchors at the parent's top-left corner and zero inset on both axes, which is exactly what
        /// <c>LayoutGroup.SetInsetAndSizeFromParentEdge</c> writes — then <c>spacing</c> IS the game's own
        /// answer to "how far apart does this HUD put two buttons", and it is already denominated in the
        /// parent's local units, the same space <c>anchoredPosition</c> lives in. No conversion, no constant.
        ///
        /// <see cref="Gap"/> survives only as the hand-placed-row fallback.
        /// </summary>
        private float NativeSpacing(float height, out string from)
        {
            var group = Source.parent == null
                ? null
                : Source.parent.GetComponent<HorizontalOrVerticalLayoutGroup>();
            if (group != null && group.spacing > 0f)
            {
                from = "native " + group.GetType().Name + ".spacing";
                return group.spacing;
            }
            from = "no layout group, fallback " + Gap + " x height";
            return height * Gap;
        }

        /// <summary>
        /// THE FOURTH LIAR, AND THE ONE THAT SURVIVED THREE FIXES: the RECT IS NOT WHAT THE BUTTON DRAWS.
        ///
        /// The previous attempt is arithmetically correct and still looked wrong, which is the whole clue. Its
        /// own log: End Turn's rect spans 0..-60 in the parent's units and the clone was placed at -90..-150 —
        /// thirty units of daylight between two rects that testers nevertheless saw touching. Because the End
        /// Turn button does not stop at its rect. Its cloned subtree (same log) is
        /// <c>shadow&lt;Tactical_FeatheredCircle&gt;</c>, <c>underlight&lt;UI_Gradient_Round&gt;</c> and
        /// <c>MainButton_Sidelight_Glow</c> — child rects that deliberately bleed past the frame. Measuring the
        /// PARENT rect and clearing that is measuring the one box the art was never inside.
        ///
        /// So clear what the row RENDERS: the lowest world-space corner of the End Turn button AND of every
        /// other live child of the shared parent, descendants included. That covers both ways this can be
        /// wrong at once — the art that overhangs End Turn, and any native sibling sitting below it that a
        /// measurement taken against End Turn alone would never have seen. Both are read off real rects at
        /// their settled size; nothing here is a number anyone picked.
        ///
        /// Returns the EXTRA drop on top of the one-height-plus-spacing step the caller already applies, so a
        /// row whose lowest drawn point is just End Turn's own rect bottom returns 0 and the placement is
        /// unchanged.
        /// </summary>
        private float ExtraClearance(RectTransform me, float height, out string from)
        {
            var parent = Source.parent;
            float rectBottom = WorldBottom(Source);
            float drawnBottom = SubtreeBottom(Source);
            string lowest = Source.name;
            if (parent != null)
            {
                for (int i = 0; i < parent.childCount; i++)
                {
                    var child = parent.GetChild(i) as RectTransform;
                    // Skip OURSELVES (we are a sibling too — measuring our own rect here would walk the clone
                    // further down every single frame) and anything the game currently has switched off.
                    if (child == null || ReferenceEquals(child, me)) continue;
                    if (!child.gameObject.activeInHierarchy) continue;
                    float b = SubtreeBottom(child);
                    if (b < drawnBottom) { drawnBottom = b; lowest = child.name; }
                }
            }

            float scale = parent == null ? 1f : Mathf.Abs(parent.lossyScale.y);
            if (scale <= 1e-5f) scale = 1f;
            float extra = (rectBottom - drawnBottom) / scale;

            // ponytail: capped at one button height. A feathered glow is a bounded thing, but a future prefab
            // with a full-screen decorative child under this parent would otherwise push the clone off the
            // bottom of the HUD — a worse failure than the overlap this fixes. Raise the cap if a real prefab
            // ever needs more than one button of clearance.
            float capped = Mathf.Clamp(extra, 0f, height);
            from = "lowest drawn by '" + lowest + "', raw=" + extra + " capped=" + capped;
            return capped;
        }

        /// <summary>Reusable buffer for the non-allocating <c>GetComponentsInChildren</c> overload. This runs
        /// every LateUpdate, so the allocating overload would drop a fresh array per sibling per frame.</summary>
        private static readonly List<RectTransform> Scratch = new List<RectTransform>();

        /// <summary>The lowest point this rect or anything under it actually DRAWS at, in world units. Active
        /// children only — an inactive child's corners are wherever the layout last left them and would pin the
        /// row against art nobody can see.</summary>
        private static float SubtreeBottom(RectTransform root)
        {
            float bottom = WorldBottom(root);
            root.GetComponentsInChildren(false, Scratch);
            for (int i = 0; i < Scratch.Count; i++)
            {
                float b = WorldBottom(Scratch[i]);
                if (b < bottom) bottom = b;
            }
            Scratch.Clear();
            return bottom;
        }

        /// <summary>Lowest rendered edge in world units. Corners are BL, TL, TR, BR — both bottom corners are
        /// read so a rotated rect still reports its true lowest point.</summary>
        private static float WorldBottom(RectTransform rt)
        {
            rt.GetWorldCorners(Corners);
            return Mathf.Min(Corners[0].y, Corners[3].y);
        }

        /// <summary>
        /// The button's height IN THE UNITS <c>anchoredPosition</c> IS DENOMINATED IN, which is the parent's
        /// local space — the one number this whole placement turns on, and the one every earlier attempt got
        /// from a field that could not supply it.
        ///
        /// <c>GetWorldCorners</c> is used instead of <c>rect</c>/<c>sizeDelta</c> because it is the only
        /// reading that is true under EVERY anchor mode: with stretched anchors <c>sizeDelta</c> is an INSET
        /// (it can read 0 or negative under a visibly tall button), and <c>rect</c>, while correct, is
        /// expressed in the button's own space and so is off by its <c>localScale</c> the moment the prefab is
        /// not drawn at 1:1. Corners are world-space, after every scale in the chain; dividing by the PARENT's
        /// <c>lossyScale</c> lands exactly in the parent's units.
        /// </summary>
        private static float ParentLocalHeight(RectTransform rt)
        {
            float world = WorldHeight(rt);
            var parent = rt.parent;
            float scale = parent == null ? 1f : Mathf.Abs(parent.lossyScale.y);
            // A degenerate parent scale would divide the offset into infinity and fling the clone off-screen;
            // the un-converted height at least keeps it on the button's own row.
            return scale > 1e-5f ? world / scale : world;
        }

        /// <summary>Rendered height in world units. Corners are BL, TL, TR, BR.</summary>
        private static float WorldHeight(RectTransform rt)
        {
            rt.GetWorldCorners(Corners);
            return (Corners[1] - Corners[0]).magnitude;
        }
    }

    /// <summary>
    /// The clone's one seam: the End Turn module's own <c>Awake</c>, which is where the native code itself
    /// first treats <c>Button</c> as linked (UIModuleEndTurnContainer.cs:32-35). Once per battle, on every
    /// peer, and a no-op outside a co-op session.
    /// </summary>
    [HarmonyPatch(typeof(UIModuleEndTurnContainer), "Awake")]
    internal static class EndTurnContainerAwakePatch
    {
        private static void Postfix(UIModuleEndTurnContainer __instance) => TacticalReadyButton.Build(__instance);
    }
}
