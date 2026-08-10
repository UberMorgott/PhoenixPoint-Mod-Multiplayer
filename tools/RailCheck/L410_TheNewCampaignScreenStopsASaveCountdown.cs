using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;

namespace RailCheck
{
    /// <summary>
    /// L410 — OPENING THE NATIVE NEW-CAMPAIGN SCREEN STOPS A RUNNING SAVE COUNTDOWN.
    ///
    /// THE FAILURE, and it is SILENT AND DESTRUCTIVE: the host is ready, everybody is ready, a save is
    /// chosen — so <c>LobbyCountdown</c> has armed the five-second SAVE route. The host then presses NEW
    /// CAMPAIGN. That button gates on <c>LobbyController.ClientsReady</c>, which deliberately excludes
    /// <c>_saveChosen</c> and <c>_hostReady</c> (LobbyController.cs:147), so it opens while the clock runs —
    /// and nothing about opening a native screen changes the roster, so <c>CanStart</c> stays TRUE and
    /// <c>HostTick</c> keeps counting. At zero it fires <c>OnLobbyPlay</c>, which drops the curtain and
    /// loads THE OLD SAVE underneath the open native new-game screen. The host asked for a fresh campaign
    /// and got somebody's old one, with no error anywhere.
    ///
    /// NOT A QUORUM AND MUST NEVER BECOME ONE (P13). The fix is a CANCEL — the existing veto, taken by the
    /// host on its own behalf. It stops a clock and clears the host's OWN ready, which is what keeps the
    /// gate shut for as long as the host is off in the native flow. It waits on nobody: every other player
    /// can be AFK throughout, and the host alone drives both the cancel and whatever it does next.
    ///
    /// IL, NOT BEHAVIOUR, and that limit is worth stating: <c>MultiplayerUI</c> is a MonoBehaviour whose
    /// press path needs a live session, a roster and a native screen, none of which exist here. What this
    /// asserts is that the press path REACHES the cancel at all — the regression that actually happened is
    /// that it reached nothing.
    ///
    /// GUARDED TWICE: every member must resolve, and the walk over <c>OnLobbyNewCampaign</c> must reach
    /// <c>NewCampaignInterceptPatch.OpenNativeNewGameScreen</c> — the call that CLOSES that method — so a
    /// reader that died on an opcode it could not decode reports itself instead of accusing the button.
    ///
    /// Falsify: delete the <c>LobbyCountdown.Cancel</c> call from <c>MultiplayerUI.OnLobbyNewCampaign</c>.
    /// </summary>
    internal static class L410_TheNewCampaignScreenStopsASaveCountdown
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var mod = typeof(LobbyCountdown).Assembly;
            var ui = mod.GetType("Multiplayer.UI.MultiplayerUI");
            var press = ui?.GetMethod("OnLobbyNewCampaign", All);
            var cancel = typeof(LobbyCountdown).GetMethod("Cancel", All);
            var intercept = mod.GetType("Multiplayer.Harmony.NewCampaignInterceptPatch")
                            ?? mod.GetTypes().FirstOrDefault(t => t.Name == "NewCampaignInterceptPatch");
            var openNative = intercept?.GetMethod("OpenNativeNewGameScreen", All);

            if (ui == null || press == null || cancel == null || intercept == null || openNative == null)
            {
                yield return "L410 premise-changed: MultiplayerUI.OnLobbyNewCampaign, LobbyCountdown.Cancel " +
                             "or NewCampaignInterceptPatch.OpenNativeNewGameScreen no longer resolves, so " +
                             "nothing checks that entering the native new-campaign flow kills a running SAVE " +
                             "countdown. Re-point this law at whatever the new-campaign entry point is called " +
                             "now — do not delete it: what it guards is the OLD SAVE loading underneath the " +
                             "open NEW GAME screen, silently.";
                yield break;
            }

            // ANTI-VACUITY: OpenNativeNewGameScreen is the last call in the method, so seeing it proves the
            // IL walk reached the bottom rather than dying on an opcode (Program.CallSites yield-breaks).
            var callees = Program.Callees(press, mod).ToList();
            if (!callees.Any(c => c.MetadataToken == openNative.MetadataToken &&
                                  c.Module == openNative.Module))
            {
                yield return "L410 premise-changed: the IL walk over MultiplayerUI.OnLobbyNewCampaign never " +
                             "reached OpenNativeNewGameScreen, the call that closes that method — so the scan " +
                             "stopped early and the verdict below would accuse the button of something the " +
                             "reader simply did not see. Fix the reader, or pick a new landmark at the end.";
                yield break;
            }

            if (!callees.Any(c => c.MetadataToken == cancel.MetadataToken && c.Module == cancel.Module))
                yield return "L410 new-campaign-leaves-the-clock-running: MultiplayerUI.OnLobbyNewCampaign " +
                             "does not cancel the lobby countdown. The NEW CAMPAIGN gate is ClientsReady, " +
                             "which excludes the chosen save and the host's own ready, so this button opens " +
                             "while a SAVE countdown is already ticking — and CanStart stays true because " +
                             "opening a native screen changes no roster row. At zero OnLobbyPlay drops the " +
                             "curtain and loads the OLD SAVE under the open NEW GAME screen. The cancel is " +
                             "the host's own veto, not a wait on anybody (P13): it clears the host's READY, " +
                             "which is what holds the gate shut until the host comes back.";
        }
    }
}
