using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MusicOrganiser.Models;

namespace MusicOrganiser.Services;

/// <summary>
/// Owns lyrics fetch + cache. Three free, no-key sources are tried in order (LRCLIB →
/// LRCMux → SyncLRC, see <see cref="FetchOnlineAsync"/>); AI web-search plain lyrics is a
/// fallback that costs an API call, so callers only invoke it on request. Also owns the LRC
/// parse/build shared with <c>WhisperAlignService</c>'s output.
/// Cached results live in the SQLite <c>lyrics</c> table; an embedded ID3/Vorbis lyrics tag
/// is a last-resort fallback when nothing has been fetched yet.
/// </summary>
public class LyricsService
{
    private static readonly HttpClient ApiHttp = CreateApiHttp();

    private HttpClient? _anthropicHttp;
    private string? _apiKey;

    public LyricsService()
    {
        LoadApiKey();
    }

#if DEBUG
    static LyricsService()
    {
        SelfCheckParseLrc();
    }
#endif

    private static HttpClient CreateApiHttp()
    {
        var http = new HttpClient();
        // LRCLIB (and, by the same convention, LRCMux/SyncLRC) asks API consumers to identify
        // themselves in the User-Agent rather than pose as a browser.
        http.DefaultRequestHeaders.Add("User-Agent", "MusicOrganiser (https://github.com/rts/MusicOrganiser)");
        return http;
    }

    private void LoadApiKey()
    {
        var currentDir = AppDomain.CurrentDomain.BaseDirectory;
        var envPath = FindEnvFile(currentDir);
        if (envPath != null)
        {
            DotNetEnv.Env.Load(envPath);
        }

        _apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrEmpty(_apiKey))
        {
            _anthropicHttp = new HttpClient();
            _anthropicHttp.BaseAddress = new Uri("https://api.anthropic.com/");
            _anthropicHttp.DefaultRequestHeaders.Add("x-api-key", _apiKey);
            _anthropicHttp.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        }
    }

    private static string? FindEnvFile(string startDir)
    {
        var dir = startDir;
        while (!string.IsNullOrEmpty(dir))
        {
            var envPath = Path.Combine(dir, ".env");
            if (File.Exists(envPath))
            {
                return envPath;
            }

            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }
        return null;
    }

    public bool IsConfigured => _anthropicHttp != null;

    // ---------------------------------------------------------------- Cache

    /// <summary>DB cache, else an embedded ID3/Vorbis lyrics tag (unsynced). Never touches the network.</summary>
    public LyricsRecord? GetCached(string path)
    {
        var cached = DatabaseService.Instance.GetLyrics(path);
        if (cached != null) return cached;

        try
        {
            using var tag = TagLib.File.Create(path);
            var embedded = tag.Tag.Lyrics;
            if (!string.IsNullOrWhiteSpace(embedded))
            {
                return new LyricsRecord { FullPath = path, Plain = embedded, Lrc = null, Source = "embedded", Synced = false };
            }
        }
        catch { /* unreadable/unsupported file — no embedded lyrics */ }
        return null;
    }

    public void Save(string path, string? plain, string? lrc, string source)
        => DatabaseService.Instance.SaveLyrics(path, plain, lrc, source);

    public void Delete(string path) => DatabaseService.Instance.DeleteLyrics(path);

    // --------------------------------------------------------- Online sources

    /// <summary>Walks the free, no-key sources in order and returns the first synced (LRC) hit,
    /// falling back to the first plain-text hit if none of them has timings. A source that
    /// positively flags the track as instrumental short-circuits the whole chain — there are no
    /// lyrics to find, so the caller must not spend an AI call or a whisper run on it.
    ///
    /// Order is cheapest-and-most-trusted first: LRCLIB (community-curated), then LRCMux (an
    /// aggregator that fans out to Genius/KuGou/Musixmatch/NetEase/YouTube Music), then SyncLRC.
    /// </summary>
    public async Task<LyricsRecord?> FetchOnlineAsync(string? artist, string? title, string? album, int durationSec)
    {
        var sources = new Func<string?, string?, string?, int, Task<LyricsRecord?>>[]
        {
            FetchLrcLibAsync, FetchLrcMuxAsync, FetchSyncLrcAsync
        };

        LyricsRecord? plainFallback = null;
        foreach (var fetch in sources)
        {
            var rec = await fetch(artist, title, album, durationSec);
            if (rec == null) continue;
            if (rec.Instrumental) return rec;
            if (!string.IsNullOrWhiteSpace(rec.Lrc)) return rec;
            plainFallback ??= rec;
        }
        return plainFallback;
    }

    // ---------------------------------------------------------------- LRCLIB

    /// <summary>Free, no-key lyrics lookup. Tries the exact `get` match first, then falls back
    /// to fuzzy `search`. Returns null on no match / network failure.</summary>
    public async Task<LyricsRecord?> FetchLrcLibAsync(string? artist, string? title, string? album, int durationSec)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        try
        {
            var url = "https://lrclib.net/api/get" +
                       $"?artist_name={Uri.EscapeDataString(artist ?? string.Empty)}" +
                       $"&track_name={Uri.EscapeDataString(title)}" +
                       $"&album_name={Uri.EscapeDataString(album ?? string.Empty)}" +
                       $"&duration={durationSec}";
            var resp = await ApiHttp.GetAsync(url);
            if (resp.IsSuccessStatusCode)
            {
                var rec = ParseLrcLibItem(JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement);
                if (rec != null) return rec;
            }

            // Fallback: fuzzy search, first hit that actually carries lyrics.
            var q = Uri.EscapeDataString($"{artist} {title}".Trim());
            var searchResp = await ApiHttp.GetAsync($"https://lrclib.net/api/search?q={q}");
            if (!searchResp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await searchResp.Content.ReadAsStringAsync());
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var rec = ParseLrcLibItem(item);
                if (rec != null) return rec;
            }
            return null;
        }
        catch { return null; }
    }

    private static LyricsRecord? ParseLrcLibItem(JsonElement el)
    {
        if (el.TryGetProperty("instrumental", out var inst) && inst.ValueKind == JsonValueKind.True)
            return new LyricsRecord { Source = "lrclib", Instrumental = true };

        var synced = el.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        var plain = el.TryGetProperty("plainLyrics", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        if (string.IsNullOrWhiteSpace(synced) && string.IsNullOrWhiteSpace(plain)) return null;
        return new LyricsRecord { Lrc = synced, Plain = plain, Source = "lrclib" };
    }

    // ---------------------------------------------------------------- LRCMux

    /// <summary>LRCMux (https://lrcmux.dev) — a free, no-key aggregator that queries Genius,
    /// KuGou, Musixmatch, NetEase and YouTube Music and returns whichever result is best. Its
    /// JSON carries one entry per line with a `start` offset in ms; `meta.level` says whether
    /// those offsets are real (`word`/`line`) or absent, and word-level timing is flattened to
    /// one timestamp per line, which is all our LRC format holds.</summary>
    public async Task<LyricsRecord?> FetchLrcMuxAsync(string? artist, string? title, string? album, int durationSec)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        try
        {
            var url = "https://api.lrcmux.dev/get" +
                      $"?artist={Uri.EscapeDataString(artist ?? string.Empty)}" +
                      $"&title={Uri.EscapeDataString(title)}" +
                      "&format=json";
            var resp = await ApiHttp.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (!root.TryGetProperty("meta", out var meta)) return null;

            if (meta.TryGetProperty("instrumental", out var inst) && inst.ValueKind == JsonValueKind.True)
                return new LyricsRecord { Source = "lrcmux", Instrumental = true };

            if (!root.TryGetProperty("lines", out var linesEl) || linesEl.ValueKind != JsonValueKind.Array) return null;

            var timed = new List<LyricLine>();
            var plain = new StringBuilder();
            foreach (var line in linesEl.EnumerateArray())
            {
                var text = line.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                plain.Append(text).Append('\n');
                if (line.TryGetProperty("start", out var st) && st.TryGetInt64(out var ms))
                    timed.Add(new LyricLine { Time = TimeSpan.FromMilliseconds(ms), Text = text });
            }

            var plainText = plain.ToString().Trim();
            if (plainText.Length == 0) return null;

            var level = meta.TryGetProperty("level", out var lv) ? lv.GetString() : null;
            var lrc = (level is "word" or "line") && timed.Count > 0 ? BuildLrc(timed) : null;
            return new LyricsRecord { Lrc = lrc, Plain = plainText, Source = "lrcmux" };
        }
        catch { return null; }
    }

    // --------------------------------------------------------------- SyncLRC

    /// <summary>SyncLRC (https://synclrc.dev) — free, no-key, and hands back an LRC blob
    /// directly in `synced`, so it feeds <see cref="ParseLrc"/> as-is. Its word-level `karaoke`
    /// field is ignored; our LRC format only holds one timestamp per line.</summary>
    public async Task<LyricsRecord?> FetchSyncLrcAsync(string? artist, string? title, string? album, int durationSec)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        try
        {
            var url = "https://api.synclrc.dev/lyrics" +
                      $"?track={Uri.EscapeDataString(title)}" +
                      $"&artist={Uri.EscapeDataString(artist ?? string.Empty)}";
            var resp = await ApiHttp.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            if (root.TryGetProperty("instrumental", out var inst) && inst.ValueKind == JsonValueKind.True)
                return new LyricsRecord { Source = "synclrc", Instrumental = true };

            var synced = root.TryGetProperty("synced", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            var plain = root.TryGetProperty("plain", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

            // Plain-only consumers (the paste box, whisper) still need text when the source
            // only shipped an LRC blob — flatten it rather than dropping the hit.
            if (string.IsNullOrWhiteSpace(plain) && !string.IsNullOrWhiteSpace(synced))
                plain = string.Join("\n", ParseLrc(synced).Select(l => l.Text));

            if (string.IsNullOrWhiteSpace(synced) && string.IsNullOrWhiteSpace(plain)) return null;
            return new LyricsRecord { Lrc = synced, Plain = plain, Source = "synclrc" };
        }
        catch { return null; }
    }

    // -------------------------------------------------------------------- AI

    // Claude legitimately won't reproduce full copyrighted lyrics for a song it can't confirm is
    // in the public domain / licensed for reproduction — that's correct behavior, not a bug to
    // route around. These are the phrasings a refusal tends to use in prose on the rare occasion
    // the model answers in plain text instead of calling provide_lyrics (see SendLyricsQueryAsync's
    // system prompt); caught as a backstop so a refusal never gets saved, synced, or
    // karaoke-displayed as if it were real lyrics.
    private static readonly string[] RefusalMarkers =
    {
        "cannot reproduce", "can't reproduce", "protected by copyright", "violate copyright",
        "copyright protection", "copyrighted", "legally access", "licensed lyrics",
        "i appreciate your request", "i cannot provide the", "unable to provide the",
        "azlyrics", "where you can read", "purchase the song",
    };

    /// <summary>AI web-search fallback for plain lyrics — costs an API call, so callers should
    /// only reach this after an LRCLIB miss. Mirrors ArtistInfoService's REST call shape. Uses a
    /// structured-output tool (provide_lyrics) instead of parsing prose, so assistant commentary
    /// ("I found partial lyrics, but...") can never leak in as if it were a lyric line — we only
    /// ever read the tool's own input, never the surrounding text.</summary>
    public async Task<(string text, bool confident, bool available)> FetchAiPlainAsync(string? artist, string? title, string? album, string fileName)
    {
        if (_anthropicHttp == null)
            return ("API key not configured. Add ANTHROPIC_API_KEY to .env file.", false, false);

        var contextBuilder = new StringBuilder();
        contextBuilder.AppendLine("I need the full lyrics for a song. Here's the information from the audio file:");
        contextBuilder.AppendLine();
        if (!string.IsNullOrWhiteSpace(artist)) contextBuilder.AppendLine($"- Artist: {artist}");
        if (!string.IsNullOrWhiteSpace(title)) contextBuilder.AppendLine($"- Song title: {title}");
        if (!string.IsNullOrWhiteSpace(album)) contextBuilder.AppendLine($"- Album: {album}");
        var cleanFileName = Path.GetFileNameWithoutExtension(fileName);
        if (!string.IsNullOrWhiteSpace(cleanFileName)) contextBuilder.AppendLine($"- File name: {cleanFileName}");
        contextBuilder.AppendLine();
        contextBuilder.AppendLine("Please use web search to find the lyrics for this song.");

        try
        {
            var responseBody = await SendLyricsQueryAsync(contextBuilder.ToString());
            using var doc = JsonDocument.Parse(responseBody);

            if (doc.RootElement.TryGetProperty("content", out var contentArray))
            {
                foreach (var block in contentArray.EnumerateArray())
                {
                    // The server web_search blocks are server_tool_use/web_search_tool_result, so
                    // they never collide with our own client tool_use block here.
                    if (block.TryGetProperty("type", out var t) && t.GetString() == "tool_use" &&
                        block.TryGetProperty("name", out var n) && n.GetString() == "provide_lyrics" &&
                        block.TryGetProperty("input", out var input))
                    {
                        var found = input.TryGetProperty("found", out var f) && f.ValueKind == JsonValueKind.True;
                        var lyrics = input.TryGetProperty("lyrics", out var l) && l.ValueKind == JsonValueKind.String
                            ? l.GetString() ?? ""
                            : "";
                        return (lyrics, found, found && !string.IsNullOrWhiteSpace(lyrics));
                    }
                }
            }

            // Fallback: the model answered in plain text instead of calling provide_lyrics.
            // Apply the same refusal heuristic as before to keep a prose refusal from being
            // treated as real lyrics.
            var text = ExtractTextFromResponse(responseBody);
            var available = !RefusalMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
            return available ? (text, false, true) : ("", false, false);
        }
        catch (Exception ex)
        {
            return ($"Error: {ex.Message}", false, false);
        }
    }

    private async Task<string> SendLyricsQueryAsync(string prompt)
    {
        var requestBody = new
        {
            model = "claude-haiku-4-5",
            max_tokens = 2000,
            system = "You help locate song lyrics that are publicly published on lyrics websites. " +
                     "Use web search to find the song's lyrics. When finished, ALWAYS call the " +
                     "provide_lyrics tool exactly once: if you located the lyrics, set found=true and " +
                     "put the lyric lines in `lyrics` (one line per line, plain text, no commentary, " +
                     "numbering, or section headers unless they are part of the lyrics); if you could " +
                     "not find them or cannot provide them, set found=false and leave lyrics empty.",
            messages = new[]
            {
                new { role = "user", content = prompt }
            },
            tools = new object[]
            {
                new
                {
                    type = "web_search_20250305",
                    name = "web_search",
                    max_uses = 3
                },
                new
                {
                    name = "provide_lyrics",
                    description = "Return the located song lyrics, or indicate none were found.",
                    input_schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            found = new { type = "boolean", description = "true if the lyrics were located and are provided" },
                            lyrics = new { type = "string", description = "the lyric lines, one per line, plain text, no commentary/numbering/section-headers unless part of the lyrics; empty if not found" }
                        },
                        required = new[] { "found", "lyrics" }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _anthropicHttp!.PostAsync("v1/messages", content);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new Exception($"API error: {response.StatusCode}");

        return responseBody;
    }

    private static string ExtractTextFromResponse(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("content", out var contentArray))
            {
                var textParts = new StringBuilder();

                foreach (var block in contentArray.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var typeElement) &&
                        typeElement.GetString() == "text" &&
                        block.TryGetProperty("text", out var textElement))
                    {
                        textParts.Append(textElement.GetString());
                    }
                }

                var result = textParts.ToString().Trim();
                if (!string.IsNullOrEmpty(result))
                {
                    return result;
                }
            }

            return "Could not parse response.";
        }
        catch
        {
            return "Could not parse response.";
        }
    }

    // ---------------------------------------------------------------- LRC

    // Matches one or more leading [mm:ss.xx] tags (LRC allows several per line, for
    // repeated/backing lines); the fractional part is 1-3 digits, normalized to ms.
    // Minutes take 1-3 digits, not 2: LRC has no hours field, so a long-form track's timestamps
    // just keep counting minutes ([83:15.00], and past [100:00.00] for drone/doom album-length
    // single tracks). Seconds stay exactly 2 so a 3-digit minute can't be mis-split.
    private static readonly Regex LrcTagPattern = new(@"\[(\d{1,3}):(\d{2})(?:\.(\d{1,3}))?\]", RegexOptions.Compiled);

    /// <summary>Parses a standard LRC blob into timestamp-ordered lines. Lines without a
    /// recognized time tag (metadata tags like [ar:], blank lines) are skipped.</summary>
    public static List<LyricLine> ParseLrc(string? lrc)
    {
        var lines = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(lrc)) return lines;

        foreach (var raw in lrc.Replace("\r\n", "\n").Split('\n'))
        {
            var matches = LrcTagPattern.Matches(raw);
            if (matches.Count == 0) continue;
            var text = LrcTagPattern.Replace(raw, string.Empty).Trim();
            foreach (Match m in matches)
            {
                var minutes = int.Parse(m.Groups[1].Value);
                var seconds = int.Parse(m.Groups[2].Value);
                var frac = m.Groups[3].Success ? m.Groups[3].Value.PadRight(3, '0')[..3] : "000";
                lines.Add(new LyricLine { Time = new TimeSpan(0, 0, minutes, seconds, int.Parse(frac)), Text = text });
            }
        }
        return lines.OrderBy(l => l.Time).ToList();
    }

    /// <summary>Builds a standard `[mm:ss.xx] text` LRC blob from lyric lines (order not required).</summary>
    public static string BuildLrc(IEnumerable<LyricLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var l in lines.OrderBy(l => l.Time))
            sb.Append('[').Append(LyricLine.FormatLrcTime(l.Time)).Append(']').Append(l.Text).Append('\n');
        return sb.ToString();
    }

#if DEBUG
    // Self-check: ParseLrc(BuildLrc(x)) round-trips and stays timestamp-ordered. Runs once,
    // the first time this class is touched (lazy static cctor) — no test framework needed.
    private static void SelfCheckParseLrc()
    {
        var sample = new List<LyricLine>
        {
            new() { Time = TimeSpan.FromSeconds(10.5), Text = "Second line" },
            new() { Time = TimeSpan.FromSeconds(1.25), Text = "First line" },
            new() { Time = TimeSpan.FromSeconds(65.0), Text = "Third line" },
            // Past the hour: TimeSpan's "mm" wraps here (1:23:15 → "23:15"), which used to move
            // every late line an hour earlier on album-length single tracks.
            new() { Time = new TimeSpan(1, 23, 15), Text = "Long-form line" },
        };
        var parsed = ParseLrc(BuildLrc(sample));
        var expectedOrder = sample.OrderBy(l => l.Time).ToList();

        Debug.Assert(parsed.Count == expectedOrder.Count, "LyricsService self-check: line count mismatch");
        for (int i = 0; i < parsed.Count; i++)
        {
            Debug.Assert(Math.Abs((parsed[i].Time - expectedOrder[i].Time).TotalMilliseconds) < 15,
                "LyricsService self-check: timestamp round-trip drift");
            Debug.Assert(parsed[i].Text == expectedOrder[i].Text, "LyricsService self-check: text mismatch");
        }
        for (int i = 1; i < parsed.Count; i++)
            Debug.Assert(parsed[i].Time >= parsed[i - 1].Time, "LyricsService self-check: not monotonic");
    }
#endif
}
