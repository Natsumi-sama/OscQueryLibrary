# OscQuery Example Library

## Info

A more human readable but potentially less functional OscQuery library for VRChat.

## Functionality

Auto negotiate with VRC to connect to your OSC server without the need of a OSC router.
<br>
Receive available parameter list immediately without needing to read avatar JSON file or switch avatar.
<br>
Quest/second PC support soon...

## Setup

```c#
new OscQueryServer(
    "HelloWorld", // service name
    "127.0.0.1", // ip address for udp and http server
    8063, // osc server port
    CallbackMethod // optional parameter list callback on vrc discovery
);

private static void CallbackMethod(Dictionary<string, object?> parameterList)
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
