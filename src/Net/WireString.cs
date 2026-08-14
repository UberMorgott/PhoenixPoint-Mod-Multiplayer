using System.IO;

namespace Multiplayer.Network.MessageLayer
{
    /// <summary>Named byte ceilings for strings decoded off the wire, over
    /// <see cref="MessageSerializer.ReadBoundedString"/>.
    ///
    /// FA-0012 generalised. <c>BinaryReader.ReadString</c> pre-sizes a StringBuilder from the length the
    /// SENDER declared, before a byte of payload is read, so a 20-byte packet claiming 2^28 makes the
    /// receiver allocate ~512 MB. <c>ReadBoundedString</c> closes that by reading the 7-bit prefix itself and
    /// refusing a length the stream cannot back — but "the stream can back it" is a weak bound on a large
    /// legitimate packet, and it is the same bound for a body-part slot id as for an event narrative. These
    /// three buckets give each site a ceiling in the order of magnitude the value actually has.
    ///
    /// The bound is READ-SIDE ONLY: the helper consumes exactly the bytes <c>BinaryWriter.Write(string)</c>
    /// produced and decodes with the same UTF-8, so the wire format is unchanged.
    ///
    /// <see cref="MessageSerializer.ReadBoundedString"/> STAYS ON <see cref="MessageSerializer"/> — RailCheck
    /// L414 arm (b) resolves it by name off that type and proves the serializer still reaches it. This class
    /// only calls in there; it is a naming layer, not a new home.</summary>
    internal static class WireString
    {
        /// <summary>Machine identifiers: def GUIDs, entity ref keys, slot ids, .NET type names,
        /// localization keys, dictionary keys. The longest such string the game itself produces is a def
        /// name well under 100 chars; 256 clears a modded one several times over and still rejects
        /// anything that is plainly not an identifier.
        ///
        /// THE ONE SITE THAT NEEDED MEASURING, AND WAS. <c>RailMeta</c>'s create frames write a raw
        /// <c>t.FullName</c> (<c>EncodeDescendCreate</c>, <c>WriteActorCreateFrame</c>) and read it back
        /// through this ceiling in three places (<c>DecodeActorCreateDef</c>,
        /// <c>DescendCreateTypeName</c>, <c>DecodeCreateArgs</c>). A CLOSED GENERIC's <c>FullName</c>
        /// carries assembly-qualified type arguments and would blow past 256 — so the reachable sets were
        /// enumerated off the real game assembly rather than assumed:
        ///
        ///   [SerializeCustomCreate] types (the Descend frame): n=613, max 114, median 62
        ///                                                      (MissionContainsKeyStructuresConditionDef)
        ///   ActorComponent subclasses  (the actor frame):      n=24,  max  75, median 51
        ///   every concrete type in Assembly-CSharp:            n=10043, max 210 — and the four longest are
        ///     compiler-generated iterator/closure nests, which are never a create-frame instance type.
        ///   GENERIC types in either reachable set:             0 — so the closed-generic worry has no
        ///     subject here, not merely a small one.
        ///
        /// Nothing reaches 256, the worst real case leaves ~140 chars of headroom for a modded namespace,
        /// and encoder and decoder are the same code — a mod that somehow exceeded it would fail loudly on
        /// its first create frame for everyone, not silently for one player. Left at Key deliberately: Text
        /// would buy 4 KB of nothing and stop calling a type name an identifier.</summary>
        internal const int Key = 256;

        /// <summary>Short human strings: a player-typed base name, a recruit name, a modal title, a
        /// diagnostic reason. Invented ceiling — the game's own name fields cap far below this, and 4 KB
        /// clears the longest localized title in any language.</summary>
        internal const int Text = 4096;

        /// <summary>Rendered prose: an event narrative body, or an arbitrary <c>string</c> member of a
        /// railed type, which may be a def description. Invented ceiling, deliberately the same order as
        /// <see cref="MessageSerializer.MaxManifestBytes"/>: large enough that no legitimate body is ever
        /// refused, small enough that the allocation is bounded at kilobytes rather than gigabytes.</summary>
        internal const int Prose = 64 * 1024;

        /// <summary>A railed string leaf, which is not prose at all when the state IS a blob: mist coverage
        /// ships <c>MistState.MistData</c> — the game's own deflate+base64 of <c>_mistData</c> — as a plain
        /// <c>string</c> member, ~40 KB early and ~900 KB late-campaign
        /// (<see cref="Multiplayer.Network.Sync.MistSync.MaxMistDataBytes"/>). Under
        /// <see cref="Prose"/> that leaf started failing to APPLY the moment a campaign's mist passed 64 KB
        /// ("implausible length 67220", 35× in one session) and the CRC backstop then re-emitted a subtree
        /// no re-emit could heal.
        ///
        /// NOT an invented number, and not a wider door than the wire has: a leaf's bytes reach the decoder
        /// only after <c>GenericApplier.Reassemble</c>, which itself refuses a total above
        /// <see cref="Multiplayer.Network.Sync.RailMeta.MaxReassembledBytes"/> before it allocates — so this
        /// is that same ceiling restated, and the per-message transport limit
        /// (<c>NetworkMessage.MaxPayloadBytes</c>, 8 MB) is never approached because
        /// <c>DiffEngine.FragmentForWire</c> splits an oversize value into ≤64 KB fragments regardless.
        /// The stream bound in <see cref="MessageSerializer.ReadBoundedString"/> still applies underneath,
        /// so a declared length the buffer cannot back is refused here exactly as before.</summary>
        internal const int Blob = Multiplayer.Network.Sync.RailMeta.MaxReassembledBytes;

        internal static string ReadKey(BinaryReader r) => MessageSerializer.ReadBoundedString(r, Key);

        internal static string ReadText(BinaryReader r) => MessageSerializer.ReadBoundedString(r, Text);

        internal static string ReadProse(BinaryReader r) => MessageSerializer.ReadBoundedString(r, Prose);

        internal static string ReadBlob(BinaryReader r) => MessageSerializer.ReadBoundedString(r, Blob);
    }
}
