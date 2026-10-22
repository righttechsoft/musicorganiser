namespace MusicOrganiser.Models;

public enum FileDuplicateAction
{
    Skip,
    Overwrite,
    Cancel
}

public enum FolderDuplicateAction
{
    Skip,
    Merge,
    Replace,
    KeepBoth,
    IntoTarget,
    Custom,
    Cancel
}

public enum FolderPlanAction
{
    Transfer,
    DeleteTarget
}

/// <summary>One file-level step of a per-file folder plan; the path is relative to both roots.</summary>
public record FolderPlanItem(string RelativePath, FolderPlanAction Action);
