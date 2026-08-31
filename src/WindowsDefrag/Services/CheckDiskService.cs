using System.Diagnostics;

namespace WindowsDefrag.Services;

public sealed class CheckDiskService
{
    private readonly LogService _log;

    public CheckDiskService(LogService log)
    {
        _log = log;
    }

    public async Task<string> ScanAsync(char letter, bool fix, CancellationToken cancellationToken)
    {
        var args = fix ? $"{letter}: /F /scan" : $"{letter}: /scan";
        _log.Info("chkdsk " + args);

        var psi = new ProcessStartInfo
        {
            FileName = "chkdsk.exe",
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = Process.Start(psi);
        if (proc is null)
            return "Could not start chkdsk.";

        var output = await proc.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var err = await proc.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var text = (output + Environment.NewLine + err).Trim();
        if (proc.ExitCode == 0)
            _log.Success($"{letter}: check completed.");
        else
            _log.Warn($"{letter}: chkdsk exit {proc.ExitCode}.");
        return string.IsNullOrWhiteSpace(text) ? $"chkdsk exit {proc.ExitCode}." : text;
    }
}
