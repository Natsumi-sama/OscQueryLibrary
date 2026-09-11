using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using EmbedIO;
using EmbedIO.Actions;
using MeaMod.DNS.Multicast;
using OscQueryLibrary.Models;
using OscQueryLibrary.Utils;
using Serilog;

namespace OscQueryLibrary;

public sealed class OscQueryHostOptions
{
    /// <summary>
    /// mDNS instance name and HOST_INFO NAME.
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    /// Address advertised over mDNS and reported as OSC_IP.
    /// </summary>
    public required IPAddress Address { get; init; }

    /// <summary>
    /// Host the HTTP server binds to, <c>*</c> for all interfaces.
    /// </summary>
    public string HttpBind { get; init; } = "*";

    /// <summary>
    /// 0 picks a free port.
    /// </summary>
    public ushort HttpPort { get; init; }

    /// <summary>
    /// Port your OSC server listens on, 0 picks a free UDP port you then have to bind to (see <see cref="OscQueryHost.OscPort"/>).
    /// </summary>
    public ushort OscPort { get; init; }

    public OscTransport OscTransport { get; init; } = OscTransport.UDP;

    /// <summary>
    /// Also advertise the OSC server itself as <c>_osc._udp</c>.
    /// </summary>
    public bool AdvertiseOsc { get; init; } = true;

    public Dictionary<string, bool> Extensions { get; init; } = new()
    {
        ["ACCESS"] = true,
        ["CLIPMODE"] = true,
        ["RANGE"] = true,
        ["TYPE"] = true,
        ["VALUE"] = true
    };

    /// <summary>
    /// The address space to serve, can be swapped later via <see cref="OscQueryHost.Root"/>.
    /// </summary>
    public OscQueryNode? Root { get; init; }
}

/// <summary>
/// Serves an OSCQuery address space over HTTP and advertises it via mDNS.
/// </summary>
public sealed class OscQueryHost : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext(typeof(OscQueryHost));

    private const string OscServiceType = "_osc._udp";

    private readonly OscQueryHostOptions _options;
    private readonly MulticastService _multicastService;
    private readonly bool _ownsMulticastService;
    private readonly ServiceDiscovery _serviceDiscovery;
    private readonly WebServer _httpServer;
    private readonly CancellationTokenSource _cts = new();
    private volatile OscQueryNode _root;

    private bool _started;
    private bool _disposed;

    static OscQueryHost()
    {
        Swan.Logging.Logger.NoLogging();
    }

    public string ServiceName => _options.ServiceName;
    public ushort HttpPort { get; }
    public ushort OscPort { get; }
    public OscQueryHostInfo HostInfo { get; }

    /// <summary>
    /// The served address space, replace it to change what clients see.
    /// </summary>
    public OscQueryNode Root
    {
        get => _root;
        set => _root = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <param name="options">What to serve and where.</param>
    /// <param name="multicastService">
    /// mDNS service to share with e.g. an <see cref="OscQueryDiscovery"/>. When passed, the caller owns it and must start it.
    /// When null, the host creates and manages its own.
    /// </param>
    public OscQueryHost(OscQueryHostOptions options, MulticastService? multicastService = null)
    {
        _options = options;
        _root = options.Root ?? new OscQueryNode { FullPath = "/", Access = AccessValues.NoValue, Contents = new() };
        HttpPort = options.HttpPort != 0 ? options.HttpPort : SocketUtils.FindAvailableTcpPort(options.Address);
        OscPort = options.OscPort != 0 ? options.OscPort : SocketUtils.FindAvailableUdpPort(options.Address);

        HostInfo = new OscQueryHostInfo
        {
            Name = options.ServiceName,
            OscIp = options.Address,
            OscPort = OscPort,
            OscTransport = options.OscTransport,
            Extensions = options.Extensions
        };

        var webServerUrl = $"http://{options.HttpBind}:{HttpPort}/";
        _httpServer = new WebServer(o => o
                .WithUrlPrefix(webServerUrl)
                .WithMode(HttpListenerMode.EmbedIO))
            .WithModule(new ActionModule("/", HttpVerbs.Get, HandleRequest));
        Logger.Information("Configured webserver for url {Url}", webServerUrl);

        _ownsMulticastService = multicastService == null;
        _multicastService = multicastService ?? OscQueryDiscovery.CreateMulticastService();
        _serviceDiscovery = new ServiceDiscovery(_multicastService);
    }

    public void Start()
    {
        if (_started) return;
        _started = true;

        ErrorHandledTask.Run(() => _httpServer.RunAsync(_cts.Token));
        Logger.Information("HTTP Server started!");

        if (_ownsMulticastService) _multicastService.Start();

        _serviceDiscovery.Advertise(new ServiceProfile(_options.ServiceName, OscQueryDiscovery.ServiceType, HttpPort, [_options.Address]));
        if (_options.AdvertiseOsc)
            _serviceDiscovery.Advertise(new ServiceProfile(_options.ServiceName, OscServiceType, OscPort, [_options.Address]));
    }

    private Task HandleRequest(IHttpContext ctx)
    {
        var query = ctx.Request.Url.Query.TrimStart('?');
        if (query == "HOST_INFO")
            return SendJson(ctx, JsonSerializer.Serialize(HostInfo, ModelsSourceGenerationContext.Default.OscQueryHostInfo));

        var node = Root.Find(Uri.UnescapeDataString(ctx.RequestedPath));
        if (node == null) throw HttpException.NotFound();

        if (query.Length == 0)
            return SendJson(ctx, JsonSerializer.Serialize(node, ModelsSourceGenerationContext.Default.OscQueryNode));

        // A single attribute was requested, e.g. /foo?VALUE
        using var document = JsonSerializer.SerializeToDocument(node, ModelsSourceGenerationContext.Default.OscQueryNode);
        if (!document.RootElement.TryGetProperty(query, out var attribute)) throw HttpException.NotFound();
        return SendJson(ctx, $"{{\"{query}\":{attribute.GetRawText()}}}");
    }

    private static Task SendJson(IHttpContext ctx, string json) =>
        ctx.SendStringAsync(json, MediaTypeNames.Application.Json, Encoding.UTF8);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Logger.Debug("Disposing OscQueryHost");

        // Sends the mDNS goodbye, needs the multicast service to still be running
        try
        {
            _serviceDiscovery.Unadvertise();
        }
        catch (Exception ex)
        {
            // e.g. a shared multicast service that was never started or already stopped
            Logger.Warning(ex, "Failed to send mDNS goodbye");
        }
        _serviceDiscovery.Dispose();
        _cts.Cancel();
        _httpServer.Dispose();
        if (_ownsMulticastService) _multicastService.Dispose();
        _cts.Dispose();
    }
}
