using System;
using System.Collections.Generic;
using System.IO;
using Multipleer.Network.Sync;
using Multipleer.Network.Sync.State;
using Multipleer.Validation;
using Xunit;

public class SurfaceRouterTests
{
    // ─── fakes ────────────────────────────────────────────────────────────
    private sealed class FakeAction : ISyncedAction
    {
        public static int ApplyCount;
        public bool ValidateResult = true;
        public string Tag;
        public ushort ActionId => SyncedActionIds.StartResearch;
        public ActionCategory Category => ActionCategory.Research;
        public void Write(BinaryWriter w) => w.Write(Tag ?? "");
        public bool Validate(GeoRuntime rt, Guid actor) => ValidateResult;
        public void Apply(GeoRuntime rt) => ApplyCount++;
    }

    private sealed class FakeSink : ISyncSink
    {
        public bool Host;
        public Guid Actor = Guid.NewGuid();
        public readonly List<(ulong peer, byte surface)> Rejects = new List<(ulong, byte)>();
        public readonly List<(byte surface, ulong seq, byte[] payload)> Applies = new List<(byte, ulong, byte[])>();
        public readonly List<byte> Dirtied = new List<byte>();
        public int RefreshUiCount;
        public bool IsHost => Host;
        public GeoRuntime Runtime => null;   // tests never touch the game runtime (no Unity/Harmony loaded)
        public Guid ResolveActor(ulong peerId) => Actor;
        public void RejectTo(ulong peerId, byte surfaceId) => Rejects.Add((peerId, surfaceId));
        public void RebroadcastActionApply(byte surfaceId, ulong sequence, byte[] payload)
            => Applies.Add((surfaceId, sequence, payload));
        public void MarkSurfaceDirty(byte surfaceId) => Dirtied.Add(surfaceId);
        public void RefreshUi() => RefreshUiCount++;
    }

    private static SurfaceRegistry RegistryWithStartResearch(bool validate = true)
    {
        var reg = new SurfaceRegistry();
        reg.RegisterAction(SurfaceIds.StartResearch,
            r => new FakeAction { Tag = r.ReadString(), ValidateResult = validate },
            GeoUiRefresh.Screen.Research);
        return reg;
    }

    private static byte[] RequestEnvelope(string tag)
    {
        byte[] payload;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8)) { w.Write(tag); w.Flush(); payload = ms.ToArray(); }
        return SyncProtocol.EncodeEnvelope(SurfaceIds.StartResearch, SyncKind.ActionRequest, payload);
    }

    // ─── host: ActionRequest happy path ───────────────────────────────────
    [Fact]
    public void Host_ActionRequest_Authorized_Applies_AndRebroadcasts()
    {
        FakeAction.ApplyCount = 0;
        var sink = new FakeSink { Host = true };
        PermissionManager.SetPermission(sink.Actor, CampaignPermission.FullCommander, true);
        var router = new SurfaceRouter(RegistryWithStartResearch(), new SequenceTracker(), new RequestDedup());

        router.OnInbound(senderPeerId: 7, data: RequestEnvelope("plasma"), sink: sink);

        Assert.Equal(1, FakeAction.ApplyCount);           // host applied authoritatively
        Assert.Single(sink.Applies);                       // exactly one rebroadcast
        Assert.Equal(SurfaceIds.StartResearch, sink.Applies[0].surface);
        Assert.Equal(1UL, sink.Applies[0].seq);            // first host sequence
        Assert.Empty(sink.Rejects);
    }

    // ─── host: duplicate (peer,nonce-equivalent) request dropped ──────────
    [Fact]
    public void Host_DuplicateRequest_AppliesOnce()
    {
        FakeAction.ApplyCount = 0;
        var sink = new FakeSink { Host = true };
        PermissionManager.SetPermission(sink.Actor, CampaignPermission.FullCommander, true);
        var dedup = new RequestDedup();
        var router = new SurfaceRouter(RegistryWithStartResearch(), new SequenceTracker(), dedup);

        var env = RequestEnvelope("plasma");
        router.OnInbound(7, env, sink);   // first
        router.OnInbound(7, env, sink);   // reliable-transport duplicate → dropped

        Assert.Equal(1, FakeAction.ApplyCount);
        Assert.Single(sink.Applies);
    }

    // ─── host: unauthorized request rejected, no apply ────────────────────
    [Fact]
    public void Host_Unauthorized_Rejects_NoApply()
    {
        FakeAction.ApplyCount = 0;
        var sink = new FakeSink { Host = true, Actor = Guid.Empty }; // Guid.Empty = unmapped → fail closed
        var router = new SurfaceRouter(RegistryWithStartResearch(), new SequenceTracker(), new RequestDedup());

        router.OnInbound(7, RequestEnvelope("plasma"), sink);

        Assert.Equal(0, FakeAction.ApplyCount);
        Assert.Empty(sink.Applies);
        Assert.Single(sink.Rejects);
        Assert.Equal(SurfaceIds.StartResearch, sink.Rejects[0].surface);
    }

    // ─── host: failing Validate rejects, no apply ─────────────────────────
    [Fact]
    public void Host_ValidateFails_Rejects_NoApply()
    {
        FakeAction.ApplyCount = 0;
        var sink = new FakeSink { Host = true };
        PermissionManager.SetPermission(sink.Actor, CampaignPermission.FullCommander, true);
        var router = new SurfaceRouter(RegistryWithStartResearch(validate: false),
            new SequenceTracker(), new RequestDedup());

        router.OnInbound(7, RequestEnvelope("plasma"), sink);

        Assert.Equal(0, FakeAction.ApplyCount);
        Assert.Single(sink.Rejects);
    }

    // ─── client: ActionApply applies in order, drops stale ────────────────
    [Fact]
    public void Client_ActionApply_AppliesNewer_DropsStale()
    {
        FakeAction.ApplyCount = 0;
        var sink = new FakeSink { Host = false };
        var tracker = new SequenceTracker();
        var router = new SurfaceRouter(RegistryWithStartResearch(), tracker, new RequestDedup());

        // Apply envelopes carry the host sequence in the inner payload header (seq:u64 + action bytes).
        router.OnInbound(0, ApplyWithSeq("a", 1), sink);  // seq 1 → applies
        router.OnInbound(0, ApplyWithSeq("b", 1), sink);  // seq 1 again → stale, dropped
        router.OnInbound(0, ApplyWithSeq("c", 2), sink);  // seq 2 → applies

        Assert.Equal(2, FakeAction.ApplyCount);
        Assert.Empty(sink.Applies);   // client never rebroadcasts
        Assert.Empty(sink.Rejects);
    }

    // ─── unknown surface id is dropped, never throws ──────────────────────
    [Fact]
    public void UnknownSurface_Dropped()
    {
        FakeAction.ApplyCount = 0;
        var sink = new FakeSink { Host = true };
        var router = new SurfaceRouter(new SurfaceRegistry(), new SequenceTracker(), new RequestDedup());

        router.OnInbound(7, RequestEnvelope("plasma"), sink); // empty registry
        Assert.Equal(0, FakeAction.ApplyCount);
        Assert.Empty(sink.Applies);
        Assert.Empty(sink.Rejects);
    }

    // ─── wrong kind for the surface is dropped ────────────────────────────
    [Fact]
    public void WrongKindForSurface_Dropped()
    {
        FakeAction.ApplyCount = 0;
        var sink = new FakeSink { Host = true };
        PermissionManager.SetPermission(sink.Actor, CampaignPermission.FullCommander, true);
        var router = new SurfaceRouter(RegistryWithStartResearch(), new SequenceTracker(), new RequestDedup());

        // StateSnapshot kind on an action-only surface → registry.Accepts == false → drop.
        var env = SyncProtocol.EncodeEnvelope(SurfaceIds.StartResearch, SyncKind.StateSnapshot, new byte[] { 1 });
        router.OnInbound(7, env, sink);

        Assert.Equal(0, FakeAction.ApplyCount);
        Assert.Empty(sink.Applies);
    }

    // Apply envelope helper: inner payload = [seq:u64][actionBytes]. Mirrors SurfaceRouter's apply
    // wire (host writes seq, then the action's Write bytes).
    private static byte[] ApplyWithSeq(string tag, ulong seq)
    {
        byte[] payload;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8))
        {
            w.Write(seq);
            w.Write(tag);
            w.Flush();
            payload = ms.ToArray();
        }
        return SyncProtocol.EncodeEnvelope(SurfaceIds.StartResearch, SyncKind.ActionApply, payload);
    }
}
