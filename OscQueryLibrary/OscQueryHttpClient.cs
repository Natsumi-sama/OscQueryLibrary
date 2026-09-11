using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using OscQueryLibrary.Models;

namespace OscQueryLibrary;

/// <summary>
/// Reads HOST_INFO and the address space from OSCQuery servers.
/// Methods throw <see cref="HttpRequestException"/>, <see cref="TaskCanceledException"/> (timeout) or <see cref="JsonException"/> on failure.
/// </summary>
public sealed class OscQueryHttpClient
{
    private static readonly Lazy<HttpClient> SharedHttpClient = new(() => CreateHttpClient("OscQueryLibrary"));
    private static readonly Regex InvalidTokenChars = new(@"[^A-Za-z0-9!#$%&'*+.^_`|~-]", RegexOptions.Compiled);

    private readonly HttpClient _httpClient;

    public OscQueryHttpClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? SharedHttpClient.Value;
    }

    /// <summary>
    /// Creates an <see cref="HttpClient"/> suited for OSCQuery: short timeout, no caching.
    /// </summary>
    public static HttpClient CreateHttpClient(string userAgent) => new()
    {
        // Keep this short, an unreachable service (e.g. a Quest that went to sleep) would otherwise block for 100s
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders =
        {
            UserAgent = { new ProductInfoHeaderValue(InvalidTokenChars.Replace(userAgent, "-"), "1.0") },
            Pragma = { new NameValueHeaderValue("no-cache") }
        }
    };

    public Task<OscQueryHostInfo> GetHostInfoAsync(IPEndPoint endPoint, CancellationToken cancellationToken = default) =>
        GetAsync(new Uri($"http://{endPoint}/?HOST_INFO"), ModelsSourceGenerationContext.Default.OscQueryHostInfo, cancellationToken);

    /// <summary>
    /// Fetches the node at <paramref name="path"/> including everything below it.
    /// </summary>
    public Task<OscQueryNode> GetNodeAsync(IPEndPoint endPoint, string path = "/", CancellationToken cancellationToken = default) =>
        GetAsync(new Uri($"http://{endPoint}{EscapePath(path)}"), ModelsSourceGenerationContext.Default.OscQueryNode, cancellationToken);

    private async Task<T> GetAsync<T>(Uri uri, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false)
               ?? throw new JsonException($"Empty response from {uri}");
    }

    private static string EscapePath(string path) =>
        "/" + string.Join("/", path.Split(['/'], StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
}
