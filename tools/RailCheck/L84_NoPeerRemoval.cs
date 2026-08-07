using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Transport;

namespace RailCheck
{
    /// <summary>
    /// L84 — NOBODY IS KICKED, AND NOBODY WAITS FOR ANYBODY.
    ///
    /// THE MANDATE (developer, 2026-08-05, first-class alongside reactivity and universal-first): "Say 50
    /// players. Everyone joins, presses ready, it loads, everyone plays. What goes on under the hood must
    /// concern nobody. NOBODY may be kicked or disconnected. Maximally stable, reactive for everyone, no lag,
    /// no ping problems — some players WILL have bad internet or high ping. The game must not be a fight to
    /// be able to play."
    ///
    /// WHY THIS LAW EXISTS AND L91 DOES NOT COVER IT. L91 is the same principle one namespace over, and it
    /// says so in its own comment: it sweeps <c>Multiplayer.Network.Sync</c> and <c>Multiplayer.Tactical</c>
    /// and DELIBERATELY excludes <c>Multiplayer.Network</c>, "because peer bookkeeping is its whole job".
    /// That exclusion is exactly where all three quorums lived — the lobby's ready gate, the save-transfer
    /// LOADED barrier and its straggler kick — so the rule was enforced everywhere except the place that
    /// broke it. Bookkeeping is indeed the lobby's job; DECIDING who gets to play from that bookkeeping is
    /// not. This law draws the line there, on the shapes rather than on the feature.
    ///
    /// WHAT WAS ACTUALLY THERE, so the arms are not abstract:
    ///   • <c>_readyClients.Count >= _clients.Count</c> gated the start — one AFK or parity-blocked player
    ///     held fifty shut, and the bigger the roster the more certain that is.
    ///   • <c>BarrierReleased(hostLoaded, loadedClientCount, expectedClientCount)</c> made everyone sit
    ///     behind a loading screen for the slowest download, up to three minutes.
    ///   • ...and then <c>ConnectionRejected + DisconnectPeer</c> for whoever had not made it, which is the
    ///     mandate's forbidden sentence written out in code.
    ///   • Six paths in total could take a player out of the session (heartbeat silence, straggler timeout,
    ///     send-queue overflow, socket write stall, N Steam send failures, two JOIN refusals). All six are
    ///     now ONE rule — <c>SessionManager.PausePeer</c> — and a peer keeps its row, slot, permissions and
    ///     guid binding while it is away.
    ///
    /// WHAT "NOBODY WAITS" MEANS, SCOPED AGAIN (owner ruling, 2026-08-07 — supersedes the 2026-08-05 line
    /// below for the LOBBY only). The mandate governs progress AFTER the game has been entered: once play is
    /// running, nothing may gate one peer on another peer ACTING. It does NOT reach the lobby, where no play
    /// exists to be blocked — so the host MAY hold the start until every live peer has readied, and the greyed
    /// PLAY / NEW CAMPAIGN button is wanted behaviour rather than a violation. That reverses <c>afc111a</c>
    /// deliberately and on the record. Arm (c) is retargeted accordingly; its replacement is the guard that
    /// makes the lobby gate safe (LIVE peers only), which is the same concern as the rest of this law.
    ///
    /// The 2026-08-05 ruling, unchanged for everything else: the forbidden wait is an ADMISSION
    /// quorum: a gate that reads what OTHER peers have done before letting play begin — the
    /// LOADED count, which held a whole lobby hostage to one slow download, and which arm
    /// (d) keeps dead. A LEVEL TRANSITION is explicitly NOT that wait: "if it's loading from geoscape
    /// into tactical, or from tactical back, then without options EVERYONE must load, in order to start" —
    /// otherwise the first peer in plays a world the others have not reached (law L94's report). That barrier is
    /// legitimate because it can never strand anybody: it releases as soon as every LIVE peer is in, gives up on
    /// a peer showing no sign of life, and — the part this law owns — takes NOBODY out of the session when it
    /// does. Arms (a), (b) and (e) are what make the two rules compatible; none of them was weakened.
    ///
    /// THE ONE KICK THAT SURVIVES is a peer that LEFT: <c>SessionManager.HandleLeave</c>, on the ClientLeave
    /// packet. Arm (a) is that sentence made mechanical — the caller SET of <c>ITransport.DisconnectPeer</c>
    /// must be exactly that one method. It is a set equality on purpose, not a "does it still get called":
    /// the failure this law guards against is a NEW caller appearing, which any weaker check would miss.
    ///
    /// Falsify (each verified to go RED, then restored):
    ///   • add <c>Transport.DisconnectPeer(id)</c> anywhere outside HandleLeave → <c>kick-outside-a-leave</c>
    ///   • put <c>DropPeer</c> back in the send-queue overflow branch → <c>backpressure-removes-the-peer</c>
    ///   • count a PAUSED or parity-blocked row in <c>LobbyController.AllLivePeersReady</c> →
    ///     <c>lobby-counts-a-dead-peer</c>; make that fold return true for everything →
    ///     <c>POSITIVE CONTROL</c>
    ///
    /// ARM (c) WAS GREEN THROUGH A REAL BREAKAGE and has been REPAIRED rather than supplemented
    /// (2026-08-07). Its rows were built with no <c>PlayerGuid</c> — which is exactly what a roster row
    /// carries BEFORE its JOIN arrives — so the arm proved the fold's behaviour only for a shape no live
    /// peer has, and it read green while a never-joined phantom held a host's lobby shut. The rows now
    /// carry identities. The claim that a row with no identity is not a live peer is L181's, and the
    /// claim that such a row never gets a held seat in the first place is L180's.
    ///   • restore the client counts to <c>SaveTransferMath.BarrierReleased</c> → <c>loaded-quorum-back</c>
    ///   • swap <c>NotePeerLoss</c> back to <c>RemoveClient</c> in the disconnect funnel, or stop
    ///     <c>NotePeerLoss</c> pausing → <c>drop-removes-the-peer</c>
    /// </summary>
    internal static class L84_NoPeerRemoval
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>The ONE method allowed to sever a peer's transport link: the ClientLeave handler.</summary>
        private const string VoluntaryLeave = "SessionManager.HandleLeave";

        internal static IEnumerable<string> Check()
        {
            var mod = typeof(SessionManager).Assembly;
            var lobbyTypes = mod.GetTypes()
                                .Where(t => t.Namespace == "Multiplayer.Network")
                                .ToList();

            // ─── POSITIVE CONTROL — a sweep that finds nothing proves nothing ───
            if (lobbyTypes.Count == 0)
            {
                yield return "L84 lobby-namespace-gone: no types resolve in Multiplayer.Network, so every arm " +
                             "below passes vacuously. The namespace L91 excludes is the one this law covers — " +
                             "if it moved, re-point this law at it rather than letting it read green";
                yield break;
            }

            // ─── (a) THE ONLY KICK IS A LEAVE ───
            var kick = typeof(ITransport).GetMethod("DisconnectPeer", All);
            if (kick == null)
                yield return "L84 premise-changed: ITransport.DisconnectPeer no longer exists, so this law can " +
                             "no longer tell a kick from a leave";
            else
            {
                // The transports IMPLEMENT the mechanism; they are not deciding anything. Everything else is.
                var callers = mod.GetTypes()
                                 .Where(t => t.Namespace != "Multiplayer.Transport")
                                 .SelectMany(SafeMethods)
                                 .Where(m => CallsMethod(m, kick))
                                 .Select(m => m.DeclaringType.Name + "." + m.Name)
                                 .Distinct(StringComparer.Ordinal)
                                 .OrderBy(n => n, StringComparer.Ordinal)
                                 .ToList();

                foreach (var caller in callers.Where(c => c != VoluntaryLeave))
                    yield return "L84 kick-outside-a-leave: " + caller + " calls ITransport.DisconnectPeer. " +
                                 "The mandate allows exactly one — " + VoluntaryLeave + ", on the ClientLeave " +
                                 "packet, i.e. a player who actually left. Every other severance was collateral " +
                                 "for something the peer could not control (heartbeat silence on a bad link, a " +
                                 "download that took longer than someone's timeout, a JOIN that landed at an " +
                                 "inconvenient moment) and is now SessionManager.PausePeer, which keeps the row, " +
                                 "the slot, the permissions and the guid binding so the peer resumes its own seat";

                if (!callers.Contains(VoluntaryLeave, StringComparer.Ordinal))
                    yield return "L84 leave-no-longer-disconnects: nothing calls ITransport.DisconnectPeer any " +
                                 "more, including " + VoluntaryLeave + ". A peer that genuinely LEFT must still " +
                                 "be severed, or the host keeps writing to a dead id forever and this arm has " +
                                 "stopped being able to tell a kick from a leave at all";
            }

            // ─── (b) BACKPRESSURE DEGRADES THE PEER, IT NEVER REMOVES IT ───
            var direct = mod.GetType("Multiplayer.Transport.DirectTransport");
            var drop = direct?.GetMethod("DropPeer", All);
            var enqueue = direct?.GetMethod("EnqueueBuiltFrame", All);
            if (direct == null || drop == null || enqueue == null)
                yield return "L84 premise-changed: DirectTransport.DropPeer / EnqueueBuiltFrame no longer both " +
                             "exist, so this law can no longer prove a full send queue does not remove a player";
            else if (CallsMethod(enqueue, drop))
                yield return "L84 backpressure-removes-the-peer: DirectTransport's enqueue path calls DropPeer. " +
                             "A full queue means the peer is behind — high ping, a bad link, a stalled read — " +
                             "which is the exact condition the mandate says must not cost anybody the session. " +
                             "Discard the OLDEST queued frames instead: stale is what the rail is built to " +
                             "survive (idempotent Apply, seq'd delivery, resync on gap)";

            // ─── (c) A LOBBY GATE COUNTS ONLY LIVE PEERS — EXECUTED against the real fold ───
            //
            // SUBJECT NARROWED, STRENGTH UNCHANGED (owner ruling 2026-08-07). This arm used to assert
            // that the LOBBY start gate reads nobody's ready flag, which reversed into this repo as
            // afc111a. The mandate is now scoped: it governs progress AFTER the game has been entered.
            // Before the game starts there is no play to block, so the host may wait for the table to sit
            // down, and the greyed PLAY / NEW CAMPAIGN button is wanted behaviour. Everything the mandate
            // ever said about IN-GAME progress is untouched — arms (a), (b), (d) and (e) are byte for
            // byte what they were, L91/L145/L151 still forbid gating one peer on another peer ACTING, and
            // the load barrier still waits on a LOAD that ends by itself and never on a person.
            //
            // WHAT REPLACES IT IS THE GUARD THAT MAKES THE LOBBY GATE SAFE, and it is this law's business
            // for exactly the reason the rest of it is: a peer that is gone must cost nobody the session.
            // Drops PausePeer rather than RemoveClient (arm (e)), so a dead peer keeps its roster row
            // forever — count that row and the lobby hangs for everyone with no human able to clear it.
            // Same for a parity-blocked peer, whose ready the host itself refuses (SetClientReady).
            var fold = typeof(LobbyController).GetMethod("AllLivePeersReady", All);
            if (fold == null)
                yield return "L84 premise-changed: LobbyController.AllLivePeersReady is gone — this law can no " +
                             "longer prove the lobby gate skips the peers no human can ready for";

            // FIXTURES REPAIRED 2026-08-07 (L180/L181 incident). Every row here used to be built with no
            // PlayerGuid, i.e. Guid.Empty — the value a roster row carries BEFORE its JOIN arrives. So
            // this arm and its positive control were exercising the fold with rows that no live peer ever
            // looks like, and both read green straight through a real phantom sitting in a real lobby.
            // The live rows now carry an identity, which is what makes the arm mean what it says.
            var joined = Guid.NewGuid();

            var lobby = new LobbyController();
            lobby.BeginHost();

            // A dead peer must not hold the lobby: paused + not ready, and the gate still opens.
            lobby.UpdateLobby(connectedClientCount: 49, saveChosen: true,
                allLivePeersReady: LobbyController.AllLivePeersReady(new[]
                {
                    new PeerListEntry { IsHost = true },
                    new PeerListEntry { Ready = true,  PlayerGuid = joined },
                    new PeerListEntry { Ready = false, Paused = true, PlayerGuid = Guid.NewGuid() },
                    new PeerListEntry { Ready = false, ParityDiffs = "mod X missing", PlayerGuid = Guid.NewGuid() },
                }));
            if (!lobby.CanStart)
                yield return "L84 lobby-counts-a-dead-peer: a roster whose only un-ready rows are a PAUSED peer " +
                             "and a parity-blocked one still closes the start gate. Neither can ever be cleared " +
                             "by a human pressing a button — the paused peer's machine is gone, and the host " +
                             "itself refuses the parity-blocked peer's ready — so the lobby is shut forever and " +
                             "the readiness gate has become the infinite blocker it was allowed on condition of " +
                             "never being. Count LIVE peers only";

            // POSITIVE CONTROL: the fold must still be able to say no, or the arm above proves nothing.
            if (LobbyController.AllLivePeersReady(new[]
                {
                    new PeerListEntry { IsHost = true },
                    new PeerListEntry { Ready = false, PlayerGuid = joined },
                }))
                yield return "L84 POSITIVE CONTROL: AllLivePeersReady answers TRUE for a LIVE, un-paused, " +
                             "parity-clean peer that has not readied. The fold has stopped discriminating, so " +
                             "the arm above passes for a gate that reads nothing at all";

            // ─── (d) NO LOADED QUORUM — EXECUTED against the real predicate ───
            var released = typeof(SaveTransferMath).GetMethod("BarrierReleased", All);
            if (released == null)
                yield return "L84 premise-changed: SaveTransferMath.BarrierReleased is gone — this law can no " +
                             "longer prove the load barrier waits on nobody";
            else
            {
                var ps = released.GetParameters();
                if (ps.Length != 1 || ps[0].ParameterType != typeof(bool))
                    yield return "L84 loaded-quorum-back: SaveTransferMath.BarrierReleased takes " + ps.Length +
                                 " parameter(s) (" + string.Join(", ", ps.Select(p => Pretty(p.ParameterType))) +
                                 "). It may read the HOST's own readiness and nothing else. Counting how many " +
                                 "clients have acked against how many are expected is what made everybody sit " +
                                 "behind a loading screen for the slowest download — and then get kicked for it";
                else if (!SaveTransferMath.BarrierReleased(true))
                    yield return "L84 loaded-quorum-back: BarrierReleased(hostLoaded: true) is false, so the host " +
                                 "is still waiting on something other than itself";
            }

            // ─── (e) AN INVOLUNTARY LOSS PAUSES THE PEER, IT DOES NOT REMOVE IT ───
            // ARM FOLLOWED ONTO ITS NEW SHAPE 2026-08-07 (L180), NOT WEAKENED. The transport-disconnect
            // funnel now reaches PausePeer through SessionManager.NotePeerLoss, which classifies the loss
            // before anything is held: a row whose JOIN never arrived has no seat to hold (no identity,
            // name, slot or permissions) and is removed there instead. That decision could not live in
            // PausePeer, because PausePeer announces and L120 arm (f) forbids the notice path freeing a
            // seat. So the claim is unchanged — an involuntary loss PAUSES a peer that joined — and this
            // arm now pins the extra hop rather than being satisfied by any path that reaches PausePeer.
            var funnel = typeof(NetworkEngine).GetMethod("OnPeerDisconnected", All);
            var classify = typeof(SessionManager).GetMethod("NotePeerLoss", All);
            var pause = typeof(SessionManager).GetMethod("PausePeer", All);
            if (funnel == null || classify == null || pause == null)
                yield return "L84 premise-changed: NetworkEngine.OnPeerDisconnected / SessionManager.NotePeerLoss " +
                             "/ SessionManager.PausePeer no longer all exist. That funnel is where a dead socket, " +
                             "a stalled write and a failed send channel all arrive, and pausing THERE is what " +
                             "covers all three at once";
            else if (!CallsMethod(classify, pause))
                yield return "L84 drop-removes-the-peer: SessionManager.NotePeerLoss no longer pauses anybody, so " +
                             "the classifier every involuntary loss now routes through has stopped holding the " +
                             "seat it exists to hold. The unjoined-row removal beside it (L180) is the ONE " +
                             "exception this arm allows, and only because such a row has no seat in the first place";
            else if (!CallsMethod(funnel, classify))
                yield return "L84 drop-removes-the-peer: the transport-disconnect funnel does not call NotePeerLoss. " +
                             "None of the things that reach it means the player left — they mean the network did " +
                             "something. Removing the roster row throws away the slot, the permissions and the " +
                             "guid binding, so the peer comes back as a stranger who has to fight its way in";
        }

        // ── IL probe. Flat token scan, same deliberate shortcut L94 documents: the methods under test are
        //    short and the failure direction of an unaligned hit is a token ResolveMethod rejects. ──
        private static bool CallsMethod(MethodBase caller, MethodBase target)
        {
            byte[] il;
            try { il = caller?.GetMethodBody()?.GetILAsByteArray(); } catch { return false; }
            if (il == null) return false;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6F) continue;   // call / callvirt
                MethodBase c = null;
                try { c = caller.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)); } catch { }
                if (c != null && c.MetadataToken == target.MetadataToken && c.Module == target.Module) return true;
            }
            return false;
        }

        private static IEnumerable<MethodBase> SafeMethods(Type t)
        {
            try
            {
                return t.GetMethods(All | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                        .Concat(t.GetConstructors(All | BindingFlags.DeclaredOnly).Cast<MethodBase>());
            }
            catch { return Enumerable.Empty<MethodBase>(); }
        }

        private static string Pretty(Type t) =>
            t.IsGenericType
                ? t.Name.Substring(0, t.Name.IndexOf('`')) + "<" +
                  string.Join(", ", t.GetGenericArguments().Select(Pretty)) + ">"
                : t.Name;
    }
}
