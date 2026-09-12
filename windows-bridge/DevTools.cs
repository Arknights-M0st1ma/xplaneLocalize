using Microsoft.Win32;
using System.Net.NetworkInformation;

namespace XPlaneEfbBridge;

// The exe icon (set through <ApplicationIcon>) is reused for the tray icon and
// the settings window, so file manager, taskbar and tray all match.
internal static class AppIcon
{
    public static System.Drawing.Icon Load()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon is not null) return icon;
            }
        }
        catch { }
        return SystemIcons.Application;
    }
}

// "开机自动启动" is a registry value, not part of the JSON file.
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "XPlaneEfbBridge";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunName) is not null;
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(RunName, false);
    }
}

// Hidden switches used to verify the settings code without a human clicking the
// window. They are not part of the normal user workflow.
internal static class ConfigSelfTest
{
    public static int Run(string directory)
    {
        var lines = new List<string>();
        var ok = true;

        void Check(string name, bool condition, string detail = "")
        {
            lines.Add($"{(condition ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? $" — {detail}" : "")}");
            ok &= condition;
        }

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "bridge-config.json");

        // 1. A legacy hand edited file: custom UDP port, obsolete keys, own key.
        File.WriteAllText(path, """
            {
              "UdpPorts": [49003],
              "WebPort": 8080,
              "SourceIp": "",
              "AuthorizedChartTileUrl": "",
              "NavigraphExternalUrl": "https://charts.navigraph.com/",
              "MyOwnKey": "keep-me"
            }
            """);
        var snapshot = ConfigStore.Load(path);
        Check("读取旧配置的 UDP 端口", snapshot.Config.UdpPorts.SequenceEqual([49003]), string.Join(",", snapshot.Config.UdpPorts));
        Check("读取时不改动文件", !File.ReadAllText(path).Contains("\"Owner\""));

        var edited = snapshot.Config.Clone();
        edited.SimbriefUser = "123456";
        edited.ProxyMode = BridgeConfig.ProxySystem;
        var backup = ConfigStore.Save(snapshot, edited, cleanObsolete: false, snapshot.WriteTimeUtc, snapshot.Length);
        var text = File.ReadAllText(path);
        Check("保存前生成备份", backup.Length > 0 && File.Exists(backup), Path.GetFileName(backup));
        Check("未知键被保留", text.Contains("MyOwnKey"));
        Check("废弃键默认不删除", text.Contains("NavigraphExternalUrl"));
        Check("新值写入文件", text.Contains("123456") && text.Contains("\"system\""));
        Check("中文不被转义", !text.Contains("\\u"));

        // 2. Explicit cleanup of the obsolete keys, backup again.
        var snapshot2 = ConfigStore.Load(path);
        var before = Directory.GetFiles(directory, "*.bak").Length;
        var same = ConfigStore.Save(snapshot2, snapshot2.Config, cleanObsolete: false, snapshot2.WriteTimeUtc, snapshot2.Length);
        Check("内容未变时不重复写入", same.Length == 0 && Directory.GetFiles(directory, "*.bak").Length == before);
        ConfigStore.Save(snapshot2, snapshot2.Config, cleanObsolete: true, snapshot2.WriteTimeUtc, snapshot2.Length);
        var text2 = File.ReadAllText(path);
        Check("显式清理废弃键", !text2.Contains("NavigraphExternalUrl") && text2.Contains("MyOwnKey"));

        // 3. The corrupted "two objects glued together" file this user has seen.
        File.WriteAllText(path, "{ \"UdpPorts\": [49000], \"WebPort\": 8080 } { \"UdpPorts\": [49005], \"WebPort\": 9090 }");
        var snapshot3 = ConfigStore.Load(path);
        Check("拼接文件按有效对象读取", snapshot3.Config.WebPort == 9090 && snapshot3.Config.UdpPorts.SequenceEqual([49005]));
        Check("拼接文件已备份", snapshot3.BackupPath is not null && File.Exists(snapshot3.BackupPath), Path.GetFileName(snapshot3.BackupPath ?? ""));

        // 4. Complete garbage keeps the file and falls back to defaults.
        File.WriteAllText(path, "这不是 JSON");
        var snapshot4 = ConfigStore.Load(path);
        Check("无法解析时使用默认值", snapshot4.Config.WebPort == 8080 && snapshot4.Config.UdpPorts.SequenceEqual([49000]));
        Check("无法解析时已备份", snapshot4.BackupPath is not null && File.Exists(snapshot4.BackupPath));
        Check("无法解析时原文件保留", File.ReadAllText(path) == "这不是 JSON");
        Check("无法解析时有提示", snapshot4.Note is not null);

        // 5. Someone edits the file while the settings window is open.
        var snapshot5 = ConfigStore.Load(path);
        File.WriteAllText(path, "{ \"WebPort\": 8081 }");
        var conflicted = false;
        try { ConfigStore.Save(snapshot5, snapshot5.Config, false, snapshot5.WriteTimeUtc, snapshot5.Length); }
        catch (ConfigConflictException) { conflicted = true; }
        Check("外部修改时拒绝覆盖", conflicted);

        // 6. Validation helpers used by the window.
        Check("SimBrief Pilot ID 判定", SettingsForm.IsValidSimbriefUser("123456"));
        Check("SimBrief 用户名判定", SettingsForm.IsValidSimbriefUser("some.pilot-01") && !SettingsForm.IsValidSimbriefUser("bad name"));
        Check("密钥识别", BridgeConfig.ContainsSecret("https://host/x?key=abc") && !BridgeConfig.ContainsSecret("https://tiles.example.com/{z}/{x}/{y}.png"));

        // 7. OFP parsing must never throw on the shapes SimBrief really sends.
        //    "navlog" is an empty string whenever Detailed Navlog is switched off,
        //    and the fix list can arrive as a single object instead of an array.
        static System.Text.Json.Nodes.JsonNode? Node(string json) => System.Text.Json.Nodes.JsonNode.Parse(json);
        const string positioned = "\"origin\":{\"icao_code\":\"ZSPD\",\"pos_lat\":\"31.143400\",\"pos_long\":\"121.805200\"},"
                                + "\"destination\":{\"icao_code\":\"RJAA\",\"pos_lat\":\"35.764700\",\"pos_long\":\"140.386400\"}";
        const string unpositioned = "\"origin\":{\"icao_code\":\"ZSPD\"},\"destination\":{\"icao_code\":\"RJAA\"}";
        static System.Text.Json.Nodes.JsonNode? Ofp(string prefix, string body) => Node("{" + prefix + (body.Length > 0 ? "," + body : "") + "}");

        var emptyNavlog = SimBriefClient.Parse(Ofp(positioned, "\"general\":{\"route\":\"AND BS G455\"},\"navlog\":\"\""));
        Check("navlog 为空字符串不报错", emptyNavlog is not null, "起降机场直连");
        Check("navlog 为空时航点为 0", (emptyNavlog?["waypoints"] as System.Text.Json.Nodes.JsonArray)?.Count == 0);
        Check("navlog 为空时仍保留航路文字", emptyNavlog?["route"]?.GetValue<string>() == "AND BS G455");

        var singleFix = SimBriefClient.Parse(Ofp(positioned, "\"navlog\":{\"fix\":{\"ident\":\"AND\",\"type\":\"wpt\",\"pos_lat\":\"31.45\",\"pos_long\":\"122.1\"}}"));
        Check("单个航点对象也能解析", (singleFix?["waypoints"] as System.Text.Json.Nodes.JsonArray)?.Count == 1);

        // json=v2 sends navlog as a flat array (this is what a real EDDF-VHHH
        // plan returns); the nested navlog.fix form above must keep working too.
        var flatNavlog = SimBriefClient.Parse(Ofp(positioned,
            "\"navlog\":[{\"ident\":\"DF101\",\"type\":\"wpt\",\"pos_lat\":\"49.781692\",\"pos_long\":\"8.541486\",\"via_airway\":\"SULU4L\",\"altitude_feet\":\"6900\",\"stage\":\"CLB\"},"
            + "{\"ident\":\"COSJE\",\"type\":\"wpt\",\"pos_lat\":\"49.717531\",\"pos_long\":\"9.947000\",\"via_airway\":\"SULU4L\"},"
            + "{\"ident\":\"VHHH\",\"type\":\"apt\",\"pos_lat\":\"22.308889\",\"pos_long\":\"113.914722\"}]"));
        Check("扁平 navlog 数组解析", (flatNavlog?["waypoints"] as System.Text.Json.Nodes.JsonArray)?.Count == 3);
        Check("扁平 navlog 的航路与高度", flatNavlog?["waypoints"]?[0]?["via"]?.GetValue<string>() == "SULU4L"
            && flatNavlog?["waypoints"]?[0]?["altitudeFt"]?.GetValue<double?>() == 6900);
        Check("扁平 navlog 的航点类型", flatNavlog?["waypoints"]?[2]?["type"]?.GetValue<string>() == "apt");

        var oddTypes = SimBriefClient.Parse(Ofp(positioned,
            "\"times\":\"9000\",\"general\":\"x\",\"navlog\":{\"fix\":[{\"ident\":\"A\",\"pos_lat\":\"31.5\",\"pos_long\":\"122.2\"},"
            + "{\"ident\":\"B\",\"pos_lat\":\"31.6\",\"pos_long\":\"122.3\",\"altitude_feet\":\"12000\"},{\"ident\":\"C\",\"pos_lat\":\"31.7\"}]}"));
        Check("异常类型不影响解析", (oddTypes?["waypoints"] as System.Text.Json.Nodes.JsonArray)?.Count == 2, "缺坐标的航点被跳过");
        Check("航点高度解析", oddTypes?["waypoints"]?[1]?["altitudeFt"]?.GetValue<double?>() == 12000);

        var noGeometry = SimBriefClient.Parse(Ofp(unpositioned, "\"general\":{\"route\":\"AND BS\"},\"navlog\":\"\""));
        Check("没有任何坐标时返回空", noGeometry is null, "界面会给出“只有航路文字”的提示");

        var garbage = SimBriefClient.Parse(Node("""[1,2,3]"""));
        Check("顶层不是对象时返回空", garbage is null);

        // 8. OFP text extraction (the viewer in the EFB must never receive HTML).
        var ofp = SimBriefClient.ExtractOfpText(Ofp(positioned,
            "\"text\":{\"plan_html\":\"<div><pre><b>[ OFP ]</b> EDDF-VHHH\\n  <a href='#'>page 1/4</a></pre></div>\",\"tlr_section\":\"TAKEOFF AND LANDING REPORT\"}"));
        Check("OFP 文本已提取", ofp.Contains("[ OFP ] EDDF-VHHH") && ofp.Contains("page 1/4"), $"{ofp.Length} 字符");
        Check("OFP 文本不含标签/注释", !ofp.Contains('<') && !ofp.Contains("href"));
        Check("OFP 含起降性能报告", ofp.Contains("起降性能报告") && ofp.Contains("TAKEOFF AND LANDING REPORT"));

        // 9. Local plan archive: history list, owner scoping, switching, cap.
        var historyPath = Path.Combine(directory, "flightplan-history.json");
        var legacyPath = Path.Combine(directory, "flightplan-cache.json");
        var legacyPlan = SimBriefClient.Parse(Ofp(positioned, ""));
        File.WriteAllText(legacyPath, new System.Text.Json.Nodes.JsonObject
        {
            ["owner"] = "OWNER",
            ["fetchedAt"] = 1,
            ["plan"] = legacyPlan!.DeepClone()
        }.ToJsonString());
        var history = new FlightPlanHistory(historyPath, legacyPath);
        Check("旧缓存导入为历史条目", history.Count == 1 && history.ActivePlan("OWNER") is not null);
        Check("旧缓存文件已清理", !File.Exists(legacyPath));
        Check("其他账号看不到", history.ActivePlan("OTHER") is null && history.List("OTHER").Count == 0);

        var planA = SimBriefClient.Parse(Ofp("\"origin\":{\"icao_code\":\"EDDF\",\"pos_lat\":\"50.03\",\"pos_long\":\"8.57\"},\"destination\":{\"icao_code\":\"VHHH\",\"pos_lat\":\"22.30\",\"pos_long\":\"113.91\"}", ""));
        var planB = SimBriefClient.Parse(Ofp("\"origin\":{\"icao_code\":\"ZSPD\",\"pos_lat\":\"31.14\",\"pos_long\":\"121.80\"},\"destination\":{\"icao_code\":\"RJAA\",\"pos_lat\":\"35.76\",\"pos_long\":\"140.38\"}", ""));
        var idA = history.Add("OWNER", planA!, "OFP A", 200, "req-A", FlightPlanHistory.Label(planA!));
        var idB = history.Add("OWNER", planB!, "OFP B", 300, "req-B", FlightPlanHistory.Label(planB!));
        Check("最新计划成为当前", history.ActivePlan("OWNER")?["origin"]?["ident"]?.GetValue<string>() == "ZSPD");
        Check("可按 id 切回旧计划", history.Select(idA, "OWNER") && history.ActivePlan("OWNER")?["origin"]?["ident"]?.GetValue<string>() == "EDDF");
        Check("切回后 OFP 同步", history.ActiveText("OWNER") == "OFP A");
        Check("历史列表含当前标记", history.List("OWNER").Any(item => (string?)item?["id"] == idA && item?["active"]?.GetValue<bool>() == true));
        Check("重复 request_id 不重复归档", history.Add("OWNER", planB!, "OFP B2", 400, "req-B", "again") == idB && history.Count == 3);
        for (var index = 0; index < 15; index++) history.Add("OWNER", planA!, "x", 500 + index, $"req-{index}", "x");
        Check("历史条数有上限", history.Count == 12, $"{history.Count} 条");
        Check("历史文件已落盘", File.Exists(historyPath) && File.ReadAllText(historyPath).Contains("activeId"));

        // 10. Flight card fields (callsign, type, planned times in UTC, durations).
        var withFlight = SimBriefClient.Parse(Ofp(positioned,
            "\"general\":{\"icao_airline\":\"ZZZ\",\"flight_number\":\"1234\"},"
            + "\"aircraft\":{\"icaocode\":\"A346\",\"name\":\"A340-600\",\"reg\":\"N634SB\"},"
            + "\"times\":{\"sched_off\":\"2026-09-10T22:55:00Z\",\"sched_on\":\"2026-09-11T08:42:00Z\",\"sched_time_enroute\":\"09:47:00\",\"est_on\":\"2026-09-11T10:07:53Z\",\"est_time_enroute\":\"11:12:53\"},"
            + "\"params\":{\"time_generated\":\"2026-09-10T22:01:06Z\"}"));
        var flightCard = withFlight?["flight"];
        Check("呼号按 航司/航班号 拼接", flightCard?["callsign"]?.GetValue<string>() == "ZZZ/1234");
        Check("机型回退到 aircraft.icaocode", flightCard?["aircraft"]?.GetValue<string>() == "A346" && flightCard?["aircraftName"]?.GetValue<string>() == "A340-600");
        Check("计划离港/到达保留 UTC 原文", flightCard?["plannedOff"]?.GetValue<string>() == "2026-09-10T22:55:00Z" && flightCard?["plannedOn"]?.GetValue<string>() == "2026-09-11T08:42:00Z");
        Check("HH:MM:SS 时长换算为秒", flightCard?["plannedEnrouteSeconds"]?.GetValue<double?>() == 35220
            && flightCard?["estimatedEnrouteSeconds"]?.GetValue<double?>() == 40373 && withFlight?["eteSeconds"]?.GetValue<double?>() == 40373);
        Check("生成时间解析", flightCard?["generatedAt"]?.GetValue<string>() == "2026-09-10T22:01:06Z");

        // 11. Weather overlay plumbing (allow list, coordinate range, cache, budget).
        Check("天气图层白名单", WeatherTiles.UpstreamName("clouds") == "clouds_new" && WeatherTiles.UpstreamName("../x") is null);
        Check("天气瓦片坐标校验",
            WeatherTiles.ValidCoordinate("clouds", 0, 0, 0) && WeatherTiles.ValidCoordinate("clouds", 3, 7, 7)
            && !WeatherTiles.ValidCoordinate("clouds", 3, 8, 0) && !WeatherTiles.ValidCoordinate("clouds", WeatherTiles.MaxZoom + 1, 0, 0));
        Check("天气上游 URL 带 key",
            WeatherTiles.UpstreamUrl("wind", "abc 123", 4, 8, 5) == $"{WeatherTiles.BaseUrl}/wind_new/4/8/5.png?appid=abc%20123");
        var tiles = new WeatherTiles();
        tiles.Store("clouds/1/0/0", [1, 2, 3], "image/png");
        Check("天气瓦片缓存命中", tiles.TryGet("clouds/1/0/0", out var tile) && tile.Body.Length == 3 && tile.ContentType == "image/png");
        Check("天气请求预算上限", Enumerable.Range(0, 50).All(_ => tiles.AllowRequest()) && !tiles.AllowRequest());

        // Empty-tile accounting drives the "no data in this area" hint.
        var counted = new WeatherTiles();
        counted.Record("precipitation", 334);
        counted.Record("precipitation", 334);
        counted.Record("precipitation", 334);
        counted.Record("clouds", 4705);
        var statusLayers = counted.Status()["layers"] as System.Text.Json.Nodes.JsonArray;
        System.Text.Json.Nodes.JsonNode? Layer(string name) => statusLayers?.FirstOrDefault(item => (string?)item?["name"] == name);
        Check("空瓦片按图层计数", Layer("precipitation")?["fetched"]?.GetValue<int>() == 3 && Layer("precipitation")?["empty"]?.GetValue<int>() == 3);
        Check("有数据瓦片不计为空", Layer("clouds")?["fetched"]?.GetValue<int>() == 1 && Layer("clouds")?["empty"]?.GetValue<int>() == 0);

        // 12. Weather proxy: defaults, independence from the global proxy, secret
        //     protection and credential redaction.
        var fresh = new BridgeConfig();
        Check("天气代理默认系统代理", fresh.WeatherProxyMode == BridgeConfig.ProxySystem && OutboundHttp.Weather(fresh).Mode == BridgeConfig.ProxySystem);
        fresh.ProxyMode = BridgeConfig.ProxyManual;
        fresh.ProxyUrl = "http://127.0.0.1:7890";
        Check("天气代理独立于全局", OutboundHttp.Weather(fresh).Mode == BridgeConfig.ProxySystem
            && OutboundHttp.Global(fresh).Mode == BridgeConfig.ProxyManual
            && OutboundHttp.Weather(fresh).Signature != OutboundHttp.Global(fresh).Signature);
        var protectedSecret = SecretProtection.Protect("p@ss word:123");
        Check("代理密码加密后不可读", SecretProtection.IsProtected(protectedSecret) && !protectedSecret.Contains("p@ss")
            && SecretProtection.Unprotect(protectedSecret) == "p@ss word:123");
        Check("空密码/旧明文兼容", SecretProtection.Unprotect("") == "" && SecretProtection.Unprotect("plain") == "plain" && SecretProtection.Protect("") == "");
        Check("错误信息凭据脱敏",
            SecretProtection.Redact("http://user:pass@proxy.local:8080/x") == "http://***@proxy.local:8080/x"
            && SecretProtection.Redact("http://proxy.local:8080") == "http://proxy.local:8080"
            && SecretProtection.Redact("no scheme user:pass@host") == "no scheme user:pass@host");
        var proxyWarnings = new BridgeConfig { WeatherProxyMode = BridgeConfig.ProxyManual, WeatherProxyUrl = "http://u:p@h:1" }.Warnings();
        Check("URL 里带凭据会告警", proxyWarnings.Any(text => text.Contains("不要带凭据")));
        var manualNoUrl = new BridgeConfig { WeatherProxyMode = BridgeConfig.ProxyManual }.Warnings();
        Check("自定义代理缺地址会告警", manualNoUrl.Any(text => text.Contains("代理地址为空")));
        var userNoPassword = new BridgeConfig { WeatherProxyMode = BridgeConfig.ProxyManual, WeatherProxyUrl = "http://p:1", WeatherProxyUser = "u" }.Warnings();
        Check("只有用户名会告警", userNoPassword.Any(text => text.Contains("没有可用的密码")));

        // 13. Regression for "Cannot access a disposed object": the shared client
        //     must keep working for repeated requests, and even a client that some
        //     other code disposed must not break the next call.
        static int StartProbeServer(int responses)
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint!).Port;
            _ = Task.Run(async () =>
            {
                for (var index = 0; index < responses; index++)
                {
                    try
                    {
                        using var socket = await listener.AcceptTcpClientAsync();
                        using var stream = socket.GetStream();
                        _ = await stream.ReadAsync(new byte[2048]);
                        var body = System.Text.Encoding.ASCII.GetBytes("ok");
                        var head = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nContent-Type: text/plain\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(head);
                        await stream.WriteAsync(body);
                        await stream.FlushAsync();
                    }
                    catch { }
                }
                try { listener.Stop(); } catch { }
            });
            return port;
        }

        var probePort = StartProbeServer(6);
        var repeatOk = true;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var choice = new ProxyChoice(BridgeConfig.ProxyNone, "", "", "");
                using var response = OutboundHttp.SendAsync(choice, client => client.GetAsync($"http://127.0.0.1:{probePort}/tile")).GetAwaiter().GetResult();
                repeatOk &= response.IsSuccessStatusCode && response.Content.ReadAsStringAsync().GetAwaiter().GetResult() == "ok";
            }
            catch { repeatOk = false; }
        }
        Check("同一 HttpClient 连续三次请求都成功（回归）", repeatOk);

        var disposedOk = true;
        try
        {
            var choice = new ProxyChoice(BridgeConfig.ProxyNone, "", "", "");
            OutboundHttp.For(choice).Dispose(); // simulate the old bug: someone disposes the shared client
            using var response = OutboundHttp.SendAsync(choice, client => client.GetAsync($"http://127.0.0.1:{probePort}/tile")).GetAwaiter().GetResult();
            disposedOk = response.IsSuccessStatusCode;
        }
        catch { disposedOk = false; }
        Check("被外部释放后仍能自动恢复", disposedOk);

        // 13b. Outbound HTTP: the proxy that is actually in effect must be
        // reported, and a connection that fails once must be retried.
        static int StartFlakyServer(int failures)
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint!).Port;
            _ = Task.Run(async () =>
            {
                var failed = 0;
                try
                {
                    while (true)
                    {
                        using var socket = await listener.AcceptTcpClientAsync();
                        using var stream = socket.GetStream();
                        _ = await stream.ReadAsync(new byte[2048]);
                        if (failed < failures)
                        {
                            // Dropping without answering is what a proxy that is
                            // still starting up looks like to HttpClient.
                            failed += 1;
                            continue;
                        }
                        var body = System.Text.Encoding.ASCII.GetBytes("ok");
                        var head = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nContent-Type: text/plain\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(head);
                        await stream.WriteAsync(body);
                        await stream.FlushAsync();
                        break;
                    }
                }
                catch { }
                try { listener.Stop(); } catch { }
            });
            return port;
        }

        var noneChoice = new ProxyChoice(BridgeConfig.ProxyNone, "", "", "");
        Check("代理描述：直连", OutboundHttp.DescribeEffective(noneChoice) == "不使用代理（直连）");
        Check("代理描述：自定义代理只显示主机与端口",
            OutboundHttp.DescribeEffective(new ProxyChoice(BridgeConfig.ProxyManual, "http://127.0.0.1:7890", "", "")).Contains("127.0.0.1:7890"));
        Check("代理描述：系统代理不会抛异常",
            OutboundHttp.DescribeEffective(new ProxyChoice(BridgeConfig.ProxySystem, "", "", "")).StartsWith("系统代理"),
            OutboundHttp.DescribeEffective(new ProxyChoice(BridgeConfig.ProxySystem, "", "", "")));
        Check("错误说明附带实际出口",
            OutboundHttp.Explain(new HttpRequestException("boom"), noneChoice).Contains("当前走："));
        Check("DNS 失败给出可读原因",
            OutboundHttp.Explain(new HttpRequestException("x", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound)), noneChoice).Contains("域名解析失败"));

        var flakyPort = StartFlakyServer(failures: 1);
        var retried = false;
        try
        {
            using var response = OutboundHttp.SendAsync(
                noneChoice,
                TimeSpan.FromSeconds(10),
                attempts: 2,
                (client, deadline) => client.GetAsync($"http://127.0.0.1:{flakyPort}/retry", deadline),
                CancellationToken.None).GetAwaiter().GetResult();
            retried = response.IsSuccessStatusCode;
        }
        catch { retried = false; }
        Check("首次连接失败后自动重试成功", retried);

        // 14. apt.dat ground layout (synthetic file in the X-Plane directory layout).
        var xplaneRoot = Path.Combine(directory, "xplane");
        var aptDirectory = Path.Combine(xplaneRoot, "Resources", "default scenery", "default apt dat", "Earth nav data");
        Directory.CreateDirectory(aptDirectory);
        File.WriteAllText(Path.Combine(aptDirectory, "apt.dat"), """
            1000 Version - written by WorldEditor 2.5.0r2

            1  132  0  0  KSEA  Seattle Tacoma International
            100  46.00  1  0  0.25  0  2  1  16L  47.53801700 -122.30746100  73.15  0.00  2  0  0  1  34R  47.52919200 -122.30000000  110.95  0.00  2
            110  1  0.25  150.29  A2 Exit
            111  47.53770968 -122.30849802
            111  47.53742819 -122.30825844  3
            113  47.53742819 -122.30800000
            120  Line B1
            111  47.53969864 -122.31276189  51
            115  47.54002296 -122.31189878
            20  47.54099177 -122.31031317  235.71  0  2  {@L}A1{@R}31R-13L
            1300  47.52926674 -122.29919589  304.16  gate  A8
            1301  1  A  UAL DAL
            1200 Taxi route network
            1201  0  47.52926674 -122.29919589  init
            1201  1  47.53000000 -122.30000000  junc
            1202  0  1  twoway  taxiway A

            1  10  0  0  KPDX  Portland International
            100  46.00  1  0  0.25  0  2  1  10L  45.59000000 -122.59000000  0.00  0.00  2  0  0  1  28R  45.58000000 -122.57000000  0.00  0.00  2
            """);
        var ground = new AptDat(xplaneRoot, Path.Combine(directory, "apt-index.json"));
        Check("apt.dat 识别机场数量", ground.Configured && ground.Status()["airports"]?.GetValue<int>() == 2,
            ground.Status()["airports"]?.ToJsonString() ?? "");
        var nearest = ground.Nearest(47.53, -122.30, 5);
        Check("按坐标找到最近机场", (string?)nearest?["icao"] == "KSEA");
        var runways = nearest?["runways"] as System.Text.Json.Nodes.JsonArray;
        Check("跑道两端与宽度", (runways?.Count ?? 0) == 1
            && (string?)runways?[0]?["ident"] == "16L/34R"
            && Math.Abs((runways?[0]?["a"]?[0]?.GetValue<double>() ?? 0) - 47.538017) < 0.0001
            && Math.Abs((runways?[0]?["b"]?[1]?.GetValue<double>() ?? 0) + 122.300000) < 0.0001
            && Math.Abs((runways?[0]?["widthM"]?.GetValue<double>() ?? 0) - 46) < 0.5);
        Check("铺面节点坐标", (double?)nearest?["pavements"]?[0]?["points"]?[0]?[0] == 47.53770968
            && (double?)nearest?["pavements"]?[0]?["points"]?[0]?[1] == -122.30849802);
        var routePoints = nearest?["routes"]?[0]?["points"] as System.Text.Json.Nodes.JsonArray;
        Check("滑行路线节点/边", (routePoints?.Count ?? 0) >= 2
            && Math.Abs((routePoints?[0]?[0]?.GetValue<double>() ?? 0) - 47.52926674) < 0.0001);
        Check("铺面/标线/标牌/机位/滑行路线",
            ((nearest?["pavements"] as System.Text.Json.Nodes.JsonArray)?.Count ?? 0) == 1
            && ((nearest?["lines"] as System.Text.Json.Nodes.JsonArray)?.Count ?? 0) == 1
            && ((nearest?["signs"] as System.Text.Json.Nodes.JsonArray)?.Count ?? 0) == 1
            && ((nearest?["parking"] as System.Text.Json.Nodes.JsonArray)?.Count ?? 0) == 1
            && ((nearest?["routes"] as System.Text.Json.Nodes.JsonArray)?.Count ?? 0) == 1,
            $"pavements={(nearest?["pavements"] as System.Text.Json.Nodes.JsonArray)?.Count} "
            + $"lines={(nearest?["lines"] as System.Text.Json.Nodes.JsonArray)?.Count} "
            + $"signs={(nearest?["signs"] as System.Text.Json.Nodes.JsonArray)?.Count} "
            + $"parking={(nearest?["parking"] as System.Text.Json.Nodes.JsonArray)?.Count} "
            + $"routes={(nearest?["routes"] as System.Text.Json.Nodes.JsonArray)?.Count}");
        Check("滑行道标牌文字", ((string?)nearest?["signs"]?[0]?["text"] ?? "").Contains("A1"));
        Check("机位名称与航司代码", (string?)nearest?["parking"]?[0]?["name"] == "A8"
            && ((string?)nearest?["parking"]?[0]?["airlines"] ?? "").Contains("UAL"));
        Check("索引缓存已落盘", File.Exists(Path.Combine(directory, "apt-index.json")));
        Check("超出半径不返回机场", ground.Nearest(0, 0, 5) is null);
        Check("未配置目录时明确报错", new AptDat("", "x").Status()["error"]?.GetValue<string>()?.Contains("未配置") == true);

        // 14b. Picking the folder one level too high / too low must still work,
        // and switching the X-Plane directory must rebuild without a restart.
        Check("接受 …\\Resources 目录", AptDat.ResolveRoot(Path.Combine(xplaneRoot, "Resources")) == xplaneRoot);
        Check("接受 apt.dat 文件路径", AptDat.ResolveRoot(Path.Combine(aptDirectory, "apt.dat")) == xplaneRoot);
        Check("接受只包含安装目录的上一级", AptDat.ResolveRoot(directory) == xplaneRoot);

        var secondRoot = Path.Combine(directory, "xplane2");
        var secondApt = Path.Combine(secondRoot, "Resources", "default scenery", "default apt dat", "Earth nav data");
        Directory.CreateDirectory(secondApt);
        File.WriteAllText(Path.Combine(secondApt, "apt.dat"), """
            1000 Version - written by WorldEditor 2.5.0r2

            1  100  0  0  EGLL  London Heathrow
            100  46.00  1  0  0.25  0  2  1  09L  51.47800000  -0.49000000  0.00  0.00  2  0  0  1  27R  51.47000000  -0.43000000  0.00  0.00  2
            """);
        var liveRoot = xplaneRoot;
        var live = new AptDat(() => liveRoot, Path.Combine(directory, "apt-index-live.json"));
        Check("热更新：初始目录索引出两个机场", (int?)live.Status()["airports"] == 2);
        liveRoot = secondRoot;
        Check("热更新：改目录后自动重建",
            (int?)live.Status()["airports"] == 1 && (string?)live.Nearest(51.47, -0.45, 10)?["icao"] == "EGLL",
            live.Status()["airports"]?.ToJsonString() ?? "");
        liveRoot = "";
        Check("热更新：清空目录后给出提示", live.Status()["error"]?.GetValue<string>()?.Contains("未配置") == true);

        // 14c. Vertical speed: X-Plane's -999 sentinel must be rejected, and the
        // derived value has to track a real climb.
        Check("V/S：-999 哨兵判为无效", !LocalWebServer.PlausibleVerticalSpeed(-999) && LocalWebServer.PlausibleVerticalSpeed(-800));
        Check("V/S：解析出的 float 也能识别",
            LocalWebServer.AsDouble((float)1234.5, out var floatValue) && Math.Abs(floatValue - 1234.5) < 0.01
            && !LocalWebServer.AsDouble("x", out _));
        var estimator = new LocalWebServer.VerticalSpeedEstimator();
        for (var step = 0; step <= 40; step++) { var ms = step * 200; estimator.Feed(ms, 5000 + ms / 60.0); }
        Check("V/S：按高度推算 1000 fpm", Math.Abs((estimator.Value ?? 0) - 1000) < 150,
            (estimator.Value ?? double.NaN).ToString("0.#"));

        try { Directory.Delete(directory, true); } catch { }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "efb-config-selftest.log"), string.Join(Environment.NewLine, lines));
        return ok ? 0 : 1;
    }
}

// Renders the settings window to PNG files so the layout can be reviewed without
// a display. Usage: XPlaneEfbBridge.exe --settings-preview <png-path> [tabIndex]
//
// Local diagnostic for SimBrief format changes. It talks to SimBrief exactly
// like the app does (one request, because the user asked for it) and writes the
// response plus a structure summary next to the given path. Nothing leaves the
// machine. Usage: XPlaneEfbBridge.exe --dump-ofp <output.json> [configPath]
internal static class OfpDump
{
    // Offline check: parse a saved SimBrief response with the shipped parser.
    public static int ParseFile(string inputPath, string outputPath)
    {
        var lines = new List<string>();
        try
        {
            var payload = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(inputPath));
            lines.Add($"输入：{inputPath}");
            lines.Add($"顶层字段：{SimBriefClient.Shape(payload)}");
            var plan = SimBriefClient.Parse(payload);
            if (plan is null)
            {
                lines.Add("规范化结果：空（没有可用的航路数据）");
            }
            else
            {
                var waypoints = plan["waypoints"] as System.Text.Json.Nodes.JsonArray;
                lines.Add($"起飞机场：{plan["origin"]?["ident"]?.GetValue<string>()} ({plan["origin"]?["lat"]?.GetValue<double?>()}, {plan["origin"]?["lon"]?.GetValue<double?>()})");
                lines.Add($"降落机场：{plan["destination"]?["ident"]?.GetValue<string>()} ({plan["destination"]?["lat"]?.GetValue<double?>()}, {plan["destination"]?["lon"]?.GetValue<double?>()})");
                lines.Add($"航点数：{waypoints?.Count ?? 0}");
                lines.Add($"航路距离：{plan["distanceNm"]?.GetValue<double?>()} NM，巡航高度：{plan["cruiseAltitudeFt"]?.GetValue<double?>()} ft");
                lines.Add($"机型：{plan["aircraft"]?.GetValue<string>()}");
                if (waypoints is { Count: > 0 })
                {
                    lines.Add($"第一个航点：{waypoints[0]?.ToJsonString()}");
                    lines.Add($"最后一个航点：{waypoints[^1]?.ToJsonString()}");
                }
            }
        }
        catch (Exception error)
        {
            lines.Add("解析失败：" + error);
        }
        File.WriteAllLines(Path.GetFullPath(outputPath), lines);
        return 0;
    }

    public static int Run(string outputPath, string configPath)
    {
        var log = new List<string>();
        var target = Path.GetFullPath(outputPath);
        var logPath = Path.ChangeExtension(target, ".shape.txt");
        try
        {
            var snapshot = ConfigStore.Load(configPath);
            var config = snapshot.Config;
            log.Add($"配置文件：{configPath}");
            log.Add($"SimBrief 用户：{Mask(config.SimbriefUser)}");
            log.Add($"端点：{(config.SimbriefApiUrl.Length > 0 ? config.SimbriefApiUrl : "官方默认")}");
            log.Add($"代理模式：{config.ProxyMode}");

            var client = new SimBriefClient(config, persistCache: false);
            var status = client.GetAsync(refresh: true, CancellationToken.None).GetAwaiter().GetResult();
            log.Add($"请求结果：available={status["available"]?.ToJsonString()} error={status["error"]?.ToJsonString()}");

            var raw = client.LastRaw ?? "";
            log.Add($"响应长度：{raw.Length} 字符");
            if (raw.Length > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, raw, new System.Text.UTF8Encoding(false));
                log.Add($"原始响应已写入 {target}（仅供本机排查，看完可删）");

                System.Text.Json.Nodes.JsonNode? payload = null;
                try { payload = System.Text.Json.Nodes.JsonNode.Parse(raw); } catch (Exception error) { log.Add($"JSON 解析失败：{error.Message}"); }
                log.Add($"顶层字段：{SimBriefClient.Shape(payload)}");
                var root = payload as System.Text.Json.Nodes.JsonObject;
                foreach (var key in new[] { "navlog", "general", "origin", "destination", "alternate", "times", "aircraft", "params" })
                    log.Add($"  {key} → {Describe(root?[key])}");
                var navlog = root?["navlog"] as System.Text.Json.Nodes.JsonObject;
                if (navlog is not null)
                    log.Add($"  navlog 的键：{string.Join(", ", navlog.Select(item => item.Key))}");
                var fix = navlog?["fix"];
                log.Add($"  navlog.fix → {Describe(fix)}");
                var payloadNavlog = root?["navlog"];
                var fixes = payloadNavlog is System.Text.Json.Nodes.JsonArray flat ? flat : fix switch
                {
                    System.Text.Json.Nodes.JsonArray array => array,
                    null => null,
                    _ => new System.Text.Json.Nodes.JsonArray(fix.DeepClone())
                };
                if (fixes is { Count: > 0 })
                {
                    log.Add($"  第一个航点：{fixes[0]?.ToJsonString()}");
                    if (fixes.Count > 1) log.Add($"  第二个航点：{fixes[1]?.ToJsonString()}");
                }
            }

            log.Add("规范化结果：" + (status["plan"]?.ToJsonString() ?? "(空)")[..Math.Min(600, (status["plan"]?.ToJsonString() ?? "(空)").Length)]);
        }
        catch (Exception error)
        {
            log.Add("诊断失败：" + error);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllLines(logPath, log);
        return 0;
    }

    private static string Describe(System.Text.Json.Nodes.JsonNode? node) => node switch
    {
        null => "不存在",
        System.Text.Json.Nodes.JsonObject item => $"对象(键：{string.Join(", ", item.Select(entry => entry.Key))})",
        System.Text.Json.Nodes.JsonArray array => $"数组({array.Count})",
        System.Text.Json.Nodes.JsonValue value => $"值({value.ToJsonString()[..Math.Min(24, value.ToJsonString().Length)]})",
        _ => node.GetType().Name
    };

    private static string Mask(string value) =>
        value.Length == 0 ? "(未配置)" : value.Length <= 2 ? new string('•', value.Length) : value[..1] + new string('•', Math.Min(6, value.Length - 2)) + value[^1];
}

internal static class SettingsPreview
{
    public static void Render(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var target = Path.GetFullPath(args[1]);
        var mode = args.Length > 2 ? args[2] : "0";
        if (mode is "folder" or "picker")
        {
            RenderFolderPicker(target, args.Length > 3 ? args[3] : null);
            return;
        }
        var tab = int.TryParse(mode, out var parsed) ? parsed : 0;
        var directory = Path.Combine(Path.GetTempPath(), "efb-preview-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "bridge-config.json");
        // Sample values only: the preview never reads or shows the real account.
        File.WriteAllText(path, """
            {
              "UdpPorts": [49003],
              "WebPort": 8080,
              "SourceIp": "",
              "CustomBaseMapName": "",
              "CustomBaseMapUrl": "",
              "CustomBaseMapAttribution": "",
              "SimbriefUser": "123456",
              "SimbriefApiUrl": "",
              "ProxyMode": "system",
              "ProxyUrl": ""
            }
            """);
        var snapshot = ConfigStore.Load(path);
        using var form = new SettingsForm(snapshot, "http://192.168.1.25:8080");
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-4000, -4000);
        form.ShowInTaskbar = false;
        form.Show();
        Application.DoEvents();
        form.SelectTab(tab);
        form.PerformLayout();
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        bitmap.Save(target, System.Drawing.Imaging.ImageFormat.Png);
        File.WriteAllLines(target + ".layout.txt", Describe(form));
        form.Close();
        try { Directory.Delete(directory, true); } catch { }
    }

    // Renders the folder picker on its own so its layout can be reviewed without
    // a display. Usage: --settings-preview <png> folder
    private static void RenderFolderPicker(string target, string? initial)
    {
        // The sandbox has no USERPROFILE, so fall back to the temp folder: the
        // point of the preview is the layout, not the folder that is selected.
        var start = initial ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (start.Length == 0) start = Path.GetTempPath();
        using var picker = new FolderPicker(
            "选择 X-Plane 12 安装目录",
            "选中包含 Resources 与 Custom Scenery 的那一层（通常叫 X-Plane 12）。也可以直接把路径粘贴到下面的输入框。",
            start,
            SettingsForm.DescribeXPlaneFolder);
        picker.StartPosition = FormStartPosition.Manual;
        picker.Location = new Point(-4000, -4000);
        picker.ShowInTaskbar = false;
        picker.Show();
        Application.DoEvents();
        picker.PerformLayout();
        Application.DoEvents();
        using var bitmap = new Bitmap(picker.Width, picker.Height);
        picker.DrawToBitmap(bitmap, new Rectangle(0, 0, picker.Width, picker.Height));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        bitmap.Save(target, System.Drawing.Imaging.ImageFormat.Png);
        File.WriteAllLines(target + ".layout.txt", Describe(picker));
        picker.Close();
    }

    private static IEnumerable<string> Describe(Control parent, int depth = 0)
    {
        var pad = new string(' ', depth * 2);
        yield return $"{pad}{parent.GetType().Name} \"{parent.Text}\" bounds={parent.Bounds} preferred={parent.PreferredSize} font={parent.Font.Name} {parent.Font.SizeInPoints}pt";
        foreach (Control child in parent.Controls) foreach (var line in Describe(child, depth + 1)) yield return line;
    }
}
