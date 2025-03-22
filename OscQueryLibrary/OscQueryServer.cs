using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using MeaMod.DNS.Model;
using MeaMod.DNS.Multicast;
using Serilog;
using EmbedIO;
using EmbedIO.Actions;
using MeaMod.DNS.Server;
using OpenShock.MinimalEvents;
using OscQueryLibrary.Models;
using OscQueryLibrary.Utils;

namespace OscQueryLibrary;

public class OscQueryServer : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext(typeof(OscQueryServer));

    private static readonly HttpClient Client = new();
    
    private readonly ushort _httpPort;
    private readonly IPAddress _serviceIpAddress;
    public ushort OscReceivePort { get; private set; }
    private const string OscHttpServiceName = "_oscjson._tcp";
    private const string OscUdpServiceName = "_osc._udp";
    private readonly MulticastService _multicastService;
    private readonly ServiceDiscovery _serviceDiscovery;
    private readonly string _serviceName;
    
    private HostInfo? _hostInfo;
    private RootNode? _queryData;

    private readonly ConcurrentSet<string> _foundServices = new();
    private IPEndPoint? _lastVrcHttpServer;
    
    public struct ParameterUpdateArgs
    {
        public ImmutableDictionary<string, object?> Parameters { get; init; }
        public string AvatarId { get; init; }
    }
    
    public IAsyncMinimalEventObservable<ParameterUpdateArgs> ParameterUpdate => _parameterUpdate;
    private readonly AsyncMinimalEvent<ParameterUpdateArgs> _parameterUpdate = new();
    
    public IAsyncMinimalEventObservable<IPEndPoint> FoundVrcClient => _foundVrcClient;
    private readonly AsyncMinimalEvent<IPEndPoint> _foundVrcClient = new();

    private readonly WebServer _httpServer;

    static OscQueryServer()
    {
        Swan.Logging.Logger.NoLogging();
    }
    
    public OscQueryServer(string serviceName, IPAddress serviceIpAddress, IPAddress? oscQueryServerBind = null)
    {
        _serviceName = serviceName;
        _serviceIpAddress = serviceIpAddress;
        OscReceivePort = SocketUtils.FindAvailableUdpPort(serviceIpAddress);
        _httpPort = SocketUtils.FindAvailableTcpPort(serviceIpAddress);
        SetupJsonObjects();
        // ignore our own service
        _foundServices.Add($"{_serviceName.ToLower()}.{OscHttpServiceName}.local:{_httpPort}");
        _foundServices.Add($"{_serviceName.ToLower()}.{OscUdpServiceName}.local:{OscReceivePort}");

        // HTTP Server
        var webServerUrl = $"http://{IPAddress.Loopback}:{_httpPort}/";
        _httpServer = new WebServer(o => o
                .WithUrlPrefix(webServerUrl)
                .WithMode(HttpListenerMode.EmbedIO))
            .WithModule(new ActionModule("/", HttpVerbs.Get,
                ctx => ctx.SendStringAsync(
                    ctx.Request.RawUrl.Contains("HOST_INFO")
                        ? JsonSerializer.Serialize(_hostInfo, ModelsSourceGenerationContext.Default.HostInfo!)
                        : JsonSerializer.Serialize(_queryData, ModelsSourceGenerationContext.Default.RootNode!), MediaTypeNames.Application.Json, Encoding.UTF8)));

        Logger.Information("Configured webserver for url {Url}", webServerUrl);
        
        // mDNS
        _multicastService = new MulticastService
        {
            UseIpv6 = false,
            IgnoreDuplicateMessages = true
        };
        _serviceDiscovery = new ServiceDiscovery(_multicastService);
        
        _multicastService.NetworkInterfaceDiscovered += (a, args) =>
        {
            Logger.Debug("Network interface discovered");
            _multicastService.SendQuery($"{OscHttpServiceName}.local");
            _multicastService.SendQuery($"{OscUdpServiceName}.local");
        };

        _multicastService.AnswerReceived += OnAnswerReceived;
    }
    
    private bool _started;
    
    public void Start()
    {
        if (_started) return;
        _started = true;
        
        ErrorHandledTask.Run(() => _httpServer.RunAsync());
        Logger.Information("HTTP Server started!");
        
        _multicastService.Start();
        AdvertiseOscQueryServer();
    }

    private void AdvertiseOscQueryServer()
    {
        var httpProfile =
            new ServiceProfile(_serviceName, OscHttpServiceName, _httpPort,
                [_serviceIpAddress]);
        var oscProfile =
            new ServiceProfile(_serviceName, OscUdpServiceName, OscReceivePort,
                [_serviceIpAddress]);
        _serviceDiscovery.Advertise(httpProfile);
        _serviceDiscovery.Advertise(oscProfile);
    }
    

    private void OnAnswerReceived(object? sender, MessageEventArgs args)
    {
        ErrorHandledTask.Run(() => ProcessAnswer(args));
    }

    private async Task ProcessAnswer(MessageEventArgs args)
    {
        var message = args.Message;
        try
        {
            foreach (var record in message.AdditionalRecords.OfType<SRVRecord>())
            {
                var domainName = record.Name.Labels;
                var instanceName = domainName[0];
                var type = domainName[2];
                var serviceId = $"{record.CanonicalName}:{record.Port}";
                if (type == "_udp")
                    continue; // ignore UDP services

                if (record.TTL == TimeSpan.Zero)
                {
                    Logger.Debug("Goodbye message from {RecordCanonicalName}", record.CanonicalName);
                    _foundServices.Remove(serviceId);
                    continue;
                }

                if (_foundServices.Contains(serviceId))
                    continue;
                _foundServices.Add(serviceId);

                var ips = message.AdditionalRecords.OfType<ARecord>().Select(r => r.Address);
                // TODO: handle more than one IP address
                var ipAddress = ips.FirstOrDefault();
                Logger.Debug("Found service {ServiceId} {InstanceName} {IpAddress}:{RecordPort}", serviceId, instanceName, ipAddress, record.Port);

                if (instanceName.StartsWith("VRChat-Client-") && ipAddress != null)
                {
                    try
                    {
                        await FoundNewVrcClient(ipAddress, record.Port);
                    }
                    catch (Exception e)
                    {
                        Logger.Error(e, "Failed to process new client");
                    }
                    
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to parse from {ArgsRemoteEndPoint}", args.RemoteEndPoint);
        }
    }
    
    private async Task FoundNewVrcClient(IPAddress ipAddress, ushort port)
    {
        _lastVrcHttpServer = new IPEndPoint(ipAddress, port);
        var oscEndpoint = await FetchOscSendPortFromVrc(ipAddress, port).ConfigureAwait(false);
        if(oscEndpoint == null) return;
        try
        {
            await _foundVrcClient.InvokeAsyncParallel(oscEndpoint).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error while invoking found VRC client");
        }

        await RefreshParameters().ConfigureAwait(false);
    }
    
    private async Task<IPEndPoint?> FetchOscSendPortFromVrc(IPAddress ipAddress, ushort port)
    {
        var url = $"http://{ipAddress}:{port}?HOST_INFO";
        Logger.Debug("Fetching OSC send port from {Url}", url);
        var response = string.Empty;

        try
        {
            response = await Client.GetStringAsync(url).ConfigureAwait(false);
            var rootNode = JsonSerializer.Deserialize(response, ModelsSourceGenerationContext.Default.HostInfo);
            if (rootNode?.OscPort == null)
            {
                Logger.Error("Error no OSC port found");
                return null;
            }

            return new IPEndPoint(rootNode.OscIp, rootNode.OscPort);
        }
        catch (HttpRequestException ex)
        {
            Logger.Error("Error {ExMessage}", ex.Message);
        }
        catch (Exception ex)
        {
            Logger.Error("Error {ExMessage}\\n{Response}", ex.Message, response);
        }

        return null;
    }

    private async Task<ParameterUpdateArgs?> FetchJsonFromVrc(IPAddress ipAddress, ushort port)
    {
        
        var url = $"http://{ipAddress}:{port}/";
        Logger.Debug("Fetching new parameters from {Url}", url);
        try
        {
            var response = await Client.GetStringAsync(url).ConfigureAwait(false);
            
            var rootNode = JsonSerializer.Deserialize(response, ModelsSourceGenerationContext.Default.RootNode);
            if (rootNode?.Contents?.Avatar?.Contents?.Parameters?.Contents == null)
            {
                Logger.Warning("Error no parameters found");
                return null;
            }
            
            Dictionary<string, object?> parameterList = new();
            
            foreach (var node in rootNode.Contents.Avatar.Contents.Parameters.Contents!)
            {
                RecursiveParameterLookup(node.Value, parameterList);
            }

            var avatarId = rootNode.Contents.Avatar.Contents.Change.Value.FirstOrDefault() ?? string.Empty;
            
            return new ParameterUpdateArgs
            {
                Parameters = parameterList.ToImmutableDictionary(),
                AvatarId = avatarId
            };
        }
        catch (HttpRequestException ex)
        {
            _lastVrcHttpServer = null;
            Logger.Error(ex, "HTTP request failed");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Unexpected exception while receiving parameters via osc query");
        }
        
        return null;
    }

    private void RecursiveParameterLookup(OscParameterNode node, Dictionary<string, object?> parameterList)
    {
        if (node.Contents == null)
        {
            parameterList.Add(node.FullPath, node.Value?.FirstOrDefault());
            return;
        }

        foreach (var subNode in node.Contents) 
            RecursiveParameterLookup(subNode.Value, parameterList);
    }

    public async Task RefreshParameters()
    {
        if (_lastVrcHttpServer == null)
            return;
        
        Logger.Debug("Refreshing parameters");

        var parameters = await FetchJsonFromVrc(_lastVrcHttpServer.Address, (ushort)_lastVrcHttpServer.Port).ConfigureAwait(false);
        
        if (parameters == null) return;
        try
        {
            await _parameterUpdate.InvokeAsyncParallel(parameters.Value).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error while invoking parameter update");
        }
    }

    private void SetupJsonObjects()
    {
        _queryData = new RootNode
        {
            FullPath = "/",
            Access = 0,
            Contents = new RootNode.RootContents
            {
                Avatar = new Node<AvatarContents>
                {
                    FullPath = "/avatar",
                    Access = 2
                }
            }
        };

        _hostInfo = new HostInfo
        {
            Name = _serviceName,
            OscPort = OscReceivePort,
            OscIp = _serviceIpAddress,
            OscTransport = HostInfo.OscTransportType.UDP,
            Extensions = new HostInfo.ExtensionsNode
            {
                Access = true,
                ClipMode = true,
                Range = true,
                Type = true,
                Value = true
            }
        };
    }
    
    private bool _disposed;
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        Logger.Debug("Disposing OscQueryServer");;
        
        _serviceDiscovery.Unadvertise();
        _multicastService.Dispose();
        _serviceDiscovery.Dispose();
        
        GC.SuppressFinalize(this);
    }

    ~OscQueryServer()
    {
        Dispose();
    }
}