using Multipleer.Sync.Tactical;
using Xunit;

/// <summary>
/// PURE wire-codec tests for the Inc Overwatch rail (tac.intent.overwatch + tac.overwatch.state). No engine
/// types — mirrors <see cref="TacticalEquipCodecTests"/> / <see cref="TacticalCombatCodecTests"/>. Covers
/// round-trip fidelity of the flattened CONE (Tip.xyz, Height, Radius, Forward.xyz) on the arm intent and the
/// armed state, the CLEAR (armed=false, no cone) state, and truncation rejection (including a truncated armed
/// frame, which must reject rather than partially accept).
/// </summary>
public class TacticalOverwatchCodecTests
{
    // ─── tac.intent.overwatch (client→host) ───────────────────────────
    [Fact]
    public void OverwatchIntent_RoundTrips_TypicalCone()
    {
        var bytes = TacticalLiveCodec.EncodeOverwatchIntent(
            actorNetId: 42, nonce: 7u,
            tipX: 1.5f, tipY: 2.5f, tipZ: -3.25f, height: 12.5f, radius: 4.0f, fwdX: 0f, fwdY: 0f, fwdZ: 1f);
        Assert.True(TacticalLiveCodec.TryDecodeOverwatchIntent(bytes, out var i));
        Assert.Equal(42, i.ActorNetId);
        Assert.Equal(7u, i.Nonce);
        Assert.Equal(1.5f, i.TipX);
        Assert.Equal(2.5f, i.TipY);
        Assert.Equal(-3.25f, i.TipZ);
        Assert.Equal(12.5f, i.Height);
        Assert.Equal(4.0f, i.Radius);
        Assert.Equal(0f, i.FwdX);
        Assert.Equal(0f, i.FwdY);
        Assert.Equal(1f, i.FwdZ);
    }

    [Fact]
    public void OverwatchIntent_RoundTrips_ZeroAndNegative()
    {
        var bytes = TacticalLiveCodec.EncodeOverwatchIntent(0, 0u, 0f, 0f, 0f, 0f, 0f, -1f, 0f, 0f);
        Assert.True(TacticalLiveCodec.TryDecodeOverwatchIntent(bytes, out var i));
        Assert.Equal(0, i.ActorNetId);
        Assert.Equal(0u, i.Nonce);
        Assert.Equal(-1f, i.FwdX);
    }

    [Fact]
    public void OverwatchIntent_RoundTrips_LargeValues()
    {
        var bytes = TacticalLiveCodec.EncodeOverwatchIntent(
            int.MaxValue, uint.MaxValue, 1234.5f, -9999.9f, 0.001f, 50f, 25f, 0.577f, 0.577f, 0.577f);
        Assert.True(TacticalLiveCodec.TryDecodeOverwatchIntent(bytes, out var i));
        Assert.Equal(int.MaxValue, i.ActorNetId);
        Assert.Equal(uint.MaxValue, i.Nonce);
        Assert.Equal(1234.5f, i.TipX);
        Assert.Equal(-9999.9f, i.TipY);
        Assert.Equal(50f, i.Height);
        Assert.Equal(25f, i.Radius);
    }

    [Fact]
    public void OverwatchIntent_Is40Bytes()
    {
        var bytes = TacticalLiveCodec.EncodeOverwatchIntent(1, 1u, 1, 2, 3, 4, 5, 6, 7, 8);
        Assert.Equal(40, bytes.Length);   // i32 actor + u32 nonce + 8 floats
    }

    [Fact]
    public void OverwatchIntent_RejectsTruncated()
    {
        Assert.False(TacticalLiveCodec.TryDecodeOverwatchIntent(null, out _));
        Assert.False(TacticalLiveCodec.TryDecodeOverwatchIntent(new byte[] { 1, 2, 3 }, out _));
        Assert.False(TacticalLiveCodec.TryDecodeOverwatchIntent(new byte[39], out _));   // one byte short of 40
    }

    // ─── tac.overwatch.state (host→all) — ARMED ───────────────────────
    [Fact]
    public void OverwatchState_RoundTrips_Armed()
    {
        var bytes = TacticalLiveCodec.EncodeOverwatchState(
            seq: 3u, actorNetId: 77, armed: true,
            tipX: 10f, tipY: 1f, tipZ: 20f, height: 15f, radius: 5f, fwdX: 1f, fwdY: 0f, fwdZ: 0f);
        Assert.True(TacticalLiveCodec.TryDecodeOverwatchState(bytes, out var s));
        Assert.Equal(3u, s.Seq);
        Assert.Equal(77, s.ActorNetId);
        Assert.True(s.Armed);
        Assert.Equal(10f, s.TipX);
        Assert.Equal(1f, s.TipY);
        Assert.Equal(20f, s.TipZ);
        Assert.Equal(15f, s.Height);
        Assert.Equal(5f, s.Radius);
        Assert.Equal(1f, s.FwdX);
    }

    [Fact]
    public void OverwatchState_Armed_Is41Bytes()
    {
        var bytes = TacticalLiveCodec.EncodeOverwatchState(1u, 1, true, 1, 2, 3, 4, 5, 6, 7, 8);
        Assert.Equal(41, bytes.Length);   // u32 seq + i32 actor + 1 bool + 8 floats
    }

    // ─── tac.overwatch.state — CLEAR (armed=false) ────────────────────
    [Fact]
    public void OverwatchState_RoundTrips_Clear()
    {
        var bytes = TacticalLiveCodec.EncodeOverwatchState(9u, 5, false, 0, 0, 0, 0, 0, 0, 0, 0);
        Assert.True(TacticalLiveCodec.TryDecodeOverwatchState(bytes, out var s));
        Assert.Equal(9u, s.Seq);
        Assert.Equal(5, s.ActorNetId);
        Assert.False(s.Armed);
    }

    [Fact]
    public void OverwatchClear_Helper_RoundTrips()
    {
        var bytes = TacticalLiveCodec.EncodeOverwatchClear(4u, 12);
        Assert.True(TacticalLiveCodec.TryDecodeOverwatchState(bytes, out var s));
        Assert.Equal(4u, s.Seq);
        Assert.Equal(12, s.ActorNetId);
        Assert.False(s.Armed);
    }

    [Fact]
    public void OverwatchState_Clear_Is9Bytes()
    {
        var bytes = TacticalLiveCodec.EncodeOverwatchClear(1u, 1);
        Assert.Equal(9, bytes.Length);   // u32 seq + i32 actor + 1 bool, no cone
    }

    [Fact]
    public void OverwatchState_Armed_DropsConeWhenCleared()
    {
        // Even if caller passes cone numbers, armed=false omits them on the wire (9-byte frame).
        var bytes = TacticalLiveCodec.EncodeOverwatchState(1u, 1, false, 99, 99, 99, 99, 99, 99, 99, 99);
        Assert.Equal(9, bytes.Length);
    }

    // ─── truncation rejection ─────────────────────────────────────────
    [Fact]
    public void OverwatchState_RejectsTruncatedPrefix()
    {
        Assert.False(TacticalLiveCodec.TryDecodeOverwatchState(null, out _));
        Assert.False(TacticalLiveCodec.TryDecodeOverwatchState(new byte[] { 0, 0, 0 }, out _));
        Assert.False(TacticalLiveCodec.TryDecodeOverwatchState(new byte[8], out _));   // one byte short of the 9-byte prefix
    }

    [Fact]
    public void OverwatchState_RejectsTruncatedArmedCone()
    {
        // A full armed frame is 41 bytes; an armed flag with a short cone tail must reject (no partial accept).
        var full = TacticalLiveCodec.EncodeOverwatchState(1u, 1, true, 1, 2, 3, 4, 5, 6, 7, 8);
        var truncated = new byte[40];                  // armed prefix present, cone tail one byte short
        System.Array.Copy(full, truncated, 40);
        Assert.False(TacticalLiveCodec.TryDecodeOverwatchState(truncated, out _));
    }
}
