using System.Net;
using System.Text.Json;
using MeaMod.DNS.Multicast;
using OscQueryLibrary.Models;
using OscQueryLibrary.Tests.Fakes;
using OscQueryLibrary.VRChat;

namespace OscQueryLibrary.Tests;

public sealed class VrOscQueryServerTests : IDisposable
{
    // TEST-NET-1, never assigned to this machine
    private static readonly IPAddress RemoteAddress = IPAddress.Parse("192.0.2.10");

    private readonly MulticastService _mdns = OscQueryDiscovery.CreateMulticastService();
    private readonly FakeOscQueryServers _servers = new();
    private readonly VrOscQueryServer _server;

    public VrOscQueryServerTests()
    {
        // Never started, so nothing is advertised and all mDNS traffic comes from FakeMdns
        _server = new VrOscQueryServer("Test", IPAddress.Loopback, "127.0.0.1", null, _servers.CreateClient(), _mdns);
    }

    private FakeService AddClient(string instance, IPAddress address, ushort port, string? rootJson = TestData.VrcRootJson)
    {
        var service = new FakeService(instance, port, address);
        _servers.Add(service.HttpEndPoint(), TestData.HostInfoJson(instance, address, 9000), rootJson);
        return service;
    }

    [Test]
    public async Task Host_AsksForAvatarMessages()
    {
        var change = _server.Host.Root.Find("/avatar/change");

        await Assert.That(change?.Access).IsEqualTo(AccessValues.Write);
        await Assert.That(change?.Type).IsEqualTo("s");
        await Assert.That(_server.Host.HostInfo.OscPort).IsEqualTo(_server.OscReceivePort);
    }

    [Test]
    public async Task DiscoveredClient_IsSelected_AndParametersFetched()
    {
        var found = await EventRecorder<IPEndPoint>.Subscribe(_server.FoundVrClient);
        var parameters = await EventRecorder<VrOscQueryServer.ParameterUpdateArgs>.Subscribe(_server.ParameterUpdate);

        FakeMdns.Announce(_mdns, AddClient("VRChat-Client-A", IPAddress.Loopback, 5000));

        await Assert.That(await found.Next()).IsEqualTo(new IPEndPoint(IPAddress.Loopback, 9000));
        await Assert.That(_server.CurrentVrClient?.InstanceName).IsEqualTo("VRChat-Client-A");

        var update = await parameters.Next();
        await Assert.That(update.AvatarId).IsEqualTo("avtr_123");
        await Assert.That(update.Parameters.Keys.Order().ToArray()).IsEquivalentTo(
            ["/avatar/parameters/Nested/Float", "/avatar/parameters/NoValue", "/avatar/parameters/VRCEmote"], CollectionOrdering.Matching);
        await Assert.That(((JsonElement)update.Parameters["/avatar/parameters/VRCEmote"]!).GetInt32()).IsEqualTo(4);
        await Assert.That(((JsonElement)update.Parameters["/avatar/parameters/Nested/Float"]!).GetDouble()).IsEqualTo(0.5);
        await Assert.That(update.Parameters["/avatar/parameters/NoValue"]).IsNull();
    }

    [Test]
    public async Task OnlyVrcClients_AreResolved()
    {
        var discovered = await EventRecorder<OscQueryServiceInfo>.Subscribe(_server.VrClientDiscovered);
        var other = AddClient("SomeOtherApp", IPAddress.Loopback, 5000);

        FakeMdns.Announce(_mdns, other, AddClient("VRChat-Client-A", IPAddress.Loopback, 5001));

        await Assert.That((await discovered.Next()).InstanceName).IsEqualTo("VRChat-Client-A");
        await Assert.That(_servers.RequestCount(other.HttpEndPoint())).IsEqualTo(0);
        await Assert.That(_server.VrcClients).HasSingleItem();
    }

    [Test]
    public async Task LocalClient_IsPreferred_PinnedClientWins_UntilLost()
    {
        var found = await EventRecorder<IPEndPoint>.Subscribe(_server.FoundVrClient);
        var lost = await EventRecorder<OscQueryServiceInfo>.Subscribe(_server.VrClientLost);
        var remote = AddClient("VRChat-Client-Remote", RemoteAddress, 5000);
        var remoteOsc = new IPEndPoint(RemoteAddress, 9000);
        var localOsc = new IPEndPoint(IPAddress.Loopback, 9000);

        FakeMdns.Announce(_mdns, remote);
        await Assert.That(await found.Next()).IsEqualTo(remoteOsc);

        // Found later, but on this machine
        FakeMdns.Announce(_mdns, AddClient("VRChat-Client-Local", IPAddress.Loopback, 5001));
        await Assert.That(await found.Next()).IsEqualTo(localOsc);

        await _server.SelectVrClient(_server.VrcClients.Single(c => c.InstanceName == "VRChat-Client-Remote"));
        await Assert.That(await found.Next()).IsEqualTo(remoteOsc);
        await Assert.That(_server.CurrentVrClient?.InstanceName).IsEqualTo("VRChat-Client-Remote");

        FakeMdns.SrvGoodbye(_mdns, remote);
        await Assert.That((await lost.Next()).InstanceName).IsEqualTo("VRChat-Client-Remote");
        await Assert.That(await found.Next()).IsEqualTo(localOsc);
        await Assert.That(_server.CurrentVrClient?.InstanceName).IsEqualTo("VRChat-Client-Local");
    }

    [Test]
    public async Task ClientFailingParameterFetch_IsDropped()
    {
        var lost = await EventRecorder<OscQueryServiceInfo>.Subscribe(_server.VrClientLost);

        FakeMdns.Announce(_mdns, AddClient("VRChat-Client-Broken", IPAddress.Loopback, 5000, rootJson: null));

        await Assert.That((await lost.Next()).InstanceName).IsEqualTo("VRChat-Client-Broken");
        await Assert.That(_server.CurrentVrClient).IsNull();
        await Assert.That(_server.VrcClients).IsEmpty();
    }

    public void Dispose() => _server.Dispose();
}
