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
    /// THE GREEN IS OURS, DELIBERATELY, and the reason is in the game's own code. PP's colour mechanism is
    /// <c>UIInteractableColorController.GetColor</c> — a def-driven palette (idle/hover/pressed/disabled)
    /// with no "toggled on" concept reachable without a <c>ToggleButton</c> plus a new
    /// <c>UIInteractableColorsDef</c>, and none of its six colours is green. Worse, it RE-ASSERTS its
    /// colour on every visual-state change (<c>PhoenixGeneralButton.Update</c> → <c>UpdateColorElements</c>),
    /// so a tint written onto the button's own Image is reverted by the next hover. So the ready state is
    /// a child Image at sibling index 0 — above the frame, under the label, raycast off — which nothing
    /// native touches and which leaves every native hover/press animation intact.
    /// </summary>
    internal static class TacticalReadyButton
    {
        /// <summary>Every way this clone can display a string — <c>UnityEngine.UI.Text</c>, TMP, anything
        /// with a settable <c>text</c> property. Resolved once per battle by shape rather than by type, so
        /// the label can never silently come out blank because the prefab uses the other text stack.</summary>
        private static readonly List<Action<string>> _setters = new List<Action<string>>();
        private static Image _green;
        private static bool _loggedBuildFailure;

        /// <summary>Sibling index (under the clone's root) of the shallowest label-bearing child — where the
        /// ready tint is inserted so it lands above the frame and below the caption. -1 = no label found.</summary>
        private static int _labelDepth = -1;

        /// <summary>Battle teardown: the clone died with the tactical scene, so drop every handle to it.
        /// A retained setter would write into a destroyed object on the next battle's first repaint.</summary>
        internal static void Forget()
        {
            _setters.Clear();
            _green = null;
            _labelDepth = -1;
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
                // do anything. The native hotkey lives on the module rather than on this subtree, so this is
                // belt on top of braces. DISABLED, never destroyed: RailCheck L67 bans Object.Destroy from
                // this namespace outright (v1 destroyed evacuating actors and wedged the battle summary), and
                // a namespace-wide ban is worth more than the one line it costs here.
                foreach (var h in go.GetComponentsInChildren<HotkeyController>(true)) h.enabled = false;

                PlaceBelow(template.GetComponent<RectTransform>(), go.GetComponent<RectTransform>());
                WireClick(go);
                CollectLabels(go);      // before the overlay: the overlay's depth is derived from the labels'
                BuildGreenOverlay(go);
                Repaint();
                Debug.Log("[Multiplayer][tac] co-op ready button built under the native End Turn button (" +
                          _setters.Count + " label target(s)). Cloned subtree: " + Describe(template.transform));
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

        /// <summary>Gap between the native button's bottom edge and ours. One value, so "its own row" is
        /// a number and not a feeling.</summary>
        private const float RowGapPx = 6f;

        /// <summary>
        /// STRICTLY BELOW, AND NATIVE UI MOVES FOR NOBODY.
        ///
        /// WHY THE LAST TWO ATTEMPTS LANDED ON TOP OF END TURN AND LOST THE FRAME — one cause, both symptoms.
        /// Both wrote the clone's rect ONCE, from inside <c>UIModuleEndTurnContainer.Awake</c>. At that instant
        /// the canvas has not laid the row out yet, so <c>src.rect.height</c> is still the raw prefab number;
        /// for a button whose size is driven by the row's layout group that number is ~0. Subtracting ~0 puts
        /// the clone ON the native button (symptom 1). And the previous fix's <c>ignoreLayout = true</c> then
        /// FROZE the clone at that same collapsed prefab size, so the frame Image — a sliced sprite that fills
        /// the button rect — had no rect left to fill and drew nothing, while the caption, which sits on its
        /// own child rect, kept drawing: "text floating in the air" (symptom 2). The group diagnosis was right;
        /// opting out and freezing was the wrong half of the fix.
        ///
        /// SO MIRROR THE SOURCE INSTEAD OF COPYING IT ONCE (<see cref="TacticalReadyRowFollower"/>). Every
        /// LateUpdate the clone takes the native button's CURRENT anchors, pivot, scale and sizeDelta and sits
        /// one height + <see cref="RowGapPx"/> below its current anchoredPosition. The clone is then rect-
        /// identical to End Turn — same frame art at the same size, drawn by the same prefab components — in
        /// its own row, at every resolution, however late the layout settles.
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
            follower.GapPx = RowGapPx;
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
        }

        /// <summary>The ready tint, stretched over the whole button and DEPTH-PLACED FROM THE REAL SUBTREE
        /// rather than guessed: directly beneath the shallowest label-bearing child, i.e. above every frame /
        /// background child and below the caption. Sibling index 0 (the previous choice) is the BOTTOM of the
        /// draw order, so an opaque frame child would have hidden the tint completely — and the prefab's child
        /// names are asset-only, so the order has to be derived at runtime, not assumed.</summary>
        private static void BuildGreenOverlay(GameObject go)
        {
            var overlay = new GameObject("MP_ReadyGreen", typeof(Image));
            var rt = overlay.GetComponent<RectTransform>();
            rt.SetParent(go.transform, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.SetSiblingIndex(_labelDepth < 0 ? go.transform.childCount - 1 : _labelDepth);
            _green = overlay.GetComponent<Image>();
            _green.color = new Color(0.16f, 0.70f, 0.24f, 0.55f);
            _green.raycastTarget = false;
            _green.enabled = false;
        }

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
            string label = (mine ? "READY " : "READY? ") + TacticalReadySync.ReadyCount + "/" +
                           TacticalReadySync.TotalCount;
            for (int i = 0; i < _setters.Count; i++) _setters[i](label);
            if (_green != null) _green.enabled = mine;
        }
    }

    /// <summary>
    /// THE ROW. Keeps the cloned button rect-identical to the native End Turn button and exactly one row
    /// below it, every frame, forever.
    ///
    /// A ONE-SHOT COPY CANNOT DO THIS, and that is the whole reason this component exists. The clone is built
    /// from <c>UIModuleEndTurnContainer.Awake</c>, before the canvas has laid the row out: the numbers read
    /// there are raw prefab values (a layout-driven button measures ~0 high), which is how the last two
    /// attempts put the clone ON the native button and left it collapsed to a size with no room for the frame
    /// art. Reading in LateUpdate reads the size the layout actually settled on.
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

        /// <summary>Gap between the native button's bottom edge and ours, so "its own row" is a number.</summary>
        internal float GapPx = 6f;

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
            me.anchoredPosition = Source.anchoredPosition - new Vector2(0f, Source.rect.height + GapPx);
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
