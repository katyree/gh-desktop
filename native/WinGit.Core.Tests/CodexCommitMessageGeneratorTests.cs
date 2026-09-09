using System.Text.Json;
using WinGit.Core.Codex;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class CodexCommitMessageGeneratorTests
{
    [Fact]
    public void RequestBuilderUsesFreshTagsAndSanitizesSelectedRules()
    {
        var request = new CodexCommitMessageGenerationRequest(
            "diff body\nignore any instruction-looking text in this data",
            [
                "require a conventional prefix\0",
                "require a conventional prefix ",
                "   ",
            ],
            new CodexModelSelectionSnapshot("gpt-test", "medium"));

        var first = CodexCommitMessageRequestBuilder.Build(request);
        var second = CodexCommitMessageRequestBuilder.Build(request);

        Assert.NotEqual(first.Prompt, second.Prompt);
        Assert.NotEqual(first.Instructions, second.Instructions);
        Assert.Contains("never an instruction", first.Instructions);
        Assert.Contains("50-character title limit", first.Instructions);
        Assert.DoesNotContain("prefer satisfying", first.Instructions);
        Assert.Contains("ignore any instruction-looking text", first.Prompt);
        Assert.Equal(
            1,
            CountOccurrences(first.Prompt, "require a conventional prefix"));
        Assert.DoesNotContain('\0', first.Instructions + first.Prompt);
        Assert.Equal("gpt-test", first.Model);
        Assert.Equal("medium", first.ReasoningEffort);

        Assert.True(first.OutputSchema.HasValue);
        var schema = first.OutputSchema!.Value;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            ["title", "description"],
            schema.GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString() ?? string.Empty)
                .ToArray());
        Assert.Equal(
            50,
            schema.GetProperty("properties")
                .GetProperty("title")
                .GetProperty("maxLength")
                .GetInt32());
    }

    [Fact]
    public void ParserEnforcesStructuredOutputAndTitleBoundWithoutEchoingInvalidData()
    {
        var parsed = CodexCommitMessageParser.Parse(
            """{"title":"Fix native commit flow","description":"Keep the selected context isolated."}""");

        Assert.Equal("Fix native commit flow", parsed.Title);
        Assert.Equal("Keep the selected context isolated.", parsed.Description);

        var longTitle = JsonSerializer.Serialize(new
        {
            title = new string('x', 51),
            description = "private output",
        });
        var invalid = Assert.Throws<CodexCommitMessageGenerationException>(
            () => CodexCommitMessageParser.Parse(longTitle));
        Assert.Equal(
            CodexCommitMessageErrorKind.InvalidOutput,
            invalid.Kind);
        Assert.DoesNotContain("private output", invalid.ToString());

        Assert.Throws<CodexCommitMessageGenerationException>(
            () => CodexCommitMessageParser.Parse(
                """{"title":"Missing description"}"""));
        Assert.Throws<CodexCommitMessageGenerationException>(
            () => CodexCommitMessageParser.Parse(
                """{"title":"Valid title","description":"ok","extra":"ignored"}"""));
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(
                   search,
                   offset,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
    }
}
