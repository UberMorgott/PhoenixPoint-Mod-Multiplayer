namespace Multiplayer.Network.MessageLayer
{
    public enum PacketType : byte
    {
        // Connection
        ConnectionRequest = 0x01,
        ConnectionAccepted = 0x02,
        ConnectionRejected = 0x03,
        // 0x04 (ClientDisconnected) removed 2026-07-22: zero senders ever — graceful quit rides
        //   ClientLeave 0x08, hard drops surface via the transport disconnect callback. Do NOT reuse.
        HostDisconnected = 0x05,
        Heartbeat = 0x06,
        HeartbeatAck = 0x07,
        ClientLeave = 0x08,
        PlayerRename = 0x09,

        // Session
        // 0x10 (InitialGameState) removed: empty seed path — no sender ever wired, handler just re-raised an
        //   event with zero subscribers. Do NOT reuse the id.
        // 0x11 (GameStateDelta) reserved: future geoscape/tactical delta-sync (empty stub removed 2026-07-06). Do NOT reuse.
        // 0x12, 0x13 (StateSyncRequest/Response) removed: empty stub — no sender/handler. Do NOT reuse the ids.
        ClientReady = 0x14,
        AllClientsReady = 0x15,
        // 0x16, 0x17 (PauseRequest/PauseAccepted) reserved: future cooperative pause feature (empty stub removed 2026-07-06). Do NOT reuse the ids.
        SaveChunk = 0x18,
        SaveDone = 0x19,
        LoadProgress = 0x1A,
        ClientLoaded = 0x1B,
        SessionBegin = 0x1C,
        RosterProgress = 0x1D,
        LoadComplete = 0x1E,
        RevealAll = 0x1F,

        // Tactical Actions
        TacticalActionRequest = 0x20,   // 0x21-0x24, 0x27 retired (legacy approve/reject/result/broadcast/turn-state path removed); do NOT reuse
        // 0x25 (EndTurnRequest) + 0x26 (EndTurnAccepted) retired: end-turn rides envelope TacIntentEndTurn 0x84 / TacTurn 0x85. Do NOT reuse the ids.

        // Campaign Actions
        // 0x30, 0x31, 0x32 (CampaignActionRequest/Approved/Rejected) removed: never sent, no handler — the
        //   geoscape approve/reject relay rides the 0x67 SyncEnvelope (GeoIntent 0xA2 / GeoOutcome 0xA3 /
        //   GeoReject 0xA4). Do NOT reuse the ids.
        // 0x33 (CampaignActionResult) + 0x34 (CampaignStateUpdate) removed: never sent, no handler. Do NOT reuse the ids.
        // 0x35 (GeoStateDiff) + 0x36 (GeoEntityOp) retired: orphan ids, no sender/handler; the diff codec they fronted no longer exists. Do NOT reuse the ids.
        // 0x37-0x3A (TimeAnchor/TimeRequest/TimeClockPing/TimeClockPong) removed 2026-07-26: zero senders/
        //   handlers — time control rides the 0xB0 GeoTimeIntent surface + the "TA" TimeAnchor root on the
        //   0xAC value rail (TimeAnchor.EnforceDrift is the standing corrector). Do NOT reuse the ids.

        // Management
        // 0x40 (PermissionUpdate) + 0x41 (SoldierAssignment) reserved: no sender ever wired — per-flag permission
        //   toggle + per-soldier ownership assignment are deferred future work (a host management UI would drive
        //   them). Handlers + serializers removed 2026-07-06. Do NOT reuse the ids.
        PlayerListUpdate = 0x42,
        SetSave = 0x43,
        ClientUnready = 0x44,   // client->host: un-ready toggle (pair of ClientReady=0x14; no payload, keyed by sender)
        JoinReady = 0x45,       // client->host: a MID-SESSION on-demand joiner reached the live geoscape (Playing) and
                                // is ready to be re-seeded; host replies with BroadcastFullWallet + BroadcastAllChannels.
                                // No payload — keyed by sender. See SaveTransferCoordinator on-demand-join path.
        ParityUpdate = 0x46,    // client->host: refreshed parity manifest after the client auto-applied the host's
                                // mod settings (host manifest rides ConnectionAccepted). Host re-compares, updates
                                // that client's roster ParityDiffs and re-broadcasts PEER_LIST (badge/lock clears).
        EntryTransferAbort = 0x47, // host->all: the tac-entry save transfer ABORTED (mid-tactical save write failed /
                                   // transfer never started) — clients drop the stashed deploy + stall watchdog and
                                   // lift their curtain back to the live geoscape mirror; the host self-reveals.
                                   // Payload: [reason:str] (diagnostics only — the abort action is unconditional).
        EntryTransferBegin = 0x48, // host->all: a tactical entry has BEGUN (host reached LaunchTacticalGame) — every
                                   // peer drops the native curtain NOW instead of staying fully interactive until its
                                   // own first save chunk arrives (13.0 s later in the 2026-07-31 live run: host
                                   // 00:24:06.037 vs both clients 00:24:19.04). No payload — the signal IS the event,
                                   // and the curtain drop is unconditional + idempotent with the first-chunk drop.
                                   // Pairs with 0x47: that one takes the same curtain back down. Law L71.
        LobbyCountdown = 0x49,     // host->all: the LOBBY start countdown ARMED or CLEARED. Payload
                                   // [secondsLeft:u8][route:u8] — non-zero ARMS, 0 CLEARS. The route is
                                   // LobbyCountdown.Route (1 = start from the chosen SAVE, 2 = the host's
                                   // NEW CAMPAIGN confirm, which the intercept refuses and re-issues when
                                   // this reaches zero); it rides the wire so a client's overlay names the
                                   // right thing. A one-byte arm reads as the save route. Exactly two writes per
                                   // countdown, never one per second: each peer counts its own display down
                                   // from the arm off local realtime (LobbyCountdown.DisplaySecondsLeft), the
                                   // same shape DeployCountdown already uses and for the same reason. It
                                   // needs a TOP-LEVEL id rather than the 0x67 sync rail because the rail is
                                   // not live before the session starts — the lobby is exactly the window
                                   // where no rail exists. Host-authoritative: only the host arms and clears.
        LobbyCountdownCancel = 0x4A, // client->host: veto the lobby countdown. No payload — keyed by sender,
                                   // which the host DOES read here (unlike the deployment veto): the cancel
                                   // clears the CANCELLER'S OWN ready, or every peer would still be ready the
                                   // instant the countdown died and it would re-arm on the very next frame.
                                   // Pair of 0x44 ClientUnready in shape and of 0x47 in direction-of-effect.
        ReturnCountdown = 0x4B,    // host->all: the post-battle RETURN countdown ARMED or CLEARED. Payload
                                   // [secondsLeft:u8] — non-zero ARMS, 0 CLEARS. Same two-write shape as
                                   // LobbyCountdown 0x49: each peer counts its own display down from the
                                   // arm off local realtime (ReturnCountdown.DisplaySecondsLeft). A top-level
                                   // id rather than a rail root because the tactical scene's rail lifetime
                                   // is ending and a mod-root write here would re-enter tactical UI that is
                                   // about to be torn down. Host-authoritative: only the host arms and clears.
        ReturnCountdownCancel = 0x4C, // client->host: veto the return countdown. No payload — any peer's
                                   // cancel stops the countdown for everyone. Unlike the lobby cancel (0x4A),
                                   // no ready state to clear — the battle is over and there is no gate to
                                   // re-arm the countdown automatically.
        RevealReady = 0x4D,        // any direction: "MY OWN first post-load frame has rendered" for one
                                   // slot. Payload [slot:u8][boundaryId:16]. Sent once per load boundary
                                   // by EVERY peer including the host, one frame after that peer arms its
                                   // reveal, and RELAYED by the host so every peer builds the SAME
                                   // ready-set locally and decides its own lift without asking anyone.
                                   // Pairs with 0x1E LoadComplete, which says the strictly earlier and far
                                   // cheaper thing ("my LOAD finished"): the ~2 s between the two is the
                                   // head start the host used to get. Law L554. NOT a readiness VOTE — it
                                   // reports a RENDERED FRAME, which happens with nobody at the keyboard,
                                   // so no human action gates it (P13 / NO QUORUMS).

        // ActionSync 0x60-0x6F
        // 0x60 (ActionRequest) + 0x61 (ActionApply) + 0x62 (ActionReject) RETIRED at the envelope cutover — the
        // geoscape action relay rides the 0x67 SyncEnvelope on GeoIntent 0xA2 / GeoOutcome 0xA3 / GeoReject 0xA4.
        // See the tombstone block below. Do NOT reuse the ids.
        // 0x63 (WalletSync) + 0x64 (StateSync) RETIRED — see the tombstone block below. Do NOT reuse the ids.
        SyncEnvelope  = 0x67,   // any direction: unified surface envelope [surfaceId:u8][kind:u8][len:u16][payload:N]
        // 0x65, 0x66, 0x68-0x6D (EventRaised/EventDismiss/ChoiceClaim/ReportModalShow/EventAdvanceResult/
        //   EventAdvanceRequest/ReportModalHide/GeoLogNotice) removed 2026-07-26: zero senders/handlers —
        //   the old repo's event/report/log mirroring never rode here (this repo: state mirrors via the
        //   0xAC value rail, presentation is client-local off the mirrored state — the client sim RUNS,
        //   only law-4b gates skip the mutators; see ClientSimGate). Do NOT reuse the ids.

        // Chat
        ChatMessage = 0x50,

        // Presentation (no rail, no surface id, no exactly-once guarantee — a lost one is not a problem).
        PingMarker = 0x51,      // any direction: "look here" marker, geoscape or tactical. Client→host→all,
                                // the ChatMessage fan-out shape. TOP-LEVEL on purpose: it never enters
                                // SurfaceRouter, so it needs no surface id — which matters because the
                                // geoscape band 0xA0-0xBF is full of tombstones and law L62 hard-requires
                                // the name prefix to match the band. Payload: [scene:u8][kind:u8] then
                                // either an entity ref / actor key (object ping) or 3 floats (point ping).
                                // See src/Lobby/PingMarkers.cs. Laws L158 (presentation alters nothing),
                                // L160 (moves no camera, enters no state, changes no selection).

        // Transport-specific (STUN hole punch, etc.)
        TransportInternal = 0xF0

        // ─── RETIRED / RESERVED wire ids — permanent tombstones, do NOT reuse ─────────────────
        // These ids were live in earlier revisions and have been removed. Their receivers are
        // gone; a new sender on any of them would be a silent-desync bug. Kept here (not as enum
        // members) so the ranges stay reserved and greppable.
        //   0x10             — InitialGameState (empty seed path; no sender, event had zero subscribers)
        //   0x11             — GameStateDelta (reserved: future geoscape/tactical delta-sync)
        //   0x12, 0x13       — StateSyncRequest/Response (empty stub; no sender/handler)
        //   0x16, 0x17       — PauseRequest/PauseAccepted (reserved: future cooperative pause feature)
        //   0x21-0x24, 0x27  — legacy tactical approve/reject/result/broadcast/turn-state path
        //   0x25, 0x26       — EndTurnRequest/Accepted → now envelope TacIntentEndTurn 0x84 / TacTurn 0x85
        //   0x30, 0x31, 0x32 — CampaignActionRequest/Approved/Rejected (never sent; relay rides 0x67 SyncEnvelope GeoIntent 0xA2 / GeoOutcome 0xA3 / GeoReject 0xA4)
        //   0x33, 0x34       — CampaignActionResult/CampaignStateUpdate (never sent, no handler)
        //   0x35, 0x36       — GeoStateDiff/GeoEntityOp (orphan; the diff codec they fronted is gone)
        //   0x40, 0x41       — PermissionUpdate/SoldierAssignment (reserved: future host management UI; no sender ever wired, handlers/serializers removed)
        //   0x60, 0x61, 0x62 — ActionRequest/ActionApply/ActionReject → action relay rides 0x67 SyncEnvelope
        //                      GeoIntent 0xA2 / GeoOutcome 0xA3 / GeoReject 0xA4
        //   0x63             — WalletSync → wallet rides the 0x67 SyncEnvelope generic value rail (0xAC;
        //                      its first envelope home, GeoWallet 0xA0, is itself retired)
        //   0x64             — StateSync → per-channel state rides the same 0xAC generic value rail
        //                      (its first envelope home, GeoState 0xA1, is itself retired)
        //   0x37-0x3A        — TimeAnchor/TimeRequest/TimeClockPing/TimeClockPong (legacy time channel → 0xB0 intent + "TA" root on 0xAC)
        //   0x65, 0x66, 0x68-0x6D — EventRaised/EventDismiss/ChoiceClaim/ReportModalShow/EventAdvanceResult/
        //                      EventAdvanceRequest/ReportModalHide/GeoLogNotice (old-repo event/report/log mirroring; never sent here)
    }
}
