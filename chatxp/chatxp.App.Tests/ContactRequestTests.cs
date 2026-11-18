using System.Diagnostics;
using chatxp.core;
using chatxp.App.Services;
using Xunit;

namespace chatxp.App.Tests;

/// <summary>
/// FR-7.3 / FR-7.4 / FR-7.2a: known contacts auto-accept; unknown peers raise a
/// request that requires an explicit decision; per-contact server routing.
/// Verified against the real Node.js signaling server. Sessions run with
/// CHATXP_DATA_DIR pointed at a fresh temp dir so the REAL user data (contacts,
/// identity, settings) is never touched — no more "alice-friend" in your app.
/// </summary>
[Collection("app-session")]
public class ContactRequestTests : IAsyncLifetime
{
    private const int TestPort = 3995;
    private Process? _serverProcess;
    private string? _dataDir;

    public async Task InitializeAsync()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"chatxp-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("CHATXP_DATA_DIR", _dataDir);
        Environment.SetEnvironmentVariable("CHATXP_SIGNAL_SERVER", null);
        var serverDir = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "server"));
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
        Environment.SetEnvironmentVariable("CHATXP_DATA_DIR", null);
        try
        {
            if (_dataDir is not null && Directory.Exists(_dataDir))
                Directory.Delete(_dataDir, recursive: true);
        }
        catch { /* best-effort cleanup */ }
        return Task.CompletedTask;
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout, string what)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!condition())
        {
            await Task.Delay(100, cts.Token);
            if (cts.Token.IsCancellationRequested)
                Assert.Fail($"Timed out waiting for {what}");
        }
    }

    // NOTE: ChatSessionService uses a fixed app-data dir, so both sessions share the
    // same identity/DB. That is fine here: "alice" and "bob" are distinct peer keys
    // relative to what's in the contacts table.

    [Fact]
    public async Task PerContact_Server_Is_Routed_On_Connect_And_Stored_On_Accept()
    {
        // FR-7.2a: connecting to a contact with a stored server routes via that server.
        using var session = new ChatSessionService($"ws://127.0.0.1:{TestPort}/");

        var contactId = E2eeCrypto.Create().Base64PublicKey;
        session.AddContact("custom-server-friend", contactId,
            signalingServer: $"ws://127.0.0.1:{TestPort}/custom");

        Assert.Equal($"ws://127.0.0.1:{TestPort}/custom", session.ServerFor(contactId));

        // ConnectToContactAsync must switch the manager to the contact's server
        // before connecting (we assert the URL took effect; full connect needs the
        // peer online which is covered by the auto-accept test).
        var before = session.WebRtcServerUrl;
        _ = session.ConnectToContactAsync(contactId);
        await Task.Delay(300); // routing is synchronous before the connect fires
        Assert.Equal($"ws://127.0.0.1:{TestPort}/custom", session.WebRtcServerUrl);
        Assert.NotEqual(before, session.WebRtcServerUrl);

        session.DeleteContact(contactId);
        Assert.Null(session.ServerFor(contactId));
    }

    [Fact]
    public async Task Known_Contact_Incoming_Offer_Is_Auto_Accepted_And_Answered()
    {
        // Alice-side session (the acceptor).
        using var acceptor = new ChatSessionService($"ws://127.0.0.1:{TestPort}/");
        // A separate initiator uses its own manager (not a second service) so the
        // shared app-data doesn't cross wires.
        var initiatorCrypto = E2eeCrypto.Create();
        var initiator = new WebRtcManager(initiatorCrypto.Base64PublicKey, new SignalingClient());
        initiator.SetSignalingServer($"ws://127.0.0.1:{TestPort}/");

        try
        {
            // Simulate "this peer is already a contact": add the initiator to the
            // acceptor's contacts (nickname mandatory per FR-7.2).
            acceptor.AddContact("alice-friend", initiatorCrypto.Base64PublicKey);

            await initiator.RegisterAsync();
            await acceptor.WebRtc.RegisterAsync();

            var answerSeen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            initiator.OnConnectionStateChanged += s =>
            {
                if (s == "connected") answerSeen.TrySetResult(s);
            };

            // Initiator connects; acceptor must auto-accept WITHOUT any request raised.
            await initiator.ConnectToAsync(acceptor.Crypto.Base64PublicKey);

            await WaitUntil(() => answerSeen.Task.IsCompleted, TimeSpan.FromSeconds(20),
                "auto-accepted connection to reach connected");
            Assert.True(acceptor.WebRtc.IsPeerConnected, "acceptor P2P should be live");
            Assert.Null(acceptor.PendingIncomingContactId);
        }
        finally
        {
            await initiator.DisposeAsync();
        }
    }

    [Fact]
    public async Task Unknown_Peer_Raises_Request_That_Needs_A_Decision()
    {
        using var acceptor = new ChatSessionService($"ws://127.0.0.1:{TestPort}/");
        var initiatorCrypto = E2eeCrypto.Create();
        var initiator = new WebRtcManager(initiatorCrypto.Base64PublicKey, new SignalingClient());
        initiator.SetSignalingServer($"ws://127.0.0.1:{TestPort}/");

        try
        {
            var requestSeen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            acceptor.ContactRequestReceived += id => requestSeen.TrySetResult(id);

            await initiator.RegisterAsync();
            await acceptor.WebRtc.RegisterAsync();

            // NOT a contact: offer must raise a request, NOT auto-accept.
            await initiator.ConnectToAsync(acceptor.Crypto.Base64PublicKey);

            var requester = await requestSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(initiatorCrypto.Base64PublicKey, requester);
            Assert.Equal(initiatorCrypto.Base64PublicKey, acceptor.PendingIncomingContactId);
            // Not connected yet: the request is pending the user's decision.
            await Task.Delay(1500);
            Assert.False(initiator.IsPeerConnected, "unknown-peer offer must not be auto-accepted");
        }
        finally
        {
            await initiator.DisposeAsync();
        }
    }
}
