using Multipleer.Network.MessageLayer;
using UnityEngine;

namespace Multipleer.Network.CommandSync
{
    // Pipeline orchestrator. Built ONCE; wires HostArbiter + ClientApplier to the engine's existing
    // campaign-action events. Owns the InterceptRegistry dispatch and the re-entrancy guard that lets
    // the host/clients execute a real game method WITHOUT the Harmony prefix re-encoding it.
    //
    // THREE events are wired (the rejected channel was split out in the Task-5 fix so the originating
    // client never re-applies an action the host refused):
    //   * OnCampaignActionRequest      (host) -> HostArbiter.HandleRequest   (validate/execute/broadcast)
    //   * OnHostCampaignActionResult   (client, 0x31) -> ClientApplier.HandleResult   (apply approved)
    //   * OnHostCampaignActionRejected (client, 0x32) -> ClientApplier.HandleRejected (log only, NO apply)
    public sealed class CommandRelay
    {
        public static CommandRelay Instance { get; private set; }

        private readonly NetworkEngine _engine;
        private readonly HostArbiter _hostArbiter;
        private readonly ClientApplier _clientApplier;

        // Per-action re-entrancy guard: set while ApplyResult runs the real method so the intercept
        // prefix sees "already relayed" and returns true (execute) instead of re-sending, and the
        // postfix skips the host broadcast (the relayed apply is not a fresh host-origin action).
        [System.ThreadStatic] private static bool _applying;
        public static bool IsApplying => _applying;

        private CommandRelay(NetworkEngine engine)
        {
            _engine = engine;
            _hostArbiter = new HostArbiter(engine, this);
            _clientApplier = new ClientApplier(engine, this);
        }

        // Call once after the session/NetworkEngine exists (same place the lobby wires up the engine).
        // Idempotent: re-wiring detaches the previous instance's handlers first so a host/join/leave
        // cycle never stacks duplicate subscriptions on the singleton engine.
        public static void Wire(NetworkEngine engine)
        {
            if (engine == null) return;
            if (Instance != null) Instance.Unwire();
            Instance = new CommandRelay(engine);
            engine.OnCampaignActionRequest += Instance._hostArbiter.HandleRequest;
            engine.OnHostCampaignActionResult += Instance._clientApplier.HandleResult;
            engine.OnHostCampaignActionRejected += Instance._clientApplier.HandleRejected;
        }

        private void Unwire()
        {
            _engine.OnCampaignActionRequest -= _hostArbiter.HandleRequest;
            _engine.OnHostCampaignActionResult -= _clientApplier.HandleResult;
            _engine.OnHostCampaignActionRejected -= _clientApplier.HandleRejected;
        }

        // Client side: the Harmony prefix has already built the envelope+payload; send to host and
        // the prefix returns false to block local execution.
        public void RelayFromClient(CampaignActionMessage action)
        {
            _engine.SendCampaignAction(action);
        }

        // Host + clients: reproduce an authorized action by invoking the registered real game method
        // under the guard. Looks up the InterceptEntry; skips unconfirmed-signature rows safely.
        public void ApplyResult(CampaignActionMessage action)
        {
            var entry = InterceptRegistry.Lookup(action.ActionType);
            if (entry == null || !entry.SignatureConfirmed)
            {
                Debug.LogWarning($"[Multipleer] No confirmed intercept for {action.ActionType}; skipping apply.");
                return;
            }

            _applying = true;
            try
            {
                CommandExecutor.Execute(entry, action);
            }
            finally
            {
                _applying = false;
            }
        }
    }
}
