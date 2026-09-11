using System.Net;
using MeaMod.DNS.Multicast;
using OscQueryLibrary.Models;
using OscQueryLibrary.Tests.Fakes;

namespace OscQueryLibrary.Tests;

/// <summary>
/// mDNS answers are fed straight into the multicast service and HTTP is answered in memory, nothing touches the network.
/// </summary>
public sealed class OscQueryDiscoveryTests : IDisposable
{
    // TEST-NET-1, never assigned to this machine
    private static readonly IPAddress RemoteAddress = IPAddress.Parse("192.0.2.10");

    private readonly MulticastService _mdns = OscQueryDiscovery.CreateMulticastService();
    private readonly FakeOscQueryServers _servers = new();
    private readonly List<OscQueryDiscovery> _discoveries = [];

    private OscQueryDiscovery CreateDiscovery(Func<string, bool>? instanceFilter = null)
    {
        var discovery = new OscQueryDiscovery(new OscQueryDiscoveryOptions
        {
            HttpClient = _servers.CreateClient(),
            InstanceFilter = instanceFilter,
            HealthCheckInterval = null
        }, _mdns);
        _discoveries.Add(discovery);
        return discovery;
    }

    private FakeService AddService(string instance, ushort port, IPAddress? address = null, ushort oscPort = 9000)
    {
        var service = new FakeService(instance, port, address ?? IPAddress.Loopback);
        _servers.Add(service.HttpEndPoint(), TestData.HostInfoJson(instance, service.Addresses[0], oscPort));
        return service;
    }

    [Test]
    public async Task Announcement_ResolvesService()
    {
        var discovery = CreateDiscovery();
        var discovered = await EventRecorder<OscQueryServiceInfo>.Subscribe(discovery.ServiceDiscovered);
        var service = AddService("App", 5000, oscPort: 9001);

        FakeMdns.Announce(_mdns, service);
        var info = await discovered.Next();

        await Assert.That(info.InstanceName).IsEqualTo("App");
        await Assert.That(info.HttpEndPoint).IsEqualTo(new IPEndPoint(IPAddress.Loopback, 5000));
        await Assert.That(info.OscEndPoint).IsEqualTo(new IPEndPoint(IPAddress.Loopback, 9001));
        await Assert.That(info.HostInfo.Name).IsEqualTo("App");
        await Assert.That(info.IsLocal).IsTrue();
        await Assert.That(discovery.Services).HasSingleItem();
        await Assert.That(discovery.Services.Single()).IsSameReferenceAs(info);
    }

    [Test]
    public async Task RemoteAddress_IsNotLocal()
    {
        var discovery = CreateDiscovery();
        var discovered = await EventRecorder<OscQueryServiceInfo>.Subscribe(discovery.ServiceDiscovered);

        FakeMdns.Announce(_mdns, AddService("Remote", 5000, RemoteAddress));

        await Assert.That((await discovered.Next()).IsLocal).IsFalse();
    }

    [Test]
    public async Task RepeatedAnnouncement_IsResolvedOnce()
    {
        var discovery = CreateDiscovery();
        var discovered = await EventRecorder<OscQueryServiceInfo>.Subscribe(discovery.ServiceDiscovered);
        var first = AddService("First", 5000);
        var second = AddService("Second", 5001);

        FakeMdns.Announce(_mdns, first);
        await discovered.Next();
        // Answers in one message are processed in order, so First was handled once Second shows up
        FakeMdns.Announce(_mdns, first, second);
        await Assert.That((await discovered.Next()).InstanceName).IsEqualTo("Second");

        await Assert.That(_servers.RequestCount(first.HttpEndPoint())).IsEqualTo(1);
        await Assert.That(discovered.HasPending).IsFalse();
        await Assert.That(discovery.Services.Count).IsEqualTo(2);
    }

    [Test]
    public async Task InstanceFilter_SkipsService()
    {
        var discovery = CreateDiscovery(name => name.StartsWith("Keep"));
        var discovered = await EventRecorder<OscQueryServiceInfo>.Subscribe(discovery.ServiceDiscovered);
        var dropped = AddService("Drop", 5000);

        FakeMdns.Announce(_mdns, dropped, AddService("Keep", 5001));

        await Assert.That((await discovered.Next()).InstanceName).IsEqualTo("Keep");
        await Assert.That(_servers.RequestCount(dropped.HttpEndPoint())).IsEqualTo(0);
        await Assert.That(discovery.Services).HasSingleItem();
    }

    [Test]
    public async Task UnreachableService_IsSkipped_AndNotRetriedRightAway()
    {
        var discovery = CreateDiscovery();
        var discovered = await EventRecorder<OscQueryServiceInfo>.Subscribe(discovery.ServiceDiscovered);
        var dead = new FakeService("Dead", 5000, IPAddress.Loopback);

        FakeMdns.Announce(_mdns, dead, AddService("Alive", 5001));
        await Assert.That((await discovered.Next()).InstanceName).IsEqualTo("Alive");
        FakeMdns.Announce(_mdns, dead, AddService("Alive2", 5002));
        await Assert.That((await discovered.Next()).InstanceName).IsEqualTo("Alive2");

        await Assert.That(_servers.RequestCount(dead.HttpEndPoint())).IsEqualTo(1);
        await Assert.That(discovery.Services).DoesNotContain(s => s.InstanceName == "Dead");
    }

    [Test]
    public async Task MultipleAddresses_UsesFirstThatAnswers()
    {
        var discovery = CreateDiscovery();
        var discovered = await EventRecorder<OscQueryServiceInfo>.Subscribe(discovery.ServiceDiscovered);
        var service = new FakeService("Multi", 5000, IPAddress.Loopback, RemoteAddress);
        // Only the remote address answers, the local one is tried first
        _servers.Add(service.HttpEndPoint(1), TestData.HostInfoJson("Multi", RemoteAddress, 9000));

        FakeMdns.Announce(_mdns, service);
        var info = await discovered.Next();

        await Assert.That(info.HttpEndPoint).IsEqualTo(service.HttpEndPoint(1));
        await Assert.That(_servers.RequestCount(service.HttpEndPoint(0))).IsEqualTo(1);
        await Assert.That(info.IsLocal).IsTrue();
    }

    [Test]
    public async Task SrvGoodbye_RemovesService()
    {
        var discovery = CreateDiscovery();
        var discovered = await EventRecorder<OscQueryServiceInfo>.Subscribe(discovery.ServiceDiscovered);
        var lost = await EventRecorder<OscQueryServiceInfo>.Subscribe(discovery.ServiceLost);
        var service = AddService("App", 5000);

        FakeMdns.Announce(_mdns, service);
        var info = await discovered.Next();
        FakeMdns.SrvGoodbye(_mdns, service);

        await Assert.That(await lost.Next()).IsSameReferenceAs(info);
        await Assert.That(discovery.Services).IsEmpty();
    }

    [Test]
    public async Task PtrGoodbye_RemovesService()
    {
        var discovery = CreateDiscovery();
        var discovered = await EventRecorder<OscQueryServiceInfo>.Subscribe(discovery.ServiceDiscovered);
        var lost = await EventRecorder<OscQueryServiceInfo>.Subscribe(discovery.ServiceLost);
        var service = AddService("App", 5000);

        FakeMdns.Announce(_mdns, service);
        var info = await discovered.Next();
        FakeMdns.PtrGoodbye(_mdns, service);

        await Assert.That(await lost.Next()).IsSameReferenceAs(info);
        await Assert.That(discovery.Services).IsEmpty();
    }

    [Test]
    public async Task ReportUnreachable_RemovesService_UntilAnnouncedAgain()
    {
        var discovery = CreateDiscovery();
        var discovered = await EventRecorder<OscQueryServiceInfo>.Subscribe(discovery.ServiceDiscovered);
        var lost = await EventRecorder<OscQueryServiceInfo>.Subscribe(discovery.ServiceLost);
        var service = AddService("App", 5000);

        FakeMdns.Announce(_mdns, service);
        var info = await discovered.Next();
        await discovery.ReportUnreachable(info);

        await Assert.That(await lost.Next()).IsSameReferenceAs(info);
        await Assert.That(discovery.Services).IsEmpty();

        FakeMdns.Announce(_mdns, service);
        await Assert.That(await discovered.Next()).IsNotSameReferenceAs(info);
    }

    [Test]
    public async Task ReportUnreachable_IgnoresReplacedService()
    {
        var discovery = CreateDiscovery();
        var discovered = await EventRecorder<OscQueryServiceInfo>.Subscribe(discovery.ServiceDiscovered);
        var lost = await EventRecorder<OscQueryServiceInfo>.Subscribe(discovery.ServiceLost);
        var service = AddService("App", 5000);

        FakeMdns.Announce(_mdns, service);
        var stale = await discovered.Next();
        await discovery.ReportUnreachable(stale);
        await lost.Next();
        FakeMdns.Announce(_mdns, service);
        var current = await discovered.Next();

        // A late report about the old instance must not drop the new one
        await discovery.ReportUnreachable(stale);

        await Assert.That(discovery.Services).HasSingleItem();
        await Assert.That(discovery.Services.Single()).IsSameReferenceAs(current);
        await Assert.That(lost.HasPending).IsFalse();
    }

    public void Dispose()
    {
        foreach (var discovery in _discoveries) discovery.Dispose();
        _mdns.Dispose();
    }
}
