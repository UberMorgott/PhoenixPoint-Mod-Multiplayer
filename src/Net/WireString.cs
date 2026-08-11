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
        /// anything that is plainly not an identifier.</summary>
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

        internal static string ReadKey(BinaryReader r) => MessageSerializer.ReadBoundedString(r, Key);

        internal static string ReadText(BinaryReader r) => MessageSerializer.ReadBoundedString(r, Text);

        internal static string ReadProse(BinaryReader r) => MessageSerializer.ReadBoundedString(r, Prose);
    }
}
