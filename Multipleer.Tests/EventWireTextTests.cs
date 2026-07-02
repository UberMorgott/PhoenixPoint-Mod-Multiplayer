using System.IO;
using System.Text;
using Multipleer.Network.Sync;
using Multipleer.Network.Sync.State;
using Xunit;

/// <summary>
/// Host-authoritative WIRE TEXT for geoscape-event dialogs (VoidOmen blank-window fix).
///
/// TFTV VoidOmen events (VoidOmen_{0..19}) are created with EMPTY title/description keys
/// (TFTVDefsInjectedOnlyOnce.cs:7668-7679 → TFTVCommonMethods.CreateNewEvent(name,"","",null));
/// their narrative exists ONLY as a HOST-side runtime def mutation
/// (TFTVODIandVoidOmenRoll.GenerateVoidOmenEvent :638-639 sets Title/Description[0].General to
/// literal LocalizedTextBind(text, true)). The client's local def keeps LocalizationKey="" →
/// EventTextVariation.GetText → "" → blank raise window AND blank result page. Client-side def
/// resolution CANNOT work, so the host resolves the narrative natively at broadcast time and
/// ships the strings on the wire:
///   • EventRaised  += wireTitle + wireNarrative (Title.Localize() / Description.Last().GetText(ctx));
///   • EventDismiss += wireOutcome + wireNarrative (SelectedChoice.Outcome.OutcomeText.GetText(ctx)
///     / Description.Last().GetText(ctx), the native SetClosingEncounter pair :332-336).
/// The client prefers NON-EMPTY wire text over local-def resolution; empty/absent wire text keeps
/// the existing local fallback byte-identical (SDI_07 etc.).
/// </summary>
public class EventWireTextTests
{
    // ─── (a) HOST payload carries the resolved strings ────────────────────────────────

    [Fact]
    public void EventRaised_WithWireTexts_RoundTrips()
    {
        var bytes = SyncProtocol.EncodeEventRaised(4242, "VoidOmen_7", -1, -1, identity: null,
            singleChoice: true, oneWindow: true,
            wireTitle: "НОВОЕ ЗНАМЕНИЕ ПУСТОТЫ <b>Знамение</b>", wireNarrative: "The Void whispers.\n\nBeware.");
        Assert.True(SyncProtocol.TryDecodeEventRaised(bytes, out var occ, out var ev, out _, out _,
            out var hasId, out _, out var single, out var oneWin, out var title, out var narrative));
        Assert.Equal(4242, occ);
        Assert.Equal("VoidOmen_7", ev);
        Assert.False(hasId);
        Assert.True(single);
        Assert.True(oneWin);
        Assert.Equal("НОВОЕ ЗНАМЕНИЕ ПУСТОТЫ <b>Знамение</b>", title);
        Assert.Equal("The Void whispers.\n\nBeware.", narrative);
    }

    [Fact]
    public void EventRaised_WithWireTextsAndIdentity_RoundTrips_IdentityFirst()
    {
        // Texts are appended AFTER the identity block (fields added at END of payload).
        var id = new GeoSiteState(1337, "FAC-GUID", siteType: 30, state: 1, siteName: "KEY_SITE_42", encounterID: "ENC9");
        var bytes = SyncProtocol.EncodeEventRaised(7, "EV_BOTH", 1337, 3, id,
            singleChoice: false, oneWindow: false, wireTitle: "T", wireNarrative: "N");
        Assert.True(SyncProtocol.TryDecodeEventRaised(bytes, out _, out _, out _, out _,
            out var hasId, out var gotId, out _, out _, out var title, out var narrative));
        Assert.True(hasId);
        Assert.Equal(id, gotId);
        Assert.Equal("T", title);
        Assert.Equal("N", narrative);
    }

    [Fact]
    public void EventRaised_EmptyWireTexts_BytesIdenticalToLegacyEncode()
    {
        // No texts → NO trailing text block; the wire stays byte-identical to the pre-text encoder
        // so the pinned legacy wire (EventRaised_WireBytes_AreStable) is untouched.
        var legacy = SyncProtocol.EncodeEventRaised(7, "AB", 1, 2, null, singleChoice: true, oneWindow: false);
        var withEmpty = SyncProtocol.EncodeEventRaised(7, "AB", 1, 2, null, singleChoice: true, oneWindow: false,
            wireTitle: "", wireNarrative: null);
        Assert.Equal(legacy, withEmpty);
    }

    [Fact]
    public void EventRaised_LegacyPayload_DecodesEmptyWireTexts()
    {
        // A payload emitted WITHOUT texts decodes with empty strings (→ client local fallback).
        var bytes = SyncProtocol.EncodeEventRaised(9, "EV_OLD", 42, 5);
        Assert.True(SyncProtocol.TryDecodeEventRaised(bytes, out _, out _, out _, out _,
            out _, out _, out _, out _, out var title, out var narrative));
        Assert.Equal("", title);
        Assert.Equal("", narrative);
    }

    [Fact]
    public void EventRaised_TitleOnly_RoundTrips_NarrativeEmpty()
    {
        var bytes = SyncProtocol.EncodeEventRaised(2, "EV_T", -1, -1, null, false, false, "Only title", "");
        Assert.True(SyncProtocol.TryDecodeEventRaised(bytes, out _, out _, out _, out _,
            out _, out _, out _, out _, out var title, out var narrative));
        Assert.Equal("Only title", title);
        Assert.Equal("", narrative);
    }

    [Fact]
    public void EventDismiss_WithWireTexts_RoundTrips()
    {
        var reward = new byte[] { 0xDE, 0xAD };
        var bytes = SyncProtocol.EncodeEventDismiss(555, "VoidOmen_7", 0, reward, 1337,
            wireOutcome: "Outcome text.", wireNarrative: "Знамение Пустоты: паника.");
        Assert.True(SyncProtocol.TryDecodeEventDismiss(bytes, out var occ, out var ev, out var choice,
            out var blob, out var siteId, out var outcome, out var narrative));
        Assert.Equal(555, occ);
        Assert.Equal("VoidOmen_7", ev);
        Assert.Equal(0, choice);
        Assert.Equal(reward, blob);
        Assert.Equal(1337, siteId);
        Assert.Equal("Outcome text.", outcome);
        Assert.Equal("Знамение Пустоты: паника.", narrative);
    }

    [Fact]
    public void EventDismiss_WithWireTexts_NoRewardNoSite_RoundTrips()
    {
        // Texts force the empty reward-length marker + the siteId(-1) so the trailing strings are
        // unambiguous on decode.
        var bytes = SyncProtocol.EncodeEventDismiss(6, "EV_TEXTS", 1, null, -1,
            wireOutcome: "", wireNarrative: "Narrative only.");
        Assert.True(SyncProtocol.TryDecodeEventDismiss(bytes, out _, out _, out var choice,
            out var blob, out var siteId, out var outcome, out var narrative));
        Assert.Equal(1, choice);
        Assert.Empty(blob);
        Assert.Equal(-1, siteId);
        Assert.Equal("", outcome);
        Assert.Equal("Narrative only.", narrative);
    }

    [Fact]
    public void EventDismiss_EmptyWireTexts_BytesIdenticalToLegacyEncode()
    {
        var reward = new byte[] { 0xBE, 0xEF };
        var legacy = SyncProtocol.EncodeEventDismiss(5, "AB", 2, reward, 9);
        var withEmpty = SyncProtocol.EncodeEventDismiss(5, "AB", 2, reward, 9, wireOutcome: null, wireNarrative: "");
        Assert.Equal(legacy, withEmpty);
        // And the no-reward/no-site legacy 3-field wire stays pinned too.
        Assert.Equal(SyncProtocol.EncodeEventDismiss(5, "AB", 2, null, -1),
                     SyncProtocol.EncodeEventDismiss(5, "AB", 2, null, -1, null, null));
    }

    [Fact]
    public void EventDismiss_LegacyPayload_DecodesEmptyWireTexts()
    {
        var bytes = SyncProtocol.EncodeEventDismiss(8, "EV_OLD", 2, new byte[] { 0x01 }, 4);
        Assert.True(SyncProtocol.TryDecodeEventDismiss(bytes, out _, out _, out _,
            out var blob, out var siteId, out var outcome, out var narrative));
        Assert.Equal(new byte[] { 0x01 }, blob);
        Assert.Equal(4, siteId);
        Assert.Equal("", outcome);
        Assert.Equal("", narrative);
    }

    [Fact]
    public void EventDismiss_TruncatedTextBlock_StillDecodesLeadingFields()
    {
        // A corrupt/truncated trailing text block must not fail the whole decode: the leading
        // fields (occId/eventId/choiceIndex/reward/siteId) still come through, texts degrade to "".
        var full = SyncProtocol.EncodeEventDismiss(3, "EV_TRUNC", 0, null, 7,
            wireOutcome: "OUTCOME-LONG-TEXT", wireNarrative: "NARR");
        var truncated = new byte[full.Length - 8];   // drop the narrative block (6B) + 2B off the outcome string
        System.Array.Copy(full, truncated, truncated.Length);
        Assert.True(SyncProtocol.TryDecodeEventDismiss(truncated, out var occ, out var ev, out var choice,
            out _, out var siteId, out var outcome, out var narrative));
        Assert.Equal(3, occ);
        Assert.Equal("EV_TRUNC", ev);
        Assert.Equal(0, choice);
        Assert.Equal(7, siteId);
        Assert.Equal("", outcome);
        Assert.Equal("", narrative);
    }

    // ─── (b) CLIENT prefers non-empty wire text over local-def resolution ─────────────

    [Fact]
    public void ResultBody_WireOutcome_PreferredOverLocalOutcome()
        => Assert.Equal("wire outcome",
            EventReflection.ChooseResultBodyText(wireOutcomeText: "wire outcome", wireNarrativeText: "",
                outcomeText: "local outcome", narrativeText: null, singleChoiceOneWindow: false));

    [Fact]
    public void ResultBody_OneWindow_WireNarrative_PreferredOverLocal_WhenOutcomesEmpty()
        => Assert.Equal("wire narrative",
            EventReflection.ChooseResultBodyText(wireOutcomeText: "", wireNarrativeText: "wire narrative",
                outcomeText: "", narrativeText: "local narrative", singleChoiceOneWindow: true));

    [Fact]
    public void ResultBody_OneWindow_VoidOmen_EmptyLocal_WireNarrativeWins()
        // The actual VoidOmen shape: local def resolves EMPTY everywhere; only the wire has text.
        => Assert.Equal("The Void whispers.",
            EventReflection.ChooseResultBodyText(wireOutcomeText: "", wireNarrativeText: "The Void whispers.",
                outcomeText: "", narrativeText: "", singleChoiceOneWindow: true));

    [Fact]
    public void ResultBody_MultiChoice_WireNarrative_NeverUsed()
        // Native: a multi-choice close (useEventTexts:false) NEVER falls back to the narrative.
        => Assert.Equal("",
            EventReflection.ChooseResultBodyText(wireOutcomeText: "", wireNarrativeText: "wire narrative",
                outcomeText: "", narrativeText: null, singleChoiceOneWindow: false));

    // ─── (c) empty/absent wire text → local fallback byte-identical ───────────────────

    [Fact]
    public void ResultBody_EmptyWire_FallsBackToLocal_ExactlyAsLegacyPicker()
    {
        // With no wire text the 5-param picker must equal the legacy 3-param picker on every shape
        // (SDI_07-style events keep working byte-identical).
        var shapes = new[]
        {
            new { Outcome = "Outcome shown.", Narrative = (string)"narrative", OneWindow = true },
            new { Outcome = "", Narrative = (string)"The Void whispers.", OneWindow = true },
            new { Outcome = "", Narrative = (string)"narrative", OneWindow = false },
            new { Outcome = "Outcome shown.", Narrative = (string)"narrative", OneWindow = false },
            new { Outcome = "", Narrative = (string)null, OneWindow = true },
        };
        foreach (var s in shapes)
        {
            var legacy = EventReflection.ChooseResultBodyText(s.Outcome, s.Narrative, s.OneWindow);
            Assert.Equal(legacy, EventReflection.ChooseResultBodyText("", "", s.Outcome, s.Narrative, s.OneWindow));
            Assert.Equal(legacy, EventReflection.ChooseResultBodyText(null, null, s.Outcome, s.Narrative, s.OneWindow));
        }
    }

    [Fact]
    public void UseWireText_NonEmptyOnly()
    {
        // BuildEvent's raise-window override decision: only NON-EMPTY wire text replaces the local
        // def's Title/Description; empty/absent leaves the local def untouched.
        Assert.False(EventReflection.UseWireText(null));
        Assert.False(EventReflection.UseWireText(""));
        Assert.True(EventReflection.UseWireText("Знамение"));
    }
}
