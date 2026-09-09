using WinGit.Core.Codex;

namespace WinGit.Native;

internal sealed class CodexModelRow
{
    public CodexModelRow(CodexModel model)
    {
        Model = model;
        DisplayName = string.IsNullOrWhiteSpace(model.DisplayName)
            ? model.Model
            : model.DisplayName;
        Description = model.Description;
        ModelId = model.Id;
    }

    public CodexModel Model { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public string ModelId { get; }

    public string AutomationName => $"{DisplayName} ({ModelId})";
}

internal sealed class CodexReasoningRow
{
    public CodexReasoningRow(CodexReasoningEffort effort)
    {
        ReasoningEffort = effort.ReasoningEffort;
        Description = effort.Description;
        DisplayName = string.IsNullOrWhiteSpace(effort.Description)
            ? effort.ReasoningEffort
            : $"{effort.ReasoningEffort} — {effort.Description}";
    }

    public string ReasoningEffort { get; }

    public string Description { get; }

    public string DisplayName { get; }

    public string AutomationName => $"Reasoning effort {ReasoningEffort}";
}
