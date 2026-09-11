using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;

namespace XPlaneEfbBridge;

// Outbound HTTP for the bridge (SimBrief and OpenWeatherMap). The proxy mode is
// read from the live configuration, so switching it in the settings window takes
// effect without restarting the service.
// Which proxy an outbound request should use. SimBrief keeps the global setting
// from the "advanced" page; the OpenWeatherMap tiles have their own (with
// optional credentials, stored encrypted).
internal sealed record ProxyChoice(string Mode, string Url, string User, string Password)
{
    public string Signature => $"{Mode}|{Url}|{User}|{Password}";
    public string Describe() => Mode switch
    {
        BridgeConfig.ProxySystem => "系统代理",
        BridgeConfig.ProxyManual => User.Length > 0 ? $"自定义代理（含认证）" : "自定义代理",
        _ => "不使用代理"
    };
}

internal static class OutboundHttp
{
    private static readonly object Gate = new();
    // One client per proxy configuration. Clients are never disposed by the
    // request path (that was the "Cannot access a disposed object" bug): only
    // the cache may replace an instance, and the replaced one is released after
    // a grace period so in-flight requests are not cut off.
    private static readonly Dictionary<string, HttpClient> Clients = new(StringComparer.Ordinal);
    private static readonly Queue<string> Order = new();
    private const int KeepClients = 4;
    private static readonly TimeSpan DisposeGrace = TimeSpan.FromSeconds(30);

    public static ProxyChoice Global(BridgeConfig config) => new(config.ProxyMode, config.ProxyUrl, "", "");

    public static ProxyChoice Weather(BridgeConfig config) => new(
        config.WeatherProxyMode,
        config.WeatherProxyUrl,
        config.WeatherProxyUser,
        SecretProtection.Unprotect(config.WeatherProxyPassword));

    public static HttpClient For(ProxyChoice choice)
    {
        lock (Gate)
        {
            if (Clients.TryGetValue(choice.Signature, out var existing)) return existing;
            var created = Build(choice);
            Clients[choice.Signature] = created;
            Order.Enqueue(choice.Signature);
            while (Order.Count > KeepClients)
            {
                var stale = Order.Dequeue();
                if (Clients.Remove(stale, out var old)) ScheduleDispose(old);
            }
            return created;
        }
    }

    // Used when a cached client turns out to be disposed anyway (for example an
    // older build disposed it): build a fresh one and drop the broken entry.
    public static HttpClient Rebuild(ProxyChoice choice)
    {
        lock (Gate)
        {
            if (Clients.Remove(choice.Signature, out var broken)) ScheduleDispose(broken);
            var created = Build(choice);
            Clients[choice.Signature] = created;
            Order.Enqueue(choice.Signature);
            return created;
        }
    }

    // Single entry point for outbound calls: keeps the client alive across
    // requests and retries once with a clean client if it was disposed.
    public static async Task<HttpResponseMessage> SendAsync(ProxyChoice choice, Func<HttpClient, Task<HttpResponseMessage>> send)
    {
        try
        {
            return await send(For(choice));
        }
        catch (ObjectDisposedException)
        {
            return await send(Rebuild(choice));
        }
    }

    private static HttpClient Build(ProxyChoice choice)
    {
        var handler = new HttpClientHandler { UseProxy = false };
        if (choice.Mode == BridgeConfig.ProxySystem)
        {
            handler.UseProxy = true;
            handler.Proxy = WebRequest.GetSystemWebProxy();
        }
        else if (choice.Mode == BridgeConfig.ProxyManual && choice.Url.Length > 0)
        {
            handler.UseProxy = true;
            var proxy = new WebProxy(choice.Url);
            if (choice.User.Length > 0 && choice.Password.Length > 0)
                proxy.Credentials = new NetworkCredential(choice.User, choice.Password);
            handler.Proxy = proxy;
        }
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    private static void ScheduleDispose(HttpClient client)
    {
        _ = Task.Delay(DisposeGrace).ContinueWith(_ =>
        {
            try { client.Dispose(); } catch { }
        }, TaskScheduler.Default);
    }
}

// OpenWeatherMap raster overlays, proxied so the API key never leaves the PC.
// Layer names are an allow list (the client can only ask for these five) and the
// tile coordinates are range checked, so the endpoint cannot be pointed anywhere
// else. Tiles are cached in memory for a short while and the upstream request
// rate is capped to stay inside the free plan.
internal sealed class WeatherTiles
{
    private const int CacheMinutes = 10;
    private const int MaxCacheEntries = 400;
    private const int MaxRequestsPerMinute = 50;
    public const int MaxZoom = 12;

    // Overridable for diagnostics/tests (dev switch only, see windows-bridge/README.md).
    private const string DefaultBase = "https://tile.openweathermap.org/map";
    public static string BaseUrl { get; } = Resolve(Environment.GetEnvironmentVariable("EFB_OWM_BASE"));

    private static string Resolve(string? custom) =>
        string.IsNullOrWhiteSpace(custom) ? DefaultBase : custom.Trim().TrimEnd('/');

    public static readonly (string Name, string Upstream, string Label)[] Layers =
    [
        ("clouds", "clouds_new", "云"),
        ("precipitation", "precipitation_new", "降水"),
        ("wind", "wind_new", "风"),
        ("temperature", "temp_new", "温度"),
        ("pressure", "pressure_new", "气压")
    ];

    internal sealed record CachedTile(byte[] Body, string ContentType, long ExpiresAt);

    private readonly ConcurrentDictionary<string, CachedTile> cache = new();
    // Per layer counters so the EFB can tell the user when a layer simply has no
    // data in the current area (OpenWeatherMap answers empty tiles for that).
    private readonly ConcurrentDictionary<string, (int Fetched, int Empty)> counters = new();
    private readonly object gate = new();
    private long windowStart = Environment.TickCount64;
    private int windowCount;

    public static string? UpstreamName(string name) => Layers.FirstOrDefault(layer => layer.Name == name).Upstream;

    public static bool ValidCoordinate(string name, int z, int x, int y)
    {
        if (UpstreamName(name) is null) return false;
        if (z < 0 || z > MaxZoom) return false;
        var limit = 1 << z;
        return x >= 0 && x < limit && y >= 0 && y < limit;
    }

    public static string UpstreamUrl(string name, string apiKey, int z, int x, int y) =>
        $"{BaseUrl}/{UpstreamName(name)}/{z}/{x}/{y}.png?appid={Uri.EscapeDataString(apiKey)}";

    public bool TryGet(string key, out CachedTile tile)
    {
        if (cache.TryGetValue(key, out var found) && found.ExpiresAt > Environment.TickCount64)
        {
            tile = found;
            return true;
        }
        cache.TryRemove(key, out _);
        tile = null!;
        return false;
    }

    public void Store(string key, byte[] body, string contentType, int minutes = CacheMinutes)
    {
        if (cache.Count >= MaxCacheEntries) cache.Clear();
        cache[key] = new CachedTile(body, contentType, Environment.TickCount64 + minutes * 60_000L);
    }

    // Measured on 2026-09-11: an empty OpenWeatherMap tile is ~334 bytes, a tile
    // with data is 4.7 KB (clouds) up to 115 KB (global precipitation).
    private const int EmptyTileBytes = 900;

    public void Record(string layer, int byteCount)
    {
        var empty = byteCount < EmptyTileBytes ? 1 : 0;
        counters.AddOrUpdate(layer,
            _ => (1, empty),
            (_, current) => (current.Fetched + 1, current.Empty + empty));
    }

    public JsonObject Status()
    {
        var layers = new JsonArray();
        foreach (var layer in Layers)
        {
            counters.TryGetValue(layer.Name, out var counter);
            layers.Add(new JsonObject
            {
                ["name"] = layer.Name,
                ["fetched"] = counter.Fetched,
                ["empty"] = counter.Empty
            });
        }
        return new JsonObject { ["layers"] = layers };
    }

    public bool AllowRequest()
    {
        lock (gate)
        {
            var now = Environment.TickCount64;
            if (now - windowStart > 60_000) { windowStart = now; windowCount = 0; }
            if (windowCount >= MaxRequestsPerMinute) return false;
            windowCount++;
            return true;
        }
    }
}
