using System.Net;
using System.Net.NetworkInformation;
using Serilog;

namespace OscQueryLibrary.Utils;

internal static class NetworkUtils
{
    private static readonly ILogger Logger = Log.ForContext(typeof(NetworkUtils));

    /// <summary>
    /// True if the address is loopback or assigned to one of this machine's network interfaces.
    /// </summary>
    public static bool IsLocalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(i => i.GetIPProperties().UnicastAddresses)
                .Any(a => a.Address.Equals(address));
        }
        catch (NetworkInformationException ex)
        {
            Logger.Debug(ex, "Failed to enumerate network interfaces");
            return false;
        }
    }
}
