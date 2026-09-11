using System.Net;
using MeaMod.DNS.Model;

namespace OscQueryLibrary.Models;

/// <summary>
/// An OSCQuery service found on the network.
/// </summary>
public sealed class OscQueryServiceInfo
{
    /// <summary>
    /// mDNS instance name, e.g. <c>VRChat-Client-A1B2C3</c>.
    /// </summary>
    public required string InstanceName { get; init; }

    /// <summary>
    /// Unique key of this service, the mDNS instance name plus port.
    /// </summary>
    public required string ServiceId { get; init; }

    /// <summary>
    /// Endpoint of the OSCQuery HTTP server.
    /// </summary>
    public required IPEndPoint HttpEndPoint { get; init; }

    public required OscQueryHostInfo HostInfo { get; init; }

    /// <summary>
    /// True if one of the addresses the service advertised belongs to this machine.
    /// </summary>
    public required bool IsLocal { get; init; }

    public required DateTimeOffset DiscoveredAt { get; init; }

    /// <summary>
    /// Endpoint the service receives OSC on, taken from HOST_INFO with the HTTP endpoint as fallback.
    /// </summary>
    public IPEndPoint OscEndPoint => new(HostInfo.OscIp ?? HttpEndPoint.Address, HostInfo.OscPort ?? HttpEndPoint.Port);

    internal DomainName InstanceDomain { get; init; } = null!;
    internal int FailedHealthChecks;

    public override string ToString() => $"{InstanceName} ({HttpEndPoint}{(IsLocal ? ", local" : "")})";
}
