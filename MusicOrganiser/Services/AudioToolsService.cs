using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace MusicOrganiser.Services;

// Offline audio tools: convert a track to MP3, and cut an album image into per-track FLACs
// using a .cue sheet. Both only READ the source (never StopAndReleaseFile), so they work on the
// currently playing file, and neither deletes or overwrites anything. Blocking: call off the UI thread.
public static class AudioToolsService
{
    // ponytail: one fixed bitrate, no picker. Upgrade path: a bitrate parameter + UI choice.
    private const int Mp3Bitrate = 320000;

    // MFAudioFormat_FLAC - not in NAudio's AudioSubtypes. Needs MSFlacEncoder (ships with Windows 10).
    private static readonly Guid FlacSubtype = new("0000F1AC-0000-0010-8000-00AA00389B71");

    static AudioToolsService()
    {
        try { MediaFoundationApi.Startup(); } catch { /* already started / unavailable */ }
        // Needed for the ANSI fallback when a .cue isn't UTF-8 (.NET 8 has no legacy code pages by default).
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#if DEBUG
        SelfCheckParseCue();
#endif
    }

    // ---------------------------------------------------------------- Convert to MP3

    // Writes <same folder>/<same name>.mp3 and returns its path. Throws (never overwrites) if it exists.
    public static string ConvertToMp3(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("Source file not found.", sourcePath);
        if (string.Equals(Path.GetExtension(sourcePath), ".mp3", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"'{Path.GetFileName(sourcePath)}' is already an MP3.");

        var target = Path.ChangeExtension(sourcePath, ".mp3");
        if (File.Exists(target))
            throw new IOException($"'{Path.GetFileName(target)}' already exists.");

        // Temp must keep the .mp3 extension (MediaFoundation picks the container from it).
        var temp = Path.Combine(Path.GetDirectoryName(sourcePath)!, $"~conv-{Guid.NewGuid():N}.mp3");
        try
        {
            using (var reader = new AudioFileReader(sourcePath))
            {
                var ch = reader.WaveFormat.Channels;
                // ponytail: mono/stereo only. Upgrade path: downmix multichannel with a ChannelMixer.
                if (ch != 1 && ch != 2)
                    throw new NotSupportedException($"{ch}-channel audio is not supported (mono/stereo only).");

                IWaveProvider pcm = reader.ToWaveProvider16();
                var rate = reader.WaveFormat.SampleRate;
                // The MF MP3 encoder only takes 32/44.1/48 kHz: 88.2/176.4 -> 44.1, other hi-res -> 48.
                var targetRate = rate is 32000 or 44100 or 48000 ? rate
                    : rate % 44100 == 0 ? 44100
                    : rate > 48000 ? 48000 : 44100;
                MediaFoundationResampler? resampler = null;
                try
                {
                    if (targetRate != rate)
                    {
                        resampler = new MediaFoundationResampler(pcm, new WaveFormat(targetRate, 16, ch));
                        pcm = resampler;
                    }
                    MediaFoundationEncoder.EncodeToMp3(pcm, temp, Mp3Bitrate);
                }
                finally { resampler?.Dispose(); }
            }
            File.Move(temp, target);   // no overwrite: throws if something appeared meanwhile
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }

        try
        {
            using var src = TagLib.File.Create(sourcePath);
            using var dst = TagLib.File.Create(target);
            src.Tag.CopyTo(dst.Tag, true);
            dst.Tag.Pictures = src.Tag.Pictures;
            dst.Save();
        }
        catch { /* tags are best effort - the audio is already converted */ }
        return target;
    }

    // ---------------------------------------------------------------- Split by .cue

    // Finds the cue sheet that belongs to an audio file, or null.
    public static string? FindCue(string audioPath)
    {
        var dir = Path.GetDirectoryName(audioPath);
        if (dir == null) return null;
        var baseName = Path.GetFileNameWithoutExtension(audioPath);
        foreach (var name in new[] { baseName + ".cue", Path.GetFileName(audioPath) + ".cue" })
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        string[] cues;
        try { cues = Directory.GetFiles(dir, "*.cue"); } catch { return null; }
        foreach (var cue in cues)
        {
            try
            {
                var sheet = ParseCue(ReadCueText(cue));
                if (sheet.Files.Any(f => SameStem(f.Name, baseName))) return cue;
            }
            catch { /* unreadable cue - skip */ }
        }
        return cues.Length == 1 ? cues[0] : null;
    }

    // Cuts audioPath into NN - Title.flac files next to it and returns their paths.
    // ponytail: .ape/.wv images are not supported - the decoder (MediaFoundation) cannot read them.
    // ponytail: output is always FLAC (lossless); run Convert to MP3 on the pieces for MP3.
    public static List<string> SplitByCue(string audioPath, string? cuePath = null)
    {
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            throw new FileNotFoundException("Source file not found.", audioPath);
        cuePath ??= FindCue(audioPath)
            ?? throw new FileNotFoundException("No .cue sheet found next to this file.");

        var sheet = ParseCue(ReadCueText(cuePath));
        var baseName = Path.GetFileNameWithoutExtension(audioPath);
        var cueFile = sheet.Files.FirstOrDefault(f => SameStem(f.Name, baseName))
            ?? (sheet.Files.Count == 1 ? sheet.Files[0] : null)
            ?? throw new InvalidOperationException("The .cue has several FILE entries and none matches this audio file.");
        var tracks = cueFile.Tracks.Where(t => t.Frames >= 0).ToList();
        if (tracks.Count == 0)
            throw new InvalidOperationException("The .cue has no tracks with an INDEX 01 for this file.");
        for (int i = 1; i < tracks.Count; i++)
            if (tracks[i].Frames <= tracks[i - 1].Frames)
                throw new InvalidOperationException("The .cue track times are not increasing.");

        // Check every target name before writing anything.
        var dir = Path.GetDirectoryName(audioPath)!;
        var targets = tracks.Select(t => Path.Combine(dir, TrackFileName(t))).ToList();
        if (targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count)
            throw new InvalidOperationException("Two cue tracks would get the same file name.");
        var clash = targets.FirstOrDefault(File.Exists);
        if (clash != null)
            throw new IOException($"'{Path.GetFileName(clash)}' already exists.");

        IList<TagLib.IPicture> pictures = Array.Empty<TagLib.IPicture>();
        try { using var tf = TagLib.File.Create(audioPath); pictures = tf.Tag.Pictures; } catch { }

        var created = new List<string>();
        // Decode ONCE, sequentially, at the native format: slicing is then exact to the sample, no seeking.
        using var reader = new MediaFoundationReader(audioPath);
        IWaveProvider pcm = reader;
        if (pcm.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
            pcm = new WaveFloatTo16Provider(pcm);
        var fmt = pcm.WaveFormat;
        // ponytail: 16/24-bit PCM only (what the MF FLAC encoder takes). Upgrade path: requantize others to 24.
        if (fmt.Encoding != WaveFormatEncoding.Pcm || (fmt.BitsPerSample != 16 && fmt.BitsPerSample != 24))
            throw new NotSupportedException($"Unsupported audio format for splitting: {fmt}.");

        long blockAlign = fmt.BlockAlign;
        long Offset(CueTrack t) => t.Frames * fmt.SampleRate / 75 * blockAlign;   // bytes into the stream

        // Everything before track 1's INDEX 01 (pregap / hidden audio) is discarded.
        long cursor = 0;
        Skip(pcm, Offset(tracks[0]), ref cursor);

        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            long length = i + 1 < tracks.Count ? Offset(tracks[i + 1]) - Offset(t) : long.MaxValue;
            var temp = Path.Combine(dir, $"~split-{Guid.NewGuid():N}.flac");
            try
            {
                var take = new TakeProvider(pcm, length);
                EncodeFlac(take, temp);
                cursor += take.Taken;
                if (take.Taken == 0)
                    throw new InvalidOperationException($"Track {t.Number} starts past the end of the audio.");
                TagTrack(temp, sheet, t, tracks.Count, pictures);
                File.Move(temp, targets[i]);
                created.Add(targets[i]);
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                throw;   // tracks already finished stay
            }
        }
        return created;
    }

    private static void Skip(IWaveProvider src, long bytes, ref long cursor)
    {
        var buf = new byte[65536 - 65536 % src.WaveFormat.BlockAlign];
        while (bytes > 0)
        {
            int n = src.Read(buf, 0, (int)Math.Min(buf.Length, bytes));
            if (n <= 0) break;
            bytes -= n;
            cursor += n;
        }
    }

    // MediaFoundation FLAC sink. SelectMediaType() is NOT used: it returns the first FLAC type, which is
    // 8-bit, and the writer then rejects 16/24-bit input - so the output type is built to match the input.
    private static void EncodeFlac(IWaveProvider input, string path)
    {
        var f = input.WaveFormat;
        var raw = MediaFoundationApi.CreateMediaType();
        var mt = new MediaType(raw)
        {
            MajorType = MediaTypes.MFMediaType_Audio,
            SubType = FlacSubtype,
            SampleRate = f.SampleRate,
            ChannelCount = f.Channels,
            BitsPerSample = f.BitsPerSample,
        };
        raw.SetUINT32(MediaFoundationAttributes.MF_MT_AUDIO_BLOCK_ALIGNMENT, f.BlockAlign);
        raw.SetUINT32(MediaFoundationAttributes.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, f.AverageBytesPerSecond);
        using var enc = new MediaFoundationEncoder(mt);
        enc.Encode(path, input);
    }

    private static void TagTrack(string path, CueSheet sheet, CueTrack t, int count,
        IList<TagLib.IPicture> pictures)
    {
        try
        {
            using var f = TagLib.File.Create(path);
            if (!string.IsNullOrWhiteSpace(t.Title)) f.Tag.Title = t.Title;
            var artist = t.Performer ?? sheet.Performer;
            if (artist != null) f.Tag.Performers = new[] { artist };
            if (sheet.Performer != null) f.Tag.AlbumArtists = new[] { sheet.Performer };
            if (sheet.Title != null) f.Tag.Album = sheet.Title;
            f.Tag.Track = (uint)t.Number;
            f.Tag.TrackCount = (uint)count;
            if (sheet.Date != null && sheet.Date.Length >= 4 && uint.TryParse(sheet.Date[..4], out var year))
                f.Tag.Year = year;
            if (sheet.Genre != null) f.Tag.Genres = new[] { sheet.Genre };
            if (pictures.Count > 0) f.Tag.Pictures = pictures.ToArray();
            f.Save();
        }
        catch { /* tags are best effort */ }
    }

    private static string TrackFileName(CueTrack t)
    {
        var title = string.IsNullOrWhiteSpace(t.Title) ? $"Track {t.Number:00}" : t.Title.Trim();
        var bad = Path.GetInvalidFileNameChars();
        var safe = new string(title.Select(c => Array.IndexOf(bad, c) >= 0 ? '_' : c).ToArray())
            .TrimEnd('.', ' ');
        if (safe.Length > 120) safe = safe[..120].TrimEnd('.', ' ');
        if (safe.Length == 0) safe = $"Track {t.Number:00}";
        return $"{t.Number:00} - {safe}.flac";
    }

    // Hands out at most N bytes from a shared reader, so consecutive tracks consume it back to back.
    private sealed class TakeProvider : IWaveProvider
    {
        private readonly IWaveProvider _src;
        private long _left;
        public long Taken { get; private set; }
        public TakeProvider(IWaveProvider src, long bytes) { _src = src; _left = bytes; }
        public WaveFormat WaveFormat => _src.WaveFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            if (_left <= 0) return 0;
            count = (int)Math.Min(count, _left);
            int total = 0;
            while (total < count)
            {
                int n = _src.Read(buffer, offset + total, count - total);
                if (n <= 0) break;
                total += n;
            }
            _left -= total;
            Taken += total;
            return total;
        }
    }

    // ---------------------------------------------------------------- cue parsing

    private sealed class CueTrack
    {
        public int Number;
        public string? Title, Performer;
        public long Frames = -1;   // INDEX 01 in 1/75 s frames; -1 = none
    }

    private sealed class CueFile
    {
        public string Name = "";
        public List<CueTrack> Tracks = new();
    }

    private sealed class CueSheet
    {
        public string? Title, Performer, Date, Genre;
        public List<CueFile> Files = new();
    }

    // Strict UTF-8 (BOM honoured), else the system ANSI code page.
    // ponytail: no charset detection - a cue in e.g. Shift-JIS on a Western machine reads as mojibake.
    private static string ReadCueText(string cuePath)
    {
        var bytes = File.ReadAllBytes(cuePath);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try { return new UTF8Encoding(false, true).GetString(bytes, skip, bytes.Length - skip); }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage)
                .GetString(bytes, skip, bytes.Length - skip);
        }
    }

    private static CueSheet ParseCue(string text)
    {
        var sheet = new CueSheet();
        CueFile? file = null;
        CueTrack? track = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            int sp = line.IndexOf(' ');
            if (sp < 0) continue;
            var key = line[..sp].ToUpperInvariant();
            var rest = line[(sp + 1)..].Trim();
            switch (key)
            {
                case "REM":
                    int sp2 = rest.IndexOf(' ');
                    if (sp2 < 0) break;
                    var remKey = rest[..sp2].ToUpperInvariant();
                    var remVal = Unquote(rest[(sp2 + 1)..]);
                    if (track == null && file == null)
                    {
                        if (remKey == "DATE") sheet.Date = remVal;
                        else if (remKey == "GENRE") sheet.Genre = remVal;
                    }
                    break;
                case "PERFORMER":
                    if (track != null) track.Performer = Unquote(rest); else if (file == null) sheet.Performer = Unquote(rest);
                    break;
                case "TITLE":
                    if (track != null) track.Title = Unquote(rest); else if (file == null) sheet.Title = Unquote(rest);
                    break;
                case "FILE":
                    file = new CueFile { Name = Unquote(rest.StartsWith('"') ? rest[..(rest.IndexOf('"', 1) + 1)] : rest.Split(' ')[0]) };
                    sheet.Files.Add(file);
                    track = null;
                    break;
                case "TRACK":
                    var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (file != null && parts.Length >= 2 && int.TryParse(parts[0], out var num)
                        && parts[1].Equals("AUDIO", StringComparison.OrdinalIgnoreCase))
                    {
                        track = new CueTrack { Number = num };
                        file.Tracks.Add(track);
                    }
                    else track = null;
                    break;
                case "INDEX":
                    var ip = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (track != null && ip.Length >= 2 && ip[0] == "01")
                    {
                        var t = ip[1].Split(':');
                        if (t.Length == 3 && long.TryParse(t[0], out var m) && long.TryParse(t[1], out var s)
                            && long.TryParse(t[2], out var fr))
                            track.Frames = (m * 60 + s) * 75 + fr;
                    }
                    break;
            }
        }
        return sheet;
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;
    }

    // Cues often say .wav while the file is .flac, so compare names without the extension.
    private static bool SameStem(string cueFileName, string audioBaseName) =>
        string.Equals(Path.GetFileNameWithoutExtension(cueFileName), audioBaseName, StringComparison.OrdinalIgnoreCase);

#if DEBUG
    // Self-check: a small cue parses to the expected tracks/times. Runs once, with the static ctor.
    private static void SelfCheckParseCue()
    {
        const string cue = "REM GENRE \"Doom\"\nREM DATE 2019\nPERFORMER \"Band\"\nTITLE \"Album\"\n" +
            "FILE \"album.wav\" WAVE\n  TRACK 01 AUDIO\n    TITLE \"One\"\n    INDEX 00 00:00:00\n    INDEX 01 00:02:00\n" +
            "  TRACK 02 AUDIO\n    TITLE Two\n    PERFORMER \"Guest\"\n    INDEX 01 83:15:37\n";
        var sheet = ParseCue(cue);
        Debug.Assert(sheet.Files.Count == 1 && sheet.Files[0].Tracks.Count == 2, "AudioToolsService self-check: track count");
        var t = sheet.Files[0].Tracks;
        Debug.Assert(t[0].Title == "One" && t[1].Title == "Two" && t[1].Performer == "Guest", "AudioToolsService self-check: titles");
        Debug.Assert(sheet.Performer == "Band" && sheet.Title == "Album" && sheet.Date == "2019" && sheet.Genre == "Doom", "AudioToolsService self-check: header");
        Debug.Assert(t[0].Frames == 150, "AudioToolsService self-check: track 1 frames");
        Debug.Assert(t[1].Frames == (83 * 60 + 15) * 75 + 37, "AudioToolsService self-check: 83:15:37 frames");
        Debug.Assert(t[1].Frames * 44100 / 75 == 220301256, "AudioToolsService self-check: sample offset");
    }
#endif
}
