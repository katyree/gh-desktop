using System.Text;

namespace WinGit.Core;

public sealed partial class GitRepositoryService
{
    private static readonly UTF8Encoding CreationUtf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Initializes an unborn repository and optionally creates template files.</summary>
    public async Task<RepositoryStatus> InitializeAsync(
        string destination,
        string initialBranch,
        string? readmeContents = null,
        string? gitignoreContents = null,
        string? licenseContents = null,
        CancellationToken cancellationToken = default)
    {
        var destinationPath = NormalizeCreationDestination(destination, nameof(destination));
        var validationDirectory = GetValidationDirectory(destinationPath);
        var branchName = await ValidateBranchNameAsync(
            validationDirectory,
            initialBranch,
            cancellationToken).ConfigureAwait(false);
        var templates = new (string FileName, string? Contents)[]
        {
            ("README.md", readmeContents!),
            (".gitignore", gitignoreContents!),
            ("LICENSE", licenseContents!),
        };

        return await ExecuteMutationAsync(
            destinationPath,
            cancellationToken,
            async path =>
            {
                Directory.CreateDirectory(path);
                await EnsureNotExistingGitRepositoryAsync(path, cancellationToken).ConfigureAwait(false);
                EnsureTemplateDestinationsAvailable(path, templates);
                await processRunner.RunAsync(
                    path,
                    ["init", "--initial-branch", branchName],
                    cancellationToken).ConfigureAwait(false);

                foreach (var template in templates)
                {
                    if (template.Contents is not null)
                    {
                        await WriteNewFileAsync(
                            Path.Combine(path, template.FileName),
                            template.Contents,
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                return await GetStatusAsync(path, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    /// <summary>Clones a repository into an unoccupied destination and returns its status.</summary>
    public async Task<RepositoryStatus> CloneAsync(
        string url,
        string destination,
        string? branch = null,
        CancellationToken cancellationToken = default)
    {
        var remoteUrl = ValidateRemoteUrl(url);
        var destinationPath = NormalizeCreationDestination(destination, nameof(destination));
        var parentDirectory = Directory.GetParent(destinationPath)?.FullName;
        if (parentDirectory is null || !Directory.Exists(parentDirectory))
        {
            throw new DirectoryNotFoundException("The clone destination parent directory does not exist.");
        }

        var branchName = branch is null
            ? null
            : await ValidateBranchNameAsync(parentDirectory, branch, cancellationToken).ConfigureAwait(false);

        return await ExecuteMutationAsync(
            parentDirectory,
            cancellationToken,
            async workingDirectory =>
            {
                EnsureCloneDestinationAvailable(destinationPath);
                var arguments = new List<string> { "clone", "--recursive" };
                if (branchName is not null)
                {
                    arguments.Add("--branch");
                    arguments.Add(branchName);
                }

                arguments.Add("--");
                arguments.Add(remoteUrl);
                arguments.Add(destinationPath);
                await RunRemoteCommandAsync(workingDirectory, arguments, cancellationToken).ConfigureAwait(false);
                return await OpenAsync(destinationPath, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    private async Task EnsureNotExistingGitRepositoryAsync(
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var gitDirectory = Path.Combine(destinationPath, ".git");
        if (HasFilesystemEntry(gitDirectory))
        {
            throw new InvalidOperationException("The initialization destination already contains a Git repository.");
        }

        var workTreeResult = await processRunner.RunAsync(
            destinationPath,
            ["rev-parse", "--show-toplevel", "--is-inside-work-tree"],
            cancellationToken,
            expectedExitCodes: [128]).ConfigureAwait(false);
        EnsureComplete(workTreeResult, "existing repository check");
        if (workTreeResult.ExitCode == 0)
        {
            var lines = DecodeUtf8(workTreeResult.StandardOutput, "existing repository check")
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length >= 2
                && string.Equals(lines[1].Trim(), "true", StringComparison.OrdinalIgnoreCase)
                && IsSamePath(destinationPath, NormalizeReportedPath(lines[0], destinationPath)))
            {
                throw new InvalidOperationException("The initialization destination already contains a Git repository.");
            }
        }

        var bareResult = await processRunner.RunAsync(
            destinationPath,
            ["rev-parse", "--is-bare-repository"],
            cancellationToken,
            expectedExitCodes: [128]).ConfigureAwait(false);
        EnsureComplete(bareResult, "existing bare repository check");
        if (bareResult.ExitCode == 0
            && string.Equals(
                DecodeUtf8(bareResult.StandardOutput, "existing bare repository check").Trim(),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The initialization destination already contains a bare Git repository.");
        }
    }

    private static void EnsureTemplateDestinationsAvailable(
        string destinationPath,
        IReadOnlyList<(string FileName, string? Contents)> templates)
    {
        foreach (var template in templates)
        {
            if (template.Contents is null)
            {
                continue;
            }

            var filePath = Path.Combine(destinationPath, template.FileName);
            if (HasFilesystemEntry(filePath))
            {
                throw new IOException($"The template destination '{template.FileName}' already exists.");
            }
        }
    }

    private static void EnsureCloneDestinationAvailable(string destinationPath)
    {
        if (!HasFilesystemEntry(destinationPath))
        {
            return;
        }

        if (!Directory.Exists(destinationPath)
            || Directory.EnumerateFileSystemEntries(destinationPath).Any())
        {
            throw new IOException("The clone destination is already occupied.");
        }
    }

    private static async Task WriteNewFileAsync(
        string path,
        string contents,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var writer = new StreamWriter(
            stream,
            CreationUtf8,
            bufferSize: 64 * 1024,
            leaveOpen: false);
        await writer.WriteAsync(contents.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeCreationDestination(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (path.IndexOf('\0') >= 0 || path.Contains('\r') || path.Contains('\n'))
        {
            throw new ArgumentException("The repository destination cannot contain NUL or line breaks.", parameterName);
        }

        var fullPath = TrimDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(fullPath, Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A filesystem root cannot be used as a repository destination.", parameterName);
        }

        return fullPath;
    }

    private static string GetValidationDirectory(string destinationPath)
    {
        if (Directory.Exists(destinationPath))
        {
            return destinationPath;
        }

        var parent = Directory.GetParent(destinationPath)?.FullName;
        return parent is not null && Directory.Exists(parent)
            ? parent
            : Directory.GetCurrentDirectory();
    }

    private static string NormalizeReportedPath(string reportedPath, string candidate)
    {
        var trimmed = reportedPath.Trim();
        return Path.IsPathRooted(trimmed)
            ? Path.GetFullPath(trimmed)
            : Path.GetFullPath(Path.Combine(candidate, trimmed));
    }

}
