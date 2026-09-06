using WinGit.Core;

namespace WinGit.Native;

internal sealed class SubmoduleRow
{
    public SubmoduleRow(SubmoduleSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    public SubmoduleSnapshot Snapshot { get; }

    public string Path => Snapshot.Path;

    public string StatusGlyph => !Snapshot.IsInitialized
        ? "○"
        : Snapshot.IsDirty
            ? "!"
            : IsAtExpectedCommit ? "✓" : "↗";

    public string StatusLabel => !Snapshot.IsInitialized
        ? Snapshot.IsDirty ? "Not initialized · Local files" : "Not initialized"
        : Snapshot.IsDirty
            ? "Local changes"
            : IsAtExpectedCommit
                ? "At index commit"
                : "Different checked-out commit";

    public string RevisionLabel => Snapshot.IsInitialized
        ? $"HEAD {ShortObjectId(Snapshot.CheckedOutHeadCommitId!)}  ·  index {ShortObjectId(Snapshot.ExpectedIndexCommitId)}"
        : $"Index {ShortObjectId(Snapshot.ExpectedIndexCommitId)}";

    public bool IsAtExpectedCommit =>
        Snapshot.IsInitialized
        && string.Equals(
            Snapshot.CheckedOutHeadCommitId,
            Snapshot.ExpectedIndexCommitId,
            StringComparison.OrdinalIgnoreCase);

    public string AutomationName =>
        $"{Path}; {StatusLabel}; expected index commit {ShortObjectId(Snapshot.ExpectedIndexCommitId)}; "
        + (Snapshot.CheckedOutHeadCommitId is null
            ? "not initialized"
            : $"current HEAD {ShortObjectId(Snapshot.CheckedOutHeadCommitId)}");

    private static string ShortObjectId(string value) =>
        value.Length > 12 ? value[..12] : value;
}
