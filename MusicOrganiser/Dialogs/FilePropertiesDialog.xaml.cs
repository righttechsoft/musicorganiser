using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using TagLib;

namespace MusicOrganiser.Dialogs;

/// <summary>Read-only dump of everything known about a track: filesystem info, this app's
/// own library data, TagLib-reported audio properties, and every native tag field found
/// in the file. Never throws — a TagLib read failure just shows an Error row.</summary>
public partial class FilePropertiesDialog : Window
{
    private record Row(string Section, string Property, string Value);

    // Fields common to every concrete tag type (TagLib.Tag), used as the reflection
    // fallback for tag types without a native field enumerator. Pictures are handled
    // separately by the Artwork section.
    private static readonly PropertyInfo[] BaseTagProperties = typeof(TagLib.Tag)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(p => p.Name != nameof(TagLib.Tag.Pictures))
        .ToArray();

    public FilePropertiesDialog(MusicOrganiser.Models.MusicFile file)
    {
        InitializeComponent();
        Title = file.FileName;

        var rows = BuildRows(file);
        var view = new CollectionViewSource { Source = rows };
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Row.Section)));
        PropertiesGrid.ItemsSource = view.View;
    }

    private static List<Row> BuildRows(MusicOrganiser.Models.MusicFile file)
    {
        var rows = new List<Row>();

        try
        {
            var info = new FileInfo(file.FullPath);
            var mb = info.Length / 1024.0 / 1024.0;
            rows.Add(new Row("File", "Name", info.Name));
            rows.Add(new Row("File", "Folder", info.DirectoryName ?? string.Empty));
            rows.Add(new Row("File", "Full path", info.FullName));
            rows.Add(new Row("File", "Size", $"{info.Length:N0} bytes ({mb:N2} MB)"));
            rows.Add(new Row("File", "Created", info.CreationTime.ToString("g")));
            rows.Add(new Row("File", "Modified", info.LastWriteTime.ToString("g")));
        }
        catch
        {
            // ponytail: FileInfo can throw for a since-deleted/locked file; the dialog should still open.
        }

        rows.Add(new Row("Library", "Rating", file.Rating is int r ? new string('★', r) : "Unrated"));
        rows.Add(new Row("Library", "Tags", string.IsNullOrWhiteSpace(file.Tags) ? "—" : file.Tags));

        try
        {
            using var tagFile = TagLib.File.Create(file.FullPath);

            var props = tagFile.Properties;
            if (props != null)
            {
                rows.Add(new Row("Audio", "Duration", props.Duration.ToString(@"h\:mm\:ss")));
                rows.Add(new Row("Audio", "Bitrate", $"{props.AudioBitrate} kbps"));
                rows.Add(new Row("Audio", "Sample rate", $"{props.AudioSampleRate:N0} Hz"));
                rows.Add(new Row("Audio", "Channels", props.AudioChannels.ToString()));
                if (props.BitsPerSample != 0)
                    rows.Add(new Row("Audio", "Bits per sample", props.BitsPerSample.ToString()));
                var codecs = string.Join(", ", props.Codecs.Where(c => c != null).Select(c => c.Description));
                if (!string.IsNullOrWhiteSpace(codecs))
                    rows.Add(new Row("Audio", "Codecs", codecs));
                rows.Add(new Row("Audio", "Media types", props.MediaTypes.ToString()));
            }

            AddTagRows(rows, tagFile);
            AddArtworkRows(rows, tagFile.Tag.Pictures);
        }
        catch (Exception ex)
        {
            rows.Add(new Row("Tags", "Error", ex.Message));
        }

        return rows;
    }

    private static void AddTagRows(List<Row> rows, TagLib.File tagFile)
    {
        foreach (TagTypes type in Enum.GetValues(typeof(TagTypes)))
        {
            if (type == TagTypes.None || type == TagTypes.AllTags) continue;
            if (!tagFile.TagTypes.HasFlag(type)) continue;

            var tag = tagFile.GetTag(type);
            if (tag == null) continue;

            var section = $"Tags ({type})";

            switch (tag)
            {
                case TagLib.Id3v2.Tag id3v2:
                    // Iterate frames directly (not the reflection fallback) so repeated
                    // FrameIds (e.g. multiple TXXX/COMM frames) each get their own row.
                    foreach (var frame in id3v2)
                        AddTagValue(rows, section, frame.FrameId.ToString(), frame.ToString());
                    break;

                case TagLib.Ogg.XiphComment xiph:
                    foreach (var name in xiph)
                        AddTagValue(rows, section, name, string.Join("; ", xiph.GetField(name)));
                    break;

                case TagLib.Ape.Tag ape:
                    foreach (var key in ape)
                        AddTagValue(rows, section, key, ape.GetItem(key)?.ToString());
                    break;

                default:
                    // TagLib.Id3v1.Tag, TagLib.Mpeg4.AppleTag, and anything else without a
                    // native field enumerator worth the code.
                    AddReflectedTagRows(rows, section, tag);
                    break;
            }
        }
    }

    private static void AddReflectedTagRows(List<Row> rows, string section, TagLib.Tag tag)
    {
        foreach (var prop in BaseTagProperties)
        {
            object? value;
            try { value = prop.GetValue(tag); }
            catch { continue; }

            var formatted = value switch
            {
                null => null,
                string[] arr => string.Join("; ", arr),
                _ => value.ToString()
            };

            AddTagValue(rows, section, prop.Name, formatted);
        }
    }

    // Skips empty/whitespace values and unset numeric fields (TagLib reads an absent
    // numeric tag as 0), so the dump doesn't fill up with noise rows.
    private static void AddTagValue(List<Row> rows, string section, string property, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "0") return;
        rows.Add(new Row(section, property, value));
    }

    private static void AddArtworkRows(List<Row> rows, IPicture[] pictures)
    {
        rows.Add(new Row("Artwork", "Count", pictures.Length.ToString()));
        for (var i = 0; i < pictures.Length; i++)
        {
            var pic = pictures[i];
            var value = $"{pic.Type} · {pic.MimeType} · {pic.Data.Count:N0} bytes";
            try
            {
                using var ms = new MemoryStream(pic.Data.Data);
                var frame = BitmapFrame.Create(ms);
                value += $" · {frame.PixelWidth}x{frame.PixelHeight}";
            }
            catch
            {
                // ponytail: dimensions are a bonus; an undecodable picture still gets its row.
            }
            rows.Add(new Row("Artwork", $"Picture {i + 1}", value));
        }
    }
}
