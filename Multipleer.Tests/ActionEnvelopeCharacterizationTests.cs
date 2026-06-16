using System;
using System.Collections.Generic;
using System.IO;
using Multipleer.Network.Sync;
using Multipleer.Validation;
using Xunit;

public class ActionEnvelopeCharacterizationTests
{
    private sealed class CountingAction : ISyncedAction
    {
        public static int Applies;
        public string Id;
        public ushort ActionId => SyncedActionIds.StartResearch;
        public ActionCategory Category => ActionCategory.Research;
        public void Write(BinaryWriter w) => w.Write(Id ?? "");
        public bool Validate(GeoRuntime rt, Guid actor) => true;
        public void Apply(GeoRuntime rt) => Applies++;
    }

    private sealed class LoopSink : ISyncSink
    {
        public bool Host;
        public Guid Actor = Guid.NewGuid();
        public readonly List<byte[]> Broadcasts = new List<byte[]>();
        public bool IsHost => Host;
        public GeoRuntime Runtime => null;
        public Guid ResolveActor(ulong peerId) => Actor;
        public void RejectTo(ulong peerId, byte surfaceId) { }
        public void RebroadcastActionApply(byte surfaceId, ulong sequence, byte[] payload)
            => Broadcasts.Add(SyncProtocol.EncodeEnvelope(surfaceId, SyncKind.ActionApply,
                SurfaceRouter.EncodeApplyPayload(sequence, payload)));
        public void MarkSurfaceDirty(byte surfaceId) { }
        public void RefreshUi() { }
    }

    private static SurfaceRegistry Registry()
    {
        var reg = new SurfaceRegistry();
        reg.RegisterAction(SurfaceIds.StartResearch, r => new CountingAction { Id = r.ReadString() }, null);
        return reg;
    }

    private static byte[] RequestEnvelope(string id)
    {
        byte[] payload;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8)) { w.Write(id); w.Flush(); payload = ms.ToArray(); }
        return SyncProtocol.EncodeEnvelope(SurfaceIds.StartResearch, SyncKind.ActionRequest, payload);
    }

    [Fact]
    public void HostRequest_Rebroadcast_ClientApplies_EndToEnd()
    {
        CountingAction.Applies = 0;
        var reg = Registry();

        // HOST: request → apply once → produce an apply envelope.
        var host = new LoopSink { Host = true };
        PermissionManager.SetPermission(host.Actor, CampaignPermission.FullCommander, true);
        var hostRouter = new SurfaceRouter(reg, new SequenceTracker(), new RequestDedup());
        hostRouter.OnInbound(7, RequestEnvelope("plasma"), host);
        Assert.Equal(1, CountingAction.Applies);
        Assert.Single(host.Broadcasts);

        // CLIENT: feed the host's apply envelope → applies once more (total 2), no rebroadcast.
        var client = new LoopSink { Host = false };
        var clientRouter = new SurfaceRouter(reg, new SequenceTracker(), new RequestDedup());
        clientRouter.OnInbound(0, host.Broadcasts[0], client);
        Assert.Equal(2, CountingAction.Applies);
        Assert.Empty(client.Broadcasts);
    }
}
