using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XPlaneEfbBridge;

internal sealed class ConfigConflictException : Exception
{
    public ConfigConflictException(string message) : base(message) { }
}

// Result of reading the configuration file: the typed settings that are actually
// in effect, plus the raw JSON so keys this program does not know about survive
// a round trip through the settings window.
internal sealed class ConfigSnapshot
{
    public required BridgeConfig Config { get; init; }
    public required JsonObject Raw { get; init; }
    public required string Path { get; init; }
    public required DateTime WriteTimeUtc { get; init; }
    public required long Length { get; init; }
    public required bool Repaired { get; init; }
    public string? BackupPath { get; init; }
    public string? Note { get; init; }
}

// Reads and writes %LOCALAPPDATA%\XPlaneEfbBridge\bridge-config.json.
//
// The file is the user's own text file, so the rules are:
//   * never lose a value: unknown keys are copied back verbatim;
//   * never lose the file: every rewrite is preceded by a timestamped backup;
//   * never leave a half written file: write a temp file, then replace atomically;
//   * never silently overwrite somebody else's edit: callers pass the write time
//     they saw when the settings window opened.
internal static class ConfigStore
{
    public static readonly string[] KnownKeys =
    [
        "UdpPorts", "WebPort", "SourceIp",
        "CustomBaseMapName", "CustomBaseMapUrl", "CustomBaseMapAttribution",
        "SimbriefUser", "SimbriefApiUrl",
        "WeatherApiKey",
        "WeatherProxyMode", "WeatherProxyUrl", "WeatherProxyUser", "WeatherProxyPassword",
        "XplanePath", "GroundMinZoom", "AirlineLogoUrlTemplate", "ManualFolder",
        "ProxyMode", "ProxyUrl",
        // Which simulator feeds the map (xp = X-Plane 12 over UDP, tsw = Train Sim World 6
        // over its local HTTP API) plus the TSW endpoint, key path and polling rate.
        "TelemetrySource", "TswApiUrl", "TswApiKeyPath", "TswPollHz"
    ];

    public static readonly string[] ObsoleteKeys =
    [
        "NavigraphExternalUrl", "AuthorizedChartTileUrl", "AuthorizedChartAttribution"
    ];

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    // Relaxed escaping keeps Chinese text readable when the user opens the file
    // in Notepad instead of turning it into \uXXXX.
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XPlaneEfbBridge", "bridge-config.json");

    public static string DirectoryFor(string path) => Path.GetDirectoryName(path) ?? ".";

    // The configuration this process is actually running on. Cache files (the
    // SimBrief history, the airport index) live next to it, so running a second
    // configuration - a test fixture or --headless <other.json> - stays
    // self-contained instead of mixing its data into the user's real folder.
    public static string ActivePath { get; private set; } = DefaultPath;

    public static ConfigSnapshot Load(string path)
    {
        ActivePath = path;
        Directory.CreateDirectory(DirectoryFor(path));
        if (!File.Exists(path))
        {
            var fresh = new BridgeConfig();
            var raw = new JsonObject();
            Write(path, raw, null, null, 0);
            var info = new FileInfo(path);
            return new ConfigSnapshot
            {
                Config = fresh, Raw = raw, Path = path,
                WriteTimeUtc = info.LastWriteTimeUtc, Length = info.Length,
                Repaired = false,
                Note = $"配置文件不存在，已按默认值创建：{path}"
            };
        }

        var info2 = new FileInfo(path);
        var source = File.ReadAllText(path);
        BridgeConfig config;
        var repaired = false;
        string? backup = null;
        string? note = null;
        try
        {
            config = JsonSerializer.Deserialize<BridgeConfig>(source, ReadOptions) ?? new BridgeConfig();
        }
        catch (JsonException error)
        {
            if (TryExtractCurrentConfig(source, out var validObject))
            {
                config = JsonSerializer.Deserialize<BridgeConfig>(validObject, ReadOptions) ?? new BridgeConfig();
                repaired = true;
                note = "原文件不是合法 JSON（可能拼接了多个对象），已按最后一个有效对象读取。";
            }
            else
            {
                // Nothing usable: keep the file (backed up) and start from defaults
                // so the tray, the web app and the settings window still work.
                config = new BridgeConfig();
                backup = BackupPath(path, "invalid");
                File.Copy(path, backup, true);
                note = $"原配置文件无法解析（{error.Message}），已备份为 {Path.GetFileName(backup)} 并暂时使用默认值。请在设置窗口中确认后再保存。";
            }
        }

        config.Normalize();
        var root = ParseRoot(source, repaired ? config : null) ?? BuildRoot(config);
        if (repaired && backup is null)
        {
            // Same repair behaviour as before, but now reported to the caller and
            // visible in the settings window instead of happening silently.
            backup = BackupPath(path, "invalid");
            File.Copy(path, backup, true);
            var raw = BuildRoot(config, root, cleanObsolete: false);
            Write(path, raw, null, null, 0);
            root = raw;
            note += $" 原文件已备份为 {Path.GetFileName(backup)}。";
            info2 = new FileInfo(path);
        }

        var warnings = config.Warnings();
        if (warnings.Count > 0) note = string.Join(" ", new[] { note }.Concat(warnings).Where(text => !string.IsNullOrWhiteSpace(text)));

        return new ConfigSnapshot
        {
            Config = config,
            Raw = root,
            Path = path,
            WriteTimeUtc = info2.LastWriteTimeUtc,
            Length = info2.Length,
            Repaired = repaired,
            BackupPath = backup,
            Note = note
        };
    }

    // Writes the edited settings while keeping every other key of the file, and
    // refuses to continue if the file changed behind our back.
    public static string Save(ConfigSnapshot snapshot, BridgeConfig config, bool cleanObsolete, DateTime expectedWriteTimeUtc, long expectedLength)
    {
        var path = snapshot.Path;
        if (File.Exists(path))
        {
            var current = new FileInfo(path);
            if (current.LastWriteTimeUtc != expectedWriteTimeUtc || current.Length != expectedLength)
                throw new ConfigConflictException("配置文件在设置窗口打开期间被其它程序修改过。");
        }

        config.Normalize();
        var root = BuildRoot(config, snapshot.Raw, cleanObsolete);
        // Nothing changed: do not rewrite the file and do not add a backup.
        if (File.Exists(path) && File.ReadAllText(path) == root.ToJsonString(WriteOptions)) return "";
        var backup = File.Exists(path) ? BackupPath(path, "") : null;
        Write(path, root, backup, expectedWriteTimeUtc, expectedLength);
        return backup ?? "";
    }

    public static IReadOnlyList<FileInfo> Backups(string path)
    {
        var directory = DirectoryFor(path);
        if (!Directory.Exists(directory)) return [];
        var name = Path.GetFileName(path);
        return new DirectoryInfo(directory).GetFiles($"{name}.*.bak")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();
    }

    private static JsonObject BuildRoot(BridgeConfig config, JsonObject? existing = null, bool cleanObsolete = false)
    {
        var root = new JsonObject
        {
            ["UdpPorts"] = new JsonArray(config.UdpPorts.Select(port => (JsonNode)JsonValue.Create(port)).ToArray()),
            ["WebPort"] = config.WebPort,
            ["SourceIp"] = config.SourceIp,
            ["CustomBaseMapName"] = config.CustomBaseMapName,
            ["CustomBaseMapUrl"] = config.CustomBaseMapUrl,
            ["CustomBaseMapAttribution"] = config.CustomBaseMapAttribution,
            ["SimbriefUser"] = config.SimbriefUser,
            ["SimbriefApiUrl"] = config.SimbriefApiUrl,
            ["WeatherApiKey"] = config.WeatherApiKey,
            ["WeatherProxyMode"] = config.WeatherProxyMode,
            ["WeatherProxyUrl"] = config.WeatherProxyUrl,
            ["WeatherProxyUser"] = config.WeatherProxyUser,
            ["WeatherProxyPassword"] = config.WeatherProxyPassword,
            ["XplanePath"] = config.XplanePath,
            ["GroundMinZoom"] = config.GroundMinZoom,
            ["AirlineLogoUrlTemplate"] = config.AirlineLogoUrlTemplate,
            ["ManualFolder"] = config.ManualFolder,
            ["ProxyMode"] = config.ProxyMode,
            ["ProxyUrl"] = config.ProxyUrl,
            // These four are read per use, but they still have to be written here: the loop below
            // keeps every key the program does NOT know about and skips the ones it does, so a key
            // that is listed in KnownKeys but missing from this object would be dropped on every
            // save. That is exactly how "地图跟随 Train Sim World 6" came back as "X-Plane 12".
            ["TelemetrySource"] = config.TelemetrySource,
            ["TswApiUrl"] = config.TswApiUrl,
            ["TswApiKeyPath"] = config.TswApiKeyPath,
            ["TswPollHz"] = config.TswPollHz
        };
        if (existing is null) return root;

        var known = KnownKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var obsolete = ObsoleteKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in existing)
        {
            if (known.Contains(item.Key)) continue;
            if (cleanObsolete && obsolete.Contains(item.Key)) continue;
            root[item.Key] = item.Value?.DeepClone();
        }
        return root;
    }

    private static JsonObject? ParseRoot(string source, BridgeConfig? repairedConfig)
    {
        try
        {
            return JsonNode.Parse(source, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            }) as JsonObject;
        }
        catch (JsonException)
        {
            if (repairedConfig is not null && TryExtractCurrentConfig(source, out var validObject))
                return JsonNode.Parse(validObject) as JsonObject;
            return null;
        }
    }

    private static void Write(string path, JsonObject root, string? backupPath, DateTime? expectedWriteTimeUtc, long expectedLength)
    {
        var directory = DirectoryFor(path);
        Directory.CreateDirectory(directory);
        var json = root.ToJsonString(WriteOptions);
        // Fail fast rather than writing something unreadable.
        _ = JsonSerializer.Deserialize<BridgeConfig>(json, ReadOptions);
        var temp = Path.Combine(directory, $"{Path.GetFileName(path)}.{Environment.ProcessId}.tmp");
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        try
        {
            if (File.Exists(path))
            {
                if (expectedWriteTimeUtc is not null)
                {
                    var current = new FileInfo(path);
                    if (current.LastWriteTimeUtc != expectedWriteTimeUtc || current.Length != expectedLength)
                        throw new ConfigConflictException("配置文件在设置窗口打开期间被其它程序修改过。");
                }
                File.Replace(temp, path, backupPath, true);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static string BackupPath(string path, string kind)
    {
        var directory = DirectoryFor(path);
        var name = Path.GetFileName(path);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var suffix = attempt == 0 ? "" : $"-{attempt + 1}";
            var stamp = $"{DateTime.Now:yyyyMMdd-HHmmss}{suffix}";
            var candidate = Path.Combine(directory, kind.Length == 0 ? $"{name}.{stamp}.bak" : $"{name}.{kind}-{stamp}.bak");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(directory, $"{name}.{kind}-{Guid.NewGuid():N}.bak");
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
                var score = node.Any(item => string.Equals(item.Key, nameof(BridgeConfig.WebPort), StringComparison.OrdinalIgnoreCase)) ? 100 : 0;
                score += node.Any(item => string.Equals(item.Key, nameof(BridgeConfig.UdpPorts), StringComparison.OrdinalIgnoreCase)) ? 10 : 0;
                candidates.Add((candidate, score, order++));
            }
            catch (JsonException) { }
        }
        var selected = candidates.OrderByDescending(item => item.Score).ThenByDescending(item => item.Order).FirstOrDefault();
        json = selected.Json ?? "";
        return json.Length > 0;
    }
}
