﻿using System.Diagnostics;
using System.Net;
using System.Text.Json;
using LucHeart.CoreOSC;
using OscQueryLibrary;
using OscQueryLibrary.Utils;
using Serilog;
using Serilog.Events;

namespace OscQueryExample;

public static class Program
{
    private static OscQueryServer _localHostServer;
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
        
        _localHostServer = new OscQueryServer(
            "HelloWorld", // service name
            IPAddress.Loopback // ip address for our local service
        );
        await _localHostServer.FoundVrcClient.SubscribeAsync(ipEndPoint => FoundVrcClient(ipEndPoint, _localHostServer)); // event on vrc discovery
        await _localHostServer.ParameterUpdate.SubscribeAsync(UpdateAvailableParameters); // event on parameter list update

        _localHostServer.Start(); // this is required to start the service, no events will fire without this.
        
        // call a class that properly disposes of the game connection and the oscqueryservice.
        // disposing of the oscqueryservice is required to send a goodbye packet to VRChat.
        AppDomain.CurrentDomain.ProcessExit += (s, e) => Cleanup();
        while (true)
        {
            Console.ReadLine();
            await ToggleMute();
        }
    }
    
    private static void Cleanup()
    {
        _currentOscQueryServer?.Dispose();
        _gameConnection?.Dispose();
    }

    private static CancellationTokenSource _loopCancellationToken = new CancellationTokenSource();
    private static OscQueryServer? _currentOscQueryServer = null;

    private static Task FoundVrcClient(IPEndPoint endPoint, OscQueryServer oscQueryServer)
    {
        // stop tasks
        _loopCancellationToken.Cancel();
        _loopCancellationToken = new CancellationTokenSource();
        _gameConnection?.Dispose();
        _gameConnection = null;

        _logger.Information("Found VRC client at {EndPoint}", endPoint);
        _logger.Information("Starting listening for VRC client at {Port}", oscQueryServer.OscReceivePort);
        
        _gameConnection = new OscDuplex(new IPEndPoint(endPoint.Address, oscQueryServer.OscReceivePort), endPoint);
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
                await _currentOscQueryServer!.RefreshParameters();
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
        arguments ??= [];
        await _gameConnection.SendAsync(new OscMessage(address, arguments));
    }

    private static Task UpdateAvailableParameters(OscQueryServer.ParameterUpdateArgs parameterUpdateArgs)
    {
        AvailableParameters.Clear();
        foreach (var parameter in parameterUpdateArgs.Parameters)
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