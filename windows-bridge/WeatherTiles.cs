using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
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

    // The proxy a request would actually go through, for logs and error messages.
    // Never includes credentials.
    public static string DescribeEffective(ProxyChoice choice) => choice.Mode switch
    {
        BridgeConfig.ProxySystem => SystemProxy.Shared.Describe(),
        BridgeConfig.ProxyManual => DescribeManual(choice.Url),
        _ => "不使用代理（直连）"
    };

    private static string DescribeManual(string url)
    {
        if (url.Length == 0) return "自定义代理（未填地址，等同直连）";
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"自定义代理 {uri.Host}:{uri.Port}" : "自定义代理（地址无法解析）";
    }

    // Turns a transport exception into something the user can act on, plus the
    // proxy that was in effect (the usual reason "the browser works but the exe
    // times out" is that the two are not using the same route).
    public static string Explain(Exception cause, ProxyChoice choice)
    {
        var inner = cause is AggregateException aggregate ? aggregate.GetBaseException() : cause;
        var socket = inner as SocketException ?? inner.InnerException as SocketException;
        var text = socket?.SocketErrorCode switch
        {
            SocketError.HostNotFound or SocketError.NoData => "域名解析失败（DNS）",
            SocketError.ConnectionRefused => "连接被拒绝（对端或代理端口没有监听）",
            SocketError.TimedOut => "连接超时",
            SocketError.NetworkUnreachable or SocketError.HostUnreachable => "网络不可达",
            SocketError.ConnectionReset => "连接被重置",
            _ => null
        };
        text ??= inner switch
        {
            TaskCanceledException or TimeoutException => "请求超时",
            AuthenticationException => "TLS/证书校验失败",
            HttpRequestException http => $"HTTP 请求失败：{SecretProtection.Redact(http.Message)}",
            _ => SecretProtection.Redact(inner.Message)
        };
        return $"{text}｜当前走：{DescribeEffective(choice)}";
    }

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
        => await SendAsync(choice, DefaultTimeout, 1, (client, _) => send(client));

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    // Every attempt gets its own deadline, so a slow but working download is not
    // cut off at the same second as a dead connection, and the caller's token
    // still aborts immediately (an iPad that closed the page must not keep the
    // request alive). Transient transport failures are retried with a short
    // backoff; the caller's own cancellation is never retried.
    public static async Task<HttpResponseMessage> SendAsync(
        ProxyChoice choice,
        TimeSpan timeout,
        int attempts,
        Func<HttpClient, CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken token = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);
            try
            {
                return await send(For(choice), deadline.Token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw; // the caller went away: no retry, no error report
            }
            catch (ObjectDisposedException)
            {
                // A disposed shared client is a caller bug, not a network
                // failure: rebuilding is cheap, so this always gets one extra
                // recovery attempt even when the caller asked for no retries.
                _ = Rebuild(choice);
                if (attempt >= attempts + 1) throw;
            }
            catch (Exception)
            {
                if (attempt >= attempts) throw;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), token);
        }
    }

    private static HttpClient Build(ProxyChoice choice)
    {
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            // SimBrief's responses are large and compress well; without this the
            // transfer is several times bigger than it needs to be.
            AutomaticDecompression = DecompressionMethods.All,
            // Corporate/NTLM proxies (and local proxies that ask for auth) need
            // the Windows identity of the user running the bridge.
            DefaultProxyCredentials = CredentialCache.DefaultCredentials
        };
        if (choice.Mode == BridgeConfig.ProxySystem)
        {
            handler.UseProxy = true;
            handler.Proxy = SystemProxy.Shared;
        }
        else if (choice.Mode == BridgeConfig.ProxyManual && choice.Url.Length > 0)
        {
            handler.UseProxy = true;
            var proxy = new WebProxy(choice.Url);
            if (choice.User.Length > 0 && choice.Password.Length > 0)
                proxy.Credentials = new NetworkCredential(choice.User, choice.Password);
            handler.Proxy = proxy;
        }
        // The per-attempt deadline is the real limit; HttpClient's own timeout
        // would apply to the whole (possibly retried) operation instead.
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    // Windows proxy settings can change while the bridge keeps running (Clash and
    // friends toggle them), and a WebProxy resolved once and cached forever was
    // exactly why "the browser can reach SimBrief but the exe times out". This
    // wrapper re-reads them every so often and also honours the conventional
    // HTTPS_PROXY/HTTP_PROXY environment variables when Windows has none.
    internal sealed class SystemProxy : IWebProxy
    {
        private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(45);
        private readonly object gate = new();
        private IWebProxy resolved = new WebProxy();
        private DateTimeOffset resolvedAt = DateTimeOffset.MinValue;

        public static SystemProxy Shared { get; } = new();

        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination) => Current().GetProxy(destination);

        public bool IsBypassed(Uri host)
        {
            try { return Current().IsBypassed(host); }
            catch { return true; }
        }

        public string Describe()
        {
            try
            {
                var probe = new Uri("https://www.simbrief.com/");
                var proxy = Current().GetProxy(probe);
                if (proxy is null || proxy.Host.Length == 0 || proxy == probe) return "系统代理未配置（直连）";
                return $"系统代理 {proxy.Host}:{proxy.Port}";
            }
            catch { return "系统代理（无法读取）"; }
        }

        private IWebProxy Current()
        {
            lock (gate)
            {
                if (DateTimeOffset.UtcNow - resolvedAt < Ttl) return resolved;
                resolved = Resolve();
                resolvedAt = DateTimeOffset.UtcNow;
                return resolved;
            }
        }

        private static IWebProxy Resolve()
        {
            var configured = Environment.GetEnvironmentVariable("HTTPS_PROXY") ?? Environment.GetEnvironmentVariable("https_proxy")
                ?? Environment.GetEnvironmentVariable("HTTP_PROXY") ?? Environment.GetEnvironmentVariable("http_proxy");
            if (!string.IsNullOrWhiteSpace(configured) && Uri.TryCreate(configured.Trim(), UriKind.Absolute, out var uri))
                return new WebProxy(uri);
            try { return WebRequest.GetSystemWebProxy(); }
            catch { return new WebProxy(); }
        }
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
