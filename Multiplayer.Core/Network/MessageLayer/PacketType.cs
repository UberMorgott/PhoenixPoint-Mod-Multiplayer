namespace Multiplayer.Network.MessageLayer
{
    public enum PacketType : byte
    {
        // Connection
        ConnectionRequest = 0x01,
        ConnectionAccepted = 0x02,
        ConnectionRejected = 0x03,
        ClientDisconnected = 0x04,
        HostDisconnected = 0x05,
        Heartbeat = 0x06,
        HeartbeatAck = 0x07,
        ClientLeave = 0x08,
        PlayerRename = 0x09,

        // Session
        InitialGameState = 0x10,
        GameStateDelta = 0x11,
        StateSyncRequest = 0x12,
        StateSyncResponse = 0x13,
        ClientReady = 0x14,
        AllClientsReady = 0x15,
        PauseRequest = 0x16,
        PauseAccepted = 0x17,
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
        CampaignActionRequest = 0x30,
        CampaignActionApproved = 0x31,
        CampaignActionRejected = 0x32,
        // 0x33 (CampaignActionResult) + 0x34 (CampaignStateUpdate) removed: never sent, no handler. Do NOT reuse the ids.
        // 0x35 (GeoStateDiff) + 0x36 (GeoEntityOp) retired: orphan ids, no sender/handler; the diff codec they fronted no longer exists. Do NOT reuse the ids.
        TimeAnchor = 0x37,    // host->all: authoritative anchor {version, tAnchor, gAnchor, paused, speedIndex}
        TimeRequest = 0x38,   // client->host: time-control request {paused, speedIndex}
        TimeClockPing = 0x39, // client->host: NTP-style offset ping {pingId, t0}
        TimeClockPong = 0x3A, // host->client: NTP-style offset pong {pingId, t0, t1}

        // Management
        PermissionUpdate = 0x40,
        SoldierAssignment = 0x41,
        PlayerListUpdate = 0x42,
        SetSave = 0x43,
        ClientUnready = 0x44,   // client->host: un-ready toggle (pair of ClientReady=0x14; no payload, keyed by sender)
        JoinReady = 0x45,       // client->host: a MID-SESSION on-demand joiner reached the live geoscape (Playing) and
                                // is ready to be re-seeded; host replies with BroadcastFullWallet + BroadcastAllChannels.
                                // No payload — keyed by sender. See SaveTransferCoordinator on-demand-join path.

        // ActionSync 0x60-0x6F
        ActionRequest = 0x60,   // client -> host
        ActionApply   = 0x61,   // host -> all
        ActionReject  = 0x62,   // host -> originator
        // 0x63 (WalletSync) + 0x64 (StateSync) RETIRED — see the tombstone block below. Do NOT reuse the ids.
        EventRaised   = 0x65,   // host -> all: show a geoscape event dialog on clients [eventId][siteId]
        EventDismiss  = 0x66,   // host -> all: close the open geoscape event dialog on clients [eventId]
        SyncEnvelope  = 0x67,   // any direction: unified surface envelope [surfaceId:u8][kind:u8][len:u16][payload:N]
        // 0x68 retired (was ChoiceClaim): geoscape event-choice resolution now rides AnswerEventAction over the
        // research-style ActionRequest/ActionApply relay (occId on the action wire). Do NOT reuse this id.
        ReportModalShow = 0x69, // host -> all: show a geoscape REPORT modal on clients (Phase-A report-window mirror)
        EventAdvanceResult = 0x6A, // host -> all: single-choice PROMPT->RESULT advance (no native CompleteEvent fires); reuses the EventDismiss codec (occId/eventId/choiceIndex/siteId, no reward blob)
        EventAdvanceRequest = 0x6B, // client -> host: "advance your single-choice PROMPT" (client OK'd its prompt mirror; event already auto-completed at trigger so AnswerEventAction can't drive the host UI); reuses the EventDismiss codec (occId/eventId only); idempotent first-wins on the host
        ReportModalHide = 0x6C, // host -> all: close the mirrored BLOCKING report modal (ambush brief) on clients — the host resolved it [modalType:u8]

        // Chat
        ChatMessage = 0x50,

        // Transport-specific (STUN hole punch, etc.)
        TransportInternal = 0xF0

        // ─── RETIRED / RESERVED wire ids — permanent tombstones, do NOT reuse ─────────────────
        // These ids were live in earlier revisions and have been removed. Their receivers are
        // gone; a new sender on any of them would be a silent-desync bug. Kept here (not as enum
        // members) so the ranges stay reserved and greppable.
        //   0x21-0x24, 0x27  — legacy tactical approve/reject/result/broadcast/turn-state path
        //   0x25, 0x26       — EndTurnRequest/Accepted → now envelope TacIntentEndTurn 0x84 / TacTurn 0x85
        //   0x33, 0x34       — CampaignActionResult/CampaignStateUpdate (never sent, no handler)
        //   0x35, 0x36       — GeoStateDiff/GeoEntityOp (orphan; the diff codec they fronted is gone)
        //   0x63             — WalletSync → wallet rides 0x67 SyncEnvelope GeoWallet 0xA0 surface
        //   0x64             — StateSync → per-channel state rides 0x67 SyncEnvelope GeoState 0xA1 surface
        //   0x68             — ChoiceClaim → event-choice resolution rides AnswerEventAction (occId on the action wire)
    }
}
