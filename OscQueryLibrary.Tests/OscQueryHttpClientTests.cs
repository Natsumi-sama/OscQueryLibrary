using System.Net;
using System.Text;
using System.Text.Json;
using OscQueryLibrary.Tests.Fakes;

namespace OscQueryLibrary.Tests;

public class OscQueryHttpClientTests
{
    private static readonly IPEndPoint EndPoint = new(IPAddress.Loopback, 1234);

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Test]
    public async Task GetHostInfoAsync_RequestsHostInfo()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, TestData.HostInfoJson("Test", IPAddress.Loopback, 9000));
        var client = new OscQueryHttpClient(new HttpClient(handler));

        var hostInfo = await client.GetHostInfoAsync(EndPoint);

        await Assert.That(handler.Requests).HasSingleItem();
        await Assert.That(handler.Requests[0].ToString()).IsEqualTo("http://127.0.0.1:1234/?HOST_INFO");
        await Assert.That(hostInfo.Name).IsEqualTo("Test");
        await Assert.That(hostInfo.OscPort).IsEqualTo((ushort)9000);
    }

    [Test]
    [Arguments("/", "/")]
    [Arguments("", "/")]
    [Arguments("/avatar/parameters", "/avatar/parameters")]
    [Arguments("avatar//parameters/", "/avatar/parameters")]
    [Arguments("/avatar/parameters/My Param", "/avatar/parameters/My%20Param")]
    [Arguments("/a/#?", "/a/%23%3F")]
    public async Task GetNodeAsync_EscapesPath(string path, string expected)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"FULL_PATH":"/"}""");
        var client = new OscQueryHttpClient(new HttpClient(handler));

        await client.GetNodeAsync(EndPoint, path);

        await Assert.That(handler.Requests).HasSingleItem();
        await Assert.That(handler.Requests[0].PathAndQuery).IsEqualTo(expected);
    }

    [Test]
    public async Task GetNodeAsync_ParsesTree()
    {
        var client = new OscQueryHttpClient(new HttpClient(new RecordingHandler(HttpStatusCode.OK, TestData.VrcRootJson)));

        var root = await client.GetNodeAsync(EndPoint);

        await Assert.That(root.Find("/avatar/change")?.Type).IsEqualTo("s");
    }

    [Test]
    public async Task ErrorStatus_Throws()
    {
        var client = new OscQueryHttpClient(new HttpClient(new RecordingHandler(HttpStatusCode.NotFound, "")));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetNodeAsync(EndPoint, "/missing"));

        await Assert.That(ex!.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task NullBody_Throws()
    {
        var client = new OscQueryHttpClient(new HttpClient(new RecordingHandler(HttpStatusCode.OK, "null")));

        await Assert.ThrowsAsync<JsonException>(() => client.GetHostInfoAsync(EndPoint));
    }

    [Test]
    public async Task CreateHttpClient_SanitizesUserAgent()
    {
        using var client = OscQueryHttpClient.CreateHttpClient("OscQuery-My App/1");

        await Assert.That(client.DefaultRequestHeaders.UserAgent.ToString()).IsEqualTo("OscQuery-My-App-1/1.0");
        await Assert.That(client.DefaultRequestHeaders.Pragma.ToString()).IsEqualTo("no-cache");
        await Assert.That(client.Timeout).IsEqualTo(TimeSpan.FromSeconds(10));
    }
}
