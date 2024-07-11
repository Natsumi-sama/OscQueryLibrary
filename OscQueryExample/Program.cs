using System.Diagnostics;
using System.Net;
using System.Text.Json;
using LucHeart.CoreOSC;
using OscQueryLibrary;
using OscQueryLibrary.Utils;
using Serilog;
using Serilog.Events;
using Swan.Logging;

namespace OscQueryExample;

public static class Program
{
    private static OscDuplex? _gameConnection = null;

    private static readonly HashSet<string> AvailableParameters = new();

    private static readonly HashSet<string> ParameterList = new()
    {
        "test",
        "AFK",
        "MuteSelf"
    };

    private static bool _isMuted;

    private static Serilog.ILogger _logger = null!;

    public static async Task Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(LogEventLevel.Verbose,
                "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        
        _logger = Log.ForContext(typeof(Program));

        var oscQueryServers = new List<OscQueryServer>();

        //listen for VRC on every network interface
        // var host = await Dns.GetHostEntryAsync(Dns.GetHostName());
        // foreach (var ip in host.AddressList)
        // {
        //     if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        //         continue;
        //
        //     var server = new OscQueryServer(
        //         "HelloWorld", // service name
        //         ip
        //     );
        //
        //     server.FoundVrcClient += FoundVrcClient; // event on vrc discovery
        //     server.ParameterUpdate += UpdateAvailableParameters; // event on parameter list update
        //
        //     oscQueryServers.Add(server);
        //
        //     server.Start();
        // }

        var localHostServer = new OscQueryServer(
            "HelloWorld", // service name
            IPAddress.Loopback // ip address for udp and http server
        );
        localHostServer.FoundVrcClient += FoundVrcClient; // event on vrc discovery
        localHostServer.ParameterUpdate += UpdateAvailableParameters; // event on parameter list update
        
        oscQueryServers.Add(localHostServer);

        localHostServer.Start();

        while (true)
        {
            Console.ReadLine();
            await ToggleMute();
        }
    }

    private static CancellationTokenSource _loopCancellationToken = new CancellationTokenSource();
    private static OscQueryServer? _currentOscQueryServer = null;

    private static Task FoundVrcClient(OscQueryServer oscQueryServer, IPEndPoint ipEndPoint)
    {
        // stop tasks
        _loopCancellationToken.Cancel();
        _loopCancellationToken = new CancellationTokenSource();
        _gameConnection?.Dispose();
        _gameConnection = null;

        _logger.Information("Found VRC client at {EndPoint}", ipEndPoint);
        _logger.Information("Starting listening for VRC client at {Port}", oscQueryServer.OscReceivePort);
        
        _gameConnection = new OscDuplex(
            new IPEndPoint(ipEndPoint.Address, oscQueryServer.OscReceivePort),
            ipEndPoint);
        _currentOscQueryServer = oscQueryServer;
        ErrorHandledTask.Run(ReceiverLoopAsync);
        return Task.CompletedTask;
    }

    private static async Task ReceiverLoopAsync()
    {
        var currentCancellationToken = _loopCancellationToken.Token;
        while (!currentCancellationToken.IsCancellationRequested)
        {
            try
            {
                await ReceiveLogic();
            }
            catch (Exception e)
            {
                _logger.Error(e, "Error in receiver loop");
            }
        }
        // ReSharper disable once FunctionNeverReturns
    }

    private static async Task ReceiveLogic()
    {
        if (_gameConnection == null) return;
        OscMessage received;
        try
        {
            received = await _gameConnection.ReceiveMessageAsync();
        }
        catch (Exception e)
        {
            Debug.WriteLine(e, "Error receiving message");
            return;
        }

        var addr = received.Address;
        
        switch (addr)
        {
            case "/avatar/change":
            {
                var avatarId = received.Arguments.ElementAtOrDefault(0);
                Console.WriteLine($"Avatar changed: {avatarId}");
                await _currentOscQueryServer!.GetParameters();
                break;
            }
            case "/avatar/parameters/MuteSelf":
            {
                var isMuted = received.Arguments.ElementAtOrDefault(0);
                if (isMuted != null)
                    _isMuted = (bool)isMuted;
                Console.WriteLine($"isMuted: {_isMuted}");
                break;
            }
        }
    }

    private static async Task SendGameMessage(string address, params object?[]? arguments)
    {
        if (_gameConnection == null) return;
        arguments ??= Array.Empty<object>();
        await _gameConnection.SendAsync(new OscMessage(address, arguments));
    }

    private static Task UpdateAvailableParameters(Dictionary<string, object?> parameterList, string s)
    {
        AvailableParameters.Clear();
        foreach (var parameter in parameterList)
        {
            var parameterName = parameter.Key.Replace("/avatar/parameters/", "");
            if (ParameterList.Contains(parameterName))
                AvailableParameters.Add(parameterName);

            if (parameterName == "MuteSelf" && parameter.Value != null)
            {
                _isMuted = ((JsonElement)parameter.Value).GetBoolean();
                Console.WriteLine($"AviJson isMuted: {_isMuted}");
            }
        }

        Console.WriteLine($"Found {AvailableParameters.Count} parameters");
        return Task.CompletedTask;
    }

    private static async Task ToggleMute()
    {
        if (_isMuted)
            Debug.WriteLine("Unmuting...");
        else
            Debug.WriteLine("Muting...");

        await SendGameMessage("/input/Voice", false);
        await Task.Delay(50);
        await SendGameMessage("/input/Voice", true);
        await Task.Delay(50);
        await SendGameMessage("/input/Voice", false);
    }
}