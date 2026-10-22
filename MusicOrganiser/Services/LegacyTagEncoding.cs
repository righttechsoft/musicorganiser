using System.Diagnostics;
using System.Text;
using TagLib;
using TagLib.Id3v2;

namespace MusicOrganiser.Services;

public static class LegacyTagEncoding
{
    private static readonly Encoding Cp1250;
    private static readonly Encoding Cp1251;
    private static readonly Encoding Cp1252;

    static LegacyTagEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp1250 = Encoding.GetEncoding(1250);
        Cp1251 = Encoding.GetEncoding(1251);
        Cp1252 = Encoding.GetEncoding(1252);
#if DEBUG
        SelfCheck();
#endif
    }

    public static void Install()
    {
        TagLib.Id3v1.Tag.DefaultStringHandler = new Id3v1Handler();
        TagLib.File.AddFileTypeResolver(Resolve);
    }

    // ponytail: candidates are CP1251, CP1250 (Polish letters only) and CP1252; no KOI8-R/CP866/CJK. One codepage per file is assumed. ID3v2 repair is mp3 only (WAV/AIFF untouched).
    internal static bool LooksCp1251(ReadOnlySpan<byte> b)
    {
        int ascii = 0, high = 0, run = 0;
        var pair = false;
        foreach (var x in b)
        {
            if ((x >= 'A' && x <= 'Z') || (x >= 'a' && x <= 'z')) ascii++;
            if (x >= 0xC0 || x == 0xA8 || x == 0xB8)
            {
                high++;
                if (++run >= 2) pair = true;
            }
            else run = 0;
        }
        return pair && high > ascii;
    }

    internal static bool LooksCp1250(ReadOnlySpan<byte> b)
    {
        for (var i = 1; i < b.Length; i++)
        {
            var x = b[i];
            if (x != 0xA5 && x != 0xB9 && x != 0xA3 && x != 0xB3 && x != 0xAF && x != 0xBF) continue;
            var p = b[i - 1];
            if ((p >= 'A' && p <= 'Z') || (p >= 'a' && p <= 'z') || p >= 0xC0) return true;
        }
        return false;
    }

    internal static Encoding Pick(ReadOnlySpan<byte> b) =>
        LooksCp1251(b) ? Cp1251 : LooksCp1250(b) ? Cp1250 : Cp1252;

    internal static Encoding PickForFile(IEnumerable<byte[]> fields)
    {
        var list = fields.ToList();
        if (list.Any(f => LooksCp1251(f))) return Cp1251;
        if (list.Any(f => LooksCp1250(f))) return Cp1250;
        return Cp1252;
    }

    internal static byte[] ToRaw(string s)
    {
        foreach (var enc in new[] { Cp1252, Cp1251, Cp1250 })
        {
            var bytes = enc.GetBytes(s);
            if (enc.GetString(bytes) == s) return bytes;
        }
        return Encoding.Latin1.GetBytes(s);
    }

    private static string Redecode(string s, Encoding enc) =>
        string.IsNullOrEmpty(s) ? s : enc.GetString(ToRaw(s));

    private static TagLib.File? Resolve(TagLib.File.IFileAbstraction abstraction, string mimetype, ReadStyle style)
    {
        if (mimetype != "taglib/mp3") return null;
        var file = new TagLib.Mpeg.AudioFile(abstraction, style);
        var v2 = file.GetTag(TagTypes.Id3v2, false) as TagLib.Id3v2.Tag;
        var v1 = file.GetTag(TagTypes.Id3v1, false) as TagLib.Id3v1.Tag;
        RepairFile(v2, v1);
        return file;
    }

    private static void RepairFile(TagLib.Id3v2.Tag? v2, TagLib.Id3v1.Tag? v1)
    {
        var frames = v2 == null
            ? new List<Frame>()
            : v2.GetFrames().Where(f => f switch
            {
                TextInformationFrame t => t.TextEncoding == StringType.Latin1,
                CommentsFrame c => c.TextEncoding == StringType.Latin1,
                UnsynchronisedLyricsFrame l => l.TextEncoding == StringType.Latin1,
                _ => false
            }).ToList();

        var raws = new List<byte[]>();
        foreach (var f in frames)
            foreach (var str in FrameStrings(f))
                raws.Add(Encoding.Latin1.GetBytes(str));
        if (v1 != null)
            foreach (var str in new[] { v1.Title, v1.Album, v1.Comment, string.Join(";", v1.Performers) })
                if (!string.IsNullOrEmpty(str)) raws.Add(ToRaw(str));

        var enc = PickForFile(raws);

        foreach (var f in frames)
        {
            switch (f)
            {
                case TextInformationFrame t:
                    var texts = t.Text;
                    var fixedTexts = texts.Select(x => Redecode(x, enc)).ToArray();
                    if (!fixedTexts.SequenceEqual(texts))
                    {
                        t.Text = fixedTexts;
                        t.TextEncoding = StringType.UTF16;
                    }
                    break;
                case CommentsFrame c:
                    var ct = Redecode(c.Text, enc);
                    var cd = Redecode(c.Description, enc);
                    if (ct != c.Text || cd != c.Description)
                    {
                        c.Text = ct;
                        c.Description = cd;
                        c.TextEncoding = StringType.UTF16;
                    }
                    break;
                case UnsynchronisedLyricsFrame l:
                    var lt = Redecode(l.Text, enc);
                    var ld = Redecode(l.Description, enc);
                    if (lt != l.Text || ld != l.Description)
                    {
                        l.Text = lt;
                        l.Description = ld;
                        l.TextEncoding = StringType.UTF16;
                    }
                    break;
            }
        }

        if (v1 != null)
        {
            var title = Redecode(v1.Title, enc);
            if (title != v1.Title) v1.Title = title;
            var album = Redecode(v1.Album, enc);
            if (album != v1.Album) v1.Album = album;
            var comment = Redecode(v1.Comment, enc);
            if (comment != v1.Comment) v1.Comment = comment;
            var joined = string.Join(";", v1.Performers);
            var artist = Redecode(joined, enc);
            if (artist != joined) v1.Performers = artist.Split(';');
        }
    }

    private static IEnumerable<string> FrameStrings(Frame f)
    {
        switch (f)
        {
            case TextInformationFrame t:
                return t.Text.Where(x => !string.IsNullOrEmpty(x));
            case CommentsFrame c:
                return new[] { c.Text, c.Description }.Where(x => !string.IsNullOrEmpty(x));
            case UnsynchronisedLyricsFrame l:
                return new[] { l.Text, l.Description }.Where(x => !string.IsNullOrEmpty(x));
            default:
                return Array.Empty<string>();
        }
    }

    private sealed class Id3v1Handler : TagLib.Id3v1.StringHandler
    {
        public override string Parse(ByteVector data) =>
            Pick(data.Data).GetString(data.Data).Trim().Trim('\0');

        public override ByteVector Render(string text) => new ByteVector(ToRaw(text));
    }

#if DEBUG
    private static void SelfCheck()
    {
        var h = new Id3v1Handler();
        byte[] B(Encoding e, string s) => e.GetBytes(s);
        byte[] Mix(string pre, byte x, string post) =>
            Encoding.Latin1.GetBytes(pre).Append(x).Concat(Encoding.Latin1.GetBytes(post)).ToArray();
        string P(byte[] b) => h.Parse(new ByteVector(b));

        Debug.Assert(P(B(Cp1251, "Сонет №7")) == "Сонет №7", "LegacyTagEncoding self-check: cp1251");
        Debug.Assert(P(Mix("Burz", 0xB9, "")) == "Burzą", "LegacyTagEncoding self-check: Burzą");
        Debug.Assert(P(Mix("D", 0xBF, "ingiel")) == "Dżingiel", "LegacyTagEncoding self-check: Dżingiel");
        Debug.Assert(P(Mix("Hera", 0x92, "s")) == "Hera’s", "LegacyTagEncoding self-check: Hera's");
        Debug.Assert(P(Mix("c", 0x9C, "ur")) == "cœur", "LegacyTagEncoding self-check: cœur");
        Debug.Assert(P(new byte[] { 0xBF, (byte)'Q', (byte)'u', 0xE9, (byte)'?' }) == "¿Qué?", "LegacyTagEncoding self-check: ¿Qué?");
        Debug.Assert(P(B(Encoding.Latin1, "Björk")) == "Björk", "LegacyTagEncoding self-check: Björk");
        Debug.Assert(P(B(Encoding.Latin1, "Été")) == "Été", "LegacyTagEncoding self-check: Été");
        Debug.Assert(P(B(Encoding.ASCII, "Track 7")) == "Track 7", "LegacyTagEncoding self-check: ascii");
        Debug.Assert(P(h.Render("Ключ").Data) == "Ключ", "LegacyTagEncoding self-check: render round trip");

        var hyp = Mix("Hypothese ", 0xB9, "1");
        Debug.Assert(P(hyp) == "Hypothese ¹1", "LegacyTagEncoding self-check: Hypothese alone");
        Debug.Assert(PickForFile(new[] { hyp, B(Cp1251, "Оцепеневшие") }) == Cp1251, "LegacyTagEncoding self-check: per-file pick");
        Debug.Assert(Cp1251.GetString(hyp) == "Hypothese №1", "LegacyTagEncoding self-check: Hypothese with sibling");

        foreach (var b in new[] { B(Cp1251, "Сонет №7"), Mix("Burz", 0xB9, ""), Mix("D", 0xBF, "ingiel"), Mix("Hera", 0x92, "s"), Mix("c", 0x9C, "ur"), B(Encoding.Latin1, "Björk") })
            Debug.Assert(ToRaw(Pick(b).GetString(b)).SequenceEqual(b), "LegacyTagEncoding self-check: ToRaw round trip");
    }
#endif
}
