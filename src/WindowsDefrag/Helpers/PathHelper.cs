namespace WindowsDefrag.Helpers;

internal static class PathHelper
{
    public static string LongPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + path.TrimStart('\\');
        return @"\\?\" + path;
    }

    public static bool IsExcluded(string path, IReadOnlyList<string> exclusions)
    {
        foreach (var raw in exclusions)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            try
            {
                if (path.StartsWith(raw, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                // ignore bad patterns
            }
        }

        return false;
    }
}
