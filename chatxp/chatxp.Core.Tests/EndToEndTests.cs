using System.Diagnostics;
using chatxp.core;
using Xunit;

namespace chatxp.Core.Tests;

/// <summary>
/// Full-stack integration test: the REAL Node.js signaling server + two WebRtcManager
/// instances performing the complete WebRTC handshake (offer/answer/ICE) and exchanging
/// an E2EE-encrypted message over the data channel — no UI involved.
///
/// Proves: SignalingClient ↔ server/index.js ∧ SIPSorcery peer connection ∧ Event
/// JSON wire compatibility ∧ E2eeCrypto round-trip — the whole core stack.
/// </summary>
public class EndToEndTests : IAsyncLifetime
{
    private const int TestPort = 3998;
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
            Arguments = $"index.js {TestPort} --log",
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

    public async Task DisposeAsync()
    {
        if (_serverProcess is { } p && !p.HasExited)
            p.Kill(entireProcessTree: true);
        if (_serverProcess is { } proc)
        {
            proc.WaitForExit(5000);
            proc.Dispose();
        }
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

    [Fact]
    public async Task Two_Peers_Connect_And_Exchange_Encrypted_Message()
    {
        var server = $"ws://127.0.0.1:{TestPort}/";

        // Two users, each with their own E2EE identity key.
        var aliceCrypto = E2eeCrypto.Create();
        var bobCrypto = E2eeCrypto.Create();
        string aliceId = aliceCrypto.Base64PublicKey;
        string bobId = bobCrypto.Base64PublicKey;

        var alice = new WebRtcManager(aliceId, new SignalingClient());
        var bob = new WebRtcManager(bobId, new SignalingClient());
        alice.SetSignalingServer(server);
        bob.SetSignalingServer(server);

        var bobInbox = new System.Collections.Concurrent.ConcurrentQueue<Event>();
        bob.OnMessage += evt => bobInbox.Enqueue(evt);

        var incomingOffer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        bob.OnIncomingConnection += (fromId, sdp) => incomingOffer.TrySetResult(fromId);

        try
        {
            await alice.RegisterAsync();
            await bob.RegisterAsync();

            // Alice initiates the P2P connection to Bob.
            await alice.ConnectToAsync(bobId);

            // Bob accepts the offer (the app layer auto-accepts here).
            var from = await incomingOffer.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(aliceId, from);
            await bob.AcceptIncomingAsync(from);

            // Both sides reach a live P2P data channel.
            await WaitUntil(() => alice.IsPeerConnected, TimeSpan.FromSeconds(20), "Alice connected");
            await WaitUntil(() => bob.IsPeerConnected, TimeSpan.FromSeconds(20), "Bob connected");
            await WaitUntil(() => alice.IsReadyToSend, TimeSpan.FromSeconds(20), "Alice ready");
            await WaitUntil(() => bob.IsReadyToSend, TimeSpan.FromSeconds(20), "Bob ready");

            // Alice sends an E2EE-encrypted message to Bob (exact app flow:
            // encryptP2PMessage with the peer's public key, wrapped in a send Event).
            const string secret = "hello from dotnet over webrtc";
            var ciphertext = aliceCrypto.Encrypt(secret, bobId);
            Assert.NotNull(ciphertext);
            alice.Send(Event.SendEvent(ciphertext!));

            Event? received = null;
            var deadline = Task.Delay(TimeSpan.FromSeconds(10));
            while (received is null)
            {
                if (bobInbox.TryDequeue(out var evt))
                {
                    received = evt;
                    break;
                }
                if (deadline.IsCompleted)
                    Assert.Fail("Bob never received the Event");
                await Task.Delay(50);
            }

            Assert.Equal(EventTypes.Send, received!.Type);
            Assert.NotNull(ciphertext);

            // Bob decrypts with Alice's public key — the full E2EE loop.
            var plaintext = bobCrypto.Decrypt(received.Payload, aliceId);
            Assert.Equal(secret, plaintext);

            // The signaling socket must have been dropped once P2P is live (mirrors Swift).
            await WaitUntil(() => !alice.SignalingConnected, TimeSpan.FromSeconds(5),
                "Alice signaling socket closed");
        }
        finally
        {
            await alice.DisposeAsync();
            await bob.DisposeAsync();
        }
    }
}
