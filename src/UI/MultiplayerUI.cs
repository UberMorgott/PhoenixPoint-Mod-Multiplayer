using System.Collections.Generic;
using Base.Core;
using Base.Serialization;
using Base.UI.MessageBox;
using Multipleer.Harmony;
using Multipleer.Network;
using Multipleer.Transport;
using Multipleer.Util;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Multipleer.UI
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

        // ─── In-game bottom bar ─────────────────────────────────────────────
        private GameObject _inGameBar;
        private Text _barStatusText;

        // ─── Lobby panel (built once the menu Canvas is captured) ───────────
        private LobbyPanel _lobby;
        private SavePickerPanel _savePicker;
        private bool _panelsBuilt;

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

        // True while the lobby overlay is on-screen. Consumed by the main-menu Escape patch
        // (MainMenuPatches) so Escape leaves the lobby instead of opening the Options menu.
        public bool IsLobbyOpen => _lobby?.IsVisible == true;

        private void Awake()
        {
            Instance = this;

            // Bar canvas first so the in-geoscape status bar has a valid render root.
            EnsureBarCanvas();
            CreateInGameBar();
            _inGameBar.SetActive(false);

            // Panels are built lazily in OnMenuReady (they need the native menu Canvas).
            _lobby = new LobbyPanel(this);
            _savePicker = new SavePickerPanel();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Menu hook — native Canvas captured by InjectNetworkButtonPatch
        // ═══════════════════════════════════════════════════════════════════

        // Called from InjectNetworkButtonPatch.Postfix once the main-menu Canvas + native
        // button template are captured. Builds the lobby + save-picker as cloned NATIVE
        // widgets parented to the menu's own Canvas (native look + correct CanvasScaler/sort).
        public void OnMenuReady(Canvas menuCanvas)
        {
            if (_panelsBuilt || menuCanvas == null) return;
            _lobby.Build(menuCanvas);
            // Picker builds its OWN ROOT canvas under the mod's ModGO transform (this MonoBehaviour),
            // mirroring the bar/lobby canvases — a root canvas honours its CanvasScaler and its
            // overrideSorting wins, so the picker is a correctly-sized modal on top, not a nested
            // canvas crammed behind the lobby. It clones its native widgets via Resources, so it no
            // longer needs the menu canvas.
            _savePicker.Build(transform);
            _panelsBuilt = true;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Minimal overlay Canvas for the in-game status bar
        // ═══════════════════════════════════════════════════════════════════

        private Transform EnsureBarCanvas()
        {
            if (_barRoot != null) return _barRoot;

            var go = new GameObject("MultipleerBarCanvas");
            // Parent under ModGO so the Canvas shares the mod's persistent lifetime.
            go.transform.SetParent(transform, false);

            _barCanvas = go.AddComponent<Canvas>();
            _barCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _barCanvas.sortingOrder = 5000;

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            _barRoot = go.transform;
            return _barRoot;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Main entry — called from button click
        // ═══════════════════════════════════════════════════════════════════

        public void ShowNetworkMenu()
        {
            if (NetworkEngine.Instance?.IsActive == true)
            {
                // Already in a session → just (re)show the live lobby panel.
                _lobby?.Show();
                return;
            }
            StartHostAndOpenLobby();
        }

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
            ShowInGameBar();
            _lobby?.Show();
        }

        // ═══ Lobby panel callbacks ═════════════════════════════════════════

        // LEAVE button on the lobby panel: tear down the session and hide the lobby + bar.
        public void OnLobbyLeave()
        {
            _lobby?.Hide();
            OnDisconnectClicked();
        }

        // Ready toggle from the lobby panel.
        public void OnLobbyToggleReady()
        {
            var engine = NetworkEngine.Instance;
            if (engine?.Session == null) return;

            if (engine.IsHost)
            {
                // Host toggles its own ready state locally, then re-broadcasts the roster.
                engine.Session.HostReady = !engine.Session.HostReady;
                engine.Session.BroadcastPeerList();
            }
            else
            {
                // Client → host ClientReady (foundation keys by sender; host broadcasts updated roster).
                engine.Session.SetClientReady(engine.LocalSteamId);
            }
        }

        // Nickname edit committed in the lobby panel.
        public void OnLobbyRename(string newName)
        {
            if (string.IsNullOrWhiteSpace(newName)) return;
            NetworkEngine.Instance?.Session?.SendRename(newName.Trim());
        }

        // Host PLAY now transfers the already-chosen save (rail-selected); if none chosen, open
        // the picker as a last-chance fallback.
        public void OnLobbyPlay()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsHost) return;

            if (_pendingChosenSave != null)
            {
                engine.SaveTransfer?.HostStartSession(_pendingChosenSave);
                return;
            }
            // No save chosen in the rail yet → open the picker as a fallback.
            _savePicker?.Show(chosen =>
            {
                _pendingChosenSave = chosen;
                engine.Session?.SetChosenSave(SaveDisplayName(chosen), SaveDisplayMeta(chosen));
                NetworkEngine.Instance?.SaveTransfer?.HostStartSession(chosen);
            });
        }

        // ─── Smart-Join (footer Join…) ─────────────────────────────────────

        // Native ShowInputPrompt → classify → drop our own host lobby → join as client.
        public void OnLobbyJoin(string input)
        {
            var target = SmartJoinParser.Parse(input ?? "");
            if (target.Kind == JoinKind.Invalid)
            {
                // Reached via OnLobbyJoinPrompt's OK path, which left the lobby HIDDEN. Keep it hidden
                // behind this error box (HideForNativeScreen is idempotent) so the native error shows
                // through cleanly, then re-show the lobby when the box is dismissed.
                var mbErr = GameUtl.GetMessageBox();
                _lobby?.HideForNativeScreen();
                mbErr?.ShowSimplePrompt("Could not read that address or code.",
                    MessageBoxIcon.Error, MessageBoxButtons.OK,
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

                NetworkEngine.Create();
                switch (target.Kind)
                {
                    case JoinKind.DirectIp:
                        NetworkEngine.Instance.Initialize(TransportType.DirectIP);
                        NetworkEngine.Instance.OnConnectionFailed += OnConnectionFailed;
                        NetworkEngine.Instance.JoinGame(target.Ip, target.Port);
                        break;
                    case JoinKind.StunCode:
                        NetworkEngine.Instance.Initialize(TransportType.StunUDP);
                        NetworkEngine.Instance.OnConnectionFailed += OnConnectionFailed;
                        NetworkEngine.Instance.JoinGame($"{target.Ip}:{target.Port}", 0);
                        break;
                    case JoinKind.SteamId:
                        NetworkEngine.Instance.Initialize(TransportType.SteamP2P);
                        NetworkEngine.Instance.OnConnectionFailed += OnConnectionFailed;
                        NetworkEngine.Instance.JoinGame(target.SteamId.ToString(), 0);
                        break;
                }
                // DO NOT open the lobby yet. The connect is async + time-bounded (up to 10s) and the
                // host has not accepted us until its first PEER_LIST arrives — opening the lobby here
                // would show a FAKE empty lobby that an async failure then kicks us out of. Instead
                // enter the "connecting" state: keep the lobby HIDDEN (OnLobbyJoinPrompt already hid
                // it via HideForNativeScreen) behind a native "Connecting…" box with a CANCEL button.
                // Update() opens the real lobby once the join is confirmed; OnConnectionFailed tears
                // down + shows the error on failure/timeout; CANCEL aborts. See ShowConnectingBox.
                ShowConnectingBox();
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Multipleer] Join attempt failed: {ex}");
                // Reset any half-open session so a subsequent retry / host still works cleanly.
                NetworkEngine.Instance?.Shutdown();
                OnConnectionFailed($"Join failed: {ex.Message}");
            }
        }

        // Footer Join… button → native modal input → OnLobbyJoin.
        public void OnLobbyJoinPrompt()
        {
            var mb = GameUtl.GetMessageBox();
            if (mb == null) return;

            // Hide the lobby overlay (proven Load-screen approach) so the native input prompt shows
            // through cleanly, then bring it back on dismiss. CANCEL re-shows the lobby here. On OK we
            // hand off to OnLobbyJoin while the lobby is STILL hidden — OnLobbyJoin owns the visibility
            // from there: its valid path re-shows the lobby after connecting (ShowInGameBar + Show),
            // and its invalid-input branch shows its OWN error box (lobby hidden behind it) and
            // re-shows the lobby on that box's dismiss. This sequences the chained prompts so the
            // visible element is always on top and the lobby always returns once everything is gone.
            _lobby?.HideForNativeScreen();
            mb.ShowInputPrompt("Paste IP or code… (auto-detect)", "", null,
                MessageBoxIcon.Question, MessageBoxButtons.OKCancel,
                delegate (MessageBoxCallbackResult res)
                {
                    // Empty submit is impossible through OK (the native ValidateResult blocks
                    // whitespace-only OK), and an empty string reaching OnLobbyJoin classifies as
                    // Invalid → error box, never a connect-to-nothing. So empty == cancel/no-op.
                    if (res.DialogResult == MessageBoxResult.OK)
                        OnLobbyJoin(res.InputTextResult ?? "");
                    else
                        _lobby?.Show();
                }, this);

            // Web-style: clear the field and show a grey hint behind a blinking caret.
            TryUpgradePromptInput("IP:port or STUN code");
        }

        // ─── Nickname rename (own roster row pencil) ───────────────────────

        public void OnLobbyRenamePrompt()
        {
            var mb = GameUtl.GetMessageBox();
            if (mb == null) return;
            var current = NetworkEngine.Instance?.IsHost == true
                ? NetworkEngine.Instance.Session?.HostNickname ?? ""
                : SystemInfo.deviceName;

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

            // Clear any prefilled value so the placeholder hint shows. An empty submit can never
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
        // empty); the native screen is the game's verified save UI. Falls back to the from-code picker
        // only if the native Load button can't be reached.
        public void OnLobbyChooseSave()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsHost) return;

            // The handler that records + broadcasts the chosen save (identical to the old picker path).
            System.Action<SavegameMetaData> onPicked = chosen =>
            {
                if (chosen == null) return;
                _pendingChosenSave = chosen;
                var name = SaveDisplayName(chosen);
                var meta = SaveDisplayMeta(chosen);
                NetworkEngine.Instance?.Session?.SetChosenSave(name, meta);
            };

            // Arm the intercept, hide our overlay so the native screen shows through, then open it by
            // firing the live LoadGameButton. If the native open fails, disarm + fall back to the
            // legacy from-code picker so the host is never stuck.
            SaveLoadInterceptPatch.Arm(onPicked);
            _lobby?.HideForNativeScreen();
            if (!SaveLoadInterceptPatch.OpenNativeLoadScreen())
            {
                SaveLoadInterceptPatch.Disarm();
                _lobby?.Show();
                _savePicker?.Show(onPicked);
            }
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

        // Host STUN short code (or a status placeholder while discovering / on failure).
        // The host's CompositeTransport always includes a hosting StunTransport, so the code
        // shows whenever discovery succeeds — alongside the DirectIP and Steam rail values.
        public string GetRailStunCode()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsHost) return "(host on STUN to share)";
            var stun = FindHostingChild(TransportType.StunUDP);
            if (stun == null) return "(host on STUN to share)";
            if (stun.PublicEndPoint == null)
            {
                // Mirror the StunTransport discovery thread's state messages (it retries across a
                // pool of Google STUN servers and only marks itself unavailable after exhausting all
                // rounds). "unavailable" = environmentally blocked (symmetric NAT / firewall); the
                // rail re-reads every frame, so a late success still appears live without reopening.
                if (stun.LocalEndpoint.Contains("unavailable"))
                    return "unavailable (NAT/firewall)";
                return "discovering…";
            }
            var code = ConnectCode.Encode(stun.PublicEndPoint);
            return code ?? "unavailable (NAT/firewall)";
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

        public void CopyToClipboard(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            GUIUtility.systemCopyBuffer = text;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Connection events
        // ═══════════════════════════════════════════════════════════════════

        private void OnConnectionFailed(string reason)
        {
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
            _inGameBar?.SetActive(false);

            var mb = GameUtl.GetMessageBox();
            if (mb != null)
            {
                // Fires asynchronously after a smart-join attempt while the lobby is still HIDDEN (we
                // never opened it for an unconfirmed join). Keep it hidden behind this error box, then
                // HIDE it on dismiss: the session is gone, so we return to the plain main menu where
                // the user can re-open the network menu to host or retry the join. NO empty lobby is
                // ever shown on the client failure path.
                _lobby?.HideForNativeScreen();
                mb.ShowSimplePrompt($"Connection failed: {reason}",
                    MessageBoxIcon.Error, MessageBoxButtons.OK,
                    delegate (MessageBoxCallbackResult _) { _lobby?.Hide(); }, this);
            }
            else
            {
                _lobby?.Hide();
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
        private void ShowConnectingBox()
        {
            _clientConnecting = true;
            _connectingBox = GameUtl.GetMessageBox();
            _connectingBox?.ShowSimplePrompt("Connecting to host…",
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
            _clientConnecting = false;
            _connectingBox = null;
            NetworkEngine.Instance?.Disconnect();
            NetworkEngine.Instance?.Shutdown();
            _inGameBar?.SetActive(false);
            _lobby?.Hide();
        }

        private void OnDisconnectClicked()
        {
            NetworkEngine.Instance?.Disconnect();
            NetworkEngine.Instance?.Shutdown();
            _inGameBar.SetActive(false);
            _lobby?.Hide();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  In-game bar
        // ═══════════════════════════════════════════════════════════════════

        public void ShowInGameBar()
        {
            _inGameBar.SetActive(true);
        }

        public void HideInGameBar()
        {
            _inGameBar.SetActive(false);
        }

        private void CreateInGameBar()
        {
            var bar = new GameObject("MultipleerStatusBar");
            bar.transform.SetParent(EnsureBarCanvas(), false);
            var rect = bar.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 0);
            rect.anchorMax = new Vector2(1, 0);
            rect.sizeDelta = new Vector2(0, 28);
            rect.anchoredPosition = new Vector2(0, 0);

            var bg = bar.AddComponent<Image>();
            bg.color = new Color(0, 0, 0, 0.7f);

            _barStatusText = CreateText(bar, "Status",
                new Vector2(10, 2), new Vector2(500, 24),
                "Not connected");

            _inGameBar = bar;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Update
        // ═══════════════════════════════════════════════════════════════════

        private void Update()
        {
            var engine = NetworkEngine.Instance;
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
                    _clientConnecting = false;
                    DismissConnectingBox();   // close "Connecting…" before showing the lobby
                    ShowInGameBar();
                    _lobby?.Show();           // real, populated roster — never a fake empty lobby
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
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = 12;
            text.color = Color.white;
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

                var friendsType = asm.GetType("Steamworks.SteamFriends");
                if (friendsType == null)
                {
                    ShowSteamNotAvailable();
                    return;
                }

                var method = friendsType.GetMethod("OpenOverlay",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null, new[] { typeof(string) }, null);

                if (method != null)
                {
                    method.Invoke(null, new object[] { "friends" });
                    return;
                }

                ShowSteamNotAvailable();
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

        // ═══════════════════════════════════════════════════════════════════
        //  Pause menu — shown when NETWORK is clicked during gameplay
        // ═══════════════════════════════════════════════════════════════════

        public void ShowPauseMenu()
        {
            var engine = NetworkEngine.Instance;
            var mb = GameUtl.GetMessageBox();
            if (mb == null) return;

            if (engine == null || !engine.IsActive)
            {
                StartHostAndOpenLobby();
                return;
            }

            var info = BuildConnectionInfo(engine);
            var labels = new Dictionary<MessageBoxButtons, string>();

            labels[MessageBoxButtons.Yes] = "DISCONNECT";
            labels[MessageBoxButtons.Cancel] = "CLOSE";

            var transport = engine.Transport;
            if (transport != null)
            {
                if (transport.TransportType == TransportType.SteamP2P)
                    labels[MessageBoxButtons.No] = "COPY STEAM ID";
                else if (transport.TransportType == TransportType.DirectIP)
                    labels[MessageBoxButtons.No] = "COPY IP:PORT";
                else
                    labels[MessageBoxButtons.No] = "COPY STUN CODE";
            }
            else
            {
                labels[MessageBoxButtons.No] = "COPY INFO";
            }

            labels[MessageBoxButtons.Abort] = "INVITE FRIEND";

            mb.ShowSimplePrompt(info, MessageBoxIcon.Information,
                MessageBoxButtons.Yes | MessageBoxButtons.No | MessageBoxButtons.Abort | MessageBoxButtons.Cancel,
                labels, OnPauseMenuResult, this);
        }

        private static string BuildConnectionInfo(NetworkEngine engine)
        {
            var transport = engine.Transport;
            var transportName = transport != null ? transport.TransportType.ToString() : "None";
            var endpoint = transport?.LocalEndpoint ?? "N/A";
            var role = engine.IsHost ? "HOST" : "CLIENT";
            var steamId = engine.LocalSteamId != 0 ? engine.LocalSteamId.ToString() : "N/A";

            var clientsStr = "";
            if (engine.Session != null)
            {
                var count = engine.Session.ClientCount;
                clientsStr = $"\n  Players: {count + 1} (including you)";
                if (count > 0)
                {
                    clientsStr += "\n  Connected:";
                    foreach (var c in engine.Session.GetConnectedClients())
                        clientsStr += $"\n    \u2022 {c}";
                }
            }

            var copyable = GetCopyableInfo(engine);

            return $"Network Session\n" +
                   $"\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\n" +
                   $"Transport: {transportName}\n" +
                   $"Role: {role}\n" +
                   $"Steam ID: {steamId}\n" +
                   $"Endpoint: {endpoint}" +
                   clientsStr +
                   $"\n\nCopyable: {copyable}";
        }

        private static string GetCopyableInfo(NetworkEngine engine)
        {
            if (engine.Transport == null) return engine.LocalSteamId.ToString();

            switch (engine.Transport.TransportType)
            {
                case TransportType.SteamP2P:
                    return engine.LocalSteamId.ToString();
                case TransportType.DirectIP:
                    return engine.Transport.LocalEndpoint
                        .Replace("DirectIP(host:", "")
                        .Replace("DirectIP(client:", "")
                        .Replace(")", "");
                case TransportType.StunUDP:
                    return engine.Transport.LocalEndpoint
                        .Replace("STUN(", "")
                        .Replace(")", "");
                default:
                    return engine.Transport.LocalEndpoint;
            }
        }

        private void OnPauseMenuResult(MessageBoxCallbackResult res)
        {
            if (res.DialogResult == MessageBoxResult.Yes)
            {
                OnDisconnectClicked();
            }
            else if (res.DialogResult == MessageBoxResult.No)
            {
                var engine = NetworkEngine.Instance;
                if (engine != null)
                {
                    var text = GetCopyableInfo(engine);
                    GUIUtility.systemCopyBuffer = text;
                    var mb = GameUtl.GetMessageBox();
                    if (mb != null)
                        mb.ShowSimplePrompt($"Copied to clipboard:\n{text}",
                            MessageBoxIcon.Information, MessageBoxButtons.OK, null, this);
                }
            }
            else if (res.DialogResult == MessageBoxResult.Abort)
            {
                InvitePlayers();
            }
        }
    }
}
