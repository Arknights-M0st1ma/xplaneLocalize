using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XPlaneEfbBridge;

// SimBrief's public API only ever returns the pilot's most recent plan (the
// request_id / sequence_id query parameters are ignored, verified 2026-09-11),
// so every plan the bridge fetches is archived here and can be re-selected later
// without asking SimBrief again.
internal sealed class FlightPlanHistory
{
    private const int MaxEntries = 12;
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string path;
    private readonly string legacyCachePath;
    private JsonArray entries = [];
    private string? activeId;

    public FlightPlanHistory(string path, string legacyCachePath)
    {
        this.path = path;
        this.legacyCachePath = legacyCachePath;
        Load();
    }

    public int Count => entries.Count;
    public string? ActiveId => activeId;

    // Read-only lookups used by the settings window (never writes).
    public static int PeekCount(string path, string legacyCachePath)
    {
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject root && root["entries"] is JsonArray stored) return stored.Count;
            return File.Exists(legacyCachePath) ? 1 : 0;
        }
        catch { return 0; }
    }

    // Every accessor is scoped to the configured account: a plan that was
    // archived for a different SimBrief user is never returned.
    public JsonObject? ActivePlan(string owner) => Owned(owner) ? ActiveEntry()?["plan"] as JsonObject : null;
    public string ActiveText(string owner) => Owned(owner) ? ActiveEntry()?["ofpText"]?.GetValue<string>() ?? "" : "";

    public long? ActiveFetchedAt(string owner)
    {
        if (!Owned(owner)) return null;
        var value = ActiveEntry()?["fetchedAt"];
        return value is null ? null : value.GetValue<long>();
    }

    private bool Owned(string owner) => (string?)ActiveEntry()?["owner"] == owner;

    // Summary list for the picker in the EFB (only for the configured account).
    public JsonArray List(string owner)
    {
        var list = new JsonArray();
        foreach (var item in entries.OfType<JsonObject>())
        {
            if ((string?)item["owner"] != owner) continue;
            var plan = item["plan"] as JsonObject;
            list.Add(new JsonObject
            {
                ["id"] = (string?)item["id"],
                ["fetchedAt"] = item["fetchedAt"]?.DeepClone(),
                ["label"] = (string?)item["label"],
                ["origin"] = plan?["origin"]?["ident"]?.DeepClone(),
                ["destination"] = plan?["destination"]?["ident"]?.DeepClone(),
                ["aircraft"] = plan?["aircraft"]?.DeepClone(),
                ["distanceNm"] = plan?["distanceNm"]?.DeepClone(),
                ["waypointCount"] = (plan?["waypoints"] as JsonArray)?.Count ?? 0,
                ["active"] = (string?)item["id"] == activeId,
                ["hasOfp"] = (item["ofpText"]?.GetValue<string>()?.Length ?? 0) > 0
            });
        }
        return list;
    }

    public bool Select(string id, string owner)
    {
        if (!entries.OfType<JsonObject>().Any(item => (string?)item["id"] == id && (string?)item["owner"] == owner)) return false;
        activeId = id;
        Save();
        return true;
    }

    // Stores a freshly fetched plan. A repeated fetch of the same plan (same
    // request id) updates the entry instead of piling up duplicates.
    public string Add(string owner, JsonObject plan, string ofpText, long fetchedAt, string? requestId, string label)
    {
        var existing = requestId is { Length: > 0 }
            ? entries.OfType<JsonObject>().FirstOrDefault(item => (string?)item["requestId"] == requestId)
            : null;
        var id = existing is not null ? (string?)existing["id"] ?? Guid.NewGuid().ToString("N")[..12] : Guid.NewGuid().ToString("N")[..12];
        if (existing is not null) entries.Remove(existing);
        entries.Insert(0, new JsonObject
        {
            ["id"] = id,
            ["fetchedAt"] = fetchedAt,
            ["owner"] = owner,
            ["label"] = label,
            ["requestId"] = requestId ?? "",
            ["plan"] = plan.DeepClone(),
            ["ofpText"] = ofpText
        });
        while (entries.Count > MaxEntries) entries.RemoveAt(entries.Count - 1);
        activeId = id;
        Save();
        return id;
    }

    private JsonObject? ActiveEntry()
    {
        if (activeId is null) return null;
        return entries.OfType<JsonObject>().FirstOrDefault(item => (string?)item["id"] == activeId);
    }

    private void Load()
    {
        try
        {
            if (File.Exists(path))
            {
                var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                if (root?["entries"] is JsonArray stored) entries = stored;
                activeId = (string?)root?["activeId"];
                return;
            }
            // One time import of the single-plan cache used before history existed.
            if (!File.Exists(legacyCachePath)) return;
            var legacy = JsonNode.Parse(File.ReadAllText(legacyCachePath)) as JsonObject;
            if (legacy?["plan"] is not JsonObject legacyPlan) return;
            var owner = (string?)legacy["owner"] ?? "";
            var id = Guid.NewGuid().ToString("N")[..12];
            entries = [new JsonObject
            {
                ["id"] = id,
                ["fetchedAt"] = legacy["fetchedAt"]?.DeepClone() ?? 0,
                ["owner"] = owner,
                ["label"] = Label(legacyPlan),
                ["requestId"] = "",
                ["plan"] = legacyPlan.DeepClone(),
                ["ofpText"] = ""
            }];
            activeId = id;
            Save();
            try { File.Delete(legacyCachePath); } catch { }
        }
        catch
        {
            entries = [];
            activeId = null;
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var root = new JsonObject
            {
                ["version"] = 1,
                ["activeId"] = activeId,
                ["entries"] = entries.DeepClone()
            };
            var temp = path + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(WriteOptions), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null, true);
            else File.Move(temp, path);
        }
        catch { /* the archive is best effort, it must never break the bridge */ }
    }

    public static string Label(JsonObject plan)
    {
        var origin = plan["origin"]?["ident"]?.GetValue<string>();
        var destination = plan["destination"]?["ident"]?.GetValue<string>();
        var distance = plan["distanceNm"]?.GetValue<double?>();
        var name = origin is null && destination is null ? "未命名计划" : $"{origin ?? "?"} → {destination ?? "?"}";
        return distance is > 0 ? $"{name} · {Math.Round(distance.Value)} NM" : name;
    }
}
