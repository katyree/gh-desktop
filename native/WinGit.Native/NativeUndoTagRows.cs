using WinGit.Core;

namespace WinGit.Native;

internal sealed class TagRow
{
    public TagRow(TagSummary tag)
    {
        Tag = tag;
    }

    public TagSummary Tag { get; }

    public string Name => Tag.Name;

    public string ObjectId => Tag.ObjectId;

    public string TargetId => Tag.TargetId;

    public bool IsAnnotated => Tag.IsAnnotated;

    public string TargetLabel => $"target {ShortObjectId(TargetId)}";

    public string KindLabel => Tag.IsAnnotated ? "Annotated tag" : "Lightweight tag";

    public string SearchText => $"{Name} {KindLabel} {ObjectId} {TargetId}";

    private static string ShortObjectId(string value) => value.Length > 12 ? value[..12] : value;
}
