using System.Diagnostics;
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
    public async Task AcceptIncoming_Timeout_Appends_Failure_Details()
    {
        // Regression for the bug: accepting an offer whose peer then never completes
        // ICE (e.g. it died) used to hang silently with NOTHING in the diagnostics log.
        var log = new CapturingLog();

        // Start the REAL Node.js signaling server on a scratch port (same approach as
        // SignalingClientTests) so the offer can actually be forwarded to the manager.
        const int testPort = 3996;
        var serverDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "server"));
        using var server = Process.Start(new ProcessStartInfo
        {
            FileName = "node",
            Arguments = $"index.js {testPort}",
            WorkingDirectory = serverDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        try
        {
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!ready.Token.IsCancellationRequested)
            {
                try
                {
                    using var probe = new System.Net.Sockets.TcpClient();
                    await probe.ConnectAsync("127.0.0.1", testPort, ready.Token);
                    if (probe.Connected) break;
                }
                catch { /* retry */ }
                await Task.Delay(100, ready.Token);
            }

            await using var manager = new WebRtcManager("local-id", new SignalingClient(), log);
            manager.SetSignalingServer($"ws://127.0.0.1:{testPort}/");
            await manager.RegisterAsync();

            // "carol" sends an offer and immediately vanishes — the manager accepts,
            // the 15s timeout fires, and the failure must appear in the log.
            await using var carol = new SignalingClient();
            carol.SetSignalingServer($"ws://127.0.0.1:{testPort}/");
            await carol.RegisterAsync("carol-id");
            await carol.SendAsync(SignalingMessage.Offer("carol-id", "local-id", "v=0 fake"));
            await carol.CloseSocketAsync();

            var incoming = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.OnIncomingConnection += (from, sdp) => incoming.TrySetResult(from);
            await incoming.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.OnDisconnected += reason => failed.TrySetResult(reason);

            await manager.AcceptIncomingAsync("carol-id");

            var reason = await failed.Task.WaitAsync(TimeSpan.FromSeconds(25));

            // Snapshot while holding the harness lock: the manager's callbacks can
            // still be appending from background threads (ICE timers don't stop on
            // failure). Asserting on a copy avoids the enumeration race.
            List<(string level, string message)> snapshot;
            lock (log)
            {
                snapshot = log.Entries.ToList();
            }

            Assert.Contains(snapshot, e =>
                e.level == "ERROR" && e.message.Contains("Connection failed:"));
            Assert.Contains(snapshot, e =>
                e.level == "INFO" && e.message.Contains("Accepting incoming connection"));
        }
        finally
        {
            if (!server.HasExited)
                server.Kill(entireProcessTree: true);
            server.WaitForExit(5000);
        }
    }

    [Fact]
    public async Task NullLog_Default_Does_Not_Throw()
    {
        // The default (no log passed) must not break the manager.
        await using var manager = new WebRtcManager("id", new SignalingClient());
        Assert.False(manager.IsPeerConnected);
    }
}
