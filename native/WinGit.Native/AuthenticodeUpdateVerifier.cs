using System.Diagnostics;
using WinGit.Core;

namespace WinGit.Native;

internal sealed class AuthenticodeUpdateVerifier : INativeUpdateSignatureVerifier
{
    public async Task<bool> IsValidAsync(string executablePath, string expectedSignerSubject, CancellationToken cancellationToken)
        => await RunVerifierAsync("verify-update-signature.ps1",
            ["-ExecutablePath", executablePath, "-ExpectedSignerSubject", expectedSignerSubject], cancellationToken);

    public async Task<bool> IsPackageValidAsync(string packageDirectory, string expectedSignerSubject, CancellationToken cancellationToken)
        => await RunVerifierAsync("verify-update-package.ps1",
            ["-PackageDirectory", packageDirectory, "-ExpectedSignerSubject", expectedSignerSubject], cancellationToken);

    private static async Task<bool> RunVerifierAsync(string scriptName, string[] arguments, CancellationToken cancellationToken)
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
        process.StartInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, scriptName));
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
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
