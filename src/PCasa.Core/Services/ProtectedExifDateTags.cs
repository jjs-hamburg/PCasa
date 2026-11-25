namespace PCasa.App.Services;

// One shared whitelist for native metadata writes and date preservation.
internal static class ProtectedExifDateTags
{
    internal static readonly (string WriteTag, string ReadTag)[] ProtectedTags =
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

    internal static bool IsAllowedTag(string tag) =>
        ProtectedTags.Any(t => t.WriteTag.Equals(tag, StringComparison.Ordinal));
}
