using System;
using Multipleer.Network.MessageLayer;
using UnityEngine;

namespace Multipleer.Network.CommandSync
{
    // Client-only applier. Subscribed to NetworkEngine.OnHostCampaignActionResult (fired on 0x31
    // Approved AND 0x32 Rejected). For an APPROVED action it reproduces the result locally by
    // executing the real game method under CommandRelay's guard (so the Harmony prefix does not
    // re-send it). A REJECTED action carries no separate channel here (the envelope is identical) —
    // Stage 1 treats every OnHostCampaignActionResult as an apply; rejection feedback (toast) is a
    // Stage-1+ follow-up tracked in the design doc, NOT implemented here.
    public sealed class ClientApplier
    {
        private readonly NetworkEngine _engine;
        private readonly CommandRelay _relay;

        public ClientApplier(NetworkEngine engine, CommandRelay relay)
        {
            _engine = engine;
            _relay = relay;
        }

        public void HandleResult(CampaignActionMessage action)
        {
            if (_engine.IsHost) return; // host already applied in HostArbiter
            try
            {
                _relay.ApplyResult(action);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Multipleer] ClientApplier apply failed for {action.ActionType}: {ex}");
            }
        }
    }
}
