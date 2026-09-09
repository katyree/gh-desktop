using WinGit.Core.Codex;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class CodexConflictSuggestionOrchestratorTests
{
    [Fact]
    public async Task SuggestsSequentialTwentyFileChunksAndPreservesEarlierResults()
    {
        var files = CreateFiles(21);
        var calls = new List<int>();
        var firstPaths = new List<string>();
        var progress = new List<CodexConflictSuggestionProgress>();
        var request = new CodexConflictSuggestionBatchRequest(
            new CodexConflictSuggestionContext(
                "main",
                "feature",
                files,
                [new CodexConflictPullRequestContext(42, "Incoming value", "")],
                [],
                []),
            new CodexModelSelectionSnapshot("gpt-test", "high"),
            [new CodexConflictSkippedFile(
                "generated.lock",
                "The file was skipped before suggestion generation.")]);

        var orchestrator = new CodexConflictSuggestionOrchestrator(
            (generationRequest, _) =>
            {
                calls.Add(generationRequest.Context.Files.Count);
                firstPaths.Add(generationRequest.Context.Files[0].Path);
                if (calls.Count == 2)
                {
                    throw new CodexConflictSuggestionGenerationException(
                        CodexConflictSuggestionErrorKind.RateLimited);
                }

                Assert.Equal("gpt-test", generationRequest.ModelSelection?.Model);
                Assert.Equal(
                    "high",
                    generationRequest.ModelSelection?.ReasoningEffort);
                return Task.FromResult(
                    CreateResult(generationRequest.Context.Files, "first summary"));
            });

        var result = await orchestrator.SuggestAsync(
            request,
            progress.Add);

        Assert.Equal([19, 2], calls);
        Assert.Equal(["file-000.ts", "file-019.ts"], firstPaths);
        Assert.Equal(21, calls.Sum());
        Assert.Equal(19, result.Suggestions.Count);
        Assert.Equal("first summary", result.SummaryMarkdown);
        Assert.Single(result.References);
        Assert.Equal("42", result.References[0].Id);
        Assert.Equal(3, result.SkippedFiles.Count);
        Assert.Equal("generated.lock", result.SkippedFiles[0].Path);
        Assert.Equal(
            "The file was skipped before suggestion generation.",
            result.SkippedFiles[0].Reason);
        Assert.Equal("file-019.ts", result.SkippedFiles[1].Path);
        Assert.Equal(
            "ChatGPT usage was exhausted before this file could be analyzed.",
            result.SkippedFiles[1].Reason);
        Assert.Equal("file-020.ts", result.SkippedFiles[^1].Path);
        Assert.Equal(
            "ChatGPT usage was exhausted before this file could be analyzed.",
            result.SkippedFiles[^1].Reason);
        Assert.Equal(4, progress.Count);
        Assert.Equal(
            new CodexConflictSuggestionProgress(
                CodexConflictSuggestionProgressPhase.Generating,
                0,
                21),
            progress[0]);
        Assert.Equal(
            new CodexConflictSuggestionProgress(
                CodexConflictSuggestionProgressPhase.Validating,
                0,
                21),
            progress[1]);
        Assert.Equal(
            new CodexConflictSuggestionProgress(
                CodexConflictSuggestionProgressPhase.Generating,
                19,
                21),
            progress[2]);
        Assert.Equal(
            new CodexConflictSuggestionProgress(
                CodexConflictSuggestionProgressPhase.Generating,
                21,
                21),
            progress[3]);
    }

    [Fact]
    public async Task CancellationAfterAnEarlierChunkDoesNotReturnPartialResults()
    {
        var cancellation = new CancellationTokenSource();
        var calls = 0;
        var request = new CodexConflictSuggestionBatchRequest(
            new CodexConflictSuggestionContext(
                "main",
                "feature",
                CreateFiles(21),
                [],
                [],
                []));

        var orchestrator = new CodexConflictSuggestionOrchestrator(
            (generationRequest, _) =>
            {
                calls++;
                cancellation.Cancel();
                return Task.FromResult(
                    CreateResult(generationRequest.Context.Files, "unused"));
            });

        await Assert.ThrowsAsync<CodexConflictSuggestionGenerationCancelledException>(
            () => orchestrator.SuggestAsync(
                request,
                cancellationToken: cancellation.Token));
        Assert.Equal(1, calls);
    }

    private static IReadOnlyList<CodexConflictFileContext> CreateFiles(int count)
    {
        var files = new List<CodexConflictFileContext>(count);
        for (var index = 0; index < count; index++)
        {
            var ours = $"const value = {index};";
            if (index == count - 2)
            {
                ours = $"import Value from './file-{index + 1:000}';\n{ours}";
            }
            else if (index == count - 1)
            {
                ours = $"export const Value = {index};";
            }

            files.Add(
                new CodexConflictFileContext(
                    $"file-{index:000}.ts",
                    [new CodexConflictHunkContext(
                        ours,
                        $"const value = {index + 1};",
                        null,
                        "before",
                        "after")]));
        }

        return files;
    }

    private static CodexConflictSuggestionResult CreateResult(
        IReadOnlyList<CodexConflictFileContext> files,
        string summary)
    {
        return new CodexConflictSuggestionResult(
            summary,
            [new CodexConflictSuggestionReference(
                CodexConflictReferenceType.PullRequest,
                "42")],
            files.Select(
                    file => new CodexConflictResolution(
                        file.Path,
                        [new CodexConflictHunkResolution("resolved")],
                        "The incoming value is intentional."))
                .ToArray());
    }
}
