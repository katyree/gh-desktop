using System.Collections.Immutable;
using System.Security;
using Microsoft.Win32;

namespace WinGit.Native;

internal enum NativeIntegrationKind
{
    Editor,
    Shell,
}

internal enum NativeShellKind
{
    CommandPrompt,
    PowerShell,
    PowerShellCore,
    Hyper,
    GitBash,
    Cygwin,
    Wsl,
    WindowsTerminal,
    FluentTerminal,
    Alacritty,
    Warp,
}

internal sealed record NativeIntegrationOption(
    string StableId,
    string DisplayName,
    string ExecutablePath,
    NativeIntegrationKind Kind,
    NativeShellKind? ShellKind = null);

internal sealed record NativeIntegrationDiagnostic(
    string StableId,
    string DisplayName,
    string Reason);

internal sealed record NativeIntegrationDiscoveryResult(
    ImmutableArray<NativeIntegrationOption> Options,
    ImmutableArray<NativeIntegrationDiagnostic> Unavailable)
{
    public ImmutableArray<NativeIntegrationOption> Editors =>
        Options.Where(option => option.Kind == NativeIntegrationKind.Editor)
            .ToImmutableArray();

    public ImmutableArray<NativeIntegrationOption> Shells =>
        Options.Where(option => option.Kind == NativeIntegrationKind.Shell)
            .ToImmutableArray();
}

/// <summary>
/// Resolves only the external tools that the Electron Windows implementation
/// knows how to launch. The result contains no arbitrary PATH lookup or shell
/// command text, so the settings UI can persist a stable identifier and use the
/// returned executable path without reparsing registry commands.
/// </summary>
internal static class NativeIntegrationDiscovery
{
    private const string UninstallSubKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private const string Wow64UninstallSubKey =
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    private const string AppPathsSubKey =
        @"Software\Microsoft\Windows\CurrentVersion\App Paths";

    private enum EditorLocation
    {
        InstallLocation,
        UninstallString,
        DisplayIcon,
    }

    private sealed record EditorDefinition(
        string StableId,
        string DisplayName,
        EditorLocation Location,
        ImmutableArray<string> DisplayNamePrefixes,
        ImmutableArray<string> Publishers,
        ImmutableArray<string> ExecutablePaths);

    private sealed record InstalledApplication(
        string HiveName,
        string RegistrySubKey,
        string DisplayName,
        string Publisher,
        string InstallLocation,
        string UninstallString,
        string DisplayIcon);

    private static readonly EditorDefinition[] EditorDefinitions =
    [
        CreateEditor(
            "editor.visual-studio-code",
            "Visual Studio Code",
            EditorLocation.InstallLocation,
            ["Microsoft Visual Studio Code"],
            ["Microsoft Corporation"],
            "code.exe"),
        CreateEditor(
            "editor.visual-studio-code-insiders",
            "Visual Studio Code (Insiders)",
            EditorLocation.InstallLocation,
            ["Microsoft Visual Studio Code Insiders"],
            ["Microsoft Corporation"],
            "Code - Insiders.exe"),
        CreateEditor(
            "editor.vscodium",
            "VSCodium",
            EditorLocation.InstallLocation,
            ["VSCodium"],
            ["VSCodium", "Microsoft Corporation"],
            "VSCodium.exe"),
        CreateEditor(
            "editor.vscodium-insiders",
            "VSCodium (Insiders)",
            EditorLocation.InstallLocation,
            ["VSCodium Insiders", "VSCodium (Insiders)"],
            ["VSCodium"],
            "VSCodium - Insiders.exe"),
        CreateEditor(
            "editor.sublime-text",
            "Sublime Text",
            EditorLocation.InstallLocation,
            ["Sublime Text"],
            ["Sublime HQ Pty Ltd"],
            "subl.exe"),
        CreateEditor(
            "editor.brackets",
            "Brackets",
            EditorLocation.InstallLocation,
            ["Brackets"],
            ["brackets.io"],
            "Brackets.exe"),
        CreateEditor(
            "editor.coldfusion-builder",
            "ColdFusion Builder",
            EditorLocation.InstallLocation,
            ["Adobe ColdFusion Builder"],
            ["Adobe Systems Incorporated"],
            "CFBuilder.exe"),
        CreateEditor(
            "editor.typora",
            "Typora",
            EditorLocation.InstallLocation,
            ["Typora"],
            ["typora.io"],
            "typora.exe"),
        CreateEditor(
            "editor.slickedit",
            "SlickEdit",
            EditorLocation.InstallLocation,
            ["SlickEdit"],
            ["SlickEdit Inc."],
            Path.Combine("win", "vs.exe")),
        CreateEditor(
            "editor.aptana-studio",
            "Aptana Studio 3",
            EditorLocation.InstallLocation,
            ["Aptana Studio"],
            ["Appcelerator"],
            "AptanaStudio3.exe"),
        CreateEditor(
            "editor.jetbrains-webstorm",
            "JetBrains Webstorm",
            EditorLocation.InstallLocation,
            ["WebStorm"],
            ["JetBrains s.r.o."],
            Path.Combine("bin", "webstorm64.exe"),
            Path.Combine("bin", "webstorm.exe")),
        CreateEditor(
            "editor.jetbrains-phpstorm",
            "JetBrains PhpStorm",
            EditorLocation.InstallLocation,
            ["PhpStorm"],
            ["JetBrains s.r.o."],
            Path.Combine("bin", "phpstorm64.exe"),
            Path.Combine("bin", "phpstorm.exe")),
        CreateEditor(
            "editor.android-studio",
            "Android Studio",
            EditorLocation.UninstallString,
            ["Android Studio"],
            ["Google LLC"],
            Path.Combine("..", "bin", "studio64.exe"),
            Path.Combine("..", "bin", "studio.exe")),
        CreateEditor(
            "editor.notepad-plus-plus",
            "Notepad++",
            EditorLocation.DisplayIcon,
            ["Notepad++"],
            ["Notepad++ Team"]),
        CreateEditor(
            "editor.jetbrains-rider",
            "JetBrains Rider",
            EditorLocation.InstallLocation,
            ["JetBrains Rider"],
            ["JetBrains s.r.o."],
            Path.Combine("bin", "rider64.exe"),
            Path.Combine("bin", "rider.exe")),
        CreateEditor(
            "editor.rstudio",
            "RStudio",
            EditorLocation.DisplayIcon,
            ["RStudio"],
            ["RStudio", "Posit Software"]),
        CreateEditor(
            "editor.jetbrains-intellij-idea",
            "JetBrains IntelliJ Idea",
            EditorLocation.InstallLocation,
            ["IntelliJ IDEA "],
            ["JetBrains s.r.o."],
            Path.Combine("bin", "idea64.exe"),
            Path.Combine("bin", "idea.exe")),
        CreateEditor(
            "editor.jetbrains-intellij-idea-community",
            "JetBrains IntelliJ Idea Community Edition",
            EditorLocation.InstallLocation,
            ["IntelliJ IDEA Community Edition "],
            ["JetBrains s.r.o."],
            Path.Combine("bin", "idea64.exe"),
            Path.Combine("bin", "idea.exe")),
        CreateEditor(
            "editor.jetbrains-pycharm",
            "JetBrains PyCharm",
            EditorLocation.InstallLocation,
            ["PyCharm "],
            ["JetBrains s.r.o."],
            Path.Combine("bin", "pycharm64.exe"),
            Path.Combine("bin", "pycharm.exe")),
        CreateEditor(
            "editor.jetbrains-pycharm-community",
            "JetBrains PyCharm Community Edition",
            EditorLocation.InstallLocation,
            ["PyCharm Community Edition"],
            ["JetBrains s.r.o."],
            Path.Combine("bin", "pycharm64.exe"),
            Path.Combine("bin", "pycharm.exe")),
        CreateEditor(
            "editor.jetbrains-clion",
            "JetBrains CLion",
            EditorLocation.InstallLocation,
            ["CLion "],
            ["JetBrains s.r.o."],
            Path.Combine("bin", "clion64.exe"),
            Path.Combine("bin", "clion.exe")),
        CreateEditor(
            "editor.jetbrains-rubymine",
            "JetBrains RubyMine",
            EditorLocation.InstallLocation,
            ["RubyMine "],
            ["JetBrains s.r.o."],
            Path.Combine("bin", "rubymine64.exe"),
            Path.Combine("bin", "rubymine.exe")),
        CreateEditor(
            "editor.jetbrains-goland",
            "JetBrains GoLand",
            EditorLocation.InstallLocation,
            ["GoLand "],
            ["JetBrains s.r.o."],
            Path.Combine("bin", "goland64.exe"),
            Path.Combine("bin", "goland.exe")),
        CreateEditor(
            "editor.jetbrains-fleet",
            "JetBrains Fleet",
            EditorLocation.DisplayIcon,
            ["Fleet "],
            ["JetBrains s.r.o."]),
        CreateEditor(
            "editor.jetbrains-dataspell",
            "JetBrains DataSpell",
            EditorLocation.InstallLocation,
            ["DataSpell "],
            ["JetBrains s.r.o."],
            Path.Combine("bin", "dataspell64.exe"),
            Path.Combine("bin", "dataspell.exe")),
        CreateEditor(
            "editor.jetbrains-rustrover",
            "JetBrains RustRover",
            EditorLocation.InstallLocation,
            ["RustRover "],
            ["JetBrains s.r.o."],
            Path.Combine("bin", "rustrover64.exe"),
            Path.Combine("bin", "rustrover.exe")),
        CreateEditor(
            "editor.pulsar",
            "Pulsar",
            EditorLocation.InstallLocation,
            ["Pulsar"],
            ["Pulsar-Edit"],
            Path.Combine("..", "pulsar", "Pulsar.exe")),
        CreateEditor(
            "editor.cursor",
            "Cursor",
            EditorLocation.DisplayIcon,
            ["Cursor", "Cursor (User)"],
            ["Cursor AI, Inc.", "Anysphere"]),
        CreateEditor(
            "editor.windsurf",
            "Windsurf",
            EditorLocation.DisplayIcon,
            ["Windsurf", "Windsurf (User)"],
            ["Codeium"]),
        CreateEditor(
            "editor.zed",
            "Zed",
            EditorLocation.DisplayIcon,
            ["Zed"],
            ["Zed Industries"]),
    ];

    public static NativeIntegrationDiscoveryResult DiscoverAll()
    {
        var editors = DiscoverEditors();
        var shells = DiscoverShells();
        return new NativeIntegrationDiscoveryResult(
            editors.Options.AddRange(shells.Options),
            editors.Unavailable.AddRange(shells.Unavailable));
    }

    public static NativeIntegrationDiscoveryResult DiscoverEditors()
    {
        var options = ImmutableArray.CreateBuilder<NativeIntegrationOption>();
        var unavailable = ImmutableArray.CreateBuilder<NativeIntegrationDiagnostic>();
        var applications = EnumerateInstalledApplications();

        foreach (var definition in EditorDefinitions)
        {
            var matchedEntry = false;
            var executablePath = string.Empty;

            foreach (var application in applications)
            {
                if (!Matches(definition, application))
                {
                    continue;
                }

                matchedEntry = true;
                foreach (var candidate in GetEditorExecutableCandidates(definition, application))
                {
                    if (TryGetExecutablePath(candidate, out executablePath))
                    {
                        break;
                    }
                }

                if (executablePath.Length > 0)
                {
                    break;
                }
            }

            if (executablePath.Length > 0)
            {
                options.Add(new NativeIntegrationOption(
                    definition.StableId,
                    definition.DisplayName,
                    executablePath,
                    NativeIntegrationKind.Editor));
            }
            else
            {
                unavailable.Add(new NativeIntegrationDiagnostic(
                    definition.StableId,
                    definition.DisplayName,
                    matchedEntry
                        ? "An installed entry matched, but its editor executable was not found."
                        : "No supported installed entry was found."));
            }
        }

        AddJetBrainsToolboxEditors(applications, options);
        return new NativeIntegrationDiscoveryResult(
            options.ToImmutable(),
            unavailable.ToImmutable());
    }

    public static NativeIntegrationDiscoveryResult DiscoverShells()
    {
        var options = ImmutableArray.CreateBuilder<NativeIntegrationOption>();
        var unavailable = ImmutableArray.CreateBuilder<NativeIntegrationDiagnostic>();

        AddShell(
            options,
            unavailable,
            "shell.command-prompt",
            "Command Prompt",
            NativeShellKind.CommandPrompt,
            FindCommandPrompt());
        AddShell(
            options,
            unavailable,
            "shell.powershell",
            "PowerShell",
            NativeShellKind.PowerShell,
            FindPowerShell());
        AddShell(
            options,
            unavailable,
            "shell.powershell-core",
            "PowerShell Core",
            NativeShellKind.PowerShellCore,
            FindAppPathExecutable("pwsh.exe"));
        AddShell(
            options,
            unavailable,
            "shell.hyper",
            "Hyper",
            NativeShellKind.Hyper,
            FindHyper());
        AddShell(
            options,
            unavailable,
            "shell.git-bash",
            "Git Bash",
            NativeShellKind.GitBash,
            FindGitBash());
        AddShell(
            options,
            unavailable,
            "shell.cygwin",
            "Cygwin",
            NativeShellKind.Cygwin,
            FindCygwin());
        AddShell(
            options,
            unavailable,
            "shell.wsl",
            "WSL",
            NativeShellKind.Wsl,
            FindWsl());
        AddShell(
            options,
            unavailable,
            "shell.windows-terminal",
            "Windows Terminal",
            NativeShellKind.WindowsTerminal,
            FindWindowsTerminal());
        AddShell(
            options,
            unavailable,
            "shell.fluent-terminal",
            "Fluent Terminal",
            NativeShellKind.FluentTerminal,
            FindFluentTerminal());
        AddShell(
            options,
            unavailable,
            "shell.alacritty",
            "Alacritty",
            NativeShellKind.Alacritty,
            FindAlacritty());
        AddShell(
            options,
            unavailable,
            "shell.warp",
            "Warp",
            NativeShellKind.Warp,
            FindWarp());

        return new NativeIntegrationDiscoveryResult(
            options.ToImmutable(),
            unavailable.ToImmutable());
    }

    private static EditorDefinition CreateEditor(
        string stableId,
        string displayName,
        EditorLocation location,
        string[] displayNamePrefixes,
        string[] publishers,
        params string[] executablePaths) =>
        new(
            stableId,
            displayName,
            location,
            displayNamePrefixes.ToImmutableArray(),
            publishers.ToImmutableArray(),
            executablePaths.ToImmutableArray());

    private static void AddShell(
        ImmutableArray<NativeIntegrationOption>.Builder options,
        ImmutableArray<NativeIntegrationDiagnostic>.Builder unavailable,
        string stableId,
        string displayName,
        NativeShellKind shellKind,
        string? executablePath)
    {
        if (TryGetExecutablePath(executablePath, out var normalizedPath))
        {
            options.Add(new NativeIntegrationOption(
                stableId,
                displayName,
                normalizedPath,
                NativeIntegrationKind.Shell,
                shellKind));
            return;
        }

        unavailable.Add(new NativeIntegrationDiagnostic(
            stableId,
            displayName,
            "No supported installed executable was found."));
    }

    private static bool Matches(
        EditorDefinition definition,
        InstalledApplication application) =>
        definition.DisplayNamePrefixes.Any(prefix =>
            application.DisplayName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        && definition.Publishers.Any(publisher =>
            string.Equals(application.Publisher, publisher, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> GetEditorExecutableCandidates(
        EditorDefinition definition,
        InstalledApplication application)
    {
        var installLocation = definition.Location switch
        {
            EditorLocation.InstallLocation => application.InstallLocation,
            EditorLocation.UninstallString => ExtractExecutablePath(application.UninstallString),
            EditorLocation.DisplayIcon => CleanDisplayIcon(application.DisplayIcon),
            _ => string.Empty,
        };

        if (string.IsNullOrWhiteSpace(installLocation))
        {
            yield break;
        }

        if (definition.Location == EditorLocation.DisplayIcon)
        {
            yield return installLocation;
            yield break;
        }

        foreach (var executablePath in definition.ExecutablePaths)
        {
            string candidate;
            try
            {
                candidate = Path.GetFullPath(Path.Combine(installLocation, executablePath));
            }
            catch (ArgumentException)
            {
                continue;
            }

            yield return candidate;
        }
    }

    private static void AddJetBrainsToolboxEditors(
        IReadOnlyList<InstalledApplication> applications,
        ImmutableArray<NativeIntegrationOption>.Builder options)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var application in applications)
        {
            if (!application.DisplayName.StartsWith(
                    "JetBrains Toolbox (",
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    application.Publisher,
                    "JetBrains s.r.o.",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var stableId = "editor.jetbrains-toolbox." + ToStableIdPart(application.DisplayName);
            if (!seen.Add(stableId))
            {
                continue;
            }

            var executablePath = CleanDisplayIcon(application.DisplayIcon);
            if (!TryGetExecutablePath(executablePath, out var normalizedPath))
            {
                continue;
            }

            options.Add(new NativeIntegrationOption(
                stableId,
                application.DisplayName,
                normalizedPath,
                NativeIntegrationKind.Editor));
        }
    }

    private static string ToStableIdPart(string value)
    {
        var characters = value
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-');
        var slug = new string(characters.ToArray()).Trim('-');
        return slug.Length == 0 ? "unknown" : slug;
    }

    private static IReadOnlyList<InstalledApplication> EnumerateInstalledApplications()
    {
        var applications = new List<InstalledApplication>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hives = new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine };
        var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };
        var uninstallPaths = new[] { UninstallSubKey, Wow64UninstallSubKey };

        foreach (var hive in hives)
        {
            foreach (var view in views)
            {
                foreach (var uninstallPath in uninstallPaths)
                {
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                        using var uninstallKey = baseKey.OpenSubKey(uninstallPath);
                        if (uninstallKey is null)
                        {
                            continue;
                        }

                        foreach (var subKeyName in uninstallKey.GetSubKeyNames().OrderBy(name => name))
                        {
                            try
                            {
                                using var applicationKey = uninstallKey.OpenSubKey(subKeyName);
                                if (applicationKey is null)
                                {
                                    continue;
                                }

                                var application = new InstalledApplication(
                                    hive.ToString(),
                                    subKeyName,
                                    GetRegistryString(applicationKey, "DisplayName"),
                                    GetRegistryString(applicationKey, "Publisher"),
                                    GetRegistryString(applicationKey, "InstallLocation"),
                                    GetRegistryString(applicationKey, "UninstallString"),
                                    GetRegistryString(applicationKey, "DisplayIcon"));
                                if (application.DisplayName.Length == 0
                                    || application.Publisher.Length == 0)
                                {
                                    continue;
                                }

                                var identity = string.Join(
                                    "\u001f",
                                    application.HiveName,
                                    application.RegistrySubKey,
                                    application.DisplayName,
                                    application.Publisher,
                                    application.InstallLocation,
                                    application.UninstallString,
                                    application.DisplayIcon);
                                if (seen.Add(identity))
                                {
                                    applications.Add(application);
                                }
                            }
                            catch (IOException)
                            {
                                // One unreadable uninstall entry must not hide other tools.
                            }
                            catch (UnauthorizedAccessException)
                            {
                                // One unreadable uninstall entry must not hide other tools.
                            }
                            catch (SecurityException)
                            {
                                // One unreadable uninstall entry must not hide other tools.
                            }
                        }
                    }
                    catch (IOException)
                    {
                        // Registry discovery is best effort and reports missing tools below.
                    }
                    catch (SecurityException)
                    {
                        // Registry discovery is best effort and reports missing tools below.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Registry discovery is best effort and reports missing tools below.
                    }
                }
            }
        }

        return applications;
    }

    private static string GetRegistryString(RegistryKey key, string valueName) =>
        key.GetValue(valueName)?.ToString()?.Trim() ?? string.Empty;

    private static string? FindCommandPrompt()
    {
        var comSpec = Environment.GetEnvironmentVariable("ComSpec");
        if (TryGetExecutablePath(comSpec, out var normalizedPath)
            && string.Equals(
                Path.GetFileName(normalizedPath),
                "cmd.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return normalizedPath;
        }

        var systemDirectory = Environment.SystemDirectory;
        return TryGetExecutablePath(
            Path.Combine(systemDirectory, "cmd.exe"),
            out normalizedPath)
            ? normalizedPath
            : null;
    }

    private static string? FindPowerShell()
    {
        var registryPath = FindAppPathExecutable("PowerShell.exe");
        if (registryPath is not null)
        {
            return registryPath;
        }

        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (string.IsNullOrWhiteSpace(systemRoot))
        {
            systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        }

        var fallbackPath = Path.Combine(
            systemRoot,
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        return TryGetExecutablePath(fallbackPath, out var normalizedPath)
            ? normalizedPath
            : null;
    }

    private static string? FindAppPathExecutable(string executableName)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var appPathKey = baseKey.OpenSubKey(
                    Path.Combine(AppPathsSubKey, executableName));
                var value = appPathKey?.GetValue(string.Empty)?.ToString();
                if (TryGetExecutablePath(value, out var normalizedPath))
                {
                    return normalizedPath;
                }
            }
            catch (IOException)
            {
                // Try the other registry view.
            }
            catch (SecurityException)
            {
                // Try the other registry view.
            }
            catch (UnauthorizedAccessException)
            {
                // Try the other registry view.
            }
        }

        return null;
    }

    private static string? FindHyper()
    {
        var command = ReadRegistryValue(
            RegistryHive.CurrentUser,
            RegistryView.Default,
            @"Software\Classes\Directory\Background\shell\Hyper\command",
            string.Empty);
        var commandPath = ExtractExecutablePath(command);
        if (TryGetExecutablePath(commandPath, out var normalizedPath)
            && string.Equals(
                Path.GetFileName(normalizedPath),
                "Hyper.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return normalizedPath;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return TryGetExecutablePath(
            Path.Combine(localAppData, "hyper", "Hyper.exe"),
            out normalizedPath)
            ? normalizedPath
            : null;
    }

    private static string? FindGitBash()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            var installPath = ReadRegistryValue(
                RegistryHive.LocalMachine,
                view,
                @"SOFTWARE\GitForWindows",
                "InstallPath");
            if (string.IsNullOrWhiteSpace(installPath))
            {
                continue;
            }

            if (TryGetExecutablePath(
                    Path.Combine(installPath, "git-bash.exe"),
                    out var normalizedPath))
            {
                return normalizedPath;
            }
        }

        return null;
    }

    private static string? FindCygwin()
    {
        var registryPaths = new[]
        {
            @"SOFTWARE\Cygwin\setup",
            @"SOFTWARE\WOW6432Node\Cygwin\setup",
        };
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            foreach (var registryPath in registryPaths)
            {
                var rootDirectory = ReadRegistryValue(
                    RegistryHive.LocalMachine,
                    view,
                    registryPath,
                    "rootdir");
                if (string.IsNullOrWhiteSpace(rootDirectory))
                {
                    continue;
                }

                if (TryGetExecutablePath(
                        Path.Combine(rootDirectory, "bin", "mintty.exe"),
                        out var normalizedPath))
                {
                    return normalizedPath;
                }
            }
        }

        return null;
    }

    private static string? FindWsl()
    {
        var systemDirectory = Environment.SystemDirectory;
        var wslPath = Path.Combine(systemDirectory, "wsl.exe");
        var wslConfigPath = Path.Combine(systemDirectory, "wslconfig.exe");
        return TryGetExecutablePath(wslPath, out var normalizedPath)
            && TryGetExecutablePath(wslConfigPath, out _)
            ? normalizedPath
            : null;
    }

    private static string? FindWindowsTerminal()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = Path.Combine(localAppData, "Microsoft", "WindowsApps", "wt.exe");
        return TryGetExecutablePath(path, out var normalizedPath)
            ? normalizedPath
            : null;
    }

    private static string? FindFluentTerminal()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = Path.Combine(localAppData, "Microsoft", "WindowsApps", "flute.exe");
        return TryGetExecutablePath(path, out var normalizedPath)
            ? normalizedPath
            : null;
    }

    private static string? FindAlacritty()
    {
        var iconPath = ReadRegistryValue(
            RegistryHive.ClassesRoot,
            RegistryView.Default,
            @"Directory\Background\shell\Open Alacritty here",
            "Icon");
        return TryGetExecutablePath(CleanDisplayIcon(iconPath), out var normalizedPath)
            ? normalizedPath
            : null;
    }

    private static string? FindWarp()
    {
        var configuredPath = ReadRegistryValue(
            RegistryHive.CurrentUser,
            RegistryView.Default,
            @"Software\Warp.dev\Warp",
            "InstallationPath");
        foreach (var candidate in new[]
        {
            configuredPath,
            Path.Combine(configuredPath ?? string.Empty, "warp.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "warp",
                "Warp",
                "warp.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Warp",
                "warp.exe"),
            Path.Combine(
                Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? string.Empty,
                "Warp",
                "warp.exe"),
        })
        {
            if (TryGetExecutablePath(candidate, out var normalizedPath))
            {
                return normalizedPath;
            }
        }

        return null;
    }

    private static string? ReadRegistryValue(
        RegistryHive hive,
        RegistryView view,
        string subKey,
        string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey);
            return key?.GetValue(valueName)?.ToString()?.Trim();
        }
        catch (IOException)
        {
            return null;
        }
        catch (SecurityException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string CleanDisplayIcon(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var first = value.Split(',', 2)[0].Trim();
        if (first.Length >= 2 && first[0] == '"' && first[^1] == '"')
        {
            first = first[1..^1];
        }

        return first.Trim();
    }

    private static string ExtractExecutablePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed[0] == '"')
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            return closingQuote > 1 ? trimmed[1..closingQuote] : string.Empty;
        }

        foreach (var extension in new[] { ".exe", ".com" })
        {
            var extensionIndex = trimmed.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            if (extensionIndex >= 0)
            {
                return trimmed[..(extensionIndex + extension.Length)];
            }
        }

        return trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    }

    private static bool TryGetExecutablePath(string? path, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var trimmed = path.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(trimmed))
            {
                return false;
            }

            var candidate = Path.GetFullPath(trimmed);
            var extension = Path.GetExtension(candidate);
            if (!string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(extension, ".com", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!File.Exists(candidate))
            {
                return false;
            }

            normalizedPath = candidate;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (SecurityException)
        {
            return false;
        }
    }
}
