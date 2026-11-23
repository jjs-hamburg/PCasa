using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PCasa.App.Services;

/// <summary>
/// Immutable native capture dates, separate from NTFS creation/last-write
/// timestamps. A normal image editor must not get to redefine these fields.
/// Only an explicit user edit of an EXIF field is allowed to change them.
/// </summary>
public static class ExifDateMetadata
{
    // ExifTool -G1 group names in ImageExifReader's single-source JSON.
    // Do not include FileModifyDate/FileCreateDate: editing legitimately
    // advances the filesystem revision timestamp.
    private static readonly (string WriteTag, string ReadTag)[] ProtectedTags =
    [
        ("EXIF:DateTimeOriginal", "ExifIFD:DateTimeOriginal"),
        ("EXIF:CreateDate", "ExifIFD:CreateDate"),
        ("EXIF:ModifyDate", "IFD0:ModifyDate"),
        ("EXIF:SubSecTimeOriginal", "ExifIFD:SubSecTimeOriginal"),
        ("EXIF:SubSecTimeDigitized", "ExifIFD:SubSecTimeDigitized"),
        ("EXIF:SubSecTime", "ExifIFD:SubSecTime"),
        ("EXIF:OffsetTimeOriginal", "ExifIFD:OffsetTimeOriginal"),
        ("EXIF:OffsetTimeDigitized", "ExifIFD:OffsetTimeDigitized"),
        ("EXIF:OffsetTime", "ExifIFD:OffsetTime"),
        ("XMP-exif:DateTimeOriginal", "XMP-exif:DateTimeOriginal"),
        ("XMP-exif:DateTimeDigitized", "XMP-exif:DateTimeDigitized"),
        ("XMP-xmp:CreateDate", "XMP-xmp:CreateDate"),
        ("XMP-xmp:ModifyDate", "XMP-xmp:ModifyDate"),
        ("XMP-photoshop:DateCreated", "XMP-photoshop:DateCreated"),
        ("IPTC:DateCreated", "IPTC:DateCreated"),
        ("IPTC:TimeCreated", "IPTC:TimeCreated")
    ];

    public sealed record Fields(string? DateTimeOriginal, string? CreateDate,
        string? ModifyDate);

    public static Fields FieldsFromJson(string json)
    {
        var tags = ParseJson(json);
        return new Fields(tags["EXIF:DateTimeOriginal"],
            tags["EXIF:CreateDate"], tags["EXIF:ModifyDate"]);
    }

    public static Fields ReadFields(string file,
        CancellationToken token = default)
    {
        using var reader = new ImageExifReader();
        return FieldsFromJson(reader.Read(file, token).Json);
    }

    public static string? ParseEditableDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var formats = new[]
        {
            "dd.MM.yyyy HH:mm:ss", "dd.MM.yyyy H:mm:ss",
            "yyyy:MM:dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss",
            "yyyy/MM/dd HH:mm:ss"
        };
        if (!DateTime.TryParseExact(value.Trim(), formats,
                CultureInfo.InvariantCulture, DateTimeStyles.None,
                out var date) || date.Year < 1900 || date.Year > 9999)
            throw new FormatException(
                "Ungültiges EXIF-Datum. Format: TT.MM.JJJJ HH:MM:SS.");
        return date.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    public static string DisplayDate(string? date)
    {
        if (string.IsNullOrWhiteSpace(date)) return "";
        try
        {
            var normalized = ParseEditableDate(date);
            return DateTime.ParseExact(normalized!, "yyyy:MM:dd HH:mm:ss",
                CultureInfo.InvariantCulture).ToString(
                    "dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch (FormatException) { return date; }
    }

    public static void Preserve(string original, string result,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (ImageExifReader.IsSameSourcePath(original, result))
            return;
        using var reader = new ImageExifReader();
        var sourceTags = ParseJson(reader.Read(original, token).Json);
        var resultTags = ParseJson(reader.Read(result, token).Json);
        // Copy only actual differences. Rewriting all sixteen protected
        // fields needlessly touches unrelated XMP/IPTC metadata and can
        // trigger date normalization on camera-specific JPEGs.
        var different = ProtectedTags
            .Where(tag => !string.Equals(sourceTags[tag.WriteTag],
                resultTags[tag.WriteTag], StringComparison.Ordinal))
            .ToDictionary(tag => tag.WriteTag,
                tag => sourceTags[tag.WriteTag], StringComparer.Ordinal);
        if (different.Count == 0) return;
        if (RawImageSource.IsRawPath(result))
            throw new InvalidDataException(
                "Externes RAW-Ergebnis hat das ursprüngliche Aufnahmedatum verändert.");

        // Log tag IDENTIFIERS only, never the user's photo timestamps.
        PCasaDiagnostics.WriteMessage("Image.Edit.ExifDateRepair",
            "Restoring protected date tags on staged result: " +
            string.Join(", ", different.Keys));

        using (var writer = new ExifToolKeywordWriter.Session())
            writer.WriteExactExifDates(result, different, token);

        // Reject before publication if the native writer failed, ignored a
        // format-specific tag, or unintentionally changed another date field.
        using var verifyReader = new ImageExifReader();
        var verified = ParseJson(verifyReader.Read(result, token).Json);
        if (!SameTags(sourceTags, verified))
            throw new InvalidDataException(
                "Exif-Aufnahmedaten des Originals konnten nicht vollständig wiederhergestellt werden.");
    }

    public static void VerifyUnchanged(string original, string result,
        CancellationToken token = default)
    {
        using var reader = new ImageExifReader();
        var expected = ParseJson(reader.Read(original, token).Json);
        var actual = ParseJson(reader.Read(result, token).Json);
        if (!SameTags(expected, actual))
            throw new InvalidDataException(
                "Bildbearbeitung hat EXIF-Aufnahme- oder Digitalisierungsdatum verändert.");
    }

    public static void SaveExplicit(string path, Fields updated,
        CancellationToken token = default, UndoOptions? undoOptions = null)
    {
        if (RawImageSource.IsRawPath(path))
            throw new NotSupportedException(
                "EXIF-Datumsbearbeitung in RAW-Dateien ist nicht freigegeben.");
        var normalized = new Fields(
            ParseEditableDate(updated.DateTimeOriginal),
            ParseEditableDate(updated.CreateDate),
            ParseEditableDate(updated.ModifyDate));
        using var lease = SourceFileLocks.Enter(path, token);
        var initial = ReadFields(path, token);
        if (normalized == initial) return;
        var beforeModifiedUtc = File.GetLastWriteTimeUtc(path);
        var beforeCreatedUtc = File.GetCreationTimeUtc(path);
        // One central full-file snapshot before the native atomic EXIF write.
        new UndoHistoryService(undoOptions ?? UndoOptions.Load())
            .CaptureBeforeSave(path, token);
        using (var writer = new ExifToolKeywordWriter.Session())
            writer.WriteExactExifDates(path, new Dictionary<string, string?>
            {
                ["EXIF:DateTimeOriginal"] = normalized.DateTimeOriginal,
                ["EXIF:CreateDate"] = normalized.CreateDate,
                ["EXIF:ModifyDate"] = normalized.ModifyDate
            }, token);
        var verified = ReadFields(path, token);
        if (verified != normalized)
            throw new InvalidDataException(
                "Gespeicherte EXIF-Datumsfelder stimmen nicht mit der Eingabe überein.");
        // ExifTool's -P preserves last-write timestamps by default. An
        // EXPLICIT metadata edit must advance the file revision so the
        // catalog cache and file watchers recognize its new EXIF fields.
        var now = DateTime.UtcNow;
        // ExifTool replaces the native file through a temporary. Retain the
        // filesystem CREATION date even though this intentional edit must
        // advance its LAST MODIFIED revision.
        File.SetCreationTimeUtc(path, beforeCreatedUtc);
        File.SetLastWriteTimeUtc(path, now > beforeModifiedUtc
            ? now : beforeModifiedUtc.AddMilliseconds(1));
    }

    // Other services must not write arbitrary -stay_open commands. This
    // whitelist is also the allowed set of restore tags for external editors.
    internal static bool IsAllowedTag(string tag) =>
        ProtectedTags.Any(t => t.WriteTag.Equals(tag, StringComparison.Ordinal));

    internal static IReadOnlyDictionary<string, string?> ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var o = doc.RootElement;
        if (o.ValueKind == JsonValueKind.Array && o.GetArrayLength() == 1)
            o = o[0];
        if (o.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("EXIF enthält kein gültiges JSON-Objekt.");

        var tags = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (writeTag, readTag) in ProtectedTags)
        {
            var alternate = readTag.StartsWith("ExifIFD:", StringComparison.Ordinal)
                ? "IFD0:" + readTag["ExifIFD:".Length..] : readTag;
            JsonElement field;
            if (!o.TryGetProperty(readTag, out field) &&
                !o.TryGetProperty(alternate, out field))
                tags.Add(writeTag, null);
            else
                tags.Add(writeTag, field.ValueKind == JsonValueKind.String
                    ? field.GetString()
                    : field.ValueKind == JsonValueKind.Number
                        ? field.GetRawText() : null);
        }
        return tags;
    }

    private static bool SameTags(IReadOnlyDictionary<string, string?> a,
        IReadOnlyDictionary<string, string?> b) =>
        ProtectedTags.All(t => string.Equals(a[t.WriteTag], b[t.WriteTag],
            StringComparison.Ordinal));
}

/// <summary>Strict known smartphone filename timestamps; never guess a clock time.</summary>
public static class PhotoDateFromFilename
{
    public sealed record Detected(DateOnly Date, TimeOnly? Time)
    {
        public string AsExifDateTime => Time is { } time
            ? Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                  .Replace('-', ':') +
              " " + time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : "";
    }

    private static readonly Regex Compact = new(
        @"(?<!\d)(?<date>20\d{6})[_\- ](?<time>\d{6})(?<fraction>\d{0,3})(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Separated = new(
        @"(?<!\d)(?<date>20\d{2}[-_.]\d{2}[-_.]\d{2})[_ T-](?<time>\d{2}[-:.]\d{2}[-:.]\d{2})(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WhatsApp = new(
        @"^(?:IMG|VID)[-_](?<date>20\d{6})[-_]WA\d+(?:[-_].*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);

    public static Detected? Recognize(string filename)
    {
        var name = Path.GetFileNameWithoutExtension(filename);
        foreach (var match in new[] { Compact.Match(name), Separated.Match(name) })
        {
            if (!match.Success) continue;
            var day = match.Groups["date"].Value.Replace("-", "")
                .Replace("_", "").Replace(".", "");
            var clock = match.Groups["time"].Value.Replace("-", "")
                .Replace(":", "").Replace(".", "");
            if (DateOnly.TryParseExact(day, "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date) &&
                TimeOnly.TryParseExact(clock, "HHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var time))
                return new Detected(date, time);
        }
        var whatsapp = WhatsApp.Match(name);
        if (whatsapp.Success &&
            DateOnly.TryParseExact(whatsapp.Groups["date"].Value,
                "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var whatsappDate))
            return new Detected(whatsappDate, null);
        return null;
    }
}
