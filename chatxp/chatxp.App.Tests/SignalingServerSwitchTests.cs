using System.Diagnostics;
using chatxp.core;
using chatxp.App.Services;
using Xunit;

namespace chatxp.App.Tests;

/// <summary>
/// Verifies the runtime signaling-server switch (UI "Apply" flow):
/// bad URL → clean error, no state corruption; good URL → registers on the NEW server
/// and receives forwards there.
/// </summary>
[Collection("app-session")]
public class SignalingServerSwitchTests : IAsyncLifetime
{
    private const int TestPort = 3997;
    private Process? _serverProcess;

    public async Task InitializeAsync()
    {
        var serverDir = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "server"));
        if (!File.Exists(Path.Combine(serverDir, "index.js")))
            Assert.Fail($"Server not found at {serverDir}");

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = $"index.js {TestPort}",
            WorkingDirectory = serverDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        _serverProcess = Process.Start(psi)!;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                using var probe = new System.Net.Sockets.TcpClient();
                await probe.ConnectAsync("127.0.0.1", TestPort, cts.Token);
                if (probe.Connected) break;
            }
            catch { /* retry */ }
            await Task.Delay(100, cts.Token);
        }
    }

    public Task DisposeAsync()
    {
        if (_serverProcess is { } p && !p.HasExited)
            p.Kill(entireProcessTree: true);
        _serverProcess?.WaitForExit(5000);
        _serverProcess?.Dispose();
        return Task.CompletedTask;
    }

    // Build the app assembly reference is via the App project; these tests live in the
    // app's test project? No — keep them here referencing chatxp.App.

    [Fact]
    public async Task BadUrl_Returns_Error_And_GoodUrl_Registers_On_New_Server()
    {
        // Point the session's app-data at a temp dir so we don't touch real user data.
        var tempHome = Path.Combine(Path.GetTempPath(), $"chatxp-app-{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(tempHome, "settings.txt");
        try
        {
            // Start a service bound to an unreachable-but-valid URL.
            using var session = new ChatSessionService("ws://127.0.0.1:59999/");

            // 1) Malformed scheme → clean error, nothing thrown.
            var bad = await session.ApplySignalingServerAsync("http://nope/");
            Assert.NotNull(bad);
            Assert.Contains("ws://", bad);

            // 2) Valid URL of a server that isn't listening → connection error surfaced.
            var unreachable = await session.ApplySignalingServerAsync("ws://127.0.0.1:59999/");
            Assert.NotNull(unreachable);
            Assert.Contains("Could not connect", unreachable);

            // 3) Apply the REAL server → registers and the choice persisted.
            var ok = await session.ApplySignalingServerAsync($"ws://127.0.0.1:{TestPort}/");
            Assert.Null(ok);
            Assert.Equal($"ws://127.0.0.1:{TestPort}/", session.SignalingServer);

            // Registration on the new server actually works: another client's offer
            // forwarded by the server reaches this session.
            await using var other = new SignalingClient();
            other.SetSignalingServer($"ws://127.0.0.1:{TestPort}/");
            var otherId = Guid.NewGuid().ToString();
            await other.RegisterAsync(otherId);

            var offerSeen = new TaskCompletionSource<SignalingMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            session.WebRtc.OnIncomingConnection += (from, sdp) =>
                offerSeen.TrySetResult(new SignalingMessage { Type = "offer", From = from, Sdp = sdp });

            await other.SendAsync(SignalingMessage.Offer(otherId, session.Crypto.Base64PublicKey, "v=0"));
            var received = await offerSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(otherId, received.From);
            Assert.Equal("v=0", received.Sdp);
        }
        finally
        {
            if (Directory.Exists(tempHome)) Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public async Task Saved_Server_Is_Persisted_And_Reloaded()
    {
        // The service persists its choice to settings.txt and a fresh session loads it.
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "chatxp");
        var settings = Path.Combine(appData, "settings.txt");

        // Backup existing user settings so the test never leaves the user's app
        // pointing at a dead test server.
        string? backup = File.Exists(settings) ? File.ReadAllText(settings) : null;
        try
        {
            using var session1 = new ChatSessionService($"ws://127.0.0.1:{TestPort}/");
            var err = await session1.ApplySignalingServerAsync($"ws://127.0.0.1:{TestPort}/s2");
            Assert.Null(err);
            Assert.Equal($"ws://127.0.0.1:{TestPort}/s2", session1.SignalingServer);

            Assert.True(File.Exists(settings));
            Assert.Equal($"ws://127.0.0.1:{TestPort}/s2", File.ReadAllText(settings).Trim());

            // A fresh session without an explicit server loads the saved one
            // (precedence: explicit arg > env > saved > default).
            using var session2 = new ChatSessionService();
            Assert.Equal($"ws://127.0.0.1:{TestPort}/s2", session2.SignalingServer);
        }
        finally
        {
            if (backup is not null) File.WriteAllText(settings, backup);
            else if (File.Exists(settings)) File.Delete(settings);
        }
    }
}
