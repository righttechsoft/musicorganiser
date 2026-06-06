using System;

namespace MusicOrganiser.Models;

/// <summary>
/// Plain DTO mirroring a row of the SQLite <c>folders</c> table.
/// Returned by <see cref="Services.DatabaseService.GetChildFolders"/>.
/// </summary>
public class FolderRecord
{
    public string FullPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ParentPath { get; set; }
    public DateTime? CreatedUtc { get; set; }
    public DateTime? ModifiedUtc { get; set; }
    public int? Rating { get; set; }
    public string Tags { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
}
