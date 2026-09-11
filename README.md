# OscQuery Library

[![NuGet OscQueryLibrary](https://img.shields.io/nuget/v/OscQueryLibrary?style=for-the-badge&label=NuGet%20OscQueryLibrary)](https://www.nuget.org/packages/OscQueryLibrary/)
[![NuGet OscQueryLibrary Downloads](https://img.shields.io/nuget/dt/OscQueryLibrary?style=for-the-badge&label=NuGet%20Downloads)](https://www.nuget.org/packages/OscQueryLibrary/)

## Info

A more human readable OscQuery library, with VRChat support built on top.

## Functionality

### VR (VRChat, ChilloutVR or similar applications) (`OscQueryLibrary.VR`)

`VrOscQueryServer` auto negotiates with VRChat, or similar applications, to connect to your OSC server without the need of a OSC router.
<br>
Receive available parameter list immediately without needing to read avatar JSON file or switch avatar.
<br>
Quest support, it can auto discover VRC running on the same local network.
<br>
Multiple VRC clients on the network: a client on the same machine is preferred, otherwise the most recently discovered one. All known clients are available via `VrcClients`, and a specific one can be pinned with `SelectVrcClient`. If the active client quits or stops responding, the next best one is used.

### Generic OSCQuery (`OscQueryLibrary`)

- `OscQueryDiscovery` finds every OSCQuery service (`_oscjson._tcp`) on the network via mDNS, resolves its HOST_INFO and drops services that say goodbye or stop responding.
- `OscQueryHttpClient` reads HOST_INFO and the address space (`OscQueryNode` tree) of any OSCQuery server.
- `OscQueryHost` serves your own address space over HTTP and advertises it via mDNS.

`OscQueryServer` still exists for compatibility but is obsolete, use `VrcOscQueryServer` instead.

## Usage

- `OscQueryExample`: VRChat integration, reads avatar parameters and toggles mute.
- `OscQueryExplorer`: terminal UI that lists every OSCQuery service on the network and shows its live address space. Run it with `dotnet run --project OscQueryExplorer`.
