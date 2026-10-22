using System;
using System.IO;

namespace MusicOrganiser.Services;

// Minimal FLAC container parser: STREAMINFO plus a frame locator that finds a CRC-8 verified,
// chain-confirmed frame near a target sample, so FlacSeekReader can hand Media Foundation a stream
// that starts exactly at a frame boundary we chose. Based on the FLAC format spec (RFC 9639).
// ponytail: no seek-table use and no cached index - each seek estimates a byte offset from the
// average bitrate and refines it (a few 128 KB reads). Cache a per-file index if this ever shows
// up in a profile.
internal sealed class FlacInfo
{
    public int MinBlock, MaxBlock, SampleRate, Channels, Bits;
    public long TotalSamples;      // 0 = unknown
    public long AudioStart;        // file offset of the first frame
    public byte[] StreamInfo = Array.Empty<byte>();   // the 34 STREAMINFO bytes
}

internal static class FlacFrameFinder
{
    private const int ChunkSize = 128 * 1024;
    private const int MaxHeaderLen = 16;
    private const long ConfirmWindow = 1 << 20;   // how far past a candidate we look for its successor

    private static readonly byte[] Crc8Table = BuildCrc8();

    private static byte[] BuildCrc8()
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int c = i;
            for (int k = 0; k < 8; k++) c = (c & 0x80) != 0 ? (c << 1) ^ 0x07 : c << 1;
            t[i] = (byte)c;
        }
        return t;
    }

    private static readonly int[] RateTable = { 0, 88200, 176400, 192000, 8000, 16000, 22050, 24000, 32000, 44100, 48000, 96000 };
    private static readonly int[] BitsTable = { 0, 8, 12, -1, 16, 20, 24, 32 };

    private readonly record struct Frame(long Offset, int HeaderLen, bool Variable, long Number, int BlockSize);

    // Returns null when this is not a FLAC stream we understand.
    public static FlacInfo? Parse(FileStream fs)
    {
        try
        {
            var b = new byte[10];
            long pos = 0;
            while (true)
            {
                fs.Position = pos;
                if (fs.Read(b, 0, 10) < 10) return null;
                if (b[0] == 'I' && b[1] == 'D' && b[2] == '3')
                    pos += 10 + ((b[6] & 0x7F) << 21 | (b[7] & 0x7F) << 14 | (b[8] & 0x7F) << 7 | (b[9] & 0x7F)) + ((b[5] & 0x10) != 0 ? 10 : 0);
                else break;
            }
            if (b[0] != 'f' || b[1] != 'L' || b[2] != 'a' || b[3] != 'C') return null;

            var info = new FlacInfo();
            pos += 4;
            bool first = true;
            while (true)
            {
                fs.Position = pos;
                var h = new byte[4];
                if (fs.Read(h, 0, 4) < 4) return null;
                bool last = (h[0] & 0x80) != 0;
                int type = h[0] & 0x7F;
                int len = h[1] << 16 | h[2] << 8 | h[3];
                if (first)
                {
                    if (type != 0 || len != 34) return null;
                    var si = new byte[34];
                    if (fs.Read(si, 0, 34) < 34) return null;
                    info.StreamInfo = si;
                    info.MinBlock = si[0] << 8 | si[1];
                    info.MaxBlock = si[2] << 8 | si[3];
                    info.SampleRate = si[10] << 12 | si[11] << 4 | si[12] >> 4;
                    info.Channels = ((si[12] >> 1) & 7) + 1;
                    info.Bits = (((si[12] & 1) << 4) | (si[13] >> 4)) + 1;
                    info.TotalSamples = (long)(si[13] & 0xF) << 32 | (long)si[14] << 24 | (long)si[15] << 16 | (long)si[16] << 8 | si[17];
                    first = false;
                }
                pos += 4 + len;
                if (last) break;
            }
            info.AudioStart = pos;
            if (info.SampleRate <= 0 || info.MaxBlock < 16 || info.MinBlock < 16 || pos >= fs.Length) return null;
            return info;
        }
        catch { return null; }
    }

    // The bytes a spliced stream starts with: fLaC + a lone STREAMINFO (total samples and MD5
    // zeroed = unknown, since the stream no longer begins at sample 0).
    public static byte[] BuildHeader(FlacInfo info)
    {
        var h = new byte[4 + 4 + 34];
        h[0] = (byte)'f'; h[1] = (byte)'L'; h[2] = (byte)'a'; h[3] = (byte)'C';
        h[4] = 0x80; h[7] = 34;
        Array.Copy(info.StreamInfo, 0, h, 8, 34);
        h[8 + 13] &= 0xF0;
        for (int i = 14; i < 34; i++) h[8 + i] = 0;
        return h;
    }

    // Finds a frame at or before targetSample (ideally within ~4 s of it). false = none found.
    public static bool TryFind(FileStream fs, FlacInfo info, long total, long targetSample, out long frameOffset, out long frameSample)
    {
        frameOffset = 0; frameSample = 0;
        long dataLen = fs.Length - info.AudioStart;
        if (total <= 0 || dataLen <= 0) return false;
        double bps = dataLen / ((double)total / info.SampleRate);   // average bytes per second
        long back = (long)(bps * 1.5);
        long est = info.AudioStart + (long)((double)targetSample / total * dataLen);
        bool have = false;

        for (int it = 0; it < 8; it++)
        {
            long from = Math.Max(info.AudioStart, est - back);
            if (!FindConfirmed(fs, info, from, out var f))
            {
                // Ran off the end: the estimate was too far on; step back.
                if (from <= info.AudioStart) break;
                est = from - back;
                continue;
            }
            long sample = f.Variable ? f.Number : f.Number * info.MaxBlock;
            if (sample <= targetSample)
            {
                if (!have || sample > frameSample) { frameOffset = f.Offset; frameSample = sample; have = true; }
                if (targetSample - sample <= 4L * info.SampleRate || f.Offset <= info.AudioStart) return true;
            }
            est = f.Offset + (long)((double)(targetSample - sample) / info.SampleRate * bps);
        }
        return have;
    }

    // First frame header at or after `from` whose successor header is consistent with it.
    private static bool FindConfirmed(FileStream fs, FlacInfo info, long from, out Frame result)
    {
        result = default;
        var buf = new byte[ChunkSize];
        long pos = from;
        while (NextHeader(fs, info, buf, ref pos, fs.Length, out var c))
        {
            long scan = c.Offset + c.HeaderLen;
            long limit = Math.Min(fs.Length, c.Offset + ConfirmWindow);
            bool anyNext = false;
            while (NextHeader(fs, info, buf, ref scan, limit, out var n))
            {
                anyNext = true;
                if (n.Variable == c.Variable && n.Number == (c.Variable ? c.Number + c.BlockSize : c.Number + 1))
                { result = c; return true; }
                scan = n.Offset + 1;
            }
            // No successor at all up to the end of the file: this is the last frame.
            if (!anyNext && limit == fs.Length) { result = c; return true; }
            pos = c.Offset + 1;
        }
        return false;
    }

    // Scans [pos, limit) for the next CRC-valid, STREAMINFO-consistent frame header; pos is advanced.
    private static bool NextHeader(FileStream fs, FlacInfo info, byte[] buf, ref long pos, long limit, out Frame frame)
    {
        frame = default;
        while (pos < limit)
        {
            int want = (int)Math.Min(buf.Length, fs.Length - pos);
            if (want <= 0) return false;
            fs.Position = pos;
            int n = fs.Read(buf, 0, want);
            if (n <= 0) return false;
            // Keep an overlap so a header never straddles two chunks (except at end of file).
            int scanEnd = pos + n >= fs.Length ? n : n - MaxHeaderLen;
            if (scanEnd <= 0) return false;
            for (int i = 0; i < scanEnd; i++)
            {
                if (pos + i >= limit) { pos = limit; return false; }
                if (buf[i] == 0xFF && i + 1 < n && (buf[i + 1] & 0xFE) == 0xF8 && TryHeader(buf, i, n, info, out var f))
                {
                    frame = f with { Offset = pos + i };
                    pos = frame.Offset;
                    return true;
                }
            }
            pos += scanEnd;
        }
        return false;
    }

    private static bool TryHeader(byte[] b, int p, int avail, FlacInfo info, out Frame f)
    {
        f = default;
        if (p + 5 >= avail) return false;
        bool variable = (b[p + 1] & 1) != 0;
        int bsCode = b[p + 2] >> 4, srCode = b[p + 2] & 0xF;
        int chCode = b[p + 3] >> 4, bitsCode = (b[p + 3] >> 1) & 7;
        if ((b[p + 3] & 1) != 0 || bsCode == 0 || srCode == 15 || chCode > 10 || bitsCode == 3) return false;

        int ch = chCode < 8 ? chCode + 1 : 2;
        if (ch != info.Channels) return false;
        if (bitsCode != 0 && BitsTable[bitsCode] != info.Bits) return false;

        int q = p + 4;
        // UTF-8 style coded frame/sample number
        int first = b[q];
        int extra = first < 0x80 ? 0 : first < 0xC0 ? -1 : first < 0xE0 ? 1 : first < 0xF0 ? 2 : first < 0xF8 ? 3 : first < 0xFC ? 4 : first < 0xFE ? 5 : first == 0xFE ? 6 : -1;
        if (extra < 0 || (!variable && extra > 5)) return false;
        if (q + extra >= avail) return false;
        long number = extra == 0 ? first : first & (0x3F >> extra);
        for (int k = 1; k <= extra; k++)
        {
            if ((b[q + k] & 0xC0) != 0x80) return false;
            number = number << 6 | (uint)(b[q + k] & 0x3F);
        }
        q += extra + 1;

        int blockSize;
        if (bsCode == 1) blockSize = 192;
        else if (bsCode <= 5) blockSize = 576 << (bsCode - 2);
        else if (bsCode == 6) { if (q >= avail) return false; blockSize = b[q++] + 1; }
        else if (bsCode == 7) { if (q + 1 >= avail) return false; blockSize = (b[q] << 8 | b[q + 1]) + 1; q += 2; }
        else blockSize = 256 << (bsCode - 8);

        int rate = srCode == 0 ? info.SampleRate : srCode <= 11 ? RateTable[srCode] : 0;
        if (srCode == 12) { if (q >= avail) return false; rate = b[q++] * 1000; }
        else if (srCode == 13) { if (q + 1 >= avail) return false; rate = b[q] << 8 | b[q + 1]; q += 2; }
        else if (srCode == 14) { if (q + 1 >= avail) return false; rate = (b[q] << 8 | b[q + 1]) * 10; q += 2; }
        if (rate != info.SampleRate) return false;
        if (blockSize > info.MaxBlock) return false;

        if (q >= avail) return false;
        byte crc = 0;
        for (int i = p; i < q; i++) crc = Crc8Table[crc ^ b[i]];
        if (crc != b[q]) return false;

        f = new Frame(0, q - p + 1, variable, number, blockSize);
        return true;
    }
}
