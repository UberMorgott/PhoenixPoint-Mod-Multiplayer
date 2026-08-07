using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;

namespace RailCheck
{
    /// <summary>
    /// L181 — BOTH HALVES OF THE LOBBY START GATE COUNT THE SAME PEERS, AND THE REFUSAL NAMES THEM.
    ///
    /// THE GATE IS WANTED — the owner's 2026-08-07 ruling put readiness back in the lobby (3161d33),
    /// reversing afc111a on the record, and L84 arm (c) was retargeted to it. This law is not about
    /// whether the gate exists. It is about the gate having ONE answer to "who is at this table".
    ///
    /// IT HAD TWO. The gate is two clauses over the same roster (<c>LobbyController.PeersReady</c>):
    /// <c>_connectedClientCount &gt;= 1</c>, fed by <c>MultiplayerUI.RefreshGateFacts</c>, and
    /// <c>AllLivePeersReady</c>. They counted different sets. The fold skipped a PAUSED row as
    /// unreadyable; the count took every non-host row. So the phantom of L180 — a row minted by the
    /// transport connect whose JOIN never arrived, and which was then PAUSED forever — satisfied
    /// "the host is not alone" while being excluded from "everyone has readied", and the host could have
    /// started a co-op session with a player who was not there. Turn the phantom's Paused flag off (it is
    /// only set when the socket dies; a half-open joiner keeps its socket) and the same disagreement runs
    /// the other way: counted as present, unable to ever press READY, gate shut forever.
    ///
    /// SO "LIVE" IS ONE PREDICATE, <see cref="LobbyController.IsLivePeer"/>, and both clauses read it. A
    /// row is live iff a human is behind it now: not the host's own, not paused, and its JOIN actually
    /// arrived — <c>PlayerGuid</c> is the identity the JOIN carries and nothing else sets it, so
    /// <c>Guid.Empty</c> is precisely the pre-handshake row (<c>SessionLifecycle.StaleRejoinPeers</c>
    /// names the same shape).
    ///
    /// AND THE REFUSAL NAMES THE BLOCKER. "not every live peer has readied" is unactionable exactly when
    /// it matters, and against a phantom it named a player who was not in the room. <see
    /// cref="LobbyController.PeersBlockedBy"/> returns "" when the readiness half is open and otherwise
    /// the peers it is waiting on, by name.
    ///
    /// THE ARMS. (a) executes IsLivePeer over its four shapes, its own positive control included. (b) and
    /// (c) are the pair that has to hold TOGETHER, and each alone is a bug: a phantom must not BLOCK the
    /// fold, and it must not OPEN the gate by standing in for the player the host may not start without.
    /// (d) is the name in the message. (e) is the seam — the count half must read the same predicate, or
    /// the two clauses drift apart again, which is the defect itself.
    ///
    /// Falsify (each verified to go RED, then restored): drop the <c>PlayerGuid</c> test from IsLivePeer →
    /// <c>unjoined-row-is-counted-live</c> and <c>phantom-blocks-the-gate</c>; make IsLivePeer true for
    /// everything → <c>host-row-counted</c>; make PeersBlockedBy return "" always →
    /// <c>refusal-names-nobody</c> and <c>phantom-opens-the-gate</c>; count raw non-host rows in
    /// RefreshGateFacts again → <c>gate-halves-count-different-peers</c>.
    /// </summary>
    internal static class L181_TheLobbyGateCountsOnlyPeersAHumanCouldReady
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var mod = typeof(LobbyController).Assembly;
            var isLive = typeof(LobbyController).GetMethod("IsLivePeer", All);
            var blockedBy = typeof(LobbyController).GetMethod("PeersBlockedBy", All);
            var identity = typeof(PeerListEntry).GetField("PlayerGuid", All)
                           ?? (MemberInfo)typeof(PeerListEntry).GetProperty("PlayerGuid", All);
            if (isLive == null || blockedBy == null || identity == null)
            {
                yield return "L181 premise-changed: LobbyController.IsLivePeer / PeersBlockedBy or " +
                             "PeerListEntry.PlayerGuid no longer resolves. The whole law is that ONE predicate " +
                             "answers who is at the table and that the identity the JOIN carries is what tells a " +
                             "real peer from a row the transport minted — re-point it before it reads green.";
                yield break;
            }

            var joined = Guid.NewGuid();
            var hostRow = new PeerListEntry { IsHost = true, PlayerGuid = Guid.NewGuid(), Ready = true };

            // ─── (a) ONE DEFINITION OF LIVE, over its four shapes ───
            if (LobbyController.IsLivePeer(hostRow))
                yield return "L181 host-row-counted: the host's own row reads as a live PEER. It would then " +
                             "satisfy the host-is-not-alone clause on its own, which is Bug B — a host starting a " +
                             "co-op session with nobody in it — reintroduced through the counting rule.";
            if (LobbyController.IsLivePeer(new PeerListEntry { PlayerGuid = joined, Paused = true }))
                yield return "L181 paused-row-counted-live: a PAUSED peer reads as live. Every involuntary loss " +
                             "funnels there (drops use PausePeer, never RemoveClient), so that row is how a gone " +
                             "peer looks — counting it is the infinite blocker the lobby gate was allowed on " +
                             "condition of never becoming.";
            if (LobbyController.IsLivePeer(new PeerListEntry { PlayerGuid = Guid.Empty }))
                yield return "L181 unjoined-row-is-counted-live: a row carrying no identity reads as a live peer. " +
                             "That is the row the TRANSPORT mints before any JOIN arrives — no name, no slot, no " +
                             "permissions, and no human who could ever press READY for it. On 2026-08-07 one such " +
                             "row sat in a host's lobby after its owner had quit the game and turned his VPN off.";
            if (!LobbyController.IsLivePeer(new PeerListEntry { PlayerGuid = joined }))
                yield return "L181 POSITIVE CONTROL: a joined, un-paused peer does NOT read as live, so the " +
                             "predicate has stopped saying yes to anybody and every arm above passes for a gate " +
                             "that counts nothing at all.";

            // ─── (b)(c) THE PHANTOM NEITHER BLOCKS THE GATE NOR OPENS IT ───
            var phantomOnly = new[] { hostRow, new PeerListEntry { PlayerGuid = Guid.Empty, Ready = false } };
            if (!LobbyController.AllLivePeersReady(phantomOnly))
                yield return "L181 phantom-blocks-the-gate: a roster whose only non-host row never finished " +
                             "joining still closes the readiness fold. Nobody can ready that row and nothing on " +
                             "the host can clear it, so PLAY and NEW CAMPAIGN are dead for as long as it is there.";
            if (LobbyController.PeersBlockedBy(phantomOnly).Length == 0)
                yield return "L181 phantom-opens-the-gate: the same roster reports the readiness half as OPEN. " +
                             "Skipping an unreadyable row must not promote it to a player who is present — this is " +
                             "the other direction of the same disagreement, and it lets the host start a co-op " +
                             "session whose only other participant never completed its handshake.";

            // ─── (d) THE REFUSAL NAMES THE PEER ───
            var waitingOnBob = new[] { hostRow, new PeerListEntry { PlayerGuid = joined, Nickname = "Bob", Ready = false } };
            var reason = LobbyController.PeersBlockedBy(waitingOnBob);
            if (reason.IndexOf("Bob", StringComparison.Ordinal) < 0)
                yield return "L181 refusal-names-nobody: the blocked message for a roster waiting on exactly one " +
                             "named peer does not contain that name (got: \"" + reason + "\"). The host is then " +
                             "told only that somebody has not readied, which is unactionable precisely when it " +
                             "matters and named nobody at all when the blocker was a phantom.";
            if (LobbyController.PeersBlockedBy(new[]
                { hostRow, new PeerListEntry { PlayerGuid = joined, Nickname = "Bob", Ready = true } }).Length != 0)
                yield return "L181 POSITIVE CONTROL: the reason is non-empty for a table where the one live peer " +
                             "HAS readied, so it can never report the gate open and arm (d) proves nothing.";

            // ─── (e) THE COUNT HALF READS THE SAME PREDICATE ───
            var facts = mod.GetType("Multiplayer.UI.MultiplayerUI")?.GetMethod("RefreshGateFacts", All);
            if (facts == null)
                yield return "L181 premise-changed: MultiplayerUI.RefreshGateFacts is gone. It is the single " +
                             "projection point that feeds BOTH halves of the gate; if the count is now derived " +
                             "somewhere else, that somewhere is unguarded.";
            else if (!Program.Callees(facts, mod).Any(c => c.MetadataToken == isLive.MetadataToken))
                yield return "L181 gate-halves-count-different-peers: the clientCount half of the gate no longer " +
                             "reads IsLivePeer. It then counts rows the readiness fold skips, which is the defect " +
                             "verbatim: a phantom excluded from \"everyone has readied\" while still standing in " +
                             "for the player the host is not allowed to start without.";
        }
    }
}
