using System.IO;
using System.Text;
using Multiplayer.Sync.Tactical;
using Xunit;

/// <summary>
/// PURE wire tests for the TS2 generic ability-intent codec (surface 0x8E, <c>tac.intent.generic</c>). Covers:
///   (a) round-trip per target-KIND (none/self, actor, pos, equipment-slot, object) — every field survives
///       byte-identically, header (actorNetId/guid) + trailing nonce included,
///   (b) truncation / garbage / a corrupt payload length → clean <c>false</c> (no partial accept),
///   (c) an UNKNOWN target-kind decodes cleanly (kind byte + nonce preserved, no target fields) so the host can
///       degrade-to-notify — and its length-prefixed payload is SKIPPED (backward-tolerant recLen discipline),
///   (d) forward-tolerance: a KNOWN kind whose payload carries EXTRA tail bytes still decodes its known prefix.
/// The engine glue (TacticalCombatSync.ClientInterceptGenericAbility / HostOnGenericIntent + the
/// GenericAbilityRelayPatches Harmony patch) binds game types and is in-game verified.
/// </summary>
public class TacticalGenericIntentCodecTests
{
    // ─── (a) round-trip per kind ───────────────────────────────────────
    [Fact]
    public void None_RoundTrips()
    {
        var bytes = TacticalGenericIntentCodec.Encode(
            TacticalGenericIntentCodec.None(actorNetId: 7, guid: "heal-guid", nonce: 42u));
        Assert.True(TacticalGenericIntentCodec.TryDecode(bytes, out var i));
        Assert.Equal(7, i.ActorNetId);
        Assert.Equal("heal-guid", i.AbilityDefGuid);
        Assert.Equal(TacticalGenericIntentCodec.KindNone, i.TargetKind);
        Assert.Equal(TacticalGenericIntentCodec.TargetNetIdNone, i.TargetNetId);
        Assert.Equal(TacticalGenericIntentCodec.SlotIndexNone, i.SlotIndex);
        Assert.Equal(42u, i.Nonce);
    }

    [Fact]
    public void Actor_RoundTrips()
    {
        var bytes = TacticalGenericIntentCodec.Encode(
            TacticalGenericIntentCodec.Actor(actorNetId: 3, guid: "g", targetNetId: 1_000_005, nonce: 9u));
        Assert.True(TacticalGenericIntentCodec.TryDecode(bytes, out var i));
        Assert.Equal(TacticalGenericIntentCodec.KindActor, i.TargetKind);
        Assert.Equal(1_000_005, i.TargetNetId);
        Assert.Equal(9u, i.Nonce);
    }

    [Fact]
    public void Pos_RoundTrips()
    {
        var bytes = TacticalGenericIntentCodec.Encode(
            TacticalGenericIntentCodec.Pos(actorNetId: 3, guid: "g", tx: 1.5f, ty: -2.25f, tz: 3.75f, nonce: 11u));
        Assert.True(TacticalGenericIntentCodec.TryDecode(bytes, out var i));
        Assert.Equal(TacticalGenericIntentCodec.KindPos, i.TargetKind);
        Assert.Equal(1.5f, i.TX);
        Assert.Equal(-2.25f, i.TY);
        Assert.Equal(3.75f, i.TZ);
        Assert.Equal(TacticalGenericIntentCodec.TargetNetIdNone, i.TargetNetId);   // pos carries no actor
        Assert.Equal(11u, i.Nonce);
    }

    [Fact]
    public void Slot_RoundTrips()
    {
        var bytes = TacticalGenericIntentCodec.Encode(
            TacticalGenericIntentCodec.Slot(actorNetId: 3, guid: "reload", slotIndex: 2, nonce: 5u));
        Assert.True(TacticalGenericIntentCodec.TryDecode(bytes, out var i));
        Assert.Equal(TacticalGenericIntentCodec.KindSlot, i.TargetKind);
        Assert.Equal(2, i.SlotIndex);
        Assert.Equal(5u, i.Nonce);
    }

    [Fact]
    public void Object_RoundTrips()
    {
        var bytes = TacticalGenericIntentCodec.Encode(
            TacticalGenericIntentCodec.Object(actorNetId: 3, guid: "interact", objectNetId: 1_000_099, nonce: 8u));
        Assert.True(TacticalGenericIntentCodec.TryDecode(bytes, out var i));
        Assert.Equal(TacticalGenericIntentCodec.KindObject, i.TargetKind);
        Assert.Equal(1_000_099, i.TargetNetId);
        Assert.Equal(8u, i.Nonce);
    }

    // ─── (b) truncation / garbage ──────────────────────────────────────
    [Fact]
    public void RejectsNullAndTooShort()
    {
        Assert.False(TacticalGenericIntentCodec.TryDecode(null, out _));
        Assert.False(TacticalGenericIntentCodec.TryDecode(new byte[3], out _));   // shorter than the fixed header
    }

    [Fact]
    public void RejectsChoppedMidFrame()
    {
        var bytes = TacticalGenericIntentCodec.Encode(
            TacticalGenericIntentCodec.Pos(1, "g", 1f, 2f, 3f, 1u));
        var chopped = new byte[bytes.Length - 3];   // drop the trailing nonce bytes
        System.Array.Copy(bytes, chopped, chopped.Length);
        Assert.False(TacticalGenericIntentCodec.TryDecode(chopped, out _));
    }

    [Fact]
    public void RejectsPayloadShorterThanKnownKind()
    {
        // Hand-craft an ACTOR-kind frame whose payloadLen claims 0 bytes (a known kind needs 4) → clean drop.
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write(3);                 // actorNetId
            w.Write("g");               // guid
            w.Write(TacticalGenericIntentCodec.KindActor);
            w.Write((ushort)0);         // payloadLen = 0 — too short for an actor's i32
            w.Write(1u);                // nonce
            Assert.False(TacticalGenericIntentCodec.TryDecode(ms.ToArray(), out _));
        }
    }

    [Fact]
    public void RejectsCorruptPayloadLength()
    {
        // payloadLen far exceeds the remaining buffer → guarded reject (no wild allocation / partial read).
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write(3);
            w.Write("g");
            w.Write(TacticalGenericIntentCodec.KindActor);
            w.Write((ushort)60000);     // claims 60000 payload bytes that are not there
            Assert.False(TacticalGenericIntentCodec.TryDecode(ms.ToArray(), out _));
        }
    }

    // ─── (c) unknown kind → skip payload, preserve nonce (degrade-to-notify) ─────
    [Fact]
    public void UnknownKind_DecodesCleanly_SkipsPayload_KeepsNonce()
    {
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write(4);                 // actorNetId
            w.Write("future-ability");  // guid
            w.Write((byte)200);         // a target-kind THIS build does not know
            var futurePayload = new byte[] { 1, 2, 3, 4, 5, 6 };
            w.Write((ushort)futurePayload.Length);
            w.Write(futurePayload);     // opaque payload the decoder must SKIP
            w.Write(77u);               // nonce AFTER the skipped payload
            Assert.True(TacticalGenericIntentCodec.TryDecode(ms.ToArray(), out var i));
            Assert.Equal(4, i.ActorNetId);
            Assert.Equal("future-ability", i.AbilityDefGuid);
            Assert.Equal((byte)200, i.TargetKind);   // kind preserved so the host degrades-to-notify on THIS kind
            Assert.Equal(77u, i.Nonce);              // nonce read correctly PAST the unknown payload
        }
    }

    // ─── (d) forward-tolerance: extra tail bytes on a KNOWN kind ───────
    [Fact]
    public void KnownKind_WithExtraTailBytes_DecodesKnownPrefix()
    {
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write(3);
            w.Write("g");
            w.Write(TacticalGenericIntentCodec.KindActor);
            // A future build extended the ACTOR payload: i32 targetNetId + 2 extra bytes. THIS build reads the
            // known i32 and ignores the tail.
            w.Write((ushort)6);
            w.Write(1_000_007);         // the known targetNetId
            w.Write((byte)9); w.Write((byte)9);   // extra tail
            w.Write(31u);               // nonce
            Assert.True(TacticalGenericIntentCodec.TryDecode(ms.ToArray(), out var i));
            Assert.Equal(1_000_007, i.TargetNetId);
            Assert.Equal(31u, i.Nonce);
        }
    }

    [Fact]
    public void KindConstants_AreDistinct()
    {
        var kinds = new[]
        {
            TacticalGenericIntentCodec.KindNone, TacticalGenericIntentCodec.KindActor,
            TacticalGenericIntentCodec.KindPos, TacticalGenericIntentCodec.KindSlot,
            TacticalGenericIntentCodec.KindObject,
        };
        Assert.Equal(kinds.Length, new System.Collections.Generic.HashSet<byte>(kinds).Count);
        Assert.NotEqual(TacticalGenericIntentCodec.KindUnknown, TacticalGenericIntentCodec.KindNone);
    }
}
