using System.Collections.Concurrent;
using System.Text.Json;
using OscQueryLibrary;
using OscQueryLibrary.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace OscQueryExplorer;

internal sealed class Explorer
{
    public static readonly TimeSpan ChangeHighlight = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdleRedraw = TimeSpan.FromMilliseconds(500);

    private const int FooterHeight = 1;
    private const int InfoRows = 6;
    private const int TypeWidth = 6;
    private const int AccessWidth = 7;

    private static readonly Style SelectedFocused = new(Color.Black, Color.Aqua);
    private static readonly Style SelectedUnfocused = new(Color.White, Color.Grey23);
    private static readonly Style Dim = new(Color.Grey);

    private readonly OscQueryDiscovery _discovery;
    private readonly OscQueryHttpClient _httpClient = new();
    private readonly ConcurrentDictionary<string, TreeSnapshot> _trees = new();
    // Collapsed paths per service, only touched from the UI loop
    private readonly Dictionary<string, HashSet<string>> _collapsed = new();
    private readonly SemaphoreSlim _wakeFetcher = new(0);

    private volatile string? _selectedServiceId;
    private volatile bool _autoRefresh = true;
    private volatile bool _dirty = true;
    private Pane _focus = Pane.Services;
    private int _treeCursor;
    private int _treeScroll;
    private int _servicesScroll;

    public Explorer(OscQueryDiscovery discovery)
    {
        _discovery = discovery;
    }

    public async Task RunAsync()
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var fetcher = Task.Run(() => FetchLoop(cts.Token));

        await AnsiConsole.Live(new Text(string.Empty))
            .AutoClear(true)
            .Overflow(VerticalOverflow.Crop)
            .Cropping(VerticalOverflowCropping.Bottom)
            .StartAsync(async ctx =>
            {
                var lastRender = DateTime.MinValue;
                var lastShape = string.Empty;
                while (!cts.IsCancellationRequested)
                {
                    var frame = BuildFrame();
                    while (Console.KeyAvailable)
                    {
                        if (!HandleKey(Console.ReadKey(intercept: true), frame))
                        {
                            cts.Cancel();
                            break;
                        }

                        _dirty = true;
                        frame = BuildFrame();
                    }

                    // Redraw on input, new data, resize or service list changes, and regularly so change highlights fade
                    var shape = $"{frame.Width}x{frame.Height}|{string.Join(",", frame.Services.Select(s => s.ServiceId))}";
                    if (_dirty || shape != lastShape || DateTime.UtcNow - lastRender > IdleRedraw)
                    {
                        _dirty = false;
                        lastShape = shape;
                        lastRender = DateTime.UtcNow;
                        ctx.UpdateTarget(Render(frame));
                    }

                    try
                    {
                        await Task.Delay(30, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // quitting
                    }
                }
            });

        await fetcher;
    }

    public async Task RenderSnapshotAsync(TimeSpan discoveryTime, string? filter)
    {
        await Task.Delay(discoveryTime);
        var service = SortedServices()
            .FirstOrDefault(s => filter == null || s.InstanceName.Contains(filter, StringComparison.OrdinalIgnoreCase));
        if (service != null)
        {
            _selectedServiceId = service.ServiceId;
            _focus = Pane.Tree;
            await FetchTree(service, CancellationToken.None);
        }
        else if (filter != null)
        {
            AnsiConsole.MarkupLine($"[red]No service matching '{Markup.Escape(filter)}'[/]");
        }

        AnsiConsole.Write(Render(BuildFrame()));
    }

    #region Data

    private IReadOnlyList<OscQueryServiceInfo> SortedServices() =>
    [
        .. _discovery.Services
            .OrderBy(s => s.InstanceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.ServiceId, StringComparer.Ordinal)
    ];

    private void WakeFetcher()
    {
        if (_wakeFetcher.CurrentCount == 0) _wakeFetcher.Release();
    }

    private async Task FetchLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var selectedId = _selectedServiceId;
            var service = selectedId == null ? null : _discovery.Services.FirstOrDefault(s => s.ServiceId == selectedId);
            if (service != null) await FetchTree(service, cancellationToken);

            try
            {
                await _wakeFetcher.WaitAsync(_autoRefresh ? RefreshInterval : Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task FetchTree(OscQueryServiceInfo service, CancellationToken cancellationToken)
    {
        _trees.TryGetValue(service.ServiceId, out var previous);
        try
        {
            var root = await _httpClient.GetNodeAsync(service.HttpEndPoint, "/", cancellationToken);
            _trees[service.ServiceId] = TreeSnapshot.Create(root, previous);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            // Keep showing the last good tree along with the error
            _trees[service.ServiceId] = (previous ?? TreeSnapshot.Empty) with { Error = ex.Message, FetchedAt = DateTime.UtcNow };
        }

        _dirty = true;
    }

    private HashSet<string> CollapsedFor(string serviceId)
    {
        if (!_collapsed.TryGetValue(serviceId, out var set))
            _collapsed[serviceId] = set = new HashSet<string>(StringComparer.Ordinal);
        return set;
    }

    #endregion

    #region Frame & input

    private sealed record Frame(
        IReadOnlyList<OscQueryServiceInfo> Services,
        int SelectedIndex,
        OscQueryServiceInfo? Selected,
        TreeSnapshot? Tree,
        IReadOnlyList<TreeRow> Rows,
        int Width,
        int Height,
        int ServicesWidth,
        int VisibleServices,
        int VisibleRows);

    private Frame BuildFrame()
    {
        var width = AnsiConsole.Profile.Width;
        // One line short of the console, a full height frame makes some terminals scroll
        var height = Math.Max(10, AnsiConsole.Profile.Height - 1);

        var services = SortedServices();
        var selectedIndex = services.Count == 0 ? -1 : Math.Max(0, IndexOf(services, _selectedServiceId));
        var selected = selectedIndex >= 0 ? services[selectedIndex] : null;
        if (selected?.ServiceId != _selectedServiceId)
        {
            _selectedServiceId = selected?.ServiceId;
            _treeCursor = _treeScroll = 0;
            WakeFetcher();
        }

        TreeSnapshot? tree = null;
        var rows = new List<TreeRow>();
        if (selected != null && _trees.TryGetValue(selected.ServiceId, out tree) && tree.Root != null)
            Flatten(tree.Root, "/", "/", 0, CollapsedFor(selected.ServiceId), rows);

        var panelLines = height - FooterHeight - 2;
        var visibleServices = Math.Max(1, panelLines / 2);
        var visibleRows = Math.Max(1, panelLines - InfoRows - 2);

        _treeCursor = Math.Clamp(_treeCursor, 0, Math.Max(0, rows.Count - 1));
        _treeScroll = ScrollToShow(_treeCursor, _treeScroll, visibleRows, rows.Count);
        _servicesScroll = ScrollToShow(Math.Max(0, selectedIndex), _servicesScroll, visibleServices, services.Count);

        return new Frame(services, selectedIndex, selected, tree, rows, width, height,
            Math.Clamp(width / 3, 28, 48), visibleServices, visibleRows);
    }

    private static int IndexOf(IReadOnlyList<OscQueryServiceInfo> services, string? serviceId)
    {
        for (var i = 0; i < services.Count; i++)
            if (services[i].ServiceId == serviceId) return i;
        return -1;
    }

    private static int ScrollToShow(int index, int scroll, int visible, int count)
    {
        if (index < scroll) scroll = index;
        if (index >= scroll + visible) scroll = index - visible + 1;
        return Math.Clamp(scroll, 0, Math.Max(0, count - visible));
    }

    private static void Flatten(OscQueryNode node, string name, string path, int depth, HashSet<string> collapsed, List<TreeRow> rows)
    {
        var hasChildren = node.Contents is { Count: > 0 };
        var isCollapsed = hasChildren && collapsed.Contains(path);
        rows.Add(new TreeRow(node, name, path, depth, hasChildren, isCollapsed));
        if (!hasChildren || isCollapsed) return;

        foreach (var (key, child) in node.Contents!.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
            Flatten(child, key, TreeSnapshot.ChildPath(path, key), depth + 1, collapsed, rows);
    }

    /// <returns>False to quit.</returns>
    private bool HandleKey(ConsoleKeyInfo key, Frame frame)
    {
        switch (key.Key)
        {
            case ConsoleKey.Q or ConsoleKey.Escape:
                return false;
            case ConsoleKey.Tab:
                _focus = _focus == Pane.Services ? Pane.Tree : Pane.Services;
                break;
            case ConsoleKey.R:
                _discovery.Query();
                WakeFetcher();
                break;
            case ConsoleKey.A:
                _autoRefresh = !_autoRefresh;
                WakeFetcher();
                break;
            case ConsoleKey.E when frame.Selected != null:
                CollapsedFor(frame.Selected.ServiceId).Clear();
                break;
            case ConsoleKey.C when frame.Selected != null && frame.Tree?.Root != null:
            {
                var collapsed = CollapsedFor(frame.Selected.ServiceId);
                collapsed.Clear();
                var all = new List<TreeRow>();
                Flatten(frame.Tree.Root, "/", "/", 0, collapsed, all);
                foreach (var row in all.Where(r => r.HasChildren && r.Depth > 0)) collapsed.Add(row.Path);
                _treeCursor = 0;
                break;
            }
            case ConsoleKey.UpArrow or ConsoleKey.K:
                Move(frame, -1);
                break;
            case ConsoleKey.DownArrow or ConsoleKey.J:
                Move(frame, 1);
                break;
            case ConsoleKey.PageUp:
                Move(frame, -(_focus == Pane.Tree ? frame.VisibleRows : frame.VisibleServices));
                break;
            case ConsoleKey.PageDown:
                Move(frame, _focus == Pane.Tree ? frame.VisibleRows : frame.VisibleServices);
                break;
            case ConsoleKey.Home:
                Move(frame, -100_000);
                break;
            case ConsoleKey.End:
                Move(frame, 100_000);
                break;
            case ConsoleKey.LeftArrow or ConsoleKey.H when _focus == Pane.Tree:
                CollapseOrGoToParent(frame);
                break;
            case ConsoleKey.LeftArrow or ConsoleKey.H:
                break;
            case ConsoleKey.RightArrow or ConsoleKey.L or ConsoleKey.Enter or ConsoleKey.Spacebar when _focus == Pane.Services:
                _focus = Pane.Tree;
                break;
            case ConsoleKey.RightArrow or ConsoleKey.L:
                ExpandOrGoToChild(frame);
                break;
            case ConsoleKey.Enter or ConsoleKey.Spacebar:
                Toggle(frame);
                break;
        }

        return true;
    }

    private void Move(Frame frame, int delta)
    {
        if (_focus == Pane.Tree)
        {
            _treeCursor = Math.Clamp(_treeCursor + delta, 0, Math.Max(0, frame.Rows.Count - 1));
            return;
        }

        if (frame.Services.Count == 0) return;
        var index = Math.Clamp(frame.SelectedIndex + delta, 0, frame.Services.Count - 1);
        if (index == frame.SelectedIndex) return;
        _selectedServiceId = frame.Services[index].ServiceId;
        _treeCursor = _treeScroll = 0;
        WakeFetcher();
    }

    private void CollapseOrGoToParent(Frame frame)
    {
        if (frame.Selected == null || frame.Rows.Count == 0) return;
        var row = frame.Rows[_treeCursor];
        if (row.HasChildren && !row.Collapsed)
        {
            CollapsedFor(frame.Selected.ServiceId).Add(row.Path);
            return;
        }

        for (var i = _treeCursor - 1; i >= 0; i--)
        {
            if (frame.Rows[i].Depth >= row.Depth) continue;
            _treeCursor = i;
            return;
        }
    }

    private void ExpandOrGoToChild(Frame frame)
    {
        if (frame.Selected == null || frame.Rows.Count == 0) return;
        var row = frame.Rows[_treeCursor];
        if (!row.HasChildren) return;
        if (row.Collapsed) CollapsedFor(frame.Selected.ServiceId).Remove(row.Path);
        else _treeCursor++;
    }

    private void Toggle(Frame frame)
    {
        if (frame.Selected == null || frame.Rows.Count == 0) return;
        var row = frame.Rows[_treeCursor];
        if (!row.HasChildren) return;
        var collapsed = CollapsedFor(frame.Selected.ServiceId);
        if (!collapsed.Remove(row.Path)) collapsed.Add(row.Path);
    }

    #endregion

    #region Rendering

    private FixedHeight Render(Frame frame)
    {
        var layout = new Layout("root").SplitRows(
            new Layout("main").SplitColumns(
                new Layout("services").Size(frame.ServicesWidth).Update(RenderServices(frame, frame.ServicesWidth - 4)),
                new Layout("details").Update(RenderDetails(frame, frame.Width - frame.ServicesWidth - 4))),
            new Layout("footer").Size(FooterHeight).Update(RenderFooter(frame)));

        return new FixedHeight(layout, frame.Height);
    }

    private Panel RenderServices(Frame frame, int innerWidth)
    {
        IRenderable content;
        if (frame.Services.Count == 0)
        {
            content = new Text("Searching for OSCQuery services...", Dim);
        }
        else
        {
            var lines = new List<IRenderable>();
            for (var i = _servicesScroll; i < Math.Min(frame.Services.Count, _servicesScroll + frame.VisibleServices); i++)
            {
                var service = frame.Services[i];
                var style = i == frame.SelectedIndex ? SelectionStyle(Pane.Services) : Style.Plain;
                lines.Add(new Text(Fit(service.InstanceName, innerWidth), style));
                var endpoint = $"  {service.HttpEndPoint}{(service.IsLocal ? "  local" : "")}";
                lines.Add(new Text(Fit(endpoint, innerWidth), i == frame.SelectedIndex ? style : Dim));
            }

            content = new Rows(lines);
        }

        return FramePanel(content, $"Services ({frame.Services.Count})", Pane.Services);
    }

    private Panel RenderDetails(Frame frame, int innerWidth)
    {
        var service = frame.Selected;
        if (service == null)
            return FramePanel(new Text("No service selected", Dim), "Details", Pane.Tree);

        var hostInfo = service.HostInfo;
        var tree = frame.Tree;
        var cursorRow = frame.Rows.Count > 0 ? frame.Rows[_treeCursor] : null;
        var extensions = hostInfo.Extensions?.Where(e => e.Value).Select(e => e.Key) ?? [];

        var lines = new List<IRenderable>
        {
            InfoLine("Name", hostInfo.Name ?? "-", innerWidth),
            InfoLine("HTTP", $"{service.HttpEndPoint}{(service.IsLocal ? "  (this machine)" : "")}", innerWidth),
            InfoLine("OSC", $"{service.OscEndPoint}  {hostInfo.OscTransport?.ToString() ?? "transport not specified"}", innerWidth),
            InfoLine("Extensions", string.Join(" ", extensions), innerWidth),
            tree switch
            {
                null => InfoLine("Tree", "loading...", innerWidth),
                { Error: not null } => InfoLine("Tree", $"error: {tree.Error}", innerWidth, new Style(Color.Red)),
                _ => InfoLine("Tree", $"{tree.NodeCount} nodes, updated {(DateTime.UtcNow - tree.FetchedAt).TotalSeconds:0.0}s ago" +
                                      (_autoRefresh ? "" : "  (auto refresh off)"), innerWidth)
            },
            InfoLine("Selected", cursorRow == null ? "-" : DescribeNode(cursorRow), innerWidth),
            new Rule().RuleStyle(Dim)
        };

        var pathWidth = Math.Clamp(frame.Rows.Count == 0 ? 0 : frame.Rows.Max(r => r.Depth * 2 + 2 + r.Name.Length) + 1,
            12, Math.Max(12, innerWidth / 2));
        var valueWidth = Math.Max(1, innerWidth - pathWidth - TypeWidth - AccessWidth);
        lines.Add(new Paragraph()
            .Append(Fit("Path", pathWidth), Dim)
            .Append(Fit("Type", TypeWidth), Dim)
            .Append(Fit("Access", AccessWidth), Dim)
            .Append(Fit("Value", valueWidth), Dim));

        var now = DateTime.UtcNow;
        for (var i = _treeScroll; i < Math.Min(frame.Rows.Count, _treeScroll + frame.VisibleRows); i++)
        {
            var row = frame.Rows[i];
            var selected = i == _treeCursor;
            var marker = row.HasChildren ? (row.Collapsed ? "▸ " : "▾ ") : "  ";
            tree!.Values.TryGetValue(row.Path, out var value);
            var changed = tree.ChangedAt.TryGetValue(row.Path, out var changedAt) && now - changedAt < ChangeHighlight;

            var baseStyle = selected ? SelectionStyle(Pane.Tree) : (Style?)null;
            lines.Add(new Paragraph()
                .Append(Fit(new string(' ', row.Depth * 2) + marker + row.Name, pathWidth),
                    baseStyle ?? (row.HasChildren ? new Style(Color.SkyBlue1) : Style.Plain))
                .Append(Fit(row.Node.Type ?? "", TypeWidth), baseStyle ?? Dim)
                .Append(Fit(FormatAccess(row.Node.Access), AccessWidth), baseStyle ?? AccessStyle(row.Node.Access))
                .Append(Fit(value ?? "", valueWidth), baseStyle ?? (changed ? new Style(Color.Yellow) : Style.Plain)));
        }

        return FramePanel(new Rows(lines), service.InstanceName, Pane.Tree);
    }

    private IRenderable RenderFooter(Frame frame)
    {
        var keys = $" ↑↓ move  Tab pane  ←→ fold  Enter toggle  E/C expand/collapse all  R refresh  A auto refresh ({(_autoRefresh ? "on" : "off")})  Q quit";
        return new Text(Fit(keys, frame.Width), Dim);
    }

    private Panel FramePanel(IRenderable content, string header, Pane pane) =>
        new Panel(content)
            .Header($" {Markup.Escape(header)} ")
            .Border(BoxBorder.Rounded)
            .BorderColor(_focus == pane ? Color.Aqua : Color.Grey)
            .Expand();

    private Style SelectionStyle(Pane pane) => _focus == pane ? SelectedFocused : SelectedUnfocused;

    private static Paragraph InfoLine(string label, string value, int width, Style? style = null) =>
        new Paragraph()
            .Append(Fit(label, 12), Dim)
            .Append(Fit(value, Math.Max(1, width - 12)), style ?? Style.Plain);

    private static string DescribeNode(TreeRow row)
    {
        var parts = new List<string> { row.Path };
        if (!string.IsNullOrEmpty(row.Node.Description)) parts.Add(row.Node.Description!);
        if (row.Node.Range is { } range) parts.Add($"range {range.GetRawText()}");
        if (row.Node.Unit is { } unit) parts.Add($"unit {unit.GetRawText()}");
        return string.Join("  ·  ", parts);
    }

    private static string FormatAccess(AccessValues? access) => access switch
    {
        AccessValues.Read => "r",
        AccessValues.Write => "w",
        AccessValues.ReadWrite => "rw",
        AccessValues.NoValue => "-",
        _ => ""
    };

    private static Style AccessStyle(AccessValues? access) => access switch
    {
        AccessValues.Read => new Style(Color.Green),
        AccessValues.Write => new Style(Color.Magenta1),
        AccessValues.ReadWrite => new Style(Color.Aqua),
        _ => Dim
    };

    /// <summary>
    /// Pads or truncates to exactly <paramref name="width"/> characters so lines never wrap.
    /// </summary>
    private static string Fit(string text, int width)
    {
        if (width <= 0) return string.Empty;
        text = text.Replace('\n', ' ').Replace('\r', ' ');
        return text.Length <= width ? text.PadRight(width) : text[..(width - 1)] + "…";
    }

    #endregion

    private enum Pane
    {
        Services,
        Tree
    }

    private sealed record TreeRow(OscQueryNode Node, string Name, string Path, int Depth, bool HasChildren, bool Collapsed);

    /// <summary>
    /// Renders its child at a fixed height, Layout otherwise fills the whole console.
    /// </summary>
    private sealed class FixedHeight(IRenderable child, int height) : Renderable
    {
        protected override Measurement Measure(RenderOptions options, int maxWidth) => child.Measure(options, maxWidth);

        protected override IEnumerable<Segment> Render(RenderOptions options, int maxWidth) =>
            child.Render(options with { Height = height }, maxWidth);
    }
}

/// <summary>
/// One fetched address space plus which values changed recently. Immutable once created.
/// </summary>
internal sealed record TreeSnapshot(
    OscQueryNode? Root,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyDictionary<string, DateTime> ChangedAt,
    int NodeCount,
    DateTime FetchedAt,
    string? Error)
{
    public static readonly TreeSnapshot Empty = new(null, new Dictionary<string, string>(), new Dictionary<string, DateTime>(), 0,
        DateTime.MinValue, null);

    public static TreeSnapshot Create(OscQueryNode root, TreeSnapshot? previous)
    {
        var now = DateTime.UtcNow;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var changedAt = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var count = 0;
        Visit(root, "/");
        return new TreeSnapshot(root, values, changedAt, count, now, null);

        void Visit(OscQueryNode node, string path)
        {
            count++;
            if (node.Value is { Length: > 0 })
            {
                var value = string.Join(", ", node.Value.Select(FormatValue));
                values[path] = value;
                if (previous != null && previous.Values.TryGetValue(path, out var old))
                {
                    if (old != value) changedAt[path] = now;
                    else if (previous.ChangedAt.TryGetValue(path, out var at) && now - at < Explorer.ChangeHighlight) changedAt[path] = at;
                }
            }

            if (node.Contents == null) return;
            foreach (var (key, child) in node.Contents)
                Visit(child, ChildPath(path, key));
        }
    }

    public static string ChildPath(string parent, string key) => parent == "/" ? "/" + key : parent + "/" + key;

    private static string FormatValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => value.GetRawText()
    };
}
