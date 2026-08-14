using System;
using System.Collections.Generic;
using System.IO;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L470 — THE MIST BLOB FITS THE STRING LEAF IT RIDES ON.
    ///
    /// THE FAILURE (2026-08-14 session). Mist coverage ships as a plain <c>string</c> member of a railed
    /// type — <see cref="MistState"/>.<c>MistData</c>, the game's own deflate+base64 of the CPU mist buffer.
    /// The value rail read every string leaf through <c>WireString.Prose</c> = 64 KB, and a real campaign's
    /// mist passes 64 KB and keeps growing. So the leaf stopped APPLYING on the client the moment it did:
    /// <c>GenericApplier: apply failed M#mist.MistData: string: implausible length 67220</c>, 35× in one
    /// session, the last at 119968 — and because a value that never applies never converges, the host's CRC
    /// backstop burned its one heal on a subtree no re-emit could fix
    /// (<c>root 'M#mist' STILL diverged on peer 2 after a forced re-emit</c>). The client simply never saw
    /// the mist, permanently, with the campaign otherwise healthy.
    ///
    /// THE RULE. The read bound on the rail's string leaf must CLEAR what the rail's own producers can put
    /// on it — and it must not exceed the reassembly ceiling those bytes already passed, or the bound would
    /// merely move the failure to a layer that allocates first.
    ///
    /// WHY A LAW. The bound and the producer live in different subsystems and neither mentions the other at
    /// runtime: nothing fails at build time, nothing fails on a fresh campaign, and the regression only
    /// appears hours into a session on the peer that is not the host. That is exactly the shape a unit test
    /// nobody writes would have caught, so it is executed here instead.
    ///
    /// THE ARMS:
    ///   (a) <c>blob-roundtrip</c>, EXECUTED against the shipped codec — a string of
    ///       <see cref="MistSync.MaxMistDataBytes"/> encoded by <c>RailMeta.EncodeLeaf</c> must come back
    ///       identical from <c>RailMeta.DecodeLeaf</c>. This is the whole claim, run rather than asserted.
    ///   (b) POSITIVE CONTROL, EXECUTED — arm (a) proves nothing if the decode path is unbounded, because
    ///       then it would pass at any size forever. The SAME encoded bytes are read back through the old
    ///       64 KB ceiling and must be REFUSED, which is both the proof that a bound is really in the path
    ///       and the exact historical failure, reproduced.
    ///   (c) <c>bound-shape</c> — the declared ceiling must sit between the producer and the reassembler:
    ///       <c>WireString.Blob</c> >= <see cref="MistSync.MaxMistDataBytes"/>, and
    ///       <c>WireString.Blob</c> &lt;= <c>RailMeta.MaxReassembledBytes</c>.
    ///
    /// Falsify: put <c>WireString.Prose</c> back on the string leaf (or lower <c>WireString.Blob</c> under
    /// the mist ceiling) → (a) + (c); drop the ceiling argument from the leaf read entirely → (b).
    /// </summary>
    internal static class L470_TheMistBlobFitsTheStringLeafItRidesOn
    {
        internal static IEnumerable<string> Check()
        {
            var payload = new string('A', MistSync.MaxMistDataBytes);
            byte[] encoded;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8))
            {
                RailMeta.EncodeLeaf(w, typeof(string), payload);
                encoded = ms.ToArray();
            }

            // ── arm (b), FIRST: a round-trip through an UNBOUNDED reader would pass arm (a) forever.
            var refused = false;
            try
            {
                using (var ms = new MemoryStream(encoded))
                using (var r = new BinaryReader(ms, System.Text.Encoding.UTF8))
                {
                    r.ReadByte(); // the LeafKind marker EncodeLeaf wrote in front of the string
                    MessageSerializer.ReadBoundedString(r, WireString.Prose);
                }
            }
            catch (InvalidDataException) { refused = true; }
            if (!refused)
            {
                yield return "L470 premise-changed: POSITIVE CONTROL failed — a " + MistSync.MaxMistDataBytes +
                             "-byte string leaf was NOT refused at the old 64 KB ceiling (WireString.Prose). " +
                             "Either EncodeLeaf no longer writes a leaf-kind byte in front of the string, or " +
                             "the length bound left MessageSerializer.ReadBoundedString. Arm (a) below only " +
                             "means something while a ceiling is genuinely in the read path, so fix the reader " +
                             "(or re-point this law) before believing it.";
                yield break;
            }

            // ── arm (a): the real claim, run against the shipped codec.
            string decoded = null;
            string threw = null;
            try
            {
                using (var ms = new MemoryStream(encoded))
                using (var r = new BinaryReader(ms, System.Text.Encoding.UTF8))
                    decoded = RailMeta.DecodeLeaf(r, typeof(string), null) as string;
            }
            catch (Exception ex) { threw = ex.Message; }
            if (threw != null || !string.Equals(decoded, payload, StringComparison.Ordinal))
                yield return "L470 blob-roundtrip: a " + MistSync.MaxMistDataBytes + "-byte string leaf does " +
                             "not survive RailMeta.EncodeLeaf → DecodeLeaf (" +
                             (threw ?? "decoded " + (decoded == null ? "null" : decoded.Length + " chars")) +
                             "). MistState.MistData is exactly that leaf and is exactly that big late in a " +
                             "campaign, so this is the client never receiving the mist at all — silently, " +
                             "with the host's CRC backstop re-emitting a subtree no re-emit can heal.";

            // ── arm (c): the ceiling sits between the producer and the reassembler.
            if (WireString.Blob < MistSync.MaxMistDataBytes)
                yield return "L470 bound-shape: WireString.Blob (" + WireString.Blob + ") is below what " +
                             "MistSync can produce (" + MistSync.MaxMistDataBytes + "). The rail's string " +
                             "leaf must clear its own producers, or the leaf fails to apply on every peer.";
            if (WireString.Blob > RailMeta.MaxReassembledBytes)
                yield return "L470 bound-shape: WireString.Blob (" + WireString.Blob + ") exceeds " +
                             "RailMeta.MaxReassembledBytes (" + RailMeta.MaxReassembledBytes + "). Bytes reach " +
                             "this decoder only through GenericApplier.Reassemble, so a wider string ceiling " +
                             "buys nothing and only moves the refusal to the layer that allocates first.";
        }
    }
}
