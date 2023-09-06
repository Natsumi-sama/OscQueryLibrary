using System.Diagnostics;
using System.Net;
using System.Text.Json;
using LucHeart.CoreOSC;
using OscQueryLibrary;

namespace OscQueryExample;

public static class Program
{
    private const string IpAddress = "127.0.0.1";
    private const int OscSendPort = 9000;

    private static OscDuplex _gameConnection = null!;

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
        var oscQueryServer = new OscQueryServer(
            "HelloWorld", // service name
            IpAddress, // ip address for udp and http server
            UpdateAvailableParameters // parameter list callback on vrc discovery
        );
        
        _gameConnection = new(
            new IPEndPoint(IPAddress.Parse(IpAddress), OscQueryServer.OscPort),
            new IPEndPoint(IPAddress.Parse(IpAddress), OscSendPort)
        );
        Task.Run(ReceiverLoopAsync);

        while (true)
        {
            Thread.Sleep(10000);
            // ToggleMute();
        }
        // ReSharper disable once FunctionNeverReturns
    }

    private static async Task ReceiverLoopAsync()
    {
        while (true)
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
        var received = await _gameConnection.ReceiveMessageAsync();
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