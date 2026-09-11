using System.Collections.Concurrent;
using System.Net;
using MeaMod.DNS.Model;
using MeaMod.DNS.Multicast;
using MeaMod.DNS.Server;
using OpenShock.MinimalEvents;
using OscQueryLibrary.Models;
using OscQueryLibrary.Utils;
using Serilog;

namespace OscQueryLibrary;

public sealed class OscQueryDiscoveryOptions
{
    /// <summary>
    /// Only services whose mDNS instance name passes this filter are resolved. Null accepts every service.
    /// </summary>
    public Func<string, bool>? InstanceFilter { get; init; }

    public OscQueryHttpClient? HttpClient { get; init; }

    /// <summary>
    /// How often known services are pinged and the network is queried again. A service is dropped after two failed
    /// checks in a row. Null disables health checks, services are then only dropped on an mDNS goodbye or
    /// <see cref="OscQueryDiscovery.ReportUnreachable"/>.
    /// </summary>
    public TimeSpan? HealthCheckInterval { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Finds OSCQuery services (<c>_oscjson._tcp</c>) on the local network via mDNS and tracks which are still around.
/// </summary>
public sealed class OscQueryDiscovery : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<OscQueryDiscovery>();

    public const string ServiceType = "_oscjson._tcp";
    private static readonly DomainName ServiceDomain = ServiceType + ".local";
    private const int MaxFailedHealthChecks = 2;

    private readonly OscQueryDiscoveryOptions _options;
    private readonly OscQueryHttpClient _httpClient;
    private readonly MulticastService _multicastService;
    private readonly bool _ownsMulticastService;
    private readonly CancellationTokenSource _cts = new();

    // Resolved services keyed by service id
#if NET10_0_OR_GREATER
    private readonly Lock _servicesLock = new();
#else 
    private readonly object _servicesLock = new();
#endif

    private readonly Dictionary<string, OscQueryServiceInfo> _services = new();
    private readonly ConcurrentSet<string> _resolving = new();
    // Advertised but not answering over HTTP, e.g. stale records of a crashed app. Retried with backoff instead of on every answer.
    private readonly ConcurrentDictionary<string, (DateTime RetryAfter, int Failures)> _unreachable = new();

    private bool _started;
    private bool _disposed;

    /// <summary>
    /// Fired once a service was found and its HOST_INFO could be read.
    /// </summary>
    public IAsyncMinimalEventObservable<OscQueryServiceInfo> ServiceDiscovered => _serviceDiscovered;
    private readonly AsyncMinimalEvent<OscQueryServiceInfo> _serviceDiscovered = new();

    /// <summary>
    /// Fired when a service sent an mDNS goodbye or stopped responding.
    /// </summary>
    public IAsyncMinimalEventObservable<OscQueryServiceInfo> ServiceLost => _serviceLost;
    private readonly AsyncMinimalEvent<OscQueryServiceInfo> _serviceLost = new();

    /// <summary>
    /// Snapshot of all services currently known.
    /// </summary>
    public IReadOnlyCollection<OscQueryServiceInfo> Services
    {
        get
        {
            lock (_servicesLock) return [.. _services.Values];
        }
    }

    /// <param name="options">Discovery options, defaults are used when null.</param>
    /// <param name="multicastService">
    /// mDNS service to share with e.g. an <see cref="OscQueryHost"/>. When passed, the caller owns it and must start it
    /// before calling <see cref="Start"/>. When null, the discovery creates and manages its own.
    /// </param>
    public OscQueryDiscovery(OscQueryDiscoveryOptions? options = null, MulticastService? multicastService = null)
    {
        _options = options ?? new OscQueryDiscoveryOptions();
        _httpClient = _options.HttpClient ?? new OscQueryHttpClient();
        _ownsMulticastService = multicastService == null;
        _multicastService = multicastService ?? CreateMulticastService();

        _multicastService.NetworkInterfaceDiscovered += OnNetworkInterfaceDiscovered;
        _multicastService.AnswerReceived += OnAnswerReceived;
    }

    /// <summary>
    /// Creates a <see cref="MulticastService"/> configured for OSCQuery, use it when sharing one between discovery and host.
    /// </summary>
    public static MulticastService CreateMulticastService() => new()
    {
        UseIpv6 = false,
        // MeaMod drops any message identical to one received in the last minute. mDNS queries are byte-identical, so
        // with this on a host stays silent for apps that query shortly after another one, and discovery ignores the
        // repeated answers it needs to find a service again after dropping it. Everything here is idempotent anyway.
        IgnoreDuplicateMessages = false
    };

    public void Start()
    {
        if (_started) return;
        _started = true;

        // An owned service queries once its network interfaces come up, a shared one is already running
        if (_ownsMulticastService) _multicastService.Start();
        else Query();

        if (_options.HealthCheckInterval is { } interval)
            ErrorHandledTask.Run(() => HealthCheckLoop(interval, _cts.Token));
    }

    /// <summary>
    /// Asks the network for OSCQuery services, e.g. to find new ones sooner than the next health check would.
    /// </summary>
    public void Query()
    {
        try
        {
            SendQuery(ServiceDomain, DnsType.ANY);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to send mDNS query");
        }
    }

    private static int _lastQueryId = Environment.TickCount;

    private void SendQuery(DomainName name, DnsType type)
    {
        // A unique id makes every query unique. MeaMod based responders (VRChat's included) otherwise ignore a query
        // identical to one they saw in the last minute, e.g. the same query from another app. Responders must ignore the id.
        var message = new Message { Id = (ushort)Interlocked.Increment(ref _lastQueryId) };
        message.Questions.Add(new Question { Name = name, Class = DnsClass.IN, Type = type });
        _multicastService.SendQuery(message);
    }

    /// <summary>
    /// Drops a service that failed to respond to your own request, e.g. a failed <see cref="OscQueryHttpClient.GetNodeAsync"/>.
    /// It is found again once it answers the next mDNS query.
    /// </summary>
    public async Task ReportUnreachable(OscQueryServiceInfo service)
    {
        await RemoveService(service.ServiceId, "stopped responding", service).ConfigureAwait(false);
    }

    private void OnNetworkInterfaceDiscovered(object? sender, NetworkInterfaceEventArgs args)
    {
        Logger.Debug("Network interface discovered");
        Query();
    }

    private void OnAnswerReceived(object? sender, MessageEventArgs args)
    {
        ErrorHandledTask.Run(() => ProcessAnswer(args));
    }

    private async Task ProcessAnswer(MessageEventArgs args)
    {
        try
        {
            // Responses to our queries carry SRV/A records as additional records, unsolicited announcements as answers
            var records = args.Message.Answers.Concat(args.Message.AdditionalRecords).ToArray();
            var srvRecords = records.OfType<SRVRecord>().Where(r => IsOscQueryInstance(r.Name)).ToArray();

            foreach (var ptr in records.OfType<PTRRecord>())
            {
                if (ptr.Name != ServiceDomain) continue;

                if (ptr.TTL == TimeSpan.Zero)
                {
                    await RemoveServices(ptr.DomainName, "sent goodbye").ConfigureAwait(false);
                    continue;
                }

                // Some responders only send the PTR record, ask for the SRV record of instances we don't know yet
                if (srvRecords.Any(s => s.Name == ptr.DomainName) || IsKnownInstance(ptr.DomainName)) continue;
                if (_options.InstanceFilter?.Invoke(ptr.DomainName.Labels[0]) == false) continue;
                SendQuery(ptr.DomainName, DnsType.SRV);
            }

            foreach (var srv in srvRecords)
            {
                var instanceName = srv.Name.Labels[0];
                var serviceId = $"{srv.CanonicalName}:{srv.Port}";

                if (srv.TTL == TimeSpan.Zero)
                {
                    _unreachable.TryRemove(serviceId, out _);
                    await RemoveService(serviceId, "sent goodbye").ConfigureAwait(false);
                    continue;
                }

                if (_options.InstanceFilter?.Invoke(instanceName) == false) continue;
                if (_unreachable.TryGetValue(serviceId, out var unreachable) && DateTime.UtcNow < unreachable.RetryAfter) continue;

                lock (_servicesLock)
                {
                    if (_services.ContainsKey(serviceId)) continue;
                }

                if (!_resolving.Add(serviceId)) continue;
                try
                {
                    await ResolveService(serviceId, srv.Name, instanceName, GetAddresses(records, srv.Target), srv.Port)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _resolving.Remove(serviceId);
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // disposed
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to process mDNS answer from {RemoteEndPoint}", args.RemoteEndPoint);
        }
    }

    private static bool IsOscQueryInstance(DomainName name)
    {
        var labels = name.Labels;
        return labels.Count >= 4
               && string.Equals(labels[1], "_oscjson", StringComparison.OrdinalIgnoreCase)
               && string.Equals(labels[2], "_tcp", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsKnownInstance(DomainName instanceDomain)
    {
        lock (_servicesLock) return _services.Values.Any(s => s.InstanceDomain == instanceDomain);
    }

    private static IPAddress[] GetAddresses(IReadOnlyCollection<ResourceRecord> records, DomainName target)
    {
        var aRecords = records.OfType<ARecord>().ToArray();
        var matching = aRecords.Where(r => r.Name == target).Select(r => r.Address).Distinct().ToArray();
        // Fall back to every A record in the message in case the responder names them differently
        return matching.Length > 0 ? matching : aRecords.Select(r => r.Address).Distinct().ToArray();
    }

    private async Task ResolveService(string serviceId, DomainName instanceDomain, string instanceName, IPAddress[] addresses, ushort port)
    {
        if (addresses.Length == 0)
        {
            Logger.Debug("No addresses for OSCQuery service {InstanceName}", instanceName);
            return;
        }

        var localAddresses = addresses.Where(NetworkUtils.IsLocalAddress).ToHashSet();

        // A service can advertise several addresses (multiple NICs), try local ones first and keep the first that answers
        foreach (var address in addresses.OrderByDescending(localAddresses.Contains))
        {
            var endPoint = new IPEndPoint(address, port);
            OscQueryHostInfo hostInfo;
            try
            {
                hostInfo = await _httpClient.GetHostInfoAsync(endPoint, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!_cts.IsCancellationRequested)
            {
                Logger.Debug("Failed to fetch HOST_INFO of {InstanceName} from {EndPoint}: {Error}", instanceName, endPoint, ex.Message);
                continue;
            }

            var service = new OscQueryServiceInfo
            {
                InstanceName = instanceName,
                ServiceId = serviceId,
                InstanceDomain = instanceDomain,
                HttpEndPoint = endPoint,
                HostInfo = hostInfo,
                IsLocal = localAddresses.Count > 0,
                DiscoveredAt = DateTimeOffset.UtcNow
            };

            _unreachable.TryRemove(serviceId, out _);
            lock (_servicesLock) _services[serviceId] = service;
            Logger.Information("Discovered OSCQuery service {Service}", service);
            await _serviceDiscovered.InvokeSafeAsync(service, Logger, nameof(ServiceDiscovered)).ConfigureAwait(false);
            return;
        }

        var failures = _unreachable.TryGetValue(serviceId, out var previous) ? previous.Failures + 1 : 1;
        var retryIn = TimeSpan.FromSeconds(Math.Min(300, 15 * Math.Pow(2, failures - 1)));
        _unreachable[serviceId] = (DateTime.UtcNow + retryIn, failures);

        // Only warn once, stale records keep getting announced until their TTL runs out
        if (failures == 1)
            Logger.Warning("OSCQuery service {InstanceName} is not reachable on {Addresses} port {Port}, retrying in {RetryIn}",
                instanceName, addresses, port, retryIn);
        else
            Logger.Debug("OSCQuery service {InstanceName} still not reachable, retrying in {RetryIn}", instanceName, retryIn);
    }

    private async Task RemoveServices(DomainName instanceDomain, string reason)
    {
        string[] serviceIds;
        lock (_servicesLock)
            serviceIds = _services.Values.Where(s => s.InstanceDomain == instanceDomain).Select(s => s.ServiceId).ToArray();

        foreach (var serviceId in serviceIds)
            await RemoveService(serviceId, reason).ConfigureAwait(false);
    }

    private async Task RemoveService(string serviceId, string reason, OscQueryServiceInfo? expected = null)
    {
        OscQueryServiceInfo? removed;
        lock (_servicesLock)
        {
            if (!_services.TryGetValue(serviceId, out removed)) return;
            // The service may have been replaced by a newer announcement in the meantime
            if (expected != null && !ReferenceEquals(removed, expected)) return;
            _services.Remove(serviceId);
        }

        Logger.Information("Lost OSCQuery service {Service}, it {Reason}", removed, reason);
        await _serviceLost.InvokeSafeAsync(removed, Logger, nameof(ServiceLost)).ConfigureAwait(false);
        // Look for services that restarted or that we missed
        Query();
    }

    private async Task HealthCheckLoop(TimeSpan interval, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                await Task.WhenAll(Services.Select(s => CheckService(s, cancellationToken))).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            Query();
        }
    }

    private async Task CheckService(OscQueryServiceInfo service, CancellationToken cancellationToken)
    {
        try
        {
            await _httpClient.GetHostInfoAsync(service.HttpEndPoint, cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref service.FailedHealthChecks, 0);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            var failed = Interlocked.Increment(ref service.FailedHealthChecks);
            Logger.Debug(ex, "Health check {Failed}/{Max} of {Service} failed", failed, MaxFailedHealthChecks, service);
            if (failed >= MaxFailedHealthChecks)
                await RemoveService(service.ServiceId, "stopped responding", service).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _multicastService.NetworkInterfaceDiscovered -= OnNetworkInterfaceDiscovered;
        _multicastService.AnswerReceived -= OnAnswerReceived;
        if (_ownsMulticastService) _multicastService.Dispose();
        _cts.Dispose();
    }
}
