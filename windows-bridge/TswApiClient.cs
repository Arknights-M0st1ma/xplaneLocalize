using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XPlaneEfbBridge;

/// <summary>
/// Read-only client for the Train Sim World 6 local HTTP API
/// (<c>http://127.0.0.1:31270</c>, authenticated with the <c>DTGCommKey</c> header).
///
/// Facts this file relies on, and how sure we are of each:
///   - Base URL, the header name and the success envelope <c>{"Result":"Success","Values":{…}}</c>
///     are confirmed by the public reverse-engineered OpenAPI 3.1 specification
///     (https://tsw-inspector.waaghals.dev/openapi.yaml).
///   - The <em>node and endpoint names</em> (DriverAid.PlayerInfo,
///     CurrentDrivableActor.Function.HUD_GetSpeed, DriverAid.Data, /info) come from third-party
///     documentation and client code; the official PDF needs a Dovetail forum login. They are
///     therefore treated as candidates to be *discovered* (see <see cref="DiscoverAsync"/>), never
///     as guaranteed constants, and every read tolerates a missing or renamed field.
///   - Failure can arrive as HTTP 200 with <c>{"Result":"Error",…}</c>, so this client never judges
///     success by the status code alone.
///
/// The key stays on this machine: it is read from the game's own file, kept in memory, and is never
/// written to the configuration file, the log, the HTTP API served to the iPad, or any exception
/// message. <see cref="Describe"/> reports credential-free diagnostics for the settings window.
/// </summary>
internal sealed class TswApiClient : IDisposable
{
    public const int DefaultPort = 31270;
    public const string KeyHeader = "DTGCommKey";
    /// <summary>Environment override, mirroring EFB_OWM_BASE in WeatherTiles. Tests and the Node
    /// fake in test/tsw-fake-api.js point the real client at a local stand-in game.</summary>
    public const string BaseOverrideVariable = "EFB_TSW_BASE";
    private const string DefaultBase = "http://127.0.0.1:31270";

    // The API is on the loopback interface, so a healthy round trip is sub-millisecond. A one second
    // ceiling keeps a stalled poll from eating the whole 4 Hz budget the way a 5 s one would.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(1);
    // A dead node is skipped for a while instead of being re-requested four times a second. The
    // unavailability is reported to the UI meanwhile, so this is not a silent failure.
    private static readonly TimeSpan MissingNodeCooldown = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions ParseOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient http;
    private readonly string keyPathOverride;
    private readonly Dictionary<string, DateTimeOffset> missingUntil = new(StringComparer.Ordinal);
    private string? cachedKey;

    public TswApiClient(string? keyPath = null, string? baseOverride = null)
    {
        keyPathOverride = keyPath ?? "";
        var candidate = baseOverride ?? Environment.GetEnvironmentVariable(BaseOverrideVariable) ?? "";
        var normalized = NormalizeBase(candidate, out var note);
        BaseUrl = normalized;
        BaseNote = note;
        http = new HttpClient(new SocketsHttpHandler
        {
            // The API lives on loopback: it must never go through the user's system proxy. A proxy
            // that blackholes 127.0.0.1 turns every poll into a full one-second timeout, which looks
            // exactly like "the game has no coordinates yet".
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(1),
            PooledConnectionLifetime = TimeSpan.FromSeconds(30)
        })
        { Timeout = RequestTimeout };
    }

    /// <summary>Effective API base, without a trailing slash.</summary>
    public string BaseUrl { get; }

    /// <summary>Empty when the built-in default was used; otherwise explains where the base came
    /// from or why the override was rejected (shown in the settings window).</summary>
    public string BaseNote { get; }

    public string KeyPathUsed { get; private set; } = "";
    public string KeyFileNote { get; private set; } = "尚未读取密钥文件。";
    public DateTimeOffset? LastSuccessAt { get; private set; }

    // ------------------------------------------------------------------ key discovery

    /// <summary>Candidate locations of the key the game generates. The explicit path from the
    /// settings window wins; the OneDrive entry exists because the game resolves "My Documents"
    /// through the shell folder, which is redirected when OneDrive backup is on.</summary>
    public static IEnumerable<string> KeyCandidates(string? explicitPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var trimmed = explicitPath!.Trim();
            if (seen.Add(trimmed)) yield return trimmed;
        }
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(docs))
        {
            var path = Path.Combine(docs, "My Games", "TrainSimWorld6", "Saved", "Config", "CommAPIKey.txt");
            if (seen.Add(path)) yield return path;
        }
        var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        if (!string.IsNullOrWhiteSpace(oneDrive))
        {
            var path = Path.Combine(oneDrive!, "Documents", "My Games", "TrainSimWorld6", "Saved", "Config", "CommAPIKey.txt");
            if (seen.Add(path)) yield return path;
        }
    }

    /// <summary>Reads the key once and caches it. Returns null when no candidate file exists or the
    /// file holds nothing usable; the reason is left in <see cref="KeyFileNote"/> for the UI.</summary>
    public string? Key()
    {
        if (cachedKey is not null) return cachedKey;

        var candidates = KeyCandidates(keyPathOverride).ToList();
        var existing = candidates.Where(File.Exists).ToList();
        if (existing.Count == 0)
        {
            KeyFileNote = keyPathOverride.Trim().Length > 0
                ? $"找不到密钥文件：{keyPathOverride.Trim()}"
                : $"找不到密钥文件（已尝试 {candidates.Count} 个默认位置）。请在 TSW6 的 Steam 启动项加入 -HTTPAPI 并启动一次游戏。";
            return null;
        }

        foreach (var path in existing)
        {
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception error)
            {
                KeyFileNote = $"无法读取 {path}：{error.Message}";
                continue;
            }
            // The game writes the key with a trailing newline and, on some editors/sync tools, a BOM.
            var key = text.Trim().TrimStart('\uFEFF').Trim();
            if (key.Length == 0)
            {
                KeyFileNote = $"密钥文件是空的：{path}";
                continue;
            }
            cachedKey = key;
            KeyPathUsed = path;
            KeyFileNote = $"已读取密钥：{path}";
            return cachedKey;
        }
        return null;
    }

    /// <summary>Drops the cached key so the next call re-reads the file (used when the user points
    /// the settings window at a different path, or when a 403 says the key changed).</summary>
    public void ForgetKey()
    {
        cachedKey = null;
        KeyPathUsed = "";
        KeyFileNote = "密钥已清除，下次请求会重新读取。";
    }

    // ------------------------------------------------------------------ requests

    public bool KeyConfigured => Key() is not null;

    /// <summary>True when the configured base points somewhere other than the real game, i.e. the
    /// developer pointed it at the Node fake with <see cref="BaseOverrideVariable"/>.</summary>
    public bool IsOverridden => BaseUrl != DefaultBase;

    /// <summary>One GET of a node endpoint. Never throws: transport problems, timeouts and error
    /// envelopes all come back as a non-success <see cref="TswResponse"/> so the polling loop can
    /// keep its own state machine.</summary>
    public async Task<TswResponse> GetAsync(string node, string endpoint, CancellationToken token)
    {
        // Segment-wise escaping: the node path itself must keep its dots and slashes, only the
        // characters that would break the URL are encoded.
        var path = $"/get/{EscapeNode(node)}.{Uri.EscapeDataString(endpoint)}";
        return await SendAsync(path, token);
    }

    public Task<TswResponse> GetAsync(string path, CancellationToken token) => SendAsync(path, token);

    // ------------------------------------------------------------------ subscriptions
    // /subscription is the API's push-ish surface: register a path once, then read every registered
    // value back in a single request. Both public TSW6 clients (GarethLowe's tsw6-realtime-weather
    // and TheJAG's tsw_connect) read the player position this way rather than with /get, so this is
    // a documented shape of the same data that the bridge has to be able to fall back to
    // (docs/TSW6-TELEMETRY.md §3.2).

    public Task<TswResponse> SubscribeAsync(string node, string endpoint, int subscriptionId, CancellationToken token) =>
        SendAsync(HttpMethod.Post,
            $"/subscription/{EscapeNode(node)}.{Uri.EscapeDataString(endpoint)}?Subscription={subscriptionId}", token);

    public Task<TswResponse> ReadSubscriptionAsync(int subscriptionId, CancellationToken token) =>
        SendAsync($"/subscription?Subscription={subscriptionId}", token);

    public Task<TswResponse> UnsubscribeAsync(int subscriptionId, CancellationToken token) =>
        SendAsync(HttpMethod.Delete, $"/subscription/?Subscription={subscriptionId}", token);

    public Task<TswResponse> SendAsync(string path, CancellationToken token) => SendAsync(HttpMethod.Get, path, token);

    public async Task<TswResponse> SendAsync(HttpMethod method, string path, CancellationToken token)
    {
        var key = Key();
        if (key is null) return TswResponse.Failure("密钥不可用", KeyFileNote);

        using var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.TryAddWithoutValidation(KeyHeader, key);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, token);
            var body = await response.Content.ReadAsStringAsync(token);
            var parsed = Parse(response.StatusCode, body, path, out var envelope);
            if (parsed)
            {
                LastSuccessAt = DateTimeOffset.UtcNow;
                missingUntil.Remove(path);
            }
            else if (envelope == Envelope.InvalidKey)
            {
                // The game rewrites this file on its own schedule; a 403 means our copy is stale.
                ForgetKey();
                KeyFileNote = "游戏拒绝了密钥（HTTP 403 dtg.comm.InvalidKey），已清除缓存；请确认密钥文件是最新的。";
            }
            return new TswResponse
            {
                Ok = parsed,
                Status = (int)response.StatusCode,
                ErrorCode = ErrorCodeOf(body),
                Message = MessageOf(body, response.StatusCode),
                Values = ValuesOf(body),
                // /get routes answer with {Result, Values}, but /list and /info put their data
                // (Nodes, Endpoints, Meta) straight on the root, so both are kept: reading only
                // "Values" made the endpoint listing and the game info come back empty.
                Root = RootOf(body),
                Raw = body,
                Path = path
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return TswResponse.Failure($"请求超时（{RequestTimeout.TotalSeconds:0.#} 秒）", path);
        }
        catch (HttpRequestException error)
        {
            return TswResponse.Failure(DescribeHttpFailure(error), path);
        }
        catch (Exception error)
        {
            return TswResponse.Failure(error.GetType().Name + "：" + error.Message, path);
        }
    }

    /// <summary>Enumerates the endpoints of a node. This is how the integration copes with the
    /// uncertainty in §3.4 of docs/TSW6-TELEMETRY.md: instead of trusting hard-coded names, the
    /// probe prints what the running game actually exposes.</summary>
    public async Task<IReadOnlyList<string>> ListEndpointsAsync(string node, CancellationToken token)
    {
        var response = await SendAsync($"/list/{EscapeNode(node)}", token);
        if (!response.Ok) return [];
        var source = response.Root ?? response.Values;
        if (source?["Endpoints"] is not JsonArray endpoints) return [];
        var names = new List<string>();
        foreach (var item in endpoints)
        {
            var name = Text(item, "Name");
            if (name.Length > 0) names.Add(name);
        }
        return names;
    }

    /// <summary>Lists the child node names of a node (used by --tsw-probe to walk the tree).</summary>
    public async Task<IReadOnlyList<string>> ListNodesAsync(string node, CancellationToken token)
    {
        var path = node.Length == 0 ? "/list" : $"/list/{EscapeNode(node)}";
        var response = await SendAsync(path, token);
        if (!response.Ok) return [];
        var source = response.Root ?? response.Values;
        if (source?["Nodes"] is not JsonArray nodes) return [];
        var names = new List<string>();
        foreach (var item in nodes)
        {
            var name = Text(item, "Name");
            if (name.Length > 0) names.Add(name);
        }
        return names;
    }

    /// <summary>Two-step liveness probe, because /info is not in the reverse-engineered spec:
    /// first the raw TCP connect (is anything listening on that port?), then /list (is the key
    /// accepted?). /info is then read opportunistically for build information only.</summary>
    public async Task<TswProbe> ProbeAsync(CancellationToken token)
    {
        var probe = new TswProbe { BaseUrl = BaseUrl, KeyPath = KeyPathUsed, KeyNote = KeyFileNote };
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri))
        {
            probe.TcpReachable = false;
            probe.Summary = "API 地址不合法。";
            return probe;
        }
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            await tcp.ConnectAsync(uri.Host, uri.Port, timeout.Token);
            probe.TcpReachable = true;
        }
        catch (Exception error)
        {
            probe.TcpReachable = false;
            probe.Summary = $"连不上 {uri.Host}:{uri.Port}（{error.GetType().Name}）。游戏没在运行，或 API 没启用（Steam 启动项需要 -HTTPAPI）。";
            return probe;
        }

        // Read the key before reporting on it, so KeyPath/KeyNote describe the real outcome instead
        // of the "not read yet" placeholders the probe starts with.
        probe.KeyConfigured = KeyConfigured;
        _ = Key();
        probe.KeyPath = KeyPathUsed;
        probe.KeyNote = KeyFileNote;
        if (!probe.KeyConfigured)
        {
            probe.Summary = "端口可达，但没有可用的密钥文件：" + KeyFileNote;
            return probe;
        }

        var list = await SendAsync("/list", token);
        probe.ListStatus = list.Status;
        probe.ListOk = list.Ok;
        probe.ListError = list.Ok ? "" : list.Message;
        if (list.Ok)
        {
            probe.Nodes = (await ListNodesAsync("", token)).ToList();
            probe.Summary = $"API 可用（/list 返回 Result:Success），根节点 {probe.Nodes.Count} 个。";
        }
        else
        {
            probe.Summary = list.ErrorCode == "dtg.comm.InvalidKey"
                ? "密钥被拒绝（HTTP 403 dtg.comm.InvalidKey）：CommAPIKey.txt 可能是旧的，重启游戏后重试。"
                : $"API 可达但 /list 失败：{list.Message}";
            return probe;
        }

        // /info is a bonus, not a requirement: it is absent from the reverse-engineered spec, so its
        // payload is read from the response root (Meta sits there, not under "Values").
        var info = await SendAsync("/info", token);
        probe.InfoAvailable = info.Ok;
        if (info.Ok)
        {
            probe.GameName = Text(info.Root, "Meta", "GameName");
            probe.Worker = Text(info.Root, "Meta", "Worker");
            probe.GameBuild = Text(info.Root, "Meta", "GameBuildNumber");
            probe.ApiVersion = Text(info.Root, "Meta", "APIVersion");
        }
        return probe;
    }

    private static bool Parse(HttpStatusCode status, string body, string path, out Envelope envelope)
    {
        envelope = Envelope.Other;
        if ((int)status is 403 or 401)
        {
            envelope = Envelope.InvalidKey;
            return false;
        }
        if (!IsSuccessStatus(status))
        {
            envelope = Envelope.Other;
            return false;
        }
        Envelope parsed;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { envelope = Envelope.Other; return false; }
            var result = root.TryGetProperty("Result", out var resultNode) && resultNode.ValueKind == JsonValueKind.String
                ? resultNode.GetString() ?? ""
                : "";
            // Missing "Result" on a 2xx is treated as success so a future API that drops the wrapper
            // still works; an explicit "Error" is what actually matters.
            parsed = result.Equals("Error", StringComparison.OrdinalIgnoreCase) || result.Equals("Failure", StringComparison.OrdinalIgnoreCase)
                ? Envelope.ErrorEnvelope
                : Envelope.Success;
            envelope = parsed;
            return parsed == Envelope.Success;
        }
        catch (JsonException)
        {
            envelope = Envelope.NotJson;
            return false;
        }
    }

    private static bool IsSuccessStatus(HttpStatusCode status) => (int)status is >= 200 and < 300;

    private enum Envelope { Success, ErrorEnvelope, InvalidKey, NotJson, Other }

    private static string ErrorCodeOf(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("errorCode", out var node)
                && node.ValueKind == JsonValueKind.String
                ? node.GetString() ?? ""
                : "";
        }
        catch (JsonException) { return ""; }
    }

    private static string MessageOf(string body, HttpStatusCode status)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "Message", "errorMessage", "message" })
                {
                    if (root.TryGetProperty(key, out var node) && node.ValueKind == JsonValueKind.String)
                    {
                        var text = node.GetString() ?? "";
                        if (text.Length > 0) return text;
                    }
                }
            }
        }
        catch (JsonException) { }
        return $"HTTP {(int)status}";
    }

    private static JsonObject? ValuesOf(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty("Values", out var values)) return null;
            if (values.ValueKind != JsonValueKind.Object) return null;
            return JsonNode.Parse(values.GetRawText()) as JsonObject;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The whole response object. /list and /info carry their payload at the root rather
    /// than under "Values", so both shapes have to be reachable.</summary>
    private static JsonObject? RootOf(string body)
    {
        try
        {
            return JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException) { return null; }
    }

    // The node keeps its dots and slashes (they are part of the route), but a value injected from
    // the configuration file must not be able to escape the /get prefix.
    private static string EscapeNode(string node) =>
        string.Join('/', node.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    internal static string NormalizeBase(string? candidate, out string note)
    {
        note = "";
        var text = (candidate ?? "").Trim();
        if (text.Length == 0) return DefaultBase;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            note = $"环境变量 {BaseOverrideVariable} 的值不是合法的 http(s) 地址，已改用默认 {DefaultBase}。";
            return DefaultBase;
        }
        var normalized = $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath.TrimEnd('/')}";
        note = normalized == DefaultBase
            ? ""
            : $"API 地址来自环境变量 {BaseOverrideVariable}：{normalized}";
        return normalized;
    }

    // Turns a transport failure into something a user can act on. The message never contains the
    // key: HttpClient puts the URL in the exception text and this URL carries no credentials.
    private static string DescribeHttpFailure(HttpRequestException error) => error.HttpRequestError switch
    {
        HttpRequestError.ConnectionError => "连接被拒绝（游戏没在运行，或 API 未启用）",
        HttpRequestError.NameResolutionError => "域名解析失败",
        HttpRequestError.SecureConnectionError => "TLS 连接失败",
        _ => error.Message
    };

    public string Describe() =>
        $"{(IsOverridden ? "自定义（" + BaseUrl + "）" : BaseUrl)} · "
        + (KeyConfigured ? $"密钥已读取（{KeyPathUsed}）" : "密钥未读取")
        + (LastSuccessAt is DateTimeOffset at ? $" · 最近成功 {at.ToLocalTime():HH:mm:ss}" : " · 尚未成功读取");

    /// <summary>Credential-free explanation of the key situation, for the settings window and the
    /// self-test. Never contains the key itself.</summary>
    public string StatusNote() => KeyConfigured ? $"密钥已读取：{KeyPathUsed}" : KeyFileNote;

    public void Dispose() => http.Dispose();

    // Reads a possibly nested property as text. Returns "" for anything missing or of the wrong
    // kind, which is what makes a renamed field degrade to "—" instead of throwing. Numbers are
    // stringified too: /info reports GameBuildNumber as a JSON number, not a string.
    public static string Text(JsonNode? node, params string[] path)
    {
        var current = node;
        foreach (var step in path)
        {
            if (current is not JsonObject item || !item.TryGetPropertyValue(step, out var next)) return "";
            current = next;
        }
        return current switch
        {
            null => "",
            JsonValue value => value.TryGetValue<string>(out var text) ? text ?? ""
                : value.TryGetValue<double>(out var number) ? number.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                : value.TryGetValue<bool>(out var flag) ? (flag ? "true" : "false")
                : "",
            _ => ""
        };
    }
}

/// <summary>Result of one API call. <see cref="Ok"/> means the response carried
/// <c>Result: "Success"</c>, not merely a 2xx status.</summary>
internal sealed class TswResponse
{
    public bool Ok { get; init; }
    public int Status { get; init; }
    public string ErrorCode { get; init; } = "";
    public string Message { get; init; } = "";
    public JsonObject? Values { get; init; }
    /// <summary>The full response object, for the routes that answer at the root (/list, /info).</summary>
    public JsonObject? Root { get; init; }
    public string Raw { get; init; } = "";
    public string Path { get; init; } = "";

    public static TswResponse Failure(string message, string path) =>
        new() { Ok = false, Status = 0, Message = message, Path = path };
}

/// <summary>Everything the settings window and --tsw-probe can report about API reachability.</summary>
internal sealed class TswProbe
{
    public string BaseUrl { get; set; } = "";
    public string KeyPath { get; set; } = "";
    public string KeyNote { get; set; } = "";
    public bool TcpReachable { get; set; }
    public bool KeyConfigured { get; set; }
    public bool ListOk { get; set; }
    public int ListStatus { get; set; }
    public string ListError { get; set; } = "";
    public List<string> Nodes { get; set; } = [];
    public bool InfoAvailable { get; set; }
    public string GameName { get; set; } = "";
    public string Worker { get; set; } = "";
    public string GameBuild { get; set; } = "";
    public string ApiVersion { get; set; } = "";
    public string Summary { get; set; } = "";
}
