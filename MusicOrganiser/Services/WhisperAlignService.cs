using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MusicOrganiser.Models;
using NAudio.MediaFoundation;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;

namespace MusicOrganiser.Services;

/// <summary>
/// Fallback lyric sync: transcribes the track with Whisper.net and force-aligns the known
/// plain lyrics against the transcript to produce a synced LRC. Only used on request (the
/// Sync button) — LRCLIB has no match and transcription is slow.
/// ponytail: base ggml model + naive word-LCS alignment, no vocal isolation. Upgrade path:
/// medium model / Demucs vocal isolation / whisperX forced alignment if quality falls short.
/// </summary>
public class WhisperAlignService
{
    private static readonly string ModelDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MusicOrganiser", "models");
    private static readonly string ModelPath = Path.Combine(ModelDir, "ggml-base.bin");

    private static readonly SemaphoreSlim ModelDownloadLock = new(1, 1);

    static WhisperAlignService()
    {
        try { MediaFoundationApi.Startup(); } catch { /* already started / unavailable */ }
#if DEBUG
        SelfCheckMonotonic();
#endif
    }

    public async Task<string> AlignAsync(string audioPath, string plainLyrics, IProgress<string>? progress, CancellationToken ct)
    {
        await EnsureModelAsync(progress, ct);

        progress?.Report("Decoding audio...");
        var samples = await Task.Run(() => DecodeToPcm16k(audioPath), ct);
        ct.ThrowIfCancellationRequested();
        var totalDuration = TimeSpan.FromSeconds(samples.Length / 16000.0);

        progress?.Report("Transcribing (this can take a while)...");
        var words = await TranscribeWordsAsync(samples, progress, ct);
        ct.ThrowIfCancellationRequested();

        progress?.Report("Aligning lyrics...");
        var lines = AlignLines(plainLyrics, words, totalDuration);

        return LyricsService.BuildLrc(lines);
    }

    private static async Task EnsureModelAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (File.Exists(ModelPath)) return;

        await ModelDownloadLock.WaitAsync(ct);
        try
        {
            if (File.Exists(ModelPath)) return; // another caller won the race while we waited
            Directory.CreateDirectory(ModelDir);
            progress?.Report("Downloading speech model (one-time, ~140MB)...");
            using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.Base, QuantizationType.NoQuantization, ct);
            var partial = ModelPath + ".partial";
            using (var fileStream = File.Create(partial))
                await modelStream.CopyToAsync(fileStream, ct);
            File.Move(partial, ModelPath, overwrite: true);
        }
        finally
        {
            ModelDownloadLock.Release();
        }
    }

    // Decodes to 16kHz mono samples via NAudio (MediaFoundation is already in-project for the
    // AAC transcoder) — the format WhisperProcessor.ProcessAsync(float[]) expects.
    private static float[] DecodeToPcm16k(string audioPath)
    {
        using var reader = new AudioFileReader(audioPath);
        var target = new WaveFormat(16000, 16, 1);
        using var resampler = new MediaFoundationResampler(reader, target);
        var provider = resampler.ToSampleProvider();

        var samples = new List<float>();
        var buffer = new float[16000];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            samples.AddRange(buffer.Take(read));
        return samples.ToArray();
    }

    private static async Task<List<(string Word, TimeSpan Time)>> TranscribeWordsAsync(
        float[] samples, IProgress<string>? progress, CancellationToken ct)
    {
        using var factory = WhisperFactory.FromPath(ModelPath);
        using var processor = factory.CreateBuilder()
            .WithLanguage("auto")
            .WithTokenTimestamps()
            .Build();

        var words = new List<(string, TimeSpan)>();
        await foreach (var segment in processor.ProcessAsync(samples, ct))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Transcribing... {segment.Start:mm\\:ss}");

            // Token-level timestamps when available (WhisperToken.Start/End are in centiseconds);
            // otherwise fall back to the segment's own start for every word in it — coarser,
            // but still fine for line-level LRC.
            if (segment.Tokens is { Length: > 0 })
            {
                foreach (var token in segment.Tokens)
                {
                    var text = token.Text?.Trim();
                    if (string.IsNullOrEmpty(text) || text.StartsWith("[_") || text.StartsWith("<|")) continue;
                    words.Add((text, TimeSpan.FromMilliseconds(token.Start * 10)));
                }
            }
            else
            {
                foreach (var w in (segment.Text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    words.Add((w, segment.Start));
            }
        }
        return words;
    }

    // ---------------------------------------------------------- Alignment

    private static readonly Regex NonWord = new(@"[^\w']+", RegexOptions.Compiled);

    private static string Normalize(string s) => NonWord.Replace(s.ToLowerInvariant(), " ").Trim();

    /// <summary>LCS-matches the plain lyric words against the whisper transcript; each lyric
    /// line gets the timestamp of its first matched word, with unmatched lines interpolated
    /// between their known neighbours (or the song start/end for a leading/trailing run with
    /// no neighbour on one side) and clamped to be non-decreasing.</summary>
    public static List<LyricLine> AlignLines(string plainLyrics, IReadOnlyList<(string Word, TimeSpan Time)> transcriptWords, TimeSpan totalDuration)
    {
        var rawLines = (plainLyrics ?? string.Empty).Replace("\r\n", "\n").Split('\n')
            .Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

        // Flatten lyric words with a back-reference to their line, for one pass over the whole
        // song rather than per-line (keeps matches moving forward across line boundaries).
        var lyricWords = new List<(string Norm, int LineIndex)>();
        for (int i = 0; i < rawLines.Count; i++)
            foreach (var w in rawLines[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var norm = Normalize(w);
                if (norm.Length > 0) lyricWords.Add((norm, i));
            }

        var transcriptNorm = transcriptWords.Select(t => Normalize(t.Word)).ToList();

        var lineTimes = new TimeSpan?[rawLines.Count];
        int tIdx = 0;
        foreach (var (norm, lineIndex) in lyricWords)
        {
            if (lineTimes[lineIndex] != null) continue; // line already has its first-match time
            var found = FindNextMatch(transcriptNorm, norm, tIdx);
            if (found < 0) continue;
            lineTimes[lineIndex] = transcriptWords[found].Time;
            tIdx = found + 1; // keep matches moving forward through the transcript
        }

        InterpolateGaps(lineTimes, totalDuration);

        var result = new List<LyricLine>(rawLines.Count);
        for (int i = 0; i < rawLines.Count; i++)
            result.Add(new LyricLine { Time = lineTimes[i] ?? TimeSpan.Zero, Text = rawLines[i] });

        EnforceMonotonic(result);
        return result;
    }

    // First match at/after `from` — a small forward-only search that keeps matches ordered
    // without the O(n*m) DP table of a true LCS; good enough for line-level sync.
    private static int FindNextMatch(List<string> transcriptNorm, string word, int from)
    {
        for (int i = from; i < transcriptNorm.Count; i++)
            if (transcriptNorm[i] == word) return i;
        return -1;
    }

    private static void InterpolateGaps(TimeSpan?[] lineTimes, TimeSpan totalDuration)
    {
        int i = 0;
        while (i < lineTimes.Length)
        {
            if (lineTimes[i] != null) { i++; continue; }

            int start = i - 1;
            int j = i;
            while (j < lineTimes.Length && lineTimes[j] == null) j++;
            int end = j;

            var before = start >= 0 ? lineTimes[start] : (TimeSpan?)null;
            var after = end < lineTimes.Length ? lineTimes[end] : (TimeSpan?)null;

            // Leading run (no earlier anchor, e.g. an unmatched instrumental intro): spread
            // from the song start instead of collapsing onto `after`. The previous `before ??=
            // after` here made before==after for this case too — same collapse bug as the
            // trailing run below, just less commonly hit since whisper usually catches the
            // first few words.
            before ??= TimeSpan.Zero;

            // Trailing run (no later anchor — whisper lost the vocals for the rest of the
            // song): spread toward the track's end instead of collapsing every remaining line
            // onto the last real match. ponytail: even spread across the unmatched tail is a
            // heuristic, not a real alignment — the user can hand-tune any line afterwards with
            // the row's ⏺ "now" button. Guard: never spread backwards past `before`.
            after ??= totalDuration > before ? totalDuration : before;

            int steps = end - start; // number of positions from `before` to `after`
            for (int k = i; k < end; k++)
            {
                var frac = steps > 0 ? (double)(k - start) / steps : 0;
                lineTimes[k] = before.Value + TimeSpan.FromTicks((long)((after.Value - before.Value).Ticks * frac));
            }
            i = end;
        }
    }

    private static void EnforceMonotonic(List<LyricLine> lines)
    {
        for (int i = 1; i < lines.Count; i++)
            if (lines[i].Time < lines[i - 1].Time)
                lines[i].Time = lines[i - 1].Time;
    }

#if DEBUG
    // Self-check: a synthetic transcript with a missing interior line, an out-of-order match,
    // and a trailing run whisper never matched at all (simulating a lost outro) still produces
    // a monotonic, non-collapsing line schedule — the trailing lines spread toward
    // totalDuration instead of all piling onto the last real match. Runs once (lazy static cctor).
    private static void SelfCheckMonotonic()
    {
        var lyrics = "hello world\nthis is a test\nfinal line here\nnever transcribed one\nnever transcribed two";
        var transcript = new List<(string, TimeSpan)>
        {
            ("hello", TimeSpan.FromSeconds(1)),
            ("world", TimeSpan.FromSeconds(1.4)),
            // "this is a test" deliberately missing from the transcript — forces interpolation.
            ("final", TimeSpan.FromSeconds(0.5)), // earlier than the previous match, on purpose
            ("line", TimeSpan.FromSeconds(5.2)),
            ("here", TimeSpan.FromSeconds(5.6)),
            // "never transcribed one/two" deliberately missing — a trailing run with no anchor
            // after it, exercising the trailing-spread path against totalDuration below.
        };
        var totalDuration = TimeSpan.FromSeconds(30); // well past the last transcript word (5.6s)
        var lines = AlignLines(lyrics, transcript, totalDuration);
        Debug.Assert(lines.Count == 5, "WhisperAlignService self-check: line count mismatch");
        for (int i = 1; i < lines.Count; i++)
            Debug.Assert(lines[i].Time >= lines[i - 1].Time, "WhisperAlignService self-check: not monotonic");
        // The unmatched tail (lines 3, 4) must be spread, not collapsed onto the last real match.
        Debug.Assert(lines[3].Time != lines[4].Time, "WhisperAlignService self-check: trailing lines not spread");
        Debug.Assert(lines[4].Time < totalDuration, "WhisperAlignService self-check: trailing line overshot totalDuration");
    }
#endif
}
