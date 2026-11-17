using chatxp.core;
using SIPSorcery.Net;
using Xunit;

namespace chatxp.Core.Tests;

public class IceConfigurationTests
{
    [Fact]
    public void TailnetInterfaces_AreIncludedByDefault()
    {
        var config = WebRtcManager.CreateIceConfiguration();

        Assert.True(config.X_ICEIncludeAllInterfaceAddresses);
        Assert.Contains(config.iceServers, server =>
            server.urls.StartsWith("stun:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppConfig_GathersHostAddressesBeyondDefaultRouteInterface()
    {
        var allInterfaces = await GatherHostAddressesAsync(includeAll: true);
        var defaultRouteOnly = await GatherHostAddressesAsync(includeAll: false);

        Assert.NotEmpty(allInterfaces);
        Assert.Empty(defaultRouteOnly.Except(allInterfaces));
    }

    private static async Task<HashSet<string>> GatherHostAddressesAsync(bool includeAll)
    {
        var config = WebRtcManager.CreateIceConfiguration();
        config.X_ICEIncludeAllInterfaceAddresses = includeAll;

        var pc = new RTCPeerConnection(config);
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            pc.onicecandidate += candidate =>
            {
                if (candidate is null ||
                    !candidate.candidate.Contains(" typ host ", StringComparison.Ordinal))
                    return;

                var fields = candidate.candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length > 4)
                    addresses.Add(fields[4]);
            };

            await pc.createDataChannel("test", new RTCDataChannelInit { ordered = true });
            await pc.setLocalDescription(pc.createOffer(null));
            await Task.Delay(1500);
        }
        finally
        {
            pc.Close("test complete");
        }

        return addresses;
    }
}
