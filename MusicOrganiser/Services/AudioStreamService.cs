using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace MusicOrganiser.Services;

// Transcodes tracks to AAC (.m4a) on demand and caches them so the mobile app can
// stream compressed audio over VPN/mobile data. Reuses AudioFileReader (same decoder
// as playback), so anything that plays on the desktop can be streamed.
public static class AudioStreamService
{
    // Windows MediaFoundation AAC-LC supports up to 192 kbps at 44.1/48 kHz.
    private static readonly int[] SupportedBitrates = { 96000, 128000, 160000, 192000 };
    public const int DefaultBitrate = 160000;

    private static readonly string CacheDir = Path.Combine(
        Path.GetTempPath(), "MusicOrganiser", "stream");

    // Serializes transcodes of the same cache key so two requests don't both encode.
    private static readonly ConcurrentDictionary<string, object> Locks = new();

    static AudioStreamService()
    {
        try { MediaFoundationApi.Startup(); } catch { /* already started / unavailable */ }
    }

    public static int ClampBitrate(int bitrate)
    {
        if (bitrate <= 0) return DefaultBitrate;
        var best = SupportedBitrates[0];
        var bestDiff = Math.Abs(bitrate - best);
        foreach (var b in SupportedBitrates)
        {
            var diff = Math.Abs(bitrate - b);
            if (diff < bestDiff) { best = b; bestDiff = diff; }
        }
        return best;
    }

    // Returns the path to a cached .m4a for (sourcePath, bitrate), transcoding if needed.
    // Throws if the source is missing or cannot be decoded/encoded.
    public static string GetOrCreateAac(string sourcePath, int bitrate)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("source not found", sourcePath);

        bitrate = ClampBitrate(bitrate);
        var mtime = File.GetLastWriteTimeUtc(sourcePath).Ticks;
        var key = Hash($"{sourcePath}|{mtime}|{bitrate}");
        var cachePath = Path.Combine(CacheDir, key + ".m4a");

        if (File.Exists(cachePath)) return cachePath;

        lock (Locks.GetOrAdd(key, _ => new object()))
        {
            if (File.Exists(cachePath)) return cachePath; // won the race meanwhile
            Directory.CreateDirectory(CacheDir);
            // Temp must keep the .m4a extension: MediaFoundation picks the container
            // format from the file extension, and ".partial" makes the sink writer fail.
            var partial = Path.Combine(CacheDir, $"{key}.{Guid.NewGuid():N}.m4a");
            try
            {
                using (var reader = new AudioFileReader(sourcePath))
                {
                    MediaFoundationEncoder.EncodeToAac(reader.ToWaveProvider16(), partial, bitrate);
                }
                File.Move(partial, cachePath, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(partial)) File.Delete(partial); } catch { }
                throw;
            }
            return cachePath;
        }
    }

    // Fire-and-forget warm of the cache so the phone's request hits a ready file.
    public static void Prewarm(string sourcePath, int bitrate)
    {
        _ = Task.Run(() =>
        {
            try { GetOrCreateAac(sourcePath, bitrate); } catch { /* best effort */ }
        });
    }

    public static void ClearCache()
    {
        try { if (Directory.Exists(CacheDir)) Directory.Delete(CacheDir, recursive: true); }
        catch { /* best effort */ }
    }

    private static string Hash(string s)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(s));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
