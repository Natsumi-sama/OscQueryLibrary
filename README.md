# OscQuery Example Library

[![NuGet OscQueryLibrary](https://img.shields.io/nuget/v/OscQueryLibrary?style=for-the-badge&label=NuGet%20OscQueryLibrary)](https://www.nuget.org/packages/OscQueryLibrary/)
[![NuGet OscQueryLibrary Downloads](https://img.shields.io/nuget/dt/OscQueryLibrary?style=for-the-badge&label=NuGet%20Downloads)](https://www.nuget.org/packages/OscQueryLibrary/)

## Info

A more human readable OscQuery library for VRChat.

## Functionality

Auto negotiate with VRC to connect to your OSC server without the need of a OSC router.
<br>
Receive available parameter list immediately without needing to read avatar JSON file or switch avatar.
<br>
Quest support, it can auto discover VRC running on the same local network.

## Setup

```c#
// listen on localhost
var localHostServer = new OscQueryServer(
    "HelloWorld", // service name
    IPAddress.Loopback // ip address for udp and http server
);
localHostServer.FoundVrcClient += FoundVrcClient; // event on vrc discovery
localHostServer.ParameterUpdate += UpdateAvailableParameters; // event on parameter list update
localHostServer.Start(); // this is required to start the service, no events will fire without this.

// listen for VRC on every network interface (Quest only)
var host = await Dns.GetHostEntryAsync(Dns.GetHostName());
foreach (var ip in host.AddressList)
{
    if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        continue;

    var server = new OscQueryServer(
        "HelloWorld", // service name
        ip
    );

    server.FoundVrcClient += FoundVrcClient; // event on vrc discovery
    server.ParameterUpdate += UpdateAvailableParameters; // event on parameter list update
    server.Start(); // this is required to start the service, no events will fire without this.
}

private static Task FoundVrcClient(OscQueryServer oscQueryServer, IPEndPoint ipEndPoint)
{
    // stop tasks
    Task.Delay(1000).Wait(); // wait for tasks to stop
    _gameConnection?.Dispose();
    _gameConnection = null;

    _gameConnection = new OscDuplex(
        new IPEndPoint(IPAddress.Parse(OscQueryServer.OscIpAddress), OscQueryServer.OscReceivePort),
        ipEndPoint)
    );
    Task.Run(ReceiverLoopAsync);
    return Task.CompletedTask;
}

private static Task UpdateAvailableParameters(Dictionary<string, object?> parameterList, string s)
{
    // ran when client connects or optionally when user changes avatar
    // the parameter values are in their initial state so they are mostly useless
    foreach (var parameter in parameterList)
    {
        Console.WriteLine(parameter.Key);
    }
    return Task.CompletedTask;
}

// depending on your OSC library you may want to run this method on avatar change to update your list of available parameters
if (received.Address == "/avatar/change")
    OscQueryServer.GetParameters();

```
