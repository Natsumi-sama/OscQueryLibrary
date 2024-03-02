# OscQuery Example Library

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
new OscQueryServer(
    "HelloWorld", // service name
    "127.0.0.1", // ip address for udp and http server
    FoundVrcClient, // optional callback on vrc discovery
    UpdateAvailableParameters // parameter list callback on vrc discovery
);

// listen for VRC on every network interface (Quest only)
var host = Dns.GetHostEntry(Dns.GetHostName());
foreach (var ip in host.AddressList)
{
    if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        continue;

    var ipAddress = ip.ToString();
    _ = new OscQueryServer(
        "HelloWorld", // service name
        ipAddress, // ip address for udp and http server
        FoundVrcClient, // optional callback on vrc discovery
        UpdateAvailableParameters // parameter list callback on vrc discovery
    );
}

private static void FoundVrcClient()
{
    // stop tasks
    Task.Delay(1000).Wait(); // wait for tasks to stop
    _gameConnection?.Dispose();
    _gameConnection = null;

    _gameConnection = new OscDuplex(
        new IPEndPoint(IPAddress.Parse(OscQueryServer.OscIpAddress), OscQueryServer.OscReceivePort),
        new IPEndPoint(IPAddress.Parse(OscQueryServer.OscIpAddress), OscQueryServer.OscSendPort)
    );
    Task.Run(ReceiverLoopAsync);
}

private static void UpdateAvailableParameters(Dictionary<string, object?> parameterList)
{
    // ran when client connects or optionally when user changes avatar
    // the parameter values are in their initial state so they are mostly useless
    foreach (var parameter in parameterList)
    {
        Console.WriteLine(parameter.Key);
    }
}

// depending on your OSC library you might want to run this method on avatar change to update your list of available parameters
if (received.Address == "/avatar/change")
    OscQueryServer.GetParameters();

```
