using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace XPlaneEfbBridge;

internal static class Program
{
    [STAThread]
    private static async Task Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--repair-config")
        {
            _ = BridgeConfig.Load(args[1]);
            Console.WriteLine("配置文件检查/修复完成。");
            return;
        }
        if (args.Length == 2 && args[0] == "--headless")
        {
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
            var service = new BridgeService(BridgeConfig.Load(args[1]));
            service.StatusChanged += Console.WriteLine;
            try { await service.RunAsync(cancellation.Token); }
            finally { await service.StopAsync(); }
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}

internal sealed class BridgeConfig
{
    public int[] UdpPorts { get; set; } = [49000];
    public int WebPort { get; set; } = 8080;
    public string SourceIp { get; set; } = "";
    public string AuthorizedChartTileUrl { get; set; } = "";
    public string AuthorizedChartAttribution { get; set; } = "";
    public string CustomBaseMapName { get; set; } = "";
    public string CustomBaseMapUrl { get; set; } = "";
    public string CustomBaseMapAttribution { get; set; } = "";
    public string NavigraphExternalUrl { get; set; } = "https://charts.navigraph.com/";

    public static BridgeConfig Load(string path)
    {
        var source = File.ReadAllText(path);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip };
        BridgeConfig loaded;
        var repaired = false;
        try
        {
            loaded = JsonSerializer.Deserialize<BridgeConfig>(source, options) ?? new BridgeConfig();
        }
        catch (JsonException error)
        {
            if (!TryExtractCurrentConfig(source, out var validObject))
                throw new InvalidDataException("配置文件不是合法 JSON。请确保文件中只有一个以 { 开始、以 } 结束的对象。", error);
            loaded = JsonSerializer.Deserialize<BridgeConfig>(validObject, options) ?? new BridgeConfig();
            repaired = true;
        }
        loaded.SourceIp ??= "";
        loaded.AuthorizedChartTileUrl ??= "";
        loaded.AuthorizedChartAttribution ??= "";
        loaded.CustomBaseMapName ??= "";
        loaded.CustomBaseMapUrl ??= "";
        loaded.CustomBaseMapAttribution ??= "";
        loaded.NavigraphExternalUrl ??= "https://charts.navigraph.com/";
        if (loaded.WebPort is < 1 or > 65535 || loaded.UdpPorts is null || loaded.UdpPorts.Length == 0 || loaded.UdpPorts.Any(port => port is < 1 or > 65535))
            throw new InvalidDataException("端口必须是 1–65535，且至少配置一个 UDP 端口。");
        if (repaired)
        {
            var backup = $"{path}.invalid-{DateTime.Now:yyyyMMdd-HHmmssfff}.bak";
            File.Copy(path, backup, false);
            File.WriteAllText(path, JsonSerializer.Serialize(loaded, new JsonSerializerOptions { WriteIndented = true }));
        }
        return loaded;
    }

    private static bool TryExtractCurrentConfig(string source, out string json)
    {
        var candidates = new List<(string Json, int Score, int Order)>();
        var depth = 0; var start = -1; var inString = false; var escaped = false; var order = 0;
        for (var index = 0; index < source.Length; index++)
        {
            var character = source[index];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') inString = false;
                continue;
            }
            if (character == '"') { inString = true; continue; }
            if (character == '{') { if (depth++ == 0) start = index; continue; }
            if (character != '}' || depth == 0) continue;
            if (--depth != 0 || start < 0) continue;
            var candidate = source[start..(index + 1)];
            try
            {
                var node = JsonNode.Parse(candidate, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })?.AsObject();
                if (node is null) continue;
                var score = node.Any(item => string.Equals(item.Key, nameof(WebPort), StringComparison.OrdinalIgnoreCase)) ? 100 : 0;
                score += node.Any(item => string.Equals(item.Key, nameof(UdpPorts), StringComparison.OrdinalIgnoreCase)) ? 10 : 0;
                candidates.Add((candidate, score, order++));
            }
            catch (JsonException) { }
        }
        var selected = candidates.OrderByDescending(item => item.Score).ThenByDescending(item => item.Order).FirstOrDefault();
        json = selected.Json ?? "";
        return json.Length > 0;
    }
}

internal sealed class TrayContext : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "XPlaneEfbBridge";
    private readonly NotifyIcon icon;
    private readonly ToolStripMenuItem statusItem;
    private readonly ToolStripMenuItem addressesItem;
    private readonly ToolStripMenuItem autoStartItem;
    private readonly Control dispatcher = new();
    private readonly string configPath;
    private CancellationTokenSource serviceCancellation = new();
    private BridgeService? service;
    private BridgeConfig config = new();
    private bool exiting;

    public TrayContext()
    {
        dispatcher.CreateControl();
        _ = dispatcher.Handle;
        var configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XPlaneEfbBridge");
        Directory.CreateDirectory(configDir);
        configPath = Path.Combine(configDir, "bridge-config.json");
        var firstRun = EnsureConfig();

        statusItem = new ToolStripMenuItem("正在启动…") { Enabled = false };
        addressesItem = new ToolStripMenuItem("iPad 访问地址") { Enabled = false };
        autoStartItem = new ToolStripMenuItem("开机自动启动") { Checked = IsAutoStart(), CheckOnClick = true };
        autoStartItem.Click += (_, _) => SetAutoStart(autoStartItem.Checked);
        var menu = new ContextMenuStrip();
        menu.Items.Add(statusItem);
        menu.Items.Add(addressesItem);
        menu.Items.Add("在本机打开 EFB", null, (_, _) => OpenEfb());
        menu.Items.Add("显示访问说明", null, (_, _) => ShowAddresses());
        menu.Items.Add("打开配置", null, (_, _) => OpenConfig());
        menu.Items.Add("重新加载配置", null, async (_, _) => await RestartAsync());
        menu.Items.Add(autoStartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) => await ExitAsync());
        icon = new NotifyIcon { Icon = SystemIcons.Application, Text = "X-Plane EFB Bridge", Visible = true, ContextMenuStrip = menu };
        icon.DoubleClick += (_, _) => OpenEfb();
        StartService();
        if (firstRun) ShowAddresses(true);
    }

    private bool EnsureConfig()
    {
        if (File.Exists(configPath)) return false;
        File.WriteAllText(configPath, JsonSerializer.Serialize(new BridgeConfig(), new JsonSerializerOptions { WriteIndented = true }));
        return true;
    }

    private BridgeConfig LoadConfig() => BridgeConfig.Load(configPath);

    private void StartService()
    {
        try
        {
            config = LoadConfig();
            addressesItem.Text = $"iPad: {FirstLanUrl(config.WebPort)}";
            service = new BridgeService(config);
            service.StatusChanged += text => { if (exiting || dispatcher.IsDisposed) return; dispatcher.BeginInvoke(() => {
                if (exiting) return;
                statusItem.Text = text;
                icon.Text = text.Length <= 63 ? text : text[..63];
            }); };
            var running = service.RunAsync(serviceCancellation.Token);
            _ = running.ContinueWith(task => { if (exiting || dispatcher.IsDisposed) return; dispatcher.BeginInvoke(() => {
                if (exiting) return;
                var message = task.Exception?.GetBaseException().Message ?? "未知错误";
                statusItem.Text = $"服务错误: {message}";
                icon.Text = "X-Plane EFB 服务错误";
            }); }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch (Exception error)
        {
            statusItem.Text = "配置错误";
            MessageBox.Show(error.Message, "X-Plane EFB Bridge 配置错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task RestartAsync()
    {
        serviceCancellation.Cancel();
        if (service is not null) await service.StopAsync();
        serviceCancellation.Dispose();
        serviceCancellation = new CancellationTokenSource();
        StartService();
    }

    private static IEnumerable<string> LanUrls(int port) => NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(adapter => {
            var properties = adapter.GetIPProperties();
            var hasGateway = properties.GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any));
            var isWireless = adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            return properties.UnicastAddresses
                .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
                .Select(address => new { address.Address, HasGateway = hasGateway, IsWireless = isWireless });
        })
        .OrderByDescending(item => item.HasGateway)
        .ThenByDescending(item => item.IsWireless)
        .ThenBy(item => item.Address.ToString(), StringComparer.Ordinal)
        .Select(item => $"http://{item.Address}:{port}")
        .Distinct();
    private static string FirstLanUrl(int port) => LanUrls(port).FirstOrDefault() ?? $"http://电脑IP:{port}";

    private void ShowAddresses(bool firstRun = false)
    {
        var urls = string.Join(Environment.NewLine, LanUrls(config.WebPort));
        if (urls.Length == 0) urls = $"http://电脑局域网IPv4:{config.WebPort}";
        var prefix = firstRun ? $"配置文件已创建：\n{configPath}\n\n" : "";
        MessageBox.Show($"{prefix}请让 iPad 和电脑连接同一 Wi-Fi，然后在 Safari 打开：\n\n{urls}\n\nXP12 UDP 目标端口：{string.Join(", ", config.UdpPorts)}", "X-Plane EFB Bridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OpenEfb() => Process.Start(new ProcessStartInfo($"http://127.0.0.1:{config.WebPort}") { UseShellExecute = true });
    private void OpenConfig()
    {
        var start = new ProcessStartInfo("notepad.exe") { UseShellExecute = true };
        start.ArgumentList.Add(configPath);
        Process.Start(start);
    }
    private bool IsAutoStart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunName) is not null;
    }
    private void SetAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunName, $"\"{Environment.ProcessPath}\""); else key.DeleteValue(RunName, false);
    }
    private async Task ExitAsync()
    {
        if (exiting) return;
        exiting = true;
        icon.Visible = false;
        try
        {
            serviceCancellation.Cancel();
            if (service is not null) await service.StopAsync();
        }
        catch { }
        finally
        {
            icon.Dispose();
            serviceCancellation.Dispose();
            dispatcher.Dispose();
            ExitThread();
        }
    }
}

internal sealed class BridgeService
{
    private readonly BridgeConfig config;
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentBag<UdpClient> sockets = [];
    private readonly LocalWebServer web;
    private long lastStatusTick;
    private int stopping;
    public event Action<string>? StatusChanged;

    public BridgeService(BridgeConfig config)
    {
        this.config = config;
        web = new LocalWebServer(config);
    }
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref stopping, 1) != 0) return;
        stop.Cancel();
        foreach (var socket in sockets) socket.Dispose();
        await web.StopAsync();
    }

    public async Task RunAsync(CancellationToken appToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(appToken, stop.Token);
        await web.StartAsync(linked.Token);
        StatusChanged?.Invoke($"网页已启动 · UDP {string.Join(",", config.UdpPorts)}");
        var listeners = config.UdpPorts.Distinct().Select(port => ListenAsync(port, linked.Token)).ToList();
        try { await Task.WhenAll(listeners.Append(web.WaitAsync(linked.Token))); } catch (OperationCanceledException) { } catch (ObjectDisposedException) { }
    }

    private async Task ListenAsync(int port, CancellationToken token)
    {
        UdpClient udp;
        try
        {
            udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            sockets.Add(udp);
        }
        catch (Exception error) { StatusChanged?.Invoke($"UDP {port} 不可用: {error.Message}"); return; }

        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try { packet = await udp.ReceiveAsync(token); }
            catch (ObjectDisposedException) { break; }
            if (config.SourceIp.Length > 0 && packet.RemoteEndPoint.Address.ToString() != config.SourceIp) continue;
            var telemetry = XPlaneParser.Parse(packet.Buffer);
            web.CountPacket(telemetry is not null, packet.RemoteEndPoint);
            if (telemetry is null) continue;
            telemetry["source"] = packet.RemoteEndPoint.Address.ToString();
            telemetry["protocol"] = "DATA";
            telemetry["receivedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            web.Publish(telemetry);
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref lastStatusTick) > 1000)
            {
                Interlocked.Exchange(ref lastStatusTick, now);
                StatusChanged?.Invoke($"XP12 {packet.RemoteEndPoint.Address} · UDP {port} · {web.ViewerCount} 台设备");
            }
        }
    }
}

internal static class XPlaneParser
{
    private static float F(ReadOnlySpan<byte> bytes, int offset) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, 4)));
    private static bool Valid(float value) => float.IsFinite(value) && Math.Abs(value) < 1e20;
    private static void Put(Dictionary<string, object> output, string name, float value) { if (Valid(value)) output[name] = value; }

    public static Dictionary<string, object>? Parse(byte[] packet)
    {
        if (packet.Length < 41 || Encoding.ASCII.GetString(packet, 0, 4) != "DATA") return null;
        Dictionary<string, object> output = [];
        for (var offset = 5; offset + 36 <= packet.Length; offset += 36)
        {
            var row = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(offset, 4));
            if (row == 3) { Put(output, "indicatedAirspeedKt", F(packet, offset + 4)); Put(output, "trueAirspeedKt", F(packet, offset + 12)); Put(output, "groundSpeedKt", F(packet, offset + 16)); }
            else if (row == 4) { Put(output, "mach", F(packet, offset + 4)); Put(output, "verticalSpeedFpm", F(packet, offset + 8)); Put(output, "gLoad", F(packet, offset + 20)); }
            else if (row == 17) { Put(output, "pitchDeg", F(packet, offset + 4)); Put(output, "rollDeg", F(packet, offset + 8)); Put(output, "headingTrueDeg", F(packet, offset + 12)); Put(output, "headingMagDeg", F(packet, offset + 16)); }
            else if (row == 20)
            {
                var lat = F(packet, offset + 4); var lon = F(packet, offset + 8);
                if (Valid(lat) && lat is >= -90 and <= 90) output["latitude"] = lat;
                if (Valid(lon) && lon is >= -180 and <= 180) output["longitude"] = lon;
                Put(output, "altitudeMslFt", F(packet, offset + 12)); Put(output, "altitudeAglFt", F(packet, offset + 16));
            }
        }
        return output.Count > 0 ? output : null;
    }
}

internal sealed class LocalWebServer
{
    private readonly BridgeConfig config;
    private readonly ConcurrentDictionary<Guid, WebSocket> viewers = new();
    private readonly Channel<string> updates = Channel.CreateBounded<string>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly object stateLock = new();
    private Dictionary<string, object> state = [];
    private WebApplication? app;
    private long packets, parsed;
    private string? lastSender;
    private DateTimeOffset? lastPacketAt;
    public int ViewerCount => viewers.Count;
    public LocalWebServer(BridgeConfig config) => this.config = config;

    public async Task StartAsync(CancellationToken token)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ApplicationName = Assembly.GetExecutingAssembly().FullName });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls($"http://0.0.0.0:{config.WebPort}");
        app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.MapGet("/api/config", () => Results.Json(new {
            websocketPath = "/ws",
            authorizedChart = config.AuthorizedChartTileUrl.Length > 0 ? new { name = "Authorized charts", url = config.AuthorizedChartTileUrl, attribution = config.AuthorizedChartAttribution } : null,
            customBaseMap = config.CustomBaseMapUrl.Length > 0 ? new { name = config.CustomBaseMapName.Length > 0 ? config.CustomBaseMapName : "自定义底图", url = config.CustomBaseMapUrl, attribution = config.CustomBaseMapAttribution } : null,
            navigraphExternalUrl = config.NavigraphExternalUrl,
            local = true
        }));
        app.MapGet("/api/status", () => {
            Dictionary<string, object> snapshot;
            lock (stateLock) snapshot = new(state);
            return Results.Json(new { udp = new { ports = config.UdpPorts, sourceIp = config.SourceIp.Length > 0 ? config.SourceIp : null }, stats = new { packets, parsed, lastSender, lastPacketAt }, viewers = viewers.Count, hasPosition = snapshot.ContainsKey("latitude") && snapshot.ContainsKey("longitude") });
        });
        app.Map("/ws", HandleWebSocket);
        app.MapMethods("/{**path}", ["GET", "HEAD"], ServeEmbedded);
        await app.StartAsync(token);
        _ = BroadcastLoop(token);
    }

    public Task WaitAsync(CancellationToken token) => app?.WaitForShutdownAsync(token) ?? Task.CompletedTask;
    public async Task StopAsync()
    {
        updates.Writer.TryComplete();
        foreach (var socket in viewers.Values)
        {
            try { socket.Abort(); socket.Dispose(); } catch { }
        }
        viewers.Clear();
        if (app is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try { await app.StopAsync(timeout.Token); } catch (OperationCanceledException) { }
        try { await app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
        app = null;
    }

    public void CountPacket(bool wasParsed, IPEndPoint sender)
    {
        Interlocked.Increment(ref packets); if (wasParsed) Interlocked.Increment(ref parsed);
        lastSender = sender.ToString(); lastPacketAt = DateTimeOffset.UtcNow;
    }
    public void Publish(Dictionary<string, object> telemetry)
    {
        lock (stateLock) { foreach (var item in telemetry) state[item.Key] = item.Value; telemetry = new(state); }
        updates.Writer.TryWrite(JsonSerializer.Serialize(new { type = "telemetry", payload = telemetry }));
    }

    private async Task HandleWebSocket(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var id = Guid.NewGuid(); viewers[id] = socket;
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "hello", payload = new { local = true, udpPorts = config.UdpPorts } })), WebSocketMessageType.Text, true, context.RequestAborted);
            Dictionary<string, object> snapshot; lock (stateLock) snapshot = new(state);
            if (snapshot.Count > 0) await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "telemetry", payload = snapshot })), WebSocketMessageType.Text, true, context.RequestAborted);
            var buffer = new byte[256];
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally { viewers.TryRemove(id, out _); }
    }

    private async Task BroadcastLoop(CancellationToken token)
    {
        try
        {
            await foreach (var message in updates.Reader.ReadAllAsync(token))
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                foreach (var item in viewers.ToArray())
                {
                    try { if (item.Value.State == WebSocketState.Open) await item.Value.SendAsync(bytes, WebSocketMessageType.Text, true, token); else viewers.TryRemove(item.Key, out _); }
                    catch { viewers.TryRemove(item.Key, out _); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase) {
        [".html"] = "text/html; charset=utf-8", [".js"] = "text/javascript; charset=utf-8", [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8", [".webmanifest"] = "application/manifest+json", [".svg"] = "image/svg+xml", [".png"] = "image/png"
    };
    private static readonly IReadOnlyDictionary<string, string> Resources = Assembly.GetExecutingAssembly().GetManifestResourceNames()
        .ToDictionary(name => name.Replace('\\', '/'), name => name, StringComparer.Ordinal);
    private static async Task ServeEmbedded(HttpContext context)
    {
        if (context.Request.Method is not ("GET" or "HEAD")) { context.Response.StatusCode = 405; return; }
        var relative = context.Request.Path.Value == "/" ? "index.html" : Uri.UnescapeDataString(context.Request.Path.Value?.TrimStart('/') ?? "");
        if (relative.Contains("..", StringComparison.Ordinal) || relative.Contains('\\')) { context.Response.StatusCode = 403; return; }
        Resources.TryGetValue($"wwwroot/{relative}", out var resourceName);
        await using var stream = resourceName is null ? null : Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream is null) { context.Response.StatusCode = 404; return; }
        context.Response.ContentType = Mime.GetValueOrDefault(Path.GetExtension(relative), "application/octet-stream");
        context.Response.ContentLength = stream.Length;
        context.Response.Headers.CacheControl = relative.StartsWith("vendor/", StringComparison.Ordinal) ? "public,max-age=604800,immutable" : "no-cache";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (context.Request.Method == "GET") await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
}
