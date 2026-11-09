namespace RustProxy.Core.Storage;

/// <summary>Validates portable relative paths and rejects symbolic links in managed storage.</summary>
public static class PathSafety
{
    /// <summary>Returns a normalized portable path, optionally restricted to source resources.</summary>
    public static string Normalize(string? path, bool sourceOnly = false)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.StartsWith('/') || Path.IsPathRooted(path))
            throw new InvalidDataException("A relative resource path is required.");
        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/');
        foreach (var segment in segments)
        {
            var stem = segment.Split('.')[0];
            if (segment is "" or "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.Any(c => char.IsControl(c) || ":<>\"|?*".Contains(c)) ||
                stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '0' and <= '9')
                throw new InvalidDataException($"Unsafe resource path: {path}");
        }
        if (sourceOnly && (segments.Length < 2 || segments[0] is not ("cargo" or "rustup")))
            throw new InvalidDataException("Only cargo/ and rustup/ resources can be imported by the server.");
        return normalized;
    }

    /// <summary>Resolves a path beneath a trusted root without following existing links.</summary>
    public static string Resolve(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var normalized = Normalize(relative);
        RejectLink(fullRoot);
        var current = fullRoot;
        foreach (var segment in normalized.Split('/'))
        {
            current = Path.Combine(current, segment);
            RejectLink(current);
        }
        return current;
    }

    /// <summary>Rejects a symbolic link or Windows reparse point, including linked directories.</summary>
    public static void RejectLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Symbolic links are not allowed in managed storage: {path}");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
}
