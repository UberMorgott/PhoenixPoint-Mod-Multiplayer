using System.Collections.Generic;
using Base.Core;
using Base.Serialization;
using Base.UI.MessageBox;
using Multiplayer.Harmony;
using Multiplayer.Net;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Transport;
using Multiplayer.Util;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Multiplayer.UI
{
    public class MultiplayerUI : MonoBehaviour
    {
        public static MultiplayerUI Instance { get; private set; }

        // ─── Minimal mod canvas — IN-GAME STATUS BAR ONLY ──────────────────
        // The lobby + save-picker now clone NATIVE widgets and parent under the game's
        // own main-menu Canvas (captured in OnMenuReady), so they need no overlay canvas.
        // The in-game status bar, however, is an in-GEOSCAPE element: the menu Canvas dies
        // on the menu→geoscape scene load, so the bar keeps a tiny dedicated overlay Canvas
        // that lives with ModGO. This is the only remaining from-code canvas.
        private Canvas _barCanvas;
        private Transform _barRoot;
        // Kept so Update() can re-apply the aspect-adaptive match: this canvas lives for the WHOLE
        // process, so a match computed once at build is stale the moment the player changes resolution
        // or goes windowed. Mirrors LobbyPanel._scaler.
        private CanvasScaler _barScaler;

        // ─── In-game bottom bar ─────────────────────────────────────────────
        private GameObject _inGameBar;
        private Text _barStatusText;

        // ─── Co-op player panel (name | ping | status), both scenes ─────────
        private PlayerPanel _playerPanel;
        private CountdownPanel _countdownPanel;
        private PrepJoinButton _prepJoinButton;

        // ─── Lobby panel (built once the menu Canvas is captured) ───────────
        private LobbyPanel _lobby;
        // …and the CREATE-or-JOIN gate that now sits in front of it. Same lifecycle, same canvas band,
        // same build moment; only one of the two ever owns the menu chrome (see NetworkGatePanel).
        private NetworkGatePanel _gate;
        private bool _panelsBuilt;

        // The lobby lifecycle FSM + start gate (single source of truth; pure logic). The UI reads its
        // State/CanStart and drives transitions (BeginHost/CommitStart/Reset); it never lets the
        // Play-button visual be the sole start guard (that was Bug B).
        private readonly LobbyController _lobbyController = new LobbyController();
        public LobbyController Lobby => _lobbyController;

        // ─── Client-join connecting state ───────────────────────────────────
        // True from the moment a CLIENT smart-join is kicked off (OnLobbyJoin) until that attempt
        // resolves — confirmed (first host PEER_LIST arrives, Update opens the lobby), failed/timed
        // out (OnConnectionFailed), or cancelled (the connecting box's CANCEL button). It gates the
        // optimistic lobby Show: while it is true the lobby stays HIDDEN behind a native
        // "Connecting…" box, so a client never sees a fake empty lobby for a join that hasn't been
        // accepted yet. The HOST path never sets this (hosting is instant/local).
        private bool _clientConnecting;
        // The native "Connecting to host…" MessageBox shown during _clientConnecting, kept so we can
        // dismiss it programmatically (ForceCloseAllPrompts) on confirm/fail/timeout. On user CANCEL
        // the box closes itself and fires its callback.
        private MessageBox _connectingBox;
        // Realtime stamp of the CURRENT cascade stage's start. NOTHING else bounds the wait between
        // "the transport says connected" and "the host accepted us": SteamTransport declares
        // ConnectionState.Connected the instant Connect() returns (Steam P2P has no handshake to fail —
        // SteamTransport.cs:392-397), and a one-way UDP path lets STUN's punch succeed while the host's
        // replies never come back. Either way no ConnectionState.Failed is ever raised, so
        // OnConnectionFailed never runs, so the cascade never advances and the box sits on
        // "Connecting to host…" forever — the 2026-08-07 ZeroTier report, where the host could see the
        // joiner arrive and the joiner saw nothing, three attempts in a row with no error and no timeout.
        // Longer than the longest transport-level connect it has to contain (DirectTransport's
        // ConnectTimeoutMs = 10 s) so this only ever fires for a stage that hangs BEYOND its own bound.
        private const float JoinStageTimeoutSec = 20f;
        private float _joinStageStartedAt;

        // ─── Client-join cascade ────────────────────────────────────────────
        // The ordered transports to try for the current join (JoinPlan.Build), pinned by how the join was
        // started: a unified code from an accepted Steam INVITE cascades Steam → Direct → STUN, the same
        // code PASTED is Direct → STUN with no Steam leg, and a legacy code is a single attempt of its own
        // transport. So this is at most a fallback WITHIN one technology, never across them.
        // _joinAttemptIndex is the stage in
        // flight; on a stage failure OnConnectionFailed advances to the next instead of surfacing the
        // error, so only the LAST stage's failure reaches the user. Cleared on confirm/cancel/final-fail.
        private List<JoinAttempt> _joinPlan;
        private int _joinAttemptIndex;

        // True while the lobby overlay is on-screen. Consumed by the main-menu Escape patch
        // (MainMenuPatches) so Escape leaves the lobby instead of opening the Options menu.
        public bool IsLobbyOpen => _lobby?.IsVisible == true;

        /// <summary>True while ANY mod page owns the main menu — the create/join gate, the join screen, or
        /// the lobby. The Escape/Cancel suppression patches gate on THIS, not on the lobby alone: a native
        /// back-navigation firing under one of our full-screen pages tears the menu down beneath it.</summary>
        public bool IsModScreenOpen => IsLobbyOpen || _gate?.IsVisible == true;

        /// <summary>Escape / the back gesture on a gate screen: go back ONE screen (join → gate → main
        /// menu). False when no gate screen is open, so the caller falls through to its own rule (the
        /// lobby's is to swallow the press — LEAVE is its only exit).</summary>
        public bool BackOneScreen() => _gate != null && _gate.Back();

        private void Awake()
        {
            Instance = this;

            // Bar canvas first so the in-geoscape status bar has a valid render root.
            EnsureBarCanvas();
            CreateInGameBar();
            _inGameBar.SetActive(false);

            // The co-op player panel shares that canvas: it is the mod's only persistent render root, so
            // the panel outlives every scene load and simply re-pins itself (tactical → under the ready
            // button, geoscape → the right edge). Built hidden; Update drives it.
            _playerPanel = gameObject.AddComponent<PlayerPanel>();
            _playerPanel.Attach(EnsureBarCanvas());

            // Same canvas, same reason: the deployment countdown must draw over WHATEVER is on screen —
            // geoscape, research tree, deployment roster — without a view-state transition of any kind.
            _countdownPanel = gameObject.AddComponent<CountdownPanel>();
            _countdownPanel.Attach(EnsureBarCanvas());

            // Same canvas again, and AFTER the countdown panel: that one adds the GraphicRaycaster this
            // canvas needs before any click on it can reach a handler at all.
            _prepJoinButton = gameObject.AddComponent<PrepJoinButton>();
            _prepJoinButton.Attach(EnsureBarCanvas());

            // Panels are built lazily in OnMenuReady (they need the native menu Canvas).
            _lobby = new LobbyPanel(this);
            _gate = new NetworkGatePanel(this);

            WireSteamInvite();
        }

        // One-time wiring of the Steam invite/join subsystem. Registered here (persistent ModGO) so an
        // invite accepted from the MAIN MENU — before the co-op lobby is even opened — still routes into
        // the join flow. The join callback reuses OnLobbyJoin (full client join + OnConnectionFailed
        // diagnostics); the report callback surfaces every stage (errors → native message box, never a
        // silent no-op). Idempotent: SteamInvite.RegisterJoinHandlers self-guards against re-subscribe.
        private void WireSteamInvite()
        {
            // THE WHOLE OF THIS RUNS FROM Awake, WHERE A THROW KILLS THE MOD — not a feature, the mod.
            // Every line of the core below names SteamInvite, whose Lobby? statics cannot resolve without
            // Facepunch.Steamworks.Win64 (absent on GOG/Epic), and the failure is raised while COMPILING
            // the method that names it — surfacing at that method's call site, one frame up. So the
            // naming lives in a NoInlining core and the catch lives out here, where the failure actually
            // lands (the ClientIdentity.cs:82-97 shape). Guarding the three UI readers was worth nothing
            // while this could take the mod down before any UI existed.
            //
            // Where Facepunch IS present the core runs exactly as before, in the same order — the try
            // adds no branch to the happy path.
            try
            {
                WireSteamInviteCore();
            }
            catch (System.Exception e)
            {
                // Same latch the probes use: if the wiring never attached, every Steam surface is off for
                // this run and should stop asking. Logged once, there.
                SteamProbe.Latch(e);
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void WireSteamInviteCore()
        {
            // JoinOrigin.SteamInvite: this callback is reached ONLY by an accepted Steam invite (overlay,
            // friends-list "Join Game", rich presence, or the cold-start "+connect_lobby"), so the user
            // has explicitly chosen Steam and the plan keeps its Steam-first order.
            SteamInvite.OnJoinResolved = joinString => OnLobbyJoin(joinString, JoinOrigin.SteamInvite);
            SteamInvite.Report = (msg, isError) =>
            {
                if (!isError) return; // successes already reach Player.log inside SteamInvite
                var mb = GameUtl.GetMessageBox();
                // On dismiss, put the player back on whatever mod page the box was covering. A friends-list
                // row that fails to enter the lobby is raised through here while the gate is hidden behind
                // the box, and without this the player would be left staring at the bare main menu.
                mb?.ShowSimplePrompt("Steam invite: " + msg, MessageBoxIcon.Error,
                    MessageBoxButtons.OK,
                    delegate (MessageBoxCallbackResult _) { ReturnFromNativeBox(); }, this);
            };
            // Lifecycle hooks: EVERY teardown path routes through NetworkEngine.Shutdown/TearDown,
            // which invoke these to leave the invite lobby + clear rich presence (canonical Valve
            // lifecycle — rich presence never auto-clears while the game keeps running). The joinable
            // gate is driven from the engine's peer connect/disconnect hooks (session full ↔ freed).
            NetworkEngine.SteamLobbyCleanup = SteamInvite.LeaveSteamLobby;
            NetworkEngine.SteamLobbySetJoinable = SteamInvite.SetLobbyJoinable;
            SteamInvite.RegisterJoinHandlers();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Menu hook — native Canvas captured by InjectNetworkButtonPatch
        // ═══════════════════════════════════════════════════════════════════

        // Called from InjectNetworkButtonPatch.Postfix once the main-menu Canvas + native
        // button template are captured. Builds the lobby + save-picker as cloned NATIVE
        // widgets parented to the menu's own Canvas (native look + correct CanvasScaler/sort).
        public void OnMenuReady(Canvas menuCanvas)
        {
            if (menuCanvas == null) return;
            if (!_panelsBuilt)
            {
                _lobby.Build(menuCanvas);
                _gate.Build(menuCanvas);
                _panelsBuilt = true;

                // Cold start: if the game was launched by accepting a Steam invite ("+connect_lobby <id>"),
                // kick the join now that the menu + lobby panel are ready (OnLobbyJoin needs them).
                // Through the probe — this is the menu-build path, and a throw here would cost the lobby
                // and the gate, not just the cold start.
                SteamProbe.HandleColdStart();
            }

            // F3 — the session died UNDER this client (host quit, crashed, or went silent). SessionEnd
            // has already run: level finished, engine torn down, lobby FSM reset, and the player is
            // being dropped at a bare main menu with nothing to do but click NETWORK GAME again. Click
            // it for them.
            //
            // A KICKED CLIENT LANDS ON THE GATE, NOT IN A HOST LOBBY OF ITS OWN. This used to be the
            // host-and-open-lobby path, because that was the only thing the button did. Now that the
            // button asks first, auto-hosting here would be putting words in the player's mouth: they
            // never chose to host, and hosting is not free — it publishes a friends-only Steam lobby,
            // lights a "Join Game" on their friends list and asks the router for a UPnP mapping, all for
            // someone whose actual intent was to play in THAT friend's game. The gate is one click from
            // either answer, including "rejoin them", so nothing is lost and nothing is presumed.
            //
            // CONSUMED HERE and nowhere earlier because this is the first moment the menu exists again:
            // Show() hides the native menu chrome, and there is no chrome to hide (nor a restore to
            // pair with) until Init has rebuilt it. One-shot on the handler's side, so a voluntary
            // leave, a host closing its own lobby, a quit, and the campaign-end path never reach this.
            if (HostLeaveHandler.ConsumeLobbyReopen())
            {
                MpLog.Log("[Multiplayer] F3: main menu is back — reopening the network create/join gate.");
                ShowNetworkMenu();
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Minimal overlay Canvas for the in-game status bar
        // ═══════════════════════════════════════════════════════════════════

        private Transform EnsureBarCanvas()
        {
            if (_barRoot != null) return _barRoot;

            var go = new GameObject("MultiplayerBarCanvas");
            // Parent under ModGO so the Canvas shares the mod's persistent lifetime.
            go.transform.SetParent(transform, false);

            _barCanvas = go.AddComponent<Canvas>();
            _barCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _barCanvas.sortingOrder = 5000;

            // Responsive: native aspect-adaptive scaler (match WIDTH <=16:9 / HEIGHT >16:9), replacing
            // the old fixed match=0.5, so the status bar scales consistently with the lobby/picker.
            _barScaler = go.AddComponent<CanvasScaler>();
            LobbyTheme.ConfigureScaler(_barScaler);

            _barRoot = go.transform;
            return _barRoot;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Co-op loading overlay (own ScreenSpaceOverlay canvas, sortingOrder 7000)
        // ═══════════════════════════════════════════════════════════════════

        // The overlay is OWNED by MultiplayerMain (added to the same ModGO at mod enable) and is fully
        // state-driven: its own Update decides Show/Hide from LoadOverlayVisibility.ShouldShow. So there
        // is no Show side here at all — only this immediate Hide, which the teardown paths call to drop
        // it in the same frame instead of waiting for the next Update tick. Resolved by lookup, never by
        // AddComponent: a second instance would draw a second set of rows.
        private LoadOverlayController _loadOverlay;

        public void HideLoadOverlay()
        {
            if (_loadOverlay == null) _loadOverlay = GetComponent<LoadOverlayController>();
            if (_loadOverlay != null) _loadOverlay.Hide();
        }

        /// <summary>
        /// CLIENT: on the first received save chunk, enter the game's NATIVE loading screen for the
        /// download IMMEDIATELY (instead of sitting in the lobby with only the top-right plaque). Drops
        /// the native curtain (full-screen loading page), begins driving its bottom bar with the download
        /// %, and hides the lobby behind it. The bar hands off to the real level-load progress at phase-2
        /// (SaveTransferCoordinator.SetLoadingLevel). The top-right plaque still shows on top (sort 7000)
        /// as secondary per-peer detail. Idempotent per transfer (the coordinator guards the first-chunk call).
        /// </summary>
        public void EnterDownloadLoadingScreen()
        {
            DropCurtainEarly();                                        // full-screen native loading page
            NativeWidgetFactory.BeginDownloadBar("Downloading save…"); // drive the bottom bar with download %
            _lobby?.HideForNativeScreen();                            // lobby out of the way, session intact
        }

        /// <summary>
        /// CLIENT never-silent: the save transfer failed WHILE the native loading screen is up (bad blob /
        /// checksum / prepare fail). Lift the curtain + hide the plaque so the player is not stranded on a
        /// stuck bar, then surface the staged failure via the native message box and return to the lobby
        /// (the session is still connected; the host owns the straggler timeout).
        /// </summary>
        public void OnClientTransferFailed(string stage)
        {
            LiftCurtainEarly();
            HideLoadOverlay();
            var mb = GameUtl.GetMessageBox();
            if (mb != null)
            {
                _lobby?.HideForNativeScreen();
                mb.ShowSimplePrompt($"Save transfer failed ({stage}). Returning to the lobby.",
                    MessageBoxIcon.Warning, MessageBoxButtons.OK,
                    delegate (MessageBoxCallbackResult _) { _lobby?.Show(); }, this);
            }
            else
            {
                _lobby?.Show();
            }
        }

        // Force the native curtain down early so phase-1 (download) shows under a vanilla-style
        // loading screen. Resolved dynamically; on any failure we fall back to the overlay's own
        // opaque backdrop (the overlay panel already paints a dark background).
        private void DropCurtainEarly()
        {
            try
            {
                var t = HarmonyLib.AccessTools.TypeByName("Base.Utils.LevelSwitchCurtainController");
                if (t == null) return;
                var ctrl = UnityEngine.Object.FindObjectOfType(t);
                if (ctrl == null) return;
                var m = HarmonyLib.AccessTools.Method(t, "DropCurtainInstant", new System.Type[0]);
                m?.Invoke(ctrl, null);
            }
            catch (System.Exception e)
            {
                MpLog.LogWarning("[Multiplayer] Early curtain drop failed (fallback to overlay backdrop): " + e.Message);
            }
        }

        // Reverse of DropCurtainEarly: lift the native curtain back up. Used only when a committed start
        // FAILED downstream (no scene load happens, so the native Loaded→Playing lift never fires) and
        // we must restore the lobby to its pre-press visible state. Best-effort + dynamic, like the drop.
        private void LiftCurtainEarly()
        {
            try
            {
                var t = HarmonyLib.AccessTools.TypeByName("Base.Utils.LevelSwitchCurtainController");
                if (t == null) return;
                var ctrl = UnityEngine.Object.FindObjectOfType(t);
                if (ctrl == null) return;
                var m = HarmonyLib.AccessTools.Method(t, "LiftCurtain", new System.Type[0]);
                m?.Invoke(ctrl, null);
            }
            catch (System.Exception e)
            {
                MpLog.LogWarning("[Multiplayer] Early curtain lift failed: " + e.Message);
            }
        }

        // ─── CLIENT tac load-phase curtain (in-game geo↔tac transitions) ────
        // The co-op loading INDICATOR for host-driven level loads (TacticalLoadPhaseSync). Mirrors
        // EnterDownloadLoadingScreen but WITHOUT the lobby hide (that is a menu-only concern — these fire
        // in-game where no lobby is shown) and with a caller-supplied label.

        /// <summary>Drop the native curtain + begin driving the bottom bar with a co-op loading label.
        /// Idempotent (DropCurtainEarly / BeginDownloadBar are).</summary>
        public void EnterTacLoadCurtain(string label)
        {
            DropCurtainEarly();
            NativeWidgetFactory.BeginDownloadBar(label);
            // 0x48 now also announces the LOBBY seams (host PLAY press, new-campaign arm), where the lobby
            // canvas sits on top of the native curtain and would leave the client looking at a live lobby
            // while every other peer loads. Gated on IsVisible so this stays the no-op it always was in-game
            // — HideForNativeScreen also runs RestoreMenuChrome, which has no business on a live geoscape.
            if (_lobby?.IsVisible == true) _lobby.HideForNativeScreen();
        }

        /// <summary>Abort: stop our bar AND lift the native curtain, because no native level-load will lift it
        /// (watchdog / disconnect / already-in-tactical). Never leaves the client stuck on a curtain.</summary>
        public void TacLoadAbort()
        {
            NativeWidgetFactory.EndDownloadBar();
            LiftCurtainEarly();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Main entry — called from button click
        // ═══════════════════════════════════════════════════════════════════

        public void ShowNetworkMenu()
        {
            if (NetworkEngine.Instance?.IsActive == true)
            {
                // Already in a session → just (re)show the live lobby panel.
                ShowLobbyScreen();
                return;
            }
            // NO SESSION YET → THE GATE, NOT A LOBBY. This used to run StartHostAndOpenLobby directly, so
            // one click on NETWORK GAME made you a host: a player who wanted to JOIN was looking at a page
            // announcing that they were hosting, with the join control buried in a card inside it. Nothing
            // is hosted now until CREATE SESSION is pressed (OnGateCreate), and JOIN SESSION opens a screen
            // whose whole job is joining.
            _gate?.ShowGate();
        }

        // ─── Gate callbacks (NetworkGatePanel → here) ──────────────────────

        /// <summary>
        /// EXACTLY ONE MOD PAGE OWNS THE MENU CHROME. Show() hides the native menu canvases and Hide()
        /// restores them, and they are a single global pair — so the outgoing page must always go down
        /// BEFORE the incoming one comes up, or the restore lands last and the native menu is drawn back
        /// over our page. Both halves are no-ops when their page was not open.
        /// </summary>
        private void ShowLobbyScreen()
        {
            _gate?.Hide();
            _lobby?.Show();
        }

        /// <summary>CREATE SESSION: today's host path, byte-for-byte unchanged — only the gate closing in
        /// front of it is new.</summary>
        internal void OnGateCreate()
        {
            _gate?.Hide();
            StartHostAndOpenLobby();
        }

        /// <summary>JOIN on the gate's join screen: hand the typed target to the EXISTING client join flow.
        /// The gate is taken down FOR A NATIVE SCREEN rather than closed, so an unreadable target or a
        /// failed connect can put the player straight back on it with their text still in the field.
        /// No input validation here on purpose — OnLobbyJoin classifies the text and raises its own error
        /// box, and a second check would be a second opinion to keep in step with SmartJoinParser.</summary>
        internal void OnGateJoin(string text)
        {
            // Same rule as the friends row (OnGateJoinFriend): the connect is asynchronous, so taking the
            // page down HERE would show the main menu until it lands. The gate holds with a connecting
            // line; the box sites below step it aside if the target turns out to be unreadable.
            _gate?.ShowConnecting(text);
            OnLobbyJoin(text ?? "", JoinOrigin.PastedCode);
        }

        /// <summary>
        /// A Steam friends-list row: route into the SAME join flow an accepted invite takes, with the
        /// host id the row already carries so no Steam round trip is needed to find it.
        ///
        /// THE PAGE IS NOT TAKEN DOWN HERE, and that is the fix for the main-menu bounce. This used to
        /// call HideForNativeScreen() up front and then hand off to an ASYNCHRONOUS resolve; with the
        /// gate gone and no lobby open yet, the player was left looking at the bare main menu for the
        /// whole resolve — measured at 10,0 s on the 2026-08-10 playtest (D:\PP-Instance3\Player.log
        /// 83,691 → 93,706). The gate now HOLDS with a connecting line and ShowLobbyScreen closes it
        /// when the session is actually up. Nothing here waits on another PLAYER — only on the server's
        /// own reply — so the no-quorum mandate is untouched.
        /// </summary>
        internal void OnGateJoinFriend(ulong lobbyId, ulong hostId, string who)
        {
            _gate?.ShowConnecting(who);
            // Through SteamProbe, never SteamInvite directly: naming that type in THIS method's body is
            // what fails to JIT on a GOG/Epic box, and the throw lands at OnGateJoinFriend's call site
            // where no try of ours exists (SteamInvite.cs:393-408).
            SteamProbe.JoinFriendLobby(lobbyId, hostId);
        }

        /// <summary>
        /// The mirror of <see cref="ReturnFromNativeBox"/>, and it exists because that asymmetry was a bug.
        /// A native MessageBox draws on the menu's own canvases, which BOTH mod pages disable while they
        /// hold the chrome — so a box raised over either one is invisible until that page steps aside. The
        /// join path used to satisfy this by having its ENTRY point hide the gate, which is exactly what
        /// put the player on the main menu for the whole resolve. Now the page stays up, so the box sites
        /// themselves must step it aside. Both halves are no-ops when their page is not open (the gate
        /// restores only chrome it hid itself — NetworkGatePanel.ReleaseChrome).
        /// </summary>
        private void LeaveForNativeBox()
        {
            _lobby?.HideForNativeScreen();
            _gate?.HideForNativeScreen();
        }

        /// <summary>
        /// THE ONE "the native box is gone — put the player back where they were" hook. A join can now
        /// start from the GATE, where there is no session and therefore no lobby to return to: the old
        /// unconditional <c>_lobby.Show()</c> would open an EMPTY lobby for a session that does not exist.
        /// So: the live lobby when a session is active, else whichever gate screen was up, else the bare
        /// main menu — every branch a no-op when its page was never open.
        /// </summary>
        private void ReturnFromNativeBox()
        {
            if (NetworkEngine.Instance?.IsActive == true) ShowLobbyScreen();
            else { _lobby?.Hide(); _gate?.Reshow(); }
        }

        /// <summary>
        /// The join field's starting text, shared by the gate's join screen and the lobby's paste prompt so
        /// the two can never drift: a valid invite code already on the clipboard (the host clicked CODE) so
        /// the joiner only has to press JOIN, else the localhost default endpoint for the normal IP flow —
        /// but never a healthy host's OWN loopback, which it cannot join (blank instead).
        /// </summary>
        internal string JoinPrefill()
        {
            var clip = ClipboardText();
            // All THREE published forms, because the host publishes whichever is shortest for what it
            // has (GetSessionInviteCode): 8-symbol InviteCode, 10-symbol ConnectCode, 13/19 UnifiedCode.
            // Miss one and that host's code falls through to the 127.0.0.1 default the joiner must delete.
            var clipIsCode = !string.IsNullOrEmpty(clip)
                && (InviteCode.TryDecode(clip, out _)
                    || ConnectCode.Decode(clip) != null
                    || UnifiedCode.TryDecode(clip, out _, out _, out _, out _));
            return clipIsCode
                ? clip.Trim()
                : (IsHealthyDirectHost() ? "" : "127.0.0.1:" + SmartJoinParser.DefaultDirectPort);
        }

        // THE VERSION GATE IS NOT HERE, and cannot be. `CoopGuardBlocks` used to sit at this spot returning
        // false unconditionally (its ReflectionGuard stayed in the v1 quarry), and no amount of filling it in
        // could have gated a BUILD: both of its call sites run BEFORE any peer exists — one starts hosting,
        // the other parses the join code — so there is no second version to compare against yet. The real
        // gate lives where both versions are actually in hand: ParityManifest.GameVersion
        // (Base.Build.RuntimeBuildInfo.BuildVersion) rides the JOIN message and ParityComparer.Compare names
        // both builds in the roster diff, which is the same soft gate that already blocks READY — so a
        // mismatched peer is told at lobby time and the campaign never starts, rather than desyncing later.

        // Default DirectIP listen port for the host (matches the client connect default).
        private const int DefaultDirectPort = 14242;

        // CREATE (lobby-first): start hosting on Direct + STUN + Steam SIMULTANEOUSLY in one
        // session and open the lobby panel immediately. A joiner arriving by DirectIP, by STUN
        // invite-code, or by Steam all land in the same peer list (CompositeTransport namespaces
        // their ids). No campaign is loaded here — the host picks a save only after everyone is
        // ready (Play). CompositeTransport.Host wraps each child so one failing transport (e.g.
        // Steam unavailable) does not abort the others.
        private void StartHostAndOpenLobby()
        {
            NetworkEngine.Create();
            var composite = new CompositeTransport(new ITransport[]
            {
                new DirectTransport(),
                new StunTransport(),
                new SteamTransport()
            });
            NetworkEngine.Instance.Initialize(composite);
            NetworkEngine.Instance.OnConnectionFailed += OnConnectionFailed;
            NetworkEngine.Instance.StartHost(DefaultDirectPort);
            // Publish a friends-only Steam lobby advertising our SteamID64 so "Invite via Steam" (and the
            // friends-list Join Game) work. Fire-and-forget: the lobby is ready a beat later (reported via
            // SteamProbe.Report); the invite button below no-ops loudly until then. Harmless off Steam.
            //
            // THROUGH SteamProbe, AND THAT IS WHY HOSTING WORKS OFF STEAM AT ALL. This method used to name
            // SteamInvite here in its own body. SteamInvite holds Lobby? statics, so where Facepunch is
            // absent (GOG/Epic) the type cannot load, the JIT fails while compiling THIS method, and the
            // throw surfaces at StartHostAndOpenLobby's call site — before NetworkEngine.Create() above ever
            // ran. CREATE SESSION did nothing whatsoever for every non-Steam player. The shim keeps the name
            // out of this body (SteamInvite.cs:393-408, RailCheck L155 arm (g)).
            SteamProbe.HostPublish();
            // Steam-free reachability: ask the router (UPnP) to forward TCP+UDP 14242 to this PC so a
            // GOG/Epic host is directly joinable across the internet. Fire-and-forget off the main
            // thread; the unified invite code prefers the resulting WAN endpoint once it lands (rail
            // re-reads every frame). No-op / null when UPnP is unsupported. Torn down via UpnpPortMapper.Unmap
            // on the same NetworkEngine cleanup chokepoint that drops the Steam lobby.
            _ = UpnpPortMapper.TryMap();
            // Fresh host: drop any chosen-save carried over from a PRIOR session. Without this the
            // stale _pendingChosenSave (the rich transfer payload) survives a host→PLAY→leave→re-host
            // and PLAY could ship a save the new lobby's clients never chose. (The gate itself now keys
            // on the authoritative Session.ChosenSaveName, which is null on a fresh lobby because
            // Initialize() above builds a new SessionManager — the field is never carried over.)
            _pendingChosenSave = null;
            _lobbyController.Reset();      // fresh lobby (idempotent; clears any stale lock)
            LobbyCountdown.Reset();        // …and no number left over from the last one
            _lobbyController.BeginHost();
            ShowInGameBar();
            ShowLobbyScreen();   // gate down first, then the lobby up — one page owns the chrome
        }

        // ═══ Lobby panel callbacks ═════════════════════════════════════════

        // LEAVE button on the lobby panel: tear down the session and hide the lobby + bar.
        public void OnLobbyLeave()
        {
            _lobby?.Hide();
            OnDisconnectClicked();
        }

        // Ready toggle from the lobby panel — EVERY player has one now, the host included. The host used to
        // be excluded here ("it is the starter, not a ready-gated player"), which was true for exactly as
        // long as there was a PLAY button for it to press. There is not: readiness IS the start, so the
        // host's own ready is a term of LobbyController.CanStart and the countdown arms off it.
        public void OnLobbyToggleReady(bool ready)
        {
            var engine = NetworkEngine.Instance;
            if (engine?.Session == null) return;

            // Host: its ready lives on its own roster self-entry (SessionManager.HostReady) and needs no
            // packet — SetHostReady re-broadcasts the roster so every client sees it move. It also REFUSES
            // a ready with no save chosen, host-authoritatively.
            if (engine.IsHost) { engine.Session.SetHostReady(ready); return; }

            // Client → host ClientReady / ClientUnready per the local intent (keyed by sender; the host
            // broadcasts the updated roster, which closes the start gate again on an unready).
            engine.Session.SetClientReady(engine.LocalSteamId, ready);
        }

        // Parity soft-gate: the roster "!" badge's click-through detail — the exact host-computed diff
        // list. The lobby has no hover-tooltip mechanism; the native message box IS this mod's canonical
        // never-silent detail surface.
        public void OnLobbyShowParityDiffs(string diffs)
        {
            if (string.IsNullOrEmpty(diffs)) return;
            var mb = GameUtl.GetMessageBox();
            if (mb == null) return;

            // The native MessageBox renders BELOW the lobby overlay (sort 4000), so a box shown over
            // the OPEN lobby is fully occluded — invisible. The user, seeing nothing, re-clicks the
            // "!" badge; each click stacks another hidden ModalData in the MessageBox's
            // _modalPopupStack (ShowPromptImpl pushes the current box when one is already showing).
            // Those stacked boxes only become visible when the overlay tears down on lobby EXIT, then
            // pour out one-by-one (the "высыпались after exit" bug). Fix at the source by mirroring the
            // proven rename-prompt path: hide the lobby overlay first so the box shows through, then
            // re-show the lobby on dismiss — the FIRST click now gives immediate feedback, so nothing
            // ever queues to spill out later.
            _lobby?.HideForNativeScreen();
            mb.ShowSimplePrompt(
                "Mods / DLC / mod settings differ from the host:\n\n" + diffs +
                "\n\nREADY unlocks once these match the host.",
                MessageBoxIcon.Warning, MessageBoxButtons.OK,
                delegate (MessageBoxCallbackResult _) { _lobby?.Show(); }, this);
        }

        // Nickname edit committed in the lobby panel.
        public void OnLobbyRename(string newName)
        {
            if (string.IsNullOrWhiteSpace(newName)) return;
            NetworkEngine.Instance?.Session?.SendRename(newName.Trim());
        }

        // THE START. Transfers the already-chosen save (rail-selected) to every peer.
        //
        // NOTHING PRESSES THIS ANY MORE. It was the PLAY button's handler; the button is gone and its ONE
        // caller is now LobbyCountdown reaching zero (driven from Update below), which happens only while
        // LobbyController.CanStart is open — every player ready, the host included, and a save chosen. The
        // body is deliberately unchanged: the countdown was specified as "do exactly what pressing PLAY
        // does today", and the way to guarantee that is to keep doing it here rather than to grow a second
        // start path beside it. The press-time re-validation and the failure box below are therefore
        // defense-in-depth against a one-frame race rather than the everyday route they used to be.
        public void OnLobbyPlay()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsHost) return;

            // Press-time re-validation of the SAME start gate (defense-in-depth vs the stale-frame
            // race, Bug B H2): never trust the Play button's lit visual alone. Project the live roster
            // facts into the FSM through the SINGLE shared projection point (RefreshGateFacts — the
            // exact same one the Play-button VISUAL uses), then attempt CommitStart — which only
            // succeeds when >=1 client is connected and a save is chosen, and which LOCKS the lobby on
            // success so no mid-start join can race in. NO ready quorum — see LobbyController.
            RefreshGateFacts(out int clientCount, out bool saveChosen);

            if (_lobbyController.CommitStart())
            {
                // NOTE: do NOT show the load overlay here — there is deliberately no show entry point.
                // Its visibility is fully state-driven (LoadOverlayController.Update →
                // LoadOverlayVisibility.ShouldShow) and gates on the NATIVE load-start (LoadPhaseStarted =
                // curtain entered "Loading"), NOT on the command-time TransferActive. An eager show here
                // would only flash the overlay in the LOBBY one frame before any mission load began (the
                // reported bug). Curtain drop stays — phase-1 seamlessness.
                bool started;
                try
                {
                    // THE ENTER HALF, at the seam where the boundary is DECIDED. DropCurtainEarly below is
                    // the HOST's own screen; on its own it is exactly the asymmetry the user reported — the
                    // host curtains at the press and every client keeps a live, clickable lobby until its
                    // first save byte lands. Broadcast-and-go: no ack, no quorum, the very next statement is
                    // our own load.
                    //
                    // INSIDE THE TRY, and the ORDER IS UNCHANGED (L143 pins the announce order, not the
                    // handler). These two ran AFTER CommitStart() locked the lobby but OUTSIDE this try, so
                    // a throw from either escaped Update with State=Starting and IsLocked=true and
                    // ReopenAfterFailedStart — sitting right below, written for exactly this — could never
                    // be reached: the lobby was dead for the rest of the run.
                    engine.SaveTransfer?.BroadcastLoadBoundaryBegin("lobby-play");
                    DropCurtainEarly();           // phase-1 looks like one seamless vanilla load
                    started = engine.SaveTransfer?.HostStartSession(_pendingChosenSave) ?? false;
                }
                catch (System.Exception e)
                {
                    // The synchronous portion of the start threw AFTER the lobby was locked. Without this
                    // the exception would escape with the gate stuck false forever → dead lobby. Run the
                    // EXACT SAME reopen sequence as the started==false branch, log the cause, and swallow
                    // (the user is informed via the same warning box).
                    MpLog.LogError("[Multiplayer] the host start threw after the lobby locked: " + e);
                    ReopenAfterFailedStart();
                    return;
                }
                if (started) return;

                ReopenAfterFailedStart();
                return;
            }

            // Gate closed at press time. Tell the host exactly what is missing, reading the SAME
            // projected facts (no parallel rule) so the message can never contradict the gate.
            // NAME THE BLOCKER (L181). "All players must be ready" told the host nothing about WHICH row
            // was holding the button down — and when the row was a phantom that could never ready, it
            // named a player who was not there at all.
            string why = !saveChosen ? "Choose a save before starting."
                : clientCount < 1 ? "Wait for a player to join before starting."
                : "All players must be ready before starting — " +
                  LobbyController.PeersBlockedBy(NetworkEngine.Instance?.Session?.GetLobbyRoster()) + ".";
            var mb = GameUtl.GetMessageBox();
            if (mb != null)
            {
                _lobby?.HideForNativeScreen();
                mb.ShowSimplePrompt(why,
                    MessageBoxIcon.Warning, MessageBoxButtons.OK,
                    delegate (MessageBoxCallbackResult _) { _lobby?.Show(); }, this);
            }
        }

        /// <summary>
        /// Shared reopen path: the gate passed and we LOCKED the lobby, but the start could not proceed
        /// (HostStartSession returned false, or anything in the locked window threw). Reopen instead of
        /// leaving the lobby permanently dead-locked: unlock the FSM, undo the visible commit side effects
        /// (overlay + early curtain drop), re-show the lobby, and warn. The cached facts are intact, so the
        /// gate is satisfied again on reopen.
        ///
        /// A METHOD AND NOT A LOCAL FUNCTION, because the OTHER caller cannot reach a local one: the
        /// countdown dispatch in <c>Update</c> is what invokes the start nowadays, and a throw that got past
        /// <c>OnLobbyPlay</c> used to escape <c>Update</c> with the lobby locked and this recovery
        /// unreachable. Idempotent — <c>LobbyController.CancelStart</c> no-ops outside Starting, and both
        /// curtain halves are no-ops when nothing was dropped.
        /// </summary>
        private void ReopenAfterFailedStart()
        {
            _lobbyController.CancelStart();
            // AND DROP THE HOST'S OWN READY. Without this the reopened lobby is instantly back in the
            // state that armed the countdown — everybody ready, gate open — so it would re-arm, fail,
            // and stack another warning box, forever. Same mechanism the cancel uses, same reason:
            // the arm condition has to actually stop being true. The host re-readies when it has
            // decided what to do about the failure.
            NetworkEngine.Instance?.Session?.SetHostReady(false);
            HideLoadOverlay();
            LiftCurtainEarly();
            // …and the clients we curtained at the press must come back with us: LiftCurtainEarly is the
            // HOST's screen only, and no save transfer will ever start to lift theirs.
            NetworkEngine.Instance?.SaveTransfer?.BroadcastLoadBoundaryAbort("host start failed");
            var failBox = GameUtl.GetMessageBox();
            if (failBox != null)
            {
                _lobby?.HideForNativeScreen();
                failBox.ShowSimplePrompt("Failed to start session. Please try again.",
                    MessageBoxIcon.Warning, MessageBoxButtons.OK,
                    delegate (MessageBoxCallbackResult _) { _lobby?.Show(); }, this);
            }
            else
            {
                _lobby?.Show();
            }
        }

        // ─── Start-gate projection (THE single source of truth feed) ────────────────────────────
        // Project the authoritative lobby roster down to the primitive gate facts and push them into
        // the LobbyController FSM. This is the ONE place the non-host/all-ready rule is applied (via
        // LobbyController.AllClientsReady) — both the Play-button VISUAL (EvaluateStartGate) and the
        // press-time guard (OnLobbyPlay) call through here, so they can never derive a different gate.
        //   • clientCount      = LIVE non-host peers (LobbyController.IsLivePeer — host self-entry, a
        //                        PAUSED row and a row whose JOIN never arrived all excluded). It used to
        //                        count every non-host row, so a phantom stood in for the player the host
        //                        is not allowed to start without: the readiness fold skipped that row as
        //                        not-live and the count accepted it as present, and the two halves of one
        //                        gate disagreed about who was at the table (2026-08-07, L181).
        //   • saveChosen       = the AUTHORITATIVE broadcast value Session.ChosenSaveName is non-empty
        //                        (single source of truth; see the gate-vs-rail divergence note below).
        //   • allLivePeersReady = every LIVE non-host row has readied (owner ruling 2026-08-07: readiness
        //                        is a LOBBY gate; see LobbyController's class summary for the scope and
        //                        for why a PAUSED peer is excluded rather than counted-and-unready).
        // UpdateLobby ignores updates while the FSM is locked, so this is safe to call every frame.
        private void RefreshGateFacts(out int clientCount, out bool saveChosen)
        {
            var engine = NetworkEngine.Instance;
            var roster = engine?.Session?.GetLobbyRoster();

            clientCount = 0;
            if (roster != null)
                foreach (var p in roster)
                    if (LobbyController.IsLivePeer(p)) clientCount++;

            // SINGLE SOURCE OF TRUTH for "a save is chosen": the AUTHORITATIVE broadcast value
            // (Session.ChosenSaveName, set only by SetChosenSave which also broadcasts SetSave + drives
            // the rail), NOT the local _pendingChosenSave field. A stale _pendingChosenSave surviving a
            // re-host used to arm the gate while ChosenSaveName was null and the rail read "(none)" and
            // nothing was broadcast — gate, rail and broadcast could diverge. Keying the gate on the
            // broadcast value makes them converge: PLAY can never arm unless a save was really broadcast.
            saveChosen = !string.IsNullOrEmpty(engine?.Session?.ChosenSaveName);

            // THE HOST IS ONE OF THE PLAYERS THE GATE COUNTS (2026-08-10). Read off the SAME authoritative
            // roster as everything else — the host's self-entry carries Ready = SessionManager.HostReady —
            // so host and clients derive the identical fact from the identical row and there is no second,
            // drifting notion of "the host is ready". On a client the roster is the last PEER_LIST, which
            // is exactly what makes the client's footer say the same thing the host's does.
            bool hostReady = false;
            if (roster != null)
                foreach (var p in roster)
                    if (p != null && p.IsHost) { hostReady = p.Ready; break; }

            _lobbyController.UpdateLobby(clientCount, saveChosen,
                                         LobbyController.AllLivePeersReady(roster), hostReady);
        }

        // Refresh the FSM from the live roster, then return the FULL start gate (clients>=1 && all
        // clients ready && save chosen && HostLobby && !locked). The Play-button VISUAL calls this so
        // its enabled state matches the press-time gate on EVERY dimension — including save-chosen and
        // the post-commit lock — not just the all-ready dimension.
        public bool EvaluateStartGate()
        {
            RefreshGateFacts(out _, out _);
            return _lobbyController.CanStart;
        }

        // Same projection for NEW CAMPAIGN, which needs the CLIENTS' readiness half WITHOUT the chosen-save
        // half (a fresh campaign picks no save) and without the host's own ready (the host cannot ready
        // until a save is chosen, so folding it in would make this button unpressable forever). Reads
        // ClientsReady, which is byte-for-byte the rule this button had before the host gained a toggle —
        // the fresh-campaign route is deliberately untouched by that work. Both the button visual and
        // OnLobbyNewCampaign's press-time guard read THIS, so they cannot drift into two rules.
        public bool EvaluateNewCampaignGate()
        {
            RefreshGateFacts(out _, out _);
            return _lobbyController.ClientsReady;
        }

        // ─── Smart-Join (footer Join…) ─────────────────────────────────────

        // True when THIS engine is a LIVE DirectIP host: we hold the host role AND the DirectIP child
        // transport is actually Connected (bound + listening). Keyed on the DirectIP child's OWN state,
        // NOT CompositeTransport's aggregate — a 2nd same-box instance whose DirectIP bind FAILED
        // (AddressAlreadyInUse) is NOT a healthy host even if another child (STUN/Steam) came up, so it
        // is still allowed to join 127.0.0.1:<port> (the legitimate 2-instance same-box test).
        private static bool IsHealthyDirectHost()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsHost) return false;
            var t = engine.Transport;
            if (t == null) return false;
            ITransport direct = null;
            if (t is CompositeTransport comp)
            {
                foreach (var c in comp.Children)
                    if (c.TransportType == TransportType.DirectIP) { direct = c; break; }
            }
            else if (t.TransportType == TransportType.DirectIP)
            {
                direct = t;
            }
            return direct != null && direct.State == ConnectionState.Connected;
        }

        // Native ShowInputPrompt → classify → drop our own host lobby → join as client. <paramref
        // name="origin"/> is HOW the user asked to join, and it is a required parameter rather than a
        // defaulted one on purpose: it decides which transport a unified code is allowed to use, and a
        // caller that silently inherited the wrong default would connect over the wrong technology
        // while looking entirely correct. Both entry points are unambiguous — the Steam invite
        // subsystem's callback (WireSteamInvite, including the cold start) is JoinOrigin.SteamInvite,
        // the lobby's paste box is JoinOrigin.PastedCode.
        public void OnLobbyJoin(string input, JoinOrigin origin)
        {
            var target = SmartJoinParser.Parse(input ?? "");
            if (target.Kind == JoinKind.Invalid)
            {
                // Reached via the gate's JOIN or an accepted Steam invite, both of which left their
                // page HIDDEN. Keep it hidden behind this error box (HideForNativeScreen is idempotent)
                // so the native error shows through cleanly, then put the player back on whichever page
                // they came from when the box is dismissed — the lobby if a session is live, otherwise
                // the gate's join screen with their text still in the field.
                var mbErr = GameUtl.GetMessageBox();
                LeaveForNativeBox();
                mbErr?.ShowSimplePrompt("Could not read that address or code.",
                    MessageBoxIcon.Error, MessageBoxButtons.OK,
                    delegate (MessageBoxCallbackResult _) { ReturnFromNativeBox(); }, this);
                return;
            }

            // UX-safety guard: a HEALTHY DirectIP host pasting its OWN loopback:port is trying to join
            // the game it just created. The unconditional teardown below would Disconnect()+Shutdown()
            // the live host listener, then connect to a port nothing listens on → ConnectionRefused.
            // Refuse and keep the host session intact instead. (Failed-bind 2nd instance → not healthy →
            // falls through and still joins 127.0.0.1:<port>.)
            if (IsHealthyDirectHost() && SmartJoinParser.IsOwnLoopback(target, DefaultDirectPort))
            {
                var mbSelf = GameUtl.GetMessageBox();
                LeaveForNativeBox();
                mbSelf?.ShowSimplePrompt(
                    "You're already hosting — share your IP or invite code; you can't join your own game.",
                    MessageBoxIcon.Information, MessageBoxButtons.OK,
                    delegate (MessageBoxCallbackResult _) { _lobby?.Show(); }, this);
                return;
            }

            // Tear down the auto-hosted session, then connect as a client (overlay stays open;
            // Update's _lobby.Refresh() re-binds it to the new client session). The whole setup +
            // connect is wrapped so that NO exception (transport construction, Initialize, a
            // malformed target, etc.) can ever escape to freeze/crash the game — on any failure we
            // route to the same error dialog (OnConnectionFailed) and the lobby stays usable for a
            // retry. The actual socket connect inside JoinGame is already non-blocking + time-bounded
            // (DirectTransport/StunTransport surface their outcome from Update on the main thread),
            // so an unreachable host produces an error dialog within the connect timeout, never a
            // frozen UI.
            try
            {
                NetworkEngine.Instance?.Disconnect();
                NetworkEngine.Instance?.Shutdown();

                // Fix #4: drive the lobby FSM down the CLIENT path. We may be in HostLobby (the menu
                // auto-hosts) when the user pastes a join target, so Reset() returns the FSM to Idle
                // first, then BeginJoin() moves it Idle→Joining. JoinConfirmed() fires later, when the
                // first host PEER_LIST roster arrives (see Update), moving Joining→ClientLobby.
                _lobbyController.Reset();
                _lobbyController.BeginJoin();

                // Build the ordered cascade for this target and start the first stage. The plan is pinned
                // by the ORIGIN: a unified code that arrived through an accepted Steam invite keeps
                // Steam/Direct/STUN, a PASTED one is Direct/STUN with no Steam leg at all (JoinPlan's
                // Unified branch states why); a legacy code is a single attempt of its own transport.
                // Each stage's async connect is time-bounded; a stage failure advances to the next in
                // OnConnectionFailed.
                // SteamProbe, not SteamInvite directly: naming a static member of the Facepunch-holding
                // type throws in THIS frame where there is no Steam runtime (GOG/Epic), and this is the
                // join path — the one path a GOG/Epic player is guaranteed to take.
                _joinPlan = JoinPlan.Build(target, SteamProbe.IsAlive(), origin);
                _joinAttemptIndex = 0;
                if (_joinPlan.Count == 0)
                {
                    // Only reachable for a unified code that carried ONLY a Steam id and no endpoint —
                    // because Steam is off, or because it was PASTED and pasting is pinned off Steam.
                    // A host produces such a code when it has neither a UPnP-forwarded nor a STUN-
                    // discovered endpoint to put in it (see GetSessionInviteCode), so the honest advice is
                    // the same either way: get an address, or take the invite through Steam itself.
                    OnConnectionFailed("that invite code carries no address — ask the host to invite you through Steam, or to share their IP");
                    return;
                }
                // DO NOT open the lobby yet. The connect is async + time-bounded and the host has not
                // accepted us until its first PEER_LIST arrives — opening the lobby here would show a
                // FAKE empty lobby that an async failure then kicks us out of. StartJoinAttempt enters
                // the "connecting" state (lobby HIDDEN behind a native "Trying…" box with CANCEL);
                // Update() opens the real lobby once confirmed; OnConnectionFailed advances/aborts.
                StartJoinAttempt();
            }
            catch (System.Exception ex)
            {
                MpLog.LogError($"[Multiplayer] Join attempt failed: {ex}");
                // Reset any half-open session so a subsequent retry / host still works cleanly.
                NetworkEngine.Instance?.Shutdown();
                OnConnectionFailed($"Join failed: {ex.Message}");
            }
        }

        // Start (or advance to) the current cascade stage: init the transport for _joinPlan[_joinAttemptIndex]
        // and begin its async connect, showing a native "Trying…" box for the stage. Reused for the first
        // stage and each fallback after a stage fails (OnConnectionFailed).
        private void StartJoinAttempt()
        {
            var attempt = _joinPlan[_joinAttemptIndex];
            // EVERY STAGE LEAVES A LINE (L180). Until this existed the whole join path wrote nothing at
            // all after "transport initialized": a deadline that fired, a user who pressed CANCEL and an
            // attempt still hanging produced byte-identical logs — which is why the 2026-08-07 report
            // could say only "he waited, then it said it could not connect", with 54 s of silence in the
            // file underneath it.
            MpLog.Log($"[Multiplayer][join] stage {_joinAttemptIndex + 1}/{_joinPlan.Count} " +
                      $"{attempt.Transport} → {attempt.Address}:{attempt.Port} — connecting " +
                      $"(deadline {(int)JoinStageTimeoutSec}s for the host's first PEER_LIST).");
            NetworkEngine.Create();
            // Initialize BEFORE subscribing (unchanged from the pre-cascade order): a transport that
            // reports Failed synchronously inside Initialize (e.g. Steam API unavailable) has no UI
            // handler yet, so it is swallowed rather than surfacing a spurious first-frame failure.
            NetworkEngine.Instance.Initialize(attempt.Transport);
            NetworkEngine.Instance.OnConnectionFailed += OnConnectionFailed;
            // Show the stage box BEFORE the connect so that if JoinGame ever fails synchronously the
            // retry's Dismiss+Show cleanly replaces this box instead of a stale label stomping the next.
            ShowConnectingBox(StageLabel(attempt.Transport, _joinAttemptIndex, _joinPlan.Count));
            NetworkEngine.Instance.JoinGame(attempt.Address, attempt.Port);
        }

        // Per-stage connecting-box caption. A single-attempt plan keeps the plain "Connecting to host…";
        // a multi-stage cascade names the transport + progress so the wait is legible.
        private static string StageLabel(TransportType t, int index, int count)
        {
            if (count <= 1) return "Connecting to host…";
            string what;
            switch (t)
            {
                case TransportType.SteamP2P: what = "Trying Steam…"; break;
                case TransportType.StunUDP: what = "Trying hole-punch…"; break;
                default: what = "Trying direct connection…"; break;
            }
            return $"{what} ({index + 1}/{count})";
        }

        // THE LOBBY'S PASTE PROMPT IS GONE, and nothing replaces it in the lobby. `OnLobbyJoinPrompt`
        // stood here: a native input box opened from a JOIN A GAME button on the session card, i.e. an
        // offer to leave the session you were sitting in, drawn on the card whose job is filling it. Its
        // one caller went with the card's JOIN row. The paste box that MATTERS is on the gate's join
        // screen (NetworkGatePanel → MultiplayerUI.OnGateJoin), which is reached before any session
        // exists, and it shares this class's JoinPrefill so the two never disagreed about the prefill and
        // there is now only one of them left to disagree.

        // ─── Nickname rename (own roster row pencil) ───────────────────────

        public void OnLobbyRenamePrompt()
        {
            var mb = GameUtl.GetMessageBox();
            if (mb == null) return;
            var current = NetworkEngine.Instance?.IsHost == true
                ? NetworkEngine.Instance.Session?.HostNickname ?? ""
                : (ClientIdentity.LocalNickname ?? SystemInfo.deviceName);

            // The native MessageBox renders BELOW the lobby overlay, so a prompt opened over the open
            // lobby is occluded. Use the proven Load-screen approach: hide the lobby overlay (restore
            // menu chrome + hide the status bar) so the native prompt shows through cleanly, then
            // re-show the lobby in EVERY dialog-result path (OK and Cancel) so it never gets stuck
            // hidden. On OK the rename commits/propagates (the host BroadcastPeerList in
            // SendRename/HandleRename pushes it to every other peer); the lobby's per-frame Refresh
            // reflects the new nickname once it is shown again.
            _lobby?.HideForNativeScreen();
            // Pass `current` as suggestedText so that IF the InputField can't be located
            // (TryUpgradePromptInput == false) the prompt gracefully falls back to the OLD
            // prefilled-value behavior instead of an empty, hint-less field.
            mb.ShowInputPrompt("New nickname", current, null,
                MessageBoxIcon.Question, MessageBoxButtons.OKCancel,
                delegate (MessageBoxCallbackResult res)
                {
                    // OnLobbyRename no-ops on empty/whitespace, and the native ValidateResult
                    // already blocks an empty OK — so an empty submit keeps the current nickname.
                    if (res.DialogResult == MessageBoxResult.OK)
                        OnLobbyRename(res.InputTextResult ?? "");
                    _lobby?.Show();
                }, this);

            // Web-style: clear the prefilled value and show it as a grey placeholder hint behind a
            // blinking caret instead, so the user can type immediately without selecting/deleting.
            TryUpgradePromptInput(string.IsNullOrEmpty(current) ? "Nickname" : current);
        }

        // Reconfigure the just-opened native input prompt into a web-style field: empty text with a
        // grey placeholder hint and an already-blinking caret (no extra click). The native
        // MessageBoxInputPromptController.Show() runs synchronously inside ShowInputPrompt and
        // activates + focuses the field in the same call, so this can run immediately afterward.
        //
        // Returns true if the InputField was found and reconfigured. On false the caller's prefilled
        // suggestedText (if any) remains untouched as a graceful fallback — nothing breaks.
        //
        // Native grounding (decompiled, source-provenance authoritative):
        //   MessageBoxInputPromptController.cs:11   private InputField _inputField   (UnityEngine.UI, legacy)
        //   MessageBoxInputPromptController.cs:13-22 Show() sets _inputField.text = SuggestedText, SetActive(true)
        //   MessageBoxInputPromptController.cs:32-33 OnShowReady() Select()+ActivateInputField()
        //   MessageBoxInputPromptController.cs:50-57 ValidateResult blocks OK on whitespace-only text
        // Also normalizes the field so ALL characters type (BUG A: prefab characterValidation rejected
        // ".") and forces a readable dark text/caret on the light field (BUG B: white-on-white text).
        private static bool TryUpgradePromptInput(string placeholderHint)
        {
            var mb = GameUtl.GetMessageBox();
            if (mb == null) return false;

            // The InputField lives on the (active) input-prompt controller under the MessageBox.
            var field = mb.GetComponentInChildren<InputField>(true);
            if (field == null) return false;

            // BUG A fix — accept ALL characters (periods/colons for IP:port, base32 for STUN codes).
            // The native prompt prefab serializes this InputField with a restrictive characterValidation,
            // so "." (and other punctuation) is silently rejected by the engine's per-keystroke validation
            // — an IPv4 address can't be typed. The console-hotkey patch (doc 11) already stopped the dev
            // console from eating the dot; this normalizes the FIELD-side filter that was the remaining
            // cause. Forcing ContentType.Standard runs Unity's EnforceContentType (resets validation to
            // None, single-line, etc.); we then set CharacterValidation.None explicitly and clear any
            // leftover onValidateInput (the shared native field is reused across dialogs). Re-applied on
            // every prompt so prior-use state never leaks in.
            //   Native grounding: MessageBoxInputPromptController.cs:11 (the InputField),
            //   :19 (combines data.InputValidator into onValidateInput — ours is null).
            field.contentType = InputField.ContentType.Standard;
            field.characterValidation = InputField.CharacterValidation.None;
            field.onValidateInput = null;

            // Clear any prefilled value so the placeholder hint shows instead. An empty submit can never
            // rename/connect to nothing because the native ValidateResult rejects an empty OK.
            field.text = string.Empty;

            // BUG B fix — the typed text was white-on-white (invisible). PP's prompt prefab serializes
            // the input Text white and renders over a light field background in our usage; the dev
            // console itself overrides this same color at runtime (GameConsoleWindow.cs:611:
            // ((Graphic)_inputField.textComponent).color = style.InputFieldTextColor), confirming the
            // native default isn't readable on its own. Force a near-black text + caret that contrasts
            // the light field, and darken the placeholder hint so it's visible yet distinct (alpha 0.45
            // vs solid text). textComponent is null-guarded.
            var inkColor = new Color(0.10f, 0.10f, 0.10f, 1f); // near-black, readable on light field
            if (field.textComponent != null)
                field.textComponent.color = inkColor;
            field.customCaretColor = true;
            field.caretColor = inkColor;

            // InputField.placeholder is a Graphic, normally a Text in PP's prefab.
            if (field.placeholder is Text ph)
            {
                ph.text = placeholderHint ?? string.Empty;
                ph.color = new Color(0.10f, 0.10f, 0.10f, 0.45f); // dark translucent hint on light field
            }

            // Focus immediately so the caret blinks without an extra click. Reinforces the engine's
            // own Select()+ActivateInputField from OnShowReady (harmless to repeat).
            EventSystem.current?.SetSelectedGameObject(field.gameObject);
            field.ActivateInputField();

            field.caretPosition = 0;
            return true;
        }

        // ─── Chat send ─────────────────────────────────────────────────────

        public void OnLobbyChatSend(string text)
        {
            NetworkEngine.Instance?.Session?.SendChat(text);
        }

        // ─── Choose save (host only) ───────────────────────────────────────

        // Host rail "Choose save…": open the game's OWN native Load screen and INTERCEPT the pick so
        // it hands back the chosen SavegameMetaData WITHOUT loading the campaign (see
        // Harmony/SaveLoadInterceptPatch). We stopped hand-building the row list (it kept rendering
        // empty); the native screen is the game's verified save UI. ALWAYS use the native screen —
        // there is no custom fallback (a second click used to silently fall to the wrong custom
        // window once the cached native button went stale).
        public void OnLobbyChooseSave()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsHost) return;

            // The handler that records + broadcasts the chosen save (identical to the old picker path).
            System.Action<SavegameMetaData> onPicked = CaptureLobbySavePick;

            // Arm the intercept, hide our overlay so the native screen shows through, then open it by
            // driving the home-screen state stack straight to UIStateHomeLoadGame (robust on EVERY
            // click — see SaveLoadInterceptPatch.OpenNativeLoadScreen). If the native open genuinely
            // fails we disarm and re-show the lobby (NO custom fallback window): the host can retry.
            SaveLoadInterceptPatch.Arm(onPicked);
            _lobby?.HideForNativeScreen();
            if (!SaveLoadInterceptPatch.OpenNativeLoadScreen())
            {
                SaveLoadInterceptPatch.Disarm();
                _lobby?.Show();
                MpLog.LogWarning("[Multiplayer] Could not open the native Load screen for Choose-Save; try again.");
            }
        }

        // ─── New campaign (host only, P0 co-op bootstrap) ───────────────────

        // Host rail "New campaign…": open the game's OWN native new-game settings screen (difficulty /
        // DLC choices stay host-only, native). This button is pure NAVIGATION — the co-op arming
        // happens at the native CONFIRM chokepoint (NewCampaignInterceptPatch, a durable gate that
        // also covers any other native route to that screen), and BACK returns to the lobby via the
        // OnSettingsBackClicked postfix. After confirm the campaign is created natively; at its first
        // playable geoscape frame the coordinator autosaves + re-runs the EXISTING chunked transfer.
        public void OnLobbyNewCampaign()
        {
            var engine = NetworkEngine.Instance;
            var coord = engine?.SaveTransfer;
            if (engine == null || coord == null || !engine.IsHost) return;

            // Press-time projection of the SAME Core gate the confirm intercept arms under — the
            // button never opens a screen whose confirm would be refused.
            if (!SessionLifecycle.NewCampaignArmGuard(engine.IsHost, engine.IsActiveSession,
                    coord.SessionStarted, coord.TransferActive))
            {
                MpLog.LogWarning("[Multiplayer] New-campaign bootstrap unavailable (session started or transfer in flight).");
                return;
            }

            // The lobby readiness gate, re-checked at the press (the button is already greyed on it —
            // this is the defense-in-depth half, same shape as OnLobbyPlay's).
            if (!EvaluateNewCampaignGate())
            {
                // NAME THE BLOCKER (L181): the old line said only that somebody had not readied, which is
                // unactionable exactly when it matters — and a row that never finished joining named
                // nobody at all while holding this button down forever.
                MpLog.LogWarning("[Multiplayer] NEW CAMPAIGN blocked: " +
                                 LobbyController.PeersBlockedBy(engine.Session?.GetLobbyRoster()) +
                                 ". (A peer that dropped out is PAUSED, and one whose JOIN never arrived " +
                                 "is off the roster — neither is counted.)");
                return;
            }

            // KILL A RUNNING SAVE COUNTDOWN BEFORE THE NATIVE SCREEN GOES UP. The gate above is
            // ClientsReady, which deliberately excludes _saveChosen and _hostReady — so this button is
            // pressable while a SAVE countdown is already ticking, and nothing here used to stop it.
            // CanStart stayed true (the host's own screen does not change the roster), the clock reached
            // zero underneath the native new-game screen, and OnLobbyPlay dropped the curtain and loaded
            // the OLD SAVE while the host was picking difficulty for a fresh campaign. Cancel is the
            // existing mechanism and it is NOT a wait on anybody (P13): it stops the clock and clears the
            // host's OWN ready, which is what keeps the gate closed — and therefore the countdown
            // un-armed — for as long as the host is off in the native flow. The host re-readies when it
            // comes back. Every peer's open lobby repaints from the CLEAR this broadcasts
            // (LobbyCountdown.Clear → 0x49 → HandleCountdown, drawn by CountdownPanel).
            if (LobbyCountdown.Running) LobbyCountdown.Cancel(engine, null);

            _lobby?.HideForNativeScreen();
            if (!NewCampaignInterceptPatch.OpenNativeNewGameScreen())
            {
                _lobby?.Show();
                MpLog.LogWarning("[Multiplayer] Could not open the native New Game screen; try again.");
            }
        }

        /// <summary>Re-show the lobby overlay (host backed out of the native new-game settings —
        /// NewCampaignInterceptPatch.SettingsBack_Postfix). Idempotent.</summary>
        public void ShowLobby() => _lobby?.Show();

        // Two picked saves are the SAME selection iff they reference the same on-disk save, identified
        // by the filesystem Path (unique, set when the meta is loaded). If Path is unset/empty on either
        // side the identity is unknown, so the saves are treated as DIFFERENT (never equal by name/time).
        private static bool SameSave(SavegameMetaData a, SavegameMetaData b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            // Path is the only reliable save identity. If it is unset/empty on EITHER side, treat the
            // saves as DIFFERENT rather than falling back to name/time equality — otherwise two distinct
            // saves that happen to share a display-name + creation-time (both with empty Path) compare
            // equal, so a genuine save swap fails to reset client Ready (stale ready survives).
            if (string.IsNullOrEmpty(a.Path) || string.IsNullOrEmpty(b.Path))
                return false;
            return string.Equals(a.Path, b.Path, System.StringComparison.OrdinalIgnoreCase);
        }

        // Called from SaveLoadInterceptPatch when the native Load screen closes (after a pick OR a
        // cancel). Re-show the lobby (which re-hides the native chrome) and, if a save was chosen,
        // deliver it to the chosen-save handler. Runs on the main thread (Harmony Postfix on
        // UIStateHomeLoadGame.ExitState).
        public void OnNativeSaveScreenClosed(SavegameMetaData chosen, System.Action<SavegameMetaData> onPicked)
        {
            _lobby?.Show();
            if (chosen != null) onPicked?.Invoke(chosen);
        }

        /// <summary>
        /// Record + broadcast the host's chosen lobby save (label only — NO campaign load). This is the
        /// single source of truth for "host picked a save in the lobby": OnLobbyChooseSave's armed path
        /// hands its onPicked straight to this, AND the durable un-armed lobby gate
        /// (<see cref="OnLobbyLoadPickCaptured"/>) calls it too, so the chosen-save behaviour is identical
        /// regardless of whether the static intercept flag happened to be armed at click time.
        /// </summary>
        public void CaptureLobbySavePick(SavegameMetaData chosen)
        {
            if (chosen == null) return;

            // Reset every client's Ready ONLY on a GENUINE save change. Clients readied for a
            // specific session, so swapping to a DIFFERENT save invalidates that — but the first
            // pick (no prior session) and re-picking the SAME save must NOT drop anyone's ready,
            // or the gate's all-ready bit keeps collapsing and PLAY never lights.
            var previousChosen = _pendingChosenSave;
            _pendingChosenSave = chosen;
            bool genuineChange = previousChosen != null && !SameSave(previousChosen, chosen);
            if (genuineChange)
            {
                // Drop the FSM's cached ready fact and clear the authoritative roster ready bits
                // so the gate re-closes until everyone re-readies for the new save.
                _lobbyController.SaveChangedShouldResetReady();
                NetworkEngine.Instance?.Session?.ResetAllClientsReady();
            }

            var name = SaveDisplayName(chosen);
            var meta = SaveDisplayMeta(chosen);
            NetworkEngine.Instance?.Session?.SetChosenSave(name, meta);
        }

        /// <summary>
        /// DURABLE lobby save-pick capture. Called from the OnLoadGamePressed prefix when
        /// <see cref="SessionLifecycle.ShouldCaptureAsLobbyPick"/> is true (host in an active lobby whose
        /// session has NOT started) — REGARDLESS of the intercept's <c>_armed</c> flag. The host can reach
        /// a native Load screen un-armed (the lobby re-makes native menu buttons clickable), and the old
        /// flag-only guard then let the real load run. This records the pick as the lobby's chosen save and
        /// re-shows the lobby; the campaign load happens ONLY via host PLAY → CommitStart → HostStartSession.
        /// Self-contained: works even when _armed==false / no onPicked callback is set.
        /// </summary>
        public void OnLobbyLoadPickCaptured(SavegameMetaData chosen)
        {
            CaptureLobbySavePick(chosen);
            // Re-show the lobby (re-hides the native chrome restored when the Load screen opened). The
            // prefix already closed the native Load screen via CloseModule, so we must not be left on it.
            _lobby?.Show();
        }

        // ─── F2: host loads ANY save mid-session (tactical OR geoscape) ─────
        // Called from InGameLoadArmPatch (postfix on UIModulePauseScreen.OnLoadPressed). Arms the save
        // intercept in PREFIX-delivery mode IFF the live engine is the host in an active, already-started
        // session (in-game, not lobby) with >=1 connected client and NO transfer already in flight.
        // Returns true iff armed. On false the caller disarms so the host's load runs as normal.
        public bool TryArmInGameHostLoad()
        {
            var engine = NetworkEngine.Instance;
            var coord = engine?.SaveTransfer;
            bool ok = SessionLifecycle.HostLoadGuard(
                isHost: engine?.IsHost ?? false,
                isActiveSession: engine?.IsActiveSession ?? false,
                sessionStarted: coord?.SessionStarted ?? false,
                connectedClientCount: engine?.Session?.ClientCount ?? 0,
                transferActive: coord?.TransferActive ?? false);
            if (!ok) return false;

            SaveLoadInterceptPatch.ArmInPrefix(OnInGameLoadPicked);
            MpLog.Log("[Multiplayer] F2: armed in-game host load intercept (pause-menu Load).");
            return true;
        }

        // The picked save from the in-game pause-menu Load (delivered straight from the intercept
        // prefix). Re-run the EXISTING chunked transfer + 2-phase barrier so every client reloads into
        // it. The host is mid-game (tactical or geoscape); SaveTransferCoordinator's ClientLoadCrt /
        // PrepareEntryFromBlobCrt tears down the current level via the native FinishLevel path on BEGIN,
        // so this is a host-authoritative InGame->InGame transition — clients follow unconditionally.
        public void OnInGameLoadPicked(SavegameMetaData chosen)
        {
            var engine = NetworkEngine.Instance;
            if (chosen == null || engine == null || !engine.IsHost) return;

            // Re-validate the guard at delivery time (defense-in-depth: state may have changed between
            // arming on OnLoadPressed and the host actually clicking a slot).
            var coord = engine.SaveTransfer;
            bool ok = SessionLifecycle.HostLoadGuard(
                isHost: engine.IsHost,
                isActiveSession: engine.IsActiveSession,
                sessionStarted: coord?.SessionStarted ?? false,
                connectedClientCount: engine.Session?.ClientCount ?? 0,
                transferActive: coord?.TransferActive ?? false);
            if (!ok)
            {
                MpLog.LogWarning("[Multiplayer] F2: in-game load guard closed at delivery; ignoring pick.");
                return;
            }

            // Do NOT show the load overlay here. As with OnLobbyPlay, its visibility is state-driven and
            // gates on the NATIVE load-start (LoadPhaseStarted, curtain "Loading") via CurtainShowPatch —
            // showing it on the F2 pick would pop it mid-game before the load curtain dropped. HideLoadOverlay
            // on the failure path stays (idempotent: clears any overlay if the in-game start never began).
            bool started = coord.HostStartSessionInGame(chosen);
            if (!started)
            {
                HideLoadOverlay();
                MpLog.LogWarning("[Multiplayer] F2: HostStartSessionInGame did not start (see prior log).");
            }
        }

        // The save the host chose in the rail (consumed by PLAY); null until a pick is made.
        public SavegameMetaData PendingChosenSave => _pendingChosenSave;
        private SavegameMetaData _pendingChosenSave;

        private static string SaveDisplayName(SavegameMetaData meta)
        {
            if (meta == null) return "(none)";
            var name = meta.Name;
            if (meta is PPSavegameMetaData pp
                && !string.IsNullOrEmpty(pp.UserSetName))
                name = pp.UserSetName;
            return string.IsNullOrEmpty(name) ? "(unnamed)" : name;
        }

        private static string SaveDisplayMeta(SavegameMetaData meta)
        {
            try { return meta?.SaveCreated?.DisplayDateTime ?? ""; }
            catch { return ""; }
        }

        // ─── Connect rail values + clipboard ───────────────────────────────

        // THE PLACEHOLDERS, named ONCE. These are what the code cell shows when there is no code yet —
        // they are STATUS, not something anyone can paste into a join box, and IsPlaceholderCode below is
        // the single list that says so. Two lists would drift, and the copy path is where the drift would
        // show: a cell that says COPIED while the clipboard holds "discovering…" is worse than no
        // feedback, because the player then pastes it to a friend.
        private const string CodeWaiting = "waiting for host…";
        private const string CodeUnavailable = "unavailable (NAT/firewall)";
        private const string CodeDiscovering = "discovering…";

        private static bool IsPlaceholderCode(string code)
            => code == CodeWaiting || code == CodeUnavailable || code == CodeDiscovering;

        // THE SESSION'S ONE invite code — the PUBLIC ENDPOINT, and nothing else. The same string on every
        // peer, so ANY player can forward it to a friend, not just the host.
        //
        // NO STEAM ID IN THE CODE, EVER, and that is the design rather than a shortening. The three ways
        // in are separate channels and do not mix: STEAM is the INVITE VIA STEAM overlay and the friends
        // list (it produces no code at all); the CODE is this string, the free-services path; DIRECT is
        // typed by hand (localhost, ip:port, a DNS name). Steam P2P needs a live Steam client on BOTH
        // sides (SteamInvite.IsSteamAlive gates the client leg, JoinPlan.Build takes it as steamAlive),
        // so a Steam id inside a shared code is worthless to exactly the people a shared code is for —
        // and a code that carries only a Steam id builds a single Steam-P2P attempt that cannot come up.
        //
        //   • HOST: computed here. Endpoint priority: the UPnP-forwarded WAN endpoint (actually open for
        //     TCP+UDP) over the STUN-discovered one (UDP hole-punch only). Re-read every frame by the
        //     lobby, so it fills in live as UPnP/STUN discovery completes — and
        //     the lobby publishes each new value into SessionManager.PublishHostInviteCode, which ships
        //     it to clients on the roster broadcast.
        //   • CLIENT: the host's published value, mirrored in by PEER_LIST. Read-only, copyable.
        public string GetSessionInviteCode()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null) return "";
            if (!engine.IsHost)
            {
                var mirrored = engine.Session?.HostInviteCode;
                return string.IsNullOrEmpty(mirrored) ? CodeWaiting : mirrored;
            }

            var ep = UpnpPortMapper.Current?.ToEndPoint();      // best: router-forwarded WAN endpoint
            if (ep == null)
                ep = FindHostingChild(TransportType.StunUDP)?.PublicEndPoint; // fallback: STUN-discovered

            // ponytail: the code carries an ENDPOINT ONLY (11 symbols since the check symbol landed —
            // ConnectCode.TotalSymbols, "4-3-4"), so a host with neither a UPnP
            // mapping nor a STUN-discovered endpoint has no shareable code at all and must invite through
            // Steam — the placeholder below says so, and the join screen repeats it. Upgrade path if that
            // host ever has to be shareable: a relay, not a richer code; there is nothing else to encode.
            //
            // This leaves UnifiedCode.Encode with NO producer. Do NOT read UnifiedCode.cs as dead —
            // UnifiedCode.TryDecode stays load-bearing in SmartJoinParser and JoinPrefill for every 9/13/19
            // symbol code already copied into a Discord message before this change.
            var code = ep != null ? ConnectCode.Encode(ep) : null;
            if (code != null) return code;

            // Nothing to share yet — mirror the STUN discovery state so the placeholder is accurate.
            var stun = FindHostingChild(TransportType.StunUDP);
            if (stun != null && stun.LocalEndpoint != null && stun.LocalEndpoint.Contains("unavailable"))
                return CodeUnavailable;
            return CodeDiscovering;
        }

        /// <summary>The invite code AS SOMETHING TO COPY: the real code, or "" while the cell is showing
        /// one of the placeholders. The DISPLAY path is untouched — the lobby still writes the full
        /// GetSessionInviteCode string into the cell, so the player still reads "discovering…" there. Only
        /// the clipboard (and therefore the COPIED acknowledgement, which CopyToClipboard's empty-check
        /// gates) refuses a status line. Copying "discovering…" put a string in the clipboard that the
        /// player would forward to a friend believing it was a code.</summary>
        public string GetCopyableSessionInviteCode()
        {
            var code = GetSessionInviteCode();
            return IsPlaceholderCode(code) ? "" : code;
        }

        // Resolve the live hosting child transport of a given type from the host's transport,
        // whether that transport is a CompositeTransport (multi-listen) or a single transport.
        private static ITransport FindHostingChild(TransportType type)
        {
            var t = NetworkEngine.Instance?.Transport;
            if (t == null) return null;
            if (t is CompositeTransport composite)
            {
                foreach (var child in composite.Children)
                    if (child.TransportType == type && child.IsHost) return child;
                return null;
            }
            return t.TransportType == type && t.IsHost ? t : null;
        }

        // THE ONE CLIPBOARD SINK. Returns whether anything was actually copied, which is what lets the
        // caller acknowledge WITHOUT re-testing the value: a click on a cell whose value is empty (or on
        // a placeholder that resolved to nothing) must stay silent, and the early return here is the one
        // place that decides it. LobbyPanel.MakeCopyableValue is the only caller and the only builder of
        // copyable cells, so the ack it hangs off this bool covers every copy target there will be.
        /// GUARDED, and at THIS one seam rather than at each caller: systemCopyBuffer is a native OLE call
        /// and the copy is never worth taking a screen down for. A failure returns false, so a caller's
        /// acknowledgement stays honest.
        public bool CopyToClipboard(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            try { GUIUtility.systemCopyBuffer = text; }
            catch (System.Exception e)
            {
                MpLog.LogWarning("[Multiplayer] clipboard write failed (" + e.GetType().Name +
                                 "); nothing was copied.");
                return false;
            }
            return true;
        }

        /// <summary>The read half of the same seam — a clipboard that throws must not take the join
        /// prefill (and with it the whole join screen) down. Empty is the honest answer.</summary>
        private static string ClipboardText()
        {
            try { return GUIUtility.systemCopyBuffer; }
            catch (System.Exception e)
            {
                MpLog.LogWarning("[Multiplayer] clipboard read failed (" + e.GetType().Name + ").");
                return "";
            }
        }

        // ─── Invite code (host's own short join code) ──────────────────────

        // Host's permanent, friend-free invite code ("XXXX-XXXX") or a placeholder while Steam is
        // offline. Re-read each frame by the rail (self-heals if Steam becomes ready after build).
        public string GetOwnInviteCode()
        {
            return SteamProbe.LocalInviteCode() ?? "(Steam offline)";
        }

        // CODE button click: copy the code and confirm via the native prompt (same feedback pattern
        // as the pause-menu "Copied to clipboard" box). No-ops with a Steam-unavailable prompt offline.
        public void CopyInviteCode()
        {
            var code = SteamProbe.LocalInviteCode();
            if (string.IsNullOrEmpty(code)) { ShowSteamNotAvailable(); return; }
            CopyToClipboard(code);
            var mb = GameUtl.GetMessageBox();
            mb?.ShowSimplePrompt($"Invite code copied:\n{code}\n\nYour friend pastes it into JOIN A GAME.",
                MessageBoxIcon.Information, MessageBoxButtons.OK, null, this);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Connection events
        // ═══════════════════════════════════════════════════════════════════

        private void OnConnectionFailed(string reason)
        {
            // CASCADE: this stage failed but the plan has another transport to try. Tear down just THIS
            // stage's engine and start the next one, keeping the lobby FSM in Joining and _clientConnecting
            // set — only the LAST stage's failure falls through to the user-facing error below.
            if (_joinPlan != null && _joinAttemptIndex + 1 < _joinPlan.Count)
            {
                MpLog.LogWarning($"[Multiplayer][join] stage {_joinAttemptIndex + 1}/{_joinPlan.Count} " +
                                 $"({_joinPlan[_joinAttemptIndex].Transport}) FAILED after " +
                                 $"{UnityEngine.Time.realtimeSinceStartup - _joinStageStartedAt:F1}s: {reason} " +
                                 $"— falling back to {_joinPlan[_joinAttemptIndex + 1].Transport}.");
                _joinAttemptIndex++;
                DismissConnectingBox();
                NetworkEngine.Instance?.Disconnect();
                NetworkEngine.Instance?.Shutdown();
                StartJoinAttempt();
                return;
            }
            // The LAST stage (or a plan-less failure): this is the one the player is told about, so it is
            // also the one the log has to carry. Read the stage BEFORE the plan is dropped.
            MpLog.LogError($"[Multiplayer][join] join ABANDONED after " +
                           $"{(_joinPlan == null ? 0 : _joinPlan.Count)} stage(s), " +
                           $"{UnityEngine.Time.realtimeSinceStartup - _joinStageStartedAt:F1}s in the last one: " +
                           $"{reason}");
            _joinPlan = null;

            // A client join in flight just failed/timed out. End the connecting state and close the
            // "Connecting…" box FIRST, so the error box below isn't pushed onto the modal stack behind
            // it (and so the CANCEL callback can't also fire — it self-guards on _clientConnecting).
            // No-op for a host or an already-resolved attempt.
            _clientConnecting = false;
            DismissConnectingBox();

            // Tear down the failed/half-open session NOW so no leaked socket, worker thread, or stuck
            // "Connecting/Failed" engine lingers — a subsequent Join or Host then starts from a clean
            // slate. Shutdown also disposes any in-flight connect worker (DirectTransport/StunTransport
            // abort + join their connect thread) and clears the engine's OnConnectionFailed handler so
            // it can't double-fire. The in-game bar is hidden because this session is over.
            NetworkEngine.Instance?.Disconnect();
            NetworkEngine.Instance?.Shutdown();
            // Fix #5: a dropped/failed connect must also reset the local FSM + clear the stale chosen
            // save, exactly like LEAVE/cancel — otherwise the next host/join inherits a HostLobby/
            // Starting FSM state and a phantom _pendingChosenSave from the session that just failed.
            TeardownLobbyState();
            _inGameBar?.SetActive(false);

            var mb = GameUtl.GetMessageBox();
            if (mb != null)
            {
                // Fires asynchronously after a smart-join attempt while the lobby is still HIDDEN (we
                // never opened it for an unconfirmed join). Keep it hidden behind this error box, then
                // on dismiss return the player to the page the join came FROM: the gate's join screen,
                // where the address is still typed and one more press retries it — or the plain main
                // menu when the join did not come from there. NO empty lobby is ever shown on the client
                // failure path (the session is gone, and ReturnFromNativeBox keys on exactly that).
                LeaveForNativeBox();
                // Show the REASON, nothing else. This handler serves two very different failures and a
                // fixed networking paragraph was wrong for both: a host-authored ConnectionRejected
                // ("identity already in use", "a battle is in progress") arrives over an ALREADY-WORKING
                // socket, so advising UPnP/port-forwarding/CGNAT sent the user to fix a link that was never
                // broken; and a genuine transport failure already carries a better, per-transport hint that
                // NetworkEngine.BuildTransportFailureReason builds into `reason` itself. Any connectivity
                // advice belongs there, next to the transport that knows which one failed.
                mb.ShowSimplePrompt(
                    $"Connection failed: {reason}",
                    MessageBoxIcon.Error, MessageBoxButtons.OK,
                    delegate (MessageBoxCallbackResult _) { ReturnFromNativeBox(); }, this);
            }
            else
            {
                ReturnFromNativeBox();
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Client-join connecting state machine
        // ═══════════════════════════════════════════════════════════════════

        // Enter the connecting state for a client smart-join: keep the lobby HIDDEN and put up a
        // native "Connecting to host…" box with a CANCEL button. The box renders ON TOP of the
        // (hidden) lobby via the same approach the other prompts use; CANCEL aborts the connect. The
        // box is dismissed programmatically by DismissConnectingBox() on confirm (Update) or
        // failure/timeout (OnConnectionFailed). Null-safe: if the MessageBox is unavailable we still
        // set _clientConnecting so Update can open the lobby on confirmation (degraded: no visible
        // indicator, but never a fake lobby and never stuck).
        private void ShowConnectingBox(string message = "Connecting to host…")
        {
            _clientConnecting = true;
            _joinStageStartedAt = UnityEngine.Time.realtimeSinceStartup;
            _connectingBox = GameUtl.GetMessageBox();
            _connectingBox?.ShowSimplePrompt(message,
                MessageBoxIcon.Information, MessageBoxButtons.Cancel,
                delegate (MessageBoxCallbackResult _)
                {
                    // User pressed CANCEL. Guard on _clientConnecting: a programmatic dismiss
                    // (confirm/fail) uses ForceCloseAllPrompts which does NOT fire this callback, and
                    // it also clears the flag first, so a late/stale invocation here no-ops.
                    if (_clientConnecting)
                        CancelClientConnect();
                }, this);
        }

        // Close the "Connecting…" box without firing its CANCEL callback. Idempotent + null-safe.
        private void DismissConnectingBox()
        {
            _connectingBox?.ForceCloseAllPrompts();
            _connectingBox = null;
        }

        // CANCEL during connect: leave the connecting state, abort the in-flight connect (Shutdown
        // disposes the connect worker + aborts the socket), and return to the plain network menu —
        // no error dialog, no lobby. The box already closed itself on the CANCEL press.
        private void CancelClientConnect()
        {
            // The third outcome of a join, and the one the log could not tell from the other two.
            MpLog.LogWarning($"[Multiplayer][join] CANCELLED by the player after " +
                             $"{UnityEngine.Time.realtimeSinceStartup - _joinStageStartedAt:F1}s " +
                             $"(stage {_joinAttemptIndex + 1}) — no failure, no deadline.");
            _clientConnecting = false;
            _joinPlan = null; // abort the whole cascade
            _connectingBox = null;
            NetworkEngine.Instance?.Disconnect();
            NetworkEngine.Instance?.Shutdown();
            TeardownLobbyState();
            _inGameBar?.SetActive(false);
            // Back to the page the join was started from (the gate's join screen), or the plain main menu
            // when it was not — never an empty lobby, the session was torn down two lines ago.
            ReturnFromNativeBox();
        }

        // SINGLE teardown hook for the lobby's local FSM + chosen-save state. Every path that ends a
        // session (LEAVE / connect-cancel / connection-failure) routes through here so the FSM can
        // never carry a stale HostLobby/Starting state and _pendingChosenSave can never carry a stale
        // save into the NEXT host/join. Pure-local + idempotent (the network teardown is done by the
        // caller's Disconnect/Shutdown); safe to call more than once.
        private void TeardownLobbyState()
        {
            _lobbyController.Reset();
            // Local-only, no broadcast (there may be no transport left): a torn-down session must not leave
            // a frozen number on the NEXT lobby's overlay. Same contract as DeployCountdown's reload-boundary
            // reset, and the same reason — a static that outlives its session is a stale display.
            LobbyCountdown.Reset();
            _pendingChosenSave = null;
            // If a session ends (LEAVE / connect-cancel / connection-fail / host-left) while the client is
            // on the native download loading screen, lift the curtain + hide the plaque so we return to a
            // usable menu, never a stuck bar. Idempotent no-op when no curtain was dropped.
            LiftCurtainEarly();
            HideLoadOverlay();
        }

        // Public teardown entry for the F3 host-leave path. When the HOST ends/leaves the session, the
        // CLIENT is returned to the main menu by HostLeaveHandler (native FinishLevelAndGoToLobby +
        // NetworkEngine.TearDown). That path tears down the NETWORK session but never touched this UI's
        // lobby FSM / chosen-save, so a stale ClientLobby/Starting state + phantom _pendingChosenSave
        // would survive into the next host/join (the same Fix #5 bug LEAVE/cancel/OnConnectionFailed
        // already guard against, just on the host-left trigger). Routes through the single
        // TeardownLobbyState hook so the reset stays consistent. Null-safe via the static Instance.
        public void TeardownLobbyOnSessionEnd() => TeardownLobbyState();

        private void OnDisconnectClicked()
        {
            // Defense-in-depth: a transport-layer exception during Disconnect() (e.g. the old
            // ObjectDisposedException from reading a just-closed peer socket) must NEVER prevent the
            // session from being fully shut down + the lobby torn down. Without this, an escaping throw
            // aborted the leave before Shutdown()/TeardownLobbyState() (and HostLeaveHandler.Detach via
            // Shutdown) ran → the client froze in a half-torn-down lobby. The finally guarantees the
            // leave always completes cleanly and the client returns to the menu (lobby panel hidden).
            try
            {
                // Graceful leave notice BEFORE the transport closes: the host drops us instantly
                // (HandleLeave) instead of waiting out the 20 s heartbeat timeout. Best-effort no-op
                // on the host / with no live host link.
                NetworkEngine.Instance?.Session?.SendClientLeave();
                NetworkEngine.Instance?.Disconnect();
            }
            finally
            {
                NetworkEngine.Instance?.Shutdown();  // also drops the Steam invite lobby + rich presence (SteamLobbyCleanup hook)
                TeardownLobbyState();
                _inGameBar.SetActive(false);
                _lobby?.Hide();
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  In-game bar
        // ═══════════════════════════════════════════════════════════════════

        public void ShowInGameBar()
        {
            // Status bar removed (user feedback: useless + half-hidden at screen bottom).
            // No-op so the in-game bar never appears. The GameObject is still built once at startup
            // (kept inactive) so the existing _inGameBar.SetActive(false) call sites stay valid.
        }

        public void HideInGameBar()
        {
            _inGameBar.SetActive(false);
        }

        private void CreateInGameBar()
        {
            var bar = new GameObject("MultiplayerStatusBar");
            bar.transform.SetParent(EnsureBarCanvas(), false);
            var rect = bar.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 0);
            rect.anchorMax = new Vector2(1, 0);
            // Bar height scales with the theme so the bigger status font fits.
            var barH = LobbyTheme.ScaledRowFontSize + LobbyTheme.ScaledPadding / 2;
            rect.sizeDelta = new Vector2(0, barH);
            rect.anchoredPosition = new Vector2(0, 0);

            var bg = bar.AddComponent<Image>();
            bg.color = LobbyTheme.PageBackdrop;   // themed dark navy backing (was black@0.7)

            _barStatusText = CreateText(bar, "Status",
                new Vector2(10, 2), new Vector2(500, barH),
                "Not connected");

            _inGameBar = bar;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Update
        // ═══════════════════════════════════════════════════════════════════

        // Alt+F4 / native window close never runs the menu-quit path, so without this the other side
        // waits out the full 20 s heartbeat timeout. Best-effort leave notice on Unity's process-quit
        // callback (fires on Alt+F4 and normal quit; a hard kill/crash still falls back to the
        // heartbeat).
        //
        // QUEUING THE FAREWELL IS NOT SENDING IT. DirectTransport.Send only ENQUEUES the frame for that
        // peer's writer thread, and every one of those threads is IsBackground — the process teardown
        // that follows this callback kills them where they stand, so the ClientLeave could die in the
        // queue and the leaver would be reported to everyone else as a LOST CONNECTION instead of a
        // player who left. Shutdown() is the drain that already exists for exactly this: it requests
        // flush-then-close per peer and JOINs each writer under one bounded grace budget
        // (DirectTransport.Shutdown:117-127), so the farewell is on the wire before we exit. Steam P2P
        // needs no drain (sends hand off to the separate Steam client process, which delivers them after
        // the game is gone); a hard kill or a crash still reaches nobody, and the heartbeat backstop
        // reports that honestly as a lost connection, because that is what it is.
        private void OnApplicationQuit()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive) return;
            try
            {
                if (engine.IsHost) engine.Session?.SendHostDisconnected();
                else engine.Session?.SendClientLeave();
                engine.Shutdown();
            }
            catch (System.Exception e)
            {
                // Never block the exit — but never swallow it silently either: if this throws, the other
                // players are about to be told this player lost connection rather than left.
                MpLog.LogWarning("[Multiplayer] quit farewell not flushed (" + e.Message + ") — remaining " +
                                 "players will see a lost-connection notice instead of a leave notice.");
            }
        }

        private void Update()
        {
            // Drive the deferred TFTV guard-patch bind (arms on TFTV assembly load, binds one frame later).
            // MUST run BEFORE the session gate below: TFTV loads at startup/menu, long before any co-op session.
            Multiplayer.Harmony.TftvLateBinder.Tick();

            // Keep the status bar's aspect-adaptive match LIVE. ConfigureScaler runs once, at canvas
            // build, but this canvas outlives every resolution / windowed / monitor change the player
            // makes, and the native rule is a function of the CURRENT aspect. One float write per frame,
            // the same recompute LobbyPanel.Refresh already does — cheaper than an event we would have to
            // find, and correct on the frame the aspect actually moves.
            if (_barScaler != null) _barScaler.matchWidthOrHeight = LobbyTheme.CurrentMatch;
            // Same one float write for the gate's canvas, for the same reason and only while it is up.
            _gate?.Tick();

            var engine = NetworkEngine.Instance;
            // Shout this session on the local wire while hosting, so a peer's browser lists it without
            // anyone reading an address out loud. Self-throttling (2 s) and self-disposing when false.
            Multiplayer.Util.LanBeacon.HostTick(engine?.IsActive == true && engine.IsHost);
            if (engine?.IsActive == true)
            {
                engine.Update();

                // CLIENT-JOIN CONFIRMATION GATE. While a client join is in flight, open the real
                // lobby ONLY once the host has genuinely accepted us. The strongest available signal
                // is the host's first PEER_LIST: HandleConnectionRequest accepts the JOIN and then
                // BroadcastPeerList()s, and the client's HandlePeerList populates the roster. A bare
                // TCP connect (OnPeerConnected) is premature — the host may still reject the JOIN
                // (e.g. empty GUID) — so we wait for the roster, which is the host's explicit accept.
                // engine.Update() above drains this frame's packets first, so the roster is current.
                if (_clientConnecting && !engine.IsHost &&
                    (engine.Session?.GetLobbyRoster()?.Count ?? 0) > 0)
                {
                    MpLog.Log($"[Multiplayer][join] host ACCEPTED the join after " +
                              $"{UnityEngine.Time.realtimeSinceStartup - _joinStageStartedAt:F1}s " +
                              $"(stage {_joinAttemptIndex + 1}) — its first PEER_LIST arrived; opening the lobby.");
                    _clientConnecting = false;
                    _joinPlan = null; // cascade succeeded — drop the plan so a later drop doesn't retry
                    // Fix #4: the host's first PEER_LIST IS the explicit accept, so advance the FSM
                    // Joining→ClientLobby here (BeginJoin ran in OnLobbyJoin). Idempotent: JoinConfirmed
                    // no-ops if the FSM is not in Joining, so a re-entered frame can't corrupt the state.
                    _lobbyController.JoinConfirmed();
                    DismissConnectingBox();   // close "Connecting…" before showing the lobby
                    ShowInGameBar();

                    // VERSION GATE, client leg: the accept (which lands BEFORE this first PEER_LIST) already
                    // compared our Multiplayer version against the host's. If they differ, the player is told
                    // here — with the "Connecting…" box just closed and the lobby not yet shown, i.e. at the
                    // door, before any time is invested. Read once and cleared, so a later rejoin re-decides
                    // instead of replaying a stale notice. NOT a disconnect (L84): we go on to open the lobby
                    // on dismiss and the peer keeps its seat; the host's parity gate keeps THIS peer's READY
                    // locked and touches nobody else's. If the native box is unavailable, open the lobby
                    // anyway — a mismatch must never leave the player staring at nothing.
                    var versionNotice = engine.Session?.VersionMismatchNotice;
                    if (engine.Session != null) engine.Session.VersionMismatchNotice = "";
                    var versionBox = string.IsNullOrEmpty(versionNotice) ? null : GameUtl.GetMessageBox();
                    // ShowLobbyScreen, not a bare _lobby.Show(): the join may have been started from the
                    // gate, which is still latched open behind the "Connecting…" box. Closing it here is
                    // what hands the menu chrome over to the lobby and stops a later Reshow from springing
                    // the join screen back on top of a live session.
                    if (versionBox != null)
                        versionBox.ShowSimplePrompt(versionNotice, MessageBoxIcon.Error, MessageBoxButtons.OK,
                            delegate (MessageBoxCallbackResult _) { ShowLobbyScreen(); }, this);
                    else
                        ShowLobbyScreen();    // real, populated roster — never a fake empty lobby
                }

                // STAGE DEADLINE. Checked AFTER the confirmation gate above, so a roster that lands on the
                // deadline frame still wins. Routed through OnConnectionFailed and nowhere else: that is
                // already the one place that advances the cascade to the next transport and, on the last
                // stage, tears the half-open session down and names the reason — so a hung stage now behaves
                // exactly like a stage whose transport reported Failed, which is what the join code has
                // always documented ("failed/timed out (OnConnectionFailed)") and never actually had.
                else if (_clientConnecting && !engine.IsHost &&
                         UnityEngine.Time.realtimeSinceStartup - _joinStageStartedAt > JoinStageTimeoutSec)
                {
                    // Say that the DEADLINE is what ended this, before OnConnectionFailed says what happens
                    // next. Without this line an expired deadline and a still-hanging attempt read the same.
                    MpLog.LogWarning($"[Multiplayer][join] stage DEADLINE at " +
                                     $"{UnityEngine.Time.realtimeSinceStartup - _joinStageStartedAt:F1}s — no " +
                                     "PEER_LIST from the host, i.e. the transport connected but the host never " +
                                     "accepted the JOIN (it may never have seen one).");
                    OnConnectionFailed($"the host never accepted the join ({(int)JoinStageTimeoutSec}s). " +
                                       "It may be on a different build, or its replies are not reaching you.");
                }

                // THE LOBBY STARTS ITSELF. Every player ready (the host included) → LobbyCountdown arms a
                // five-second clock → it reaches zero → the SAME start OnLobbyPlay always ran. Driven from
                // here and not from SyncEngine.Tick, which does not run before the session starts, and not
                // from the lobby panel's Refresh, which only runs while the panel is on screen — a
                // countdown that stopped because a host alt-tabbed out of its own lobby would be the
                // silent-swallow shape this repo keeps paying for.
                //
                // The gate is re-projected from the live roster on every one of these frames, so the
                // countdown dies the moment it closes (see LobbyCountdown.HostTick). HostTick self-guards
                // on IsHost — but the ARGUMENT is evaluated BEFORE that guard can help, so the gate
                // projection is short-circuited here instead: EvaluateStartGate → RefreshGateFacts →
                // GetLobbyRoster → BuildPeerList allocates a fresh List EVERY FRAME, and without these two
                // terms it did so on every client and all the way through the in-game session, where no
                // lobby exists and the answer is a foregone false. HostTick still runs every frame (the
                // NEW CAMPAIGN route does not read the gate at all and must keep ticking).
                //
                // TWO ROUTES, ONE CLOCK. The same five seconds also guard the NEW CAMPAIGN confirm, which
                // the intercept REFUSES while this runs and which is re-issued here when it reaches zero
                // (NewCampaignInterceptPatch.CommitNewCampaign — the only caller, and the only door to
                // world creation). Which one fired rides the return value, so neither start grows a
                // second path beside itself.
                //
                // GUARDED, because the two fires below are the whole start: a throw out of either used to
                // escape Update with the lobby LOCKED (CommitStart already ran) and ReopenAfterFailedStart
                // unreachable — a lobby that could never start again and never said why.
                try
                {
                    bool startGate = engine.IsHost && _lobbyController.State == LobbyState.HostLobby
                                     && EvaluateStartGate();
                    switch (LobbyCountdown.HostTick(engine, startGate))
                    {
                        case LobbyCountdown.Route.Save: OnLobbyPlay(); break;
                        case LobbyCountdown.Route.NewCampaign: NewCampaignInterceptPatch.CommitNewCampaign(); break;
                    }
                }
                catch (System.Exception e)
                {
                    MpLog.LogError("[Multiplayer] the lobby countdown dispatch threw — reopening the lobby " +
                                   "rather than leaving it locked forever: " + e);
                    ReopenAfterFailedStart();
                }

                if (_barStatusText != null)
                {
                    _barStatusText.text = $"MP: {engine.Transport?.State ?? ConnectionState.Disconnected}" +
                        $" | {(engine.IsHost ? "Host" : "Client")}" +
                        $" | {engine.Session?.ClientCount ?? 0} player(s)";
                }
            }
            else
            {
                if (_barStatusText != null)
                    _barStatusText.text = "Not connected";
            }

            // Refresh the lobby panel EVERY frame while it is visible — UNCONDITIONALLY, i.e. not
            // gated on engine.IsActive. This is the fix for the "CONNECT rail empty on first open"
            // bug: the rail values (IP / Same-PC / STUN code) are re-pushed by LobbyPanel.Refresh,
            // so they fill within one frame of the host becoming ready on the FIRST open (no need to
            // reopen the lobby). It also makes a late-arriving STUN code appear LIVE once background
            // discovery resolves. Refresh() self-guards (hides itself if the engine is null/inactive
            // or the session has started), so calling it outside the IsActive branch is safe; it only
            // does real work while the panel is actually on screen.
            if (_lobby != null && _lobby.IsVisible)
                _lobby.Refresh();

            // THE PLAYER PANEL'S REPAINT SEAM. Unconditional and every frame, for the same reason the
            // lobby refresh above is: it is what makes a peer going ready / un-ready / silent appear on an
            // ALREADY-OPEN panel with no edge to subscribe to and no edge to miss. Sync() self-guards (it
            // hides itself outside a started session and catches everything), so calling it here is safe
            // in every scene including the main menu.
            _playerPanel?.Sync();
            // Same seam, same argument: an ARMING countdown must reach an already-open screen within one
            // frame of the rail batch that carried it, and a cancel must take it away the same way.
            _countdownPanel?.Sync();
            // Third reader of the same seam: a peer entering the deployment prep screen must put the join
            // door on every other peer's ALREADY-OPEN geoscape within one frame, and leaving it must take
            // the door away — with no view-state transition and no re-entry by the player.
            _prepJoinButton?.Sync();
            // THE POST-MISSION RETURN HOLD, released here and nowhere else. Unconditional for the same
            // reason the Sync above is: this loop is the only one that runs in the TACTICAL scene, and a
            // held return that never got a tick would strand the player on the summary screen.
            Multiplayer.Tactical.ReturnCountdown.Tick();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Helpers
        // ═══════════════════════════════════════════════════════════════════

        private static Text CreateText(GameObject parent, string name,
            Vector2 pos, Vector2 size, string content)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchoredPosition = pos;
            rect.sizeDelta = size;

            var text = go.AddComponent<Text>();
            text.font = LobbyTheme.Font;                       // native menu font (was Arial)
            text.fontSize = LobbyTheme.ScaledRowFontSize;      // themed, scales with UiScale (was 12)
            text.color = LobbyTheme.BodyText;
            text.alignment = TextAnchor.MiddleLeft;
            text.text = content;
            return text;
        }

        public void InvitePlayers()
        {
            try
            {
                var asm = FindSteamworksAssembly();
                if (asm == null)
                {
                    ShowSteamNotAvailable();
                    return;
                }

                var utilsType = asm.GetType("Steamworks.SteamUtils");
                if (utilsType != null)
                {
                    var overlayProp = utilsType.GetProperty("IsOverlayEnabled",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (overlayProp != null)
                    {
                        var enabled = (bool)overlayProp.GetValue(null, null);
                        if (!enabled)
                        {
                            ShowSteamOverlayUnavailable();
                            return;
                        }
                    }
                }

                // Open Steam's invite dialog for the host lobby (published at host start). SteamInvite
                // reports its own stage (overlay opened, or "lobby not ready — try again") so the button
                // is never a silent no-op; when the friend accepts, the Steam-P2P join path auto-connects.
                SteamProbe.OpenInviteOverlay();
            }
            catch { ShowSteamNotAvailable(); }
        }

        private static void ShowSteamNotAvailable()
        {
            var mb = GameUtl.GetMessageBox();
            mb?.ShowSimplePrompt("Steam overlay is not available.",
                MessageBoxIcon.Error, MessageBoxButtons.OK, null, null);
        }

        private static void ShowSteamOverlayUnavailable()
        {
            var mb = GameUtl.GetMessageBox();
            mb?.ShowSimplePrompt("Steam overlay is disabled.\nEnable it in Steam settings.",
                MessageBoxIcon.Warning, MessageBoxButtons.OK, null, null);
        }

        private static System.Reflection.Assembly FindSteamworksAssembly()
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = asm.GetName().Name;
                if (name == "Facepunch.Steamworks.Win64")
                    return asm;
            }
            return null;
        }
    }
}
