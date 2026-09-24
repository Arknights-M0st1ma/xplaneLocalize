using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace XPlaneEfbBridge;

internal static class Program
{
    [STAThread]
    private static async Task Main(string[] args)
    {
        // WinExe has no console of its own, so the command line tools below would
        // otherwise write into nowhere when started from a terminal.
        if (args.Length >= 1 && args[0].StartsWith("--", StringComparison.Ordinal)) ConsoleHost.Attach();
        if (args.Length >= 2 && args[0] == "--dump-udp")
        {
            Environment.ExitCode = UdpDump.Run(args[1], args.Length > 2 && int.TryParse(args[2], out var seconds) ? seconds : 20);
            return;
        }
        if (args.Length >= 2 && args[0] == "--repair-config")
        {
            var snapshot = ConfigStore.Load(args[1]);
            Console.WriteLine(snapshot.Note ?? "配置文件检查完成，未做修改。");
            return;
        }
        if (args.Length >= 2 && args[0] == "--headless")
        {
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
            var snapshot = ConfigStore.Load(args[1]);
            snapshot.Config.Validate();
            var service = new BridgeService(snapshot.Config);
            service.StatusChanged += Console.WriteLine;
            try { await service.RunAsync(cancellation.Token); }
            finally { await service.StopAsync(); }
            return;
        }
        if (args.Length >= 2 && args[0] == "--config-selftest")
        {
            Environment.ExitCode = ConfigSelfTest.Run(args[1]);
            return;
        }
        if (args.Length >= 2 && args[0] == "--settings-preview")
        {
            SettingsPreview.Render(args);
            return;
        }
        if (args.Length >= 2 && args[0] == "--dump-ofp")
        {
            Environment.ExitCode = OfpDump.Run(args[1], args.Length > 2 ? args[2] : ConfigStore.DefaultPath);
            return;
        }
        if (args.Length >= 3 && args[0] == "--parse-ofp")
        {
            Environment.ExitCode = OfpDump.ParseFile(args[1], args[2]);
            return;
        }
        if (args.Length >= 2 && args[0] == "--tsw-probe")
        {
            Environment.ExitCode = TswProbeDump.Run(args[1], args.Length > 2 ? args[2] : ConfigStore.DefaultPath);
            return;
        }
        ApplicationConfiguration.Initialize();
        // A second instance would start a second service on the same ports and
        // could write the same configuration file at the same time.
        using var single = new Mutex(true, @"Local\XPlaneEfbBridge.Bridge", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "X-Plane EFB Bridge 已经在运行。\n\n请在任务栏通知区域（右下角托盘）右击图标，选择“设置…”来查看或修改配置。",
                "X-Plane EFB Bridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Application.Run(new TrayContext());
    }
}

internal sealed class BridgeConfig
{
    public const string ProxyNone = "none";
    public const string ProxySystem = "system";
    public const string ProxyManual = "manual";

    public int[] UdpPorts { get; set; } = [49000];
    public int WebPort { get; set; } = 8080;
    public string SourceIp { get; set; } = "";
    public string CustomBaseMapName { get; set; } = "";
    public string CustomBaseMapUrl { get; set; } = "";
    public string CustomBaseMapAttribution { get; set; } = "";
    // SimBrief Pilot ID (1-7 digits) or Navigraph alias. Empty disables the route overlay.
    public string SimbriefUser { get; set; } = "";
    public string SimbriefApiUrl { get; set; } = "";
    // OpenWeatherMap API key for the optional weather overlays. It stays on this
    // PC: the EFB asks the bridge for tiles, never for the key.
    public string WeatherApiKey { get; set; } = "";
    // OpenWeatherMap requests can use their own proxy (default: system proxy),
    // independent from the SimBrief/global one above. The password is stored
    // DPAPI-encrypted, never in clear text.
    public string WeatherProxyMode { get; set; } = ProxySystem;
    public string WeatherProxyUrl { get; set; } = "";
    public string WeatherProxyUser { get; set; } = "";
    public string WeatherProxyPassword { get; set; } = "";
    // X-Plane installation directory (for apt.dat ground data) and the zoom at
    // which that layer is allowed to appear.
    public string XplanePath { get; set; } = "";
    public int GroundMinZoom { get; set; } = 15;
    // Optional airline logo URL template, e.g.
    // https://example.com/airlines/{icao}_200.png — empty means "badge only".
    public string AirlineLogoUrlTemplate { get; set; } = "";
    // Folder of PDF manuals (checklists, FCOM, QRH...) served to the EFB reader.
    // The iPad cannot read this PC's disk, so the bridge lists and streams them.
    public string ManualFolder { get; set; } = "";
    // system = Windows proxy settings (including PAC, the default),
    // none = always direct, manual = the explicit host:port below.
    public string ProxyMode { get; set; } = ProxySystem;
    public string ProxyUrl { get; set; } = "";

    // Which simulator feeds the map. "xp" is X-Plane 12 over UDP (the original mode);
    // "tsw" reads Train Sim World 6 through its local HTTP API instead. The two are
    // mutually exclusive on purpose: both write latitude/longitude/protocol into the same
    // snapshot, so running them together would make the marker jump between sources.
    public const string SourceXPlane = "xp";
    public const string SourceTsw = "tsw";
    public string TelemetrySource { get; set; } = SourceXPlane;
    // TSW6 API base. Empty means the game's built-in http://127.0.0.1:31270.
    public string TswApiUrl { get; set; } = "";
    // Where to read the game-generated CommAPIKey.txt. Empty means "search the default
    // Documents\My Games\TrainSimWorld6\Saved\Config locations".
    public string TswApiKeyPath { get; set; } = "";
    // Polling rate for the TSW API. The API has no push, and every poll becomes one
    // WebSocket frame per viewer, so this is deliberately low by default.
    public int TswPollHz { get; set; } = 4;

    public bool UsesTsw => TelemetrySource == SourceTsw;

    public BridgeConfig Clone() => new()
    {
        UdpPorts = [.. UdpPorts],
        WebPort = WebPort,
        SourceIp = SourceIp,
        CustomBaseMapName = CustomBaseMapName,
        CustomBaseMapUrl = CustomBaseMapUrl,
        CustomBaseMapAttribution = CustomBaseMapAttribution,
        SimbriefUser = SimbriefUser,
        SimbriefApiUrl = SimbriefApiUrl,
        WeatherApiKey = WeatherApiKey,
        WeatherProxyMode = WeatherProxyMode,
        WeatherProxyUrl = WeatherProxyUrl,
        WeatherProxyUser = WeatherProxyUser,
        WeatherProxyPassword = WeatherProxyPassword,
        XplanePath = XplanePath,
        GroundMinZoom = GroundMinZoom,
        AirlineLogoUrlTemplate = AirlineLogoUrlTemplate,
        ManualFolder = ManualFolder,
        ProxyMode = ProxyMode,
        ProxyUrl = ProxyUrl,
        TelemetrySource = TelemetrySource,
        TswApiUrl = TswApiUrl,
        TswApiKeyPath = TswApiKeyPath,
        TswPollHz = TswPollHz
    };

    // Fills in nulls from a hand edited file and clamps the proxy mode to a known
    // value. Never touches values the user actually wrote.
    public void Normalize()
    {
        UdpPorts = UdpPorts is { Length: > 0 } ? UdpPorts : [49000];
        SourceIp = (SourceIp ?? "").Trim();
        CustomBaseMapName = (CustomBaseMapName ?? "").Trim();
        CustomBaseMapUrl = (CustomBaseMapUrl ?? "").Trim();
        CustomBaseMapAttribution = (CustomBaseMapAttribution ?? "").Trim();
        SimbriefUser = (SimbriefUser ?? "").Trim();
        SimbriefApiUrl = (SimbriefApiUrl ?? "").Trim();
        WeatherApiKey = (WeatherApiKey ?? "").Trim();
        WeatherProxyUrl = (WeatherProxyUrl ?? "").Trim();
        WeatherProxyUser = (WeatherProxyUser ?? "").Trim();
        WeatherProxyPassword ??= "";
        XplanePath = (XplanePath ?? "").Trim();
        AirlineLogoUrlTemplate = (AirlineLogoUrlTemplate ?? "").Trim();
        ManualFolder = (ManualFolder ?? "").Trim();
        GroundMinZoom = GroundMinZoom is >= 10 and <= 19 ? GroundMinZoom : 15;
        WeatherProxyMode = (WeatherProxyMode ?? "").Trim().ToLowerInvariant() switch
        {
            ProxyNone => ProxyNone,
            ProxyManual => ProxyManual,
            _ => ProxySystem
        };
        ProxyUrl = (ProxyUrl ?? "").Trim();
        ProxyMode = (ProxyMode ?? "").Trim().ToLowerInvariant() switch
        {
            ProxyNone => ProxyNone,
            ProxySystem => ProxySystem,
            ProxyManual => ProxyManual,
            _ => ProxySystem
        };
        TelemetrySource = (TelemetrySource ?? "").Trim().ToLowerInvariant() switch
        {
            SourceTsw => SourceTsw,
            _ => SourceXPlane
        };
        TswApiUrl = (TswApiUrl ?? "").Trim().TrimEnd('/');
        TswApiKeyPath = (TswApiKeyPath ?? "").Trim();
        TswPollHz = TswPollHz is >= 1 and <= 10 ? TswPollHz : 4;
    }

    // Only what the bridge needs in order to run. Everything else is reported as
    // a warning so a hand edited file never stops the app from starting.
    public void Validate()
    {
        if (WebPort is < 1 or > 65535)
            throw new InvalidDataException($"网页端口 {WebPort} 无效：必须是 1–65535。请在“设置…”中修正。");
        if (UdpPorts is null || UdpPorts.Length == 0)
            throw new InvalidDataException("UDP 端口列表为空：至少需要一个 1–65535 的端口。请在“设置…”中修正。");
        var bad = UdpPorts.Where(port => port is < 1 or > 65535).ToArray();
        if (bad.Length > 0)
            throw new InvalidDataException($"UDP 端口 {bad[0]} 无效：必须是 1–65535。请在“设置…”中修正。");
    }

    public List<string> Warnings()
    {
        var warnings = new List<string>();
        if (WebPort is < 1 or > 65535)
            warnings.Add($"网页端口 {WebPort} 不在 1–65535 范围内，设置窗口会按限制值显示，请修正后保存。");
        var invalidPorts = UdpPorts.Where(port => port is < 1 or > 65535).ToArray();
        if (invalidPorts.Length > 0)
            warnings.Add($"UDP 端口 {string.Join(", ", invalidPorts)} 不在 1–65535 范围内，请修正后保存。");
        if (UdpPorts.GroupBy(port => port).Any(group => group.Count() > 1))
            warnings.Add("UDP 端口列表里有重复项，运行时会自动去重。");
        if (SourceIp.Length > 0)
        {
            if (!IPAddress.TryParse(SourceIp, out var parsed))
                warnings.Add($"来源 IP“{SourceIp}”不是合法的 IP 地址，当前会拒绝所有 UDP 数据。");
            else if (parsed.AddressFamily != AddressFamily.InterNetwork)
                warnings.Add($"来源 IP“{SourceIp}”不是 IPv4 地址。");
        }
        if (CustomBaseMapUrl.Length > 0)
        {
            if (!CustomBaseMapUrl.Contains("{z}") || !CustomBaseMapUrl.Contains("{x}") || !CustomBaseMapUrl.Contains("{y}"))
                warnings.Add("自定义底图 URL 缺少 {z}/{x}/{y} 占位符，iPad 上将无法取图。");
            else if (!Uri.TryCreate(CustomBaseMapUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                warnings.Add("自定义底图 URL 必须是 http:// 或 https:// 开头的完整地址。");
        }
        if (ProxyMode == ProxyManual && ProxyUrl.Length == 0)
            warnings.Add("代理模式是“手动”，但代理地址为空。");
        if (WeatherProxyMode == ProxyManual && WeatherProxyUrl.Length == 0)
            warnings.Add("天气代理是“自定义”，但代理地址为空。");
        if (WeatherProxyUrl.Contains('@'))
            warnings.Add("天气代理地址里不要带凭据，请改用下边的用户名/密码字段（会加密保存）。");
        if (WeatherProxyUser.Length > 0 && SecretProtection.Unprotect(WeatherProxyPassword).Length == 0)
            warnings.Add("天气代理填了用户名但没有可用的密码：请在设置里重新输入代理密码。");
        if (ContainsSecret(SimbriefApiUrl))
            warnings.Add("SimBrief 端点里带有疑似密钥参数，建议改用系统代理或自建代理，不要把长期密钥写进配置文件。");
        if (TswApiUrl.Length > 0)
        {
            if (!Uri.TryCreate(TswApiUrl, UriKind.Absolute, out var tswUri) || tswUri.Scheme is not ("http" or "https"))
                warnings.Add($"TSW API 地址“{TswApiUrl}”不是 http(s) 开头的完整地址，连接会失败。");
            else if (ContainsSecret(TswApiUrl))
                warnings.Add("TSW API 地址里带有疑似密钥或账号信息，建议留空使用默认的 127.0.0.1:31270。");
        }
        if (TswApiKeyPath.Length > 0 && !File.Exists(TswApiKeyPath))
            warnings.Add($"TSW 密钥文件路径当前不存在：{TswApiKeyPath}（TSW 数据源会读不到密钥）。");
        return warnings;
    }

    public static bool ContainsSecret(string url) =>
        url.Length > 0 && (url.Contains('@') || url.Contains("key=", StringComparison.OrdinalIgnoreCase)
            || url.Contains("apikey=", StringComparison.OrdinalIgnoreCase)
            || url.Contains("token=", StringComparison.OrdinalIgnoreCase));
}

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon icon;
    private readonly ToolStripMenuItem statusItem;
    private readonly ToolStripMenuItem addressesItem;
    private readonly ToolStripMenuItem autoStartItem;
    private readonly Control dispatcher = new();
    private readonly string configPath;
    private CancellationTokenSource serviceCancellation = new();
    private BridgeService? service;
    private ConfigSnapshot snapshot;
    private BridgeConfig config = new();
    private bool pendingRestart;
    private bool exiting;

    public TrayContext()
    {
        dispatcher.CreateControl();
        _ = dispatcher.Handle;
        configPath = ConfigStore.DefaultPath;
        var firstRun = !File.Exists(configPath);
        snapshot = ConfigStore.Load(configPath);
        config = snapshot.Config;

        statusItem = new ToolStripMenuItem("正在启动…") { Enabled = false };
        addressesItem = new ToolStripMenuItem("iPad 访问地址") { Enabled = false };
        autoStartItem = new ToolStripMenuItem("开机自动启动") { Checked = SafeAutoStart(), CheckOnClick = true };
        autoStartItem.Click += (_, _) => { try { AutoStart.Set(autoStartItem.Checked); } catch { } };
        var advancedMenu = new ToolStripMenuItem("高级");
        advancedMenu.DropDownItems.Add("用记事本打开配置文件", null, (_, _) => OpenConfig());
        advancedMenu.DropDownItems.Add("打开配置文件夹", null, (_, _) => OpenConfigFolder());
        var menu = new ContextMenuStrip();
        menu.Items.Add(statusItem);
        menu.Items.Add(addressesItem);
        menu.Items.Add("在本机打开 EFB", null, (_, _) => OpenEfb());
        menu.Items.Add("设置…", null, (_, _) => OpenSettings());
        menu.Items.Add("显示访问说明", null, (_, _) => ShowAddresses());
        menu.Items.Add(advancedMenu);
        menu.Items.Add("重新加载配置（重启服务）", null, async (_, _) => await RestartAsync());
        menu.Items.Add(autoStartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) => await ExitAsync());
        icon = new NotifyIcon { Icon = AppIcon.Load(), Text = "X-Plane EFB Bridge", Visible = true, ContextMenuStrip = menu };
        icon.DoubleClick += (_, _) => OpenEfb();
        StartService();
        if (firstRun) ShowAddresses(true);
        else if (snapshot.Note is not null) NotifyConfigNote();
    }

    private void NotifyConfigNote()
    {
        try
        {
            icon.BalloonTipTitle = "X-Plane EFB Bridge 配置提示";
            icon.BalloonTipText = snapshot.Note!.Length > 250 ? snapshot.Note![..250] + "…" : snapshot.Note!;
            icon.ShowBalloonTip(8000);
        }
        catch { }
    }

    private static void CopyInto(BridgeConfig target, BridgeConfig source)
    {
        target.UdpPorts = [.. source.UdpPorts];
        target.WebPort = source.WebPort;
        target.SourceIp = source.SourceIp;
        target.CustomBaseMapName = source.CustomBaseMapName;
        target.CustomBaseMapUrl = source.CustomBaseMapUrl;
        target.CustomBaseMapAttribution = source.CustomBaseMapAttribution;
        target.SimbriefUser = source.SimbriefUser;
        target.SimbriefApiUrl = source.SimbriefApiUrl;
        target.WeatherApiKey = source.WeatherApiKey;
        target.WeatherProxyMode = source.WeatherProxyMode;
        target.WeatherProxyUrl = source.WeatherProxyUrl;
        target.WeatherProxyUser = source.WeatherProxyUser;
        target.WeatherProxyPassword = source.WeatherProxyPassword;
        target.XplanePath = source.XplanePath;
        target.GroundMinZoom = source.GroundMinZoom;
        target.AirlineLogoUrlTemplate = source.AirlineLogoUrlTemplate;
        target.ProxyMode = source.ProxyMode;
        target.ProxyUrl = source.ProxyUrl;
        target.TelemetrySource = source.TelemetrySource;
        target.TswApiUrl = source.TswApiUrl;
        target.TswApiKeyPath = source.TswApiKeyPath;
        target.TswPollHz = source.TswPollHz;
    }

    // Picks up hand edits (and anything the settings window wrote) without
    // breaking the single shared configuration instance the running service uses.
    private void RebindConfigFromDisk()
    {
        snapshot = ConfigStore.Load(configPath);
        CopyInto(config, snapshot.Config);
    }

    private void OpenSettings()
    {
        try { RebindConfigFromDisk(); }
        catch (Exception error)
        {
            MessageBox.Show($"无法读取配置文件：\n{error.Message}\n\n路径：{configPath}", "X-Plane EFB Bridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        using var form = new SettingsForm(snapshot, LanAddress.First(config.WebPort));
        if (form.ShowDialog() != DialogResult.OK || form.Result is null) return;
        snapshot = form.NewSnapshot ?? snapshot;

        var edited = form.Result;
        var portsChanged = edited.WebPort != config.WebPort || !edited.UdpPorts.SequenceEqual(config.UdpPorts);
        // Switching data source (or moving either TSW endpoint) restarts the service: the old
        // collector has to stop before the new one publishes, and the snapshot must be cleared so no
        // field of the previous source survives into the new mode.
        var sourceChanged = edited.TelemetrySource != config.TelemetrySource
            || !string.Equals(edited.TswApiUrl, config.TswApiUrl, StringComparison.Ordinal)
            || !string.Equals(edited.TswApiKeyPath, config.TswApiKeyPath, StringComparison.Ordinal);
        // Everything except the listening ports is read per use, so it applies now.
        config.SourceIp = edited.SourceIp;
        config.CustomBaseMapName = edited.CustomBaseMapName;
        config.CustomBaseMapUrl = edited.CustomBaseMapUrl;
        config.CustomBaseMapAttribution = edited.CustomBaseMapAttribution;
        config.SimbriefUser = edited.SimbriefUser;
        config.SimbriefApiUrl = edited.SimbriefApiUrl;
        config.WeatherApiKey = edited.WeatherApiKey;
        config.WeatherProxyMode = edited.WeatherProxyMode;
        config.WeatherProxyUrl = edited.WeatherProxyUrl;
        config.WeatherProxyUser = edited.WeatherProxyUser;
        config.WeatherProxyPassword = edited.WeatherProxyPassword;
        var groundChanged = !string.Equals(config.XplanePath, edited.XplanePath, StringComparison.OrdinalIgnoreCase);
        config.XplanePath = edited.XplanePath;
        config.GroundMinZoom = edited.GroundMinZoom;
        config.AirlineLogoUrlTemplate = edited.AirlineLogoUrlTemplate;
        config.ProxyMode = edited.ProxyMode;
        config.ProxyUrl = edited.ProxyUrl;
        config.TelemetrySource = edited.TelemetrySource;
        config.TswApiUrl = edited.TswApiUrl;
        config.TswApiKeyPath = edited.TswApiKeyPath;
        config.TswPollHz = edited.TswPollHz;
        // Rebuild the airport index now rather than making the iPad wait: the
        // apt.dat read is the slow part of the ground layer.
        if (groundChanged) service?.PrepareGround();

        if ((portsChanged || sourceChanged) && form.RestartRequested)
        {
            pendingRestart = false;
            _ = RestartAsync();
        }
        else if (portsChanged || sourceChanged)
        {
            // The window already told the user; keep the tray honest about which
            // ports are actually listening right now.
            pendingRestart = true;
        }
        else
        {
            pendingRestart = false;
        }
        UpdateStatusHint();
    }

    private void UpdateStatusHint()
    {
        if (pendingRestart) statusItem.Text = "配置已保存 · 端口或数据源改动待重启服务生效（托盘菜单 → 重新加载配置）";
    }

    private void StartService()
    {
        try
        {
            config.Validate();
            addressesItem.Text = $"iPad: {LanAddress.First(config.WebPort)}";
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
        try { RebindConfigFromDisk(); pendingRestart = false; }
        catch (Exception error) { MessageBox.Show($"无法读取配置文件：\n{error.Message}", "X-Plane EFB Bridge", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        StartService();
    }

    private void ShowAddresses(bool firstRun = false)
    {
        var urls = string.Join(Environment.NewLine, LanAddress.All(config.WebPort));
        if (urls.Length == 0) urls = $"http://电脑局域网IPv4:{config.WebPort}";
        var prefix = firstRun ? $"配置文件已创建：\n{configPath}\n\n" : "";
        MessageBox.Show($"{prefix}请让 iPad 和电脑连接同一 Wi-Fi，然后在 Safari 打开：\n\n{urls}\n\nXP12 UDP 目标端口：{string.Join(", ", config.UdpPorts)}", "X-Plane EFB Bridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OpenEfb() => Process.Start(new ProcessStartInfo($"http://127.0.0.1:{config.WebPort}") { UseShellExecute = true });
    private void OpenConfigFolder()
    {
        var directory = ConfigStore.DirectoryFor(configPath);
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
    }
    private void OpenConfig()
    {
        var start = new ProcessStartInfo("notepad.exe") { UseShellExecute = true };
        start.ArgumentList.Add(configPath);
        Process.Start(start);
    }
    private static bool SafeAutoStart()
    {
        try { return AutoStart.IsEnabled(); }
        catch { return false; }
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

// The addresses the iPad can use, most likely first (has a gateway, wireless).
internal static class LanAddress
{
    public static IEnumerable<string> All(int port) => NetworkInterface.GetAllNetworkInterfaces()
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

    public static string First(int port) => All(port).FirstOrDefault() ?? $"http://电脑IP:{port}";

    public static IReadOnlyList<string> Addresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
        .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
        .Select(address => address.Address.ToString())
        .Distinct()
        .ToList();
}

internal sealed class BridgeService
{
    private readonly BridgeConfig config;
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentBag<UdpClient> sockets = [];
    private readonly LocalWebServer web;
    private TswTelemetrySource? tsw;
    private long lastStatusTick;
    private int stopping;
    public event Action<string>? StatusChanged;

    public BridgeService(BridgeConfig config)
    {
        this.config = config;
        web = new LocalWebServer(config);
    }

    // Starts the apt.dat index in the background so the ground layer is ready by
    // the time the iPad asks for it (saving the settings window triggers this).
    public void PrepareGround() => web.PrepareGround();
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref stopping, 1) != 0) return;
        stop.Cancel();
        foreach (var socket in sockets) socket.Dispose();
        await StopTswAsync();
        await web.StopAsync();
    }

    public async Task RunAsync(CancellationToken appToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(appToken, stop.Token);
        await web.StartAsync(linked.Token);
        // Lets /api/status and /api/tsw/status report the live polling state without the web layer
        // holding a reference to the collector.
        web.TswStatusProvider = TswStatus;
        // Switching data source changes the meaning of every field in the snapshot, so the stale
        // fields of the previous source are dropped before the new one starts publishing.
        web.ClearSnapshot();
        var sources = new List<Task>();
        if (config.UsesTsw)
        {
            tsw = new TswTelemetrySource(config, web.Publish, StatusChanged);
            tsw.Start();
            StatusChanged?.Invoke($"TSW 数据源 · {TswEndpointLabel()} · {tsw.PollHz} Hz");
        }
        else
        {
            StatusChanged?.Invoke($"网页已启动 · UDP {string.Join(",", config.UdpPorts)}");
            sources.AddRange(config.UdpPorts.Distinct().Select(port => ListenAsync(port, linked.Token)));
        }
        try { await Task.WhenAll(sources.Append(web.WaitAsync(linked.Token))); } catch (OperationCanceledException) { } catch (ObjectDisposedException) { }
    }

    private string TswEndpointLabel() =>
        config.TswApiUrl.Length > 0 ? config.TswApiUrl : $"127.0.0.1:{TswApiClient.DefaultPort}";

    /// <summary>Human-readable status line for the tray, shared by the UDP and TSW paths.</summary>
    public string SourceLabel() => config.UsesTsw
        ? $"TSW {TswEndpointLabel()} · {web.ViewerCount} 台设备"
        : $"XP12 · UDP {string.Join(",", config.UdpPorts)} · {web.ViewerCount} 台设备";

    public Dictionary<string, object>? TswStatus() => tsw?.Status();

    public async Task StopTswAsync()
    {
        if (tsw is null) return;
        await tsw.StopAsync();
        tsw.Dispose();
        tsw = null;
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
            // Row 4 is "Mach, VVI, g-load": mach, VVI (fpm), g-load normal, axial, side.
            else if (row == 4) { Put(output, "mach", F(packet, offset + 4)); Put(output, "verticalSpeedFpm", F(packet, offset + 8)); Put(output, "gLoad", F(packet, offset + 12)); }
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

// Reads the pilot's latest SimBrief OFP and caches it locally.
// Endpoint: https://developers.navigraph.com/docs/simbrief/fetching-ofp-data
// Reading an OFP needs no API key, but the endpoint must only be called in
// response to a user action (never polled), otherwise the server firewall can
// ban the client. Results are cached on disk so a restart shows the last route
// without contacting SimBrief again.
internal sealed class SimBriefClient
{
    private const string DefaultEndpoint = "https://www.simbrief.com/api/xml.fetcher.php";
    private const int MinimumIntervalMs = 30000;
    private static readonly string[] LatitudeKeys = ["pos_lat", "lat", "latitude"];
    private static readonly string[] LongitudeKeys = ["pos_long", "lon", "lng", "longitude"];

    private readonly BridgeConfig config;
    private readonly string cachePath;
    private readonly bool persistCache;
    private readonly FlightPlanHistory? history;
    private readonly SemaphoreSlim gate = new(1, 1);
    private JsonObject? plan;
    private string? planOwner;
    private long? fetchedAt;
    private string requestId = "";
    private string? error;
    private long lastAttempt;
    // Raw payload of the last fetch, for the local --dump-ofp diagnostic only.
    public string? LastRaw { get; private set; }

    public SimBriefClient(BridgeConfig config) : this(config, true) { }

    public SimBriefClient(BridgeConfig config, bool persistCache)
    {
        // The settings window edits this same instance, so the user, the endpoint
        // and the proxy below are read per request and apply without a restart.
        this.config = config;
        this.persistCache = persistCache;
        cachePath = Path.Combine(ConfigStore.DirectoryFor(ConfigStore.ActivePath), "flightplan-cache.json");
        if (!persistCache) return;
        history = new FlightPlanHistory(Path.Combine(ConfigStore.DirectoryFor(ConfigStore.ActivePath), "flightplan-history.json"), cachePath);
        plan = history.ActivePlan(OwnerHash());
        fetchedAt = history.ActiveFetchedAt(OwnerHash());
    }

    public bool Configured => config.SimbriefUser.Trim().Length > 0;

    private string Endpoint => config.SimbriefApiUrl.Trim().Length > 0 ? config.SimbriefApiUrl.Trim() : DefaultEndpoint;

    // SHA-256 of the configured account, so the cache file never stores the Pilot ID itself.
    private string OwnerHash()
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(config.SimbriefUser.Trim().ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..16];
    }

    public JsonArray HistoryEntries() => history?.List(OwnerHash()) ?? [];

    public string OfpText() => history?.ActiveText(OwnerHash()) ?? "";

    public long? HistoryFetchedAt() => history?.ActiveFetchedAt(OwnerHash()) ?? fetchedAt;

    public string? ActiveId() => history?.ActiveId;

    public string ActiveLabel()
    {
        var active = history?.ActivePlan(OwnerHash());
        return active is null ? "" : FlightPlanHistory.Label(active);
    }

    public bool SelectHistory(string id)
    {
        if (history?.Select(id, OwnerHash()) != true) return false;
        plan = history.ActivePlan(OwnerHash());
        fetchedAt = history.ActiveFetchedAt(OwnerHash());
        planOwner = OwnerHash();
        error = null;
        return plan is not null;
    }

    // Direct connection by default; the proxy is opt-in and can be switched at
    // any time from the settings window (rebuild when its configuration changes).
    public async Task<JsonObject> GetAsync(bool refresh, CancellationToken token)
    {
        // The account can be changed in the settings window at any time; a plan
        // that belongs to a different account must never be shown.
        if (plan is not null && planOwner != OwnerHash())
        {
            plan = null;
            planOwner = null;
            fetchedAt = null;
        }
        if (plan is null && history is not null)
        {
            plan = history.ActivePlan(OwnerHash());
            fetchedAt = history.ActiveFetchedAt(OwnerHash());
            if (plan is not null) planOwner = OwnerHash();
        }
        if (refresh && Configured)
        {
            await gate.WaitAsync(token);
            try
            {
                var elapsed = Environment.TickCount64 - lastAttempt;
                if (elapsed < MinimumIntervalMs)
                    error = $"请求过于频繁，请 {Math.Ceiling((MinimumIntervalMs - elapsed) / 1000.0)} 秒后重试";
                else
                    await FetchAsync(token);
            }
            finally { gate.Release(); }
        }
        return Status();
    }

    private async Task FetchAsync(CancellationToken token)
    {
        lastAttempt = Environment.TickCount64;
        var user = config.SimbriefUser.Trim();
        // Read per request: the settings window can change the proxy while the
        // bridge keeps running, and the answer has to use the new one right away.
        var choice = OutboundHttp.Global(config);
        try
        {
            var parameter = IsPilotId(user) ? "userid" : "username";
            var url = $"{Endpoint}?{parameter}={Uri.EscapeDataString(user)}&json=v2";
            // SimBrief answers with the whole OFP (several hundred KB, plan_html
            // included), so the deadline is generous and one retry is worth it:
            // a proxy that is still starting up would otherwise fail the request
            // outright.
            using var response = await OutboundHttp.SendAsync(
                choice,
                TimeSpan.FromSeconds(35),
                attempts: 2,
                (client, deadline) => client.GetAsync(url, deadline),
                token);
            var body = await response.Content.ReadAsStringAsync(token);
            LastRaw = body;
            JsonNode? payload;
            try { payload = JsonNode.Parse(body); }
            catch { throw new InvalidDataException($"SimBrief 返回了无法解析的响应（HTTP {(int)response.StatusCode}）"); }
            if (!response.IsSuccessStatusCode)
            {
                var detail = Child(Child(payload, "fetch"), "status")?.GetValue<string>() ?? $"HTTP {(int)response.StatusCode}";
                throw new InvalidDataException(detail.StartsWith("Error:") ? detail[6..].Trim() : detail);
            }
            JsonObject? parsed;
            try { parsed = Parse(payload); }
            catch (Exception shapeError) { throw new InvalidDataException($"SimBrief 响应结构无法识别（{shapeError.Message}）。字段：{Shape(payload)}"); }
            if (parsed is null)
            {
                var routeText = Text(Child(payload, "general"), "route", "route_ifps");
                throw new InvalidDataException(routeText is null
                    ? $"SimBrief 响应里没有可用的航路数据。字段：{Shape(payload)}"
                    : $"SimBrief 计划里只有航路文字、没有航点坐标（可能在 SimBrief 里关闭了 Detailed Navlog）。航路：{Truncate(routeText, 60)}");
            }
            plan = parsed;
            planOwner = OwnerHash();
            fetchedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            requestId = Text(Child(payload, "params"), "request_id") ?? "";
            error = null;
            if (history is not null)
                history.Add(OwnerHash(), parsed, ExtractOfpText(payload), fetchedAt.Value, requestId, FlightPlanHistory.Label(parsed));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            error = $"SimBrief 请求超时（{OutboundHttp.DescribeEffective(choice)}）";
        }
        catch (Exception cause) when (cause is not OperationCanceledException)
        {
            error = OutboundHttp.Explain(cause, choice);
        }
    }

    private JsonObject Status() => new()
    {
        ["configured"] = Configured,
        ["available"] = plan is not null,
        ["fetchedAt"] = fetchedAt ?? HistoryFetchedAt(),
        ["error"] = error,
        ["plan"] = plan?.DeepClone()
    };

    // The complete OFP as readable text: SimBrief delivers it as one big <pre>
    // block inside text.plan_html, plus a plain text takeoff/landing report.
    internal static string ExtractOfpText(JsonNode? payload)
    {
        var text = Child(payload, "text");
        var plan = HtmlToText(Text(text, "plan_html") ?? "");
        var tlr = (Text(text, "tlr_section") ?? "").Trim();
        var combined = tlr.Length > 0 ? $"{plan}\n\n───── 起降性能报告 ─────\n\n{tlr}" : plan;
        return combined.Length <= MaxOfpChars ? combined : combined[..MaxOfpChars] + "\n…（内容过长，已截断）";
    }

    private const int MaxOfpChars = 900_000;

    private static string HtmlToText(string html)
    {
        if (html.Length == 0) return "";
        var text = Regex.Replace(html, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "</?(p|div|tr|table|pre|h[1-6])[^>]*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<!--.*?-->", "", RegexOptions.Singleline);
        text = Regex.Replace(text, "<[^>]+>", "");
        text = text.Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'");
        text = Regex.Replace(text, "[ \t]+\n", "\n");
        return Regex.Replace(text, "\n{4,}", "\n\n\n").Trim();
    }

    private static bool IsPilotId(string value) => value.Length is >= 1 and <= 7 && value.All(char.IsAsciiDigit);

    private static IEnumerable<JsonNode?> AsArray(JsonNode? node)
    {
        if (node is JsonArray array) return array;
        return node is null ? [] : [node];
    }

    private static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<double>(out var number)) return double.IsFinite(number) ? number : null;
        if (value.TryGetValue<string>(out var text) &&
            double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return null;
    }

    /// <summary>First key that holds a number: SimBrief renames fields between shapes.</summary>
    private static double? FirstNumber(JsonNode? node, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = Number(Child(node, key));
            if (value is not null) return value;
        }
        return null;
    }

    // SimBrief normally reports decimal degrees but the XML variant also uses
    // hemisphere prefixes such as "N51.470000".
    private static double? Coordinate(JsonNode? node)
    {
        var direct = Number(node);
        if (direct is not null) return direct;
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text)) return null;
        var trimmed = text.Trim();
        if (trimmed.Length < 2) return null;
        var sign = 1;
        if ("NSEW".Contains(char.ToUpperInvariant(trimmed[0])))
        {
            if (char.ToUpperInvariant(trimmed[0]) is 'S' or 'W') sign = -1;
            trimmed = trimmed[1..].Trim();
        }
        else if ("NSEW".Contains(char.ToUpperInvariant(trimmed[^1])))
        {
            if (char.ToUpperInvariant(trimmed[^1]) is 'S' or 'W') sign = -1;
            trimmed = trimmed[..^1].Trim();
        }
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? sign * Math.Abs(parsed) : null;
    }

    private static double? FirstCoordinate(JsonNode? entry, string[] keys)
    {
        if (entry is not JsonObject item) return null;
        foreach (var key in keys)
        {
            if (!item.TryGetPropertyValue(key, out var value)) continue;
            var coordinate = Coordinate(value);
            if (coordinate is not null) return coordinate;
        }
        return null;
    }

    private static string? Text(JsonNode? entry, params string[] keys)
    {
        if (entry is not JsonObject item) return null;
        foreach (var key in keys)
        {
            if (!item.TryGetPropertyValue(key, out var value) || value is null) continue;
            var text = value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString();
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }
        return null;
    }

    // 0/0 placeholders would stretch the route across the whole world.
    private static bool Usable(double? latitude, double? longitude) =>
        latitude is not null && longitude is not null && !(latitude == 0 && longitude == 0);

    private static JsonObject? Waypoint(JsonNode? entry)
    {
        var latitude = FirstCoordinate(entry, LatitudeKeys);
        var longitude = FirstCoordinate(entry, LongitudeKeys);
        var ident = Text(entry, "ident", "name");
        if (ident is null || !Usable(latitude, longitude)) return null;
        var type = (Text(entry, "type") ?? "").ToLowerInvariant();
        var kind = type.StartsWith("vor") ? "vor" : type.StartsWith("ndb") ? "ndb" : type is "apt" or "airport" or "ad" ? "apt" : "wpt";
        var altitude = Child(entry, "altitude_feet") ?? Child(entry, "altitude");
        return new JsonObject
        {
            ["ident"] = ident,
            ["name"] = Text(entry, "name") ?? "",
            ["type"] = kind,
            ["via"] = Text(entry, "via_airway", "via") ?? "",
            ["altitudeFt"] = Number(altitude),
            ["stage"] = Text(entry, "stage") ?? "",
            ["lat"] = latitude,
            ["lon"] = longitude
        };
    }

    private static JsonObject? Airport(JsonNode? entry)
    {
        var ident = Text(entry, "icao_code", "icao", "iata_code", "ident")?.ToUpperInvariant();
        if (ident is null) return null;
        var latitude = FirstCoordinate(entry, LatitudeKeys);
        var longitude = FirstCoordinate(entry, LongitudeKeys);
        if (latitude == 0 && longitude == 0) { latitude = null; longitude = null; }
        return new JsonObject
        {
            ["ident"] = ident,
            ["name"] = Text(entry, "name") ?? "",
            ["runway"] = Text(entry, "plan_rwy", "runway") ?? "",
            ["lat"] = latitude,
            ["lon"] = longitude
        };
    }

    private static JsonObject? WithFallback(JsonObject? airport, JsonObject? waypoint)
    {
        if (airport is null) return null;
        if (airport["lat"] is not null && airport["lon"] is not null) return airport;
        if (waypoint is null) return airport;
        airport["lat"] = waypoint["lat"]?.DeepClone();
        airport["lon"] = waypoint["lon"]?.DeepClone();
        return airport;
    }

    // Unknown fields are ignored so a SimBrief format change degrades to a
    // partial route instead of breaking the map.
    // Reads a child safely. SimBrief sends an empty string for the sections a
    // pilot has switched off (navlog for example when "Detailed Navlog" is
    // disabled), and indexing into a non object node throws in System.Text.Json.
    private static JsonNode? Child(JsonNode? node, string key) =>
        node is JsonObject item && item.TryGetPropertyValue(key, out var value) ? value : null;

    internal static string Shape(JsonNode? payload) => payload switch
    {
        JsonObject root => string.Join(", ", root.Select(item => $"{item.Key}:{Kind(item.Value)}")),
        null => "空响应",
        _ => Kind(payload)
    };

    private static string Kind(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "对象",
        JsonArray array => $"数组({array.Count})",
        _ => "值"
    };

    private static string Truncate(string text, int length) =>
        text.Length <= length ? text : text[..length] + "…";

    // "11:12:53" / "09:47:00" style durations, or a plain number of seconds.
    private static double? Duration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Trim().Split(':');
        if (parts.Length == 1) return Number(JsonValue.Create(value.Trim()));
        double seconds = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var piece)) return null;
            seconds = seconds * 60 + piece;
        }
        return seconds;
    }

    // Everything the flight card in the EFB shows. Times stay as the ISO-8601
    // UTC strings SimBrief sends, so the front end can print them verbatim.
    private static JsonObject FlightInfo(JsonNode? root)
    {
        var general = Child(root, "general");
        var times = Child(root, "times");
        var aircraft = Child(root, "aircraft");
        var airline = Text(general, "icao_airline") ?? "";
        var number = Text(general, "flight_number") ?? "";
        var callsign = airline.Length > 0
            ? number.Length > 0 ? $"{airline}/{number}" : airline
            : number;
        return new JsonObject
        {
            ["callsign"] = callsign,
            ["airline"] = airline,
            ["number"] = number,
            ["aircraft"] = Text(general, "aircraft_icao") ?? Text(aircraft, "icaocode") ?? "",
            ["aircraftName"] = Text(aircraft, "name") ?? "",
            ["registration"] = Text(aircraft, "reg") ?? "",
            ["plannedOff"] = Text(times, "sched_off") ?? "",
            ["plannedOn"] = Text(times, "sched_on") ?? "",
            ["plannedEnrouteSeconds"] = Duration(Text(times, "sched_time_enroute")),
            ["estimatedOff"] = Text(times, "est_off") ?? "",
            ["estimatedOn"] = Text(times, "est_on") ?? "",
            ["estimatedEnrouteSeconds"] = Duration(Text(times, "est_time_enroute")),
            ["generatedAt"] = Text(Child(root, "params"), "time_generated") ?? ""
        };
    }

    internal static JsonObject? Parse(JsonNode? payload)
    {
        if (payload is not JsonObject root) return null;
        // json=v2 returns the fixes as a flat array under "navlog"; the older
        // shape (and the XML variant) nests them as navlog.fix. Both are accepted.
        var navlogNode = Child(root, "navlog");
        var navlog = navlogNode is JsonArray direct ? direct : Child(navlogNode, "fix");
        var waypoints = new JsonArray();
        foreach (var item in AsArray(navlog))
        {
            var waypoint = Waypoint(item);
            if (waypoint is not null) waypoints.Add(waypoint);
        }
        var alternates = new JsonArray();
        foreach (var item in AsArray(Child(root, "alternate")))
        {
            var alternate = Airport(item);
            if (alternate is not null) alternates.Add(alternate);
        }
        foreach (var item in AsArray(Child(root, "takeoff_altn")))
        {
            var alternate = Airport(item);
            if (alternate is not null) alternates.Add(alternate);
        }
        var last = waypoints.Count > 0 ? waypoints[waypoints.Count - 1] as JsonObject : null;
        var first = waypoints.Count > 0 ? waypoints[0] as JsonObject : null;
        var origin = WithFallback(Airport(Child(root, "origin")), first);
        var destination = WithFallback(Airport(Child(root, "destination")), last);
        var positioned = origin is not null && origin["lat"] is not null && origin["lon"] is not null
            || destination is not null && destination["lat"] is not null && destination["lon"] is not null;
        if (waypoints.Count == 0 && !positioned) return null;
        var general = Child(root, "general");
        var weights = Child(root, "weights");
        var fuel = Child(root, "fuel");
        return new JsonObject
        {
            ["origin"] = origin,
            ["destination"] = destination,
            ["alternates"] = alternates,
            ["route"] = Text(general, "route", "route_ifps") ?? "",
            ["aircraft"] = Text(general, "aircraft_icao") ?? Text(Child(root, "aircraft"), "icaocode") ?? "",
            ["cruiseAltitudeFt"] = Number(Child(general, "initial_altitude") ?? Child(general, "cruise_altitude")),
            ["distanceNm"] = Number(Child(general, "route_distance") ?? Child(general, "gc_distance")),
            ["eteSeconds"] = Duration(Text(Child(root, "times"), "est_time_enroute")),
            // Weights, fuel and cruise figures for the plan page. SimBrief sends them
            // as strings, and the unit ("kgs" / "lbs") rides on the request
            // parameters rather than inside the weight block.
            ["units"] = (Text(Child(root, "params"), "units") ?? Text(weights, "units") ?? Text(fuel, "units") ?? "").ToUpperInvariant(),
            ["weight"] = new JsonObject
            {
                ["pax"] = Number(Child(weights, "pax_count")),
                ["bags"] = Number(Child(weights, "bag_count")),
                ["cargo"] = Number(Child(weights, "cargo")),
                ["freight"] = Number(Child(weights, "freight_added")),
                ["payload"] = Number(Child(weights, "payload")),
                ["oew"] = Number(Child(weights, "oew")),
                // A real OFP uses est_* for the planned weights and max_* for the
                // aircraft limits; the short names are kept as a fallback.
                ["zfw"] = FirstNumber(weights, "est_zfw", "zfw"),
                ["tow"] = FirstNumber(weights, "est_tow", "tow"),
                ["lw"] = FirstNumber(weights, "est_ldw", "ldw"),
                ["ramp"] = FirstNumber(weights, "est_ramp", "ramp"),
                ["maxZfw"] = FirstNumber(weights, "max_zfw", "mzfw"),
                ["maxTow"] = FirstNumber(weights, "max_tow", "mtow"),
                ["maxLw"] = FirstNumber(weights, "max_ldw", "mlw"),
                ["towLimitCode"] = Text(weights, "tow_limit_code") ?? ""
            },
            ["fuel"] = new JsonObject
            {
                ["block"] = Number(Child(fuel, "plan_ramp")),
                ["takeoff"] = Number(Child(fuel, "plan_takeoff")),
                ["landing"] = Number(Child(fuel, "plan_landing")),
                ["taxi"] = Number(Child(fuel, "taxi")),
                ["enroute"] = Number(Child(fuel, "enroute_burn")),
                ["contingency"] = Number(Child(fuel, "contingency")),
                ["alternate"] = Number(Child(fuel, "alternate_burn")),
                ["reserve"] = Number(Child(fuel, "reserve")),
                ["extra"] = Number(Child(fuel, "extra")),
                ["minTakeoff"] = Number(Child(fuel, "min_takeoff")),
                ["avgFlow"] = Number(Child(fuel, "avg_fuel_flow"))
            },
            ["cruise"] = new JsonObject
            {
                ["mach"] = Number(Child(general, "cruise_mach")),
                ["tas"] = Number(Child(general, "cruise_tas")),
                ["costIndex"] = Number(Child(general, "costindex") ?? Child(general, "cost_index")),
                ["airDistanceNm"] = Number(Child(general, "air_distance")),
                ["greatCircleNm"] = Number(Child(general, "gc_distance"))
            },
            ["flight"] = FlightInfo(root),
            ["waypoints"] = waypoints
        };
    }
}

internal sealed class LocalWebServer
{
    private readonly BridgeConfig config;
    private readonly SimBriefClient flightPlan;
    private readonly WeatherTiles weather = new();
    private readonly AptDat ground;
    private readonly TrackStore track;
    private readonly ConcurrentDictionary<Guid, WebSocket> viewers = new();
    private readonly Channel<string> updates = Channel.CreateBounded<string>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly VerticalSpeedEstimator verticalSpeed = new();
    private readonly object stateLock = new();
    private Dictionary<string, object> state = [];
    private WebApplication? app;
    private long packets, parsed;
    private string? lastSender;
    private DateTimeOffset? lastPacketAt;
    public int ViewerCount => viewers.Count;
    /// <summary>Set by BridgeService so the web layer can report TSW source state without
    /// holding a reference to the polling loop itself.</summary>
    public Func<Dictionary<string, object>?>? TswStatusProvider { get; set; }
    public LocalWebServer(BridgeConfig config)
    {
        this.config = config;
        flightPlan = new SimBriefClient(config);
        // Reads config.XplanePath on every lookup, so changing the X-Plane folder
        // in the settings window takes effect without restarting the service.
        ground = new AptDat(() => config.XplanePath, Path.Combine(ConfigStore.DirectoryFor(ConfigStore.ActivePath), "apt-index.json"));
        // The recorded track is the bridge's own copy of the flight; the page reads
        // it back instead of recording its own (see TrackStore).
        track = new TrackStore(Path.Combine(ConfigStore.DirectoryFor(ConfigStore.ActivePath), "track.json"));
    }

    public async Task StartAsync(CancellationToken token)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ApplicationName = Assembly.GetExecutingAssembly().FullName });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls($"http://0.0.0.0:{config.WebPort}");
        app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.MapGet("/api/config", () => Results.Json(new {
            websocketPath = "/ws",
            customBaseMap = config.CustomBaseMapUrl.Length > 0 ? new { name = config.CustomBaseMapName.Length > 0 ? config.CustomBaseMapName : "自定义底图", url = config.CustomBaseMapUrl, attribution = config.CustomBaseMapAttribution } : null,
            // The API key itself never leaves the PC; the EFB only learns whether
            // the overlays are configured and which layers exist.
            weather = new {
                configured = config.WeatherApiKey.Length > 0,
                maxZoom = WeatherTiles.MaxZoom,
                layers = WeatherTiles.Layers.Select(layer => new { name = layer.Name, label = layer.Label })
            },
            // Ground layout comes from the local X-Plane installation; the EFB
            // only needs to know whether it is configured and from which zoom.
            ground = GroundStatus(),
            airlineLogoUrlTemplate = config.AirlineLogoUrlTemplate,
            manuals = new { configured = config.ManualFolder.Length > 0, folder = config.ManualFolder },
            // Which simulator feeds this page. The front end switches units, icons and
            // which panels make sense based on this, so it is part of the config payload
            // rather than something guessed from the first telemetry frame.
            telemetrySource = config.TelemetrySource,
            local = true
        }));
        // TSW6 source diagnostics, used by the EFB drawer and the settings window.
        app.MapGet("/api/tsw/status", () => {
            var payload = new JsonObject
            {
                ["enabled"] = config.UsesTsw,
                ["status"] = TswStatusProvider?.Invoke() is { } status ? JsonSerializer.SerializeToNode(status) : null
            };
            return Results.Text(payload.ToJsonString(), "application/json");
        });
        // Ground layout of the airports (apt.dat): nearest to the aircraft.
        app.MapGet("/api/ground/nearest", (HttpContext context) => {
            var query = context.Request.Query;
            var latitude = double.TryParse(query["lat"], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ? lat : double.NaN;
            var longitude = double.TryParse(query["lon"], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) ? lon : double.NaN;
            var radius = double.TryParse(query["radius"], NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? Math.Clamp(r, 1, 40) : 8;
            JsonObject payload;
            if (!ground.Configured)
            {
                payload = new JsonObject
                {
                    ["configured"] = false,
                    ["error"] = "未配置 X-Plane 12 安装目录。请在电脑端托盘“设置…”→ 地图 里选择 X-Plane 12 安装目录。"
                };
            }
            else if (double.IsNaN(latitude) || double.IsNaN(longitude))
            {
                payload = new JsonObject { ["configured"] = true, ["error"] = "缺少有效的经纬度" };
            }
            // Building the airport index reads a large apt.dat, so it runs in the
            // background: answer immediately and let the EFB poll until it is done.
            else if (!ground.Ready)
            {
                var status = GroundStatus();
                var failure = (string?)status["error"];
                // A failed build is reported as-is instead of being retried on
                // every poll (that would re-read the same broken apt.dat forever).
                if (failure is null) ground.StartBuild();
                payload = failure is null
                    ? new JsonObject { ["configured"] = true, ["building"] = true }
                    : new JsonObject { ["configured"] = true, ["error"] = failure };
            }
            else
            {
                var data = ground.Nearest(latitude, longitude, radius);
                payload = data is null
                    ? new JsonObject { ["configured"] = true, ["airport"] = null }
                    : new JsonObject { ["configured"] = true, ["airport"] = data };
            }
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            return context.Response.WriteAsync(payload.ToJsonString(), context.RequestAborted);
        });
        app.MapGet("/api/ground/status", (HttpContext context) => {
            // ?build=1 is used by the settings window's "检查目录" button.
            var build = context.Request.Query["build"] == "1";
            if (build) ground.StartBuild();
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            return context.Response.WriteAsync(GroundStatus().ToJsonString(), context.RequestAborted);
        });
        // OpenWeatherMap overlay tiles, proxied so the key stays on the PC.
        app.MapGet("/api/weather/tile/{layer}/{z}/{x}/{y}", ServeWeatherTile);
        // Lets the EFB explain an invisible layer (no data in this area) instead
        // of leaving the user guessing.
        app.MapGet("/api/weather/status", (HttpContext context) => {
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            return context.Response.WriteAsync(weather.Status().ToJsonString(), context.RequestAborted);
        });
        // Written explicitly rather than through Results.Json: a JsonNode result
        // is easy to lose in the delegate overloads, and an empty 200 would make
        // the iPad show a stale cached route without any hint of the failure.
        app.MapGet("/api/flightplan", async (HttpContext context) => {
            var query = context.Request.Query;
            var select = query["select"].ToString();
            if (select.Length > 0 && !flightPlan.SelectHistory(select))
            {
                context.Response.StatusCode = 404;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync("{\"error\":\"找不到这份历史计划\"}", context.RequestAborted);
                return;
            }
            var refresh = query["refresh"].ToString() == "1";
            var status = await flightPlan.GetAsync(refresh, context.RequestAborted);
            status["activeId"] = flightPlan.ActiveId();
            status["historyCount"] = flightPlan.HistoryEntries().Count;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsync(status.ToJsonString(), context.RequestAborted);
        });
        // Plans the bridge has fetched before can be re-selected without asking
        // SimBrief again (the public API only ever returns the latest one).
        app.MapGet("/api/flightplan/history", (HttpContext context) => {
            var payload = new JsonObject
            {
                ["configured"] = flightPlan.Configured,
                ["activeId"] = flightPlan.ActiveId(),
                ["entries"] = flightPlan.HistoryEntries()
            };
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            return context.Response.WriteAsync(payload.ToJsonString(), context.RequestAborted);
        });
        // Full OFP text of the active plan, for the viewer inside the EFB.
        app.MapGet("/api/flightplan/ofp", (HttpContext context) => {
            var text = flightPlan.OfpText();
            var payload = new JsonObject
            {
                ["available"] = text.Length > 0,
                ["label"] = flightPlan.ActiveLabel(),
                ["fetchedAt"] = flightPlan.HistoryFetchedAt(),
                ["text"] = text
            };
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            return context.Response.WriteAsync(payload.ToJsonString(), context.RequestAborted);
        });
        app.MapGet("/api/status", () => {
            Dictionary<string, object> snapshot;
            lock (stateLock) snapshot = new(state);
            return Results.Json(new
            {
                source = config.TelemetrySource,
                udp = new { ports = config.UdpPorts, sourceIp = config.SourceIp.Length > 0 ? config.SourceIp : null },
                stats = new { packets, parsed, lastSender, lastPacketAt },
                viewers = viewers.Count,
                hasPosition = snapshot.ContainsKey("latitude") && snapshot.ContainsKey("longitude"),
                // The UDP counters stay in the payload for both sources so an existing monitoring
                // script keeps working; in TSW mode they simply stay at zero.
                simbrief = flightPlan.Configured,
                tsw = TswStatusProvider?.Invoke() is { } tswStatus ? JsonSerializer.SerializeToNode(tswStatus) : null
            });
        });
        // The flown track lives on the PC; the EFB only reads it. GET is
        // incremental when the page passes the newest point it already has, and
        // DELETE is what the "清轨迹" button calls.
        app.MapGet("/api/track", (HttpContext context) => {
            var raw = context.Request.Query["since"].ToString();
            long? since = long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
            return Results.Text(track.Read(since).ToJsonString(), "application/json");
        });
        app.MapDelete("/api/track", () => Results.Json(new { ok = true, removed = track.Clear() }));
        // Manual library: the PDFs live on this PC, so the bridge lists the folder
        // and streams the files back. Kestrel's own range processing serves the
        // byte ranges PDF.js asks for.
        app.MapGet("/api/manuals", () => Results.Text(Manuals.List(config.ManualFolder).ToJsonString(), "application/json"));
        app.MapGet("/api/manuals/file", (HttpContext context) => {
            var path = Manuals.FilePath(config.ManualFolder, context.Request.Query["path"].ToString());
            // Anything that is not a PDF inside the configured folder is a 404, not
            // an error page: the reader only ever asks for what the listing offered.
            return path is null
                ? Results.Json(new { error = "找不到该手册" }, statusCode: 404)
                : Results.File(path, "application/pdf", null, enableRangeProcessing: true);
        });
        app.Map("/ws", HandleWebSocket);
        app.MapMethods("/{**path}", ["GET", "HEAD"], ServeEmbedded);
        await app.StartAsync(token);
        // A track recorded earlier (the same machine, an earlier run) is what
        // "永久保留" means to the user, so it is read back at start-up.
        track.Load();
        _ = BroadcastLoop(token);
        _ = SaveTrackLoop(token);
    }

    private async Task SaveTrackLoop(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        try
        {
            while (await timer.WaitForNextTickAsync(token)) track.Save();
        }
        catch (OperationCanceledException) { }
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
        track.Save(true);
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
        Dictionary<string, object> snapshot;
        lock (stateLock)
        {
            foreach (var item in telemetry) state[item.Key] = item.Value;
            // The altitude-derived vertical speed only makes sense for the X-Plane source: the TSW
            // frame carries no altitude at all, so running the estimator there would resample the
            // same stale value and publish a fictional climb rate.
            if (!config.UsesTsw) ApplyVerticalSpeed();
            snapshot = new(state);
        }
        // Every frame that carries a position lengthens the recorded track,
        // whatever the iPad is doing at the time. This is what stops a track from
        // losing its middle when Safari is backgrounded.
        if (snapshot.TryGetValue("latitude", out var rawLatitude) && snapshot.TryGetValue("longitude", out var rawLongitude)
            && AsDouble(rawLatitude, out var latitude) && AsDouble(rawLongitude, out var longitude))
        {
            track.Record(latitude, longitude, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            track.Save();
        }
        updates.Writer.TryWrite(JsonSerializer.Serialize(new { type = "telemetry", payload = snapshot }));
    }

    /// <summary>Read-only copy of the current telemetry snapshot (self-test only).</summary>
    public Dictionary<string, object> SnapshotForTest()
    {
        lock (stateLock) return new(state);
    }

    /// <summary>Drops every field of the current snapshot (and the derived vertical-speed history),
    /// so a data-source switch cannot leave the previous source's values looking live. Without this
    /// the merge in <see cref="Publish"/> would keep an old altitude forever and the estimator above
    /// would keep reporting a climb rate for it.</summary>
    public void ClearSnapshot()
    {
        lock (stateLock)
        {
            state.Clear();
            verticalSpeed.Reset();
        }
    }

    // X-Plane reports -999 for values it cannot supply, and row 4 ("Mach, VVI,
    // g-load") can stay at that sentinel for the whole flight. Showing "-999 FPM"
    // is worse than useless, so an implausible value is replaced by a rate derived
    // from the altitude stream (row 20, which is required for the map anyway).
    private void ApplyVerticalSpeed()
    {
        // The parser stores boxed floats, so every numeric read has to accept both
        // float and double - checking for double alone silently disabled this.
        if (state.TryGetValue("altitudeMslFt", out var rawAltitude) && AsDouble(rawAltitude, out var altitude) && double.IsFinite(altitude))
            verticalSpeed.Feed(Environment.TickCount64, altitude);

        var reported = state.TryGetValue("verticalSpeedFpm", out var rawReported) && AsDouble(rawReported, out var value) ? value : double.NaN;
        if (PlausibleVerticalSpeed(reported))
        {
            state["verticalSpeedSource"] = "sim";
            return;
        }
        if (verticalSpeed.Value is not double derived)
        {
            // Nothing usable yet: let the EFB show "—" instead of a sentinel.
            state.Remove("verticalSpeedFpm");
            return;
        }
        state["verticalSpeedFpm"] = Math.Round(derived, 0);
        state["verticalSpeedSource"] = "derived";
    }

    internal static bool AsDouble(object? raw, out double value)
    {
        switch (raw)
        {
            case double number: value = number; return true;
            case float number: value = number; return true;
            case int number: value = number; return true;
            case long number: value = number; return true;
            case decimal number: value = (double)number; return true;
            default: value = double.NaN; return false;
        }
    }

    internal static bool PlausibleVerticalSpeed(double value) =>
        double.IsFinite(value) && Math.Abs(value) <= 20000 && Math.Abs(value + 999) > 1;

    // Smoothed least-squares slope of the altitude samples of the last few
    // seconds, in feet per minute.
    internal sealed class VerticalSpeedEstimator
    {
        private const int WindowMs = 6000;
        private readonly Queue<(long Ms, double Feet)> samples = new();
        private long lastMs = -1;
        private double lastFeet;
        private double smoothed;
        private bool hasValue;

        public double? Value => hasValue ? smoothed : null;

        /// <summary>Forgets every sample. Used when the data source changes, so a leftover altitude
        /// from the previous session cannot be mistaken for a fresh measurement.</summary>
        public void Reset()
        {
            samples.Clear();
            lastMs = -1;
            lastFeet = 0;
            smoothed = 0;
            hasValue = false;
        }

        public void Feed(long ms, double feet)
        {
            if (lastMs >= 0)
            {
                var gap = ms - lastMs;
                if (gap < 20 || gap > WindowMs) samples.Clear();
                // A teleport, a slew or an altitude resync must not be read as a
                // 40,000 fpm climb: start a fresh window instead.
                else if (Math.Abs(feet - lastFeet) / gap * 60000 > 15000) { samples.Clear(); hasValue = false; }
            }
            lastMs = ms;
            lastFeet = feet;
            samples.Enqueue((ms, feet));
            while (samples.Count > 0 && ms - samples.Peek().Ms > WindowMs) samples.Dequeue();
            var rate = Rate();
            if (rate is not double value) return;
            smoothed = hasValue ? smoothed * 0.7 + value * 0.3 : value;
            hasValue = true;
            if (Math.Abs(smoothed) < 25) smoothed = 0;
        }

        private double? Rate()
        {
            if (samples.Count < 3) return null;
            var origin = samples.Peek().Ms;
            if ((samples.Last().Ms - origin) / 1000.0 < 1.2) return null;
            double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
            var count = 0;
            foreach (var (ms, feet) in samples)
            {
                var x = (ms - origin) / 1000.0;
                count++;
                sumX += x;
                sumY += feet;
                sumXY += x * feet;
                sumXX += x * x;
            }
            var denominator = count * sumXX - sumX * sumX;
            if (Math.Abs(denominator) < 1e-6) return null;
            return (count * sumXY - sumX * sumY) / denominator * 60;
        }
    }

    private JsonObject GroundStatus()
    {
        var status = ground.Status(false);
        status["minZoom"] = config.GroundMinZoom;
        return status;
    }

    public void PrepareGround() => ground.StartBuild(force: true);

    private async Task HandleWebSocket(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var id = Guid.NewGuid(); viewers[id] = socket;
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "hello", payload = new { local = true, udpPorts = config.UdpPorts, source = config.TelemetrySource } })), WebSocketMessageType.Text, true, context.RequestAborted);
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

    // Tile URLs end with ".png" (Leaflet templates), so the last coordinate
    // arrives as "7.png".
    private static int NumberOf(object? value)
    {
        var text = (Convert.ToString(value) ?? "").Trim();
        var dot = text.IndexOf('.');
        if (dot >= 0) text = text[..dot];
        return int.TryParse(text, out var parsed) ? parsed : -1;
    }

    // OpenWeatherMap overlay tiles, proxied so the API key never leaves the PC.
    private async Task ServeWeatherTile(HttpContext context)
    {
        async Task Fail(int status, string message)
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsync($"{{\"error\":\"{message}\"}}", context.RequestAborted);
        }

        var layer = Convert.ToString(context.Request.RouteValues["layer"]) ?? "";
        var z = NumberOf(context.Request.RouteValues["z"]);
        var x = NumberOf(context.Request.RouteValues["x"]);
        var y = NumberOf(context.Request.RouteValues["y"]);
        if (!WeatherTiles.ValidCoordinate(layer, z, x, y)) { await Fail(400, "瓦片参数不合法"); return; }
        var apiKey = config.WeatherApiKey.Trim();
        if (apiKey.Length == 0) { await Fail(404, "未配置 OpenWeatherMap API Key"); return; }

        var cacheKey = $"{layer}/{z}/{x}/{y}";
        if (weather.TryGet(cacheKey, out var cached))
        {
            await WriteTile(context, cached.Body, cached.ContentType);
            return;
        }
        if (!weather.AllowRequest()) { await Fail(429, "天气瓦片请求过于频繁，请稍后再刷新"); return; }

        try
        {
            using var response = await OutboundHttp.SendAsync(
                OutboundHttp.Weather(config),
                TimeSpan.FromSeconds(20),
                attempts: 2,
                (client, deadline) => client.GetAsync(WeatherTiles.UpstreamUrl(layer, apiKey, z, x, y), deadline),
                context.RequestAborted);
            var body = await response.Content.ReadAsByteArrayAsync(context.RequestAborted);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                await Fail(401, "OpenWeatherMap 拒绝了该 API Key（检查是否填错、或新 Key 还需等待生效）");
                return;
            }
            if (!response.IsSuccessStatusCode)
            {
                await Fail(502, $"OpenWeatherMap 返回 HTTP {(int)response.StatusCode}");
                return;
            }
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/png";
            weather.Record(layer, body.Length);
            weather.Store(cacheKey, body, contentType);
            await WriteTile(context, body, contentType);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            await Fail(502, $"无法连接 OpenWeatherMap：{SecretProtection.Redact(error.Message)}");
        }
    }

    private static async Task WriteTile(HttpContext context, byte[] body, string contentType)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = contentType;
        context.Response.ContentLength = body.Length;
        context.Response.Headers.CacheControl = "public, max-age=300";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        await context.Response.Body.WriteAsync(body, context.RequestAborted);
    }

    private static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase) {
        [".html"] = "text/html; charset=utf-8", [".js"] = "text/javascript; charset=utf-8", [".css"] = "text/css; charset=utf-8",
        // ES modules are MIME-checked strictly, so .mjs must not fall through to
        // application/octet-stream: the PDF.js bundle would be rejected and the
        // whole page script graph would fail to load.
        [".mjs"] = "text/javascript; charset=utf-8",
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
