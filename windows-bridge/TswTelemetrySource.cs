using System.Text.Json.Nodes;

namespace XPlaneEfbBridge;

/// <summary>
/// Translates the TSW6 API payloads into the flat dictionary the EFB front end already understands.
///
/// Every read is defensive on purpose. The node names, endpoint names and field names come from
/// third-party documentation (docs/TSW6-TELEMETRY.md §3.4), not from a document we could read
/// ourselves, so:
///   - each value is looked up under several candidate names,
///   - a missing or renamed field is dropped rather than thrown,
///   - the game's "undefined" sentinel 3.4028e+38 and out-of-range coordinates are rejected.
/// A payload that yields no usable coordinate produces no frame at all, so a broken API cannot drag
/// the map to (0, 0) or leave a stale position looking live.
/// </summary>
internal static class TswMapper
{
    public const string Protocol = "TSW-API";

    // The game's "this value is undefined" marker (float.MaxValue).
    private const double Undefined = 3.4028e38;
    private const double UndefinedTolerance = 1e30;

    private static readonly string[] LatitudeKeys = ["latitude", "lat", "Latitude", "Lat"];
    private static readonly string[] LongitudeKeys = ["longitude", "lon", "lng", "Longitude", "Lon"];
    private static readonly string[] SpeedKeys = ["Speed (ms)", "Speed (m/s)", "Speed", "speed", "SpeedMS"];
    private static readonly string[] LimitKeys = ["speedLimit", "SpeedLimit", "currentSpeedLimit"];
    private static readonly string[] NextLimitKeys = ["nextSpeedLimit", "NextSpeedLimit"];
    private static readonly string[] TrackMaxKeys = ["trackMaxSpeed", "TrackMaxSpeed"];
    private static readonly string[] ServiceMaxKeys = ["serviceMaxSpeed", "ServiceMaxSpeed"];
    private static readonly string[] FormationMaxKeys = ["formationMaxSpeed", "FormationMaxSpeed"];

    /// <summary>Builds one telemetry frame. <paramref name="speed"/> and <paramref name="driverAid"/>
    /// may be null: a position is the only hard requirement, and every other field degrades to
    /// "absent", which the front end already renders as "—".</summary>
    public static Dictionary<string, object> Map(JsonObject? player, JsonObject? speed, JsonObject? driverAid)
    {
        var frame = new Dictionary<string, object>();
        if (player is null) return frame;

        var latitude = FindCoordinate(player, LatitudeKeys);
        var longitude = FindCoordinate(player, LongitudeKeys);
        // A sentinel or an impossible coordinate means "no position yet" (main menu, loading screen).
        if (latitude is not double lat || lat is < -90 or > 90) return frame;
        if (longitude is not double lon || lon is < -180 or > 180) return frame;
        frame["latitude"] = lat;
        frame["longitude"] = lon;

        var metresPerSecond = Number(Find(speed, SpeedKeys)) ?? Number(Find(player, SpeedKeys));
        if (metresPerSecond is double ms && Plausible(ms))
        {
            frame["groundSpeedKmh"] = Math.Round(ms * 3.6, 1);
            // Kept so the existing front-end formatting keeps working unchanged.
            frame["groundSpeedKt"] = Math.Round(ms * 1.943844, 1);
        }

        var service = FirstText(player, ["currentServiceName", "serviceName"]);
        if (service.Length > 0) frame["currentServiceName"] = service;

        var profile = FirstText(player, ["playerProfileName", "profileName"]);
        if (profile.Length > 0) frame["playerProfileName"] = profile;

        if (driverAid is not null)
        {
            AddSpeedLimit(frame, "limitKmh", Find(driverAid, LimitKeys));
            AddSpeedLimit(frame, "nextLimitKmh", Find(driverAid, NextLimitKeys));
            AddSpeedLimit(frame, "trackMaxKmh", Find(driverAid, TrackMaxKeys));
            AddSpeedLimit(frame, "serviceMaxKmh", Find(driverAid, ServiceMaxKeys));
            AddSpeedLimit(frame, "formationMaxKmh", Find(driverAid, FormationMaxKeys));

            AddDistance(frame, "distanceToNextLimitM", Find(driverAid, ["distanceToNextSpeedLimit", "distanceToNextLimit"]));
            AddDistance(frame, "distanceToSignalM", Find(driverAid, ["distanceToSignal", "distanceToNextSignal"]));

            var gradient = Number(Find(driverAid, ["gradient", "Gradient"]));
            if (gradient is double slope && Plausible(slope) && Math.Abs(slope) < 100) frame["gradient"] = Math.Round(slope, 2);

            var aspect = FirstText(driverAid, ["signalAspectClass", "signalAspect"]);
            if (aspect.Length > 0) frame["signalAspect"] = aspect;
        }

        frame["protocol"] = Protocol;
        frame["receivedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return frame;
    }

    /// <summary>The speed-limit fields arrive in metres per second, sometimes as a bare number and
    /// sometimes wrapped in a {value: …} object.</summary>
    private static void AddSpeedLimit(Dictionary<string, object> frame, string name, JsonNode? node)
    {
        var value = Number(node);
        if (value is null && node is JsonObject wrapper) value = Number(Find(wrapper, ["value", "Value", "speed", "Speed"]));
        if (value is not double ms || !Plausible(ms)) return;
        var kmh = ms * 3.6;
        // Above 500 km/h this is not a railway speed limit; reject instead of printing nonsense.
        if (kmh <= 0 || kmh > 500) return;
        frame[name] = Math.Round(kmh, 0);
    }

    private static void AddDistance(Dictionary<string, object> frame, string name, JsonNode? node)
    {
        var value = Number(node);
        if (value is null && node is JsonObject wrapper) value = Number(Find(wrapper, ["value", "Value"]));
        if (value is not double metres || !Plausible(metres)) return;
        if (metres is < 0 or > 200000) return;
        frame[name] = Math.Round(metres, 0);
    }

    /// <summary>Reads a coordinate. The documented position endpoint nests it as
    /// <c>Values.geoLocation.latitude</c>, but other candidate endpoints put it at the top level or
    /// one level down under a differently named wrapper, so the direct read is tried first and a
    /// bounded search follows. Without this the whole frame is dropped even though the payload
    /// clearly carries a position.</summary>
    private static double? FindCoordinate(JsonObject? node, string[] keys) =>
        Number(Find(node, keys)) ?? FindDeep(node, keys);

    private static string FirstText(JsonObject? node, string[] keys)
    {
        foreach (var key in keys)
        {
            var text = TswApiClient.Text(node, key);
            if (text.Length > 0) return text;
        }
        return "";
    }

    /// <summary>First present candidate, in order. An empty string counts as absent, because the
    /// game uses "" for "this section is switched off" in several places.</summary>
    public static JsonNode? Find(JsonObject? node, params string[] keys)
    {
        if (node is null) return null;
        foreach (var key in keys)
        {
            if (!node.TryGetPropertyValue(key, out var value)) continue;
            if (value is JsonValue text && text.TryGetValue<string>(out var stringValue) && (stringValue ?? "").Length == 0) continue;
            return value;
        }
        return null;
    }

    /// <summary>Reads a numeric value, accepting both a JSON number and a numeric string.</summary>
    public static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<double>(out var number)) return number;
        if (value.TryGetValue<string>(out var text)
            && double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }

    /// <summary>Rejects the undefined sentinel and non-finite values.</summary>
    public static bool Plausible(double value) =>
        double.IsFinite(value) && Math.Abs(value) < UndefinedTolerance && Math.Abs(value - Undefined) > 1;

    /// <summary>Finds a coordinate-shaped number anywhere under the given node, so a payload that
    /// nests the position differently than documented still yields a position. Arrays are searched
    /// as well: the subscription envelope carries its values in <c>Entries[]</c> and the driver-aid
    /// blob has look-ahead arrays of its own. The depth limit keeps that from becoming an unbounded
    /// tree walk on every frame.</summary>
    public static double? FindDeep(JsonObject? node, string[] keys, int depth = 5) =>
        node is null ? null : FindDeepNode(node, keys, depth);

    private static double? FindDeepNode(JsonNode? node, string[] keys, int depth)
    {
        if (node is JsonObject obj)
        {
            var direct = Number(Find(obj, keys));
            if (direct is not null) return direct;
            if (depth <= 0) return null;
            foreach (var item in obj)
            {
                var nested = FindDeepNode(item.Value, keys, depth - 1);
                if (nested is not null) return nested;
            }
            return null;
        }
        if (node is not JsonArray array || depth <= 0) return null;
        foreach (var item in array)
        {
            var nested = FindDeepNode(item, keys, depth - 1);
            if (nested is not null) return nested;
        }
        return null;
    }

    /// <summary>True when this exact object carries a usable latitude/longitude pair (no search).</summary>
    public static bool HasCoordinate(JsonObject? node)
    {
        var latitude = Number(Find(node, LatitudeKeys));
        if (latitude is not double lat || lat is < -90 or > 90) return false;
        var longitude = Number(Find(node, LongitudeKeys));
        return longitude is double lon && lon is >= -180 and <= 180;
    }

    /// <summary>The coordinate pair carried anywhere inside this payload, or null when it holds no
    /// usable one (depth-limited, arrays included, sentinels and impossible values rejected). Shared
    /// by discovery, the subscription fallback and the settings window's test button, so all three
    /// agree on what "this payload is a position" means.</summary>
    public static (double Latitude, double Longitude)? Coordinates(JsonObject? node)
    {
        if (FindDeep(node, LatitudeKeys) is not double lat || lat is < -90 or > 90) return null;
        if (FindDeep(node, LongitudeKeys) is not double lon || lon is < -180 or > 180) return null;
        return (lat, lon);
    }

    public static bool HasPosition(JsonObject? node) => Coordinates(node) is not null;

    /// <summary>Pulls the value object out of a <c>/subscription</c> response. The envelope is
    /// <c>{RequestedSubscriptionID, Entries:[{Values:{…}}, …]}</c>, and the entry that carries a
    /// coordinate is the position one. Returns null when no entry holds a position, so the caller
    /// can report "not in a route yet" instead of drawing a marker at 0,0.</summary>
    public static JsonObject? SubscriptionValues(JsonObject? root)
    {
        if (root is null) return null;
        if (root["Entries"] is not JsonArray entries) return HasPosition(root) ? root : null;
        foreach (var entry in entries)
        {
            var values = entry is JsonObject item ? item["Values"] as JsonObject ?? item : null;
            if (values is null) continue;
            if (HasPosition(values)) return values;
        }
        return null;
    }

    /// <summary>Field names of a payload, for the settings window's failure report. Names only,
    /// never values: a screenshot of the dialog must not be able to leak a key or a profile name.</summary>
    public static string DescribeFields(JsonObject? node, int max = 12)
    {
        if (node is null) return "（没有 Values 对象）";
        var names = node.Select(item => item.Key).Where(name => name.Length > 0).Take(max).ToList();
        if (names.Count == 0) return "（空对象）";
        var text = string.Join("、", names);
        return node.Count > names.Count ? $"{text}…（共 {node.Count} 个）" : text;
    }
}

/// <summary>
/// Polling loop for the TSW6 API. It is the exact counterpart of the UDP listener in
/// <see cref="BridgeService"/>: it produces one telemetry dictionary per tick and hands it to the
/// same <c>Publish</c> path, so the WebSocket, the map and the track need nothing new.
///
/// Deliberate design points:
///   - 4 Hz by default. The API has no push and no batching, and every frame becomes one WebSocket
///     message per viewer, so the polling rate is a real cost (docs/TSW6-TELEMETRY.md §6).
///   - the optional nodes (speed, driver aid) are fetched in the same tick as the position, so a
///     frame is never half-old.
///   - a node that stops answering has its endpoint names re-discovered, which is how the
///     integration survives a rename in a game patch instead of silently going blank.
///   - failures back off exponentially and publish nothing, so the front end reports "stale" through
///     the existing 3 s timeout rather than showing frozen data as if it were live.
/// </summary>
internal sealed class TswTelemetrySource : IDisposable
{
    private static readonly TimeSpan NodeRetryInterval = TimeSpan.FromSeconds(30);
    // Discovery used to be one-shot; this is how long it waits before trying again.
    private static readonly TimeSpan DiscoveryRetryInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    // Candidate names from docs/TSW6-TELEMETRY.md §3.4. Candidates, not facts: discovery replaces
    // them with whatever the running game actually exposes.
    private const string PositionNodeDefault = "DriverAid";
    private const string PositionEndpointDefault = "PlayerInfo";
    private const string SpeedNodeDefault = "CurrentDrivableActor";
    private const string SpeedEndpointDefault = "Function.HUD_GetSpeed";
    private const string DriverAidNodeDefault = "DriverAid";
    private const string DriverAidEndpointDefault = "Data";

    /// <summary>Position candidates in the order the third-party docs attest them. Shared by /get
    /// discovery and by the subscription fallback so the two can never drift apart.</summary>
    internal static readonly (string Node, string Endpoint)[] PositionCandidates =
    [
        (PositionNodeDefault, PositionEndpointDefault),
        ("DriverAid", "Data"),
        ("CurrentDrivableActor", "LatLon"),
        ("CurrentDrivableActor", "Function.LatLon")
    ];

    private readonly BridgeConfig config;
    private readonly Action<Dictionary<string, object>> publish;
    private readonly Action<string>? log;
    private readonly object gate = new();
    private readonly List<Action<JsonObject?, JsonObject?, JsonObject?>> subscribers = [];
    // A random id keeps this process from fighting another tool (ThirdRails, a controller app) that
    // is already subscribed on the same game.
    private readonly int subscriptionId = Random.Shared.Next(1, ushort.MaxValue);

    private CancellationTokenSource? cancellation;
    private Task? loop;
    private TswApiClient? client;
    private string clientSignature = "";

    // Endpoint state. "Live" means the last call succeeded; when it turns false the name is
    // re-discovered after a cooldown instead of being hammered four times a second.
    private bool positionLive;
    private bool positionViaSubscription;
    // False when the last discovery could not reach the API at all (game not running): that is a
    // failure and keeps the exponential backoff, while "the API answered but has no coordinate yet"
    // is not and must not back off.
    private bool positionReachable;
    private string positionNode = PositionNodeDefault;
    private string positionEndpoint = PositionEndpointDefault;
    private DateTimeOffset positionRetryAt;

    private bool speedLive;
    private bool speedGaveUp;
    private string speedNode = SpeedNodeDefault;
    private string speedEndpoint = SpeedEndpointDefault;
    private DateTimeOffset speedRetryAt;

    private bool driverAidLive;
    private bool driverAidGaveUp;
    private string driverAidNode = DriverAidNodeDefault;
    private string driverAidEndpoint = DriverAidEndpointDefault;
    private DateTimeOffset driverAidRetryAt;

    private long frames;
    private long errors;
    private DateTimeOffset? lastFrameAt;
    private string lastError = "";
    private string lastNote = "尚未轮询。";

    public TswTelemetrySource(BridgeConfig config, Action<Dictionary<string, object>> publish, Action<string>? log = null)
    {
        this.config = config;
        this.publish = publish;
        this.log = log;
    }

    public int PollHz => Math.Clamp(config.TswPollHz, 1, 10);

    /// <summary>Snapshot for /api/status, /api/tsw/status and the settings window.</summary>
    public Dictionary<string, object> Status()
    {
        lock (gate)
        {
            return new Dictionary<string, object>
            {
                ["configured"] = true,
                ["apiUrl"] = client?.BaseUrl ?? TswApiClient.NormalizeBase(config.TswApiUrl, out _),
                ["pollHz"] = PollHz,
                ["frames"] = frames,
                ["errors"] = errors,
                ["lastFrameAt"] = lastFrameAt?.ToUnixTimeMilliseconds() ?? 0L,
                ["lastError"] = lastError,
                ["note"] = lastNote,
                ["keyFile"] = client?.KeyPathUsed ?? "",
                ["keyNote"] = client?.KeyFileNote ?? "",
                ["endpoints"] = new Dictionary<string, object>
                {
                    ["position"] = positionLive
                        ? $"{positionNode}.{positionEndpoint}{(positionViaSubscription ? "（订阅）" : "")}"
                        : "",
                    ["positionMode"] = positionViaSubscription ? "subscription" : "get",
                    ["speed"] = speedLive ? $"{speedNode}.{speedEndpoint}" : "",
                    ["driverAid"] = driverAidLive ? $"{driverAidNode}.{driverAidEndpoint}" : ""
                }
            };
        }
    }

    /// <summary>Test hook: receives every mapped frame's raw inputs. Lets a self-test drive the
    /// mapping and the state machine without a game installed.</summary>
    public void Subscribe(Action<JsonObject?, JsonObject?, JsonObject?> subscriber)
    {
        lock (gate) subscribers.Add(subscriber);
    }

    public void Start()
    {
        if (loop is not null) return;
        cancellation = new CancellationTokenSource();
        loop = Task.Run(() => RunAsync(cancellation.Token));
    }

    public async Task StopAsync()
    {
        var token = cancellation;
        token?.Cancel();
        var running = loop;
        if (running is not null)
        {
            try { await running.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception) { }
        }
        loop = null;
        token?.Dispose();
        cancellation = null;
        // Best effort: a subscription left behind keeps accumulating on the game's side.
        if (positionViaSubscription && client is not null)
        {
            try
            {
                using var unsubscribe = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                await client.UnsubscribeAsync(subscriptionId, unsubscribe.Token);
            }
            catch (Exception) { }
        }
        lock (gate)
        {
            client?.Dispose();
            client = null;
            clientSignature = "";
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        var backoff = MinBackoff;
        while (!token.IsCancellationRequested)
        {
            var started = Environment.TickCount64;
            var api = Client();
            var outcome = PollOutcome.Failed;
            try
            {
                outcome = await PollOnceAsync(api, token);
                if (outcome != PollOutcome.Failed) backoff = MinBackoff;
                else if (lastError.Length == 0) lock (gate) lastError = api.KeyConfigured ? lastNote : api.KeyFileNote;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                lock (gate)
                {
                    errors++;
                    lastError = error.GetType().Name + "：" + error.Message;
                    lastNote = "轮询时出错，正在重试。";
                }
                outcome = PollOutcome.Failed;
            }

            var elapsed = Environment.TickCount64 - started;
            var interval = 1000.0 / PollHz;
            int delay;
            // "Answered, but there is no position yet" (main menu, loading screen) is not a failure:
            // backing off there would make the map wait half a minute after a route finally loads.
            if (outcome is PollOutcome.Published or PollOutcome.NoPosition)
            {
                delay = (int)Math.Max(50, interval - elapsed);
            }
            else
            {
                delay = (int)Math.Max(interval, backoff.TotalMilliseconds);
                backoff = TimeSpan.FromMilliseconds(Math.Min(MaxBackoff.TotalMilliseconds, backoff.TotalMilliseconds * 2));
            }
            try { await Task.Delay(delay, token); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>What a polling tick achieved. The middle case exists so that "the API is fine, the
    /// game just has no coordinate yet" does not trigger the failure backoff.</summary>
    private enum PollOutcome { Published, NoPosition, Failed }

    /// <summary>The settings window edits the same configuration instance, so a changed URL or key
    /// path rebuilds the client instead of waiting for a restart.</summary>
    private TswApiClient Client()
    {
        var signature = $"{config.TswApiUrl}|{config.TswApiKeyPath}";
        lock (gate)
        {
            if (client is null || clientSignature != signature)
            {
                client?.Dispose();
                client = new TswApiClient(config.TswApiKeyPath, config.TswApiUrl);
                clientSignature = signature;
            }
            return client;
        }
    }

    /// <summary>One polling tick.</summary>
    private async Task<PollOutcome> PollOnceAsync(TswApiClient api, CancellationToken token)
    {
        if (!positionLive)
        {
            // Discovery is retried on a cooldown. It used to be one-shot, and the first probe almost
            // always runs while the game is still on the main menu - where the API has no coordinate
            // yet. One failure there left the bridge blind until the service was restarted, while
            // the settings window's own test kept succeeding, which made it look like a field-name
            // problem (docs/TSW6-TELEMETRY.md §3.4).
            if (positionRetryAt > DateTimeOffset.UtcNow) return positionReachable ? PollOutcome.NoPosition : PollOutcome.Failed;
            var answered = await DiscoverPositionAsync(api, token);
            if (!positionLive) return answered ? PollOutcome.NoPosition : PollOutcome.Failed;
        }

        var position = await ReadPositionAsync(api, token);
        if (position is null) return PollOutcome.NoPosition;

        var speed = await OptionalAsync(api, true);
        var driverAid = await OptionalAsync(api, false);

        var frame = TswMapper.Map(position, speed, driverAid);
        if (!frame.ContainsKey("latitude"))
        {
            Note("响应里没有可用的经纬度（可能还在主菜单或加载中）。");
            return PollOutcome.NoPosition;
        }
        frame["source"] = $"{positionNode}.{positionEndpoint}";
        frame["apiUrl"] = api.BaseUrl;

        foreach (var subscriber in SubscriberSnapshot())
        {
            try { subscriber(position, speed, driverAid); } catch { }
        }

        lock (gate)
        {
            frames++;
            lastFrameAt = DateTimeOffset.UtcNow;
            lastError = "";
            lastNote = speed is null && driverAid is null
                ? "已在推送位置；速度与限速字段还没读到。"
                : "运行中。";
        }
        publish(frame);
        return PollOutcome.Published;
    }

    /// <summary>Reads the position payload for this tick: the plain /get endpoint, or the
    /// subscription envelope when discovery found the position there. Returns null when this tick has
    /// no position, which is a normal state while the game sits in a menu.</summary>
    private async Task<JsonObject?> ReadPositionAsync(TswApiClient api, CancellationToken token)
    {
        if (!positionViaSubscription)
        {
            var response = await api.GetAsync(positionNode, positionEndpoint, token);
            if (response.Ok) return response.Values;
            // The API had answered before, so this is a real failure rather than a wrong name: clear
            // "live" and let the cooldown re-discover in case the game changed its routes.
            lock (gate)
            {
                positionLive = false;
                positionRetryAt = DateTimeOffset.UtcNow + NodeRetryInterval;
                lastError = response.Message;
            }
            Note("位置读取失败：" + response.Message);
            return null;
        }

        var subscription = await api.ReadSubscriptionAsync(subscriptionId, token);
        if (!subscription.Ok)
        {
            lock (gate)
            {
                positionLive = false;
                positionViaSubscription = false;
                positionRetryAt = DateTimeOffset.UtcNow + NodeRetryInterval;
                lastError = subscription.Message;
            }
            Note("读订阅失败：" + subscription.Message);
            return null;
        }

        var values = TswMapper.SubscriptionValues(subscription.Root ?? subscription.Values);
        if (values is null)
        {
            // The route answered but holds no coordinate yet. Keep the subscription instead of
            // tearing it down; the next tick retries immediately.
            Note("订阅里还没有经纬度（可能还在主菜单或加载中）。");
            return null;
        }
        return values;
    }

    private Action<JsonObject?, JsonObject?, JsonObject?>[] SubscriberSnapshot()
    {
        lock (gate) return [.. subscribers];
    }

    /// <summary>Reads the speed node (<paramref name="isSpeed"/> true) or the driver-aid node,
    /// discovering its name when it is unknown or has stopped answering.</summary>
    private async Task<JsonObject?> OptionalAsync(TswApiClient api, bool isSpeed)
    {
        var node = isSpeed ? speedNode : driverAidNode;
        var endpoint = isSpeed ? speedEndpoint : driverAidEndpoint;
        var live = isSpeed ? speedLive : driverAidLive;
        var gaveUp = isSpeed ? speedGaveUp : driverAidGaveUp;
        var retryAt = isSpeed ? speedRetryAt : driverAidRetryAt;

        if (!live)
        {
            if (gaveUp || retryAt > DateTimeOffset.UtcNow) return null;
            var candidate = await DiscoverOptionalAsync(api, isSpeed, node, endpoint);
            if (candidate is null) return null;
            return candidate;
        }

        var response = await api.GetAsync(node, endpoint, token: default);
        if (response.Ok) return response.Values;

        SetOptionalState(isSpeed, live: false, gaveUp: false, retryAt: DateTimeOffset.UtcNow, node, endpoint);
        Note($"可选端点 {node}.{endpoint} 读取失败：{response.Message}");
        return null;
    }

    private async Task<JsonObject?> DiscoverOptionalAsync(TswApiClient api, bool isSpeed, string node, string endpoint)
    {
        var probe = await api.GetAsync(node, endpoint, token: default);
        if (!probe.Ok && isSpeed)
        {
            // The documented alternative name for the speed endpoint, tried before giving up.
            var alternative = await api.GetAsync("CurrentDrivableActor", "Speed", token: default);
            if (alternative.Ok)
            {
                SetOptionalState(isSpeed, live: true, gaveUp: false, retryAt: default, "CurrentDrivableActor", "Speed");
                return alternative.Values;
            }
        }
        if (probe.Ok)
        {
            SetOptionalState(isSpeed, live: true, gaveUp: false, retryAt: default, node, endpoint);
            Note($"可选端点已就绪：{node}.{endpoint}");
            return probe.Values;
        }

        // Give up once, loudly, instead of retrying four times a second. The status endpoint keeps
        // reporting the reason so the iPad can explain an empty field.
        SetOptionalState(isSpeed, live: false, gaveUp: true, retryAt: DateTimeOffset.UtcNow + NodeRetryInterval, node, endpoint);
        Note($"可选端点 {node}.{endpoint} 不可用：{probe.Message}。用 --tsw-probe 查看游戏实际暴露的端点清单。");
        log?.Invoke(lastNote);
        return null;
    }

    private void SetOptionalState(bool isSpeed, bool live, bool gaveUp, DateTimeOffset retryAt, string node, string endpoint)
    {
        lock (gate)
        {
            if (isSpeed)
            {
                speedLive = live;
                speedGaveUp = gaveUp;
                speedRetryAt = retryAt;
                speedNode = node;
                speedEndpoint = endpoint;
            }
            else
            {
                driverAidLive = live;
                driverAidGaveUp = gaveUp;
                driverAidRetryAt = retryAt;
                driverAidNode = node;
                driverAidEndpoint = endpoint;
            }
        }
    }

    /// <summary>Position endpoint discovery. The candidate list is ordered by how well each name is
    /// attested in the third-party documentation, and a candidate is accepted only when the response
    /// really carries a coordinate — a node that exists but holds something else is not a position.
    /// When no /get candidate carries one, the subscription shape is tried: both public TSW6 clients
    /// read the player position from /subscription rather than /get, so a game build whose plain
    /// endpoint does not expose <c>geoLocation</c> still works.</summary>
    private async Task<bool> DiscoverPositionAsync(TswApiClient api, CancellationToken token)
    {
        var answered = false;
        foreach (var (node, endpoint) in PositionCandidates)
        {
            var probe = await api.GetAsync(node, endpoint, token);
            if (!probe.Ok) continue;
            answered = true;
            if (!TswMapper.HasPosition(probe.Values)) continue;
            lock (gate)
            {
                positionNode = node;
                positionEndpoint = endpoint;
                positionViaSubscription = false;
                positionLive = true;
                positionReachable = true;
                positionRetryAt = default;
            }
            Note($"已定位位置端点：{node}.{endpoint}");
            return true;
        }

        var subscription = await TrySubscriptionAsync(api, token);
        if (subscription.Found) return true;
        answered |= subscription.Answered;

        // Not a hard stop: the usual reason is that the game is still in a menu, and the retry below
        // is what lets the map come alive once a route is loaded.
        var tried = string.Join("、", PositionCandidates.Select(item => $"{item.Node}.{item.Endpoint}"));
        var message = answered
            ? $"暂时读不到位置：已试 {tried} 与 /subscription，游戏返回了响应但没有坐标。"
                + "在主菜单或加载中就是这样，进入线路后会自动开始；若长时间没有，请用 --tsw-probe 查看游戏实际暴露的端点清单。"
            : $"连不上 TSW6 API（{api.BaseUrl}）：游戏没在运行，或启动项里没有 -HTTPAPI。"
                + "游戏启动后会自动重试，不需要重启本程序。";
        lock (gate)
        {
            positionReachable = answered;
            positionRetryAt = DateTimeOffset.UtcNow + DiscoveryRetryInterval;
            lastNote = message;
        }
        log?.Invoke(message);
        return answered;
    }

    /// <summary>Registers the candidate paths on one subscription and reads them back. Both public
    /// TSW6 clients get the player position this way, so this is the second documented shape of the
    /// same data rather than a guess (docs/TSW6-TELEMETRY.md §3.2).</summary>
    private async Task<(bool Found, bool Answered)> TrySubscriptionAsync(TswApiClient api, CancellationToken token)
    {
        var answered = false;
        foreach (var (node, endpoint) in PositionCandidates)
        {
            var registered = await api.SubscribeAsync(node, endpoint, subscriptionId, token);
            if (!registered.Ok) continue;
            answered = true;
            var read = await api.ReadSubscriptionAsync(subscriptionId, token);
            if (!read.Ok) continue;
            answered = true;
            var values = TswMapper.SubscriptionValues(read.Root ?? read.Values);
            if (values is null) continue;
            lock (gate)
            {
                positionNode = node;
                positionEndpoint = endpoint;
                positionViaSubscription = true;
                positionLive = true;
                positionReachable = true;
                positionRetryAt = default;
            }
            var message = $"已定位位置端点：{node}.{endpoint}（订阅）";
            lock (gate) lastNote = message;
            log?.Invoke(message);
            return (true, answered);
        }
        return (false, answered);
    }

    private void Note(string text)
    {
        lock (gate) lastNote = text;
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}
