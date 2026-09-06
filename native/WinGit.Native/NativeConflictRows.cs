using System.ComponentModel;
using Microsoft.UI.Xaml;
using WinGit.Core;

namespace WinGit.Native;

/// <summary>Mutable presentation state for one immutable conflict hunk.</summary>
internal sealed class ConflictHunkRow : INotifyPropertyChanged
{
    public ConflictHunkRow(ConflictHunk hunk)
    {
        Hunk = hunk;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ConflictHunk Hunk { get; }

    public int Index => Hunk.Index;

    public string Title => $"Conflict {Hunk.Index + 1}";

    public string OursContent => DisplayContent(Hunk.OursContent);

    public string TheirsContent => DisplayContent(Hunk.TheirsContent);

    public string BaseContent => DisplayContent(Hunk.BaseContent);

    public string ContextBefore => Hunk.ContextBefore;

    public string ContextAfter => Hunk.ContextAfter;

    public Visibility ContextBeforeVisibility => string.IsNullOrEmpty(Hunk.ContextBefore)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility ContextAfterVisibility => string.IsNullOrEmpty(Hunk.ContextAfter)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility BaseVisibility => Hunk.BaseContent is null
        ? Visibility.Collapsed
        : Visibility.Visible;

    public string ResolvedContent { get; private set; } = string.Empty;

    public bool HasResolution { get; private set; }

    public void SetResolvedContent(string content, bool markResolved = true)
    {
        content ??= string.Empty;
        var changed = !string.Equals(ResolvedContent, content, StringComparison.Ordinal)
            || (markResolved && !HasResolution);
        ResolvedContent = content;
        if (markResolved)
        {
            HasResolution = true;
        }

        if (changed)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ResolvedContent)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasResolution)));
        }
    }

    private static string DisplayContent(string? content) =>
        string.IsNullOrEmpty(content) ? "(empty)" : content;
}
