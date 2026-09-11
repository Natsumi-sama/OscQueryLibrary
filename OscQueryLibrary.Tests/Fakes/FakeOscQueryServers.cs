using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace OscQueryLibrary.Tests.Fakes;

/// <summary>
/// Answers OSCQuery HTTP requests in memory. Requests to endpoints that weren't added fail like a refused connection.
/// </summary>
internal sealed class FakeOscQueryServers : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, (string HostInfo, string? Root)> _servers = new();
    private readonly ConcurrentDictionary<string, int> _requests = new();

    /// <param name="hostInfoJson">Served for the HOST_INFO endpoint.</param>
    /// <param name="rootJson">Served for every path, null answers those with a 500.</param>
    /// <param name="endPoint"></param>
    public void Add(IPEndPoint endPoint, string hostInfoJson, string? rootJson = TestData.VrcRootJson) =>
        _servers[Key(endPoint.Address.ToString(), endPoint.Port)] = (hostInfoJson, rootJson);

    public int RequestCount(IPEndPoint endPoint) =>
        _requests.TryGetValue(Key(endPoint.Address.ToString(), endPoint.Port), out var count) ? count : 0;

    public OscQueryHttpClient CreateClient() => new(new HttpClient(this));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var key = Key(uri.Host, uri.Port);
        _requests.AddOrUpdate(key, 1, (_, count) => count + 1);

        if (!_servers.TryGetValue(key, out var server))
            throw new HttpRequestException($"Connection refused ({key})");

        var json = uri.Query == "?HOST_INFO" ? server.HostInfo : server.Root;
        return Task.FromResult(json == null
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    private static string Key(string host, int port) => $"{host}:{port}";
}
