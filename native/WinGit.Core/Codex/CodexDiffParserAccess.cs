using System.Text;

namespace WinGit.Core;

/// <summary>
/// Exposes the existing bounded Git diff parser to Codex review validation.
/// The Codex boundary supplies text already captured by its caller; it does
/// not run Git or maintain a second diff grammar.
/// </summary>
public sealed partial class GitRepositoryService
{
    internal static FileDiff ParseDiffForCodexReview(string diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        return ParseDiff(Encoding.UTF8.GetBytes(diff), outputTruncated: false);
    }
}
