using System;
using Multiplayer.Network;
using Xunit;

// Inc4 V2 — pure gate + display-time helper for the CLIENT geoscape "true sim pin". The game-bound wiring
// (WriteClock skipping the per-frame ProcessInstanceData; ClientTimeDateDisplayFreezePatch painting the HUD
// clock) is not unit-testable, but the DECISION ("pin the sim clock?") and the DISPLAY DateTime conversion are
// extracted into ClientSimFreezeV2Gate so the truth table + the TimeUnit.DateTime reproduction are asserted here.
public class ClientSimFreezeV2GateTests
{
    // ─── ShouldPinSim: pin ONLY when V2 gate ON AND the V1 freeze is active ───

    [Fact]
    public void ShouldPinSim_BothOn_Pins()
    {
        // V2 gate ON + V1 freeze active (active-session client) → pin the sim clock.
        Assert.True(ClientSimFreezeV2Gate.ShouldPinSim(v2Enabled: true, freeze: true));
    }

    [Fact]
    public void ShouldPinSim_V2Off_NeverPins()
    {
        // V2 rollback: gate OFF → never pin, even when the V1 freeze is active (exact V1 per-frame-advance path).
        Assert.False(ClientSimFreezeV2Gate.ShouldPinSim(v2Enabled: false, freeze: true));
    }

    [Fact]
    public void ShouldPinSim_FreezeInactive_NeverPins()
    {
        // No V1 freeze (host / single-player / no session / V1-flag-OFF, all folded into ShouldFreeze=false) →
        // never pin, regardless of the V2 gate. V2 is a strict refinement of V1.
        Assert.False(ClientSimFreezeV2Gate.ShouldPinSim(v2Enabled: true, freeze: false));
        Assert.False(ClientSimFreezeV2Gate.ShouldPinSim(v2Enabled: false, freeze: false));
    }

    [Fact]
    public void Enabled_DefaultsOn_ForValidation()
    {
        // The V2 pin ships ON as the committed validation default; rollback = set Enabled=false + rebuild.
        Assert.True(ClientSimFreezeV2Gate.Enabled);
    }

    // ─── DisplayDateTime: byte-for-byte reproduction of Base.Core.TimeUnit.DateTime (default+TimeSpan) ───

    [Fact]
    public void DisplayDateTime_Zero_IsDefaultDateTime()
    {
        // TimeUnit.DateTime => default(DateTime) + _time; at 0 seconds that is default(DateTime) (0001-01-01).
        Assert.Equal(default(DateTime), ClientSimFreezeV2Gate.DisplayDateTime(0));
    }

    [Fact]
    public void DisplayDateTime_MatchesTimeSpanOffset()
    {
        // 90s → 00:01:30 past the epoch: the same value the widget derived from Now under V1
        // (Now == FromTimeSpan(FromSeconds(display)); Now.DateTime == default + that TimeSpan).
        var dt = ClientSimFreezeV2Gate.DisplayDateTime(90);
        Assert.Equal(default(DateTime) + TimeSpan.FromSeconds(90), dt);
        Assert.Equal(1, dt.Minute);
        Assert.Equal(30, dt.Second);
    }

    [Fact]
    public void DisplayDateTime_HoursAndDays_Compose()
    {
        // 1h + 1min = 3660s → HH:mm == 01:01 (the fields the postfix formats).
        var dt = ClientSimFreezeV2Gate.DisplayDateTime(3660);
        Assert.Equal(1, dt.Hour);
        Assert.Equal(1, dt.Minute);
        // One day of seconds advances the calendar day by 1 from 0001-01-01.
        var day = ClientSimFreezeV2Gate.DisplayDateTime(86400);
        Assert.Equal(2, day.Day);
    }
}
