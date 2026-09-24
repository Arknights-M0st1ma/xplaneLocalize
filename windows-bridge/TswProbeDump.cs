using System.Text;
using System.Text.Json.Nodes;

namespace XPlaneEfbBridge;

/// <summary>
/// Diagnostic switch: talks to the TSW6 HTTP API and writes everything it can learn to a text file.
///
/// This exists because the node, endpoint and field names used by the TSW integration come from
/// third-party documentation (docs/TSW6-TELEMETRY.md §3.4) rather than from a document we could
/// read, and because the machine that develops this bridge is not the machine that runs the game:
/// one run of this command replaces the entire "poke at it with curl" checklist (§9.2) and gives
/// the exact endpoint list the installed game build actually exposes.
///
///   XPlaneEfbBridge.exe --tsw-probe "<输出.txt>" ["%LOCALAPPDATA%\XPlaneEfbBridge\bridge-config.json"]
///
/// The API key is never printed, never written to the report, and never included in an error
/// message; only the path of the file it was read from is reported.
/// </summary>
internal static class TswProbeDump
{
    // Candidates for the two values the map needs. Tried and reported individually so a partial
    // result still tells us which one to use.
    private static readonly (string Label, string Node, string Endpoint)[] PositionCandidates =
    [
        ("位置（文档首选）", "DriverAid", "PlayerInfo"),
        ("位置（备选）", "DriverAid", "Data"),
        ("位置（备选）", "CurrentDrivableActor", "LatLon"),
        ("位置（备选）", "CurrentDrivableActor", "Function.LatLon")
    ];

    private static readonly (string Label, string Node, string Endpoint)[] OtherCandidates =
    [
        ("速度", "CurrentDrivableActor", "Function.HUD_GetSpeed"),
        ("速度（备选）", "CurrentDrivableActor", "Speed"),
        ("限速与信号", "DriverAid", "Data")
    ];

    public static int Run(string output, string configPath)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* no console attached */ }
        return RunAsync(output, configPath).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(string output, string configPath)
    {
        var config = new BridgeConfig();
        var note = "";
        try
        {
            if (File.Exists(configPath)) config = ConfigStore.Load(configPath).Config;
            else note = $"（没有找到配置文件 {configPath}，使用默认设置）";
        }
        catch (Exception error)
        {
            note = $"（读取配置失败：{error.Message}；使用默认设置）";
        }

        var report = new StringBuilder();
        void Line(string text = "")
        {
            report.AppendLine(text);
            Console.WriteLine(text);
        }

        using var client = new TswApiClient(config.TswApiKeyPath, config.TswApiUrl);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;

        Line("Train Sim World 6 遥测探针");
        Line($"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        Line($"配置文件：{(File.Exists(configPath) ? configPath : "（不存在，用默认值）")}{note}");
        Line($"数据源设置：{config.TelemetrySource}（本探针不依赖该设置）");
        Line();

        Line("── 1. 可达性与密钥 ──");
        Line($"API 地址：{client.BaseUrl}{(client.BaseNote.Length > 0 ? " · " + client.BaseNote : "（默认值）")}");
        Line($"环境变量 {TswApiClient.BaseOverrideVariable}：{(Environment.GetEnvironmentVariable(TswApiClient.BaseOverrideVariable) is { Length: > 0 } over ? over : "（未设置）")}");
        Line($"密钥候选位置：");
        foreach (var path in TswApiClient.KeyCandidates(config.TswApiKeyPath))
        {
            Line($"  [{(File.Exists(path) ? "找到" : "没有")}] {path}");
        }

        var probe = await client.ProbeAsync(token);
        Line($"TCP 连接：{(probe.TcpReachable ? "成功" : "失败")}");
        Line($"密钥可用：{(probe.KeyConfigured ? "是" : "否")}");
        if (probe.KeyPath.Length > 0) Line($"密钥来源：{probe.KeyPath}（只报告路径，不打印内容）");
        else Line($"密钥说明：{probe.KeyNote}");
        if (probe.ListStatus > 0) Line($"/list 响应：HTTP {probe.ListStatus} · {(probe.ListOk ? "Result:Success" : probe.ListError)}");
        if (probe.InfoAvailable)
        {
            Line($"/info：可用 · 游戏 {probe.GameName} · Worker {probe.Worker} · build {probe.GameBuild} · API {probe.ApiVersion}");
        }        else
        {
            Line("/info：不可用或不存在（公开的逆向规范里也没有该路由，属预期情况；/list 才是权威探针）");
        }
        Line($"结论：{probe.Summary}");
        Line();
        if (!probe.TcpReachable || !probe.ListOk)
        {
            Line("API 不可用，后面的端点清单无法获取。");
            Line("排查顺序：① 游戏是否正在运行；② Steam 启动项是否加入 -HTTPAPI；③ 是否重启过游戏（重启后密钥文件会重新生成）；④ 主机版没有该 API。");
            return Write(output, report) ? 1 : 1;
        }

        Line("── 2. 根节点 ──");
        var nodes = await client.ListNodesAsync("", token);
        Line(nodes.Count > 0 ? string.Join("、", nodes) : "（未列出根节点）");
        Line();

        Line("── 3. 关键节点的端点清单 ──");
        foreach (var node in new[] { "DriverAid", "CurrentDrivableActor", "CurrentFormation", "TimeOfDay" })
        {
            var endpoints = await client.ListEndpointsAsync(node, token);
            var children = await client.ListNodesAsync(node, token);
            Line($"[{node}] 端点 {endpoints.Count} 个，子节点 {children.Count} 个");
            if (endpoints.Count > 0) Line("  端点：" + string.Join("、", endpoints));
            if (children.Count > 0) Line("  子节点：" + string.Join("、", children));
        }
        Line();

        Line("── 4. 候选端点逐个试读 ──");
        foreach (var (label, node, endpoint) in PositionCandidates.Concat(OtherCandidates))
        {
            var response = await client.GetAsync(node, endpoint, token);
            Line($"[{label}] {node}.{endpoint}");
            Line(response.Ok ? "  HTTP 200 · Result:Success" : $"  失败：HTTP {response.Status} · {response.Message}{(response.ErrorCode.Length > 0 ? " · " + response.ErrorCode : "")}");
            if (response.Ok)
            {
                var payload = response.Values ?? response.Root;
                Line("  Values 字段：" + Describe(payload));
                Line("  原始响应：" + Trim(response.Raw, 900));
                AddExtracted(Line, payload);
            }
            Line();
        }

        Line("── 5. 结论 ──");
        var position = await FindPositionAsync(client, token);
        Line(position is null
            ? "没有找到可用的位置端点（已试 /get 的候选端点与 /subscription 订阅）。"
                + "游戏停在主菜单或加载中时本来就没有坐标，请进入一条线路后重跑；仍然没有的话，请把上面的端点清单发回以便修正候选名。"
            : $"可用的位置端点：{position}");
        Line("把本文件完整发回即可；其中不含密钥，但可能含你的账号名（playerProfileName），如介意可先删掉那几行。");
        Write(output, report);
        return position is null ? 2 : 0;
    }

    private static async Task<string?> FindPositionAsync(TswApiClient client, CancellationToken token)
    {
        foreach (var (_, node, endpoint) in PositionCandidates)
        {
            var response = await client.GetAsync(node, endpoint, token);
            if (!response.Ok) continue;
            if (TswMapper.Coordinates(response.Values) is (double lat, double lon))
                return $"{node}.{endpoint} → {lat:0.00000}, {lon:0.00000}";
        }
        // The subscription shape, in the same order the bridge uses it: some TSW6 builds expose the
        // position only here, and a report that stops at /get would call that "no position endpoint".
        const int subscriptionId = 4242;
        foreach (var (_, node, endpoint) in PositionCandidates)
        {
            var registered = await client.SubscribeAsync(node, endpoint, subscriptionId, token);
            if (!registered.Ok) continue;
            var read = await client.ReadSubscriptionAsync(subscriptionId, token);
            if (!read.Ok) continue;
            var values = TswMapper.SubscriptionValues(read.Root ?? read.Values);
            if (values is null) continue;
            if (TswMapper.Coordinates(values) is (double subLat, double subLon))
                return $"{node}.{endpoint}（订阅）→ {subLat:0.00000}, {subLon:0.00000}";
        }
        return null;
    }

    // Reports what the mapper would extract from this payload, so the report answers the question the
    // reader actually has - "does the bridge get a speed out of this?" - instead of leaving them to
    // compare JSON by eye. The same lookup helpers the mapper uses are used here on purpose: if a
    // candidate name is wrong, this says so rather than paraphrasing.
    private static void AddExtracted(Action<string> line, JsonObject? payload)
    {
        if (payload is null)
        {
            line("  桥接器从这个响应里取不到任何字段（没有可解析的对象）。");
            return;
        }

        var found = new List<string>();
        var latitude = TswMapper.FindDeep(payload, ["latitude", "lat", "Latitude", "Lat"]);
        var longitude = TswMapper.FindDeep(payload, ["longitude", "lon", "lng", "Longitude", "Lon"]);
        if (latitude is double lat) found.Add($"latitude={lat}");
        if (longitude is double lon) found.Add($"longitude={lon}");

        // Same candidate names as TswMapper.Number / AddSpeedLimit, including the {value: …} wrapper.
        foreach (var (name, keys) in new (string, string[])[]
        {
            ("Speed (ms)", ["Speed (ms)", "Speed (m/s)", "Speed", "speed", "SpeedMS"]),
            ("speedLimit", ["speedLimit", "SpeedLimit", "currentSpeedLimit"]),
            ("nextSpeedLimit", ["nextSpeedLimit", "NextSpeedLimit"]),
            ("trackMaxSpeed", ["trackMaxSpeed", "TrackMaxSpeed"]),
            ("serviceMaxSpeed", ["serviceMaxSpeed", "ServiceMaxSpeed"]),
            ("formationMaxSpeed", ["formationMaxSpeed", "FormationMaxSpeed"]),
            ("distanceToNextSpeedLimit", ["distanceToNextSpeedLimit", "distanceToNextLimit"]),
            ("distanceToSignal", ["distanceToSignal", "distanceToNextSignal"]),
            ("gradient", ["gradient", "Gradient"])
        })
        {
            var node = TswMapper.Find(payload, keys);
            if (node is null) continue;
            var value = TswMapper.Number(node);
            if (value is null && node is JsonObject wrapper) value = TswMapper.Number(TswMapper.Find(wrapper, ["value", "Value", "speed", "Speed"]));
            if (value is double number) found.Add($"{name}={number}");
        }
        foreach (var (name, keys) in new (string, string[])[]
        {
            ("currentServiceName", ["currentServiceName", "serviceName"]),
            ("playerProfileName", ["playerProfileName", "profileName"]),
            ("signalAspectClass", ["signalAspectClass", "signalAspect"])
        })
        {
            var text = TswApiClient.Text(payload, keys);
            if (text.Length > 0) found.Add($"{name}=\"{text}\"");
        }

        line(found.Count > 0
            ? "  桥接器可用的字段：" + string.Join("、", found)
            : "  桥接器从这个响应里取不到任何字段（字段名可能不同）。");
    }

    private static string Describe(JsonObject? node)
    {
        if (node is null) return "（没有 Values 对象）";
        var parts = new List<string>();
        foreach (var item in node)
        {
            parts.Add($"{item.Key}:{Kind(item.Value)}");
            if (parts.Count >= 30) { parts.Add("…"); break; }
        }
        return string.Join(", ", parts);
    }

    private static string Kind(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "对象",
        JsonArray array => $"数组({array.Count})",
        JsonValue => "值",
        _ => "?"
    };

    private static string Trim(string text, int limit) =>
        text.Length <= limit ? text : text[..limit] + "…";

    private static bool Write(string output, StringBuilder report)
    {
        try
        {
            File.WriteAllText(output, report.ToString(), new UTF8Encoding(false));
            Console.WriteLine();
            Console.WriteLine($"报告已写入：{Path.GetFullPath(output)}");
            return true;
        }
        catch (Exception error)
        {
            Console.WriteLine($"无法写入报告文件：{error.Message}");
            return false;
        }
    }
}
