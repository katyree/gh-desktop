using Microsoft.UI.Xaml;

namespace WinGit.Native;

internal sealed class RepositoryChooserRow
{
    public RepositoryChooserRow(
        string path,
        string? currentRoot,
        string? alias = null)
    {
        Path = path;
        RepositoryName = GetDisplayName(path);
        Alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
        DisplayName = Alias ?? RepositoryName;
        IsMissing = !Directory.Exists(path);
        IsCurrent = !IsMissing && currentRoot is not null &&
            PathsEqual(path, currentRoot);
        StatusText = IsMissing
            ? "Unavailable"
            : IsCurrent
                ? "Current repository"
                : "Available";
        RemoveVisibility = IsMissing
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public string Path { get; }

    public string RepositoryName { get; }

    public string? Alias { get; }

    public string DisplayName { get; }

    public string StatusText { get; }

    public bool IsMissing { get; }

    public bool IsCurrent { get; }

    public Visibility RemoveVisibility { get; }

    public string AutomationName =>
        $"{DisplayName}, {Path}{(IsMissing ? ", unavailable repository" : string.Empty)}{(IsCurrent ? ", current repository" : string.Empty)}";

    private static string GetDisplayName(string path)
    {
        try
        {
            var normalized = NativeSettingsStore.NormalizeRepositoryPath(path);
            var name = new DirectoryInfo(normalized).Name;
            return string.IsNullOrWhiteSpace(name) ? normalized : name;
        }
        catch (Exception)
        {
            return path;
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                NativeSettingsStore.NormalizeRepositoryPath(left),
                NativeSettingsStore.NormalizeRepositoryPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
