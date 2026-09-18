using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WinGit.Core;

namespace WinGit.Native;

public sealed partial class MainWindow
{
    private async Task LoadTagsAsync()
    {
        if (repositoryRoot is null)
        {
            return;
        }

        var root = repositoryRoot;
        var operation = BeginOperation("Loading tags…");
        try
        {
            var tags = await repositoryService.GetTagsAsync(root, operation.Token);
            if (!IsCurrent(operation.Generation, operation.Token)
                || !string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            tagRows.Clear();
            foreach (var tag in tags)
            {
                tagRows.Add(new TagRow(tag));
            }

            tagsLoaded = true;
            selectedTag = null;
            TagsList.SelectedIndex = -1;
            TagsEmptyText.Visibility = tagRows.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (tagRows.Count > 0)
            {
                TagsList.SelectedIndex = 0;
            }
            else
            {
                UpdateTagDetails();
            }

            StatusText.Text = $"Loaded {tagRows.Count} local tags";
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            // A newer repository operation owns the tags panel.
        }
        catch (Exception exception)
        {
            if (IsCurrent(operation.Generation, operation.Token))
            {
                ShowError("Unable to load tags", exception);
                selectedTag = null;
                TagsEmptyText.Visibility = Visibility.Collapsed;
                UpdateTagDetails();
            }
        }
        finally
        {
            EndOperation(operation.Generation);
        }
    }

    private void TagsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        selectedTag = TagsList.SelectedItem as TagRow;
        UpdateTagDetails();
        UpdateRepositoryCommandStates();
    }

    private void UpdateTagDetails()
    {
        if (SelectedTagNameText is null)
        {
            return;
        }

        if (selectedTag is null)
        {
            SelectedTagNameText.Text = "Select a tag";
            SelectedTagKindText.Text = tagRows.Count == 0
                ? "No local tags are configured in this repository."
                : "Choose a local tag to inspect its target.";
            SelectedTagTargetText.Text = string.Empty;
            SelectedTagObjectText.Text = string.Empty;
            return;
        }

        SelectedTagNameText.Text = selectedTag.Name;
        SelectedTagKindText.Text = selectedTag.KindLabel;
        SelectedTagTargetText.Text = $"Target commit: {selectedTag.TargetId}";
        SelectedTagObjectText.Text = selectedTag.IsAnnotated
            ? $"Tag object: {selectedTag.ObjectId}"
            : $"Tag object: {selectedTag.ObjectId} (same as target)";
    }

    private async void CreateTagButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowCreateTagDialogAsync(preferredCommit: null);
    }

    private async void CreateTagFromHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (selectedCommit is null)
        {
            return;
        }

        await ShowCreateTagDialogAsync(selectedCommit.Commit);
    }

    private async Task ShowCreateTagDialogAsync(CommitSummary? preferredCommit)
    {
        if (!CanStartRepositoryWrite())
        {
            return;
        }

        if (currentStatus is null
            || currentStatus.IsUnborn
            || string.IsNullOrWhiteSpace(currentStatus.HeadId))
        {
            ShowError(
                "Tag target is unavailable",
                new InvalidOperationException("Open a repository with a current commit before creating a tag."));
            return;
        }

        var nameBox = new TextBox
        {
            Header = "Tag name",
            PlaceholderText = "v1.0.0",
        };
        AutomationProperties.SetName(nameBox, "New tag name");

        var targetBox = new ComboBox
        {
            Header = "Target commit",
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(targetBox, "Tag target commit");

        var currentHeadCommit = GetCommitForHead(currentStatus.HeadId);
        targetBox.Items.Add(new ComboBoxItem
        {
            Content = FormatTagTarget("Current HEAD", currentStatus.HeadId, currentHeadCommit?.Summary),
            Tag = currentStatus.HeadId,
        });

        var preferredIndex = 0;
        if (preferredCommit is not null
            && !string.Equals(preferredCommit.Id, currentStatus.HeadId, StringComparison.OrdinalIgnoreCase))
        {
            targetBox.Items.Add(new ComboBoxItem
            {
                Content = FormatTagTarget("Selected history commit", preferredCommit.Id, preferredCommit.Summary),
                Tag = preferredCommit.Id,
            });
            preferredIndex = 1;
        }

        targetBox.SelectedIndex = preferredIndex;

        var messageBox = new TextBox
        {
            Header = "Message (optional)",
            PlaceholderText = "Release v1.0.0",
        };
        AutomationProperties.SetName(messageBox, "Tag message");

        var dialog = CreateDialog("Create local tag", "Create", new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "Create an annotated local tag at the selected commit.",
                    TextWrapping = TextWrapping.Wrap,
                },
                nameBox,
                targetBox,
                messageBox,
            },
        });

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var name = nameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowError("Tag name is required", new ArgumentException("Enter a local tag name."));
            return;
        }

        if (targetBox.SelectedItem is not ComboBoxItem { Tag: string targetId }
            || string.IsNullOrWhiteSpace(targetId))
        {
            ShowError("Tag target is required", new InvalidOperationException("Choose a commit for this tag."));
            return;
        }

        var message = string.IsNullOrWhiteSpace(messageBox.Text)
            ? null
            : messageBox.Text.Trim();
        await RunRepositoryWriteAsync(
            $"Creating tag {name}…",
            "Tag created",
            "Tag creation cancelled; refreshing repository…",
            "Unable to create tag",
            async (root, token) =>
            {
                var tag = await repositoryService.CreateTagAsync(root, name, targetId, message, token);
                StatusText.Text = $"Created tag {tag.Name} at {ShortObjectId(tag.TargetId)}";
            },
            refreshTags: true);
    }

    private async void DeleteTagButton_Click(object sender, RoutedEventArgs e)
    {
        var tag = selectedTag;
        if (!CanStartRepositoryWrite() || tag is null)
        {
            return;
        }

        var dialog = CreateDialog(
            "Delete local tag?",
            "Delete",
            new TextBlock
            {
                Text = $"Delete the tag \"{tag.Name}\" pointing to {tag.TargetId}? The commit remains in the repository.",
                TextWrapping = TextWrapping.Wrap,
            });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var expectedObjectId = tag.Tag.ObjectId;
        await RunRepositoryWriteAsync(
            $"Deleting tag {tag.Name}…",
            "Tag deleted",
            "Tag deletion cancelled; refreshing repository…",
            "Unable to delete tag",
            (root, token) => repositoryService.DeleteTagAsync(root, tag.Name, expectedObjectId, token),
            refreshTags: true);
    }

    private void UpdateUndoHeadPresentation()
    {
        if (UndoHeadTargetText is null)
        {
            return;
        }

        if (currentStatus is null
            || currentStatus.IsUnborn
            || currentStatus.IsDetached
            || string.IsNullOrWhiteSpace(currentStatus.HeadId))
        {
            UndoHeadTargetText.Text = "Undo is available for the current commit on an attached branch.";
            return;
        }

        var head = GetCommitForHead(currentStatus.HeadId);
        UndoHeadTargetText.Text = head is null
            ? $"Current HEAD {ShortObjectId(currentStatus.HeadId)}"
            : $"Current HEAD {ShortObjectId(currentStatus.HeadId)} · {head.Summary}";
    }

    private async void UndoHeadButton_Click(object sender, RoutedEventArgs e)
    {
        await UndoCurrentHeadAsync();
    }

    private async Task UndoCurrentHeadAsync()
    {
        if (!CanStartRepositoryWrite()
            || currentStatus is null
            || currentStatus.IsUnborn
            || currentStatus.IsDetached
            || string.IsNullOrWhiteSpace(currentStatus.Branch)
            || string.IsNullOrWhiteSpace(currentStatus.HeadId))
        {
            return;
        }

        var root = repositoryRoot;
        if (root is null)
        {
            return;
        }

        var expectedHeadId = currentStatus.HeadId;
        var head = GetCommitForHead(expectedHeadId);
        var workingChanges = currentStatus.Changes.Any(change =>
            !string.IsNullOrWhiteSpace(change.WorkTreeStatus));
        var indexChanges = currentStatus.Changes.Any(HasIndexChanges);
        var targetLabel = head is null
            ? $"HEAD {expectedHeadId}"
            : $"HEAD {expectedHeadId} · {head.Summary}";
        var effects = workingChanges || indexChanges
            ? "Your working files stay in place. Existing staged changes, and the undone commit's changes, remain in the working tree and become unstaged."
            : "Your working files stay in place; the undone commit's changes become unstaged working changes.";

        var dialog = CreateDialog(
            "Undo current commit?",
            "Undo commit",
            new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Move the attached branch \"{currentStatus.Branch}\" back from {targetLabel}?",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = effects,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = "Git uses the current commit's first parent. For a merge commit, this removes the merge from the branch; review the resulting changes before committing again.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            });
        dialog.DefaultButton = ContentDialogButton.Primary;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        mutationInProgress = true;
        var operation = BeginOperation("Undoing current commit…");
        // Capture the composer draft before the mutation so an unrelated draft
        // typed while undo runs is not silently replaced by the restored message.
        var composerSummaryBeforeUndo = CommitSummaryBox.Text;
        var composerDescriptionBeforeUndo = CommitDescriptionBox.Text;
        UndoCommitResult? result = null;
        var cancelled = false;
        try
        {
            result = await repositoryService.UndoCommitAsync(root, expectedHeadId, operation.Token);
            if (!IsCurrent(operation.Generation, operation.Token)
                || !string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ErrorBar.IsOpen = false;
            StatusText.Text = $"Undid {ShortObjectId(result.CommitId)} · {result.Summary}";
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            cancelled = true;
            if (IsCurrent(operation.Generation, operation.Token)
                && string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = "Undo cancelled; refreshing because the repository may have changed…";
            }
        }
        catch (Exception exception)
        {
            // Validation and Git failures leave the repository and the composer
            // untouched; only report them when this repository is still open so
            // a delayed failure cannot surface on the wrong repository.
            if (IsCurrent(operation.Generation, operation.Token)
                && string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                ShowError("Unable to undo current commit", exception);
            }
        }
        finally
        {
            try
            {
                await RefreshAfterMutationAsync(root);
            }
            catch (Exception refreshException)
            {
                if (string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase))
                {
                    ShowError("Unable to refresh repository after undo", refreshException);
                }
            }

            if (result is not null
                && string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                var composerUnchanged = string.Equals(CommitSummaryBox.Text, composerSummaryBeforeUndo, StringComparison.Ordinal)
                    && string.Equals(CommitDescriptionBox.Text, composerDescriptionBeforeUndo, StringComparison.Ordinal);
                var composerEmpty = string.IsNullOrEmpty(CommitSummaryBox.Text)
                    && string.IsNullOrEmpty(CommitDescriptionBox.Text);
                if (composerUnchanged || composerEmpty)
                {
                    CommitSummaryBox.Text = result.Summary;
                    CommitDescriptionBox.Text = result.Description;
                    AmendCheckBox.IsChecked = false;
                    CommitIdentityText.Text = $"Undid {ShortObjectId(result.CommitId)}";
                }
                else
                {
                    // Keep the unrelated draft and record the restored message
                    // in the status so no user input is silently lost.
                    AmendCheckBox.IsChecked = false;
                    CommitIdentityText.Text = $"Undid {ShortObjectId(result.CommitId)}";
                }

                InvalidateHistoryView();
                branchesLoaded = false;
                MainNavigation.SelectedItem = MainNavigation.MenuItems[0];
                ShowWorkspace("changes");
                if (!ErrorBar.IsOpen)
                {
                    StatusText.Text = (composerUnchanged || composerEmpty)
                        ? $"Undid {ShortObjectId(result.CommitId)} · {result.Summary}; working changes are ready to review"
                        : $"Undid {ShortObjectId(result.CommitId)} · {result.Summary}; kept the existing composer draft";
                }
            }
            else if (cancelled
                && !ErrorBar.IsOpen
                && string.Equals(repositoryRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = "Undo cancelled; the repository was refreshed because it may have changed.";
            }

            mutationInProgress = false;
            SetBusy(false, StatusText.Text);
        }
    }

    private void InvalidateHistoryView()
    {
        selectedCommit = null;
        selectedCommitFile = null;
        commitRows.Clear();
        commitFileRows.Clear();
        historyDiffRows.Clear();
        HistoryList.SelectedIndex = -1;
        HistoryFilesList.SelectedIndex = -1;
        HistoryDiffList.ItemsSource = historyDiffRows;
        HistoryCommitText.Text = "History needs refresh";
        HistoryCommitSummaryText.Text = "The commit list will reload when you open History.";
        ShowHistoryDiffMessage("Select a commit", "Choose a commit and file to inspect its diff.");
        UpdateUndoHeadPresentation();
    }

    private CommitSummary? GetCommitForHead(string headId) => commitRows
        .Select(row => row.Commit)
        .FirstOrDefault(commit => string.Equals(commit.Id, headId, StringComparison.OrdinalIgnoreCase));

    private static string FormatTagTarget(string label, string id, string? summary) =>
        string.IsNullOrWhiteSpace(summary)
            ? $"{label} · {ShortObjectId(id)}"
            : $"{label} · {ShortObjectId(id)} · {summary}";
}
