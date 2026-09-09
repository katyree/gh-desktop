using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

[assembly: InternalsVisibleTo("WinGit.Core.Tests")]

namespace WinGit.Core.Codex;

/// <summary>A file that the caller could not safely send for suggestions.</summary>
public sealed record CodexConflictSkippedFile(
    string Path,
    string Reason)
{
    public override string ToString() => "Codex conflict skipped file.";
}

/// <summary>The safe progress phases exposed to the native UI.</summary>
public enum CodexConflictSuggestionProgressPhase
{
    Generating,
    Validating,
}

/// <summary>Progress for the current conflict-suggestion run.</summary>
public sealed record CodexConflictSuggestionProgress(
    CodexConflictSuggestionProgressPhase Phase,
    int FilesResolved,
    int FilesTotal)
{
    public override string ToString() =>
        $"Codex conflict suggestion progress ({Phase}).";
}

/// <summary>
/// Caller-owned input for a multi-file conflict-suggestion run. Context.Files
/// contains only resolvable files; files already skipped by snapshot
/// collection are supplied through SkippedFiles.
/// </summary>
public sealed record CodexConflictSuggestionBatchRequest(
    CodexConflictSuggestionContext Context,
    CodexModelSelectionSnapshot? ModelSelection = null,
    IReadOnlyList<CodexConflictSkippedFile>? SkippedFiles = null)
{
    public override string ToString() =>
        "Codex conflict suggestion batch request.";
}

/// <summary>
/// Review-only results for one multi-file suggestion run. Reassembly and
/// applying any resolution remain caller-owned operations.
/// </summary>
public sealed record CodexConflictSuggestionBatchResult(
    IReadOnlyList<CodexConflictResolution> Suggestions,
    string? SummaryMarkdown,
    IReadOnlyList<CodexConflictSuggestionReference> References,
    IReadOnlyList<CodexConflictSkippedFile> SkippedFiles)
{
    public override string ToString() =>
        "Codex conflict suggestion batch result.";
}

/// <summary>
/// Orchestrates the source-compatible sequential conflict-suggestion chunks.
/// A failed later provider call can leave earlier safe results available for
/// review, while cancellation always aborts the whole run.
/// </summary>
public sealed class CodexConflictSuggestionOrchestrator
{
    private const int ConflictChunkSize = 20;
    private const string InvalidOutputReason =
        "ChatGPT did not return a safe, valid suggestion for this file.";

    private readonly Func<
        CodexConflictSuggestionGenerationRequest,
        CancellationToken,
        Task<CodexConflictSuggestionResult>> generateAsync;

    public CodexConflictSuggestionOrchestrator(
        CodexConflictSuggestionGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        generateAsync = generator.GenerateAsync;
    }

    internal CodexConflictSuggestionOrchestrator(
        Func<
            CodexConflictSuggestionGenerationRequest,
            CancellationToken,
            Task<CodexConflictSuggestionResult>> generateAsync)
    {
        this.generateAsync = generateAsync ??
            throw new ArgumentNullException(nameof(generateAsync));
    }

    /// <summary>
    /// Suggests resolutions in sequential chunks of at most 20 files. Files
    /// skipped by the caller are retained in the result. Invalid output skips
    /// only its chunk; auth, rate, timeout, and runtime failures preserve prior
    /// suggestions and mark the current and remaining files as interrupted.
    /// </summary>
    public async Task<CodexConflictSuggestionBatchResult> SuggestAsync(
        CodexConflictSuggestionBatchRequest request,
        Action<CodexConflictSuggestionProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfCancellationRequested(cancellationToken);

        var context = request.Context ??
            throw new ArgumentNullException(nameof(request.Context));
        var preSkippedFiles = request.SkippedFiles ??
            Array.Empty<CodexConflictSkippedFile>();
        foreach (var skippedFile in preSkippedFiles)
        {
            ArgumentNullException.ThrowIfNull(skippedFile);
        }

        var resolvableFiles = context.Files ??
            throw new ArgumentNullException(nameof(context.Files));
        var totalFiles = resolvableFiles.Count;
        var skippedFiles = new List<CodexConflictSkippedFile>(preSkippedFiles);
        var suggestions = new List<CodexConflictResolution>();
        var references = new List<CodexConflictSuggestionReference>();
        string? summaryMarkdown = null;
        var filesProcessed = 0;

        ReportProgress(
            onProgress,
            CodexConflictSuggestionProgressPhase.Generating,
            filesProcessed,
            totalFiles);

        if (totalFiles == 0)
        {
            ThrowIfCancellationRequested(cancellationToken);
            return CreateResult(
                suggestions,
                summaryMarkdown,
                references,
                skippedFiles);
        }

        var chunks = CreateChunks(resolvableFiles);
        for (var chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
        {
            ThrowIfCancellationRequested(cancellationToken);
            var files = chunks[chunkIndex];
            CodexConflictSuggestionResult chunkResult;
            try
            {
                chunkResult = await generateAsync(
                        new CodexConflictSuggestionGenerationRequest(
                            context with { Files = files },
                            request.ModelSelection),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CodexConflictSuggestionGenerationCancelledException)
            {
                throw;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw new CodexConflictSuggestionGenerationCancelledException();
            }
            catch (CodexConflictSuggestionGenerationException exception)
            {
                if (exception.Kind ==
                    CodexConflictSuggestionErrorKind.InvalidOutput)
                {
                    ReportProgress(
                        onProgress,
                        CodexConflictSuggestionProgressPhase.Validating,
                        filesProcessed,
                        totalFiles);
                    skippedFiles.AddRange(
                        files.Select(
                            file => new CodexConflictSkippedFile(
                                file.Path,
                                InvalidOutputReason)));
                    filesProcessed += files.Count;
                    ReportProgress(
                        onProgress,
                        CodexConflictSuggestionProgressPhase.Generating,
                        filesProcessed,
                        totalFiles);
                    continue;
                }

                if (suggestions.Count == 0)
                {
                    throw;
                }

                skippedFiles.AddRange(
                    chunks
                        .Skip(chunkIndex)
                        .SelectMany(chunk => chunk)
                        .Select(
                            file => new CodexConflictSkippedFile(
                                file.Path,
                                InterruptedReason(exception.Kind))));
                filesProcessed = totalFiles;
                ReportProgress(
                    onProgress,
                    CodexConflictSuggestionProgressPhase.Generating,
                    filesProcessed,
                    totalFiles);
                break;
            }
            catch
            {
                skippedFiles.AddRange(
                    files.Select(
                        file => new CodexConflictSkippedFile(
                            file.Path,
                            InvalidOutputReason)));
                filesProcessed += files.Count;
                ReportProgress(
                    onProgress,
                    CodexConflictSuggestionProgressPhase.Generating,
                    filesProcessed,
                    totalFiles);
                continue;
            }

            ReportProgress(
                onProgress,
                CodexConflictSuggestionProgressPhase.Validating,
                filesProcessed,
                totalFiles);
            suggestions.AddRange(chunkResult.Resolutions);
            summaryMarkdown ??= chunkResult.SummaryMarkdown;
            if (references.Count == 0)
            {
                references.AddRange(chunkResult.References);
            }

            filesProcessed += files.Count;
            ReportProgress(
                onProgress,
                CodexConflictSuggestionProgressPhase.Generating,
                filesProcessed,
                totalFiles);
        }

        ThrowIfCancellationRequested(cancellationToken);
        return CreateResult(
            suggestions,
            summaryMarkdown,
            references,
            skippedFiles);
    }

    private static IReadOnlyList<IReadOnlyList<CodexConflictFileContext>>
        CreateChunks(IReadOnlyList<CodexConflictFileContext> files)
    {
        if (files.Count <= ConflictChunkSize)
        {
            return [files.ToArray()];
        }

        var fileSymbols = files
            .Select(CreateSymbols)
            .ToArray();
        var parents = Enumerable.Range(0, files.Count).ToArray();

        int Find(int index)
        {
            while (parents[index] != index)
            {
                parents[index] = parents[parents[index]];
                index = parents[index];
            }

            return index;
        }

        void Union(int left, int right)
        {
            var leftParent = Find(left);
            var rightParent = Find(right);
            if (leftParent != rightParent)
            {
                parents[leftParent] = rightParent;
            }
        }

        for (var left = 0; left < fileSymbols.Length; left++)
        {
            for (var right = left + 1; right < fileSymbols.Length; right++)
            {
                var leftSymbols = fileSymbols[left];
                var rightSymbols = fileSymbols[right];
                var leftImportsRight = leftSymbols.ImportPaths.Any(
                    importPath =>
                        string.Equals(
                            GetImportBaseName(importPath),
                            rightSymbols.BaseName,
                            StringComparison.Ordinal));
                var rightImportsLeft = rightSymbols.ImportPaths.Any(
                    importPath =>
                        string.Equals(
                            GetImportBaseName(importPath),
                            leftSymbols.BaseName,
                            StringComparison.Ordinal));
                var sharedSymbols = leftSymbols.Exports.Any(
                        rightSymbols.References.Contains) ||
                    rightSymbols.Exports.Any(leftSymbols.References.Contains);
                if (leftImportsRight || rightImportsLeft || sharedSymbols)
                {
                    Union(left, right);
                }
            }
        }

        var groups = new Dictionary<int, List<CodexConflictFileContext>>();
        for (var index = 0; index < files.Count; index++)
        {
            var root = Find(index);
            if (!groups.TryGetValue(root, out var group))
            {
                group = [];
                groups.Add(root, group);
            }

            group.Add(files[index]);
        }

        var chunks = new List<IReadOnlyList<CodexConflictFileContext>>();
        var currentBin = new List<CodexConflictFileContext>();
        foreach (var group in groups.Values)
        {
            if (group.Count >= ConflictChunkSize)
            {
                if (currentBin.Count > 0)
                {
                    chunks.Add(currentBin.ToArray());
                    currentBin = [];
                }

                for (var offset = 0; offset < group.Count; offset += ConflictChunkSize)
                {
                    chunks.Add(
                        group
                            .Skip(offset)
                            .Take(ConflictChunkSize)
                            .ToArray());
                }
            }
            else
            {
                if (currentBin.Count + group.Count > ConflictChunkSize)
                {
                    if (currentBin.Count > 0)
                    {
                        chunks.Add(currentBin.ToArray());
                    }

                    currentBin = [.. group];
                }
                else
                {
                    currentBin.AddRange(group);
                }
            }
        }

        if (currentBin.Count > 0)
        {
            chunks.Add(currentBin.ToArray());
        }

        return chunks;
    }

    private static ConflictSymbols CreateSymbols(
        CodexConflictFileContext file)
    {
        var symbols = new ConflictSymbols(GetBaseName(file.Path));
        var textParts = new List<string>();
        foreach (var hunk in file.Hunks)
        {
            textParts.Add(hunk.OursContent);
            textParts.Add(hunk.TheirsContent);
            textParts.Add(hunk.ContextBefore);
            textParts.Add(hunk.ContextAfter);
            if (hunk.BaseContent is not null)
            {
                textParts.Add(hunk.BaseContent);
            }
        }

        var content = string.Join('\n', textParts);
        foreach (Match match in Regex.Matches(
                     content,
                     @"export\s+(?:function|const|let|class|interface|type|enum)\s+(\w+)",
                     RegexOptions.CultureInvariant))
        {
            symbols.Exports.Add(match.Groups[1].Value);
        }

        const string importPattern =
            @"import\s+(?:type\s+)?(?:(\*\s+as\s+\w+)|(\w+)\s*,\s*\{([^}]+)\}|\{([^}]+)\}|(\w+))\s+from\s+['""]([^'""]+)['""]";
        foreach (Match match in Regex.Matches(
                     content,
                     importPattern,
                     RegexOptions.CultureInvariant))
        {
            symbols.ImportPaths.Add(match.Groups[6].Value);
            var importedNames = new List<string>();
            if (match.Groups[1].Success)
            {
                importedNames.Add(
                    match.Groups[1].Value
                        .Replace("* as ", string.Empty, StringComparison.Ordinal)
                        .Trim());
            }
            else if (match.Groups[2].Success && match.Groups[3].Success)
            {
                importedNames.Add(match.Groups[2].Value);
                importedNames.AddRange(match.Groups[3].Value.Split(','));
            }
            else if (match.Groups[4].Success)
            {
                importedNames.AddRange(match.Groups[4].Value.Split(','));
            }
            else if (match.Groups[5].Success)
            {
                importedNames.Add(match.Groups[5].Value);
            }

            foreach (var importedName in importedNames)
            {
                var trimmed = importedName.Trim();
                if (trimmed.StartsWith("type ", StringComparison.Ordinal))
                {
                    trimmed = trimmed[5..];
                }

                var aliasSeparator = Regex.Match(
                    trimmed,
                    @"\s+as\s+",
                    RegexOptions.CultureInvariant);
                if (aliasSeparator.Success)
                {
                    trimmed = trimmed[..aliasSeparator.Index].Trim();
                }

                if (trimmed.Length > 0)
                {
                    symbols.References.Add(trimmed);
                }
            }
        }

        foreach (Match match in Regex.Matches(
                     content,
                     @"(?:extends|implements|instanceof|new|typeof)\s+(\w+)",
                     RegexOptions.CultureInvariant))
        {
            symbols.References.Add(match.Groups[1].Value);
        }

        return symbols;
    }

    private static string GetBaseName(string path)
    {
        var normalized = path.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        var fileName = normalized[(slash + 1)..];
        var extension = fileName.LastIndexOf('.');
        return extension >= 0 ? fileName[..extension] : fileName;
    }

    private static string GetImportBaseName(string importPath)
    {
        return GetBaseName(importPath);
    }

    private sealed class ConflictSymbols(string baseName)
    {
        public string BaseName { get; } = baseName;
        public HashSet<string> Exports { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ImportPaths { get; } = new(StringComparer.Ordinal);
        public HashSet<string> References { get; } = new(StringComparer.Ordinal);
    }

    private static CodexConflictSuggestionBatchResult CreateResult(
        List<CodexConflictResolution> suggestions,
        string? summaryMarkdown,
        List<CodexConflictSuggestionReference> references,
        List<CodexConflictSkippedFile> skippedFiles)
    {
        return new CodexConflictSuggestionBatchResult(
            suggestions.AsReadOnly(),
            summaryMarkdown,
            references.AsReadOnly(),
            skippedFiles.AsReadOnly());
    }

    private static string InterruptedReason(
        CodexConflictSuggestionErrorKind kind) =>
        kind switch
        {
            CodexConflictSuggestionErrorKind.AuthRequired =>
                "ChatGPT sign-in expired before this file could be analyzed.",
            CodexConflictSuggestionErrorKind.RateLimited =>
                "ChatGPT usage was exhausted before this file could be analyzed.",
            CodexConflictSuggestionErrorKind.Timeout =>
                "ChatGPT timed out before this file could be analyzed.",
            CodexConflictSuggestionErrorKind.RuntimeError =>
                "The ChatGPT runtime stopped before this file could be analyzed.",
            _ => InvalidOutputReason,
        };

    private static void ReportProgress(
        Action<CodexConflictSuggestionProgress>? onProgress,
        CodexConflictSuggestionProgressPhase phase,
        int filesResolved,
        int filesTotal)
    {
        onProgress?.Invoke(
            new CodexConflictSuggestionProgress(
                phase,
                filesResolved,
                filesTotal));
    }

    private static void ThrowIfCancellationRequested(
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new CodexConflictSuggestionGenerationCancelledException();
        }
    }
}
