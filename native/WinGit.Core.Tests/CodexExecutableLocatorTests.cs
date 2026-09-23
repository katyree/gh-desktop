using System.Runtime.InteropServices;
using WinGit.Core.Codex;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class CodexExecutableLocatorTests
{
    [Fact]
    public void FindsNativeExecutableBehindInstalledNpmShim()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var architecture = RuntimeInformation.ProcessArchitecture;
        if (architecture is not (Architecture.X64 or Architecture.Arm64))
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"wingit-codex-locator-{Guid.NewGuid():N}");
        var packageName = architecture == Architecture.Arm64 ? "codex-win32-arm64" : "codex-win32-x64";
        var targetTriple = architecture == Architecture.Arm64
            ? "aarch64-pc-windows-msvc"
            : "x86_64-pc-windows-msvc";
        var executable = Path.Combine(directory, "node_modules", "@openai", "codex",
            "node_modules", "@openai", packageName, "vendor", targetTriple, "bin", "codex.exe");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllText(Path.Combine(directory, "codex.cmd"), "test shim");
            File.WriteAllText(executable, "test executable");

            Assert.Equal(executable, CodexExecutableLocator.FindOnPath(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
