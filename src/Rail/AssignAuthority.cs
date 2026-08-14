namespace Multiplayer.Network.Sync
{
    /// <summary>The two ASSIGNMENTS decisions as PURE functions over booleans — no game type, no TFTV type,
    /// no static read — so RailCheck L441 executes the REAL answers headless instead of a copy of them that
    /// can agree with itself while production disagrees. Same shape and same reason as
    /// <c>LoadBarrierAuthority</c>.</summary>
    internal static class AssignAuthority
    {
        /// <summary>May this ASSIGNMENTS gesture be applied? Only the host simulates
        /// (<c>AssignSync</c>'s whole premise), and only over a character it resolved on its OWN graph, that
        /// belongs to the Phoenix faction, with a role byte that is a defined enum value — the role arrives
        /// as raw wire input and is the one term an attacker controls outright.</summary>
        internal static bool AcceptAssignMove(bool isHost, bool sessionActive, bool characterResolved,
            bool isPhoenix, bool roleDefined) =>
            isHost && sessionActive && characterResolved && isPhoenix && roleDefined;

        /// <summary>Must this peer's TFTV automation be MUTED? The pure statement of what the one door
        /// (<c>IntentRail.ShouldRunNative</c>) means for this surface: a client, in a live session, outside a
        /// delta apply, simulates nothing — auto-assign, eviction, training clocks and the completed-deployment
        /// popup all belong to the host. Inside an apply the native code MUST run (law 8: an apply that fires
        /// native events may not be blocked by the guard that exists to stop it echoing).
        ///
        /// It is a SPECIFICATION, not a second door: the mute prefixes gate on
        /// <c>IntentRail.ShouldRunNative()</c> itself (§7 "no local copy of the host/client decision"), and
        /// L441 asserts the two agree in the one posture a headless harness can construct.</summary>
        internal static bool ClientMutesAutomation(bool sessionActive, bool isHost, bool inApplyScope) =>
            sessionActive && !isHost && !inApplyScope;

        /// <summary>The same decision WITHOUT law 8's apply-scope carve-out, for the three funnels that
        /// simulate rather than present: <c>TryAutoAssignUnassignedPersonnel</c>,
        /// <c>EnforceLivingCapacity</c> and <c>AttachCharacter</c> (whose tail calls the first).
        ///
        /// The carve-out exists so a delta apply can FIRE native events without being blocked by the guard
        /// that stops it echoing back as an intent. Here the native event IS the divergence: a mirrored
        /// character arriving on a client runs <c>AttachCharacter</c> → <c>TryAutoAssignUnassignedPersonnel</c>
        /// inside the apply scope, so the client assigns people itself, the mirror does not move, and the
        /// drift survives until the HOST next touches the table. Muting unconditionally on a client is the
        /// smaller of the two fixes — the alternative is re-asserting the whole mirror on a timer, i.e. a
        /// poll that pays for every peer's frames to correct a write that should never have happened.</summary>
        internal static bool ClientMutesSimulation(bool sessionActive, bool isHost) => sessionActive && !isHost;

        /// <summary>May the client CLOSE its apply gate? Only on a complete apply. A skipped id is one whose
        /// <c>GeoCharacter</c> delta has not landed yet, and that arrival does not change
        /// <c>AssignState</c> — so a fingerprint committed over skips is a row, or a whole training session,
        /// silently lost for the rest of the campaign.</summary>
        internal static bool CommitApplyFingerprint(int skippedRows, int skippedSessions) =>
            skippedRows == 0 && skippedSessions == 0;

        /// <summary>Is this mirror safe to apply over what the client already has? An empty root is
        /// indistinguishable from "the delta has not arrived" until a non-empty one has been seen once, and
        /// applying it wipes the table and then recomputes the economy from nothing. After that an empty
        /// mirror is an ordinary host state (the last worker was dismissed) and must apply.
        ///
        /// ponytail: "seen" is measured as "a non-empty mirror has arrived", not as "a batch carrying this
        /// root has arrived" — the latter needs a per-root delivery signal the applier does not publish, and
        /// the gap it would close is not reachable. A client only ever holds rows because an apply put them
        /// there (its own AttachCharacter is muted) or because it loaded the save the host transferred, and
        /// in both cases a host whose table is genuinely empty had an empty table when those rows arrived.
        /// Upgrade path if a delivery signal ever exists: pass it in place of this flag, nothing else moves.</summary>
        internal static bool MirrorIsUsable(bool everSeenNonEmpty, int mirrorRows, int liveRows) =>
            mirrorRows > 0 || liveRows == 0 || everSeenNonEmpty;
    }
}
