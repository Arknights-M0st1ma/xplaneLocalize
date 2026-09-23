using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XPlaneEfbBridge;

/// <summary>
/// Flown-track recorder owned by the bridge.
///
/// The browser used to be the recorder: it appended a point for every telemetry
/// frame it happened to be awake for, so backgrounding Safari (or losing the
/// socket for a minute) punched a hole in the track. The PC is always running,
/// so it records instead, and the page only reads the result back.
///
/// Sampling keeps a point whenever the aircraft has moved 0.1 NM, never faster
/// than about 1.4 Hz, plus a 5 s heartbeat so a parked aircraft still shows up.
/// </summary>
internal sealed class TrackStore
{
    public const int MaxPoints = 40000;
    private const long MinGapMs = 700;
    private const double MinMoveNm = 0.1;
    private const long HeartbeatMs = 5000;
    private const double EarthRadiusNm = 3440.065;

    private readonly string savePath;
    private readonly object gate = new();
    private readonly List<Fix> points = [];
    private bool dirty;
    private long savedAt;

    private readonly record struct Fix(double Lat, double Lon, double Time);

    public TrackStore(string savePath) => this.savePath = savePath;

    public int Count { get { lock (gate) return points.Count; } }

    /// <summary>Reads the track left by the previous run. A corrupt file is ignored.</summary>
    public int Load()
    {
        try
        {
            if (!File.Exists(savePath)) return 0;
            var parsed = JsonNode.Parse(File.ReadAllText(savePath));
            if (parsed is not JsonArray array) return 0;
            var loaded = new List<Fix>();
            foreach (var entry in array)
            {
                if (entry is not JsonArray triple || triple.Count < 3) continue;
                var lat = triple[0]?.GetValue<double>() ?? double.NaN;
                var lon = triple[1]?.GetValue<double>() ?? double.NaN;
                var time = triple[2]?.GetValue<double>() ?? double.NaN;
                if (!double.IsFinite(lat) || !double.IsFinite(lon) || !double.IsFinite(time)) continue;
                loaded.Add(new Fix(lat, lon, time));
            }
            loaded.Sort((a, b) => a.Time.CompareTo(b.Time));
            lock (gate)
            {
                points.Clear();
                points.AddRange(loaded.Count > MaxPoints ? loaded.GetRange(loaded.Count - MaxPoints, MaxPoints) : loaded);
                dirty = false;
            }
            return loaded.Count;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Writes the track out; throttled unless <paramref name="force"/> is set.</summary>
    public bool Save(bool force = false)
    {
        Fix[] copy;
        lock (gate)
        {
            if (!dirty) return false;
            var now = Environment.TickCount64;
            if (!force && now - savedAt < 30000) return false;
            savedAt = now;
            dirty = false;
            copy = [.. points];
        }
        try
        {
            var directory = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var array = new JsonArray();
            foreach (var fix in copy)
            {
                array.Add(new JsonArray(
                    Math.Round(fix.Lat, 5),
                    Math.Round(fix.Lon, 5),
                    Math.Round(fix.Time)));
            }
            var temporary = savePath + ".tmp";
            File.WriteAllText(temporary, array.ToJsonString());
            File.Move(temporary, savePath, true);
            return true;
        }
        catch
        {
            lock (gate) dirty = true;
            return false;
        }
    }

    /// <summary>Appends a telemetry position. Returns true when a point was kept.</summary>
    public bool Record(double latitude, double longitude, double atMs)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude)) return false;
        if (!double.IsFinite(atMs)) atMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (latitude == 0 && longitude == 0) return false;
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180) return false;
        lock (gate)
        {
            if (points.Count > 0)
            {
                var last = points[^1];
                var elapsed = atMs - last.Time;
                if (elapsed < 0) return false;
                if (elapsed < MinGapMs) return false;
                if (elapsed < HeartbeatMs && NmBetween(last.Lat, last.Lon, latitude, longitude) < MinMoveNm) return false;
            }
            points.Add(new Fix(latitude, longitude, atMs));
            dirty = true;
            if (points.Count > MaxPoints) points.RemoveRange(0, points.Count - MaxPoints);
            return true;
        }
    }

    public int Clear()
    {
        int removed;
        lock (gate)
        {
            removed = points.Count;
            points.Clear();
            dirty = true;
            savedAt = 0;
        }
        Save(true);
        return removed;
    }

    /// <summary>
    /// The payload the page reads. <paramref name="since"/> makes it incremental,
    /// which is how a page that was suspended for ten minutes catches up in one
    /// request; <c>replaced</c> tells it to drop its copy instead of appending to
    /// a track that no longer lines up.
    /// </summary>
    public JsonObject Read(long? since)
    {
        const int digits = 5;
        lock (gate)
        {
            var first = points.Count > 0 ? points[0].Time : (double?)null;
            var incremental = since.HasValue && first.HasValue && since.Value >= first.Value;
            var payload = new JsonArray();
            foreach (var fix in points)
            {
                if (incremental && fix.Time <= since!.Value) continue;
                payload.Add(new JsonArray(
                    Math.Round(fix.Lat, digits),
                    Math.Round(fix.Lon, digits),
                    Math.Round(fix.Time)));
            }
            return new JsonObject
            {
                ["points"] = payload,
                ["replaced"] = !incremental,
                ["first"] = first,
                ["last"] = points.Count > 0 ? points[^1].Time : null,
                ["count"] = points.Count
            };
        }
    }

    // Great-circle distance. The page had this wrong for a long time (it converted
    // degrees to radians and then multiplied by 60 NM/degree, making every
    // distance 57x too small); keeping the units right here matters the same way.
    internal static double NmBetween(double lat1, double lon1, double lat2, double lon2)
    {
        var phi1 = lat1 * Math.PI / 180;
        var phi2 = lat2 * Math.PI / 180;
        var dPhi = phi2 - phi1;
        var dLambda = (lon2 - lon1) * Math.PI / 180;
        var h = Math.Sin(dPhi / 2) * Math.Sin(dPhi / 2)
            + Math.Cos(phi1) * Math.Cos(phi2) * Math.Sin(dLambda / 2) * Math.Sin(dLambda / 2);
        return 2 * EarthRadiusNm * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }
}
