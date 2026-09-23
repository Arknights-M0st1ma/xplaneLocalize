using System.Net;
using System.Net.Sockets;

namespace XPlaneEfbBridge;

// The visual settings window. It edits a copy of the configuration, validates it,
// writes it through ConfigStore (backup + atomic replace) and reports what has to
// be restarted. Nothing is sent over the network from here.
internal sealed class SettingsForm : Form
{
    private const int LabelWidth = 150;

    private ConfigSnapshot snapshot;
    private readonly BridgeConfig original;
    private readonly string lanUrl;
    private readonly Dictionary<Control, Label> errors = [];
    private readonly List<string> changed = [];

    private NumericUpDown webPort = new();
    private TextBox udpPorts = new();
    private ComboBox sourceIp = new();
    private ComboBox baseMap = new();
    private TextBox baseMapName = new();
    private TextBox baseMapUrl = new();
    private TextBox baseMapAttribution = new();
    private TextBox xplanePath = new();
    private Label groundHint = new();
    private NumericUpDown groundMinZoom = new();
    private TextBox airlineLogoUrl = new();
    private TextBox manualFolder = new();
    private TextBox simbriefUser = new();
    private CheckBox showSimbrief = new();
    private Button testRoute = new();
    private Label testResult = new();
    private ComboBox proxyMode = new();
    private TextBox proxyUrl = new();
    private CheckBox autoStart = new();
    private CheckBox cleanObsolete = new();
    private TextBox weatherKey = new();
    private CheckBox showWeatherKey = new();
    private Button testWeatherKey = new();
    private Label testWeatherResult = new();
    private ComboBox weatherProxyMode = new();
    private TextBox weatherProxyUrl = new();
    private TextBox weatherProxyUser = new();
    private TextBox weatherProxyPassword = new();
    private CheckBox showWeatherProxy = new();
    private Label weatherProxyNote = new();
    private ComboBox telemetrySource = new();
    private TextBox tswApiUrl = new();
    private TextBox tswKeyPath = new();
    private NumericUpDown tswPollHz = new();
    private Button testTsw = new();
    private Label testTswResult = new();
    private Label tswNote = new();
    private TextBox diagnostics = new();
    private Label status = new();
    private Label banner = new();
    private TabControl tabs = new();
    private System.Windows.Forms.Timer cooldown = new();
    private bool loading = true;
    private readonly float scale;

    // All pixel values in this file are written for 96 DPI and scaled here: the
    // text is rendered at the display DPI, so the boxes have to follow.
    private int S(int pixels) => (int)Math.Round(pixels * scale);

    public BridgeConfig? Result { get; private set; }
    public ConfigSnapshot? NewSnapshot { get; private set; }
    public bool RestartRequested { get; private set; }
    public string? BackupPath { get; private set; }

    public SettingsForm(ConfigSnapshot snapshot, string lanUrl)
    {
        this.snapshot = snapshot;
        this.lanUrl = lanUrl;
        var dpi = DeviceDpi > 0 ? DeviceDpi : 96;
        scale = dpi / 96f;
        original = snapshot.Config.Clone();
        Build();
        LoadValues(original);
        ShowNotes();
    }

    public void SelectTab(int index)
    {
        if (index >= 0 && index < tabs.TabPages.Count) tabs.SelectedIndex = index;
    }

    // ------------------------------------------------------------------ layout
    private void Build()
    {
        Text = "X-Plane EFB Bridge 设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        Font = PickFont();
        BackColor = Color.White;
        ClientSize = new Size(S(620), S(660));
        Icon = AppIcon.Load();

        banner = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 0,
            Padding = new Padding(S(12), S(8), S(12), S(8)),
            BackColor = Color.FromArgb(255, 248, 214),
            ForeColor = Color.FromArgb(102, 74, 0),
            Visible = false
        };

        tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(S(14), S(6)) };
        tabs.TabPages.Add(BuildConnectionTab());
        tabs.TabPages.Add(BuildSourceTab());
        tabs.TabPages.Add(BuildMapTab());
        tabs.TabPages.Add(BuildRouteTab());
        tabs.TabPages.Add(BuildWeatherTab());
        tabs.TabPages.Add(BuildAdvancedTab());

        var footer = new Panel { Dock = DockStyle.Bottom, Height = S(92), Padding = new Padding(S(14), S(6), S(14), S(10)) };
        status = new Label
        {
            Dock = DockStyle.Top,
            Height = S(34),
            ForeColor = Color.FromArgb(90, 90, 90),
            Text = "改完点“保存”。端口类改动需要重启服务；其它设置立即生效。"
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = S(40), WrapContents = false };
        var save = new Button { Text = "保存", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(S(110), S(32)), DialogResult = DialogResult.None };
        var cancel = new Button { Text = "取消", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(S(90), S(32)), DialogResult = DialogResult.Cancel };
        var defaults = new Button { Text = "恢复默认值", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(S(120), S(32)), DialogResult = DialogResult.None };
        save.Click += (_, _) => Save();
        defaults.Click += (_, _) => RestoreDefaults();
        buttons.Controls.AddRange([save, cancel, defaults]);
        footer.Controls.Add(buttons);
        footer.Controls.Add(status);

        Controls.Add(tabs);
        Controls.Add(footer);
        Controls.Add(banner);
        AcceptButton = save;
        CancelButton = cancel;
        cooldown.Interval = 1000;
        cooldown.Tick += OnCooldownTick;
        weatherCooldown.Tick += OnWeatherCooldownTick;
    }

    internal static Font PickFont()
    {
        foreach (var name in new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" })
        {
            try
            {
                var font = new Font(name, 9F);
                if (string.Equals(font.Name, name, StringComparison.OrdinalIgnoreCase)) return font;
                font.Dispose();
            }
            catch { }
        }
        return SystemFonts.MessageBoxFont ?? new Font("Segoe UI", 9F);
    }

    private Panel Page(out TableLayoutPanel stack)
    {
        var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(S(4), S(8), S(4), S(8)) };
        stack = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(S(10), S(4), S(10), S(12))
        };
        page.Controls.Add(stack);
        return page;
    }

    private Label Caption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(0, S(12), 0, S(3))
    };

    private Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(S(540), 0),
        ForeColor = Color.FromArgb(105, 105, 105),
        Margin = new Padding(0, S(3), 0, 0)
    };

    private Label ErrorSlot(Control control)
    {
        var label = new Label
        {
            Text = "",
            AutoSize = true,
            MaximumSize = new Size(S(540), 0),
            ForeColor = Color.FromArgb(178, 34, 34),
            Margin = new Padding(0, S(3), 0, 0),
            Visible = false
        };
        errors[control] = label;
        return label;
    }

    private void AddField(TableLayoutPanel stack, string caption, Control control, string hint, Label error)
    {
        stack.Controls.Add(Caption(caption));
        control.Margin = new Padding(0, 0, 0, 0);
        stack.Controls.Add(control);
        if (hint.Length > 0) stack.Controls.Add(Hint(hint));
        stack.Controls.Add(error);
    }

    private TabPage BuildConnectionTab()
    {
        var page = Page(out var stack);
        var tab = new TabPage("连接") { BackColor = Color.White, Padding = new Padding(6) };
        tab.Controls.Add(page);

        webPort = new NumericUpDown { Minimum = 1, Maximum = 65535, Width = S(130), TextAlign = HorizontalAlignment.Right };
        AddField(stack, "网页端口（iPad 打开网页用）", webPort,
            "默认 8080。改成别的端口后，需要在 Windows 防火墙放行新端口，iPad 也要用新地址。需要重启服务生效。", ErrorSlot(webPort));

        udpPorts = new TextBox { Width = S(300) };
        AddField(stack, "X-Plane UDP 端口", udpPorts,
            "接收 X-Plane Data Output 的端口，默认 49000，可写多个用逗号分隔（例如 49003, 49002）。改这里必须同时在 X-Plane 里把 UDP 目标端口改成一样的值。需要重启服务生效。", ErrorSlot(udpPorts));

        sourceIp = new ComboBox { Width = S(300), DropDownStyle = ComboBoxStyle.DropDown };
        AddField(stack, "只接受该来源 IP（可选）", sourceIp,
            "留空接受局域网内任意发送端。有多台模拟器时，填 X-Plane 电脑的 IPv4 可避免串数据。", ErrorSlot(sourceIp));

        var detected = LanAddress.Addresses();
        sourceIp.Items.Add("（任意来源，留空）");
        foreach (var address in detected) sourceIp.Items.Add(address);
        sourceIp.Items.Add("127.0.0.1");
        return tab;
    }

    private TabPage BuildSourceTab()
    {
        var page = Page(out var stack);
        var tab = new TabPage("数据源") { BackColor = Color.White, Padding = new Padding(6) };
        tab.Controls.Add(page);

        telemetrySource = new ComboBox { Width = S(420), DropDownStyle = ComboBoxStyle.DropDownList };
        telemetrySource.Items.AddRange([
            "X-Plane 12（UDP DATA 包，默认）",
            "Train Sim World 6（本机 HTTP API）"
        ]);
        telemetrySource.SelectedIndexChanged += (_, _) => SyncSourceEnabled();
        AddField(stack, "地图跟随哪个模拟器", telemetrySource,
            "两者互斥：它们都会写同一份位置/协议字段，同时收会让地图上的图标来回跳。切换后需要重启服务（保存时会询问）。",
            ErrorSlot(telemetrySource));

        stack.Controls.Add(Caption("Train Sim World 6"));
        stack.Controls.Add(Hint(
            "TSW6 内置一个官方 HTTP API（默认 http://127.0.0.1:31270），只能在装了游戏的这台电脑上访问，"
            + "因此桥接器必须和游戏同机运行。启用方式：Steam → TSW6 → 属性 → 启动选项加入 -HTTPAPI，"
            + "启动一次游戏后它会生成密钥文件。主机（PS/Xbox）版没有这个 API。"));

        tswApiUrl = new TextBox { Width = S(360) };
        AddField(stack, "API 地址（可留空）", tswApiUrl,
            "留空即使用游戏默认的 http://127.0.0.1:31270。只有在自己做了端口转发或指向测试用假服务器时才需要填写。",
            ErrorSlot(tswApiUrl));

        tswKeyPath = new TextBox { Width = S(420) };
        var browseKey = new Button { Text = "选择文件…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(S(100), S(28)) };
        browseKey.Click += (_, _) => BrowseKeyFile();
        var keyRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        keyRow.Controls.Add(tswKeyPath);
        keyRow.Controls.Add(browseKey);
        AddField(stack, "密钥文件（可留空）", keyRow,
            "留空时自动在“文档\\My Games\\TrainSimWorld6\\Saved\\Config\\CommAPIKey.txt”等默认位置查找（含 OneDrive 重定向的文档目录）。"
            + "密钥只读进内存、不写入配置文件、不下发给 iPad、不写日志。",
            ErrorSlot(tswKeyPath));

        tswPollHz = new NumericUpDown { Minimum = 1, Maximum = 10, Width = S(90), TextAlign = HorizontalAlignment.Right };
        AddField(stack, "轮询频率（Hz）", tswPollHz,
            "游戏 API 只有请求/响应、没有推送，所以由桥接器轮询。默认 4 Hz 已经足够地图流畅；"
            + "每帧都会变成一条发给每个 iPad 的 WebSocket 消息，所以不建议调到 10 Hz。",
            ErrorSlot(tswPollHz));

        testTsw = new Button { Text = "测试连接", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(S(120), S(32)) };
        testTsw.Click += async (_, _) => await TestTswAsync();
        testTswResult = new Label { AutoSize = true, MaximumSize = new Size(S(540), 0), ForeColor = Color.FromArgb(60, 60, 60), Margin = new Padding(0, S(6), 0, 0) };
        stack.Controls.Add(Caption("验证"));
        stack.Controls.Add(testTsw);
        stack.Controls.Add(testTswResult);
        stack.Controls.Add(Hint("测试会连接上面的地址、检查密钥是否被接受，并列出游戏实际暴露的端点。不需要先保存，也不需要游戏正在飞行。"));

        tswNote = new Label { AutoSize = true, MaximumSize = new Size(S(540), 0), ForeColor = Color.FromArgb(105, 105, 105), Margin = new Padding(0, S(8), 0, 0) };
        stack.Controls.Add(tswNote);
        return tab;
    }

    // Reads the game-generated key file path. Uses the built-in picker (the Windows one loads every
    // installed shell extension and can take the process down with it - see README).
    private void BrowseKeyFile()
    {
        try
        {
            var typed = tswKeyPath.Text.Trim();
            var initial = File.Exists(typed)
                ? Path.GetDirectoryName(typed)!
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "TrainSimWorld6", "Saved", "Config");
            if (!Directory.Exists(initial)) initial = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            using var picker = new FolderPicker(
                "选择 TSW6 生成的 CommAPIKey.txt",
                "通常是“文档\\My Games\\TrainSimWorld6\\Saved\\Config\\CommAPIKey.txt”。也可以直接把路径粘贴到输入框。",
                initial,
                DescribeKeyFile);
            if (picker.ShowDialog(this) == DialogResult.OK && picker.SelectedPath.Length > 0)
            {
                tswKeyPath.Text = picker.SelectedPath;
                testTswResult.Text = "";
            }
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"无法打开选择器：{error.Message}\n\n请直接把路径粘贴到输入框。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // Never reports the key itself, only whether a usable file is there.
    private static (string Text, bool Ok) DescribeKeyFile(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var key = File.ReadAllText(path).Trim().TrimStart('\uFEFF').Trim();
                return key.Length == 0
                    ? ("这个文件是空的，游戏可能还没写完密钥。", false)
                    : ($"读取到 {key.Length} 个字符的密钥（内容不会显示，也不会写进配置）。", true);
            }
            catch (Exception error)
            {
                return ($"无法读取：{error.Message}", false);
            }
        }
        if (Directory.Exists(path)) return ("这是文件夹。请进入 Config 目录后选中 CommAPIKey.txt；留空则自动查找。", false);
        return ("这里没有文件。", false);
    }

    private async Task TestTswAsync()
    {
        var address = tswApiUrl.Text.Trim();
        if (address.Length > 0 && (!Uri.TryCreate(address, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https")))
        {
            testTswResult.Text = "API 地址格式不对：应留空或填 http://主机:端口。";
            return;
        }
        var typedKey = tswKeyPath.Text.Trim();
        if (typedKey.Length > 0 && !File.Exists(typedKey))
        {
            testTswResult.Text = $"找不到这个密钥文件：{typedKey}";
            return;
        }

        testTsw.Enabled = false;
        testTswResult.Text = "测试中…";
        try
        {
            using var client = new TswApiClient(typedKey, address);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var probe = await client.ProbeAsync(timeout.Token);
            var lines = new List<string> { probe.Summary };
            lines.Add($"地址：{probe.BaseUrl}");
            if (probe.KeyConfigured && probe.KeyPath.Length > 0) lines.Add($"密钥：{probe.KeyPath}");
            else if (!probe.KeyConfigured) lines.Add($"密钥：{probe.KeyNote}");
            if (probe.TcpReachable && probe.ListOk)
            {
                lines.Add($"根节点：{(probe.Nodes.Count > 0 ? string.Join("、", probe.Nodes.Take(12)) : "（未列出）")}");
                lines.Add(probe.InfoAvailable
                    ? $"游戏：{probe.GameName} · Worker {probe.Worker} · build {probe.GameBuild} · API {probe.ApiVersion}"
                    : "注意：/info 不可用（该路由不在公开规范里），但 /list 正常，所以 API 是可用的。");
                var position = await PositionProbeAsync(client, timeout.Token);
                lines.Add(position);
            }
            testTswResult.Text = string.Join(Environment.NewLine, lines);
        }
        catch (OperationCanceledException)
        {
            testTswResult.Text = "测试超时（12 秒）。确认游戏在运行、且启动项里有 -HTTPAPI。";
        }
        catch (Exception error)
        {
            testTswResult.Text = "测试失败：" + error.Message;
        }
        finally
        {
            testTsw.Enabled = true;
        }
    }

    // Reports whether a position endpoint is actually readable; this is the check that turns
    // "the API answers" into "the map can be drawn". Field names come from third-party docs, so the
    // candidates are tried in the same order as TswTelemetrySource uses.
    private static async Task<string> PositionProbeAsync(TswApiClient client, CancellationToken token)
    {
        var candidates = new (string Node, string Endpoint)[]
        {
            ("DriverAid", "PlayerInfo"),
            ("DriverAid", "Data"),
            ("CurrentDrivableActor", "LatLon")
        };
        foreach (var (node, endpoint) in candidates)
        {
            var response = await client.GetAsync(node, endpoint, token);
            if (!response.Ok) continue;
            var latitude = TswMapper.FindDeep(response.Values, ["latitude", "lat", "Latitude"]);
            var longitude = TswMapper.FindDeep(response.Values, ["longitude", "lon", "Longitude"]);
            if (latitude is double lat && longitude is double lon)
                return $"位置端点可用：{node}.{endpoint} → {lat:0.00000}, {lon:0.00000}";
        }
        return "没能读到位置：已试 DriverAid.PlayerInfo / DriverAid.Data / CurrentDrivableActor.LatLon。"
            + "如果游戏在主菜单，请进入一条线路后再测；也可以点“测试连接”后把结果发给开发者核对端点名。";
    }

    private void SyncSourceEnabled()
    {
        var tsw = telemetrySource.SelectedIndex == 1;
        tswApiUrl.Enabled = tsw;
        tswKeyPath.Enabled = tsw;
        tswPollHz.Enabled = tsw;
        testTsw.Enabled = tsw;
    }

    private TabPage BuildMapTab()
    {
        var page = Page(out var stack);
        var tab = new TabPage("地图") { BackColor = Color.White, Padding = new Padding(6) };
        tab.Controls.Add(page);

        baseMap = new ComboBox { Width = S(300), DropDownStyle = ComboBoxStyle.DropDownList };
        baseMap.Items.AddRange(["OpenStreetMap（默认主力底图）", "自定义 XYZ 底图（需自行获得授权）"]);
        baseMap.SelectedIndexChanged += (_, _) => SyncBaseMapEnabled();
        AddField(stack, "底图", baseMap, "OpenStreetMap 不需要额外配置，是默认主力底图。自定义底图只填你拥有展示授权的服务地址。", ErrorSlot(baseMap));

        baseMapName = new TextBox { Width = S(300) };
        AddField(stack, "自定义底图名称", baseMapName, "显示在 iPad 图层列表里的名字，可留空。", ErrorSlot(baseMapName));

        baseMapUrl = new TextBox { Width = S(470) };
        AddField(stack, "自定义底图 URL", baseMapUrl,
            "必须是 XYZ 模板，例如 https://tiles.example.com/{z}/{x}/{y}.png。不要在这里放长期密钥。", ErrorSlot(baseMapUrl));

        baseMapAttribution = new TextBox { Width = S(470) };
        AddField(stack, "自定义底图署名", baseMapAttribution,
            "会显示在地图右下角。多数服务商要求署名，建议填写，例如 “© 服务提供商”。", ErrorSlot(baseMapAttribution));

        // ---------------------------------------------------------- ground data
        xplanePath = new TextBox { Width = S(360) };
        var browse = new Button { Text = "选择文件夹…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(S(110), S(30)), Margin = new Padding(S(8), 0, 0, 0) };
        browse.Click += (_, _) => BrowseForXPlane();
        var pathRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        pathRow.Controls.Add(xplanePath);
        pathRow.Controls.Add(browse);
        AddField(stack, "X-Plane 12 安装目录", pathRow,
            "选择包含 Resources 与 Custom Scenery 的那一层（通常叫 X-Plane 12）。用于读取本机 apt.dat 显示跑道/滑行道/标线/机位，只在本机读取、不会上传；留空则关闭地面图层。", ErrorSlot(xplanePath));
        groundHint = new Label
        {
            Text = "",
            AutoSize = true,
            MaximumSize = new Size(S(540), 0),
            ForeColor = Color.FromArgb(105, 105, 105),
            Margin = new Padding(0, S(3), 0, 0),
            Visible = false
        };
        stack.Controls.Add(groundHint);
        xplanePath.TextChanged += (_, _) => RefreshGroundHint();

        groundMinZoom = new NumericUpDown { Minimum = 10, Maximum = 19, Width = S(90), TextAlign = HorizontalAlignment.Right };
        AddField(stack, "地面图层最小缩放", groundMinZoom,
            "地图缩放到该级别及以上才显示并解析地面数据（默认 15，越大越省资源）。", ErrorSlot(groundMinZoom));

        airlineLogoUrl = new TextBox { Width = S(470) };
        AddField(stack, "航司 logo 地址模板（可选）", airlineLogoUrl,
            "留空时只在航班卡片上显示三字码徽章。航司 logo 属于商标素材，如果你有自己的授权来源，可填写例如 https://example.com/airlines/{icao}_200.png —— {icao} 会替换成三字码；请自行确认该来源的使用许可。", ErrorSlot(airlineLogoUrl));

        // ------------------------------------------------------------ manuals
        manualFolder = new TextBox { Width = S(360) };
        var manualBrowse = new Button { Text = "选择文件夹…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(S(110), S(30)), Margin = new Padding(S(8), 0, 0, 0) };
        manualBrowse.Click += (_, _) => BrowseForManuals();
        var manualRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        manualRow.Controls.Add(manualFolder);
        manualRow.Controls.Add(manualBrowse);
        AddField(stack, "手册文件夹（PDF）", manualRow,
            "把这个文件夹里的 PDF（机务手册、QRH、检查单…）提供给 iPad 的“手册”页阅读，子文件夹也会一并列出。PDF 只在本机读取，不会上传；留空则手册页显示未配置。", ErrorSlot(manualFolder));
        return tab;
    }

    private TabPage BuildRouteTab()
    {
        var page = Page(out var stack);
        var tab = new TabPage("SimBrief") { BackColor = Color.White, Padding = new Padding(6) };
        tab.Controls.Add(page);

        simbriefUser = new TextBox { Width = S(300), UseSystemPasswordChar = true };
        showSimbrief = new CheckBox { Text = "显示", AutoSize = true, Margin = new Padding(S(8), S(6), 0, 0) };
        showSimbrief.CheckedChanged += (_, _) => simbriefUser.UseSystemPasswordChar = !showSimbrief.Checked;
        var userRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        userRow.Controls.Add(simbriefUser);
        userRow.Controls.Add(showSimbrief);
        AddField(stack, "SimBrief Pilot ID / 用户名", userRow,
            "填 1–7 位数字的 Pilot ID，或 Navigraph 用户名。留空则关闭航路叠加。该值只保存在本机，不会下发给 iPad。", ErrorSlot(simbriefUser));

        proxyMode = new ComboBox { Width = S(300), DropDownStyle = ComboBoxStyle.DropDownList };
        proxyMode.Items.AddRange(["不使用代理（默认，直连）", "使用系统代理（Windows 设置）", "手动代理（主机:端口）"]);
        proxyMode.SelectedIndexChanged += (_, _) => SyncProxyEnabled();
        AddField(stack, "代理（仅影响 SimBrief 航路拉取）", proxyMode,
            "iPad 的地图瓦片是 iPad 自己下载的，不受这里影响；局域网数据链路也不走代理。", ErrorSlot(proxyMode));

        proxyUrl = new TextBox { Width = S(300) };
        AddField(stack, "代理地址", proxyUrl, "例如 http://127.0.0.1:7890。为避免明文保存密码，这里不接受用户名:密码。", ErrorSlot(proxyUrl));

        testRoute = new Button { Text = "测试并获取航路", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(S(160), S(32)) };
        testRoute.Click += async (_, _) => await TestRouteAsync();
        testResult = new Label { AutoSize = true, MaximumSize = new Size(S(540), 0), ForeColor = Color.FromArgb(60, 60, 60), Margin = new Padding(0, S(6), 0, 0) };
        stack.Controls.Add(Caption("验证"));
        stack.Controls.Add(testRoute);
        stack.Controls.Add(testResult);
        stack.Controls.Add(Hint("只有点击这个按钮时才会向 SimBrief 请求一次，程序不会后台轮询。测试使用上面填写的值。"));
        return tab;
    }

    private TabPage BuildWeatherTab()
    {
        var page = Page(out var stack);
        var tab = new TabPage("天气") { BackColor = Color.White, Padding = new Padding(6) };
        tab.Controls.Add(page);

        weatherKey = new TextBox { Width = S(360), UseSystemPasswordChar = true };
        showWeatherKey = new CheckBox { Text = "显示", AutoSize = true, Margin = new Padding(S(8), S(6), 0, 0) };
        showWeatherKey.CheckedChanged += (_, _) => weatherKey.UseSystemPasswordChar = !showWeatherKey.Checked;
        var keyRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        keyRow.Controls.Add(weatherKey);
        keyRow.Controls.Add(showWeatherKey);
        AddField(stack, "OpenWeatherMap API Key", keyRow,
            "在 openweathermap.org 免费注册后于“API keys”页面获取（免费额度 60 次/分钟、100 万次/月，足够个人使用）。Key 只保存在本机，不会下发给 iPad——iPad 是向桥接器取瓦片。新申请的 Key 可能需要等十几分钟才生效。留空表示关闭天气图层。",
            ErrorSlot(weatherKey));

        testWeatherKey = new Button { Text = "测试连接", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(S(120), S(32)) };
        testWeatherKey.Click += async (_, _) => await TestWeatherKeyAsync();
        testWeatherResult = new Label { AutoSize = true, MaximumSize = new Size(S(540), 0), ForeColor = Color.FromArgb(60, 60, 60), Margin = new Padding(0, S(6), 0, 0) };
        stack.Controls.Add(Caption("验证"));
        stack.Controls.Add(testWeatherKey);
        stack.Controls.Add(testWeatherResult);
        stack.Controls.Add(Hint("测试只下载一张最小的瓦片（zoom 0），用来确认 Key 与下面的代理设置是否可用。"));

        weatherProxyMode = new ComboBox { Width = S(300), DropDownStyle = ComboBoxStyle.DropDownList };
        weatherProxyMode.Items.AddRange(["使用系统代理（默认）", "不使用代理（直连）", "自定义代理"]);
        weatherProxyMode.SelectedIndexChanged += (_, _) => SyncWeatherProxyEnabled();
        AddField(stack, "天气请求的代理", weatherProxyMode,
            "只作用于 OpenWeatherMap 瓦片请求；SimBrief 的代理仍在“高级”页里设置。", ErrorSlot(weatherProxyMode));

        weatherProxyUrl = new TextBox { Width = S(360) };
        AddField(stack, "代理地址", weatherProxyUrl,
            "例如 http://127.0.0.1:7890 或 http://proxy.local:8080。不要在这里写用户名密码（用下面两栏）。", ErrorSlot(weatherProxyUrl));

        weatherProxyUser = new TextBox { Width = S(240), UseSystemPasswordChar = true };
        AddField(stack, "代理用户名（可选）", weatherProxyUser, "需要认证的代理才填。", ErrorSlot(weatherProxyUser));

        weatherProxyPassword = new TextBox { Width = S(240), UseSystemPasswordChar = true };
        showWeatherProxy = new CheckBox { Text = "显示认证信息", AutoSize = true, Margin = new Padding(S(8), S(6), 0, 0) };
        showWeatherProxy.CheckedChanged += (_, _) =>
        {
            weatherProxyUser.UseSystemPasswordChar = !showWeatherProxy.Checked;
            weatherProxyPassword.UseSystemPasswordChar = !showWeatherProxy.Checked;
        };
        var passwordRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        passwordRow.Controls.Add(weatherProxyPassword);
        passwordRow.Controls.Add(showWeatherProxy);
        AddField(stack, "代理密码（可选）", passwordRow,
            "密码用 Windows 凭据加密（DPAPI）后保存在本机，文件里不是明文；换电脑或换 Windows 账号后需要重新输入。", ErrorSlot(weatherProxyPassword));

        weatherProxyNote = new Label { AutoSize = true, MaximumSize = new Size(S(540), 0), ForeColor = Color.FromArgb(105, 105, 105), Margin = new Padding(0, S(6), 0, 0) };
        stack.Controls.Add(weatherProxyNote);
        stack.Controls.Add(Caption("图层"));
        stack.Controls.Add(Hint("在 iPad 地图左侧的天气图标里选择要叠加的图层（云 / 降水 / 风 / 温度 / 气压）；显示时会自动附上 OpenWeather 署名。"));
        return tab;
    }

    private void SyncWeatherProxyEnabled()
    {
        var manual = weatherProxyMode.SelectedIndex == 2;
        weatherProxyUrl.Enabled = manual;
        weatherProxyUser.Enabled = manual;
        weatherProxyPassword.Enabled = manual;
    }

    private TabPage BuildAdvancedTab()
    {
        var page = Page(out var stack);
        var tab = new TabPage("高级") { BackColor = Color.White, Padding = new Padding(6) };
        tab.Controls.Add(page);

        autoStart = new CheckBox { Text = "开机时自动启动桥接器", AutoSize = true, Margin = new Padding(0, S(8), 0, 0) };
        autoStart.CheckedChanged += (_, _) =>
        {
            if (loading) return;
            try { AutoStart.Set(autoStart.Checked); }
            catch (Exception error) { MessageBox.Show(this, $"修改开机自启失败：{error.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        stack.Controls.Add(Caption("启动"));
        stack.Controls.Add(autoStart);
        stack.Controls.Add(Hint("勾选后立即写入当前用户的启动项（不需要保存）。"));

        cleanObsolete = new CheckBox { Text = "保存时删除已废弃的旧字段", AutoSize = true, Margin = new Padding(0, S(8), 0, 0) };
        stack.Controls.Add(Caption("迁移"));
        stack.Controls.Add(cleanObsolete);
        stack.Controls.Add(Hint($"旧版本留下的 {string.Join("、", ConfigStore.ObsoleteKeys)} 会保留在文件里但不再被程序使用。默认不删除；勾选后保存时会先备份再删除。"));

        var openFile = new Button { Text = "用记事本打开配置文件", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(S(190), S(30)) };
        var openFolder = new Button { Text = "打开配置文件夹", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(S(150), S(30)) };
        openFile.Click += (_, _) => OpenWith("notepad.exe", snapshot.Path);
        openFolder.Click += (_, _) => OpenWith("explorer.exe", ConfigStore.DirectoryFor(snapshot.Path));
        var tools = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, S(4), 0, 0) };
        tools.Controls.AddRange([openFile, openFolder]);
        stack.Controls.Add(Caption("文件"));
        stack.Controls.Add(tools);
        stack.Controls.Add(Hint("配置以 JSON 文本保存，可以用记事本查看。手动改完请回到这里或使用托盘“重新加载配置（重启服务）”。"));

        diagnostics = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Width = S(540),
            Height = S(132),
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(248, 248, 248)
        };
        stack.Controls.Add(Caption("当前状态"));
        stack.Controls.Add(diagnostics);
        stack.Controls.Add(Hint("只读信息，可以选中复制。"));
        return tab;
    }

    private void OpenWith(string executable, string argument)
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = true };
            info.ArgumentList.Add(argument);
            System.Diagnostics.Process.Start(info);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"无法打开：{error.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ------------------------------------------------------------------ values
    private void LoadValues(BridgeConfig config)
    {
        webPort.Value = Math.Clamp(config.WebPort, 1, 65535);
        udpPorts.Text = string.Join(", ", config.UdpPorts);
        sourceIp.Text = config.SourceIp;
        baseMap.SelectedIndex = config.CustomBaseMapUrl.Length > 0 ? 1 : 0;
        baseMapName.Text = config.CustomBaseMapName;
        baseMapUrl.Text = config.CustomBaseMapUrl;
        baseMapAttribution.Text = config.CustomBaseMapAttribution;
        xplanePath.Text = config.XplanePath;
        groundMinZoom.Value = Math.Clamp(config.GroundMinZoom, 10, 19);
        airlineLogoUrl.Text = config.AirlineLogoUrlTemplate;
        manualFolder.Text = config.ManualFolder;
        simbriefUser.Text = config.SimbriefUser;
        weatherKey.Text = config.WeatherApiKey;
        weatherProxyMode.SelectedIndex = config.WeatherProxyMode switch
        {
            BridgeConfig.ProxyNone => 1,
            BridgeConfig.ProxyManual => 2,
            _ => 0
        };
        weatherProxyUrl.Text = config.WeatherProxyUrl;
        weatherProxyUser.Text = config.WeatherProxyUser;
        var storedPassword = SecretProtection.Unprotect(config.WeatherProxyPassword);
        weatherProxyPassword.Text = storedPassword;
        weatherProxyNote.Text = config.WeatherProxyPassword.Length == 0
            ? ""
            : storedPassword.Length > 0
                ? "已保存的代理密码可以在本机解密（换电脑/换 Windows 账号后需要重新输入）。"
                : "无法解密已保存的代理密码（可能换了电脑或 Windows 账号），请重新输入后再保存。";
        proxyMode.SelectedIndex = config.ProxyMode switch
        {
            BridgeConfig.ProxySystem => 1,
            BridgeConfig.ProxyManual => 2,
            _ => 0
        };
        proxyUrl.Text = config.ProxyUrl;
        telemetrySource.SelectedIndex = config.UsesTsw ? 1 : 0;
        tswApiUrl.Text = config.TswApiUrl;
        tswKeyPath.Text = config.TswApiKeyPath;
        tswPollHz.Value = Math.Clamp(config.TswPollHz, 1, 10);
        try { autoStart.Checked = AutoStart.IsEnabled(); } catch { autoStart.Enabled = false; }
        loading = false;
        SyncBaseMapEnabled();
        SyncProxyEnabled();
        SyncWeatherProxyEnabled();
        SyncSourceEnabled();
        UpdateTswNote();
        UpdateDiagnostics();
    }

    // Explains, before the user saves, what the TSW source needs on this machine. Only file
    // existence is reported; the key itself is never read into the UI.
    private void UpdateTswNote()
    {
        if (telemetrySource.SelectedIndex != 1)
        {
            tswNote.Text = "当前使用 X-Plane 12 数据源：UDP 端口与“连接”页里的设置生效。";
            return;
        }
        var typed = tswKeyPath.Text.Trim();
        if (typed.Length > 0)
        {
            tswNote.Text = File.Exists(typed)
                ? "已指定密钥文件。切换后记得保存并重启服务。"
                : $"指定的密钥文件不存在：{typed}";
            return;
        }
        var found = TswApiClient.KeyCandidates(null).FirstOrDefault(File.Exists);
        tswNote.Text = found is not null
            ? $"已自动找到密钥文件：{found}"
            : "还没找到密钥文件。请确认 TSW6 的 Steam 启动项已加入 -HTTPAPI 并至少启动过一次游戏；也可以手动指定路径。";
    }

    private void SyncBaseMapEnabled()
    {
        var custom = baseMap.SelectedIndex == 1;
        baseMapName.Enabled = custom;
        baseMapUrl.Enabled = custom;
        baseMapAttribution.Enabled = custom;
    }

    private void SyncProxyEnabled()
    {
        proxyUrl.Enabled = proxyMode.SelectedIndex == 2;
    }

    private void ShowNotes()
    {
        var notes = new List<string>();
        if (snapshot.Note is not null) notes.Add(snapshot.Note);
        notes.AddRange(original.Warnings());
        if (notes.Count == 0) return;
        banner.Text = string.Join(" ", notes);
        banner.Height = banner.PreferredHeight + 16;
        banner.Visible = true;
    }

    private void UpdateDiagnostics()
    {
        var config = ReadForm(showErrors: false);
        var backups = ConfigStore.Backups(snapshot.Path);
        var lines = new List<string>
        {
            $"配置文件：{snapshot.Path}",
            $"最近备份：{(backups.Count > 0 ? backups[0].Name : "（暂无）")}",
            $"数据源：{(config.UsesTsw ? "Train Sim World 6（本机 HTTP API）" : "X-Plane 12（UDP DATA）")}",
            $"保存后网页端口：{config.WebPort}",
            $"保存后 UDP 端口：{string.Join(", ", config.UdpPorts)}",
            $"iPad 访问地址（当前）：{lanUrl}",
            $"开机自动启动：{(autoStart.Checked ? "已启用" : "未启用")}",
            $"SimBrief：{(config.SimbriefUser.Length > 0 ? "已配置（" + Masked(config.SimbriefUser) + "）" : "未配置")}",
            $"OpenWeatherMap：{(config.WeatherApiKey.Length > 0 ? "已配置（" + Masked(config.WeatherApiKey) + "）" : "未配置（天气图层关闭）")}",
            $"天气代理：{OutboundHttp.Weather(config).Describe()}"
                + (config.WeatherProxyUser.Length > 0 ? $"（用户 {Masked(config.WeatherProxyUser)}）" : "")
                + (config.WeatherProxyMode == BridgeConfig.ProxyManual ? $" · {SecretProtection.Redact(config.WeatherProxyUrl)}" : ""),
            $"本地归档计划：{FlightPlanHistory.PeekCount(Path.Combine(ConfigStore.DirectoryFor(snapshot.Path), "flightplan-history.json"), Path.Combine(ConfigStore.DirectoryFor(snapshot.Path), "flightplan-cache.json"))} 份（最多 12 份，可在 iPad 的 SimBrief 面板里切换）",
            $"地面数据：{(config.XplanePath.Length > 0 ? $"已配置（{config.XplanePath}，缩放 ≥ {config.GroundMinZoom} 显示）" : "未配置（地面图层关闭）")}",
            $"航司 logo：{(config.AirlineLogoUrlTemplate.Length > 0 ? "使用自定义模板" : "仅三字码徽章（默认）")}",
            $"代理：{(config.ProxyMode switch { BridgeConfig.ProxySystem => "系统代理", BridgeConfig.ProxyManual => "手动 " + config.ProxyUrl, _ => "不使用" })}"
        };
        if (config.UsesTsw)
        {
            lines.Add($"TSW API：{(config.TswApiUrl.Length > 0 ? config.TswApiUrl : $"http://127.0.0.1:{TswApiClient.DefaultPort}（默认）")} · {config.TswPollHz} Hz");
            var typedKey = config.TswApiKeyPath;
            var foundKey = typedKey.Length > 0 ? "" : TswApiClient.KeyCandidates(null).FirstOrDefault(File.Exists) ?? "";
            var keyFile = typedKey.Length > 0 ? (File.Exists(typedKey) ? typedKey : typedKey + "（文件不存在）") : foundKey;
            lines.Add($"TSW 密钥：{(keyFile.Length > 0 ? keyFile : "未找到（需要 -HTTPAPI 启动一次游戏）")}");
        }
        diagnostics.Text = string.Join(Environment.NewLine, lines);
    }

    private static string Masked(string value) =>
        value.Length <= 2 ? new string('•', value.Length) : value[..1] + new string('•', Math.Min(6, value.Length - 2)) + value[^1];

    private BridgeConfig ReadForm(bool showErrors)
    {
        _ = showErrors;
        var config = original.Clone();
        config.WebPort = (int)Math.Clamp(webPort.Value, 1, 65535);
        config.UdpPorts = ParsePorts(udpPorts.Text, out _);
        config.SourceIp = sourceIp.Text.Trim();
        config.CustomBaseMapName = baseMap.SelectedIndex == 1 ? baseMapName.Text.Trim() : "";
        config.CustomBaseMapUrl = baseMap.SelectedIndex == 1 ? baseMapUrl.Text.Trim() : "";
        config.CustomBaseMapAttribution = baseMap.SelectedIndex == 1 ? baseMapAttribution.Text.Trim() : "";
        // Store the resolved installation root, not whatever level the user
        // happened to pick, so the ground layer works on the first try.
        var typedPath = xplanePath.Text.Trim();
        config.XplanePath = typedPath.Length > 0 ? AptDat.ResolveRoot(typedPath) : "";
        config.GroundMinZoom = (int)Math.Clamp(groundMinZoom.Value, 10, 19);
        config.AirlineLogoUrlTemplate = airlineLogoUrl.Text.Trim();
        config.ManualFolder = manualFolder.Text.Trim();
        config.SimbriefUser = simbriefUser.Text.Trim();
        config.SimbriefApiUrl = original.SimbriefApiUrl;
        config.WeatherApiKey = weatherKey.Text.Trim();
        config.WeatherProxyMode = weatherProxyMode.SelectedIndex switch
        {
            1 => BridgeConfig.ProxyNone,
            2 => BridgeConfig.ProxyManual,
            _ => BridgeConfig.ProxySystem
        };
        config.WeatherProxyUrl = weatherProxyUrl.Text.Trim();
        config.WeatherProxyUser = weatherProxyUser.Text.Trim();
        var password = weatherProxyPassword.Text;
        config.WeatherProxyPassword = password.Length == 0 ? "" : SecretProtection.Protect(password);
        config.ProxyMode = proxyMode.SelectedIndex switch
        {
            1 => BridgeConfig.ProxySystem,
            2 => BridgeConfig.ProxyManual,
            _ => BridgeConfig.ProxyNone
        };
        config.ProxyUrl = proxyUrl.Text.Trim();
        config.TelemetrySource = telemetrySource.SelectedIndex == 1 ? BridgeConfig.SourceTsw : BridgeConfig.SourceXPlane;
        config.TswApiUrl = tswApiUrl.Text.Trim().TrimEnd('/');
        config.TswApiKeyPath = tswKeyPath.Text.Trim();
        config.TswPollHz = (int)Math.Clamp(tswPollHz.Value, 1, 10);
        return config;
    }

    private static int[] ParsePorts(string text, out string error)
    {
        error = "";
        var separators = new[] { ',', '，', ';', '；', ' ', '\t', '\r', '\n' };
        var parts = text.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ports = new List<int>();
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var port) || port is < 1 or > 65535)
            {
                error = $"“{part}”不是 1–65535 的端口号。";
                return [];
            }
            ports.Add(port);
        }
        return [.. ports];
    }

    // ------------------------------------------------------------------ save
    private void Save()
    {
        if (!ValidateAll(out var firstBad))
        {
            status.Text = "有输入不合法，已在对应位置标出，尚未保存。";
            firstBad?.Focus();
            return;
        }
        var edited = ReadForm(showErrors: true);
        edited.Normalize();
        var busy = BusyPort(edited, out var busyMessage);
        if (busy)
        {
            status.Text = busyMessage;
            MessageBox.Show(this, busyMessage, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var portsChanged = edited.WebPort != original.WebPort || !edited.UdpPorts.SequenceEqual(original.UdpPorts);
        var retry = true;
        while (retry)
        {
            retry = false;
            try
            {
                BackupPath = ConfigStore.Save(snapshot, edited, cleanObsolete.Checked, snapshot.WriteTimeUtc, snapshot.Length);
                Result = edited;
                NewSnapshot = ConfigStore.Load(snapshot.Path);
            }
            catch (ConfigConflictException)
            {
                var choice = MessageBox.Show(this,
                    "配置文件在设置窗口打开期间被其它程序修改过。\n\n“是”：用这里的设置覆盖文件（会先备份当前文件）\n“否”：放弃本次修改并重新载入磁盘上的内容",
                    Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                if (choice == DialogResult.Cancel) return;
                if (choice == DialogResult.Yes)
                {
                    snapshot = ConfigStore.Load(snapshot.Path);
                    retry = true;
                }
                else
                {
                    snapshot = ConfigStore.Load(snapshot.Path);
                    LoadValues(snapshot.Config);
                    status.Text = "已重新载入磁盘上的配置，本次修改未保存。";
                    return;
                }
                continue;
            }
            catch (Exception error)
            {
                status.Text = "保存失败，配置文件没有被修改。";
                MessageBox.Show(this, $"保存失败，配置文件没有被修改：\n{error.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }

        RestartRequested = false;
        if (BackupPath is null or "")
        {
            MessageBox.Show(this,
                "设置内容与配置文件一致，没有重复写入，也没有新增备份。\n\n（开机自动启动这类不在配置文件里的选项已经立即生效。）",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        if (portsChanged)
        {
            var restart = MessageBox.Show(this,
                "配置已保存。\n\n端口改动需要重启服务才能生效（网页端口 / UDP 端口）。\n现在重启吗？重启过程中 iPad 会短暂断开，之后自动重连。",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            RestartRequested = restart == DialogResult.Yes;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    private void RestoreDefaults()
    {
        if (MessageBox.Show(this, "把界面上的所有设置恢复为默认值（还没有保存）。\n\n默认：数据源 X-Plane 12、网页端口 8080、UDP 端口 49000、不使用代理、无 SimBrief、无自定义底图。",
                Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        LoadValues(new BridgeConfig());
        foreach (var label in errors.Values) { label.Visible = false; label.Text = ""; }
        status.Text = "已恢复默认值（未保存）。点“保存”才会写入文件。";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (DialogResult == DialogResult.OK || e.CloseReason != CloseReason.UserClosing)
        {
            base.OnFormClosing(e);
            return;
        }
        var edited = ReadForm(showErrors: false);
        if (Same(edited, original))
        {
            base.OnFormClosing(e);
            return;
        }
        var choice = MessageBox.Show(this, "有修改还没有保存，要保存吗？", Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (choice == DialogResult.Cancel) { e.Cancel = true; return; }
        if (choice == DialogResult.Yes)
        {
            e.Cancel = true;
            Save();
            return;
        }
        base.OnFormClosing(e);
    }

    private static bool Same(BridgeConfig a, BridgeConfig b) =>
        a.WebPort == b.WebPort && a.UdpPorts.SequenceEqual(b.UdpPorts) && a.SourceIp == b.SourceIp &&
        a.CustomBaseMapName == b.CustomBaseMapName && a.CustomBaseMapUrl == b.CustomBaseMapUrl &&
        a.CustomBaseMapAttribution == b.CustomBaseMapAttribution && a.SimbriefUser == b.SimbriefUser &&
        a.ProxyMode == b.ProxyMode && a.ProxyUrl == b.ProxyUrl &&
        // These were missing before, so a window closed with only one of them changed used to look
        // "unchanged" and the edit was silently dropped.
        a.XplanePath == b.XplanePath && a.GroundMinZoom == b.GroundMinZoom &&
        a.AirlineLogoUrlTemplate == b.AirlineLogoUrlTemplate &&
        a.ManualFolder == b.ManualFolder &&
        a.WeatherApiKey == b.WeatherApiKey && a.WeatherProxyMode == b.WeatherProxyMode &&
        a.WeatherProxyUrl == b.WeatherProxyUrl && a.WeatherProxyUser == b.WeatherProxyUser &&
        a.WeatherProxyPassword == b.WeatherProxyPassword &&
        a.TelemetrySource == b.TelemetrySource && a.TswApiUrl == b.TswApiUrl &&
        a.TswApiKeyPath == b.TswApiKeyPath && a.TswPollHz == b.TswPollHz;

    // ------------------------------------------------------------------ validation
    private bool ValidateAll(out Control? firstBad)
    {
        foreach (var label in errors.Values) { label.Visible = false; label.Text = ""; }
        Control? first = null;
        var ok = true;

        void Fail(Control control, string message)
        {
            if (errors.TryGetValue(control, out var label))
            {
                label.Text = message;
                label.Visible = true;
            }
            first ??= control;
            ok = false;
        }

        var ports = ParsePorts(udpPorts.Text, out var portsError);
        if (portsError.Length > 0) Fail(udpPorts, portsError);
        else if (ports.Length == 0) Fail(udpPorts, "至少填写一个 UDP 端口。");
        else if (ports.Distinct().Count() != ports.Length) Fail(udpPorts, "有重复的 UDP 端口，请删掉重复项。");

        var source = sourceIp.Text.Trim();
        if (source.Length > 0 && (!IPAddress.TryParse(source, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork))
            Fail(sourceIp, "请填写合法的 IPv4 地址，或留空表示接受任意来源。");

        if (baseMap.SelectedIndex == 1)
        {
            var url = baseMapUrl.Text.Trim();
            if (url.Length == 0) Fail(baseMapUrl, "选择自定义底图时必须填写 URL。");
            else if (!url.Contains("{z}") || !url.Contains("{x}") || !url.Contains("{y}"))
                Fail(baseMapUrl, "URL 必须包含 {z}、{x}、{y} 三个占位符。");
            else if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                Fail(baseMapUrl, "URL 必须是 http:// 或 https:// 开头的完整地址。");
            else if (BridgeConfig.ContainsSecret(url))
                Fail(baseMapUrl, "URL 里带有疑似密钥或账号信息。请改用不含密钥的地址，避免明文保存。");
            else if (baseMapAttribution.Text.Trim().Length == 0)
                status.Text = "提示：自定义底图通常要求署名，建议填写“署名”。";
        }

        var user = simbriefUser.Text.Trim();
        if (user.Length > 0 && !IsValidSimbriefUser(user))
            Fail(simbriefUser, "请填写 1–7 位数字的 Pilot ID，或 Navigraph 用户名（字母、数字、点、下划线、连字符）。");

        var weather = weatherKey.Text.Trim();
        if (weather.Length > 0 && (weather.Length < 16 || weather.Any(char.IsWhiteSpace)))
            Fail(weatherKey, "OpenWeatherMap API Key 看起来不完整（应为 32 位字符）。请从 openweathermap.org 的 API keys 页面重新复制。");

        if (weatherProxyMode.SelectedIndex == 2)
        {
            var url = weatherProxyUrl.Text.Trim();
            if (url.Length == 0) Fail(weatherProxyUrl, "选择自定义代理时必须填写代理地址。");
            else if (url.Contains('@')) Fail(weatherProxyUrl, "代理地址里不要带用户名:密码，请改用下面的认证字段（会加密保存）。");
            else if (!Uri.TryCreate(url, UriKind.Absolute, out var proxyUri) || proxyUri.Scheme is not ("http" or "https") || proxyUri.Port <= 0)
                Fail(weatherProxyUrl, "代理地址格式应为 http://主机:端口，例如 http://127.0.0.1:7890。");
        }
        var proxyUser = weatherProxyUser.Text.Trim();
        var proxyPassword = weatherProxyPassword.Text;
        if (proxyUser.Length > 0 && proxyPassword.Length == 0) Fail(weatherProxyPassword, "填了代理用户名就要同时填写密码。");
        if (proxyPassword.Length > 0 && proxyUser.Length == 0) Fail(weatherProxyUser, "填了代理密码就要同时填写用户名。");
        if (proxyPassword.Length > 0 && SecretProtection.Protect(proxyPassword).Length == 0)
            Fail(weatherProxyPassword, "无法加密保存代理密码（Windows 凭据服务不可用）；请改用“使用系统代理”，或清空这里的认证信息。");

        var typed = xplanePath.Text.Trim();
        if (typed.Length > 0)
        {
            var xplane = AptDat.ResolveRoot(typed);
            if (!Directory.Exists(xplane)) Fail(xplanePath, "这个目录不存在。请选择包含 Resources 和 Custom Scenery 的 X-Plane 12 安装目录。");
            else if (!Directory.Exists(Path.Combine(xplane, "Resources")))
                Fail(xplanePath, "这个目录里没有 Resources 子目录，看起来不是 X-Plane 安装目录（可以选上一级，程序会自动往下找）。");
            else if (AptDat.AptFiles(xplane).Count == 0)
                Fail(xplanePath, "在 Resources 和 Custom Scenery 里都没有找到 apt.dat。如果这台电脑还没装机场数据，请先留空这一项。");
        }
        var logo = airlineLogoUrl.Text.Trim();
        if (logo.Length > 0 && (!Uri.TryCreate(logo.Replace("{icao}", "ABC"), UriKind.Absolute, out var logoUri) || logoUri.Scheme is not ("http" or "https")))
            Fail(airlineLogoUrl, "请填写 http(s) 开头的图片地址模板，例如 https://example.com/airlines/{icao}.png");
        if (logo.Length > 0 && BridgeConfig.ContainsSecret(logo))
            Fail(airlineLogoUrl, "地址里带有疑似密钥；这个值会下发到 iPad，请不要放长期密钥。");

        if (proxyMode.SelectedIndex == 2)
        {
            var url = proxyUrl.Text.Trim();
            if (url.Length == 0) Fail(proxyUrl, "选择手动代理时必须填写代理地址。");
            else if (url.Contains('@')) Fail(proxyUrl, "代理地址里不要带用户名:密码（会明文保存）。需要认证请改用“系统代理”。");
            else if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Port <= 0)
                Fail(proxyUrl, "代理地址格式应为 http://主机:端口，例如 http://127.0.0.1:7890。");
        }

        if (telemetrySource.SelectedIndex == 1)
        {
            var api = tswApiUrl.Text.Trim();
            if (api.Length > 0)
            {
                if (!Uri.TryCreate(api, UriKind.Absolute, out var apiUri) || apiUri.Scheme is not ("http" or "https"))
                    Fail(tswApiUrl, "API 地址应留空，或填 http://主机:端口 形式的完整地址。");
                else if (BridgeConfig.ContainsSecret(api))
                    Fail(tswApiUrl, "地址里带有疑似密钥参数。默认地址不需要密钥，请留空。");
            }
            var key = tswKeyPath.Text.Trim();
            if (key.Length > 0 && !File.Exists(key))
                Fail(tswKeyPath, "这个文件不存在。留空则由程序自动查找默认位置。");
        }

        firstBad = first;
        if (!ok) { status.Text = "有输入不合法，请按红色提示修正后再保存。"; return false; }
        return true;
    }

    internal static bool IsValidSimbriefUser(string user) =>
        (user.Length is >= 1 and <= 7 && user.All(char.IsAsciiDigit)) ||
        (user.Length is >= 3 and <= 40 && user.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'));

    private bool BusyPort(BridgeConfig edited, out string message)
    {
        message = "";
        // Only probe ports that actually change: the running service already owns
        // the current ones and would look "busy" against itself.
        if (edited.WebPort != original.WebPort && !TcpFree(edited.WebPort))
        {
            message = $"网页端口 {edited.WebPort} 已被占用：可能是另一个程序（或另一个桥接器实例）。请换一个端口。";
            return true;
        }
        foreach (var port in edited.UdpPorts.Except(original.UdpPorts))
        {
            if (!UdpFree(port))
            {
                message = $"UDP 端口 {port} 已被占用：可能是 X-Plane 自身或其他插件。请换一个端口。";
                return true;
            }
        }
        return false;
    }

    private static bool TcpFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch { return false; }
    }

    private static bool UdpFree(int port)
    {
        try
        {
            using var client = new UdpClient();
            client.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            return true;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ route test
    private int cooldownSeconds;

    private async Task TestRouteAsync()
    {
        if (!ValidateAll(out _)) return;
        var probe = ReadForm(showErrors: false);
        if (probe.SimbriefUser.Length == 0)
        {
            testResult.Text = "先填写 SimBrief Pilot ID 或用户名。";
            return;
        }
        testRoute.Enabled = false;
        testResult.Text = $"正在向 SimBrief 请求…（{OutboundHttp.DescribeEffective(OutboundHttp.Global(probe))}）";
        try
        {
            var client = new SimBriefClient(probe, persistCache: false);
            var status = await client.GetAsync(refresh: true, CancellationToken.None);
            var plan = status["plan"] as System.Text.Json.Nodes.JsonObject;
            var available = status["available"]?.GetValue<bool>() == true && plan is not null;
            var route = OutboundHttp.DescribeEffective(OutboundHttp.Global(probe));
            if (!available)
            {
                testResult.Text = $"获取失败：{status["error"]?.GetValue<string>() ?? "未知错误"}（{route}）";
            }
            else
            {
                var origin = plan!["origin"]?["ident"]?.GetValue<string>() ?? "—";
                var destination = plan["destination"]?["ident"]?.GetValue<string>() ?? "—";
                var count = (plan["waypoints"] as System.Text.Json.Nodes.JsonArray)?.Count ?? 0;
                var distance = plan["distanceNm"]?.GetValue<double?>();
                testResult.Text = count > 0
                    ? $"成功：{origin} → {destination}，{count} 个航点{(distance is > 0 ? $"，{Math.Round(distance.Value)} NM" : "")}（{route}）。点“保存”后 iPad 上会显示这条航路。"
                    : $"成功，但这份计划里没有航点明细，只会显示 {origin} → {destination} 的直连线。要显示完整航路，请在 SimBrief 里启用 Detailed Navlog（详细航路日志）后重新生成计划，再回来测试。（{route}）";
            }
        }
        catch (Exception error)
        {
            testResult.Text = $"测试失败：{OutboundHttp.Explain(error, OutboundHttp.Global(probe))}";
        }
        finally
        {
            cooldownSeconds = 15;
            cooldown.Start();
            testRoute.Text = $"测试并获取航路（{cooldownSeconds}s）";
        }
    }

    private void OnCooldownTick(object? sender, EventArgs e)
    {
        cooldownSeconds--;
        if (cooldownSeconds <= 0)
        {
            cooldown.Stop();
            testRoute.Enabled = true;
            testRoute.Text = "测试并获取航路";
            return;
        }
        testRoute.Text = $"测试并获取航路（{cooldownSeconds}s）";
    }

    // Downloads the smallest possible tile to check the key and the proxy.
    private int weatherCooldownSeconds;
    private readonly System.Windows.Forms.Timer weatherCooldown = new() { Interval = 1000 };

    private async Task TestWeatherKeyAsync()
    {
        var key = weatherKey.Text.Trim();
        if (key.Length == 0)
        {
            testWeatherResult.Text = "先填写 OpenWeatherMap API Key。";
            return;
        }
        testWeatherKey.Enabled = false;
        testWeatherResult.Text = "正在测试…";
        try
        {
            var probe = ReadForm(showErrors: false);
            var url = WeatherTiles.UpstreamUrl("clouds", key, 0, 0, 0);
            var choice = OutboundHttp.Weather(probe);
            // Never dispose the shared client here: it is reused by the map tile
            // proxy as well (disposing it made every later request fail).
            using var response = await OutboundHttp.SendAsync(choice, client => client.GetAsync(url));
            if (response.IsSuccessStatusCode)
            {
                var size = (await response.Content.ReadAsByteArrayAsync()).Length;
                testWeatherResult.Text = $"成功：拿到一张测试瓦片（{size} 字节，{choice.Describe()}）。点“保存”后就能在 iPad 上打开天气图层。";
            }
            else if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                testWeatherResult.Text = "失败：OpenWeatherMap 拒绝了这个 Key。新申请的 Key 有时要等十几分钟才生效，也可能复制时多了空格。";
            }
            else
            {
                testWeatherResult.Text = $"失败：OpenWeatherMap 返回 HTTP {(int)response.StatusCode}（经{choice.Describe()}）。";
            }
        }
        catch (Exception error)
        {
            var mode = OutboundHttp.Weather(ReadForm(showErrors: false)).Describe();
            testWeatherResult.Text = $"连接失败：{SecretProtection.Redact(error.Message)}（当前 {mode}；如果代理需要认证，请填写用户名与密码）";
        }
        finally
        {
            weatherCooldownSeconds = 5;
            weatherCooldown.Start();
            testWeatherKey.Text = $"测试连接（{weatherCooldownSeconds}s）";
        }
    }

    private void OnWeatherCooldownTick(object? sender, EventArgs e)
    {
        weatherCooldownSeconds--;
        if (weatherCooldownSeconds <= 0)
        {
            weatherCooldown.Stop();
            testWeatherKey.Enabled = true;
            testWeatherKey.Text = "测试连接";
            return;
        }
        testWeatherKey.Text = $"测试连接（{weatherCooldownSeconds}s）";
    }

    // ------------------------------------------------------------- 手册文件夹
    private void BrowseForManuals()
    {
        try
        {
            var initial = manualFolder.Text.Trim();
            if (initial.Length == 0 || !Directory.Exists(initial))
                initial = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            using var picker = new FolderPicker(
                "选择手册文件夹",
                "选中存放 PDF 手册的文件夹（子文件夹也会一并列出）。也可以直接把路径粘贴到下面的输入框。",
                initial,
                DescribeManualFolder);
            if (picker.ShowDialog(this) == DialogResult.OK && picker.SelectedPath.Length > 0)
                manualFolder.Text = picker.SelectedPath;
        }
        catch (Exception error)
        {
            MessageBox.Show(this,
                $"打开文件夹选择窗口时出错：{SecretProtection.Redact(error.Message)}\n\n可以把路径直接粘贴到“手册文件夹”输入框里，效果一样。",
                "X-Plane EFB Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    internal static (string Text, bool Ok) DescribeManualFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return ("文件夹不存在", false);
            var count = 0;
            foreach (var _ in Directory.EnumerateFiles(path, "*.pdf", SearchOption.AllDirectories))
            {
                count += 1;
                if (count > Manuals.MaxManuals) break;
            }
            if (count == 0) return ("这个文件夹里没有找到 PDF", false);
            return (count > Manuals.MaxManuals
                ? $"找到 600 个以上 PDF（只列出前 {Manuals.MaxManuals} 个）"
                : $"找到 {count} 个 PDF", true);
        }
        catch (Exception error)
        {
            return ($"无法读取：{SecretProtection.Redact(error.Message)}", false);
        }
    }

    // ------------------------------------------------------------ X-Plane 目录
    // Plain WinForms picker: the Windows shell folder dialog loads third party
    // shell extensions and a broken one takes the whole bridge down with it.
    private void BrowseForXPlane()
    {
        try
        {
            var initial = AptDat.ResolveRoot(xplanePath.Text.Trim());
            if (initial.Length == 0) initial = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            using var picker = new FolderPicker(
                "选择 X-Plane 12 安装目录",
                "选中包含 Resources 与 Custom Scenery 的那一层（通常叫 X-Plane 12）。也可以直接把路径粘贴到下面的输入框。",
                initial,
                DescribeXPlaneFolder);
            if (picker.ShowDialog(this) == DialogResult.OK && picker.SelectedPath.Length > 0)
            {
                xplanePath.Text = picker.SelectedPath;
                RefreshGroundHint();
            }
        }
        catch (Exception error)
        {
            // Even a picker problem must not be able to end the process.
            MessageBox.Show(this,
                $"打开文件夹选择窗口时出错：{SecretProtection.Redact(error.Message)}\n\n可以把路径直接粘贴到“X-Plane 12 安装目录”输入框里，效果一样。",
                "X-Plane EFB Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RefreshGroundHint()
    {
        if (groundHint.IsDisposed) return;
        var text = xplanePath.Text.Trim();
        if (text.Length == 0)
        {
            groundHint.Visible = false;
            groundHint.Text = "";
            return;
        }
        var (message, ok) = DescribeXPlaneFolder(text);
        groundHint.Text = message;
        groundHint.ForeColor = ok ? Color.FromArgb(21, 115, 71) : Color.FromArgb(178, 34, 34);
        groundHint.Visible = true;
    }

    internal static (string Text, bool Ok) DescribeXPlaneFolder(string path)
    {
        var typed = path.Trim().Trim('"');
        var root = AptDat.ResolveRoot(typed);
        if (root.Length == 0) return ("请选择包含 Resources 与 Custom Scenery 的那一层目录。", false);
        if (!Directory.Exists(root)) return ($"这个文件夹不存在：{root}", false);
        var files = AptDat.AptFiles(root);
        if (files.Count == 0)
            return ($"在这个文件夹里没有找到 apt.dat。请选择包含 Resources 的 X-Plane 12 安装目录（当前按 {root} 查找）。", false);
        long bytes = 0;
        foreach (var file in files)
        {
            try { bytes += new FileInfo(file).Length; } catch { }
        }
        var megabytes = bytes / 1024.0 / 1024.0;
        var extra = File.Exists(AptDat.DefaultAptDat(root)) ? "" : "（缺少默认机场包，只会索引自定义机场）";
        var moved = root.Equals(typed, StringComparison.OrdinalIgnoreCase) ? "" : $"，实际按 {root} 读取";
        return ($"✓ 找到 {files.Count} 个 apt.dat，共 {megabytes:0.0} MB{extra}{moved}。保存后自动建立机场索引，iPad 上打开“🛬”并放大到设定级别即可看到。", true);
    }
}
