using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace WinGit.Native;

internal sealed record NativeTemplateChoice(string Key, string DisplayName);

/// <summary>
/// Provides the repository templates that are packaged with the native app.
/// The source files are embedded at build time so the installed app never
/// needs the Electron source tree or the current working directory.
/// </summary>
internal static class NativeRepositoryTemplates
{
    private const string GitIgnoreResourcePrefix = "WinGit.Native.Templates.GitIgnore.";
    private const string LicenseResourcePrefix = "WinGit.Native.Templates.License.";
    private const string GitIgnoreExtension = ".gitignore";
    private const string LicenseExtension = ".txt";
    private static readonly Encoding TemplateEncoding = new UTF8Encoding(false, true);
    private static readonly Lazy<IReadOnlyList<LicenseTemplate>> LicenseTemplates =
        new(LoadLicenseTemplates, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<NativeTemplateChoice> GetGitIgnoreChoices()
    {
        var choices = new List<NativeTemplateChoice>
        {
            new("None", "None"),
        };

        choices.AddRange(
            GetResourceNames(GitIgnoreResourcePrefix, GitIgnoreExtension)
                .Select(name => new NativeTemplateChoice(
                    GetResourceKey(name, GitIgnoreResourcePrefix, GitIgnoreExtension),
                    GetResourceKey(name, GitIgnoreResourcePrefix, GitIgnoreExtension)))
                .OrderBy(choice => choice.DisplayName, StringComparer.CurrentCultureIgnoreCase));

        return choices;
    }

    public static IReadOnlyList<NativeTemplateChoice> GetLicenseChoices()
    {
        var choices = new List<NativeTemplateChoice>
        {
            new("None", "None"),
        };

        choices.AddRange(
            LicenseTemplates.Value
                .OrderByDescending(template => template.Featured)
                .ThenBy(template => template.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(template => new NativeTemplateChoice(template.Key, template.DisplayName)));

        return choices;
    }

    public static Task<string> ReadGitIgnoreAsync(
        string key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resourceName = FindResourceName(GitIgnoreResourcePrefix, GitIgnoreExtension, key)
            ?? throw new ArgumentException("The selected Git ignore template is unavailable.", nameof(key));

        return Task.FromResult(ReadResourceText(resourceName));
    }

    public static Task<string> ReadLicenseAsync(
        string key,
        string project,
        string holder,
        string email,
        string description,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var template = LicenseTemplates.Value.FirstOrDefault(
            candidate => string.Equals(candidate.Key, key, StringComparison.Ordinal));
        if (template is null)
        {
            throw new ArgumentException("The selected license template is unavailable.", nameof(key));
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fullname"] = holder,
            ["email"] = email,
            ["project"] = project,
            ["description"] = description,
            ["year"] = DateTimeOffset.Now.Year.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
        };

        var body = template.Body;
        foreach (var field in new[] { "fullname", "email", "project", "description", "year" })
        {
            body = ReplaceToken(body, field, fields[field]);
        }

        return Task.FromResult(body);
    }

    public static string BuildReadme(string destination, string description)
    {
        var name = new DirectoryInfo(destination).Name;
        return description.Length == 0
            ? $"# {name}\n"
            : $"# {name}\n{description}\n";
    }

    private static IReadOnlyList<LicenseTemplate> LoadLicenseTemplates()
    {
        return GetResourceNames(LicenseResourcePrefix, LicenseExtension)
            .Select(CreateLicenseTemplate)
            .ToArray();
    }

    private static LicenseTemplate CreateLicenseTemplate(string resourceName)
    {
        var key = GetResourceKey(resourceName, LicenseResourcePrefix, LicenseExtension);
        var source = ReadResourceText(resourceName);
        var (metadata, body) = SplitFrontMatter(source);
        var title = ReadMetadataValue(metadata, "title");
        var featured = string.Equals(
            ReadMetadataValue(metadata, "featured"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        var hidden = string.Equals(
            ReadMetadataValue(metadata, "hidden"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        return new LicenseTemplate(
            key,
            string.IsNullOrWhiteSpace(title) ? key : title,
            featured,
            hidden,
            body);
    }

    private static IEnumerable<string> GetResourceNames(string prefix, string extension)
    {
        return typeof(NativeRepositoryTemplates).Assembly
            .GetManifestResourceNames()
            .Where(name =>
                name.StartsWith(prefix, StringComparison.Ordinal) &&
                name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindResourceName(string prefix, string extension, string key)
    {
        return GetResourceNames(prefix, extension)
            .FirstOrDefault(name => string.Equals(
                GetResourceKey(name, prefix, extension),
                key,
                StringComparison.Ordinal));
    }

    private static string GetResourceKey(string resourceName, string prefix, string extension)
    {
        return resourceName[prefix.Length..^extension.Length];
    }

    private static string ReadResourceText(string resourceName)
    {
        using var stream = typeof(NativeRepositoryTemplates).Assembly
            .GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException(
                "The selected repository template is not included in this build.",
                resourceName);
        using var reader = new StreamReader(
            stream,
            TemplateEncoding,
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static (string Metadata, string Body) SplitFrontMatter(string text)
    {
        if (!text.StartsWith("---", StringComparison.Ordinal))
        {
            return (string.Empty, text);
        }

        var match = Regex.Match(
            text,
            "\\A---\\r?\\n(?<metadata>.*?)\\r?\\n---(?:\\r?\\n|$)",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        return match.Success
            ? (match.Groups["metadata"].Value, text[match.Length..])
            : (string.Empty, text);
    }

    private static string? ReadMetadataValue(string metadata, string key)
    {
        var match = Regex.Match(
            metadata,
            $"(?m)^[ \\t]*{Regex.Escape(key)}:[ \\t]*(?<value>[^\\r\\n]*)$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups["value"].Value.Trim();
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')))
        {
            value = value[1..^1];
        }

        return value;
    }

    private static string ReplaceToken(string text, string token, string value)
    {
        // The original templates use both [token] and {token}. Convert and
        // replace them in the same order as the Electron implementation.
        var bracketPattern = $"\\[{Regex.Escape(token)}\\]";
        var bracePattern = $"\\{{{Regex.Escape(token)}\\}}";
        var result = Regex.Replace(
            text,
            bracketPattern,
            _ => "{" + token + "}",
            RegexOptions.CultureInvariant);
        return Regex.Replace(
            result,
            bracePattern,
            _ => value,
            RegexOptions.CultureInvariant);
    }

    private sealed record LicenseTemplate(
        string Key,
        string DisplayName,
        bool Featured,
        bool Hidden,
        string Body);
}
