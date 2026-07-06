namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// HOST: monotonic per-surface seq source for live outcomes. CLIENT: last-writer-wins guard. PURE
    /// (no engine types). One instance per live mission on each side; reset on mission exit / re-deploy.
    ///
    /// Seq is assigned PER SURFACE (tac.move and tac.turn each get an independent monotonic stream) so a
    /// turn outcome never suppresses a move outcome and vice-versa. The host emits over a reliable, per-peer
    /// ORDERED transport, so a strictly-greater check is sufficient last-writer-wins (a stale duplicate or
    /// re-send is dropped; nothing newer can be overtaken).
    /// </summary>
    public sealed class TacticalLiveSeq : Network.Sync.SurfaceSeq
    {
        /// <summary>HOST: capture-time per-mission seq hook, called from the deploy capture. Intentionally a
        /// NO-OP: the host seq streams must survive a mid-mission deploy capture (never rewind). Recreating/
        /// resetting the stream here (the old <c>LiveSeq = new TacticalLiveSeq()</c>) rewound <c>_hostNext[TacTurn]</c>
        /// to 0, so the next turn re-emitted seq=1 and the client's strict <c>seq &gt; last</c> guard dropped it ⇒
        /// "turn doesn't end". The stream is created exactly once per mission (constructor + OnMissionExit
        /// reset) and must survive the capture monotonically.</summary>
        public void BeginDeployCaptureMission()
        {
            // No-op by design: the host seq streams must survive a mid-mission deploy capture (never rewind).
        }
    }

    /// <summary>
    /// HOST-side intent de-duplicator: the reliable transport can double-send a client intent envelope; a
    /// double-applied MOVE would step the actor twice. Keyed by the intent's (peerId, surfaceId, nonce) —
    /// client nonces are client-LOCAL monotonic, so the peer discriminator keeps two clients' nonce streams
    /// apart; a bounded ring drops the oldest so memory stays flat over a long battle. PURE (no engine types).
    /// </summary>
    public sealed class TacticalIntentDedup : Network.Sync.IntentDedup
    {
        public TacticalIntentDedup(int capacity = 512) : base(capacity) { }
    }
}
