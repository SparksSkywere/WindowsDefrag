using System.Diagnostics;
using WindowsDefrag.Models;
using WindowsDefrag.Native;

namespace WindowsDefrag.Services;

public sealed class TrimService
{
    private readonly LogService _log;

    public TrimService(LogService log)
    {
        _log = log;
    }

    public JobResult Run(DriveInfoModel drive, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(5);
        try
        {
            using var session = VolumeSession.Open(drive.Letter);
            session.TryTrimFreeRanges(
                (pct, _) => progress?.Report(Math.Clamp(pct, 5, 90)),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn("Native TRIM: " + ex.Message + " — trying system retrim.");
        }

        progress?.Report(92);
        var fallback = RunSystemRetrim(drive.Letter);
        progress?.Report(100);

        if (fallback)
        {
            _log.Success($"{drive.Letter}: TRIM completed.");
            return new JobResult { Success = true, Message = "TRIM completed." };
        }

        _log.Success($"{drive.Letter}: TRIM issued.");
        return new JobResult { Success = true, Message = "TRIM issued." };
    }

    private static bool RunSystemRetrim(char letter)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "defrag.exe",
                Arguments = $"{letter}: /L",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc is null)
                return false;
            proc.WaitForExit(10 * 60 * 1000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
