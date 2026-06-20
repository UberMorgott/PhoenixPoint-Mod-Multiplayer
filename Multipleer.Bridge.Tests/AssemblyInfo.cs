using Xunit;

// These tests exercise the REAL engine over an in-memory bus and share process-GLOBAL mutable state
// across test classes: the static apply-context BridgeContext.CurrentPeer (read by CounterAction.Apply
// mid-pump), the static PermissionManager registry, the static SyncedActionRegistry, the NetworkEngine
// singleton (NetworkEngineTearDownTests), and the global UnityEngine.Debug log handler. xUnit runs
// distinct test classes (collections) in PARALLEL by default, so those globals get clobbered across
// threads → BridgeScenarioTests passed in isolation but flaked (~3/6) in the full run. Disabling
// assembly-level parallelization serializes every test; combined with per-test reset of the leaked
// holders this makes the suite deterministic. Test-only; no production behavior change.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
