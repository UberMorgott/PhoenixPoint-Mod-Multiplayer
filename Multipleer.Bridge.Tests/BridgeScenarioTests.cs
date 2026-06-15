using System.Linq;
using Multipleer.Network.MessageLayer;
using Multipleer.Network.Sync;
using Multipleer.Validation;
using Xunit;
using Xunit.Abstractions;

namespace Multipleer.Bridge.Tests
{
    /// <summary>
    /// In-process host↔client action-sync scenarios over the in-memory bus, exercising the REAL
    /// SyncEngine relay. Each test dumps the global trace so the packet exchange is visible.
    /// </summary>
    public class BridgeScenarioTests
    {
        private readonly ITestOutputHelper _o;
        public BridgeScenarioTests(ITestOutputHelper o) => _o = o;

        private void DumpTrace(SimCluster c)
        {
            _o.WriteLine("─── TRACE ───");
            foreach (var line in c.Trace.Lines) _o.WriteLine(line);
        }

        // S1 — relay echo: client0 requests, host validates+sequences+broadcasts, all peers converge.
        [Fact]
        public void S1_RelayEcho_AllPeersConverge()
        {
            using (var c = new SimCluster(clientCount: 2))
            {
                c.Client(0).Engine.Sync.SendActionRequest(new CounterAction(delta: 5, tag: 1));
                c.Pump();
                DumpTrace(c);

                Assert.Equal(5, c.Host.Sink.Counter);
                Assert.Equal(5, c.Client(0).Sink.Counter);
                Assert.Equal(5, c.Client(1).Sink.Counter);

                // Host assigned the first sequence (1) — visible as a single relayed ActionApply per peer.
                Assert.Single(c.Client(0).Sink.AppliedTags);
                Assert.Single(c.Client(1).Sink.AppliedTags);
                Assert.Single(c.Host.Sink.AppliedTags);
                Assert.Equal(1, c.Host.Sink.LastTag);

                // Trace shows the client0→host ActionRequest, then host→all ActionApply fan-out.
                Assert.Contains(c.Trace.Lines, l => l.Contains("SEND peer2->peer1"));   // client0 → host request
                Assert.Contains(c.Trace.Lines, l => l.Contains("BCAST peer1->peer2"));  // host → client0 apply
                Assert.Contains(c.Trace.Lines, l => l.Contains("BCAST peer1->peer3"));  // host → client1 apply
            }
        }

        // S2 — last-writer-wins + stale-drop: two concurrent requests get host-sequenced; all peers
        // agree on the final writer; a replayed (stale) ActionApply is dropped.
        [Fact]
        public void S2_LastWriterWins_AndStaleApplyDropped()
        {
            using (var c = new SimCluster(clientCount: 2))
            {
                // Two clients request BEFORE any pump → both land in the host inbound queue together.
                c.Client(0).Engine.Sync.SendActionRequest(new CounterAction(delta: 3, tag: 1));
                c.Client(1).Engine.Sync.SendActionRequest(new CounterAction(delta: 7, tag: 2));
                c.Pump();
                DumpTrace(c);

                // Host applied both (3+7 = 10) and every peer converged to the same final value.
                Assert.Equal(10, c.Host.Sink.Counter);
                Assert.Equal(10, c.Client(0).Sink.Counter);
                Assert.Equal(10, c.Client(1).Sink.Counter);

                // All peers agree on the SAME final writer (host's last-sequenced tag). Host inbound is
                // FIFO (client0 then client1), so the last sequence (2) is tag 2 on every peer.
                int finalTag = c.Host.Sink.LastTag;
                Assert.Equal(finalTag, c.Client(0).Sink.LastTag);
                Assert.Equal(finalTag, c.Client(1).Sink.LastTag);
                Assert.Equal(2, finalTag);

                // Each peer applied exactly two actions (seq 1 and seq 2), none duplicated.
                Assert.Equal(2, c.Host.Sink.AppliedTags.Count);
                Assert.Equal(2, c.Client(0).Sink.AppliedTags.Count);
                Assert.Equal(2, c.Client(1).Sink.AppliedTags.Count);

                // ─── Replay a STALE/duplicate ActionApply at client0 and assert it is dropped. ───
                long before = c.Client(0).Sink.Counter;
                int appliesBefore = c.Client(0).Sink.AppliedTags.Count;

                // Re-encode seq 1 (already applied; lastApplied is now 2) with a fresh delta+tag. The
                // client's SequenceTracker must drop seq<=lastApplied, so the sink must NOT change.
                var stalePayload = EncodeCounterApply(seq: 1, delta: 99, tag: 999);
                var staleMsg = new NetworkMessage(PacketType.ActionApply, stalePayload);
                FeedToClientInbound(c, clientIndex: 0, msg: staleMsg);
                c.Pump();

                Assert.Equal(before, c.Client(0).Sink.Counter);                 // counter unchanged
                Assert.Equal(appliesBefore, c.Client(0).Sink.AppliedTags.Count); // no extra apply
                Assert.DoesNotContain(999, c.Client(0).Sink.AppliedTags);        // stale tag never applied
            }
        }

        // S3 — permission deny: a client whose category is revoked is rejected by the HOST-side gate;
        // host + other peers unchanged. A permitted client then succeeds.
        [Fact]
        public void S3_PermissionDeny_ThenPermittedSucceeds()
        {
            using (var c = new SimCluster(clientCount: 2))
            {
                // Revoke ALL permissions for client1 (no FullCommander, no ManageResearch).
                PermissionManager.SetPermissionsRaw(c.Client(1).PlayerGuid, 0);

                // client1's request must be rejected by the authoritative host gate in OnActionRequest.
                c.Client(1).Engine.Sync.SendActionRequest(new CounterAction(delta: 50, tag: 7));
                c.Pump();
                DumpTrace(c);

                Assert.Equal(0, c.Host.Sink.Counter);
                Assert.Equal(0, c.Client(0).Sink.Counter);
                Assert.Equal(0, c.Client(1).Sink.Counter);
                // Host sent an ActionReject targeted back to client1 (peer3), no broadcast apply.
                Assert.Contains(c.Trace.Lines, l => l.Contains("SEND peer1->peer3")); // reject → client1
                Assert.DoesNotContain(c.Trace.Lines, l => l.Contains("BCAST peer1->")); // nothing applied

                // Now a permitted client (client0 still has FullCommander) succeeds.
                c.Client(0).Engine.Sync.SendActionRequest(new CounterAction(delta: 4, tag: 8));
                c.Pump();

                Assert.Equal(4, c.Host.Sink.Counter);
                Assert.Equal(4, c.Client(0).Sink.Counter);
                Assert.Equal(4, c.Client(1).Sink.Counter);
                Assert.Equal(8, c.Host.Sink.LastTag);
            }
        }

        // ─── helpers ──────────────────────────────────────────────────────

        private static byte[] EncodeCounterApply(ulong seq, int delta, int tag)
        {
            byte[] payload;
            using (var ms = new System.IO.MemoryStream())
            using (var w = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8))
            {
                w.Write(delta);
                w.Write(tag);
                payload = ms.ToArray();
            }
            return SyncProtocol.EncodeActionApply(CounterAction.Id, seq, payload);
        }

        // Inject a raw packet straight into a client's transport inbound (as if the host had sent it),
        // so it is delivered + routed on the next Pump. Uses the real serialize/deserialize path.
        private static void FeedToClientInbound(SimCluster c, int clientIndex, NetworkMessage msg)
        {
            var client = c.Client(clientIndex);
            c.Bus.Enqueue(InMemoryBus.HostPeerId, client.PeerId, msg.Serialize(), "INJECT");
        }
    }
}
