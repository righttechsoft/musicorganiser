using System;

namespace MusicOrganiser.Models;

/// <summary>
/// Plain DTO mirroring a row of the SQLite <c>lyrics</c> table.
/// Returned by <see cref="Services.DatabaseService.GetLyrics"/>.
/// </summary>
public class LyricsRecord
{
    public string FullPath { get; set; } = string.Empty;
    public string? Plain { get; set; }
    public string? Lrc { get; set; }
    public string Source { get; set; } = string.Empty;
    public bool Synced { get; set; }
    public DateTime? UpdatedUtc { get; set; }

    /// <summary>Set by an online lookup that positively identified the track as instrumental
    /// (LRCLIB/LRCMux/SyncLRC all flag these). Not persisted — it exists so the Find flow can
    /// stop instead of burning an AI call and a whisper run on a track with no vocals.</summary>
    public bool Instrumental { get; set; }
}
