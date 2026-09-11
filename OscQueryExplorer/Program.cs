using System.Text;
using OscQueryLibrary;
using Spectre.Console;

namespace OscQueryExplorer;

public static class Program
{
    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // No instance filter, so every OSCQuery service on the network shows up, not just VRChat
        using var discovery = new OscQueryDiscovery(new OscQueryDiscoveryOptions
        {
            HealthCheckInterval = TimeSpan.FromSeconds(5)
        });
        var explorer = new Explorer(discovery);
        discovery.Start();

        // Prints a single frame instead of the interactive UI: --snapshot [seconds to discover] [instance name filter]
        if (args.Length > 0 && args[0] == "--snapshot")
        {
            var seconds = args.Length > 1 ? int.Parse(args[1]) : 3;
            var filter = args.Length > 2 ? args[2] : null;
            if (Console.IsOutputRedirected)
            {
                AnsiConsole.Profile.Width = 160;
                AnsiConsole.Profile.Height = 50;
            }

            await explorer.RenderSnapshotAsync(TimeSpan.FromSeconds(seconds), filter);
            return;
        }

        await explorer.RunAsync();
    }
}
