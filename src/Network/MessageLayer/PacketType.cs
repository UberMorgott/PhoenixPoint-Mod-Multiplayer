namespace Multipleer.Network.MessageLayer
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
        EndTurnRequest = 0x25,
        EndTurnAccepted = 0x26,

        // Campaign Actions
        CampaignActionRequest = 0x30,
        CampaignActionApproved = 0x31,
        CampaignActionRejected = 0x32,
        // 0x33 (CampaignActionResult) + 0x34 (CampaignStateUpdate) removed: never sent, no handler. Do NOT reuse the ids.
        GeoStateDiff = 0x35,
        GeoEntityOp = 0x36,
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

        // ActionSync 0x60-0x6F
        ActionRequest = 0x60,   // client -> host
        ActionApply   = 0x61,   // host -> all
        ActionReject  = 0x62,   // host -> originator
        WalletSync    = 0x63,   // host -> all
        StateSync     = 0x64,   // host -> all: per-channel versioned state echo [channelId][version][payload]
        EventRaised   = 0x65,   // host -> all: show a geoscape event dialog on clients [eventId][siteId]
        EventDismiss  = 0x66,   // host -> all: close the open geoscape event dialog on clients [eventId]
        SyncEnvelope  = 0x67,   // any direction: unified surface envelope [surfaceId:u8][kind:u8][len:u16][payload:N]
        // 0x68 retired (was ChoiceClaim): geoscape event-choice resolution now rides AnswerEventAction over the
        // research-style ActionRequest/ActionApply relay (occId on the action wire). Do NOT reuse this id.
        ReportModalShow = 0x69, // host -> all: show a geoscape REPORT modal on clients (Phase-A report-window mirror)
        EventAdvanceResult = 0x6A, // host -> all: single-choice PROMPT->RESULT advance (no native CompleteEvent fires); reuses the EventDismiss codec (occId/eventId/choiceIndex/siteId, no reward blob)

        // Chat
        ChatMessage = 0x50,

        // Transport-specific (STUN hole punch, etc.)
        TransportInternal = 0xF0
    }
}
