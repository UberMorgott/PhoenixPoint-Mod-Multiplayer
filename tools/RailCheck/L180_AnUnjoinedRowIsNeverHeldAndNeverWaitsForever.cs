using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;

namespace RailCheck
{
    /// <summary>
    /// L180 — A ROSTER ROW WHOSE JOIN NEVER ARRIVED IS NEVER HELD, AND NEVER OUTLIVES ITS DEADLINE.
    ///
    /// THE REPORT (2026-08-07, friend joining over a VPN). His first join half-opened: the host saw him in
    /// the lobby, he hung and was thrown back to the main menu, and on the HOST he stayed — a phantom that
    /// was still there after he had quit the game and turned the VPN off. Both logs close it:
    ///   host 23:06:37.599 — a packet from his SteamId arrives, "the packet is not a ConnectionRequest …
    ///                       Delivering it, minting nothing" (94fb77b's gate, working)
    ///   host 23:07:08.540 — "P2P session … failed (P2PSessionError=4) — dropping peer"
    ///   host 23:07:08.541 — "Peer 1 (Unknown) PAUSED … Roster row kept — it resumes its seat when it
    ///                       comes back"                                              ← the phantom, born
    ///   host 23:07:08.557 — "PEER_LIST fan-out MISSES 1 roster peer(s) [1]"          ← the mod naming it
    /// "Unknown" is <c>ClientInfo.PlayerName</c>'s initialiser: nothing had ever set a name, because no
    /// JOIN had ever arrived.
    ///
    /// WHERE THE ROW CAME FROM, AND WHY 94fb77b DID NOT COVER IT. That commit stopped the host minting a
    /// peer off ANY stale Steam packet — "the host arm now mints only on ConnectionRequest" — and that arm
    /// held (the 23:06:37 line IS it refusing). But the host has a SECOND minting arm: the Steam P2P
    /// session-request callback (<c>SteamTransport.OnSessionRequest</c> → <c>OnPeerConnected</c> →
    /// <c>SessionManager.AddClient</c>), and it must stay open — the host has to accept the session to
    /// receive a ConnectionRequest at all. So a roster row is minted by the TRANSPORT connect, before any
    /// identity exists, and <c>SessionLifecycle.StaleRejoinPeers</c> already says so in its own comment.
    ///
    /// WHY IT WAS IMMORTAL. <c>PausePeer</c> is the N=50 mandate's involuntary-loss funnel and it is right
    /// for what it was written for: a peer that JOINED, whose row, slot, permissions and guid binding must
    /// survive a bad link. Applied to a row with none of those it preserves nothing and can never end:
    /// <c>ResumePeer</c> needs a heartbeat from a machine that has gone, and the heartbeat reaper only
    /// calls back into <c>PausePeer</c>, which returns immediately on an already-paused row. L84 was green
    /// throughout — correctly, it asserts that funnel EXISTS. Nothing asserted whom it is for.
    ///
    /// THE DEADLINE IS THE OTHER HALF, and it is not the heartbeat reaper. That reaper keys on SILENCE,
    /// and a half-open joiner is not silent: its packets keep arriving and <c>RefreshLiveness</c> keeps its
    /// row fresh off any inbound byte, while the one packet that matters never comes. The host carried
    /// that row for 31 s and only Steam's own P2P timeout ended it; a link that keeps the session
    /// nominally alive would have carried it forever.
    ///
    /// THE ARMS. (a) executes <see cref="SessionLifecycle.UnjoinedRowExpired"/> over its cube, including
    /// both directions of the boundary and the JOINED row that must never expire whatever its age. (b) and
    /// (c) are the two seams — a deadline nothing calls and a funnel that still holds an identity-less
    /// seat are each the whole bug back. (d) pins the ordering the deadline's value depends on: the host's
    /// window must outlast the joiner's own, or the host reaps handshakes that are still in flight.
    /// (e) is the vacuity guard: if the pre-JOIN row stops existing this law has no subject.
    ///
    /// (f) IS THE HOST'S OWN DIAGNOSIS, ACTED ON. The fan-out reachability check printed `PEER_LIST
    /// fan-out MISSES 1 roster peer(s) [1]` at 23:07:08.557 — the host naming this phantom out loud, in an
    /// ERROR it then ignored for the rest of the session. Each unreachable row now routes into the same
    /// classifier. <c>CanReach</c> is a membership test (<c>SteamTransport</c>:440 is
    /// <c>_connectedPeers.Contains</c>), not a probe, so it is false exactly when the transport holds no
    /// session for that row.
    ///
    /// WHAT THIS LAW DELIBERATELY DOES NOT CLAIM: that the host should CLOSE the stale process-global
    /// session on those bytes. That was proposed and rejected on the evidence. The wedge it would fix is
    /// already fixed one branch up — the edge-triggered <c>OnP2PSessionRequest</c> cannot fire twice for
    /// one SteamId, so 94fb77b's arm registers the peer off its own ConnectionRequest instead, and the
    /// host log shows that working on the very next attempt (23:07:30.711 "Registering it now"). Closing
    /// would race the ConnectionRequest riding in behind the stale bytes, which is the hazard
    /// <c>_deferredCloses</c>' grace window already exists for. And it would have changed nothing on
    /// 23:06:37: the host RECEIVED that packet, so the inbound path was open — what never arrived was a
    /// ConnectionRequest, and no local close makes a packet arrive. 94fb77b's minting gate is left to
    /// 94fb77b; an arm pinning it here was written, found VACUOUS by falsification (ConnectionRequest is
    /// 0x01, so the `ldc.i4.1` an IL constant scan looks for occurs all over that pump) and deleted rather
    /// than shipped green. A cheap check that cannot fail is worse than no check.
    ///
    /// Falsify (each verified to go RED, then restored): drop the <c>Guid.Empty</c> branch from
    /// <c>PausePeer</c> → <c>seat-held-for-a-peer-who-never-sat</c>; delete the reaper sweep →
    /// <c>handshake-has-no-deadline</c>; make <c>UnjoinedRowExpired</c> return true for a handshaked row →
    /// <c>deadline-reaps-a-real-peer</c>; return false always → <c>POSITIVE CONTROL</c>; set
    /// <c>JoinHandshakeTimeoutMs</c> below the joiner's stage deadline → <c>host-reaps-before-the-joiner-gives-up</c>.
    /// </summary>
    internal static class L180_AnUnjoinedRowIsNeverHeldAndNeverWaitsForever
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var mod = typeof(SessionManager).Assembly;

            // ─── (e) VACUITY GUARD — the pre-JOIN row must still be a thing that exists ───
            var identity = typeof(ClientInfo).GetProperty("PlayerGuid", All);
            var addClient = typeof(SessionManager).GetMethod("AddClient", All);
            var connected = typeof(NetworkEngine).GetMethod("OnPeerConnected", All);
            var expired = typeof(SessionLifecycle).GetMethod("UnjoinedRowExpired", All);
            if (identity == null || addClient == null || connected == null || expired == null)
            {
                yield return "L180 premise-changed: ClientInfo.PlayerGuid, SessionManager.AddClient, " +
                             "NetworkEngine.OnPeerConnected or SessionLifecycle.UnjoinedRowExpired no longer " +
                             "resolves. This law is about the window between the TRANSPORT connect and the JOIN, " +
                             "and PlayerGuid is the only thing that tells the two apart — re-point it before " +
                             "letting it read green.";
                yield break;
            }
            if (!Program.Callees(connected, mod).Any(c => c.MetadataToken == addClient.MetadataToken))
                yield return "L180 premise-changed: the transport-connect funnel no longer adds a roster row, so " +
                             "there may no longer BE a row without an identity. If the row is now minted from the " +
                             "JOIN itself that is a better fix than this law — delete it and say so — but if it " +
                             "merely moved, every arm below is now guarding the wrong door.";

            // ─── (a) THE DEADLINE'S DECISION, over its cube ───
            const long minute = 60_000;
            if (SessionLifecycle.UnjoinedRowExpired(false, minute, minute + 1_000, 30_000))
                yield return "L180 deadline-reaps-a-handshake-in-flight: a row whose JOIN has not arrived is " +
                             "expired 1 s after its connect. A JOIN crosses a real network and the joiner's own " +
                             "stage deadline is 20 s — reaping inside that window turns a slow link into a join " +
                             "that can never complete, which is the failure this whole area already has.";
            if (!SessionLifecycle.UnjoinedRowExpired(false, minute, minute + 30_000, 30_000))
                yield return "L180 handshake-waits-forever: a row that has held a seat for the full deadline with " +
                             "no JOIN is still not expired. That row has no identity, name, slot or permissions, " +
                             "nobody can ready it and nothing else will ever end it — the heartbeat reaper keys on " +
                             "SILENCE and a half-open joiner is not silent (RefreshLiveness refreshes its row off " +
                             "any inbound byte). This is the 2026-08-07 phantom, exactly.";
            if (!SessionLifecycle.UnjoinedRowExpired(false, minute, minute + 300_000, 30_000))
                yield return "L180 POSITIVE CONTROL: the decision refuses to expire an unjoined row five minutes " +
                             "past its deadline, so it can no longer say yes to anything and the arm above proves " +
                             "nothing about a rule that fires.";
            if (SessionLifecycle.UnjoinedRowExpired(true, minute, minute + 300_000, 30_000))
                yield return "L180 deadline-reaps-a-real-peer: a peer that DID complete its handshake is expired " +
                             "for being old. That peer owns its seat — its row, slot, permissions and guid binding " +
                             "survive an involuntary loss by the N=50 mandate (L84), and this deadline exists only " +
                             "for the rows that mandate was never written for.";

            // ─── (b) THE FUNNEL REFUSES TO HOLD A SEAT NOBODY EVER SAT IN ───
            //
            // The decision sits in NotePeerLoss and NOT in PausePeer, and that placement is load-bearing
            // rather than taste: PausePeer announces, and L120 arm (f) forbids the notice path freeing a
            // seat ("a notice that kicks is the kick this repo removed, wearing a label"). It caught the
            // first draft of this fix, which put the branch inside PausePeer. Announcing is not deciding.
            var loss = typeof(SessionManager).GetMethod("NotePeerLoss", All);
            var pause = typeof(SessionManager).GetMethod("PausePeer", All);
            var remove = typeof(SessionManager).GetMethod("RemoveClient", All);
            if (loss == null || pause == null || remove == null)
                yield return "L180 premise-changed: SessionManager.NotePeerLoss / PausePeer / RemoveClient no " +
                             "longer all exist. NotePeerLoss is the ONE funnel every involuntary loss reaches " +
                             "(transport drop, send failure, heartbeat reaper), which is why the unjoined-row rule " +
                             "belongs in it rather than at each of its callers.";
            else if (Program.Callees(pause, mod).Any(c => c.MetadataToken == remove.MetadataToken))
                yield return "L180 the-announcer-decides-again: PausePeer itself removes a row. That is L120 arm " +
                             "(f)'s violation and it is how this fix was first written — the unjoined-row branch " +
                             "belongs in NotePeerLoss, one hop ABOVE the method that announces.";
            else if (!Program.Callees(loss, mod).Any(c => c.MetadataToken == remove.MetadataToken) ||
                     !Program.Callees(loss, mod).Any(c => c.MetadataToken == pause.MetadataToken))
                yield return "L180 seat-held-for-a-peer-who-never-sat: NotePeerLoss no longer both removes the row " +
                             "whose JOIN never arrived and pauses the one that did — it must do exactly those two " +
                             "things, or it has stopped being a classifier. Pausing an unjoined row preserves " +
                             "nothing — no identity, no name, no slot, no " +
                             "permissions — and has no return edge, because ResumePeer needs a heartbeat from a " +
                             "machine that is gone and the reaper only calls back into PausePeer, which returns on " +
                             "an already-paused row. The row becomes immortal and 3161d33's readiness gate then " +
                             "counts it: that is the phantom that held a host's NEW CAMPAIGN button down after its " +
                             "owner had quit the game and switched his VPN off.";

            // ─── (c) THE DEADLINE IS ACTUALLY ARMED ───
            var update = typeof(SessionManager).GetMethod("Update", All);
            if (update == null || !Program.Callees(update, mod).Any(c => c.MetadataToken == expired.MetadataToken))
                yield return "L180 handshake-has-no-deadline: SessionManager.Update does not consult " +
                             "UnjoinedRowExpired, so arm (a) is proved about a decision the host never makes. Every " +
                             "wait in this mod has a deadline that fails loudly and by name; the join handshake is " +
                             "a wait, and the only thing that ended it on 2026-08-07 was Steam's own P2P timeout.";

            // ─── (d) THE JOINER GIVES UP FIRST ───
            var hostWindow = ReadIntConst(typeof(SessionManager), "JoinHandshakeTimeoutMs");
            var joinerWindow = ReadFloatConst(mod.GetType("Multiplayer.UI.MultiplayerUI"), "JoinStageTimeoutSec");
            if (hostWindow == null || joinerWindow == null)
                yield return "L180 premise-changed: SessionManager.JoinHandshakeTimeoutMs or " +
                             "MultiplayerUI.JoinStageTimeoutSec is no longer a readable constant, so the ordering " +
                             "the host's deadline depends on cannot be checked.";
            else if (hostWindow.Value <= joinerWindow.Value * 1000f)
                yield return "L180 host-reaps-before-the-joiner-gives-up: the host drops an unjoined row after " +
                             hostWindow.Value + " ms while the joiner is still waiting out its own " +
                             (int)(joinerWindow.Value * 1000f) + " ms stage deadline. The host must be the more " +
                             "patient of the two: a handshake still in flight is then reaped by the one side that " +
                             "cannot tell the joiner why, and the joiner retries into a host that just deleted it.";

            // ─── (f) THE HOST'S OWN DIAGNOSIS IS ACTED ON, NOT JUST PRINTED ───
            var flush = typeof(SessionManager).GetMethod("FlushPeerList", All);
            if (flush == null)
                yield return "L180 premise-changed: SessionManager.FlushPeerList is gone — the fan-out reachability " +
                             "check went with it, and that check is the host telling itself which roster rows it " +
                             "cannot even send the roster to.";
            else if (loss != null && !Program.Callees(flush, mod).Any(c => c.MetadataToken == loss.MetadataToken))
                yield return "L180 the-miss-is-only-logged: the PEER_LIST fan-out finds roster rows the transport " +
                             "cannot reach and does not route them into NotePeerLoss. That ERROR line is the host " +
                             "naming the phantom out loud — `PEER_LIST fan-out MISSES 1 roster peer(s) [1]`, " +
                             "printed 23:07:08.557 and ignored — while the row it names sat in the lobby for the " +
                             "rest of the session. A row the transport holds no session for is not a live peer, " +
                             "and CanReach is a membership test rather than a probe, so nothing about acting on " +
                             "it is a guess.";

        }

        private static int? ReadIntConst(System.Type t, string name)
        {
            var f = t?.GetField(name, All);
            if (f == null || !f.IsLiteral) return null;
            try { return (int)f.GetRawConstantValue(); } catch { return null; }
        }

        private static float? ReadFloatConst(System.Type t, string name)
        {
            var f = t?.GetField(name, All);
            if (f == null || !f.IsLiteral) return null;
            try { return (float)f.GetRawConstantValue(); } catch { return null; }
        }
    }
}
