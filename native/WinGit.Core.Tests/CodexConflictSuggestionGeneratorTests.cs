using System.Text.Json;
using WinGit.Core.Codex;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class CodexConflictSuggestionGeneratorTests
{
    [Fact]
    public void RequestBuilderUsesFreshUntrustedTagsAndSupportedSchema()
    {
        var request = new CodexConflictSuggestionGenerationRequest(
            new CodexConflictSuggestionContext(
                "main",
                "feature",
                [
                    new CodexConflictFileContext(
                        "src/value.ts",
                        [
                            new CodexConflictHunkContext(
                                "const value = 1",
                                "const value = 2",
                                "const value = 0",
                                "before",
                                "after"),
                        ]),
                ],
                [
                    new CodexConflictPullRequestContext(
                        42,
                        "Update value",
                        "Keep the incoming behavior."),
                ],
                [
                    new CodexConflictCommitContext(
                        "abc1234",
                        "abc1234",
                        "Change value",
                        true),
                ],
                []),
            new CodexModelSelectionSnapshot("gpt-test", "high"));

        var first = CodexConflictSuggestionRequestBuilder.Build(request);
        var second = CodexConflictSuggestionRequestBuilder.Build(request);

        Assert.NotEqual(first.Prompt, second.Prompt);
        Assert.NotEqual(first.Instructions, second.Instructions);
        Assert.Contains("untrusted", first.Instructions);
        Assert.Contains("src/value.ts", first.Prompt);
        Assert.DoesNotContain("rawContent", first.Prompt);
        Assert.Equal("gpt-test", first.Model);
        Assert.Equal("high", first.ReasoningEffort);

        Assert.True(first.OutputSchema.HasValue);
        var schema = first.OutputSchema!.Value;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal(
            ["summary", "references", "resolutions"],
            schema.GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString() ?? string.Empty)
                .ToArray());
        var action = schema.GetProperty("properties")
            .GetProperty("resolutions")
            .GetProperty("items")
            .GetProperty("properties")
            .GetProperty("action")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(value => value.ValueKind == JsonValueKind.Null
                ? null
                : value.GetString())
            .ToArray();
        Assert.Contains("keep", action);
        Assert.Contains("delete", action);
        Assert.Contains(null, action);
    }

    [Fact]
    public void ParserPreservesActionsAndReferencesAndRejectsUnsafeShape()
    {
        var context = new CodexConflictSuggestionContext(
            "main",
            "feature",
            [
                new CodexConflictFileContext(
                    "src/value.ts",
                    [
                        new CodexConflictHunkContext(
                            "const value = 1",
                            "const value = 2",
                            null,
                            string.Empty,
                            string.Empty),
                    ]),
                new CodexConflictFileContext(
                    "deleted.ts",
                    [],
                    new CodexConflictDeleteConflict(
                        CodexConflictDeletedSide.Ours)),
            ],
            [new CodexConflictPullRequestContext(42, "Incoming value", "")],
            [new CodexConflictCommitContext(
                "abc1234567890abc1234567890abc1234567890a",
                "abc1234",
                "Incoming value",
                true)],
            []);

        var parsed = CodexConflictSuggestionParser.Parse(
            """
            {
              "summary": "### Conflicting changes\nBoth sides changed value.\n\n### Resolution\nKeep the incoming value.",
              "references": [
                { "type": "pullRequest", "id": "#42" },
                { "type": "commit", "id": "abc1234" },
                { "type": "commit", "id": "invalid" },
                { "type": "pullRequest", "id": "#99" },
                { "type": "commit", "id": "deadbeef" }
              ],
              "resolutions": [
                {
                  "path": "./src\\value.ts",
                  "hunks": [{ "resolvedContent": "const value = 2" }],
                  "reasoning": "The incoming change is intentional.",
                  "action": null
                },
                {
                  "path": "deleted.ts",
                  "hunks": [],
                  "reasoning": "The deletion is intentional.",
                  "action": "delete"
                }
              ]
            }
            """,
            context);

        Assert.Equal(2, parsed.Resolutions.Count);
        Assert.Equal("src/value.ts", parsed.Resolutions[0].Path);
        Assert.Equal("const value = 2", parsed.Resolutions[0].Hunks[0].ResolvedContent);
        Assert.Null(parsed.Resolutions[0].Action);
        Assert.Equal(
            CodexConflictSuggestionAction.Delete,
            parsed.Resolutions[1].Action);
        Assert.Equal(2, parsed.References.Count);
        Assert.Equal("42", parsed.References[0].Id);
        Assert.Equal("abc1234", parsed.References[1].Id);

        var invalid = Assert.Throws<CodexConflictSuggestionGenerationException>(
            () => CodexConflictSuggestionParser.Parse(
                """
                {
                  "resolutions": [
                    {
                      "path": "../outside.ts",
                      "hunks": [{ "resolvedContent": "private suggestion" }],
                      "reasoning": "private reasoning"
                    }
                  ]
                }
                """,
                context));
        Assert.Equal(
            CodexConflictSuggestionErrorKind.InvalidOutput,
            invalid.Kind);
        Assert.DoesNotContain("private", invalid.ToString());

        var actionForText = Assert.Throws<CodexConflictSuggestionGenerationException>(
            () => CodexConflictSuggestionParser.Parse(
                """
                {
                  "resolutions": [
                    {
                      "path": "src/value.ts",
                      "hunks": [],
                      "reasoning": "Delete the file.",
                      "action": "delete"
                    },
                    {
                      "path": "deleted.ts",
                      "hunks": [],
                      "reasoning": "Delete the file.",
                      "action": "delete"
                    }
                  ]
                }
                """,
                context));
        Assert.Equal(
            CodexConflictSuggestionErrorKind.InvalidOutput,
            actionForText.Kind);

        var unknownAction = Assert.Throws<CodexConflictSuggestionGenerationException>(
            () => CodexConflictSuggestionParser.Parse(
                """
                {
                  "resolutions": [
                    {
                      "path": "src/value.ts",
                      "hunks": [{ "resolvedContent": "const value = 2" }],
                      "reasoning": "Keep the incoming value.",
                      "action": "merge"
                    },
                    {
                      "path": "deleted.ts",
                      "hunks": [],
                      "reasoning": "Keep the modified file.",
                      "action": "keep"
                    }
                  ]
                }
                """,
                context));
        Assert.Equal(
            CodexConflictSuggestionErrorKind.InvalidOutput,
            unknownAction.Kind);
    }
}
