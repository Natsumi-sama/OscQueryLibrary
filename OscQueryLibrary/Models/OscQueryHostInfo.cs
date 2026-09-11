using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using OscQueryLibrary.Utils;

// ReSharper disable InconsistentNaming

namespace OscQueryLibrary.Models;

/// <summary>
/// The <c>HOST_INFO</c> document of an OSCQuery server.
/// </summary>
public sealed class OscQueryHostInfo
{
    [JsonPropertyName("NAME")] public string? Name { get; set; }

    /// <summary>
    /// Address the OSC server listens on. When missing, it's the same as the OSCQuery HTTP server.
    /// </summary>
    [JsonPropertyName("OSC_IP")]
    [JsonConverter(typeof(JsonIPAddressConverter))]
    public IPAddress? OscIp { get; set; }

    /// <summary>
    /// Port the OSC server listens on. When missing, it's the same as the OSCQuery HTTP server.
    /// </summary>
    [JsonPropertyName("OSC_PORT")] public ushort? OscPort { get; set; }

    [JsonPropertyName("OSC_TRANSPORT")] public OscTransport? OscTransport { get; set; }

    /// <summary>
    /// Supported attributes, e.g. <c>ACCESS</c>, <c>VALUE</c>, <c>RANGE</c>.
    /// </summary>
    [JsonPropertyName("EXTENSIONS")] public Dictionary<string, bool>? Extensions { get; set; }

    [JsonPropertyName("WS_IP")]
    [JsonConverter(typeof(JsonIPAddressConverter))]
    public IPAddress? WsIp { get; set; }

    [JsonPropertyName("WS_PORT")] public ushort? WsPort { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<OscTransport>))]
public enum OscTransport
{
    TCP,
    UDP
}
