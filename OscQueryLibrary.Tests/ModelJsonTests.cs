using System.Net;
using System.Text.Json;
using OscQueryLibrary.Models;
using OscQueryLibrary.Tests.Fakes;

namespace OscQueryLibrary.Tests;

public class ModelJsonTests
{
    private static readonly ModelsSourceGenerationContext Context = ModelsSourceGenerationContext.Default;

    [Test]
    public async Task HostInfo_Deserializes()
    {
        const string json = """
            {
              "NAME": "VRChat-Client-ABC",
              "OSC_IP": "192.168.1.5",
              "OSC_PORT": 9000,
              "OSC_TRANSPORT": "UDP",
              "EXTENSIONS": { "ACCESS": true, "VALUE": false },
              "VENDOR_THING": 42
            }
            """;

        var hostInfo = JsonSerializer.Deserialize(json, Context.OscQueryHostInfo)!;

        await Assert.That(hostInfo.Name).IsEqualTo("VRChat-Client-ABC");
        await Assert.That(hostInfo.OscIp).IsEqualTo(IPAddress.Parse("192.168.1.5"));
        await Assert.That(hostInfo.OscPort).IsEqualTo((ushort)9000);
        await Assert.That(hostInfo.OscTransport).IsEqualTo(OscTransport.UDP);
        await Assert.That(hostInfo.Extensions!["ACCESS"]).IsTrue();
        await Assert.That(hostInfo.Extensions["VALUE"]).IsFalse();
        await Assert.That(hostInfo.AdditionalProperties!["VENDOR_THING"].GetInt32()).IsEqualTo(42);
        await Assert.That(hostInfo.WsIp).IsNull();
    }

    [Test]
    public async Task HostInfo_Serializes_WithoutNulls()
    {
        var hostInfo = new OscQueryHostInfo { Name = "Test", OscIp = IPAddress.Loopback, OscPort = 9001, OscTransport = OscTransport.TCP };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(hostInfo, Context.OscQueryHostInfo));
        var root = document.RootElement;

        await Assert.That(root.GetProperty("OSC_IP").GetString()).IsEqualTo("127.0.0.1");
        await Assert.That(root.GetProperty("OSC_PORT").GetInt32()).IsEqualTo(9001);
        await Assert.That(root.GetProperty("OSC_TRANSPORT").GetString()).IsEqualTo("TCP");
        await Assert.That(root.TryGetProperty("WS_IP", out _)).IsFalse();
        await Assert.That(root.TryGetProperty("EXTENSIONS", out _)).IsFalse();
    }

    [Test]
    public void HostInfo_InvalidIp_Throws()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"OSC_IP":"not-an-ip"}""", Context.OscQueryHostInfo));
    }

    [Test]
    public async Task Node_Deserializes()
    {
        var root = JsonSerializer.Deserialize(TestData.VrcRootJson, Context.OscQueryNode)!;

        var emote = root.Find("/avatar/parameters/VRCEmote")!;
        await Assert.That(emote.Access).IsEqualTo(AccessValues.ReadWrite);
        await Assert.That(emote.Type).IsEqualTo("i");
        await Assert.That(emote.Value![0].GetInt32()).IsEqualTo(4);
        await Assert.That(root.Find("/avatar/parameters/NoValue")!.Access).IsEqualTo(AccessValues.Read);
        await Assert.That(root.Description).IsEqualTo("root node");
    }

    [Test]
    public async Task Node_KeepsUnknownAttributes()
    {
        const string json = """{"FULL_PATH":"/x","RANGE":[{"MIN":0,"MAX":1}],"X_VENDOR":"hi"}""";

        var node = JsonSerializer.Deserialize(json, Context.OscQueryNode)!;

        await Assert.That(node.Range!.Value[0].GetProperty("MAX").GetInt32()).IsEqualTo(1);
        await Assert.That(node.AdditionalProperties!["X_VENDOR"].GetString()).IsEqualTo("hi");
    }

    [Test]
    public async Task Node_Serializes_WithoutNulls()
    {
        var node = new OscQueryNode { FullPath = "/x", Access = AccessValues.Write, Type = "f" };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(node, Context.OscQueryNode));
        var properties = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();

        await Assert.That(properties).IsEquivalentTo(["FULL_PATH", "ACCESS", "TYPE"], CollectionOrdering.Matching);
        // VRChat expects ACCESS as a number
        await Assert.That(document.RootElement.GetProperty("ACCESS").GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task ServiceInfo_OscEndPoint_UsesHostInfo()
    {
        var service = CreateServiceInfo(new OscQueryHostInfo { OscIp = IPAddress.Parse("10.0.0.2"), OscPort = 9000 });

        await Assert.That(service.OscEndPoint).IsEqualTo(new IPEndPoint(IPAddress.Parse("10.0.0.2"), 9000));
    }

    [Test]
    public async Task ServiceInfo_OscEndPoint_FallsBackToHttpEndPoint()
    {
        var service = CreateServiceInfo(new OscQueryHostInfo());

        await Assert.That(service.OscEndPoint).IsEqualTo(service.HttpEndPoint);
    }

    private static OscQueryServiceInfo CreateServiceInfo(OscQueryHostInfo hostInfo) => new()
    {
        InstanceName = "Test",
        ServiceId = "test:1234",
        HttpEndPoint = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 1234),
        HostInfo = hostInfo,
        IsLocal = false,
        DiscoveredAt = DateTimeOffset.UtcNow
    };
}
