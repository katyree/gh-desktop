namespace WinGit.Core;

/// <summary>Finds an installed Git executable without invoking a shell.</summary>
public static class GitExecutableLocator
{
    public static string Resolve(string? searchPath = null) =>
        FindOnPath(searchPath) ?? throw new FileNotFoundException(
            "Git for Windows was not found. Install Git for Windows and add it to PATH before starting WinGit.");

    public static string? FindOnPath(string? searchPath = null)
    {
        var path = searchPath ?? Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var rawDirectory in path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = rawDirectory.Trim().Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            try
            {
                var executable = Path.Combine(directory, "git.exe");
                if (File.Exists(executable))
                {
                    return Path.GetFullPath(executable);
                }
            }
            catch (ArgumentException)
            {
                // An invalid PATH entry should not hide a later Git installation.
            }
        }

        return null;
    }
}
