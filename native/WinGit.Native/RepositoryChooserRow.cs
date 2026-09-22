using Microsoft.UI.Xaml;

namespace WinGit.Native;

internal sealed class RepositoryChooserRow
{
    public RepositoryChooserRow(
        string path,
        string? currentRoot,
        string? alias = null,
        NativeRepositoryIndicatorSnapshot? indicator = null)
    {
        Path = path;
        RepositoryName = GetDisplayName(path);
        Alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
        DisplayName = Alias ?? RepositoryName;
        Indicator = indicator;
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

    public NativeRepositoryIndicatorSnapshot? Indicator { get; }

    public string StatusText { get; }

    public bool IsMissing { get; }

    public bool IsCurrent { get; }

    public Visibility RemoveVisibility { get; }

    public string ChangedFilesIndicatorText => Indicator is { ChangedFileCount: > 0 } snapshot
        ? $"{snapshot.ChangedFileCount} changed"
        : string.Empty;

    public string AheadBehindIndicatorText
    {
        get
        {
            if (Indicator is not { } snapshot
                || (snapshot.Ahead == 0 && snapshot.Behind == 0))
            {
                return string.Empty;
            }

            var parts = new List<string>(2);
            if (snapshot.Ahead > 0)
            {
                parts.Add($"↑{snapshot.Ahead}");
            }

            if (snapshot.Behind > 0)
            {
                parts.Add($"↓{snapshot.Behind}");
            }

            return string.Join(" ", parts);
        }
    }

    public Visibility IndicatorVisibility =>
        Indicator is { ChangedFileCount: > 0 }
        || Indicator is { Ahead: > 0 }
        || Indicator is { Behind: > 0 }
            ? Visibility.Visible
            : Visibility.Collapsed;

    public string IndicatorAutomationText
    {
        get
        {
            if (Indicator is not { } snapshot
                || (snapshot.ChangedFileCount == 0
                    && snapshot.Ahead == 0
                    && snapshot.Behind == 0))
            {
                return string.Empty;
            }

            var parts = new List<string>(3);
            if (snapshot.ChangedFileCount > 0)
            {
                parts.Add($"{snapshot.ChangedFileCount} changed file{(snapshot.ChangedFileCount == 1 ? string.Empty : "s")}");
            }

            if (snapshot.Ahead > 0)
            {
                parts.Add($"{snapshot.Ahead} commit{(snapshot.Ahead == 1 ? string.Empty : "s")} ahead");
            }

            if (snapshot.Behind > 0)
            {
                parts.Add($"{snapshot.Behind} commit{(snapshot.Behind == 1 ? string.Empty : "s")} behind");
            }

            return string.Join(", ", parts);
        }
    }

    public string AutomationName =>
        $"{DisplayName}, {Path}{(IsMissing ? ", unavailable repository" : string.Empty)}{(IsCurrent ? ", current repository" : string.Empty)}{(string.IsNullOrWhiteSpace(IndicatorAutomationText) ? string.Empty : $", {IndicatorAutomationText}")}";

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
