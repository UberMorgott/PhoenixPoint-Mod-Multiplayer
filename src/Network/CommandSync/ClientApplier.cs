using System;
using Multipleer.Network.MessageLayer;
using UnityEngine;

namespace Multipleer.Network.CommandSync
{
    // Client-only applier. Two SEPARATE channels (split in NetworkEngine.RouteMessage):
    //   * OnHostCampaignActionResult   (0x31 Approved) -> HandleResult -> apply the real method.
    //   * OnHostCampaignActionRejected (0x32 Rejected) -> HandleRejected -> NEVER applies (log only).
    // An APPROVED action is reproduced locally by executing the real game method under CommandRelay's
    // guard (so the Harmony prefix does not re-send it). A REJECTED action is NOT applied — the host
    // refused it and the client already blocked its own local exec via the Prefix, so applying it now
    // would invert the host's decision. Rejection feedback (user-visible toast) is an explicit
    // Stage-1+ follow-up tracked in the design doc; a log line is enough for Stage 1.
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

        // Rejected path (0x32). Host refused this action; the client never applies it (its local
        // execution was already blocked by the Prefix). Log only — no ApplyResult.
        public void HandleRejected(CampaignActionMessage action)
        {
            if (_engine.IsHost) return;
            Debug.Log($"[Multipleer] action rejected by host: {action.ActionType} (no local apply)");
        }
    }
}
