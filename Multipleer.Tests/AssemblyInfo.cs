using Xunit;

// Several test classes here open REAL loopback sockets (DirectTransport / CompositeTransport host
// binds): DirectTransportDisconnectTests, DirectTransportHostBindFailureTests, DirectTransportConnectTests.
// Each picks a free ephemeral port via a probe TcpListener (bind IPAddress.Loopback:0, read the port,
// close, then DirectTransport.Host binds IPAddress.Any:<port>). xUnit runs distinct test classes
// (collections) in PARALLEL by default, so between a probe's close and the real Host bind another
// parallel test can be handed the SAME ephemeral port by the OS → SocketException AddressAlreadyInUse
// (observed as "DirectTransport host bind failed ... AddressAlreadyInUse" / "child DirectIP failed to
// host"). In isolation each transport class passes 20/20; only the full parallel run flaked (~1 in
// several runs). Disabling assembly-level parallelization serializes every test class so two real-socket
// binds can never overlap, which (together with the per-test free ports) makes the suite deterministic.
// Test-only; mirrors Multipleer.Bridge.Tests\AssemblyInfo.cs. No production behavior change.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
