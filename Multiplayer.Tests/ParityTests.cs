using System.Collections.Generic;
using System.Linq;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Network.Parity;
using Xunit;

// FIX-4 — pure host/client parity gate: manifest build determinism, host-authoritative comparison
// (DLC/mods/settings blocking rules), diff formatting, and the JOIN-embedded wire round-trip.
public class ParityTests
{
    private static ParityManifest Build(
        IEnumerable<string> dlc,
        IEnumerable<(string, string)> mods,
        IEnumerable<(string, IEnumerable<(string, string)>)> settings = null)
        => ParityManifest.Build(dlc, mods,
            settings ?? Enumerable.Empty<(string, IEnumerable<(string, string)>)>());

    [Fact]
    public void Build_IsOrderIndependent_AndHashesMatch()
    {
        var a = Build(
            new[] { "Zeta", "Alpha" },
            new[] { ("modB", "2.0"), ("modA", "1.0") },
            new[] { ("modA", (IEnumerable<(string, string)>)new[] { ("b", "2"), ("a", "1") }) });
        var b = Build(
            new[] { "Alpha", "Zeta" },
            new[] { ("modA", "1.0"), ("modB", "2.0") },
            new[] { ("modA", (IEnumerable<(string, string)>)new[] { ("a", "1"), ("b", "2") }) });

        Assert.Equal(a.Dlc, b.Dlc);
        Assert.Equal(new[] { "Alpha", "Zeta" }, a.Dlc);
        Assert.Equal(a.Mods.Select(m => m.Id), b.Mods.Select(m => m.Id));
        Assert.Equal(a.Settings[0].Hash, b.Settings[0].Hash);
        Assert.Empty(ParityComparer.Compare(a, b));
    }

    [Fact]
    public void Compare_Identical_NoDiffs()
    {
        var host = Build(new[] { "DlcX" }, new[] { ("mod", "1.0") });
        var client = Build(new[] { "DlcX" }, new[] { ("mod", "1.0") });
        Assert.Empty(ParityComparer.Compare(host, client));
    }

    [Fact]
    public void Compare_DlcMissingOnClient_Blocks()
    {
        var host = Build(new[] { "DlcX", "DlcY" }, new (string, string)[0]);
        var client = Build(new[] { "DlcX" }, new (string, string)[0]);
        var diffs = ParityComparer.Compare(host, client);
        Assert.Contains(diffs, d => d.Contains("DLC missing on client: DlcY"));
    }

    [Fact]
    public void Compare_ExtraDlcOnClient_IsAllowed()
    {
        var host = Build(new[] { "DlcX" }, new (string, string)[0]);
        var client = Build(new[] { "DlcX", "DlcExtra" }, new (string, string)[0]);
        Assert.Empty(ParityComparer.Compare(host, client));
    }

    [Fact]
    public void Compare_ModMissingExtraAndVersion_Block()
    {
        var host = Build(new string[0], new[] { ("common", "1.0"), ("hostOnly", "3.0") });
        var client = Build(new string[0], new[] { ("common", "1.1"), ("clientOnly", "4.0") });
        var diffs = ParityComparer.Compare(host, client);
        Assert.Contains(diffs, d => d.Contains("Mod missing on client: hostOnly v3.0"));
        Assert.Contains(diffs, d => d.Contains("Extra mod on client: clientOnly v4.0"));
        Assert.Contains(diffs, d => d.Contains("Mod version differs: common host v1.0 != client v1.1"));
    }

    [Fact]
    public void Compare_SettingsValueDiffers_ShowsBothValues()
    {
        var host = Build(new string[0], new[] { ("m", "1.0") },
            new[] { ("m", (IEnumerable<(string, string)>)new[] { ("diff", "42"), ("hostKey", "on") }) });
        var client = Build(new string[0], new[] { ("m", "1.0") },
            new[] { ("m", (IEnumerable<(string, string)>)new[] { ("diff", "7") }) });
        var diffs = ParityComparer.Compare(host, client);
        Assert.Contains(diffs, d => d.Contains("Setting m.diff: host=42 client=7"));
        Assert.Contains(diffs, d => d.Contains("Setting m.hostKey: host=on client=(absent)"));
    }

    [Fact]
    public void Compare_NullClientManifest_Blocks()
    {
        var host = Build(new[] { "DlcX" }, new (string, string)[0]);
        var diffs = ParityComparer.Compare(host, null);
        Assert.NotEmpty(diffs);
        Assert.Contains(diffs, d => d.Contains("manifest missing"));
    }

    [Fact]
    public void Serialize_ParityManifest_RoundTrips()
    {
        var m = Build(new[] { "DlcX", "DlcY" }, new[] { ("mod", "1.2.3") },
            new[] { ("mod", (IEnumerable<(string, string)>)new[] { ("a", "1"), ("b", "two") }) });
        var back = MessageSerializer.DeserializeParityManifest(MessageSerializer.SerializeParityManifest(m));
        Assert.Equal(m.Dlc, back.Dlc);
        Assert.Equal(m.Mods.Select(x => x.Id + "@" + x.Version), back.Mods.Select(x => x.Id + "@" + x.Version));
        Assert.Equal(m.Settings[0].Hash, back.Settings[0].Hash);
        Assert.Equal(m.Settings[0].Entries, back.Settings[0].Entries);
        Assert.Empty(ParityComparer.Compare(m, back));
    }

    [Fact]
    public void Join_WithManifest_RoundTrips_AndLegacyIsNull()
    {
        var manifest = Build(new[] { "DlcX" }, new[] { ("mod", "1.0") });
        var join = new JoinMessage
        {
            PlayerGuid = System.Guid.NewGuid(),
            Nickname = "tester",
            Manifest = manifest
        };
        var back = MessageSerializer.DeserializeJoin(MessageSerializer.SerializeJoin(join));
        Assert.Equal(join.PlayerGuid, back.PlayerGuid);
        Assert.Equal("tester", back.Nickname);
        Assert.NotNull(back.Manifest);
        Assert.Empty(ParityComparer.Compare(manifest, back.Manifest));

        // Legacy JOIN (no manifest attached) deserializes with Manifest == null.
        var legacy = new JoinMessage { PlayerGuid = System.Guid.NewGuid(), Nickname = "old" };
        var legacyBack = MessageSerializer.DeserializeJoin(MessageSerializer.SerializeJoin(legacy));
        Assert.Null(legacyBack.Manifest);
    }

    // ─── Soft gate: ready decision + roster wire + status transitions ───

    [Fact]
    public void ReadyAllowed_OnlyWhenDiffsEmpty()
    {
        Assert.True(ParityComparer.ReadyAllowed(""));
        Assert.True(ParityComparer.ReadyAllowed(null));
        Assert.False(ParityComparer.ReadyAllowed("Mod missing on client: x v1.0"));
    }

    [Fact]
    public void PeerList_CarriesParityDiffs_RoundTrip()
    {
        var peers = new List<PeerListEntry>
        {
            new PeerListEntry { SteamId = 1, PlayerGuid = System.Guid.NewGuid(), Nickname = "host",
                                IsHost = true, SlotIndex = 0, ParityDiffs = "" },
            new PeerListEntry { SteamId = 2, PlayerGuid = System.Guid.NewGuid(), Nickname = "cl",
                                Ready = true, SlotIndex = 1, ParityDiffs = "Mod missing on client: x v1.0" },
        };
        var back = MessageSerializer.DeserializePeerList(MessageSerializer.SerializePeerList(peers));
        Assert.Equal("", back[0].ParityDiffs);
        Assert.Equal("Mod missing on client: x v1.0", back[1].ParityDiffs);
        Assert.True(back[1].Ready); // pre-existing fields untouched by the trailing block
    }

    [Fact]
    public void PeerList_LegacyPayloadWithoutTrailingBlock_Parses()
    {
        // Simulate a legacy sender: serialize, then TRUNCATE the trailing parity block (marker byte
        // onward). A new reader must parse the entries and default ParityDiffs to "".
        var peers = new List<PeerListEntry>
        {
            new PeerListEntry { SteamId = 7, PlayerGuid = System.Guid.NewGuid(), Nickname = "p",
                                SlotIndex = 0, ParityDiffs = "" },
        };
        var full = MessageSerializer.SerializePeerList(peers);
        // Trailing block for 1 peer with "" diffs = bool(1) + 7-bit-len(1) = 2 bytes.
        var legacy = new byte[full.Length - 2];
        System.Array.Copy(full, legacy, legacy.Length);
        var back = MessageSerializer.DeserializePeerList(legacy);
        Assert.Single(back);
        Assert.Equal("", back[0].ParityDiffs);
        Assert.Equal(7UL, back[0].SteamId);
    }

    [Fact]
    public void StatusTransition_MismatchThenFixed_ClearsAndUnlocksReady()
    {
        var host = Build(new string[0], new[] { ("m", "1.0") },
            new[] { ("m", (IEnumerable<(string, string)>)new[] { ("speed", "2") }) });
        var clientBefore = Build(new string[0], new[] { ("m", "1.0") },
            new[] { ("m", (IEnumerable<(string, string)>)new[] { ("speed", "5") }) });

        // Mismatch → diff text non-empty → ready locked.
        var diffText = ParityComparer.Format(ParityComparer.Compare(host, clientBefore));
        Assert.False(ParityComparer.ReadyAllowed(diffText));
        Assert.Contains("Setting m.speed: host=2 client=5", diffText);

        // Client converges (auto-apply host settings) → re-compare → clear → ready allowed.
        var clientAfter = Build(new string[0], new[] { ("m", "1.0") },
            new[] { ("m", (IEnumerable<(string, string)>)new[] { ("speed", "2") }) });
        var after = ParityComparer.Format(ParityComparer.Compare(host, clientAfter));
        Assert.True(ParityComparer.ReadyAllowed(after));
    }
}
