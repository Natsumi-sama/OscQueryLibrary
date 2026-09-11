using System.Net;
using System.Net.Sockets;
using MeaMod.DNS.Model;
using MeaMod.DNS.Multicast;

namespace OscQueryLibrary.Tests.Fakes;

/// <summary>
/// An OSCQuery service as it would be advertised over mDNS.
/// </summary>
internal sealed class FakeService
{
    public FakeService(string instance, ushort port, params IPAddress[] addresses)
    {
        Instance = instance;
        Port = port;
        Addresses = addresses;
    }

    public string Instance { get; }
    public ushort Port { get; }
    public IPAddress[] Addresses { get; }

    public DomainName InstanceDomain => new($"{Instance}.{OscQueryDiscovery.ServiceType}.local");
    public DomainName Target => new($"{Instance}-host.local");

    public IPEndPoint HttpEndPoint(int address = 0) => new(Addresses[address], Port);
}

/// <summary>
/// Feeds mDNS messages straight into a <see cref="MulticastService"/>, no sockets involved.
/// </summary>
internal static class FakeMdns
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);
    private static readonly DomainName ServiceDomain = new($"{OscQueryDiscovery.ServiceType}.local");

    /// <summary>
    /// A response like the one to a query: PTR records as answers, SRV and A records as additional records.
    /// </summary>
    public static void Announce(MulticastService mdns, params FakeService[] services)
    {
        var message = new Message { QR = true, AA = true };
        foreach (var service in services)
        {
            message.Answers.Add(new PTRRecord { Name = ServiceDomain, DomainName = service.InstanceDomain, TTL = Ttl });
            message.AdditionalRecords.Add(new SRVRecord
                { Name = service.InstanceDomain, Target = service.Target, Port = service.Port, TTL = Ttl });
            foreach (var address in service.Addresses)
                message.AdditionalRecords.Add(new ARecord { Name = service.Target, Address = address, TTL = Ttl });
        }

        Deliver(mdns, message);
    }

    public static void SrvGoodbye(MulticastService mdns, FakeService service)
    {
        var message = new Message { QR = true, AA = true };
        message.Answers.Add(new SRVRecord
            { Name = service.InstanceDomain, Target = service.Target, Port = service.Port, TTL = TimeSpan.Zero });
        Deliver(mdns, message);
    }

    public static void PtrGoodbye(MulticastService mdns, FakeService service)
    {
        var message = new Message { QR = true, AA = true };
        message.Answers.Add(new PTRRecord { Name = ServiceDomain, DomainName = service.InstanceDomain, TTL = TimeSpan.Zero });
        Deliver(mdns, message);
    }

    private static void Deliver(MulticastService mdns, Message message) =>
        mdns.OnDnsMessage(mdns, new UdpReceiveResult(message.ToByteArray(), new IPEndPoint(IPAddress.Loopback, 5353)));
}
