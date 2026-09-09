namespace WinGit.Core;

/// <summary>The user-visible kind of a path change.</summary>
public enum ChangeKind
{
    Unknown,
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
    Untracked,
    Conflicted,
}

/// <summary>The kind of a line in a parsed diff.</summary>
public enum DiffLineKind
{
    Context,
    Added,
    Removed,
    HunkHeader,
    FileHeader,
    NoNewline,
    Binary,
}

/// <summary>A path reported by Git's index and working tree status.</summary>
public sealed record FileChange
{
    public FileChange(
        string path,
        ChangeKind kind = ChangeKind.Unknown,
        string indexStatus = "",
        string workTreeStatus = "",
        string? oldPath = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A changed path is required.", nameof(path));
        }

        Path = path;
        OldPath = oldPath;
        Kind = kind;
        IndexStatus = indexStatus;
        WorkTreeStatus = workTreeStatus;
    }

    public string Path { get; }

    public string? OldPath { get; }

    public ChangeKind Kind { get; }

    /// <summary>The one-character porcelain status for the index side.</summary>
    public string IndexStatus { get; }

    /// <summary>The one-character porcelain status for the work-tree side.</summary>
    public string WorkTreeStatus { get; }
}

/// <summary>A read-only snapshot of a repository's current status.</summary>
public sealed record RepositoryStatus
{
    public RepositoryStatus(
        string rootPath,
        string branch,
        string headId,
        IReadOnlyList<FileChange> changes)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("A repository root is required.", nameof(rootPath));
        }

        RootPath = rootPath;
        Branch = branch ?? string.Empty;
        HeadId = headId ?? string.Empty;
        Changes = new List<FileChange>(changes ?? throw new ArgumentNullException(nameof(changes))).AsReadOnly();
    }

    public string RootPath { get; }

    /// <summary>Empty when HEAD is detached or the repository has no commits.</summary>
    public string Branch { get; }

    /// <summary>Empty when the repository is unborn.</summary>
    public string HeadId { get; }

    public IReadOnlyList<FileChange> Changes { get; }

    public string Upstream { get; init; } = string.Empty;

    public int Ahead { get; init; }

    public int Behind { get; init; }

    public bool IsDetached { get; init; }

    public bool IsUnborn { get; init; }
}

/// <summary>The small history row needed by the native first slice.</summary>
public sealed record CommitSummary(
    string Id,
    string ShortId,
    string Summary,
    string Author,
    DateTimeOffset Date);

/// <summary>The subject and body of one commit message.</summary>
public sealed record CommitMessage(string Summary, string Description);

/// <summary>The Git identity that a local commit would use.</summary>
public sealed record CommitIdentity(string Name, string Email);

/// <summary>The Git configuration file targeted by a settings operation.</summary>
public enum GitConfigScope
{
    Local,
    Global,
}

/// <summary>The allowlisted Git settings exposed by the native settings boundary.</summary>
public enum GitConfigSetting
{
    UserName,
    UserEmail,
    DefaultBranch,
}

/// <summary>The selected-scope values read from Git configuration.</summary>
public sealed record GitConfigValues(
    string? UserName,
    string? UserEmail,
    string? DefaultBranch);

/// <summary>A local branch and the read-only tracking information Git reports for it.</summary>
public sealed record BranchSummary(
    string Name,
    string FullRef,
    bool IsCurrent,
    string UpstreamRemote,
    string Upstream,
    int Ahead,
    int Behind,
    string? WorktreePath);

/// <summary>How a dirty worktree is handled before switching branches.</summary>
public enum BranchCheckoutStrategy
{
    /// <summary>Keep changes in the worktree when Git permits the switch.</summary>
    BringChanges,

    /// <summary>Save changes in a named stash on the current branch.</summary>
    StashChanges,
}

/// <summary>
/// The repository state captured before a branch checkout dialog was shown.
/// Core revalidates every field inside the serialized mutation before changing Git.
/// </summary>
public sealed record BranchCheckoutContext(
    string RootPath,
    string SourceBranch,
    string SourceHeadId,
    string TargetBranch,
    string TargetTipId);

/// <summary>A registered Git worktree and its lock/prunable state.</summary>
public sealed record WorktreeSummary(
    string Path,
    string? Branch,
    string HeadId,
    bool IsLocked,
    bool IsPrunable);

/// <summary>A stash entry with its stable commit ID and current reflog reference.</summary>
public sealed record StashSummary(
    string Reference,
    string CommitId,
    string Summary,
    DateTimeOffset Date);

/// <summary>A configured remote with a presentation-safe fetch URL.</summary>
public sealed record RemoteSummary(string Name, string Url);

/// <summary>The Git operation marker currently present in a repository.</summary>
public enum GitOperationKind
{
    None,
    Merge,
    CherryPick,
    Revert,
    Rebase,
    Sequencer,
    Unknown,
}

/// <summary>The result of a merge lifecycle mutation.</summary>
public enum MergeOutcome
{
    Completed,
    AlreadyUpToDate,
    Conflicts,
    InProgress,
    Aborted,
}

/// <summary>Why a merge lifecycle operation was refused for the observed state.</summary>
public enum MergeOperationFailureReason
{
    NotInProgress,
    DifferentOperationInProgress,
    StaleHead,
    UnbornRepository,
    UnresolvedConflicts,
}

/// <summary>A bounded, read-only view of Git's current operation markers and conflicts.</summary>
public sealed record MergeOperationState
{
    public MergeOperationState(
        GitOperationKind operationKind,
        string currentHeadId,
        string branch,
        IReadOnlyList<string> mergeHeadIds,
        bool isSquash,
        IReadOnlyList<FileChange> unmergedPaths)
    {
        CurrentHeadId = currentHeadId ?? string.Empty;
        Branch = branch ?? string.Empty;
        MergeHeadIds = new List<string>(mergeHeadIds ?? throw new ArgumentNullException(nameof(mergeHeadIds))).AsReadOnly();
        UnmergedPaths = new List<FileChange>(unmergedPaths ?? throw new ArgumentNullException(nameof(unmergedPaths))).AsReadOnly();
        OperationKind = operationKind;
        IsSquash = isSquash;
    }

    public GitOperationKind OperationKind { get; }

    public string CurrentHeadId { get; }

    public string Branch { get; }

    /// <summary>One or more commit IDs from MERGE_HEAD for an in-progress merge.</summary>
    public IReadOnlyList<string> MergeHeadIds { get; }

    public bool IsSquash { get; }

    /// <summary>Porcelain status entries whose index contains unresolved stages.</summary>
    public IReadOnlyList<FileChange> UnmergedPaths { get; }

    public bool IsInProgress => OperationKind != GitOperationKind.None;

    public bool IsMergeInProgress => OperationKind == GitOperationKind.Merge;

    public bool HasUnresolvedConflicts => UnmergedPaths.Count > 0;
}

/// <summary>The typed result returned by merge, continue, and abort operations.</summary>
public sealed record MergeOperationResult(
    MergeOutcome Outcome,
    string HeadId,
    MergeOperationState State);

/// <summary>The result of a rebase lifecycle mutation.</summary>
public enum RebaseOutcome
{
    Completed,
    AlreadyUpToDate,
    Conflicts,
    InProgress,
    Skipped,
    Aborted,
}

/// <summary>Why a rebase lifecycle operation was refused for the observed state.</summary>
public enum RebaseOperationFailureReason
{
    NotInProgress,
    DifferentOperationInProgress,
    StaleHead,
    UnbornRepository,
    UnresolvedConflicts,
    UnstagedChanges,
}

/// <summary>A bounded view of rebase metadata, progress, and paths needing attention.</summary>
public sealed record RebaseOperationState
{
    public RebaseOperationState(
        GitOperationKind operationKind,
        string currentHeadId,
        string branch,
        string? originalBranchTip,
        string? baseBranchTip,
        int currentStep,
        int totalSteps,
        string? currentCommitId,
        IReadOnlyList<FileChange> unmergedPaths,
        IReadOnlyList<FileChange> unstagedPaths)
    {
        OperationKind = operationKind;
        CurrentHeadId = currentHeadId ?? string.Empty;
        Branch = branch ?? string.Empty;
        OriginalBranchTip = originalBranchTip;
        BaseBranchTip = baseBranchTip;
        CurrentStep = Math.Max(currentStep, 0);
        TotalSteps = Math.Max(totalSteps, 0);
        CurrentCommitId = currentCommitId;
        UnmergedPaths = new List<FileChange>(unmergedPaths ?? throw new ArgumentNullException(nameof(unmergedPaths))).AsReadOnly();
        UnstagedPaths = new List<FileChange>(unstagedPaths ?? throw new ArgumentNullException(nameof(unstagedPaths))).AsReadOnly();
    }

    public GitOperationKind OperationKind { get; }

    public string CurrentHeadId { get; }

    /// <summary>The branch being rewritten, or empty while HEAD is detached.</summary>
    public string Branch { get; }

    /// <summary>The branch tip saved before the rebase began.</summary>
    public string? OriginalBranchTip { get; }

    /// <summary>The onto/base commit selected for the rebase.</summary>
    public string? BaseBranchTip { get; }

    /// <summary>The one-based current patch position when Git reports it.</summary>
    public int CurrentStep { get; }

    public int TotalSteps { get; }

    /// <summary>The commit Git is currently trying to replay, when available.</summary>
    public string? CurrentCommitId { get; }

    /// <summary>Porcelain entries whose index contains unresolved conflict stages.</summary>
    public IReadOnlyList<FileChange> UnmergedPaths { get; }

    /// <summary>Tracked paths changed in the work tree but not staged for continue.</summary>
    public IReadOnlyList<FileChange> UnstagedPaths { get; }

    public bool IsInProgress => OperationKind == GitOperationKind.Rebase;

    public bool HasUnresolvedConflicts => UnmergedPaths.Count > 0;

    public bool HasUnstagedChanges => UnstagedPaths.Count > 0;
}

/// <summary>The typed result returned by rebase, continue, skip, and abort.</summary>
public sealed record RebaseOperationResult(
    RebaseOutcome Outcome,
    string HeadId,
    RebaseOperationState State);

/// <summary>Raised when a rebase lifecycle command cannot safely run for its state.</summary>
public sealed class RebaseOperationBlockedException : InvalidOperationException
{
    public RebaseOperationBlockedException(
        RebaseOperationFailureReason reason,
        RebaseOperationState state,
        string message)
        : base(message)
    {
        Reason = reason;
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    public RebaseOperationFailureReason Reason { get; }

    public RebaseOperationState State { get; }
}

/// <summary>The result of starting or advancing a cherry-pick sequence.</summary>
public enum CherryPickOutcome
{
    Completed,
    Conflicts,
    InProgress,
    Skipped,
    Aborted,
}

/// <summary>Why a cherry-pick lifecycle operation was refused for its observed state.</summary>
public enum CherryPickOperationFailureReason
{
    NotInProgress,
    DifferentOperationInProgress,
    StaleHead,
    UnbornRepository,
    EmptySelection,
    UnresolvedConflicts,
    UnstagedChanges,
}

/// <summary>A bounded view of Git's current cherry-pick marker and sequence progress.</summary>
public sealed record CherryPickOperationState
{
    public CherryPickOperationState(
        GitOperationKind operationKind,
        string currentHeadId,
        string branch,
        string? originalHeadId,
        int currentStep,
        int totalSteps,
        string? currentCommitId,
        IReadOnlyList<string> commitIds,
        IReadOnlyList<FileChange> unmergedPaths,
        IReadOnlyList<FileChange> unstagedPaths)
    {
        OperationKind = operationKind;
        CurrentHeadId = currentHeadId ?? string.Empty;
        Branch = branch ?? string.Empty;
        OriginalHeadId = originalHeadId;
        CurrentStep = Math.Max(currentStep, 0);
        TotalSteps = Math.Max(totalSteps, 0);
        CurrentCommitId = currentCommitId;
        CommitIds = new List<string>(commitIds ?? throw new ArgumentNullException(nameof(commitIds))).AsReadOnly();
        UnmergedPaths = new List<FileChange>(unmergedPaths ?? throw new ArgumentNullException(nameof(unmergedPaths))).AsReadOnly();
        UnstagedPaths = new List<FileChange>(unstagedPaths ?? throw new ArgumentNullException(nameof(unstagedPaths))).AsReadOnly();
    }

    public GitOperationKind OperationKind { get; }

    public string CurrentHeadId { get; }

    public string Branch { get; }

    /// <summary>The target branch tip saved before the sequence began.</summary>
    public string? OriginalHeadId { get; }

    /// <summary>The one-based sequence position Git is currently applying.</summary>
    public int CurrentStep { get; }

    public int TotalSteps { get; }

    /// <summary>The commit currently stopped for conflict resolution, when available.</summary>
    public string? CurrentCommitId { get; }

    /// <summary>All resolved commit IDs in the original sequence order.</summary>
    public IReadOnlyList<string> CommitIds { get; }

    public IReadOnlyList<FileChange> UnmergedPaths { get; }

    public IReadOnlyList<FileChange> UnstagedPaths { get; }

    public bool IsInProgress => OperationKind == GitOperationKind.CherryPick;

    public bool HasUnresolvedConflicts => UnmergedPaths.Count > 0;

    public bool HasUnstagedChanges => UnstagedPaths.Count > 0;
}

/// <summary>The typed result returned by cherry-pick, continue, skip, and abort.</summary>
public sealed record CherryPickOperationResult(
    CherryPickOutcome Outcome,
    string HeadId,
    CherryPickOperationState State);

/// <summary>Raised when a cherry-pick lifecycle command cannot safely run.</summary>
public sealed class CherryPickOperationBlockedException : InvalidOperationException
{
    public CherryPickOperationBlockedException(
        CherryPickOperationFailureReason reason,
        CherryPickOperationState state,
        string message)
        : base(message)
    {
        Reason = reason;
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    public CherryPickOperationFailureReason Reason { get; }

    public CherryPickOperationState State { get; }
}

/// <summary>The result of starting or advancing a revert sequence.</summary>
public enum RevertOutcome
{
    Completed,
    Conflicts,
    InProgress,
    Skipped,
    Aborted,
}

/// <summary>Why a revert lifecycle operation was refused for its observed state.</summary>
public enum RevertOperationFailureReason
{
    NotInProgress,
    DifferentOperationInProgress,
    StaleHead,
    UnbornRepository,
    EmptySelection,
    UnresolvedConflicts,
    UnstagedChanges,
}

/// <summary>A bounded view of Git's current revert marker and sequence progress.</summary>
public sealed record RevertOperationState
{
    public RevertOperationState(
        GitOperationKind operationKind,
        string currentHeadId,
        string branch,
        string? originalHeadId,
        int currentStep,
        int totalSteps,
        string? currentCommitId,
        IReadOnlyList<string> commitIds,
        IReadOnlyList<FileChange> unmergedPaths,
        IReadOnlyList<FileChange> unstagedPaths)
    {
        OperationKind = operationKind;
        CurrentHeadId = currentHeadId ?? string.Empty;
        Branch = branch ?? string.Empty;
        OriginalHeadId = originalHeadId;
        CurrentStep = Math.Max(currentStep, 0);
        TotalSteps = Math.Max(totalSteps, 0);
        CurrentCommitId = currentCommitId;
        CommitIds = new List<string>(commitIds ?? throw new ArgumentNullException(nameof(commitIds))).AsReadOnly();
        UnmergedPaths = new List<FileChange>(unmergedPaths ?? throw new ArgumentNullException(nameof(unmergedPaths))).AsReadOnly();
        UnstagedPaths = new List<FileChange>(unstagedPaths ?? throw new ArgumentNullException(nameof(unstagedPaths))).AsReadOnly();
    }

    public GitOperationKind OperationKind { get; }

    public string CurrentHeadId { get; }

    public string Branch { get; }

    /// <summary>The target branch tip saved before the sequence began.</summary>
    public string? OriginalHeadId { get; }

    public int CurrentStep { get; }

    public int TotalSteps { get; }

    /// <summary>The commit currently stopped for conflict resolution, when available.</summary>
    public string? CurrentCommitId { get; }

    /// <summary>All resolved commit IDs in the original sequence order.</summary>
    public IReadOnlyList<string> CommitIds { get; }

    public IReadOnlyList<FileChange> UnmergedPaths { get; }

    public IReadOnlyList<FileChange> UnstagedPaths { get; }

    public bool IsInProgress => OperationKind == GitOperationKind.Revert;

    public bool HasUnresolvedConflicts => UnmergedPaths.Count > 0;

    public bool HasUnstagedChanges => UnstagedPaths.Count > 0;
}

/// <summary>The typed result returned by revert, continue, skip, and abort.</summary>
public sealed record RevertOperationResult(
    RevertOutcome Outcome,
    string HeadId,
    RevertOperationState State);

/// <summary>Raised when a revert lifecycle command cannot safely run.</summary>
public sealed class RevertOperationBlockedException : InvalidOperationException
{
    public RevertOperationBlockedException(
        RevertOperationFailureReason reason,
        RevertOperationState state,
        string message)
        : base(message)
    {
        Reason = reason;
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    public RevertOperationFailureReason Reason { get; }

    public RevertOperationState State { get; }
}

/// <summary>The explicit Git reset modes exposed by the native repository API.</summary>
public enum GitResetMode
{
    Soft,
    Mixed,
    Hard,
}

/// <summary>Why a reset was refused for the repository state observed in its gate.</summary>
public enum ResetOperationFailureReason
{
    StaleHead,
    UnbornRepository,
    OperationInProgress,
    UnmergedChanges,
}

/// <summary>A bounded state snapshot used to explain why reset was blocked.</summary>
public sealed record ResetOperationState
{
    public ResetOperationState(
        GitOperationKind operationKind,
        string currentHeadId,
        string branch,
        bool isDetached,
        bool isUnborn,
        IReadOnlyList<FileChange> unmergedPaths)
    {
        OperationKind = operationKind;
        CurrentHeadId = currentHeadId ?? string.Empty;
        Branch = branch ?? string.Empty;
        IsDetached = isDetached;
        IsUnborn = isUnborn;
        UnmergedPaths = new List<FileChange>(unmergedPaths ?? throw new ArgumentNullException(nameof(unmergedPaths))).AsReadOnly();
    }

    public GitOperationKind OperationKind { get; }

    public string CurrentHeadId { get; }

    public string Branch { get; }

    public bool IsDetached { get; }

    public bool IsUnborn { get; }

    public IReadOnlyList<FileChange> UnmergedPaths { get; }

    public bool HasUnmergedChanges => UnmergedPaths.Count > 0;
}

/// <summary>The result of one explicit reset mutation.</summary>
public sealed record ResetOperationResult(
    GitResetMode Mode,
    string PreviousHeadId,
    string CurrentHeadId,
    string Branch,
    bool IsDetached);

/// <summary>Raised when reset cannot safely run for its guarded state.</summary>
public sealed class ResetOperationBlockedException : InvalidOperationException
{
    public ResetOperationBlockedException(
        ResetOperationFailureReason reason,
        ResetOperationState state,
        string message)
        : base(message)
    {
        Reason = reason;
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    public ResetOperationFailureReason Reason { get; }

    public ResetOperationState State { get; }
}

/// <summary>A bounded HEAD reflog row suitable for recovery selection.</summary>
public sealed record ReflogEntry(
    string CommitId,
    string Selector,
    DateTimeOffset Date,
    string Subject);

/// <summary>Raised when a merge lifecycle command cannot safely run for its observed state.</summary>
public sealed class MergeOperationBlockedException : InvalidOperationException
{
    public MergeOperationBlockedException(
        MergeOperationFailureReason reason,
        MergeOperationState state,
        string message)
        : base(message)
    {
        Reason = reason;
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    public MergeOperationFailureReason Reason { get; }

    public MergeOperationState State { get; }
}

/// <summary>A local tag with its ref object ID and peeled commit target.</summary>
public sealed record TagSummary(
    string Name,
    string ObjectId,
    string TargetId,
    bool IsAnnotated);

/// <summary>The repository state observed immediately before an undo mutation.</summary>
public sealed record UndoRepositoryState(
    string CurrentHeadId,
    string Branch,
    bool IsDetached,
    bool IsUnborn,
    bool HasWorkingChanges,
    bool HasIndexChanges,
    bool HasUnmergedChanges,
    bool HasInProgressOperation);

/// <summary>The prior commit metadata returned after a successful undo.</summary>
public sealed record UndoCommitResult(
    string CommitId,
    string? ParentCommitId,
    string Summary,
    string Description,
    string Author,
    DateTimeOffset Date,
    bool WasInitialCommit,
    UndoRepositoryState StateBefore);

public enum UndoCommitFailureReason
{
    StaleHead,
    DetachedHead,
    UnbornRepository,
    InProgressOperation,
    UnmergedChanges,
}

/// <summary>Raised when undo is unsafe for the repository state supplied by the caller.</summary>
public sealed class UndoCommitBlockedException : InvalidOperationException
{
    public UndoCommitBlockedException(
        UndoCommitFailureReason reason,
        UndoRepositoryState state,
        string message)
        : base(message)
    {
        Reason = reason;
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    public UndoCommitFailureReason Reason { get; }

    public UndoRepositoryState State { get; }
}

/// <summary>Raised when a tag changed after the UI read its guarded object ID.</summary>
public sealed class StaleTagException : InvalidOperationException
{
    public StaleTagException(string name, string expectedObjectId, string? actualObjectId)
        : base($"The tag '{name}' no longer identifies the expected object.")
    {
        Name = name;
        ExpectedObjectId = expectedObjectId;
        ActualObjectId = actualObjectId;
    }

    public string Name { get; }

    public string ExpectedObjectId { get; }

    public string? ActualObjectId { get; }
}

/// <summary>An opaque identity for the exact file diff used by a partial mutation.</summary>
public sealed record PartialDiffSnapshot
{
    public PartialDiffSnapshot(
        string rootPath,
        string path,
        string? oldPath,
        bool staged,
        string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("A repository root is required.", nameof(rootPath));
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A changed path is required.", nameof(path));
        }

        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            throw new ArgumentException("A diff fingerprint is required.", nameof(fingerprint));
        }

        RootPath = rootPath;
        Path = path;
        OldPath = oldPath;
        Staged = staged;
        Fingerprint = fingerprint;
    }

    public string RootPath { get; }

    public string Path { get; }

    public string? OldPath { get; }

    public bool Staged { get; }

    /// <summary>Opaque SHA-256 identity of the raw Git patch and file request.</summary>
    public string Fingerprint { get; }
}

/// <summary>A stable native identity for one selectable line in a partial diff.</summary>
public sealed record PartialDiffSelection(int HunkId, int? LineIndex)
{
    public static PartialDiffSelection ForHunk(int hunkId) => new(hunkId, null);

    public static PartialDiffSelection ForLine(int hunkId, int lineIndex) => new(hunkId, lineIndex);
}

/// <summary>A line in a partial diff, addressed by its hunk and zero-based line index.</summary>
public sealed record PartialDiffLine(
    int HunkId,
    int LineIndex,
    int? OldLineNumber,
    int? NewLineNumber,
    DiffLineKind Kind,
    string Text)
{
    /// <summary>Only additions and removals can be selected for a patch.</summary>
    public bool IsSelectable => Kind is DiffLineKind.Added or DiffLineKind.Removed;

    /// <summary>Original patch content, including a CR before LF for CRLF files.</summary>
    internal string PatchText { get; init; } = Text;
}

/// <summary>A hunk and its typed line identities for a partial staging request.</summary>
public sealed record PartialDiffHunk
{
    public PartialDiffHunk(
        int id,
        int oldStartLine,
        int oldLineCount,
        int newStartLine,
        int newLineCount,
        string header,
        IReadOnlyList<PartialDiffLine> lines)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            throw new ArgumentException("A hunk header is required.", nameof(header));
        }

        Id = id;
        OldStartLine = oldStartLine;
        OldLineCount = oldLineCount;
        NewStartLine = newStartLine;
        NewLineCount = newLineCount;
        Header = header;
        Lines = new List<PartialDiffLine>(lines ?? throw new ArgumentNullException(nameof(lines))).AsReadOnly();
    }

    public int Id { get; }

    public int OldStartLine { get; }

    public int OldLineCount { get; }

    public int NewStartLine { get; }

    public int NewLineCount { get; }

    public string Header { get; }

    public IReadOnlyList<PartialDiffLine> Lines { get; }
}

/// <summary>A bounded, typed patch snapshot that can be submitted for partial staging.</summary>
public sealed record PartialFileDiff
{
    public PartialFileDiff(
        FileChange file,
        PartialDiffSnapshot snapshot,
        IReadOnlyList<PartialDiffHunk> hunks,
        bool isBinary,
        bool isTruncated,
        string? message = null)
    {
        File = file ?? throw new ArgumentNullException(nameof(file));
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        Hunks = new List<PartialDiffHunk>(hunks ?? throw new ArgumentNullException(nameof(hunks))).AsReadOnly();
        IsBinary = isBinary;
        IsTruncated = isTruncated;
        Message = message;
        IsSupported = !isBinary && !isTruncated && message is null && Hunks.Count > 0;
    }

    public FileChange File { get; }

    public PartialDiffSnapshot Snapshot { get; }

    public IReadOnlyList<PartialDiffHunk> Hunks { get; }

    public bool IsBinary { get; }

    public bool IsTruncated { get; }

    public string? Message { get; }

    public bool IsSupported { get; }
}

/// <summary>Raised when a partial patch cannot safely be applied to the current file state.</summary>
public sealed class PartialStagingUnsupportedException : InvalidOperationException
{
    public PartialStagingUnsupportedException(string path, string message)
        : base($"Partial staging is unavailable for '{path}': {message}")
    {
        Path = path;
    }

    public string Path { get; }
}

/// <summary>Raised when a diff selection was created from a different file snapshot.</summary>
public sealed class StaleDiffSnapshotException : InvalidOperationException
{
    public StaleDiffSnapshotException(string path)
        : base($"The partial diff for '{path}' is stale; refresh it before applying the selection.")
    {
        Path = path;
    }

    public string Path { get; }
}

/// <summary>The shape of an unmerged path and its caller-visible resolution data.</summary>
public enum ConflictFileKind
{
    Text,
    DeleteModify,
    Binary,
    Unsupported,
}

/// <summary>The side which deleted a file in a delete-vs-modify conflict.</summary>
public enum ConflictDeletedSide
{
    Ours,
    Theirs,
}

/// <summary>An explicit action for a delete-vs-modify conflict.</summary>
public enum ConflictResolutionAction
{
    Keep,
    Delete,
}

/// <summary>One immutable conflict stage entry from Git's index.</summary>
public sealed record ConflictIndexStage(
    int StageNumber,
    string Mode,
    string BlobId);

/// <summary>A text conflict hunk with both sides and optional diff3 base content.</summary>
public sealed record ConflictHunk(
    int Index,
    string OursContent,
    string TheirsContent,
    string? BaseContent,
    string ContextBefore,
    string ContextAfter);

/// <summary>A caller-reviewed replacement for one indexed conflict hunk.</summary>
public sealed record ConflictHunkReplacement(
    int HunkIndex,
    string ResolvedContent);

/// <summary>Explicit, review-driven conflict application options.</summary>
public sealed class ConflictResolutionRequest
{
    public ConflictResolutionRequest(
        IReadOnlyList<ConflictHunkReplacement>? hunkReplacements = null,
        ConflictResolutionAction? deleteAction = null,
        bool stage = false)
    {
        HunkReplacements = new List<ConflictHunkReplacement>(
            hunkReplacements ?? Array.Empty<ConflictHunkReplacement>()).AsReadOnly();
        DeleteAction = deleteAction;
        Stage = stage;
    }

    public IReadOnlyList<ConflictHunkReplacement> HunkReplacements { get; }

    public ConflictResolutionAction? DeleteAction { get; }

    /// <summary>Stages the resolved path after writing it when explicitly requested.</summary>
    public bool Stage { get; }
}

/// <summary>A bounded immutable snapshot of one unmerged working-tree path.</summary>
public sealed class ConflictFileSnapshot
{
    private readonly byte[]? workingFileBytes;

    public ConflictFileSnapshot(
        string rootPath,
        string path,
        string expectedHeadId,
        IReadOnlyList<ConflictIndexStage> indexStages,
        byte[]? workingFileBytes,
        string? workingFileSha256,
        ConflictFileKind kind,
        IReadOnlyList<ConflictHunk> hunks,
        ConflictDeletedSide? deletedSide = null,
        bool hasUtf8Bom = false,
        string? unsupportedReason = null)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("A repository root is required.", nameof(rootPath));
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A conflicted path is required.", nameof(path));
        }

        if (string.IsNullOrWhiteSpace(expectedHeadId))
        {
            throw new ArgumentException("A current HEAD ID is required.", nameof(expectedHeadId));
        }

        RootPath = rootPath;
        Path = path;
        ExpectedHeadId = expectedHeadId;
        IndexStages = new List<ConflictIndexStage>(
            indexStages ?? throw new ArgumentNullException(nameof(indexStages))).AsReadOnly();
        this.workingFileBytes = workingFileBytes?.ToArray();
        WorkingFileSha256 = workingFileSha256;
        Kind = kind;
        Hunks = new List<ConflictHunk>(
            hunks ?? throw new ArgumentNullException(nameof(hunks))).AsReadOnly();
        DeletedSide = deletedSide;
        HasUtf8Bom = hasUtf8Bom;
        UnsupportedReason = unsupportedReason;
    }

    public string RootPath { get; }

    public string Path { get; }

    /// <summary>The immutable HEAD observed with the conflict stages.</summary>
    public string ExpectedHeadId { get; }

    public IReadOnlyList<ConflictIndexStage> IndexStages { get; }

    /// <summary>A defensive copy of the original working-file bytes, or null when absent.</summary>
    public byte[]? OriginalWorkingFileBytes => workingFileBytes?.ToArray();

    public string? WorkingFileSha256 { get; }

    public ConflictFileKind Kind { get; }

    public IReadOnlyList<ConflictHunk> Hunks { get; }

    public ConflictDeletedSide? DeletedSide { get; }

    public bool HasUtf8Bom { get; }

    public string? UnsupportedReason { get; }

    public bool IsSupported => UnsupportedReason is null;

    internal byte[]? GetWorkingFileBytesCopy() => workingFileBytes?.ToArray();
}

/// <summary>The result after an explicit conflict resolution write.</summary>
public sealed record ConflictResolutionResult(
    string RootPath,
    string Path,
    bool WasDeleted,
    bool WasStaged,
    string? WorkingFileSha256,
    IReadOnlyList<ConflictIndexStage> RemainingIndexStages);

/// <summary>Raised when a requested path is not a safely readable unmerged file.</summary>
public sealed class ConflictSnapshotException : InvalidOperationException
{
    public ConflictSnapshotException(string path, string message)
        : base($"Conflict snapshot unavailable for '{path}': {message}")
    {
        Path = path;
    }

    public string Path { get; }
}

/// <summary>Raised when the repository changed after a conflict snapshot was captured.</summary>
public sealed class ConflictFileSnapshotStaleException : InvalidOperationException
{
    public ConflictFileSnapshotStaleException(string path, string message)
        : base($"The conflict snapshot for '{path}' is stale: {message}")
    {
        Path = path;
    }

    public string Path { get; }
}

/// <summary>Raised when a conflict snapshot cannot be safely resolved in the native slice.</summary>
public sealed class ConflictResolutionUnsupportedException : InvalidOperationException
{
    public ConflictResolutionUnsupportedException(string path, string message)
        : base($"Conflict resolution is unavailable for '{path}': {message}")
    {
        Path = path;
    }

    public string Path { get; }
}

/// <summary>A line in a file diff, with old and new side line numbers.</summary>
public sealed record DiffLine(
    int? OldLineNumber,
    int? NewLineNumber,
    DiffLineKind Kind,
    string Text);

/// <summary>A bounded, renderable result from a diff request.</summary>
public sealed record FileDiff
{
    public FileDiff(
        IReadOnlyList<DiffLine> lines,
        bool isBinary,
        bool isTruncated,
        string? message = null,
        ImageComparison? imageComparison = null,
        SubmoduleComparison? submoduleComparison = null)
    {
        Lines = new List<DiffLine>(lines ?? throw new ArgumentNullException(nameof(lines))).AsReadOnly();
        IsBinary = isBinary;
        IsTruncated = isTruncated;
        Message = message;
        ImageComparison = imageComparison;
        SubmoduleComparison = submoduleComparison;
    }

    public IReadOnlyList<DiffLine> Lines { get; }

    public bool IsBinary { get; }

    public bool IsTruncated { get; }

    public string? Message { get; }

    /// <summary>Bounded raw image content for the before and after sides, when available.</summary>
    public ImageComparison? ImageComparison { get; }

    /// <summary>Typed before and after gitlink revisions, when this is a submodule diff.</summary>
    public SubmoduleComparison? SubmoduleComparison { get; }
}

/// <summary>A bounded, immutable image side captured for a diff comparison.</summary>
public sealed class ImageDiffContent
{
    private readonly byte[] bytes;

    public ImageDiffContent(
        string relativePath,
        string mediaType,
        string? objectId,
        byte[] bytes)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("An image path is required.", nameof(relativePath));
        }

        if (string.IsNullOrWhiteSpace(mediaType))
        {
            throw new ArgumentException("An image media type is required.", nameof(mediaType));
        }

        RelativePath = relativePath;
        MediaType = mediaType;
        ObjectId = objectId;
        this.bytes = (bytes ?? throw new ArgumentNullException(nameof(bytes))).ToArray();
    }

    public string RelativePath { get; }

    public string MediaType { get; }

    /// <summary>The immutable Git blob ID, or null for a working-tree-only side.</summary>
    public string? ObjectId { get; }

    public int ByteCount => bytes.Length;

    /// <summary>A read-only view over a defensive copy of the captured bytes.</summary>
    public ReadOnlyMemory<byte> Bytes => bytes;
}

/// <summary>A before/after image comparison captured without text decoding.</summary>
public sealed class ImageComparison
{
    public ImageComparison(
        ImageDiffContent? before,
        ImageDiffContent? after,
        bool isTruncated = false,
        string? message = null)
    {
        if (before is null && after is null && message is null)
        {
            throw new ArgumentException("An image comparison needs content or an explanatory message.");
        }

        Before = before;
        After = after;
        IsTruncated = isTruncated;
        Message = message;
    }

    public ImageDiffContent? Before { get; }

    public ImageDiffContent? After { get; }

    public bool IsTruncated { get; }

    public string? Message { get; }
}

/// <summary>A typed, immutable before and after comparison of one submodule gitlink.</summary>
public sealed class SubmoduleComparison
{
    public SubmoduleComparison(
        string? oldCommitId,
        string? newCommitId,
        bool oldIsDirty = false,
        bool newIsDirty = false)
    {
        if (oldCommitId is null && newCommitId is null)
        {
            throw new ArgumentException("A submodule comparison needs an old or new commit ID.");
        }

        if (oldCommitId is null && oldIsDirty)
        {
            throw new ArgumentException(
                "An added submodule cannot have a dirty old revision.",
                nameof(oldIsDirty));
        }

        if (newCommitId is null && newIsDirty)
        {
            throw new ArgumentException(
                "A removed submodule cannot have a dirty new revision.",
                nameof(newIsDirty));
        }

        OldCommitId = NormalizeCommitId(oldCommitId, nameof(oldCommitId));
        NewCommitId = NormalizeCommitId(newCommitId, nameof(newCommitId));
        OldIsDirty = oldIsDirty;
        NewIsDirty = newIsDirty;
    }

    public string? OldCommitId { get; }

    public string? NewCommitId { get; }

    /// <summary>Whether Git marked the old submodule worktree as dirty.</summary>
    public bool OldIsDirty { get; }

    /// <summary>Whether Git marked the new submodule worktree as dirty.</summary>
    public bool NewIsDirty { get; }

    public bool IsAdded => OldCommitId is null;

    public bool IsRemoved => NewCommitId is null;

    public bool HasRevisionChange =>
        OldCommitId is not null
        && NewCommitId is not null
        && !string.Equals(OldCommitId, NewCommitId, StringComparison.OrdinalIgnoreCase);

    public bool IsDirtyOnly =>
        !IsAdded
        && !IsRemoved
        && !HasRevisionChange
        && OldIsDirty != NewIsDirty;

    public bool HasDirtyChanges => OldIsDirty || NewIsDirty;

    private static string? NormalizeCommitId(string? commitId, string parameterName)
    {
        if (commitId is null)
        {
            return null;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(commitId, parameterName);
        if (commitId.Length is not (40 or 64)
            || !commitId.All(IsHexCharacter))
        {
            throw new ArgumentException(
                "Submodule commit IDs must be full 40- or 64-character hexadecimal IDs.",
                parameterName);
        }

        return commitId.ToLowerInvariant();
    }

    private static bool IsHexCharacter(char value) =>
        value is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F';
}

/// <summary>A non-zero exit from the owned Git process.</summary>
public sealed class GitCommandException : InvalidOperationException
{
    public GitCommandException(string message, int exitCode, string standardError)
        : base(message)
    {
        ExitCode = exitCode;
        StandardError = standardError;
    }

    public int ExitCode { get; }

    public string StandardError { get; }
}

/// <summary>Git produced more data than a bounded native read can safely retain.</summary>
public sealed class GitOutputLimitException : InvalidOperationException
{
    public GitOutputLimitException(string operation)
        : base($"Git {operation} output exceeded the native read limit.")
    {
        Operation = operation;
    }

    public string Operation { get; }
}
