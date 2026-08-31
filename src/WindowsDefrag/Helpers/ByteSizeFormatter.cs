namespace WindowsDefrag.Helpers;

public static class ByteSizeFormatter
{
    public static string Format(long bytes)
    {
        if (bytes < 0) bytes = 0;
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[unit]}"
            : $"{size:0.##} {units[unit]}";
    }
}
