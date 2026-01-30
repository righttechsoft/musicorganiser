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
    Cancel
}
