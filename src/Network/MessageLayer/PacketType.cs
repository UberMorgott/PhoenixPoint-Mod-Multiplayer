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

        // Tactical Actions
        TacticalActionRequest = 0x20,
        TacticalActionApproved = 0x21,
        TacticalActionRejected = 0x22,
        TacticalActionResult = 0x23,
        TacticalActionBroadcast = 0x24,
        EndTurnRequest = 0x25,
        EndTurnAccepted = 0x26,
        TurnStateUpdate = 0x27,

        // Campaign Actions
        CampaignActionRequest = 0x30,
        CampaignActionApproved = 0x31,
        CampaignActionRejected = 0x32,
        CampaignActionResult = 0x33,
        CampaignStateUpdate = 0x34,

        // Management
        PermissionUpdate = 0x40,
        SoldierAssignment = 0x41,
        PlayerListUpdate = 0x42,
        SetSave = 0x43,

        // Chat
        ChatMessage = 0x50,

        // Transport-specific (STUN hole punch, etc.)
        TransportInternal = 0xF0
    }
}
