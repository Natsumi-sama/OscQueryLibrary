using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using MeaMod.DNS.Multicast;
using OpenShock.MinimalEvents;
using OscQueryLibrary.Models;
using OscQueryLibrary.Utils;
using Serilog;

namespace OscQueryLibrary.VRChat;

/// <summary>
/// Makes VRChat or other compatible Clients send OSC to your app without an OSC router, and reads the current avatar's parameters.
/// Advertises an OSCQuery service that asks for <c>/avatar</c> messages and tracks the VR clients on the network.
/// </summary>
public class VrOscQueryServer : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext(typeof(VrOscQueryServer));

    public const string VrcClientInstancePrefix = "VRChat-Client-";

    private readonly MulticastService _multicastService;
    private readonly OscQueryHttpClient _httpClient;
    private bool _started;
    private bool _disposed;

    private volatile string? _pinnedInstanceName;
    private volatile OscQueryServiceInfo? _currentVrClient;
    // Serializes selection changes so FoundVrClient fires in the same order the selection changed
    private readonly SemaphoreSlim _selectionLock = new(1, 1);

    public struct ParameterUpdateArgs
    {
        public ImmutableDictionary<string, object?> Parameters { get; init; }
        public string AvatarId { get; init; }
    }

    /// <summary>
    /// The OSCQuery service VRChat reads to learn where to send OSC.
    /// </summary>
    public OscQueryHost Host { get; }

    /// <summary>
    /// Discovery of VRChat clients, only resolves services named <c>VRChat-Client-*</c>.
    /// </summary>
    public OscQueryDiscovery Discovery { get; }

    /// <summary>
    /// Port VRChat sends OSC to, bind your OSC receiver to it.
    /// </summary>
    public ushort OscReceivePort => Host.OscPort;

    public IAsyncMinimalEventObservable<ParameterUpdateArgs> ParameterUpdate => _parameterUpdate;
    private readonly AsyncMinimalEvent<ParameterUpdateArgs> _parameterUpdate = new();

    /// <summary>
    /// Fired with the OSC endpoint of the VRChat client this server is now using.
    /// When several clients are on the network, this only fires for the selected one (see <see cref="CurrentVrClient"/>).
    /// </summary>
    public IAsyncMinimalEventObservable<IPEndPoint> FoundVrClient => _foundVrClient;
    private readonly AsyncMinimalEvent<IPEndPoint> _foundVrClient = new();

    /// <summary>
    /// Fired for every VRChat client found on the network, whether or not it gets selected.
    /// </summary>
    public IAsyncMinimalEventObservable<OscQueryServiceInfo> VrClientDiscovered => _vrClientDiscovered;
    private readonly AsyncMinimalEvent<OscQueryServiceInfo> _vrClientDiscovered = new();

    /// <summary>
    /// Fired when a VRChat client sent an mDNS goodbye or stopped responding.
    /// </summary>
    public IAsyncMinimalEventObservable<OscQueryServiceInfo> VrClientLost => _vrClientLost;
    private readonly AsyncMinimalEvent<OscQueryServiceInfo> _vrClientLost = new();

    /// <summary>
    /// All VRChat clients currently known.
    /// </summary>
    public IReadOnlyCollection<OscQueryServiceInfo> VrcClients => Discovery.Services;

    /// <summary>
    /// The VRChat client parameters are fetched from. Unless pinned via <see cref="SelectVrClient"/>,
    /// a client on this machine is preferred, then the most recently discovered one.
    /// </summary>
    public OscQueryServiceInfo? CurrentVrClient => _currentVrClient;

    /// <summary>
    /// Creates a new OSCQuery service that asks VRChat to send OSC to your app and tracks VRChat clients on the network.
    /// </summary>
    /// <param name="serviceName">Name of the OSCQuery service</param>
    /// <param name="serviceIpAddress">IP address to bind the OSCQuery server to</param>
    /// <param name="oscQueryServerBind">Address to bind the OSCQuery server to</param>
    /// <param name="instanceFilter">A filter to apply to instance names, by default filters for instances starting with the "VRChat-Client-" instance prefix</param>
    public VrOscQueryServer(string serviceName, IPAddress serviceIpAddress, string oscQueryServerBind = "*", Func<string, bool>? instanceFilter = null)
    {
        _httpClient = new OscQueryHttpClient(OscQueryHttpClient.CreateHttpClient($"OscQuery-{serviceName}"));

        // One mDNS socket for both advertising and discovery
        _multicastService = OscQueryDiscovery.CreateMulticastService();

        Host = new OscQueryHost(new OscQueryHostOptions
        {
            ServiceName = serviceName,
            Address = serviceIpAddress,
            HttpBind = oscQueryServerBind,
            Root = CreateRootNode()
        }, _multicastService);

        Discovery = new OscQueryDiscovery(new OscQueryDiscoveryOptions
        {
            InstanceFilter = instanceFilter ?? (name => name.StartsWith(VrcClientInstancePrefix, StringComparison.OrdinalIgnoreCase)),
            HttpClient = _httpClient
        }, _multicastService);

        // Subscribing to a fresh event completes synchronously
        Discovery.ServiceDiscovered.SubscribeAsync(OnVrClientDiscovered).AsTask().GetAwaiter().GetResult();
        Discovery.ServiceLost.SubscribeAsync(OnVrClientLost).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Starts advertising and looking for VRChat clients, no events fire before this.
    /// </summary>
    public void Start()
    {
        if (_started) return;
        _started = true;

        _multicastService.Start();
        Host.Start();
        Discovery.Start();
    }

    // VRChat only sends what the tree asks for, /avatar gets us avatar changes and parameters
    private static OscQueryNode CreateRootNode() => new()
    {
        Description = "root node",
        FullPath = "/",
        Access = AccessValues.NoValue,
        Contents = new Dictionary<string, OscQueryNode>
        {
            ["avatar"] = new()
            {
                FullPath = "/avatar",
                Access = AccessValues.NoValue,
                Contents = new Dictionary<string, OscQueryNode>
                {
                    ["change"] = new()
                    {
                        Description = "",
                        FullPath = "/avatar/change",
                        Access = AccessValues.Write,
                        Type = "s"
                    }
                }
            }
        }
    };

    private async Task OnVrClientDiscovered(OscQueryServiceInfo client)
    {
        await _vrClientDiscovered.InvokeSafeAsync(client, Logger, nameof(VrClientDiscovered)).ConfigureAwait(false);
        await UpdateCurrentVrClient().ConfigureAwait(false);
    }

    private async Task OnVrClientLost(OscQueryServiceInfo client)
    {
        if (_pinnedInstanceName == client.InstanceName) _pinnedInstanceName = null;
        await _vrClientLost.InvokeSafeAsync(client, Logger, nameof(VrClientLost)).ConfigureAwait(false);
        await UpdateCurrentVrClient().ConfigureAwait(false);
    }

    /// <summary>
    /// Pin a specific VRChat client instead of picking one automatically, pass null to go back to automatic selection.
    /// The pin is dropped once that client disappears. Do not await this from inside a <see cref="FoundVrClient"/> handler.
    /// </summary>
    public Task SelectVrClient(OscQueryServiceInfo? client)
    {
        _pinnedInstanceName = client?.InstanceName;
        return UpdateCurrentVrClient();
    }

    private OscQueryServiceInfo? PickVrClient()
    {
        var clients = Discovery.Services;
        var pinned = _pinnedInstanceName;
        if (pinned != null)
        {
            var pinnedClient = clients.FirstOrDefault(c => c.InstanceName == pinned);
            if (pinnedClient != null) return pinnedClient;
        }

        // A client on this machine is almost always the one we want, otherwise take the newest
        return clients
            .OrderByDescending(c => c.IsLocal)
            .ThenByDescending(c => c.DiscoveredAt)
            .FirstOrDefault();
    }

    private async Task UpdateCurrentVrClient()
    {
        OscQueryServiceInfo? selected;
        await _selectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            selected = PickVrClient();
            if (ReferenceEquals(selected, _currentVrClient)) return;
            _currentVrClient = selected;

            if (selected == null)
            {
                Logger.Information("No VRChat client available");
                return;
            }

            Logger.Information("Using VRChat client {Client}", selected);
            await _foundVrClient.InvokeSafeAsync(selected.OscEndPoint, Logger, nameof(FoundVrClient)).ConfigureAwait(false);
        }
        finally
        {
            _selectionLock.Release();
        }

        await RefreshParameters().ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches the current avatar's parameters from the selected VRChat client and fires <see cref="ParameterUpdate"/>.
    /// </summary>
    public async Task RefreshParameters()
    {
        var client = _currentVrClient;
        if (client == null)
            return;

        Logger.Debug("Refreshing parameters from {Client}", client);

        OscQueryNode root;
        try
        {
            root = await _httpClient.GetNodeAsync(client.HttpEndPoint).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Logger.Error(ex, "HTTP request failed");
            // Drop it, this falls back to the next best client if there is one
            await Discovery.ReportUnreachable(client).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Unexpected exception while receiving parameters via osc query");
            return;
        }

        var parameters = ParseParameters(root);
        if (parameters == null) return;
        // The selection changed while fetching, the new client triggers its own refresh
        if (!ReferenceEquals(client, _currentVrClient)) return;
        await _parameterUpdate.InvokeSafeAsync(parameters.Value, Logger, nameof(ParameterUpdate)).ConfigureAwait(false);
    }

    private static ParameterUpdateArgs? ParseParameters(OscQueryNode root)
    {
        var parametersNode = root.Find("/avatar/parameters");
        if (parametersNode?.Contents == null)
        {
            Logger.Warning("Error no parameters found");
            return null;
        }

        var parameters = new Dictionary<string, object?>();
        foreach (var node in parametersNode.Descendants())
        {
            if (node.Contents != null) continue;
            parameters[node.FullPath] = node.Value is { Length: > 0 } value ? value[0] : null;
        }

        var change = root.Find("/avatar/change")?.Value;
        var avatarId = change is { Length: > 0 } && change[0].ValueKind == JsonValueKind.String
            ? change[0].GetString() ?? string.Empty
            : string.Empty;

        return new ParameterUpdateArgs
        {
            Parameters = parameters.ToImmutableDictionary(),
            AvatarId = avatarId
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Logger.Debug("Disposing VrOscQueryServer");

        Discovery.Dispose();
        // Sends the mDNS goodbye to VRChat, so before the multicast service goes away
        Host.Dispose();
        _multicastService.Dispose();

        GC.SuppressFinalize(this);
    }
}
