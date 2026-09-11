using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XPlaneEfbBridge;

// Reads the airports out of the local X-Plane installation ("apt.dat").
//
// Format reference (verified 2026-09-11):
//   https://developer.x-plane.com/article/airport-data-apt-dat-12-00-file-format-specification/
//   1 / 16 / 17  airport / seaplane / heliport header (ident + name)
//   100          runway (both end coordinates and the width are on this line)
//   102          helipad
//   110 + 111-116  pavement (taxiway / ramp) polygon
//   120 + 111-116  linear feature (painted line or light string)
//   20           taxiway sign (its text carries the taxiway letters)
//   1300 (old 15) start-up location = gate / parking stand, name like "A8"
//   1301         metadata for the previous start-up location
//   1200 + 1201 + 1202/1205/1206  ATC taxi route network
//
// Only the airport the aircraft is currently at is parsed, and the result is
// cached, so a 100 MB apt.dat is never read in full for a single lookup.
internal sealed class AptDat
{
    private sealed record Airport(string Icao, string Name, string Path, int Line, double? Lat, double? Lon);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string root;
    private readonly string cachePath;
    private readonly object gate = new();
    private Dictionary<string, Airport> index = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, JsonObject> parsed = new(StringComparer.OrdinalIgnoreCase);
    private bool built;
    private string? error;

    public AptDat(string root, string cachePath)
    {
        this.root = root ?? "";
        this.cachePath = cachePath;
    }

    public bool Configured => root.Length > 0 && Directory.Exists(root);

    public JsonObject Status()
    {
        EnsureIndex();
        return new JsonObject
        {
            ["configured"] = Configured,
            ["root"] = root,
            ["airports"] = index.Count,
            ["error"] = error
        };
    }

    // Nearest airport to the aircraft, with its parsed ground layout.
    public JsonObject? Nearest(double latitude, double longitude, double maxNm)
    {
        if (!Configured) return null;
        EnsureIndex();
        Airport? best = null;
        var bestDistance = double.MaxValue;
        foreach (var airport in index.Values)
        {
            if (airport.Lat is not { } lat || airport.Lon is not { } lon) continue;
            var distance = DistanceNm(latitude, longitude, lat, lon);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = airport;
        }
        if (best is null || bestDistance > maxNm) return null;
        var data = ForAirport(best.Icao);
        if (data is null) return null;
        data["distanceNm"] = Math.Round(bestDistance, 2);
        return data;
    }

    public JsonObject? ForAirport(string icao)
    {
        if (!Configured || icao.Length == 0) return null;
        EnsureIndex();
        lock (gate)
        {
            if (parsed.TryGetValue(icao, out var cached)) return cached.DeepClone() as JsonObject;
            if (!index.TryGetValue(icao, out var airport)) return null;
            var data = ParseAirport(airport);
            parsed[icao] = data;
            return data.DeepClone() as JsonObject;
        }
    }

    // ---------------------------------------------------------------- index
    private void EnsureIndex()
    {
        lock (gate)
        {
            if (built) return;
            built = true;
            if (!Configured)
            {
                error = "未配置 X-Plane 12 安装目录";
                return;
            }
            try
            {
                var files = AptFiles();
                var signature = Signature(files);
                if (TryLoadCache(signature)) return;
                var map = new Dictionary<string, Airport>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in files) IndexFile(file, map);
                index = map;
                SaveCache(signature);
            }
            catch (Exception cause)
            {
                error = cause.Message;
            }
        }
    }

    // Default airport data plus every custom scenery package (custom scenery
    // overrides the default, so it is indexed last).
    private List<string> AptFiles()
    {
        var files = new List<string>();
        void AddIfExists(string candidate)
        {
            if (File.Exists(candidate)) files.Add(candidate);
        }

        foreach (var version in new[] { "X-Plane 12", "X-Plane 11" })
        {
            AddIfExists(Path.Combine(root, "Resources", "default scenery", "default apt dat", "Earth nav data", "apt.dat"));
        }
        var custom = Path.Combine(root, "Custom Scenery");
        if (Directory.Exists(custom))
        {
            foreach (var directory in Directory.EnumerateDirectories(custom))
            {
                AddIfExists(Path.Combine(directory, "Earth nav data", "apt.dat"));
            }
        }
        return files;
    }

    private static string Signature(IEnumerable<string> files)
    {
        var text = new StringBuilder();
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            text.Append(file).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
        }
        return text.ToString();
    }

    private static void IndexFile(string path, Dictionary<string, Airport> map)
    {
        var lineNumber = 0;
        double? pendingLat = null;
        double? pendingLon = null;
        string? pendingIcao = null;
        var pendingLine = 0;
        var pendingName = "";

        void Flush()
        {
            if (pendingIcao is null) return;
            map[pendingIcao] = new Airport(pendingIcao, pendingName, path, pendingLine, pendingLat, pendingLon);
        }

        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            if (line.Length == 0) continue;
            var code = Code(line);
            if (code is 1 or 16 or 17)
            {
                Flush();
                pendingIcao = IdentToken(line);
                pendingName = AirportName(NameOf(line), pendingIcao);
                pendingLine = lineNumber;
                pendingLat = null;
                pendingLon = null;
                continue;
            }
            // The first runway of the airport gives a good enough position for
            // "which airport is the aircraft at".
            if (code == 100 && pendingIcao is not null && pendingLat is null)
            {
                var ends = RunwayEnds(line);
                if (ends.Count > 0) { pendingLat = ends[0].Lat; pendingLon = ends[0].Lon; }
            }
        }
        Flush();
    }

    private JsonObject ParseAirport(Airport airport)
    {
        var runways = new JsonArray();
        var pavements = new JsonArray();
        var lines = new JsonArray();
        var signs = new JsonArray();
        var parking = new JsonArray();
        var routes = new JsonArray();

        JsonArray? current = null;
        string currentName = "";
        var currentSurface = 0.0;
        var currentStyle = 0;
        JsonObject? lastParking = null;
        // taxi route network
        var routeNodes = new Dictionary<int, (double Lat, double Lon)>();
        JsonArray? routePoints = null;

        var lineNumber = 0;
        var nodes = 0;
        foreach (var line in File.ReadLines(airport.Path))
        {
            lineNumber++;
            if (lineNumber < airport.Line) continue;
            if (lineNumber > airport.Line)
            {
                var code = Code(line);
                if (code is 1 or 16 or 17) break; // next airport

                if (code == 100)
                {
                    var ends = RunwayEnds(line);
                    if (ends.Count >= 2)
                    {
                        var widthM = Numbers(line).FirstOrDefault();
                        runways.Add(new JsonObject
                        {
                            ["ident"] = $"{ends[0].Ident}/{ends[1].Ident}",
                            ["a"] = new JsonArray(ends[0].Lat, ends[0].Lon),
                            ["b"] = new JsonArray(ends[1].Lat, ends[1].Lon),
                            ["widthM"] = Math.Round(widthM, 1)
                        });
                        nodes++;
                    }
                    current = null;
                    continue;
                }
                if (code is 110 or 120 or 130)
                {
                    var values = Numbers(line);
                    currentSurface = values.Count > 0 ? values[0] : 0;
                    currentName = NameOf(line);
                    currentStyle = 0;
                    current = code == 110 ? new JsonArray() : code == 120 ? new JsonArray() : null;
                    if (current is not null)
                    {
                        var entry = new JsonObject
                        {
                            ["name"] = currentName,
                            ["surface"] = code == 110 ? "pavement" : "line",
                            ["points"] = current
                        };
                        if (code == 110) pavements.Add(entry); else lines.Add(entry);
                    }
                    continue;
                }
                if (code is >= 111 and <= 116)
                {
                    var values = Numbers(line);
                    if (values.Count >= 2 && current is not null)
                    {
                        current.Add(new JsonArray(values[0], values[1]));
                        if (values.Count > 2) currentStyle = (int)values[2];
                        nodes++;
                    }
                    continue;
                }
                if (code == 20)
                {
                    var values = Numbers(line);
                    var text = SignText(line);
                    if (values.Count >= 3 && text.Length > 0)
                    {
                        signs.Add(new JsonObject
                        {
                            ["text"] = text,
                            ["lat"] = values[0],
                            ["lon"] = values[1],
                            ["heading"] = values[2]
                        });
                        nodes++;
                    }
                    continue;
                }
                if (code is 1300 or 15)
                {
                    var values = Numbers(line);
                    if (values.Count >= 2)
                    {
                        lastParking = new JsonObject
                        {
                            ["name"] = ParkingName(line),
                            ["lat"] = values[0],
                            ["lon"] = values[1],
                            ["heading"] = values.Count > 2 ? values[2] : 0
                        };
                        parking.Add(lastParking);
                        nodes++;
                    }
                    continue;
                }
                if (code == 1301)
                {
                    // "<width code> <operations> <airline codes...>"
                    if (lastParking is not null) lastParking["airlines"] = NameOf(line);
                    continue;
                }
                if (code == 1201)
                {
                    var values = Numbers(line);
                    if (values.Count >= 3) routeNodes[(int)values[0]] = (values[1], values[2]);
                    continue;
                }
                if (code is 1202 or 1205 or 1206)
                {
                    var values = Numbers(line);
                    if (values.Count >= 2 && routeNodes.TryGetValue((int)values[0], out var from) && routeNodes.TryGetValue((int)values[1], out var to))
                    {
                        routePoints ??= new JsonArray();
                        routePoints.Add(new JsonArray(from.Lat, from.Lon));
                        routePoints.Add(new JsonArray(to.Lat, to.Lon));
                        nodes++;
                    }
                    continue;
                }
                if (code == 1200 && routePoints is null)
                {
                    routePoints = new JsonArray();
                }
            }
            if (lineNumber == airport.Line) continue;
        }
        if (routePoints is { Count: > 0 }) routes.Add(new JsonObject { ["points"] = routePoints });

        return new JsonObject
        {
            ["icao"] = airport.Icao,
            ["name"] = airport.Name,
            ["runways"] = runways,
            ["pavements"] = pavements,
            ["lines"] = lines,
            ["signs"] = signs,
            ["parking"] = parking,
            ["routes"] = routes,
            ["points"] = nodes
        };
    }

    // ---------------------------------------------------------------- cache
    private bool TryLoadCache(string signature)
    {
        try
        {
            if (!File.Exists(cachePath)) return false;
            var root = JsonNode.Parse(File.ReadAllText(cachePath)) as JsonObject;
            if ((string?)root?["signature"] != signature) return false;
            var map = new Dictionary<string, Airport>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in root?["airports"] as JsonArray ?? [])
            {
                if (item is not JsonObject entry) continue;
                var icao = (string?)entry["icao"];
                var path = (string?)entry["path"];
                if (icao is null || path is null) continue;
                map[icao] = new Airport(icao, (string?)entry["name"] ?? "", path, (int?)entry["line"] ?? 0,
                    (double?)entry["lat"], (double?)entry["lon"]);
            }
            if (map.Count == 0) return false;
            index = map;
            return true;
        }
        catch { return false; }
    }

    private void SaveCache(string signature)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var list = new JsonArray();
            foreach (var airport in index.Values)
            {
                list.Add(new JsonObject
                {
                    ["icao"] = airport.Icao,
                    ["name"] = airport.Name,
                    ["path"] = airport.Path,
                    ["line"] = airport.Line,
                    ["lat"] = airport.Lat,
                    ["lon"] = airport.Lon
                });
            }
            File.WriteAllText(cachePath, new JsonObject { ["signature"] = signature, ["airports"] = list }.ToJsonString(Json), new UTF8Encoding(false));
        }
        catch { /* the index is rebuilt on the next start */ }
    }

    // ---------------------------------------------------------------- helpers
    private static int Code(string line)
    {
        var index = 0;
        while (index < line.Length && line[index] == ' ') index++;
        var start = index;
        while (index < line.Length && char.IsAsciiDigit(line[index])) index++;
        if (index == start) return -1;
        return int.TryParse(line.AsSpan(start, index - start), out var code) ? code : -1;
    }

    // Numeric fields of a record, without the leading row code.
    private static List<double> Numbers(string line) =>
        Text(line).Skip(1).Where(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            .Select(part => double.Parse(part, NumberStyles.Float, CultureInfo.InvariantCulture))
            .ToList();

    private static List<string> Text(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inText = false;
        foreach (var character in line)
        {
            if (character == '"')
            {
                inText = !inText;
                continue;
            }
            if (character == ' ' && !inText)
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(character);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    private static string NameOf(string line) =>
        string.Join(" ", Text(line).Skip(1).Where(part => !double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            .Replace("{", " ").Replace("}", " ").Trim();

    private static string AirportName(string name, string icao) =>
        icao.Length > 0 && name.StartsWith(icao, StringComparison.OrdinalIgnoreCase)
            ? name[icao.Length..].Trim()
            : name;

    // Runway ends are the ident followed by latitude and longitude, e.g.
    //   100 46.00 1 0 0.25 0 2 1 16L 47.53801700 -122.30746100 ... 34R 47.52919200 -122.30000000 ...
    private static List<(string Ident, double Lat, double Lon)> RunwayEnds(string line)
    {
        var tokens = Text(line);
        var ends = new List<(string, double, double)>();
        for (var index = 1; index + 2 < tokens.Count; index++)
        {
            if (!IsRunwayIdent(tokens[index])) continue;
            if (!double.TryParse(tokens[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) continue;
            if (!double.TryParse(tokens[index + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) continue;
            if (lat is < -90 or > 90 || lon is < -180 or > 180) continue;
            ends.Add((tokens[index], lat, lon));
        }
        return ends;
    }

    private static bool IsRunwayIdent(string token) =>
        token.Length is >= 2 and <= 4 && char.IsAsciiDigit(token[0]) && token.All(char.IsAsciiLetterOrDigit);

    // "1300 <lat> <lon> <heading> [type] <name>" — keep the name, drop the type word.
    private static string ParkingName(string line)
    {
        var name = NameOf(line);
        foreach (var prefix in new[] { "gate", "tie-down", "tiedown", "hangar", "misc", "ramp", "runway", "helipad" })
        {
            if (name.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase)) return name[(prefix.Length + 1)..].Trim();
        }
        return name;
    }

    private static List<string> Idents(string line) =>
        Text(line).Where(part => part.Length > 0 && part.All(char.IsAsciiLetterOrDigit) && !double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            .ToList();

    private static string IdentToken(string line) =>
        Text(line).Skip(1).FirstOrDefault(part => part.Length is 4 or 3 && part.All(char.IsAsciiLetterOrDigit) && part.Any(char.IsAsciiLetter)) ?? "";

    private static string SignText(string line)
    {
        var tokens = Text(line);
        var start = line.IndexOf('{');
        var text = start >= 0 ? line[start..] : tokens.Count > 0 ? tokens[^1] : "";
        return text.Replace("{@L}", " ").Replace("{@R}", " ").Replace("{@Y}", " ").Replace("{@B}", " ")
            .Replace("{", " ").Replace("}", " ").Replace("  ", " ").Trim();
    }

    private static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
    {
        const double radians = Math.PI / 180;
        var dLat = (lat2 - lat1) * radians;
        var dLon = (lon2 - lon1) * radians * Math.Cos((lat1 + lat2) * radians / 2);
        return Math.Sqrt(dLat * dLat + dLon * dLon) * 60;
    }
}
