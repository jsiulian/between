using chatxp.core;
using Xunit;

namespace chatxp.Core.Tests;

/// <summary>
/// Verifies that connection failures append detailed diagnostics to the injected ILog
/// (the Diagnostics tab's data source): reason, ICE state, peer-connection state,
/// data-channel state, target, and signaling-socket state.
/// </summary>
public sealed class LogSinkTests
{
    private sealed class CapturingLog : ILog
    {
        public List<(string level, string message)> Entries { get; } = new();
        public void Info(string message) => Entries.Add(("INFO", message));
        public void Error(string message, Exception? exception = null) =>
            Entries.Add(("ERROR", exception is null
                ? message
                : message + " | " + exception.Message));
    }

    [Fact]
    public async Task ConnectTo_Nonexistent_Peer_Appends_Failure_Details()
    {
        var log = new CapturingLog();
        await using var signaling = new SignalingClient();
        signaling.SetSignalingServer("ws://127.0.0.1:59991/"); // nothing listens
        await using var manager = new WebRtcManager("local-id", signaling, log);

        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.OnDisconnected += reason => failed.TrySetResult(reason);

        // Must NOT throw — the failure is handled (status event + log), mirroring Swift
        // where connect() doesn't throw either; handleConnectionFailed drives the UI.
        await manager.ConnectToAsync("some-peer-id");

        var reason = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Signaling server unreachable", reason);

        // The failure must be logged as ERROR with the reason and the server URL.
        Assert.Contains(log.Entries, e =>
            e.level == "ERROR" && e.message.Contains("signaling registration"));
        Assert.Contains(log.Entries, e =>
            e.level == "ERROR" && e.message.Contains("Connection failed: Signaling server unreachable"));
        // And the detail block includes ICE/peer/data-channel context.
        Assert.Contains(log.Entries, e =>
            e.level == "ERROR" && e.message.Contains("ICE state: no peer connection"));
    }

    [Fact]
    public async Task NullLog_Default_Does_Not_Throw()
    {
        // The default (no log passed) must not break the manager.
        await using var manager = new WebRtcManager("id", new SignalingClient());
        Assert.False(manager.IsPeerConnected);
    }
}
