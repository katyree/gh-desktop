using System.Diagnostics;
using WinGit.Core;

namespace WinGit.Native;

internal sealed class AuthenticodeUpdateVerifier : INativeUpdateSignatureVerifier
{
    public async Task<bool> IsValidAsync(string executablePath, string expectedSignerSubject, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "verify-update-signature.ps1"));
        process.StartInfo.ArgumentList.Add("-ExecutablePath");
        process.StartInfo.ArgumentList.Add(executablePath);
        process.StartInfo.ArgumentList.Add("-ExpectedSignerSubject");
        process.StartInfo.ArgumentList.Add(expectedSignerSubject);
        process.Start();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }
    }
}
