using System.IO;
using System.Net;
using System.Text;
using WindowsDefrag.Helpers;
using WindowsDefrag.Models;

namespace WindowsDefrag.Services;

public sealed class ReportService
{
    private readonly ConfigService _config;
    private readonly LogService _log;

    public ReportService(ConfigService config, LogService log)
    {
        _config = config;
        _log = log;
    }

    public string Write(DriveInfoModel drive, AnalysisResult analysis, JobResult? job = null)
    {
        var dir = _config.ReportsDirectory;
        var name = $"{drive.Letter}_{DateTime.Now:yyyyMMdd_HHmmss}.html";
        var path = Path.Combine(dir, name);

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\"/>");
        sb.AppendLine($"<title>{AppInfo.ProductName} report — {drive.Letter}:</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,sans-serif;margin:24px;color:#222}");
        sb.AppendLine("h1{font-size:20px}table{border-collapse:collapse;width:100%;margin:12px 0}");
        sb.AppendLine("th,td{border:1px solid #ccc;padding:6px 8px;text-align:left;font-size:13px}");
        sb.AppendLine("th{background:#f3f3f3}</style></head><body>");
        sb.AppendLine($"<h1>{WebUtility.HtmlEncode(AppInfo.ProductName)} — {drive.Letter}: {WebUtility.HtmlEncode(drive.Label)}</h1>");
        sb.AppendLine($"<p>Generated {DateTime.Now:yyyy-MM-dd HH:mm:ss} by {WebUtility.HtmlEncode(AppInfo.Company)}</p>");
        sb.AppendLine("<table>");
        Row(sb, "Media", drive.MediaText + (string.IsNullOrEmpty(drive.DiskModel) ? "" : " — " + drive.DiskModel));
        Row(sb, "File system", drive.FileSystem);
        Row(sb, "Size", ByteSizeFormatter.Format(drive.TotalSize));
        Row(sb, "Free", ByteSizeFormatter.Format(drive.FreeSpace));
        Row(sb, "Cluster size", ByteSizeFormatter.Format(analysis.ClusterSize));
        Row(sb, "Files", analysis.TotalFiles.ToString("N0"));
        Row(sb, "Directories", analysis.DirectoryCount.ToString("N0"));
        Row(sb, "Fragmented files", analysis.FragmentedFiles.ToString("N0"));
        Row(sb, "Fragments", analysis.TotalFragments.ToString("N0"));
        Row(sb, "Fragmentation", $"{analysis.FragmentationPercent:0.#}%");
        Row(sb, "Locked / skipped", $"{analysis.LockedFiles:N0} / {analysis.SkippedFiles:N0}");
        if (job is not null)
        {
            Row(sb, "Job", job.Message);
            Row(sb, "Files moved", job.FilesMoved.ToString("N0"));
        }
        sb.AppendLine("</table>");

        sb.AppendLine("<h2>Most fragmented files</h2><table><tr><th>Fragments</th><th>Size</th><th>File</th></tr>");
        foreach (var f in analysis.TopFragmented.Take(50))
        {
            sb.AppendLine($"<tr><td>{f.Fragments}</td><td>{WebUtility.HtmlEncode(ByteSizeFormatter.Format(f.Size))}</td><td>{WebUtility.HtmlEncode(f.Path)}</td></tr>");
        }
        sb.AppendLine("</table>");

        sb.AppendLine("<h2>Largest files</h2><table><tr><th>Size</th><th>Fragments</th><th>File</th></tr>");
        foreach (var f in analysis.Largest.Take(50))
        {
            sb.AppendLine($"<tr><td>{WebUtility.HtmlEncode(ByteSizeFormatter.Format(f.Size))}</td><td>{f.Fragments}</td><td>{WebUtility.HtmlEncode(f.Path)}</td></tr>");
        }
        sb.AppendLine("</table></body></html>");

        File.WriteAllText(path, sb.ToString());
        _log.Info("Report written: " + path);
        return path;
    }

    public IReadOnlyList<string> ListReports()
    {
        try
        {
            return Directory.GetFiles(_config.ReportsDirectory, "*.html")
                .OrderByDescending(File.GetCreationTimeUtc)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static void Row(StringBuilder sb, string key, string value) =>
        sb.AppendLine($"<tr><th>{WebUtility.HtmlEncode(key)}</th><td>{WebUtility.HtmlEncode(value)}</td></tr>");
}
