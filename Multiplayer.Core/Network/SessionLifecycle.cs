using System;
using System.Collections.Generic;

namespace Multiplayer.Network
{
    /// <summary>
    /// Pure, Unity-free session-lifecycle helpers shared by the live wiring (SessionNotifier,
    /// MultiplayerUI, the in-game load intercept, the host-leave handler). Kept here, separate from
    /// the MonoBehaviour/Harmony code, so the message formatting, the F2 host-load guard, and the F3
    /// idempotency latch can be unit-tested directly (linked into Multiplayer.Tests).
    /// </summary>
    public static class SessionLifecycle
    {
        /// <summary>Fallback display name when a peer's real name is unknown (id-only event).</summary>
        public const string UnknownPlayer = "a player";

        /// <summary>
        /// The persistent system-chat / toast line for a peer connecting or dropping. Used by
        /// SessionNotifier for BOTH surfaces so the toast and the chat line never drift. The name is
        /// sanitized to <see cref="UnknownPlayer"/> when null/empty so a raw id is never shown.
        /// </summary>
        public static string FormatPeerEvent(bool connected, string playerName)
        {
            var name = string.IsNullOrEmpty(playerName) ? UnknownPlayer : playerName;
            return connected ? $"— {name} joined —" : $"— {name} left —";
        }

        /// <summary>The session-fatal line shown to a client when the host ends the session (F3).</summary>
        public const string HostEndedSession = "Host ended the session";

        /// <summary>
        /// F3 host-left suppression gate. A CLIENT closing its only peer (the host) on a VOLUNTARY
        /// LEAVE produces a transport drop that is byte-for-byte indistinguishable from a genuine host
        /// crash — the read-loop enqueues "connection lost" either way. To tell them apart we consult
        /// the local teardown intent: <see cref="NetworkEngine.IsIntentionalDisconnect"/> is set true at
        /// the top of every intentional teardown (Disconnect/Shutdown/TearDown) just before the peer
        /// socket is closed.
        ///
        /// Returns FALSE (suppress: no "Host ended the session" toast, no forced reload) when the local
        /// teardown was intentional — the client is leaving on its own, so it tears down + returns to
        /// the menu via its own leave path. Returns TRUE (notify) for a genuine UNEXPECTED host drop the
        /// client did not initiate. Pure + Unity-free so it is unit-testable.
        /// </summary>
        public static bool ShouldNotifyHostLeft(bool localDisconnectIntentional)
        {
            return !localDisconnectIntentional;
        }

        /// <summary>
        /// F2 host-load guard. True iff the host may re-run the chunked save transfer mid-session to
        /// pull every client into a newly picked save: we are the host, a networked session is up, the
        /// session has already started (we are in-game, not in the lobby), at least one client is
        /// connected, and no transfer is already in flight (re-entry guard — lock during, unlock after).
        /// </summary>
        public static bool HostLoadGuard(bool isHost, bool isActiveSession, bool sessionStarted,
            int connectedClientCount, bool transferActive)
        {
            return isHost
                && isActiveSession
                && sessionStarted
                && connectedClientCount >= 1
                && !transferActive;
        }

        /// <summary>
        /// Lobby save-pick gate. True iff a host with an ACTIVE co-op lobby whose session has NOT
        /// started chose a save from ANY native Load screen: that pick must become the lobby's chosen
        /// save (broadcast as a label only — NO campaign load), NOT trigger a real load. This is the
        /// DURABLE replacement for gating the intercept on the fragile static <c>_armed</c> flag (which
        /// could be false at click time because the lobby re-makes native menu buttons clickable, so the
        /// host can reach the native Load screen un-armed and the old guard let the load run).
        ///
        /// Deliberately EXCLUDES the mid-session F2 host load (<paramref name="sessionStarted"/> == true):
        /// that path is host-authoritative and SHOULD load immediately (re-running the chunked transfer;
        /// see <see cref="HostLoadGuard"/>). Also excludes non-host peers and single-player
        /// (<paramref name="lobbyActive"/> == false) so vanilla load is never captured.
        /// </summary>
        public static bool ShouldCaptureAsLobbyPick(bool isHost, bool lobbyActive, bool sessionStarted)
        {
            return isHost && lobbyActive && !sessionStarted;
        }

        /// <summary>
        /// In-session host-load gate. True iff a host whose co-op session has ALREADY STARTED reached a
        /// campaign-load convergence (<c>PhoenixSaveManager.LoadGame</c>) — i.e. via main-menu CONTINUE or
        /// Quickload (menu OR in-geoscape), which BYPASS the UI <c>OnLoadGamePressed</c> intercept. Such a
        /// load must NEVER run as a silent solo load while clients keep playing (it would desync them);
        /// the LoadGame prefix reroutes it into the host-authoritative in-session reload
        /// (<c>SaveTransferCoordinator.HostStartSessionInGame</c>, the F2 path) when the
        /// <see cref="HostLoadGuard"/> permits, or otherwise BLOCKS the solo load and logs.
        ///
        /// Deliberately EXCLUDES the lobby case (<paramref name="sessionStarted"/> == false): that is owned
        /// by <see cref="ShouldCaptureAsLobbyPick"/>. For a host in an active session, exactly ONE of the
        /// two predicates fires (partition on <paramref name="sessionStarted"/>). Non-host peers and the
        /// no-session single-player case return false so vanilla CONTINUE/Quickload is untouched.
        /// </summary>
        public static bool ShouldInterceptInSessionHostLoad(bool isHost, bool isActiveSession, bool sessionStarted)
        {
            return isHost && isActiveSession && sessionStarted;
        }

        /// <summary>
        /// Clientless-host solo-load allowance. True iff a host in an already-started co-op session has
        /// ZERO connected clients — i.e. every peer has left and the host is effectively alone. In that
        /// state a CONTINUE / Quickload has no peers to desync, so the vanilla solo load SHOULD proceed
        /// rather than being blocked: the in-game co-op reload reroute is itself >=1-client gated (see
        /// <see cref="HostLoadGuard"/>), so without this the lone host would be locked out of loading.
        ///
        /// This refines <see cref="ShouldInterceptInSessionHostLoad"/>: that gate still fires for the
        /// in-session host, but the LoadGame prefix consults THIS predicate first and, when it is true,
        /// lets the vanilla load run. Partitions cleanly with the reroute path on client count — with
        /// >=1 client this is false and the host load is intercepted (reroute or block) as before.
        /// </summary>
        public static bool HostInSessionHasNoClients(bool isHost, bool isActiveSession, bool sessionStarted,
            int connectedClientCount)
        {
            return isHost && isActiveSession && sessionStarted && connectedClientCount <= 0;
        }

        /// <summary>
        /// Client-load block gate. True iff a NON-HOST peer in an ACTIVE co-op session reached a
        /// campaign-load convergence (<c>PhoenixSaveManager.LoadGame</c>) — via main-menu CONTINUE,
        /// pause-menu LOAD, or in-game Quickload (all of which BYPASS the host-only UI intercept and
        /// fall through to vanilla <c>LoadGame</c>). In a host-authoritative model ONLY the host may
        /// load; a client solo-loading a save while still wired into the live session desyncs it from
        /// the host. The LoadGame prefix returns <c>false</c> (block) and surfaces a "only the host can
        /// load" notice when this fires.
        ///
        /// Deliberately the COMPLEMENT of the host gates on the host axis: it fires ONLY for
        /// <paramref name="isHost"/> == false, so the host lobby-capture and in-session-reroute paths are
        /// never claimed by it. Excludes the no-session case (<paramref name="isActiveSession"/> == false)
        /// so ordinary single-player CONTINUE/Quickload on a non-host machine passes through untouched.
        /// </summary>
        public static bool ShouldBlockClientLoad(bool isHost, bool isActiveSession)
        {
            return isActiveSession && !isHost;
        }

        /// <summary>
        /// Mid-session on-demand-join gate (P1). True iff the host may kick a PER-PEER save transfer to a
        /// brand-new peer that connected AFTER the session started: we are the host, the co-op session is
        /// already started (the joiner missed the lobby start), the host is in the live GEOSCAPE (a joiner
        /// can only be reproduced from a geoscape save — the tactical deploy snapshot is turn-0, so an
        /// in-progress battle cannot be joined; see <see cref="ShouldRejectMidSessionJoin"/>), and no full
        /// save transfer is already in flight (<paramref name="transferActive"/> == false) so the on-demand
        /// unicast never overlaps a global F2 re-transfer that is already reseeding every peer.
        ///
        /// The trigger is per-peer + unicast, so it must NOT touch the global LOADED barrier or any host
        /// monotonic counter — already-connected clients are untouched by a join. Pure + Unity-free.
        /// </summary>
        public static bool MidSessionJoinGuard(bool isHost, bool sessionStarted, bool geoscapeActive,
            bool transferActive)
        {
            return isHost
                && sessionStarted
                && geoscapeActive
                && !transferActive;
        }

        /// <summary>
        /// Mid-session join REJECTION gate (P1 boundary). True iff a NEW peer's JOIN must be rejected with a
        /// user-visible notice because the host cannot onboard it right now: the session has already started
        /// (mid-session) but the host is NOT in the live geoscape — i.e. a tactical battle is in progress or
        /// the host is mid-load. The tactical deploy snapshot is turn-0 (audit §5: a live mission cannot be
        /// joined mid-fight), so the joiner is bounced with "wait until the host is back on the Geoscape"
        /// rather than dropped into an inconsistent state. Before session start (lobby) this is false — the
        /// normal lobby join/start path owns that case. Pure + Unity-free.
        /// </summary>
        public static bool ShouldRejectMidSessionJoin(bool sessionStarted, bool geoscapeActive)
        {
            return sessionStarted && !geoscapeActive;
        }

        /// <summary>
        /// New-campaign co-op bootstrap ARM gate (P0). True iff the HOST may run the NATIVE
        /// new-campaign flow as a co-op bootstrap: we are the host, the co-op lobby is up (active
        /// session) and the session has NOT started yet (still in the lobby), and no save transfer is
        /// already in flight. Deliberately does NOT require a connected/ready client: the transfer
        /// fires only later, at the first playable geoscape frame, and a host who bootstraps alone
        /// simply starts the campaign — later peers onboard via the P1 mid-session join. The
        /// mid-session "second fresh campaign" case (sessionStarted == true) is owned by the EXISTING
        /// <see cref="HostLoadGuard"/> (it is exactly an F2 host reload with a to-be-created save).
        /// Pure + Unity-free.
        /// </summary>
        public static bool NewCampaignArmGuard(bool isHost, bool isActiveSession, bool sessionStarted,
            bool transferActive)
        {
            return isHost
                && isActiveSession
                && !sessionStarted
                && !transferActive;
        }

        /// <summary>
        /// Autosave-meta handoff check, shared by the P1 on-demand join capture and the P0
        /// new-campaign bootstrap. The game's <c>AutosaveGame</c> produces a NEW
        /// <c>SavegameMetaData</c> instance on a successful capture, so a capture is FRESH iff the
        /// post-save <c>SaveManager.AutoSave</c> is non-null and not the SAME instance as before the
        /// save (ironman substitution or a write failure leaves the old instance in place — the
        /// caller must abort rather than ship a stale blob). Pure + Unity-free (reference identity).
        /// </summary>
        public static bool FreshAutosaveCaptured(object previousAutosaveMeta, object capturedAutosaveMeta)
        {
            return capturedAutosaveMeta != null
                && !ReferenceEquals(capturedAutosaveMeta, previousAutosaveMeta);
        }

        /// <summary>
        /// Returning-peer rejoin prune decision (Inc5 part 2). A JOIN whose persistent playerGUID is
        /// ALREADY bound to a roster entry is a RECONNECT of a known player whose previous connection
        /// died — possibly a death the transport never reported (crash before the 20 s heartbeat
        /// timeout), possibly over a DIFFERENT transport address, possibly over the SAME reused peer id
        /// (Steam ids are stable). Returns every connected peer id whose bound identity equals
        /// <paramref name="joiningGuid"/> — the DEAD connections' residue the host must prune before
        /// onboarding the returning peer through the normal on-demand join path. Non-empty result ⇔
        /// returning peer; empty ⇔ brand-new peer (nothing to prune).
        ///
        /// A same-id reconnect returns the joining peer's OWN old entry (the caller removes it and the
        /// JOIN handler re-adds it fresh, dropping stale ready/heartbeat residue). A first-time joiner
        /// never matches: its pre-JOIN roster entry (added at transport connect) carries
        /// <see cref="Guid.Empty"/>, and an empty <paramref name="joiningGuid"/> matches nothing
        /// (defense-in-depth — the JOIN handler already rejects empty identities). Pure + idempotent:
        /// after the caller removes the returned ids, a second call yields an empty list, so a
        /// double-reconnect race prunes cleanly both times.
        /// </summary>
        public static List<ulong> StaleRejoinPeers(
            IEnumerable<KeyValuePair<ulong, Guid>> connectedPeerIdentities, Guid joiningGuid)
        {
            var stale = new List<ulong>();
            if (joiningGuid == Guid.Empty || connectedPeerIdentities == null) return stale;
            foreach (var peer in connectedPeerIdentities)
                if (peer.Value == joiningGuid)
                    stale.Add(peer.Key);
            return stale;
        }

        /// <summary>
        /// Self-identity collision guard (second-line defense). True iff a JOINing peer presents the
        /// SAME persistent playerGUID as the HOST's own identity. That only happens as an operational
        /// misconfiguration: two same-machine instances shared <c>persistentDataPath/identity.json</c>
        /// and loaded the same guid (see <c>ClientIdentity</c>). Onboarding such a peer would silently
        /// collapse it into the host's slot 0 — <c>SlotAllocator</c> seeds the host guid at slot 0, so
        /// <c>Assign(hostGuid)</c> returns 0 — and would share the host's permission/ownership key (both
        /// keyed by the guid). The host must REFUSE the JOIN loudly instead of degrading silently. Empty
        /// guids are rejected upstream (missing-identity guard) and are never a collision here. Pure +
        /// Unity-free so it is unit-testable.
        /// </summary>
        public static bool IsSelfIdentityCollision(Guid hostGuid, Guid joiningGuid)
        {
            return joiningGuid != Guid.Empty && joiningGuid == hostGuid;
        }
    }

    /// <summary>
    /// One-shot idempotency latch for the F3 host-leave handler. The host leaving can be signalled
    /// twice — a graceful <c>HostDisconnected</c> packet followed by the transport drop of the same
    /// peer (or a heartbeat timeout) — and the client must return to the main menu exactly once.
    /// <see cref="TryHandle"/> returns true only on the FIRST call; every later call returns false.
    /// Pure + Unity-free so it is unit-testable; <see cref="Reset"/> re-arms it for the next session.
    /// </summary>
    public sealed class HostLeaveLatch
    {
        private bool _handled;

        /// <summary>True once the host-leave has been handled (the menu return has fired).</summary>
        public bool Handled => _handled;

        /// <summary>Latch and return true on the FIRST call only; false on every subsequent call.</summary>
        public bool TryHandle()
        {
            if (_handled) return false;
            _handled = true;
            return true;
        }

        /// <summary>Re-arm the latch for a fresh session (called on a new host/join).</summary>
        public void Reset() => _handled = false;
    }
}
