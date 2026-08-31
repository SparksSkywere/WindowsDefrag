using WindowsDefrag.Helpers;
using WindowsDefrag.Models;
using WindowsDefrag.Services;

namespace WindowsDefrag.Cli;

public static class CliHost
{
    public static bool ShouldRunCli(string[] args)
    {
        if (args.Length == 0) return false;

        var set = new HashSet<string>(args, StringComparer.OrdinalIgnoreCase);
        return set.Contains("--no-ui") ||
               set.Contains("--list") ||
               set.Contains("--analyze") ||
               set.Contains("--defrag") ||
               set.Contains("--trim") ||
               set.Contains("--help") ||
               set.Contains("-h") ||
               set.Contains("/?");
    }

    public static async Task<int> RunAsync(
        string[] args,
        DriveInventoryService inventory,
        AnalysisService analysis,
        DefragEngine engine,
        TrimService trim,
        ProtectionService protection,
        ConfigService config,
        ElevationService elevation,
        LogService log)
    {
        var set = new HashSet<string>(args, StringComparer.OrdinalIgnoreCase);
        if (set.Contains("--help") || set.Contains("-h") || set.Contains("/?"))
        {
            PrintHelp();
            return 0;
        }

        try
        {
            var drives = await inventory.QueryAsync(config.Config.General.ShowRemovable).ConfigureAwait(false);

            if (set.Contains("--list"))
            {
                foreach (var d in drives)
                {
                    Console.WriteLine(
                        $"{d.Letter}:  {d.Label,-16} {d.MediaText,-6} {d.FileSystem,-6} " +
                        $"{ByteSizeFormatter.Format(d.TotalSize),10}  free {ByteSizeFormatter.Format(d.FreeSpace),10}" +
                        (d.IsDirty ? "  DIRTY" : "") +
                        (d.TrimSupported ? "  TRIM" : ""));
                }

                return 0;
            }

            var letters = ParseLetters(args, drives);
            if (letters.Count == 0)
            {
                Console.Error.WriteLine("No matching volumes. Pass a letter (C) or ALL.");
                return 1;
            }

            var method = DefragMethodInfo.Parse(ArgValue(args, "--method") ?? config.Config.General.DefaultMethod);

            foreach (var drive in letters)
            {
                if (set.Contains("--analyze") || set.Contains("--defrag"))
                {
                    Console.WriteLine($"Analysing {drive.Letter}: …");
                    var result = analysis.Analyze(drive, new Progress<AnalysisProgress>(p =>
                    {
                        if (p.Percent % 10 == 0)
                            Console.WriteLine($"  {p.Percent}%  {p.FilesScanned:N0} files  {p.FragmentedFiles:N0} fragmented");
                    }), CancellationToken.None);
                    Console.WriteLine(
                        $"  {result.FragmentedFiles:N0} fragmented / {result.TotalFiles:N0} files  ({result.FragmentationPercent:0.#}%)");

                    if (set.Contains("--defrag"))
                    {
                        var (ok, reason) = protection.CanDefrag(drive, elevation.IsElevated);
                        if (!ok)
                        {
                            if (drive.IsSolidState)
                            {
                                Console.WriteLine($"  Skip defrag ({reason}) — TRIM instead.");
                                trim.Run(drive, new Progress<int>(p => { }), CancellationToken.None);
                                continue;
                            }

                            Console.Error.WriteLine("  " + reason);
                            continue;
                        }

                        Console.WriteLine($"  Defragment {DefragMethodInfo.DisplayName(method)}…");
                        var job = engine.Run(drive, method, result, new Progress<DefragProgress>(p =>
                        {
                            if (p.Percent % 10 == 0)
                                Console.WriteLine($"  {p.Percent}%  {p.Status}");
                        }), CancellationToken.None);
                        Console.WriteLine("  " + job.Message);
                    }
                }

                if (set.Contains("--trim"))
                {
                    var (ok, reason) = protection.CanTrim(drive, elevation.IsElevated);
                    if (!ok)
                    {
                        Console.Error.WriteLine($"  {drive.Letter}: {reason}");
                        continue;
                    }

                    Console.WriteLine($"TRIM {drive.Letter}: …");
                    var job = trim.Run(drive, new Progress<int>(p => { }), CancellationToken.None);
                    Console.WriteLine("  " + job.Message);
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            log.Error(ex.Message);
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static List<DriveInfoModel> ParseLetters(string[] args, List<DriveInfoModel> drives)
    {
        string? token = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--analyze", StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals("--defrag", StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals("--trim", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                    token = args[i + 1];
            }
        }

        if (string.IsNullOrWhiteSpace(token) || token.Equals("ALL", StringComparison.OrdinalIgnoreCase))
            return drives;

        var wanted = new HashSet<char>(
            token.Replace(":", "").Replace(",", "").Replace(" ", "")
                .Select(char.ToUpperInvariant));
        return drives.Where(d => wanted.Contains(d.Letter)).ToList();
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"""
            {AppInfo.ProductName} {AppInfo.Version}
            {AppInfo.Description}

            Usage:
              WindowsDefrag.exe
              WindowsDefrag.exe --list --no-ui
              WindowsDefrag.exe --analyze C --no-ui
              WindowsDefrag.exe --defrag C --method SPACE --no-ui
              WindowsDefrag.exe --trim C --no-ui
              WindowsDefrag.exe --help

            Options:
              --list --no-ui           Print volumes
              --analyze <letter|ALL>   Analyse fragmentation
              --defrag <letter|ALL>    Defragment (SSD volumes TRIM instead)
              --trim <letter|ALL>      TRIM / retrim
              --method NAME            STEALTH, SPACE, COMPLETE/Name,
                                       COMPLETE/Modified, COMPLETE/Access
              --no-ui                  Console mode
              --no-elevate             Do not prompt for administrator elevation
            """);
    }
}
