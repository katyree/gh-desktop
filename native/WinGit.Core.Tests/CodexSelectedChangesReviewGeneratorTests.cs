using System.Text.Json;
using WinGit.Core.Codex;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class CodexSelectedChangesReviewGeneratorTests
{
    [Fact]
    public void BuilderUsesFreshTagsAndParserAcceptsChangedAndEmptyFindings()
    {
        var snapshot = CreateSnapshot();
        var request = new CodexSelectedChangesReviewGenerationRequest(
            snapshot,
            new CodexModelSelectionSnapshot("gpt-test", "medium"));

        var first = CodexSelectedChangesReviewRequestBuilder.Build(request);
        var second = CodexSelectedChangesReviewRequestBuilder.Build(request);

        Assert.NotEqual(first.Prompt, second.Prompt);
        Assert.NotEqual(first.Instructions, second.Instructions);
        Assert.Contains("untrusted data", first.Instructions);
        Assert.Contains("complete input", first.Instructions);
        Assert.Contains("diff body", first.Prompt);
        Assert.Equal("gpt-test", first.Model);
        Assert.Equal("medium", first.ReasoningEffort);

        Assert.True(first.OutputSchema.HasValue);
        var schema = first.OutputSchema!.Value;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            ["findings"],
            schema.GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString() ?? string.Empty)
                .ToArray());
        var findingsSchema = schema.GetProperty("properties")
            .GetProperty("findings");
        Assert.Equal(50, findingsSchema.GetProperty("maxItems").GetInt32());
        Assert.Equal(
            160,
            findingsSchema.GetProperty("items")
                .GetProperty("properties")
                .GetProperty("title")
                .GetProperty("maxLength")
                .GetInt32());

        var parsed = CodexSelectedChangesReviewParser.Parse(
            JsonSerializer.Serialize(new
            {
                findings = new[]
                {
                    new
                    {
                        path = "src/value.cs",
                        line = 2,
                        side = "old",
                        title = "Preserve the old value",
                        explanation = "The selected deletion changes the value used by callers.",
                        suggestion = "Keep the value or update its callers in the same change.",
                    },
                    new
                    {
                        path = "src/value.cs",
                        line = 2,
                        side = "new",
                        title = "Check the replacement",
                        explanation = "The selected addition changes the value used by callers.",
                        suggestion = "Add a regression check for the replacement value.",
                    },
                },
            }),
            snapshot);

        Assert.Equal(2, parsed.Count);
        Assert.Equal(CodexSelectedChangesReviewSide.Old, parsed[0].Side);
        Assert.Equal(CodexSelectedChangesReviewSide.New, parsed[1].Side);
        Assert.Equal("src/value.cs", parsed[0].Path);

        var empty = CodexSelectedChangesReviewParser.Parse(
            "{\"findings\":[]}",
            snapshot);
        Assert.Empty(empty);
    }

    [Fact]
    public void ParserRejectsUnchangedAndForeignLocationsWithoutEchoingInput()
    {
        var snapshot = CreateSnapshot();

        var unchanged = Assert.Throws<
            CodexSelectedChangesReviewGenerationException>(
            () => CodexSelectedChangesReviewParser.Parse(
                CreateFindingJson("src/value.cs", 1, "new"),
                snapshot));
        Assert.Equal(
            CodexSelectedChangesReviewErrorKind.InvalidOutput,
            unchanged.Kind);
        Assert.DoesNotContain("src/value.cs", unchanged.ToString());

        var foreign = Assert.Throws<
            CodexSelectedChangesReviewGenerationException>(
            () => CodexSelectedChangesReviewParser.Parse(
                CreateFindingJson("other.cs", 2, "new"),
                snapshot));
        Assert.Equal(
            CodexSelectedChangesReviewErrorKind.InvalidOutput,
            foreign.Kind);
        Assert.DoesNotContain("other.cs", foreign.ToString());
    }

    private static CodexSelectedChangesReviewSnapshot CreateSnapshot() =>
        new(
            "diff body",
            [
                new CodexSelectedChangesReviewFile(
                    "src/value.cs",
                    """
                    diff --git a/src/value.cs b/src/value.cs
                    index 1111111..2222222 100644
                    --- a/src/value.cs
                    +++ b/src/value.cs
                    @@ -1,3 +1,3 @@
                     before
                    -old
                    +new
                     after
                    """),
            ]);

    private static string CreateFindingJson(
        string path,
        int line,
        string side) =>
        JsonSerializer.Serialize(new
        {
            findings = new[]
            {
                new
                {
                    path,
                    line,
                    side,
                    title = "A finding",
                    explanation = "An explanation.",
                    suggestion = "A suggestion.",
                },
            },
        });
}
