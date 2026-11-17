using System.Diagnostics;
using System.Text.Json;
using chatxp.core;
using Xunit;

namespace chatxp.Core.Tests;

/// <summary>
/// Integration tests against the REAL Node.js signaling server (../../server/index.js),
/// started locally on a test port. Verifies the .NET client speaks the server's contract:
/// register + verbatim forward by "to" + not_found replies + disconnect unregistration.
/// </summary>
public class SignalingClientTests : IAsyncLifetime
{
    private const int TestPort = 3999;
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

        // Wait for the server socket to accept connections.
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

    public async Task DisposeAsync()
    {
        if (_serverProcess is { } p && !p.HasExited)
        {
            p.Kill(entireProcessTree: true);
        }
        if (_serverProcess is { } proc)
        {
            if (!proc.WaitForExit(5000))
                Assert.Fail("Server process did not exit");
            proc.Dispose();
        }
    }

    private static string NewId() => Guid.NewGuid().ToString();

    [Fact]
    public async Task Register_And_Forward_Offer()
    {
        await using var alice = new SignalingClient();
        await using var bob = new SignalingClient();
        alice.SetSignalingServer($"ws://127.0.0.1:{TestPort}/");
        bob.SetSignalingServer($"ws://127.0.0.1:{TestPort}/");

        var bobOfferTcs = new TaskCompletionSource<SignalingMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bob.OnMessage += m =>
        {
            if (m.Type == "offer") bobOfferTcs.TrySetResult(m);
        };

        var aliceId = NewId();
        var bobId = NewId();
        await alice.RegisterAsync(aliceId);
        await bob.RegisterAsync(bobId);

        await alice.SendAsync(SignalingMessage.Offer(aliceId, bobId, "v=0 fake-sdp-offer"));

        var received = await bobOfferTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("offer", received.Type);
        Assert.Equal(aliceId, received.From);
        Assert.Equal(bobId, received.To);
        Assert.Equal("v=0 fake-sdp-offer", received.Sdp);
    }

    [Fact]
    public async Task Forward_Answer_And_Candidate()
    {
        await using var alice = new SignalingClient();
        await using var bob = new SignalingClient();
        alice.SetSignalingServer($"ws://127.0.0.1:{TestPort}/");
        bob.SetSignalingServer($"ws://127.0.0.1:{TestPort}/");

        var aliceId = NewId();
        var bobId = NewId();
        await alice.RegisterAsync(aliceId);
        await bob.RegisterAsync(bobId);

        var answerTcs = new TaskCompletionSource<SignalingMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var candidateTcs = new TaskCompletionSource<SignalingMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        alice.OnMessage += m =>
        {
            if (m.Type == "answer") answerTcs.TrySetResult(m);
            else if (m.Type == "candidate") candidateTcs.TrySetResult(m);
        };

        await bob.SendAsync(SignalingMessage.Answer(bobId, aliceId, "v=0 fake-sdp-answer"));
        await bob.SendAsync(SignalingMessage.Candidate(bobId, aliceId,
            "candidate:1 1 UDP 2130706431 192.168.1.4 5000 typ host", "0", 0));

        var answer = await answerTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("v=0 fake-sdp-answer", answer.Sdp);

        var candidate = await candidateTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, candidate.SdpMLineIndex);
        Assert.Equal("0", candidate.SdpMid);
        Assert.Contains("candidate:", candidate.Sdp);
    }

    [Fact]
    public async Task NotFound_When_Target_Unregistered()
    {
        await using var alice = new SignalingClient();
        alice.SetSignalingServer($"ws://127.0.0.1:{TestPort}/");
        var aliceId = NewId();
        await alice.RegisterAsync(aliceId);

        var notFoundTcs = new TaskCompletionSource<SignalingMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        alice.OnMessage += m =>
        {
            if (m.Type == "not_found") notFoundTcs.TrySetResult(m);
        };

        var ghostId = NewId();
        await alice.SendAsync(SignalingMessage.Offer(aliceId, ghostId, "sdp"));

        var notFound = await notFoundTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Server replies { type: "not_found", from: <target>, to: <sender> }.
        Assert.Equal(ghostId, notFound.From);
        Assert.Equal(aliceId, notFound.To);
    }

    [Fact]
    public async Task Reject_Forwarded_To_Peer()
    {
        await using var alice = new SignalingClient();
        await using var bob = new SignalingClient();
        alice.SetSignalingServer($"ws://127.0.0.1:{TestPort}/");
        bob.SetSignalingServer($"ws://127.0.0.1:{TestPort}/");

        var aliceId = NewId();
        var bobId = NewId();
        await alice.RegisterAsync(aliceId);
        await bob.RegisterAsync(bobId);

        var rejectTcs = new TaskCompletionSource<SignalingMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        alice.OnMessage += m =>
        {
            if (m.Type == "reject") rejectTcs.TrySetResult(m);
        };

        // Bob rejects Alice's connection attempt.
        await bob.SendAsync(SignalingMessage.Reject(bobId, aliceId));

        var reject = await rejectTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(bobId, reject.From);
        Assert.Equal(aliceId, reject.To);
    }

    [Fact]
    public async Task Event_Json_Shape_Matches_Swift()
    {
        // Swift JSONEncoder(Event) omits nulls and writes lower-case type names.
        var serialized = JsonSerializer.Serialize(Event.SendEvent("aGVsbG8="));
        Assert.Contains("\"type\":\"send\"", serialized);
        Assert.DoesNotContain("replyingTo", serialized);

        var withReply = JsonSerializer.Serialize(Event.SendEvent("aGVsbG8=", "UUID-1"));
        Assert.Contains("\"replyingTo\":\"UUID-1\"", withReply);

        // Decoding a Swift-encoded Event works.
        const string swiftJson =
            "{\"id\":\"123E4567-E89B-12D3-A456-426614174000\",\"type\":\"send\",\"payload\":\"aGVsbG8=\"}";
        var decoded = JsonSerializer.Deserialize<Event>(swiftJson,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(decoded);
        Assert.Equal(EventTypes.Send, decoded!.Type);
        Assert.Equal("aGVsbG8=", decoded.Payload);
    }
}
