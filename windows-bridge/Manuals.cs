using System.Text.Json.Nodes;

namespace XPlaneEfbBridge;

/// <summary>
/// The PDF manual library.
///
/// The iPad cannot read this machine's disk, so the bridge lists the folder and
/// streams the files back. Everything here validates its input: a path that
/// escapes the configured folder, or a file that is not a PDF, is refused.
/// </summary>
internal static class Manuals
{
    public const int MaxManuals = 600;
    private const int MaxDepth = 6;

    /// <summary>Resolves a client-supplied relative path inside the folder, or null.</summary>
    public static string? ResolveInside(string folder, string relative)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(relative)) return null;
        if (relative.Contains('\0')) return null;
        if (Path.IsPathRooted(relative) || relative.Length > 1 && relative[1] == ':') return null;
        string root;
        string target;
        try
        {
            root = Path.GetFullPath(folder);
            target = Path.GetFullPath(Path.Combine(root, relative));
        }
        catch
        {
            return null;
        }
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(target, root, StringComparison.OrdinalIgnoreCase)) return null;
        return target;
    }

    /// <summary>The absolute path of one manual, or null when it cannot be served.</summary>
    public static string? FilePath(string folder, string relative)
    {
        var target = ResolveInside(folder, relative);
        if (target is null) return null;
        if (!string.Equals(Path.GetExtension(target), ".pdf", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            if (!File.Exists(target)) return null;
            // A symlink pointing outside the folder must not become a way out.
            var real = Path.GetFullPath(new FileInfo(target).LinkTarget is null ? target : target);
            var root = Path.GetFullPath(folder);
            return real.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? real : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Lists the PDFs (sub-folders included) for the reader's file list.</summary>
    public static JsonObject List(string folder)
    {
        var payload = new JsonObject
        {
            ["configured"] = false,
            ["folder"] = folder ?? "",
            ["files"] = new JsonArray()
        };
        if (string.IsNullOrWhiteSpace(folder)) return payload;
        payload["folder"] = folder;
        if (!Directory.Exists(folder))
        {
            payload["error"] = "找不到手册文件夹";
            return payload;
        }
        payload["configured"] = true;
        var found = new List<(string Path, string Name, long Size, long Modified)>();
        void Walk(string directory, int depth)
        {
            if (depth > MaxDepth || found.Count > MaxManuals) return;
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch
            {
                return;                     // an unreadable sub-folder must not fail the listing
            }
            foreach (var entry in entries)
            {
                if (found.Count > MaxManuals) return;
                var name = Path.GetFileName(entry);
                if (name.StartsWith('.')) continue;
                try
                {
                    if (Directory.Exists(entry))
                    {
                        Walk(entry, depth + 1);
                        continue;
                    }
                    if (!string.Equals(Path.GetExtension(entry), ".pdf", StringComparison.OrdinalIgnoreCase)) continue;
                    var info = new FileInfo(entry);
                    if (!info.Exists) continue;
                    found.Add((Path.GetRelativePath(folder, entry).Replace('\\', '/'), Path.GetFileNameWithoutExtension(name), info.Length, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds()));
                }
                catch
                {
                    // Skip whatever we cannot stat.
                }
            }
        }
        Walk(folder, 0);
        found.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.CurrentCulture));
        var files = new JsonArray();
        foreach (var item in found.Take(MaxManuals))
        {
            files.Add(new JsonObject
            {
                ["path"] = item.Path,
                ["name"] = item.Name,
                ["size"] = item.Size,
                ["modifiedAt"] = item.Modified
            });
        }
        payload["files"] = files;
        payload["truncated"] = found.Count > MaxManuals;
        return payload;
    }
}
