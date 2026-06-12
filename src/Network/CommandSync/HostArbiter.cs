using System;
using Multipleer.Network.MessageLayer;
using Multipleer.Network.CommandSync;
using UnityEngine;

namespace Multipleer.Network.CommandSync
{
    // Host-only arbiter. Subscribed to NetworkEngine.OnCampaignActionRequest (sender peerId + msg).
    // Flow: resolve sender->playerGUID via Session -> PermissionGate.IsAllowed -> on allow, execute
    // the REAL game method (CommandRelay.ApplyResult under the re-entrancy guard so the host's own
    // Harmony prefix does not re-encode it) and BROADCAST the approved action to all peers; on deny,
    // RejectCampaignAction back to the originator only.
    public sealed class HostArbiter
    {
        private readonly NetworkEngine _engine;
        private readonly CommandRelay _relay;

        public HostArbiter(NetworkEngine engine, CommandRelay relay)
        {
            _engine = engine;
            _relay = relay;
        }

        public void HandleRequest(ulong senderSteamId, CampaignActionMessage action)
        {
            if (!_engine.IsHost) return;

            var guid = ResolveGuid(senderSteamId);
            if (guid == Guid.Empty)
            {
                _engine.RejectCampaignAction(senderSteamId, action, "Unknown player identity");
                return;
            }

            if (!PermissionGate.IsAllowed(guid, action.ActionType))
            {
                var required = PermissionGate.RequiredPermission(action.ActionType);
                _engine.RejectCampaignAction(senderSteamId, action, $"Missing permission: {required}");
                return;
            }

            try
            {
                // Execute the real game method on the host (authoritative), guarded so the host-side
                // Harmony prefix treats this as an already-relayed apply and lets it run.
                _relay.ApplyResult(action);
                // Fan the approved action out to ALL peers (incl. originator, whose local exec was blocked).
                _engine.BroadcastCampaignActionResult(action);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Multipleer] HostArbiter execute failed for {action.ActionType}: {ex}");
                _engine.RejectCampaignAction(senderSteamId, action, "Host execution error");
            }
        }

        private Guid ResolveGuid(ulong senderSteamId)
        {
            var session = _engine.Session;
            if (session != null && session.Clients.TryGetValue(senderSteamId, out var client))
                return client.PlayerGuid;
            return Guid.Empty;
        }
    }
}
