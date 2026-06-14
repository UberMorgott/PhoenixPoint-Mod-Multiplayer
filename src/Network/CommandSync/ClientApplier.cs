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
            // DIAG-A1 TEMP (strip after RCA) — boundary (d): client routing. isHost gate decides if we apply.
            Debug.Log($"[Multipleer] DIAG-A1 HandleResult action={action.ActionType} target={action.TargetId} isHost={_engine.IsHost}"); // DIAG-A1 TEMP (strip after RCA)
            if (_engine.IsHost)
            {
                Debug.Log("[Multipleer] DIAG-A1 HandleResult SKIP (isHost=true: host already applied in HostArbiter)"); // DIAG-A1 TEMP (strip after RCA)
                return; // host already applied in HostArbiter
            }

            // PIVOT Step A: RE-ENABLE the host->client StartTravel command-REPLAY. The client now replays an
            // approved StartTravel (CommandExecutor.ApplyStartTravel) so its OWN native NavigateRoutine drives
            // the craft's motion off the host-synced Timing clock (deterministic parametric playback:
            // pos=Slerp(start,end,(Now-startTime)/total)). The old transform-stream (0x35 per-tick pos/rot)
            // is being retired (see USE_TRANSFORM_STREAM in NetworkEngine) — command replication + the synced
            // clock are the new motion source. Arrival stays host-authoritative: GeoVehicle.OnArrived remains
            // suppressed on the client (ClientTravelEmitterSuppressPatch), so the client NavigateRoutine renders
            // motion + travel-line/anim but never authors site exploration / mission spawn. SetTimeState still
            // replays too. ApplyStartTravel runs under CommandRelay.IsApplying (set by _relay.ApplyResult).

            try
            {
                // DIAG-A1 TEMP (strip after RCA) — boundary (d): about to dispatch into CommandRelay.ApplyResult
                // -> CommandExecutor.Execute -> ApplyStartTravel. If we reach here the client WILL try to apply.
                Debug.Log($"[Multipleer] DIAG-A1 HandleResult -> _relay.ApplyResult action={action.ActionType}"); // DIAG-A1 TEMP (strip after RCA)
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
