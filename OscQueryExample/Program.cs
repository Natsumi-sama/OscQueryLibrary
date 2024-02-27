using System.Diagnostics;
using System.Net;
using System.Text.Json;
using LucHeart.CoreOSC;
using OscQueryLibrary;
using Serilog;
using Serilog.Events;

namespace OscQueryExample;

public static class Program
{
    private const string IpAddress = "127.0.0.1";
    private static bool _oscServerActive;
    private static OscDuplex? _gameConnection = null;

    private static readonly HashSet<string> AvailableParameters = new();

    private static readonly HashSet<string> ParameterList = new()
    {
        "test",
        "AFK",
        "MuteSelf"
    };

    private static bool _isMuted;

    public static void Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .Filter.ByExcluding(ev =>
                ev.Exception is InvalidDataException a && a.Message.StartsWith("Invocation provides"))
            .WriteTo.Console(LogEventLevel.Information,
                "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        
        var oscQueryServer = new OscQueryServer(
            "HelloWorld", // service name
            IpAddress, // ip address for udp and http server
            FoundVrcClient, // optional callback on vrc discovery
            UpdateAvailableParameters // parameter list callback on vrc discovery
        );
        
        while (true)
        {
            Thread.Sleep(10000);
            // ToggleMute();
        }
        // ReSharper disable once FunctionNeverReturns
    }

    private static void FoundVrcClient()
    {
        // stop tasks
        _oscServerActive = false;
        Task.Delay(1000).Wait(); // wait for tasks to stop
        _gameConnection?.Dispose();
        _gameConnection = null;
        
        _gameConnection = new OscDuplex(
            new IPEndPoint(IPAddress.Parse(IpAddress), OscQueryServer.OscReceivePort),
            new IPEndPoint(IPAddress.Parse(IpAddress), OscQueryServer.OscSendPort)
        );
        _oscServerActive = true;
        Task.Run(ReceiverLoopAsync);
    }

    private static async Task ReceiverLoopAsync()
    {
        while (_oscServerActive)
        {
            try
            {
                await ReceiveLogic();
            }
            catch (Exception e)
            {
                Debug.WriteLine(e, "Error in receiver loop");
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
                await OscQueryServer.GetParameters();
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

    private static void UpdateAvailableParameters(Dictionary<string, object?> parameterList)
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