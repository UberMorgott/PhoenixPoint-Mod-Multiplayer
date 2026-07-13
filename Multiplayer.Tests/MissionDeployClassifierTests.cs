using Multiplayer.Network.Sync;
using Xunit;

/// <summary>
/// Pure pins for the mission-deploy exclusion classifier behind <c>EventReflection.IsMissionDeployEvent</c>
/// (the reflection reader binds live game types and is not unit-linkable; this boundary is).
///
/// Contract (2026-07-13 narrowing — 9e80b24 regression fix):
///   • PURE deploy prompt = ≥1 mission-starting choice AND every other choice a bare decline → SKIP mirror.
///   • Story event MIXING a mission choice with a rewarded/outcome-bearing alternative → MIRROR (the
///     regression: 9e80b24's ANY-classifier silently skipped every PROG_* story window with a mission choice).
///   • No mission choice at all → MIRROR (plain narrative / scavenge / diplomacy).
///   • Null mission flags → MIRROR (fail OPEN to broadcast on a read failure).
/// </summary>
public class MissionDeployClassifierTests
{
    // ── pure deploy prompts → skip mirror (the 9e80b24 goal, kept) ─────────────────────────────
    [Theory]
    [InlineData(new[] { true, false }, new[] { false, false })]   // Deploy + bare Leave (PROG_AN2_MISS shape)
    [InlineData(new[] { false, true }, new[] { false, false })]   // order-independent (Leave + Deploy)
    [InlineData(new[] { true }, new[] { false })]                 // lone Deploy choice
    [InlineData(new[] { true, true }, new[] { false, false })]    // both start missions
    [InlineData(new[] { true, false, false }, new[] { false, false, false })] // Deploy + two bare declines
    public void PureDeployPrompt_Skips(bool[] startsMission, bool[] hasPayload)
        => Assert.True(MissionDeployClassifier.IsPureDeployPrompt(startsMission, hasPayload));

    // ── story events with a mission choice + real alternatives → mirror (REGRESSION PIN) ───────
    [Theory]
    [InlineData(new[] { true, false }, new[] { false, true })]    // mission choice + rewarded alternative
    [InlineData(new[] { false, true }, new[] { true, false })]    // order-independent
    [InlineData(new[] { true, false, false }, new[] { false, false, true })] // ANY rewarded alt suffices
    [InlineData(new[] { true, true, false }, new[] { false, false, true })]  // two mission choices + story alt
    public void StoryEventWithMissionChoice_Mirrors(bool[] startsMission, bool[] hasPayload)
        => Assert.False(MissionDeployClassifier.IsPureDeployPrompt(startsMission, hasPayload));

    // ── no mission choice → mirror as normal ───────────────────────────────────────────────────
    [Theory]
    [InlineData(new bool[] { }, new bool[] { })]                  // no choices (siteless narrative)
    [InlineData(new[] { false }, new[] { false })]                // single info choice (EX93/PROG_PX12 scavenge)
    [InlineData(new[] { false }, new[] { true })]                 // single rewarded choice
    [InlineData(new[] { false, false }, new[] { true, true })]    // multi-choice narrative/diplomacy brief
    public void NoMissionChoice_Mirrors(bool[] startsMission, bool[] hasPayload)
        => Assert.False(MissionDeployClassifier.IsPureDeployPrompt(startsMission, hasPayload));

    // ── degraded reads ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public void NullMissionFlags_FailOpenToMirror()
        => Assert.False(MissionDeployClassifier.IsPureDeployPrompt(null, null));

    [Fact]
    public void NullPayloadFlags_ClassifyOnMissionFlagsAlone()
    {
        // payload unreadable → non-mission choices treated as bare declines (deploy-prompt exclusion kept)
        Assert.True(MissionDeployClassifier.IsPureDeployPrompt(new[] { true, false }, null));
        Assert.False(MissionDeployClassifier.IsPureDeployPrompt(new[] { false, false }, null));
    }

    [Fact]
    public void ShortPayloadArray_MissingEntriesTreatedAsDecline()
        => Assert.True(MissionDeployClassifier.IsPureDeployPrompt(new[] { true, false }, new[] { false }));
}
