using System;
using Multiplayer.Network;
using Multiplayer.Transport;
using UnityEngine;
using Xunit;

// Bridge.Tests references the compiled mod DLL, so NetworkEngine + its real transports are
// available. These tests exercise the pure lifecycle paths only (DirectTransport never opens a
// socket unless Host/Connect is called), so no game/Unity runtime is required.
public class NetworkEngineTearDownTests
{
    // Headless neutralization (same approach SimCluster uses): the engine's Initialize() calls
    // UnityEngine.Debug.Log, whose default handler P/Invokes into the absent Unity native runtime
    // and throws SecurityException ("ECall methods must be packaged in a system module") headless.
    // Route Debug.* through a managed no-op handler ONCE so these lifecycle tests never enter it.
    static NetworkEngineTearDownTests()
    {
        Debug.unityLogger.logHandler = new NoopLogHandler();
    }

    private sealed class NoopLogHandler : ILogHandler
    {
        public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args) { }
        public void LogException(Exception exception, UnityEngine.Object context) { }
    }

    [Fact]
    public void TearDown_NullsInstance_AndClearsActive()
    {
        NetworkEngine.Create();
        var engine = NetworkEngine.Instance;
        Assert.NotNull(engine);
        engine.Initialize(TransportType.DirectIP);
        Assert.True(engine.IsActive);

        engine.TearDown();

        Assert.False(engine.IsActive);
        Assert.Null(NetworkEngine.Instance);     // singleton dropped → next Create() is fresh
    }

    [Fact]
    public void TearDown_FromHost_ClearsHostFlag()
    {
        NetworkEngine.Create();
        var engine = NetworkEngine.Instance;
        engine.Initialize(TransportType.DirectIP);
        engine.StartHost(0);
        Assert.True(engine.IsHost);

        engine.TearDown();

        Assert.False(engine.IsHost);
        Assert.Null(NetworkEngine.Instance);
    }

    [Fact]
    public void TearDown_IsIdempotent_DoubleCallSafe()
    {
        NetworkEngine.Create();
        var engine = NetworkEngine.Instance;
        engine.Initialize(TransportType.DirectIP);

        engine.TearDown();
        // Second call on the now-idle, de-registered engine must not throw.
        engine.TearDown();

        Assert.False(engine.IsActive);
        Assert.Null(NetworkEngine.Instance);
    }

    [Fact]
    public void TearDown_OnFreshUninitializedEngine_IsSafe()
    {
        NetworkEngine.Create();
        var engine = NetworkEngine.Instance;
        // never Initialize: IsActive is false.
        engine.TearDown();
        Assert.False(engine.IsActive);
        Assert.Null(NetworkEngine.Instance);
    }
}
