using System.Net;
using System.Net.Sockets;

namespace OscQueryLibrary.Utils;

internal static class SocketUtils
{
    public static ushort FindAvailableTcpPort(IPAddress ipAddress)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(ipAddress, port: 0));
        ushort port = 0;
        if (socket.LocalEndPoint != null)
            port = (ushort)((IPEndPoint)socket.LocalEndPoint).Port;
        return port;
    }
    
    public static ushort FindAvailableUdpPort(IPAddress ipAddress)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(ipAddress, port: 0));
        ushort port = 0;
        if (socket.LocalEndPoint != null)
            port = (ushort)((IPEndPoint)socket.LocalEndPoint).Port;
        return port;
    }
}