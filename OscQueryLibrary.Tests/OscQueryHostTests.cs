using System.Net;
using MeaMod.DNS.Multicast;
using OscQueryLibrary.Models;

namespace OscQueryLibrary.Tests;

/// <summary>
/// Runs a real HTTP server on loopback. mDNS stays off, the multicast service is never started.
/// </summary>
public sealed class OscQueryHostTests : IDisposable
{
    private static readonly HttpClient Http = new();
    private readonly MulticastService _mdns = OscQueryDiscovery.CreateMulticastService();
    private readonly OscQueryHttpClient _client = new();
    private OscQueryHost? _host;

    private static OscQueryNode CreateRoot() => new()
    {
        FullPath = "/",
        Contents = new Dictionary<string, OscQueryNode>
        {
            ["avatar"] = new()
            {
                FullPath = "/avatar",
                Contents = new Dictionary<string, OscQueryNode>
                {
                    ["change"] = new() { FullPath = "/avatar/change", Access = AccessValues.Write, Type = "s" },
                    ["my param"] = new() { FullPath = "/avatar/my param", Access = AccessValues.ReadWrite, Type = "f" }
                }
            }
        }
    };

    private async Task<IPEndPoint> StartHost(ushort oscPort = 9123, OscQueryNode? root = null)
    {
        _host = new OscQueryHost(new OscQueryHostOptions
        {
            ServiceName = "Test",
            Address = IPAddress.Loopback,
            HttpBind = "127.0.0.1",
            OscPort = oscPort,
            Root = root ?? CreateRoot()
        }, _mdns);
        _host.Start();

        // EmbedIO starts listening in the background
        var endPoint = new IPEndPoint(IPAddress.Loopback, _host.HttpPort);
        for (var attempt = 0;; attempt++)
        {
            try
            {
                await _client.GetHostInfoAsync(endPoint);
                return endPoint;
            }
            catch (HttpRequestException) when (attempt < 50)
            {
                await Task.Delay(100);
            }
        }
    }

    [Test]
    public async Task ServesHostInfo()
    {
        var endPoint = await StartHost();

        var hostInfo = await _client.GetHostInfoAsync(endPoint);

        await Assert.That(hostInfo.Name).IsEqualTo("Test");
        await Assert.That(hostInfo.OscIp).IsEqualTo(IPAddress.Loopback);
        await Assert.That(hostInfo.OscPort).IsEqualTo((ushort)9123);
        await Assert.That(hostInfo.OscTransport).IsEqualTo(OscTransport.UDP);
        await Assert.That(hostInfo.Extensions!["ACCESS"]).IsTrue();
    }

    [Test]
    public async Task PicksFreePorts()
    {
        using var host = new OscQueryHost(new OscQueryHostOptions { ServiceName = "Test", Address = IPAddress.Loopback }, _mdns);

        await Assert.That(host.HttpPort).IsNotEqualTo((ushort)0);
        await Assert.That(host.OscPort).IsNotEqualTo((ushort)0);
        await Assert.That(host.HostInfo.OscPort).IsEqualTo(host.OscPort);
    }

    [Test]
    public async Task ServesTreeAndSubtree()
    {
        var endPoint = await StartHost();

        var root = await _client.GetNodeAsync(endPoint);
        var avatar = await _client.GetNodeAsync(endPoint, "/avatar");

        await Assert.That(root.Find("/avatar/change")?.Access).IsEqualTo(AccessValues.Write);
        await Assert.That(avatar.FullPath).IsEqualTo("/avatar");
        await Assert.That(avatar.Contents!.Keys.ToArray()).IsEquivalentTo(["change", "my param"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ServesEscapedPath()
    {
        var endPoint = await StartHost();

        var node = await _client.GetNodeAsync(endPoint, "/avatar/my param");

        await Assert.That(node.Type).IsEqualTo("f");
    }

    [Test]
    public async Task UnknownPath_IsNotFound()
    {
        var endPoint = await StartHost();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => _client.GetNodeAsync(endPoint, "/nope"));

        await Assert.That(ex!.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task ServesSingleAttribute()
    {
        var endPoint = await StartHost();

        var type = await Http.GetStringAsync($"http://{endPoint}/avatar/change?TYPE");
        using var missing = await Http.GetAsync($"http://{endPoint}/avatar/change?RANGE");

        await Assert.That(type).IsEqualTo("""{"TYPE":"s"}""");
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task ReplacedRoot_IsServed()
    {
        var endPoint = await StartHost();

        _host!.Root = new OscQueryNode { FullPath = "/", Contents = new() { ["new"] = new OscQueryNode { FullPath = "/new", Type = "i" } } };
        var root = await _client.GetNodeAsync(endPoint);

        await Assert.That(root.Find("/new")?.Type).IsEqualTo("i");
        await Assert.That(root.Find("/avatar")).IsNull();
    }

    public void Dispose()
    {
        _host?.Dispose();
        _mdns.Dispose();
    }
}
